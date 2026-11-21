// Package backup implements FR-A9: site/database/full-site backup and
// restore to a local or specified directory, with incremental backups and
// a confirmation + rollback point before any restore.
package backup

import (
	"archive/tar"
	"compress/gzip"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"taoops/agent/internal/modules/registry"
)

// Entry is one file record inside a backup manifest.
type Entry struct {
	Path  string `json:"path"`
	Size  int64  `json:"size"`
	MTime int64  `json:"mtime_unix_nano"`
	Same  bool   `json:"same_as_previous,omitempty"`
}

// Manifest describes one backup archive; stored as <archive>.manifest.json.
type Manifest struct {
	Name    string  `json:"name"`
	Source  string  `json:"source"`
	File    string  `json:"file"`
	Created string  `json:"created"`
	Base    string  `json:"base,omitempty"`
	Files   []Entry `json:"files"`
}

// Manager creates and restores backups under a backup directory.
type Manager struct {
	backupDir string
	now       func() time.Time
}

// NewManager returns a Manager storing archives under backupDir.
func NewManager(backupDir string) *Manager {
	return &Manager{backupDir: backupDir, now: time.Now}
}

// Register installs the "backup.manage" handler.
func Register(r *registry.Registry, m *Manager) error {
	return r.Register("backup.manage", registry.Handler(m.handle))
}

func (m *Manager) handle(args map[string]any) (registry.Result, error) {
	op, _ := args["op"].(string)
	switch op {
	case "create":
		return m.opCreate(args)
	case "list":
		return m.opList(args)
	case "restore":
		return m.opRestore(args, false)
	case "rollback":
		return m.opRestore(args, true)
	}
	return registry.Result{ExitCode: 2}, fmt.Errorf("backup: unknown op %q (want create|list|restore|rollback)", op)
}

func str(args map[string]any, key string) string {
	v, _ := args[key].(string)
	return v
}

func (m *Manager) opCreate(args map[string]any) (registry.Result, error) {
	name := str(args, "name")
	source := str(args, "source")
	if name == "" || source == "" {
		return registry.Result{ExitCode: 2}, fmt.Errorf("backup: create requires name= and source=")
	}
	incremental := str(args, "incremental") == "true"
	dest := str(args, "dest")
	if dest == "" {
		dest = filepath.Join(m.backupDir, name)
	}
	if err := os.MkdirAll(dest, 0o755); err != nil {
		return registry.Result{ExitCode: 1}, fmt.Errorf("backup: mkdir dest: %w", err)
	}

	var base *Manifest
	if incremental {
		if prev := m.latestManifest(dest, name); prev != nil {
			base = prev
		}
	}

	stamp := m.now().UTC().Format("20060102-150405")
	archivePath := filepath.Join(dest, fmt.Sprintf("%s-%s.tar.gz", name, stamp))
	manifest, err := m.createArchive(archivePath, name, source, base)
	if err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	if err := m.writeManifest(archivePath, manifest); err != nil {
		return registry.Result{ExitCode: 1}, err
	}

	included := 0
	for _, f := range manifest.Files {
		if !f.Same {
			included++
		}
	}
	return registry.Result{
		ExitCode: 0,
		Output:   fmt.Sprintf("backup created: %s (%d files, %d unchanged)", archivePath, included, len(manifest.Files)-included),
		Data:     map[string]any{"file": archivePath, "manifest": archivePath + ".manifest.json"},
	}, nil
}

func (m *Manager) opList(args map[string]any) (registry.Result, error) {
	name := str(args, "name")
	dest := str(args, "dest")
	if dest == "" {
		dest = filepath.Join(m.backupDir, name)
	}
	matches, err := filepath.Glob(filepath.Join(dest, "*.manifest.json"))
	if err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	sort.Strings(matches)
	var lines []string
	for _, mf := range matches {
		raw, err := os.ReadFile(mf)
		if err != nil {
			continue
		}
		var man Manifest
		if json.Unmarshal(raw, &man) != nil {
			continue
		}
		if name != "" && man.Name != name {
			continue
		}
		lines = append(lines, fmt.Sprintf("%s  %s  files=%d", man.Created, man.File, len(man.Files)))
	}
	if lines == nil {
		lines = []string{"(no backups)"}
	}
	return registry.Result{ExitCode: 0, Output: strings.Join(lines, "\n")}, nil
}

func (m *Manager) opRestore(args map[string]any, rollback bool) (registry.Result, error) {
	file := str(args, "file")
	if file == "" {
		return registry.Result{ExitCode: 2}, fmt.Errorf("backup: restore requires file=")
	}
	if str(args, "confirm") != "true" {
		msg := "backup: restore replaces current files; re-run with confirm=true (a rollback snapshot is created first)"
		return registry.Result{ExitCode: 2, Output: msg}, fmt.Errorf("%s", msg)
	}

	raw, err := os.ReadFile(file + ".manifest.json")
	if err != nil {
		return registry.Result{ExitCode: 1}, fmt.Errorf("backup: read manifest: %w", err)
	}
	var man Manifest
	if err := json.Unmarshal(raw, &man); err != nil {
		return registry.Result{ExitCode: 1}, fmt.Errorf("backup: parse manifest: %w", err)
	}

	// 还原前先做回滚快照（PRD FR-A9：还原前有确认与回滚提示）。
	rollbackPath := ""
	if entries := m.currentEntries(man.Source); len(entries) > 0 {
		rollbackPath = filepath.Join(filepath.Dir(file),
			fmt.Sprintf("rollback-%d.tar.gz", m.now().UnixNano()))
		rbManifest, err := m.createArchive(rollbackPath, man.Name, man.Source, nil)
		if err != nil {
			return registry.Result{ExitCode: 1}, fmt.Errorf("backup: rollback snapshot: %w", err)
		}
		if err := m.writeManifest(rollbackPath, rbManifest); err != nil {
			return registry.Result{ExitCode: 1}, err
		}
	}

	if err := m.extractArchive(file, man.Source); err != nil {
		return registry.Result{ExitCode: 1}, err
	}

	msg := fmt.Sprintf("restored %s -> %s", file, man.Source)
	if rollback {
		msg = "rollback: " + msg
	}
	if rollbackPath != "" {
		msg += fmt.Sprintf(" (previous state saved: %s)", rollbackPath)
	}
	return registry.Result{ExitCode: 0, Output: msg, Data: map[string]any{"rollback": rollbackPath}}, nil
}

// writeManifest persists the manifest sidecar next to the archive.
func (m *Manager) writeManifest(archivePath string, manifest *Manifest) error {
	manifestJSON, err := json.MarshalIndent(manifest, "", "  ")
	if err != nil {
		return fmt.Errorf("backup: marshal manifest: %w", err)
	}
	if err := os.WriteFile(archivePath+".manifest.json", manifestJSON, 0o644); err != nil {
		return fmt.Errorf("backup: write manifest: %w", err)
	}
	return nil
}

// createArchive walks source and writes a tar.gz; entries equal to base
// (same size+mtime) are recorded as unchanged and skipped when base != nil.
func (m *Manager) createArchive(archivePath, name, source string, base *Manifest) (*Manifest, error) {
	prev := map[string]Entry{}
	if base != nil {
		for _, e := range base.Files {
			prev[e.Path] = e
		}
	}

	out, err := os.Create(archivePath)
	if err != nil {
		return nil, fmt.Errorf("backup: create archive: %w", err)
	}
	defer out.Close()
	gz := gzip.NewWriter(out)
	defer gz.Close()
	tw := tar.NewWriter(gz)
	defer tw.Close()

	manifest := &Manifest{Name: name, Source: source, File: archivePath, Created: m.now().UTC().Format(time.RFC3339)}
	if base != nil {
		manifest.Base = base.File
	}

	err = filepath.WalkDir(source, func(path string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		rel, relErr := filepath.Rel(source, path)
		if relErr != nil {
			return relErr
		}
		if d.IsDir() {
			return nil
		}
		info, infoErr := d.Info()
		if infoErr != nil {
			return infoErr
		}
		entry := Entry{Path: rel, Size: info.Size(), MTime: info.ModTime().UnixNano()}
		if p, ok := prev[rel]; ok && p.Size == entry.Size && p.MTime == entry.MTime && base != nil {
			entry.Same = true
		}
		manifest.Files = append(manifest.Files, entry)
		if entry.Same {
			return nil
		}
		f, openErr := os.Open(path)
		if openErr != nil {
			return openErr
		}
		defer f.Close()
		hdr := &tar.Header{Name: rel, Mode: int64(info.Mode().Perm()), Size: info.Size()}
		if err := tw.WriteHeader(hdr); err != nil {
			return err
		}
		_, err = io.Copy(tw, f)
		return err
	})
	if err != nil {
		return nil, fmt.Errorf("backup: walk %s: %w", source, err)
	}
	return manifest, nil
}

func (m *Manager) extractArchive(archivePath, target string) error {
	f, err := os.Open(archivePath)
	if err != nil {
		return fmt.Errorf("backup: open archive: %w", err)
	}
	defer f.Close()
	gz, err := gzip.NewReader(f)
	if err != nil {
		return fmt.Errorf("backup: gzip: %w", err)
	}
	defer gz.Close()
	tr := tar.NewReader(gz)
	for {
		hdr, err := tr.Next()
		if err == io.EOF {
			break
		}
		if err != nil {
			return fmt.Errorf("backup: tar: %w", err)
		}
		// 防路径穿越：拒绝绝对路径与 ..。
		clean := filepath.Clean(hdr.Name)
		if filepath.IsAbs(clean) || strings.HasPrefix(clean, "..") {
			return fmt.Errorf("backup: unsafe path in archive: %s", hdr.Name)
		}
		dest := filepath.Join(target, clean)
		switch hdr.Typeflag {
		case tar.TypeDir:
			if err := os.MkdirAll(dest, 0o755); err != nil {
				return err
			}
		case tar.TypeReg:
			if err := os.MkdirAll(filepath.Dir(dest), 0o755); err != nil {
				return err
			}
			outFile, err := os.OpenFile(dest, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, os.FileMode(hdr.Mode)&0o777)
			if err != nil {
				return err
			}
			if _, err := io.Copy(outFile, tr); err != nil {
				outFile.Close()
				return err
			}
			outFile.Close()
		}
	}
	return nil
}

// currentEntries lists the current file state of dir (empty if missing).
func (m *Manager) currentEntries(dir string) []Entry {
	var entries []Entry
	filepath.WalkDir(dir, func(path string, d os.DirEntry, err error) error {
		if err != nil || d.IsDir() {
			return nil
		}
		if info, infoErr := d.Info(); infoErr == nil {
			rel, _ := filepath.Rel(dir, path)
			entries = append(entries, Entry{Path: rel, Size: info.Size(), MTime: info.ModTime().UnixNano()})
		}
		return nil
	})
	return entries
}

// latestManifest returns the newest manifest for name in dest, or nil.
func (m *Manager) latestManifest(dest, name string) *Manifest {
	matches, err := filepath.Glob(filepath.Join(dest, name+"-*.tar.gz.manifest.json"))
	if err != nil || len(matches) == 0 {
		return nil
	}
	sort.Strings(matches)
	raw, err := os.ReadFile(matches[len(matches)-1])
	if err != nil {
		return nil
	}
	var man Manifest
	if json.Unmarshal(raw, &man) != nil {
		return nil
	}
	return &man
}
