// Package migrate implements FR-A14 one-click site migration: source-side
// precheck and pack (reusing the FR-A9 full-backup logic), transport via
// the FR-A13 relay (backup.manage op=send), and target-side restore with
// path-mapping validation, version comparison, a confirm gate and an
// automatic rollback snapshot. Contract source: docs/protocol-contract.md §8.
package migrate

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"taoops/agent/internal/metrics"
	"taoops/agent/internal/modules/backup"
	"taoops/agent/internal/modules/registry"
)

// Manager implements the migrate.manage ops on top of the backup manager.
type Manager struct {
	backup *backup.Manager
	now    func() time.Time
}

// NewManager returns a Manager using the given backup manager for packing,
// snapshots and extraction.
func NewManager(bm *backup.Manager) *Manager {
	return &Manager{backup: bm, now: time.Now}
}

// Register installs the "migrate.manage" handler.
func Register(r *registry.Registry, m *Manager) error {
	return r.Register("migrate.manage", registry.Handler(m.handle))
}

func (m *Manager) handle(args map[string]any) (registry.Result, error) {
	op, _ := args["op"].(string)
	switch op {
	case "precheck":
		return m.opPrecheck(args)
	case "pack":
		return m.opPack(args)
	case "restore":
		return m.opRestore(args)
	}
	return registry.Result{ExitCode: 2}, fmt.Errorf("migrate: unknown op %q (want precheck|pack|restore)", op)
}

// MigrateManifest is the pack-side migration manifest, persisted next to
// the archive as <archive>.migrate.json and passed to the target restore.
type MigrateManifest struct {
	Archive       string `json:"archive"`
	SourcePath    string `json:"source_path"`
	TargetPath    string `json:"target_path"`
	SourceVersion string `json:"source_version"`
	Created       string `json:"created"`
	Files         int    `json:"files"`
	TotalSize     int64  `json:"total_size"`
}

// PrecheckReport is the precheck result returned to the panel.
type PrecheckReport struct {
	SourcePath    string  `json:"source_path"`
	TargetPath    string  `json:"target_path"`
	Files         int     `json:"files"`
	TotalSize     int64   `json:"total_size"`
	SourceVersion string  `json:"source_version"`
	Checks        []Check `json:"checks"`
	Forced        bool    `json:"forced,omitempty"`
}

// Check is one named precheck line item.
type Check struct {
	Name   string `json:"name"`
	Status string `json:"status"` // ok | warn | fail
	Detail string `json:"detail,omitempty"`
}

func str(args map[string]any, key string) string {
	v, _ := args[key].(string)
	return v
}

func (m *Manager) opPrecheck(args map[string]any) (registry.Result, error) {
	sourcePath, err := validatePath(str(args, "source_path"), "source_path")
	if err != nil {
		return registry.Result{ExitCode: 2}, err
	}
	targetPath, err := validatePath(str(args, "target_path"), "target_path")
	if err != nil {
		return registry.Result{ExitCode: 2}, err
	}
	forced := str(args, "force") == "true"

	report := PrecheckReport{
		SourcePath: sourcePath, TargetPath: targetPath,
		SourceVersion: metrics.AgentVersion, Forced: forced,
	}
	if info, err := os.Stat(sourcePath); err != nil || !info.IsDir() {
		return registry.Result{ExitCode: 2}, fmt.Errorf("migrate: source_path must be an existing directory: %s", sourcePath)
	}
	files, size, err := statTree(sourcePath)
	if err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	report.Files, report.TotalSize = files, size
	report.Checks = append(report.Checks,
		Check{Name: "source_path", Status: "ok", Detail: fmt.Sprintf("%d files, %d bytes", files, size)},
		Check{Name: "target_path", Status: "ok", Detail: "absolute, canonical, no .. segments"},
		Check{Name: "version", Status: "info", Detail: fmt.Sprintf("source agent %s; compared at restore time", metrics.AgentVersion)},
	)

	out, _ := json.Marshal(report)
	return registry.Result{
		ExitCode: 0,
		Output:   fmt.Sprintf("precheck ok: %d files, %d bytes, source agent %s%s", files, size, metrics.AgentVersion, forcedMark(forced)),
		Data:     map[string]any{"report": json.RawMessage(out)},
	}, nil
}

func (m *Manager) opPack(args map[string]any) (registry.Result, error) {
	sourcePath, err := validatePath(str(args, "source_path"), "source_path")
	if err != nil {
		return registry.Result{ExitCode: 2}, err
	}
	targetPath, err := validatePath(str(args, "target_path"), "target_path")
	if err != nil {
		return registry.Result{ExitCode: 2}, err
	}
	name := str(args, "name")
	if name == "" || strings.ContainsAny(name, "/\\") {
		return registry.Result{ExitCode: 2}, fmt.Errorf("migrate: pack requires a name without path separators")
	}
	if info, err := os.Stat(sourcePath); err != nil || !info.IsDir() {
		return registry.Result{ExitCode: 2}, fmt.Errorf("migrate: source_path must be an existing directory: %s", sourcePath)
	}

	archivePath, man, err := m.backup.PackFull(sourcePath, name)
	if err != nil {
		return registry.Result{ExitCode: 1}, fmt.Errorf("migrate: pack: %w", err)
	}
	mm := MigrateManifest{
		Archive: archivePath, SourcePath: sourcePath, TargetPath: targetPath,
		SourceVersion: metrics.AgentVersion,
		Created:       m.now().UTC().Format(time.RFC3339),
		Files:         len(man.Files), TotalSize: manTotalSize(man),
	}
	raw, err := json.MarshalIndent(mm, "", "  ")
	if err != nil {
		return registry.Result{ExitCode: 1}, fmt.Errorf("migrate: marshal manifest: %w", err)
	}
	if err := os.WriteFile(archivePath+".migrate.json", raw, 0o644); err != nil {
		return registry.Result{ExitCode: 1}, fmt.Errorf("migrate: write manifest: %w", err)
	}
	return registry.Result{
		ExitCode: 0,
		Output:   fmt.Sprintf("migration package ready: %s (%d files, %d bytes)", archivePath, mm.Files, mm.TotalSize),
		Data:     map[string]any{"file": archivePath, "manifest": archivePath + ".migrate.json", "total_size": mm.TotalSize},
	}, nil
}

func (m *Manager) opRestore(args map[string]any) (registry.Result, error) {
	file := str(args, "file")
	if file == "" {
		return registry.Result{ExitCode: 2}, fmt.Errorf("migrate: restore requires file=")
	}
	abs, err := filepath.Abs(file)
	if err != nil {
		return registry.Result{ExitCode: 2}, fmt.Errorf("migrate: resolve file: %w", err)
	}
	absBackup, _ := filepath.Abs(m.backup.Dir())
	if abs != absBackup && !strings.HasPrefix(abs, absBackup+string(filepath.Separator)) {
		return registry.Result{ExitCode: 2}, fmt.Errorf("migrate: file must live under backup dir")
	}

	mapping, err := parseMapping(args["mapping"])
	if err != nil {
		return registry.Result{ExitCode: 2}, err
	}
	targetPath, err := validatePath(mapping.TargetPath, "mapping.target_path")
	if err != nil {
		return registry.Result{ExitCode: 2}, err
	}
	if err := checkSymlinkEscape(targetPath); err != nil {
		return registry.Result{ExitCode: 2}, err
	}

	forced := str(args, "force") == "true"
	sourceVersion := str(args, "source_version")
	if sourceVersion != "" && sourceVersion != metrics.AgentVersion && !forced {
		msg := fmt.Sprintf("migrate: agent version mismatch (source %s, target %s); re-run with force=true to override", sourceVersion, metrics.AgentVersion)
		return registry.Result{ExitCode: 2, Output: msg}, fmt.Errorf("%s", msg)
	}
	if str(args, "confirm") != "true" {
		msg := "migrate: restore replaces files at the target path; re-run with confirm=true (a rollback snapshot is created first)"
		return registry.Result{ExitCode: 2, Output: msg}, fmt.Errorf("%s", msg)
	}

	// 还原前自动快照（同 FR-A9 口径；目录不存在或为空则跳过）。
	snapshot, err := m.backup.SnapshotFor(targetPath, "migrate-rollback")
	if err != nil {
		return registry.Result{ExitCode: 1}, fmt.Errorf("migrate: rollback snapshot: %w", err)
	}

	if err := m.backup.ExtractTo(abs, targetPath); err != nil {
		return registry.Result{ExitCode: 1}, fmt.Errorf("migrate: extract: %w", err)
	}
	restored, _, statErr := statTree(targetPath)

	report := map[string]any{
		"restored": restored, "snapshot": snapshot,
		"forced": forced, "target_path": targetPath,
	}
	msg := fmt.Sprintf("migration restored: %s -> %s (%d files)", abs, targetPath, restored)
	if snapshot != "" {
		msg += fmt.Sprintf(" (previous state saved: %s)", snapshot)
	}
	if forced {
		msg += " [forced: version mismatch overridden]"
	}
	_ = statErr
	return registry.Result{ExitCode: 0, Output: msg, Data: report}, nil
}

// validatePath enforces absolute, canonical form without any ".." segment
// (contract §8: both mapping endpoints must be normalized absolute paths).
func validatePath(p, label string) (string, error) {
	if p == "" {
		return "", fmt.Errorf("migrate: %s is required", label)
	}
	if !filepath.IsAbs(p) {
		return "", fmt.Errorf("migrate: %s must be an absolute path", label)
	}
	for _, seg := range strings.Split(p, string(filepath.Separator)) {
		if seg == ".." {
			return "", fmt.Errorf("migrate: %s must not contain .. segments", label)
		}
	}
	if c := filepath.Clean(p); c != p {
		return "", fmt.Errorf("migrate: %s must be in canonical (clean) form", label)
	}
	return p, nil
}

// checkSymlinkEscape rejects when any existing component of target is a
// symbolic link (anti-escape, contract §8).
func checkSymlinkEscape(target string) error {
	cur := target
	for {
		parent := filepath.Dir(cur)
		if parent == cur {
			return nil
		}
		if fi, err := os.Lstat(cur); err == nil && fi.Mode()&os.ModeSymlink != 0 {
			return fmt.Errorf("migrate: target path component is a symbolic link: %s", cur)
		}
		cur = parent
	}
}

// parseMapping accepts mapping as a JSON string or an already-decoded
// object (both arrive over the key=value bridge).
func parseMapping(v any) (MigrateManifest, error) {
	var mm MigrateManifest
	switch m := v.(type) {
	case string:
		if err := json.Unmarshal([]byte(m), &mm); err != nil {
			return mm, fmt.Errorf("migrate: parse mapping: %w", err)
		}
	case map[string]any:
		raw, _ := json.Marshal(m)
		if err := json.Unmarshal(raw, &mm); err != nil {
			return mm, fmt.Errorf("migrate: parse mapping: %w", err)
		}
	default:
		return mm, fmt.Errorf("migrate: restore requires mapping= (JSON with source_path/target_path)")
	}
	if mm.TargetPath == "" {
		return mm, fmt.Errorf("migrate: mapping.target_path is required")
	}
	return mm, nil
}

func statTree(root string) (files int, size int64, err error) {
	err = filepath.WalkDir(root, func(_ string, d os.DirEntry, e error) error {
		if e != nil {
			return e
		}
		if d.IsDir() {
			return nil
		}
		if info, infoErr := d.Info(); infoErr == nil {
			files++
			size += info.Size()
		}
		return nil
	})
	return files, size, err
}

func manTotalSize(man *backup.Manifest) int64 {
	var total int64
	for _, e := range man.Files {
		total += e.Size
	}
	return total
}

func forcedMark(forced bool) string {
	if forced {
		return " [forced]"
	}
	return ""
}
