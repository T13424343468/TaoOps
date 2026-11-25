package backup

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func jsonUnmarshal(b []byte, v any) error { return json.Unmarshal(b, v) }

func newPolicyManager(t *testing.T) *Manager {
	t.Helper()
	return NewManager(t.TempDir())
}

func TestPolicyAddListRemove(t *testing.T) {
	m := newPolicyManager(t)
	h := m.handle
	if _, err := h(map[string]any{"op": "policy_add", "name": "site", "source": "/www", "schedule": "interval:3600", "retain": 3}); err != nil {
		t.Fatalf("policy_add: %v", err)
	}
	res, err := h(map[string]any{"op": "policy_list"})
	if err != nil || !strings.Contains(res.Output, `"name":"site"`) {
		t.Fatalf("policy_list: %q err=%v", res.Output, err)
	}
	// duplicate name rejected
	if _, err := h(map[string]any{"op": "policy_add", "name": "site", "source": "/www", "schedule": "interval:3600"}); err == nil {
		t.Fatal("duplicate policy should fail")
	}
	// find id and remove
	var ps []Policy
	if err := jsonUnmarshal([]byte(res.Output), &ps); err != nil || len(ps) != 1 {
		t.Fatalf("parse policies: %v", err)
	}
	if _, err := h(map[string]any{"op": "policy_remove", "id": ps[0].ID}); err != nil {
		t.Fatalf("policy_remove: %v", err)
	}
	res, _ = h(map[string]any{"op": "policy_list"})
	if strings.Contains(res.Output, "site") {
		t.Fatal("policy should be gone")
	}
}

func TestPolicyAddValidation(t *testing.T) {
	m := newPolicyManager(t)
	h := m.handle
	if _, err := h(map[string]any{"op": "policy_add", "name": "x", "source": "/w", "schedule": "cron:blah"}); err == nil {
		t.Fatal("bad schedule should fail")
	}
	if _, err := h(map[string]any{"op": "policy_add", "name": "x", "source": "/w", "schedule": "interval:10"}); err == nil {
		t.Fatal("interval < 60s should fail")
	}
	if _, err := h(map[string]any{"op": "policy_add", "name": "x", "source": "/w", "schedule": "daily:25:00"}); err == nil {
		t.Fatal("hour 25 should fail")
	}
	if _, err := h(map[string]any{"op": "policy_add", "name": "x", "source": "/w", "schedule": "daily:03:00", "type": "weird"}); err == nil {
		t.Fatal("bad type should fail")
	}
}

func TestPolicyRunCreatesAndRotates(t *testing.T) {
	m := newPolicyManager(t)
	src := t.TempDir()
	os.WriteFile(filepath.Join(src, "a.txt"), []byte("hello"), 0o644)
	h := m.handle
	if _, err := h(map[string]any{"op": "policy_add", "name": "web", "source": src, "schedule": "interval:60", "retain": 2}); err != nil {
		t.Fatalf("policy_add: %v", err)
	}
	res, _ := h(map[string]any{"op": "policy_list"})
	var ps []Policy
	jsonUnmarshal([]byte(res.Output), &ps)
	id := ps[0].ID

	// run 4 times with distinct stamps (Manager.now is real clock; force distinct by sleeping is slow —
	// instead call opCreate directly to simulate older archives, then policy_run rotates).
	dest := filepath.Join(m.Dir(), "web")
	os.MkdirAll(dest, 0o755)
	for i := 0; i < 3; i++ {
		stamp := time.Now().UTC().Add(-time.Duration(10-i) * time.Minute).Format("20060102-150405")
		os.WriteFile(filepath.Join(dest, fmt.Sprintf("web-%s.tar.gz", stamp)), []byte("old"), 0o644)
	}
	if _, err := h(map[string]any{"op": "policy_run", "id": id}); err != nil {
		t.Fatalf("policy_run: %v", err)
	}
	matches, _ := filepath.Glob(filepath.Join(dest, "web-*.tar.gz"))
	if len(matches) != 2 {
		t.Fatalf("archives = %d, want 2 (retain)", len(matches))
	}
	// policy state updated
	res, _ = h(map[string]any{"op": "policy_list"})
	if !strings.Contains(res.Output, `"last_result":"ok"`) {
		t.Fatalf("last_result not updated: %s", res.Output)
	}
}

func TestPolicyRunFailureRecords(t *testing.T) {
	m := newPolicyManager(t)
	h := m.handle
	if _, err := h(map[string]any{"op": "policy_add", "name": "gone", "source": "/nonexistent-path-xyz", "schedule": "interval:60"}); err != nil {
		t.Fatalf("policy_add: %v", err)
	}
	res, _ := h(map[string]any{"op": "policy_list"})
	var ps []Policy
	jsonUnmarshal([]byte(res.Output), &ps)
	if _, err := h(map[string]any{"op": "policy_run", "id": ps[0].ID}); err == nil {
		t.Fatal("run on missing source should fail")
	}
	res, _ = h(map[string]any{"op": "policy_list"})
	if !strings.Contains(res.Output, "failed:") {
		t.Fatalf("failure not recorded: %s", res.Output)
	}
}

func TestPolicyDue(t *testing.T) {
	now := time.Date(2026, 11, 24, 15, 0, 0, 0, time.UTC)
	p := Policy{Enabled: true, Schedule: "interval:3600"}
	if !p.Due(now) {
		t.Fatal("never-run interval policy is due")
	}
	p.LastRunMs = now.Add(-30 * time.Minute).UnixMilli()
	if p.Due(now) {
		t.Fatal("interval not elapsed yet")
	}
	p.LastRunMs = now.Add(-2 * time.Hour).UnixMilli()
	if !p.Due(now) {
		t.Fatal("interval elapsed should be due")
	}

	d := Policy{Enabled: true, Schedule: "daily:03:00"}
	if !d.Due(now) {
		t.Fatal("never-run daily policy is due")
	}
	d.LastRunMs = time.Date(2026, 11, 24, 3, 0, 0, 0, time.UTC).UnixMilli()
	if d.Due(now) {
		t.Fatal("already run today should not be due")
	}
	d.LastRunMs = time.Date(2026, 11, 23, 3, 0, 0, 0, time.UTC).UnixMilli()
	if !d.Due(now) {
		t.Fatal("ran yesterday, past 03:00, should be due")
	}
	early := Policy{Enabled: true, Schedule: "daily:23:00", LastRunMs: time.Date(2026, 11, 23, 23, 0, 0, 0, time.UTC).UnixMilli()}
	if early.Due(now) {
		t.Fatal("scheduled time not reached today should not be due")
	}
	off := Policy{Enabled: false, Schedule: "interval:60"}
	if off.Due(now) {
		t.Fatal("disabled policy never due")
	}
}

func TestRotateDeleteFailureNonBlocking(t *testing.T) {
	dir := t.TempDir()
	// a directory where an "archive" cannot be removed: make one a directory
	for i := 0; i < 4; i++ {
		p := filepath.Join(dir, fmt.Sprintf("x-%d.tar.gz", i))
		if i == 0 {
			os.Mkdir(p, 0o755) // Remove fails on non-empty dir; empty dir actually removes, so fill it
			os.WriteFile(filepath.Join(p, "keep"), []byte("x"), 0o644)
			continue
		}
		os.WriteFile(p, []byte("data"), 0o644)
	}
	rotateBackupDir(dir, "x", 2) // must not panic or error
	// newest 2 files still there
	if _, err := os.Stat(filepath.Join(dir, "x-3.tar.gz")); err != nil {
		t.Fatal("newest archive missing")
	}
}
