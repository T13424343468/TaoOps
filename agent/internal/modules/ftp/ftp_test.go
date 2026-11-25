// FR-A8 单测：账号增删、目录授权、连接测试与路径穿越拒绝。
package ftp

import (
	"encoding/json"
	"errors"
	"net"
	"path/filepath"
	"testing"
	"time"
)

func newManager(t *testing.T) *Manager {
	t.Helper()
	return NewManager(filepath.Join(t.TempDir(), "ftp.json"))
}

func TestAddListDeleteUser(t *testing.T) {
	m := newManager(t)
	if err := m.AddUser("deploy", "s3cret", "/srv/ftp/deploy"); err != nil {
		t.Fatalf("AddUser: %v", err)
	}
	users, err := m.List()
	if err != nil || len(users) != 1 {
		t.Fatalf("List = %v, %v; want 1 user", users, err)
	}
	u := users[0]
	if u.RootDir != "/srv/ftp/deploy" || len(u.AllowedDirs) != 1 {
		t.Fatalf("unexpected user %+v", u)
	}
	if u.PasswordHash == "" || u.PasswordHash == "s3cret" {
		t.Fatal("password must be stored hashed, not raw or empty")
	}
	if err := m.DeleteUser("deploy"); err != nil {
		t.Fatalf("DeleteUser: %v", err)
	}
	if users, _ := m.List(); len(users) != 0 {
		t.Fatal("user not deleted")
	}
	if err := m.DeleteUser("deploy"); err == nil {
		t.Fatal("deleting missing user must fail")
	}
}

func TestAddUserValidation(t *testing.T) {
	m := newManager(t)
	cases := []struct {
		name, pw, root string
	}{
		{"Bad Name", "x", "/srv"}, {"9lead", "x", "/srv"}, {"ok", "", "/srv"}, {"ok", "x", "relative"}, {"ok", "x", "/srv/../etc"},
	}
	for _, c := range cases {
		if err := m.AddUser(c.name, c.pw, c.root); err == nil {
			t.Fatalf("AddUser(%q,%q,%q) must fail", c.name, c.pw, c.root)
		}
	}
	if err := m.AddUser("dup", "x", "/srv"); err != nil {
		t.Fatalf("AddUser dup prep: %v", err)
	}
	if err := m.AddUser("dup", "x", "/srv"); err == nil {
		t.Fatal("duplicate user must fail")
	}
}

func TestGrantRevokeDirs(t *testing.T) {
	m := newManager(t)
	if err := m.AddUser("deploy", "pw", "/srv/ftp"); err != nil {
		t.Fatal(err)
	}
	if err := m.Grant("deploy", "/var/www/site"); err != nil {
		t.Fatalf("Grant: %v", err)
	}
	if err := m.Grant("deploy", "/var/www/site"); err == nil {
		t.Fatal("double grant must fail")
	}
	users, _ := m.List()
	if len(users[0].AllowedDirs) != 2 {
		t.Fatalf("allowed dirs = %v", users[0].AllowedDirs)
	}
	if err := m.Revoke("deploy", "/var/www/site"); err != nil {
		t.Fatalf("Revoke: %v", err)
	}
	if err := m.Revoke("deploy", "/var/www/site"); err == nil {
		t.Fatal("double revoke must fail")
	}
	if err := m.Grant("ghost", "/srv"); err == nil {
		t.Fatal("grant to missing user must fail")
	}
	if err := m.Grant("deploy", "../etc"); err == nil {
		t.Fatal("traversal dir must fail")
	}
}

func TestConnTest(t *testing.T) {
	m := newManager(t)
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Skipf("no loopback: %v", err)
	}
	defer ln.Close()
	go func() {
		for {
			c, err := ln.Accept()
			if err != nil {
				return
			}
			_ = c.Close()
		}
	}()
	port := ln.Addr().(*net.TCPAddr).Port
	if err := m.TestConn("127.0.0.1", port); err != nil {
		t.Fatalf("TestConn to live port: %v", err)
	}
	m.Dial = func(string, int, time.Duration) (net.Conn, error) { return nil, errors.New("refused") }
	if err := m.TestConn("127.0.0.1", 1); err == nil {
		t.Fatal("TestConn to dead endpoint must fail")
	}
}

func TestHandlerDispatch(t *testing.T) {
	m := newManager(t)
	h := Handler(m)
	if res, err := h(map[string]any{"op": "adduser", "user": "web", "password": "pw", "root": "/srv/web"}); err != nil || res.ExitCode != 0 {
		t.Fatalf("adduser via handler: %v %v", res, err)
	}
	res, err := h(map[string]any{"op": "list"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("list via handler: %v %v", res, err)
	}
	if res, err := h(map[string]any{"op": "nonsense"}); err == nil || res.ExitCode != 2 {
		t.Fatalf("unknown op must exit 2: %v %v", res, err)
	}
}

// 下发桥仅回传 Output：list 结构化结果须落 Output JSON（FR-A17 约定）。
func TestHandlerListOutputJSON(t *testing.T) {
	m := newManager(t)
	h := Handler(m)
	if _, err := h(map[string]any{"op": "adduser", "user": "web", "password": "pw", "root": "/srv/web"}); err != nil {
		t.Fatalf("adduser: %v", err)
	}
	res, err := h(map[string]any{"op": "list"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("list: %v %v", res, err)
	}
	var parsed []map[string]any
	if err := json.Unmarshal([]byte(res.Output), &parsed); err != nil {
		t.Fatalf("list output must be JSON array, got %q: %v", res.Output, err)
	}
	if len(parsed) != 1 || parsed[0]["name"] != "web" {
		t.Fatalf("list output users mismatch: %v", parsed)
	}
}
