package logclean

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"taoops/agent/internal/modules/registry"
)

func newTestManager(t *testing.T) (*Manager, string) {
	t.Helper()
	dataDir := t.TempDir()
	m := NewManager(dataDir)
	now := time.Date(2026, 11, 24, 12, 0, 0, 0, time.UTC)
	m.now = func() time.Time { return now }
	seq := 0
	m.randomID = func() string { seq++; return "rule-" + string(rune('a'+seq-1)) }
	return m, dataDir
}

func run(t *testing.T, h registry.Handler, args map[string]any) registry.Result {
	t.Helper()
	res, err := h(args)
	if err != nil {
		t.Fatalf("handler error: %v", err)
	}
	return res
}

// runErr tolerates handler errors (for rejection cases).
func runErr(t *testing.T, h registry.Handler, args map[string]any) (registry.Result, error) {
	t.Helper()
	return h(args)
}

func TestAddRequiresAbsolutePath(t *testing.T) {
	m, _ := newTestManager(t)
	h, _ := m.handle, error(nil)
	_ = h
	reg := registry.New()
	if err := Register(reg, m); err != nil {
		t.Fatal(err)
	}
	handler, _ := reg.Get("logclean.manage")

	res, err := runErr(t, handler, map[string]any{"op": "add", "path": "var/log/app.log", "max_age_days": 7})
	if err == nil || res.ExitCode != 2 || !strings.Contains(err.Error(), "absolute") {
		t.Fatalf("relative path must be rejected: %+v", res)
	}
	res, err = runErr(t, handler, map[string]any{"op": "add", "path": "/", "max_age_days": 7})
	if err == nil || res.ExitCode != 2 || !strings.Contains(err.Error(), "root path") {
		t.Fatalf("root path must be rejected: %+v", res)
	}
	res, err = runErr(t, handler, map[string]any{"op": "add", "path": "/var/log/../../etc/passwd", "max_age_days": 7})
	if err == nil || res.ExitCode != 2 || !strings.Contains(err.Error(), "..") {
		t.Fatalf(".. traversal must be rejected: %+v", res)
	}
	res, err = runErr(t, handler, map[string]any{"op": "add", "path": "/var/*", "max_age_days": 7})
	if err == nil || res.ExitCode != 2 || !strings.Contains(err.Error(), "too broad") {
		t.Fatalf("overly broad wildcard must be rejected: %+v", res)
	}
	res = run(t, handler, map[string]any{"op": "add", "path": "/var/log/*.log", "max_age_days": 7})
	if res.ExitCode != 0 {
		t.Fatalf("concrete-level wildcard should pass: %+v", res)
	}
}

func TestWhitelistProtected(t *testing.T) {
	m, dataDir := newTestManager(t)
	reg := registry.New()
	if err := Register(reg, m); err != nil {
		t.Fatal(err)
	}
	handler, _ := reg.Get("logclean.manage")

	for _, p := range []string{"/etc/nginx", "/usr/local", "/bin", filepath.Join(dataDir, "sub")} {
		res, err := runErr(t, handler, map[string]any{"op": "add", "path": p, "max_age_days": 1})
		if err == nil || res.ExitCode != 2 || !strings.Contains(err.Error(), "whitelist") {
			t.Fatalf("whitelist path %q must be rejected: %+v", p, res)
		}
	}
}

func TestAddRequiresThreshold(t *testing.T) {
	m, _ := newTestManager(t)
	reg := registry.New()
	_ = Register(reg, m)
	handler, _ := reg.Get("logclean.manage")

	res, err := runErr(t, handler, map[string]any{"op": "add", "path": "/var/log/app.log"})
	if err == nil || res.ExitCode != 2 || !strings.Contains(err.Error(), "must be > 0") {
		t.Fatalf("zero thresholds must be rejected: %+v", res)
	}
}

func TestRemoveUnknownRule(t *testing.T) {
	m, _ := newTestManager(t)
	reg := registry.New()
	_ = Register(reg, m)
	handler, _ := reg.Get("logclean.manage")

	res, err := handler(map[string]any{"op": "remove", "id": "nope"})
	if err == nil || res.ExitCode != 2 {
		t.Fatalf("unknown rule remove must fail: %+v %v", res, err)
	}
}

func TestRunCleansArchivesAndLogs(t *testing.T) {
	m, dataDir := newTestManager(t)
	reg := registry.New()
	_ = Register(reg, m)
	handler, _ := reg.Get("logclean.manage")

	logDir := "/var/log/app"
	if err := os.MkdirAll(logDir, 0o755); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.RemoveAll("/var/log/app") })
	old := time.Now().Add(-10 * 24 * time.Hour)
	p := filepath.Join(logDir, "old.log")
	if err := os.WriteFile(p, []byte(strings.Repeat("x", 4096)), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.Chtimes(p, old, old); err != nil {
		t.Fatal(err)
	}
	fresh := filepath.Join(logDir, "fresh.log")
	if err := os.WriteFile(fresh, []byte("new"), 0o644); err != nil {
		t.Fatal(err)
	}

	run(t, handler, map[string]any{"op": "add", "path": logDir, "max_age_days": 7})

	res := run(t, handler, map[string]any{"op": "run"})
	if res.ExitCode != 0 {
		t.Fatalf("run failed: %+v", res)
	}
	if _, err := os.Stat(p); !os.IsNotExist(err) {
		t.Fatalf("old log should be removed: %v", err)
	}
	if _, err := os.Stat(fresh); err != nil {
		t.Fatalf("fresh log must survive: %v", err)
	}
	var cleaned []CleanedEntry
	raw, _ := json.Marshal(res.Data["cleaned"])
	if err := json.Unmarshal(raw, &cleaned); err != nil {
		t.Fatal(err)
	}
	if len(cleaned) != 1 || cleaned[0].Path != p || cleaned[0].Size != 4096 {
		t.Fatalf("unexpected cleaned entries: %s", raw)
	}
	if !strings.HasSuffix(cleaned[0].Archived, ".gz") {
		t.Fatalf("archive must be gzipped: %+v", cleaned[0])
	}
	if _, err := os.Stat(cleaned[0].Archived); err != nil {
		t.Fatalf("archive file missing: %v", err)
	}
	// Audit trail persisted.
	logRaw, err := os.ReadFile(filepath.Join(dataDir, "logclean", "cleaned.jsonl"))
	if err != nil || !strings.Contains(string(logRaw), p) {
		t.Fatalf("cleaned.jsonl missing entry: %v %s", err, logRaw)
	}
}

func TestRunSkipsWhitelistedFilesViaSymlink(t *testing.T) {
	m, _ := newTestManager(t)
	reg := registry.New()
	_ = Register(reg, m)
	handler, _ := reg.Get("logclean.manage")

	logDir := "/var/log/app-symtest"
	if err := os.MkdirAll(logDir, 0o755); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.RemoveAll(logDir) })
	etcFile := "/etc/logclean-symtest-target"
	if err := os.WriteFile(etcFile, []byte("keep"), 0o644); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.Remove(etcFile) })
	link := filepath.Join(logDir, "evil.log")
	if err := os.Symlink(etcFile, link); err != nil {
		t.Fatal(err)
	}

	res := run(t, handler, map[string]any{"op": "add", "path": logDir, "max_age_days": 1})
	res = run(t, handler, map[string]any{"op": "run"})
	if res.ExitCode != 0 {
		t.Fatalf("run failed: %+v", res)
	}
	if _, err := os.Stat(etcFile); err != nil {
		t.Fatalf("whitelisted symlink target must survive: %v", err)
	}
	if _, err := os.Stat(link); err != nil {
		t.Fatalf("symlink itself should remain (target protected): %v", err)
	}
}

func TestRunSizeThreshold(t *testing.T) {
	m, _ := newTestManager(t)
	reg := registry.New()
	_ = Register(reg, m)
	handler, _ := reg.Get("logclean.manage")

	logDir := "/var/log/app-size"
	if err := os.MkdirAll(logDir, 0o755); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.RemoveAll(logDir) })
	big := filepath.Join(logDir, "big.log")
	if err := os.WriteFile(big, make([]byte, 2<<20), 0o644); err != nil {
		t.Fatal(err)
	}
	small := filepath.Join(logDir, "small.log")
	if err := os.WriteFile(small, []byte("tiny"), 0o644); err != nil {
		t.Fatal(err)
	}

	run(t, handler, map[string]any{"op": "add", "path": logDir, "max_size_mb": 1})
	res := run(t, handler, map[string]any{"op": "run"})
	if res.ExitCode != 0 {
		t.Fatalf("run failed: %+v", res)
	}
	if _, err := os.Stat(big); !os.IsNotExist(err) {
		t.Fatalf("big log should be cleaned: %v", err)
	}
	if _, err := os.Stat(small); err != nil {
		t.Fatalf("small log must survive: %v", err)
	}
}

func TestExpireArchives(t *testing.T) {
	m, dataDir := newTestManager(t)
	reg := registry.New()
	_ = Register(reg, m)
	handler, _ := reg.Get("logclean.manage")

	archiveDir := filepath.Join(dataDir, "logclean", "archive")
	oldDir := filepath.Join(archiveDir, "20260101")
	if err := os.MkdirAll(oldDir, 0o755); err != nil {
		t.Fatal(err)
	}
	freshDir := filepath.Join(archiveDir, time.Now().UTC().Format("20060102"))
	if err := os.MkdirAll(freshDir, 0o755); err != nil {
		t.Fatal(err)
	}

	run(t, handler, map[string]any{"op": "run"})
	if _, err := os.Stat(oldDir); !os.IsNotExist(err) {
		t.Fatalf("expired archive dir should be removed: %v", err)
	}
	if _, err := os.Stat(freshDir); err != nil {
		t.Fatalf("fresh archive dir must survive: %v", err)
	}
}

func TestListEmpty(t *testing.T) {
	m, _ := newTestManager(t)
	reg := registry.New()
	_ = Register(reg, m)
	handler, _ := reg.Get("logclean.manage")

	res := run(t, handler, map[string]any{"op": "list"})
	if res.ExitCode != 0 || !strings.Contains(string(res.Data["rules"].(json.RawMessage)), "[]") {
		t.Fatalf("empty list should return []: %+v", res)
	}
}
