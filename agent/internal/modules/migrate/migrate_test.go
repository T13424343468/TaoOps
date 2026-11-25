package migrate

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"taoops/agent/internal/metrics"
	"taoops/agent/internal/modules/backup"
	"taoops/agent/internal/modules/registry"
)

func newTestManager(t *testing.T) (*Manager, string, string) {
	t.Helper()
	dir := t.TempDir()
	backupDir := filepath.Join(dir, "backup")
	bm := backup.NewManager(backupDir)
	mgr := NewManager(bm)
	source := filepath.Join(dir, "site")
	if err := os.MkdirAll(filepath.Join(source, "conf"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(source, "index.html"), []byte("hello"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(source, "conf", "app.conf"), []byte("k=v"), 0o644); err != nil {
		t.Fatal(err)
	}
	return mgr, backupDir, source
}


func newTarget(t *testing.T) string {
	t.Helper()
	return filepath.Join(t.TempDir(), "newsite")
}

func run(m *Manager, args map[string]any) (registry.Result, error) {
	return m.handle(args)
}

func TestPrecheckHappyPath(t *testing.T) {
	mgr, _, source := newTestManager(t)
	target := newTarget(t)
	res, err := run(mgr, map[string]any{
		"op": "precheck", "source_path": source, "target_path": target,
	})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("precheck failed: %v %v", res, err)
	}
	raw, _ := json.Marshal(res.Data["report"])
	var report PrecheckReport
	if json.Unmarshal(raw, &report) != nil {
		t.Fatalf("report not parseable: %s", raw)
	}
	if report.Files != 2 || report.TotalSize != 8 {
		t.Fatalf("unexpected stats: files=%d size=%d", report.Files, report.TotalSize)
	}
	if report.SourceVersion != metrics.AgentVersion {
		t.Fatalf("source version = %s", report.SourceVersion)
	}
}

func TestPrecheckRejectsRelativeAndDotDot(t *testing.T) {
	mgr, _, source := newTestManager(t)
	cases := []map[string]any{
		{"op": "precheck", "source_path": "relative/path", "target_path": "/srv/x"},
		{"op": "precheck", "source_path": source, "target_path": "/srv/../etc"},
		{"op": "precheck", "source_path": source + "/./conf", "target_path": "/srv/x"},
		{"op": "precheck", "source_path": source, "target_path": ""},
	}
	for i, args := range cases {
		if res, err := run(mgr, args); err == nil {
			t.Fatalf("case %d: expected rejection, got %v", i, res)
		}
	}
}

func TestPrecheckRejectsMissingSource(t *testing.T) {
	mgr, _, _ := newTestManager(t)
	if _, err := run(mgr, map[string]any{
		"op": "precheck", "source_path": "/nonexistent/dir", "target_path": "/srv/x",
	}); err == nil {
		t.Fatal("expected rejection for missing source dir")
	}
}

func TestPackWritesArchiveAndMigrateManifest(t *testing.T) {
	mgr, backupDir, source := newTestManager(t)
	target := newTarget(t)
	res, err := run(mgr, map[string]any{
		"op": "pack", "source_path": source, "target_path": target, "name": "mysite",
	})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("pack failed: %v %v", res, err)
	}
	archive := res.Data["file"].(string)
	if !strings.HasPrefix(archive, backupDir) {
		t.Fatalf("archive outside backup dir: %s", archive)
	}
	raw, err := os.ReadFile(archive + ".migrate.json")
	if err != nil {
		t.Fatalf("migrate manifest missing: %v", err)
	}
	var mm MigrateManifest
	if json.Unmarshal(raw, &mm) != nil {
		t.Fatalf("manifest not parseable: %s", raw)
	}
	if mm.SourcePath != source || mm.TargetPath != target || mm.SourceVersion != metrics.AgentVersion {
		t.Fatalf("unexpected manifest: %+v", mm)
	}
	if mm.Files != 2 || mm.TotalSize != 8 {
		t.Fatalf("unexpected manifest stats: files=%d size=%d", mm.Files, mm.TotalSize)
	}
}

func TestPackRejectsBadNameAndPath(t *testing.T) {
	mgr, _, source := newTestManager(t)
	if _, err := run(mgr, map[string]any{
		"op": "pack", "source_path": source, "target_path": "/srv/x", "name": "a/b",
	}); err == nil {
		t.Fatal("expected rejection for name with separator")
	}
	if _, err := run(mgr, map[string]any{
		"op": "pack", "source_path": source, "target_path": "relative", "name": "x",
	}); err == nil {
		t.Fatal("expected rejection for relative target")
	}
}

func packOnce(t *testing.T, mgr *Manager, source, target string) string {
	t.Helper()
	res, err := run(mgr, map[string]any{
		"op": "pack", "source_path": source, "target_path": target, "name": "mysite",
	})
	if err != nil {
		t.Fatalf("pack: %v", err)
	}
	return res.Data["file"].(string)
}

func TestRestoreHappyPathWithSnapshot(t *testing.T) {
	mgr, _, source := newTestManager(t)
	archive := packOnce(t, mgr, source, newTarget(t))

	target := filepath.Join(t.TempDir(), "newsite")
	if err := os.MkdirAll(target, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(target, "old.txt"), []byte("old"), 0o644); err != nil {
		t.Fatal(err)
	}

	res, err := run(mgr, map[string]any{
		"op": "restore", "file": archive,
		"mapping":       `{"source_path": "` + source + `", "target_path": "` + target + `"}`,
		"source_version": metrics.AgentVersion, "confirm": "true",
	})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("restore failed: %v %v", res, err)
	}
	data, _ := os.ReadFile(filepath.Join(target, "index.html"))
	if string(data) != "hello" {
		t.Fatalf("restored content = %q", data)
	}
	snap := res.Data["snapshot"].(string)
	if snap == "" {
		t.Fatal("expected rollback snapshot for non-empty target")
	}
	if _, err := os.Stat(snap); err != nil {
		t.Fatalf("snapshot missing: %v", err)
	}
}

func TestRestoreRequiresConfirm(t *testing.T) {
	mgr, _, source := newTestManager(t)
	target := newTarget(t)
	archive := packOnce(t, mgr, source, target)
	res, err := run(mgr, map[string]any{
		"op": "restore", "file": archive,
		"mapping": `{"target_path": "` + target + `"}`,
	})
	if err == nil || !strings.Contains(res.Output, "confirm=true") {
		t.Fatalf("expected confirm gate, got %v %v", res, err)
	}
}

func TestRestoreRejectsVersionMismatchWithoutForce(t *testing.T) {
	mgr, _, source := newTestManager(t)
	target := newTarget(t)
	archive := packOnce(t, mgr, source, target)
	if _, err := run(mgr, map[string]any{
		"op": "restore", "file": archive,
		"mapping": `{"target_path": "` + target + `"}`, "source_version": "9.9.9", "confirm": "true",
	}); err == nil {
		t.Fatal("expected version mismatch rejection")
	}
	// force 放行。
	if res, err := run(mgr, map[string]any{
		"op": "restore", "file": archive,
		"mapping": `{"target_path": "` + target + `"}`, "source_version": "9.9.9",
		"confirm": "true", "force": "true",
	}); err != nil || res.ExitCode != 0 {
		t.Fatalf("force restore failed: %v %v", res, err)
	}
}

func TestRestoreRejectsArchiveOutsideBackupDir(t *testing.T) {
	mgr, _, source := newTestManager(t)
	if _, err := run(mgr, map[string]any{
		"op": "restore", "file": source,
		"mapping": `{"target_path": "/srv/x"}`, "confirm": "true",
	}); err == nil {
		t.Fatal("expected rejection for file outside backup dir")
	}
}

func TestRestoreRejectsSymlinkEscape(t *testing.T) {
	mgr, _, source := newTestManager(t)
	archive := packOnce(t, mgr, source, newTarget(t))
	base := t.TempDir()
	realDir := filepath.Join(base, "real")
	if err := os.MkdirAll(realDir, 0o755); err != nil {
		t.Fatal(err)
	}
	link := filepath.Join(base, "link")
	if err := os.Symlink(realDir, link); err != nil {
		t.Fatal(err)
	}
	if _, err := run(mgr, map[string]any{
		"op": "restore", "file": archive,
		"mapping": `{"target_path": "` + link + `"}`, "confirm": "true",
	}); err == nil || !strings.Contains(err.Error(), "symbolic link") {
		t.Fatalf("expected symlink rejection, got %v", err)
	}
}

func TestRestoreSkipsSnapshotForEmptyTarget(t *testing.T) {
	mgr, _, source := newTestManager(t)
	archive := packOnce(t, mgr, source, newTarget(t))
	target := filepath.Join(t.TempDir(), "fresh")
	res, err := run(mgr, map[string]any{
		"op": "restore", "file": archive,
		"mapping": `{"target_path": "` + target + `"}`, "confirm": "true",
	})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("restore failed: %v %v", res, err)
	}
	if snap := res.Data["snapshot"].(string); snap != "" {
		t.Fatalf("expected no snapshot for empty target, got %s", snap)
	}
	if _, err := os.Stat(filepath.Join(target, "conf", "app.conf")); err != nil {
		t.Fatalf("nested file not restored: %v", err)
	}
}

func TestUnknownOpRejected(t *testing.T) {
	mgr, _, _ := newTestManager(t)
	if _, err := run(mgr, map[string]any{"op": "nope"}); err == nil {
		t.Fatal("expected unknown-op rejection")
	}
}
