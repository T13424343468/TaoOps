package site

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func newMgr(t *testing.T) *Manager {
	t.Helper()
	dir := t.TempDir()
	return NewManager(filepath.Join(dir, "conf"), filepath.Join(dir, "backup"))
}

func TestCreateAndList(t *testing.T) {
	m := newMgr(t)
	if err := m.Create("app1", []string{"a.example.com"}, "http://127.0.0.1:3000", 8080); err != nil {
		t.Fatalf("Create: %v", err)
	}
	if _, err := os.Stat(m.sitePath("app1")); err != nil {
		t.Fatalf("config missing: %v", err)
	}
	sites, err := m.List()
	if err != nil || len(sites) != 1 {
		t.Fatalf("List: %v sites=%v", err, sites)
	}
	if sites[0]["server_name"] != "a.example.com" {
		t.Fatalf("server_name = %q", sites[0]["server_name"])
	}
	if sites[0]["proxy_pass"] != "http://127.0.0.1:3000" {
		t.Fatalf("proxy_pass = %q", sites[0]["proxy_pass"])
	}
}

func TestCreateDuplicateFails(t *testing.T) {
	m := newMgr(t)
	if err := m.Create("app1", []string{"a.com"}, "", 0); err != nil {
		t.Fatalf("first Create: %v", err)
	}
	if err := m.Create("app1", []string{"a.com"}, "", 0); err == nil {
		t.Fatal("duplicate create should fail")
	}
}

func TestCreateValidation(t *testing.T) {
	m := newMgr(t)
	if err := m.Create("", []string{"a.com"}, "", 0); err == nil {
		t.Fatal("empty name should fail")
	}
	if err := m.Create("x", nil, "", 0); err == nil {
		t.Fatal("empty domains should fail")
	}
}

func TestUpdateBacksUp(t *testing.T) {
	m := newMgr(t)
	m.Now = func() time.Time { return time.Unix(1700000000, 0) }
	if err := m.Create("app1", []string{"old.com"}, "", 0); err != nil {
		t.Fatalf("Create: %v", err)
	}
	if err := m.Update("app1", []string{"new.com"}, "", 0); err != nil {
		t.Fatalf("Update: %v", err)
	}
	sites, _ := m.List()
	if sites[0]["server_name"] != "new.com" {
		t.Fatalf("update not applied: %v", sites[0]["server_name"])
	}
	bk, err := m.ListBackups("app1")
	if err != nil || len(bk) != 1 {
		t.Fatalf("ListBackups: %v %v", bk, err)
	}
	data, _ := os.ReadFile(filepath.Join(m.BackupDir, bk[0]))
	if !strings.Contains(string(data), "old.com") {
		t.Fatalf("backup should contain old config, got %q", string(data))
	}
}

func TestDeleteBacksUpAndRemoves(t *testing.T) {
	m := newMgr(t)
	if err := m.Create("app1", []string{"a.com"}, "", 0); err != nil {
		t.Fatalf("Create: %v", err)
	}
	if err := m.Delete("app1"); err != nil {
		t.Fatalf("Delete: %v", err)
	}
	if _, err := os.Stat(m.sitePath("app1")); !os.IsNotExist(err) {
		t.Fatal("config should be gone")
	}
	bk, _ := m.ListBackups("app1")
	if len(bk) != 1 {
		t.Fatalf("delete should leave one backup, got %v", bk)
	}
	if err := m.Delete("app1"); err == nil {
		t.Fatal("deleting missing site should fail")
	}
}

func TestSanitizeName(t *testing.T) {
	got := sanitizeName("my site/..x")
	if got != "my-site-..x" {
		t.Fatalf("sanitizeName = %q", got)
	}
}

func TestHandlerOps(t *testing.T) {
	m := newMgr(t)
	h := Handler(m)
	if _, err := h(map[string]any{"op": "bogus"}); err == nil {
		t.Fatal("unknown op should fail")
	}
	if _, err := h(map[string]any{"op": "create", "name": "h1", "domains": []any{"h.com"}}); err != nil {
		t.Fatalf("create via handler: %v", err)
	}
	res, err := h(map[string]any{"op": "list"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("list via handler: %v", err)
	}
	res, err = h(map[string]any{"op": "backups", "name": "h1"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("backups via handler: %v", err)
	}
	if _, err := h(map[string]any{"op": "delete", "name": "h1"}); err != nil {
		t.Fatalf("delete via handler: %v", err)
	}
}
