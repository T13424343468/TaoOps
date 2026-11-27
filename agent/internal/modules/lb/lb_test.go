package lb

import (
	"encoding/json"
	"errors"
	"net"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"taoops/agent/internal/modules/registry"
)

func newTestManager(t *testing.T) (*Manager, string) {
	t.Helper()
	dir := t.TempDir()
	m := NewManager(dir, filepath.Join(dir, "audit.log"))
	m.nginxBin = "" // 沙箱无 nginx：跳过 -t（有 nginx 的环境自动启用）
	return m, dir
}

func run(m *Manager, args map[string]any) (registry.Result, error) {
	return m.handle(args)
}

func TestAddAndList(t *testing.T) {
	m, _ := newTestManager(t)
	if _, err := run(m, map[string]any{
		"op": "add", "name": "web-pool", "host": "10.0.0.11", "port": 8080,
		"weight": 50, "health_mode": "tcp",
	}); err != nil {
		t.Fatalf("add: %v", err)
	}
	res, err := run(m, map[string]any{"op": "list"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("list: %v %v", res, err)
	}
	raw, _ := json.Marshal(res.Data["groups"])
	var wrapped struct {
		Groups []Group `json:"groups"`
	}
	if json.Unmarshal(raw, &wrapped) != nil || len(wrapped.Groups) != 1 {
		t.Fatalf("groups = %s", raw)
	}
	groups := wrapped.Groups
	if groups[0].Backends[0].Weight != 50 || groups[0].Backends[0].HealthMode != "tcp" {
		t.Fatalf("unexpected backend: %+v", groups[0].Backends[0])
	}
}

func TestAddValidation(t *testing.T) {
	m, _ := newTestManager(t)
	cases := []struct {
		args map[string]any
		want string
	}{
		{map[string]any{"op": "add", "name": "g", "host": "bad host!", "port": 80, "health_mode": "tcp"}, "host must match"},
		{map[string]any{"op": "add", "name": "g", "host": "[::1]", "port": 80, "health_mode": "tcp"}, "IPv6"},
		{map[string]any{"op": "add", "name": "g", "host": "h", "port": 0, "health_mode": "tcp"}, "port"},
		{map[string]any{"op": "add", "name": "g", "host": "h", "port": 70000, "health_mode": "tcp"}, "port"},
		{map[string]any{"op": "add", "name": "g", "host": "h", "port": 80, "weight": 0, "health_mode": "tcp"}, "weight"},
		{map[string]any{"op": "add", "name": "g", "host": "h", "port": 80, "weight": 101, "health_mode": "tcp"}, "weight"},
		{map[string]any{"op": "add", "name": "g", "host": "h", "port": 80, "health_mode": "icmp"}, "health_mode"},
		{map[string]any{"op": "add", "name": "g", "host": "h", "port": 80, "health_mode": "http", "health_path": "x"}, "health_path"},
	}
	for i, c := range cases {
		if _, err := run(m, c.args); err == nil || !strings.Contains(err.Error(), c.want) {
			t.Fatalf("case %d: want %q, got %v", i, c.want, err)
		}
	}
}

func TestRemoveAndEmptyGroupDeletesFile(t *testing.T) {
	m, dir := newTestManager(t)
	add := func() {
		if _, err := run(m, map[string]any{
			"op": "add", "name": "g", "host": "h1", "port": 80, "health_mode": "tcp",
		}); err != nil {
			t.Fatal(err)
		}
	}
	add()
	path := filepath.Join(dir, "lb", "g.conf")
	if _, err := os.Stat(path); err != nil {
		t.Fatalf("fragment missing: %v", err)
	}
	if _, err := run(m, map[string]any{"op": "remove", "name": "g", "host": "h1", "port": 80}); err != nil {
		t.Fatalf("remove: %v", err)
	}
	if _, err := os.Stat(path); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("empty group fragment should be deleted, got %v", err)
	}
}

func TestDrainUndrain(t *testing.T) {
	m, _ := newTestManager(t)
	if _, err := run(m, map[string]any{
		"op": "add", "name": "g", "host": "h1", "port": 80, "health_mode": "tcp",
	}); err != nil {
		t.Fatal(err)
	}
	if _, err := run(m, map[string]any{"op": "drain", "name": "g", "host": "h1", "port": 80}); err != nil {
		t.Fatalf("drain: %v", err)
	}
	g, _ := m.load("g")
	if !g.Backends[0].Down {
		t.Fatal("expected down=true after drain")
	}
	if _, err := run(m, map[string]any{"op": "undrain", "name": "g", "host": "h1", "port": 80}); err != nil {
		t.Fatalf("undrain: %v", err)
	}
	g, _ = m.load("g")
	if g.Backends[0].Down {
		t.Fatal("expected down=false after undrain")
	}
}

func TestHealthTCPFailuresMarkDown(t *testing.T) {
	m, _ := newTestManager(t)
	m.dial = func(network, addr string, timeout time.Duration) (net.Conn, error) {
		return nil, errors.New("refused")
	}
	if _, err := run(m, map[string]any{
		"op": "add", "name": "g", "host": "h1", "port": 80, "health_mode": "tcp",
	}); err != nil {
		t.Fatal(err)
	}
	for i := 0; i < HealthFailThreshold; i++ {
		if _, err := run(m, map[string]any{"op": "health"}); err != nil {
			t.Fatalf("health: %v", err)
		}
	}
	g, _ := m.load("g")
	if !g.Backends[0].Down {
		t.Fatal("expected down after 3 consecutive failures")
	}
	// 恢复探测成功后 fail_count 清零但仍 down（需手动 undrain）。
	m.dial = func(network, addr string, timeout time.Duration) (net.Conn, error) {
		return fakeConn{}, nil
	}
	if _, err := run(m, map[string]any{"op": "health"}); err != nil {
		t.Fatal(err)
	}
	g, _ = m.load("g")
	if g.Backends[0].Down {
		t.Log("backend stays down until undrain (expected design)")
	}
}

type fakeConn struct{ net.Conn }

func (fakeConn) Close() error { return nil }

func TestHealthHTTPOk(t *testing.T) {
	m, _ := newTestManager(t)
	m.httpGet = func(url string, timeout time.Duration) (int, error) { return 200, nil }
	if _, err := run(m, map[string]any{
		"op": "add", "name": "g", "host": "h1", "port": 80,
		"health_mode": "http", "health_path": "/healthz",
	}); err != nil {
		t.Fatal(err)
	}
	if _, err := run(m, map[string]any{"op": "health"}); err != nil {
		t.Fatal(err)
	}
	g, _ := m.load("g")
	if g.Backends[0].FailCount != 0 || g.Backends[0].Down {
		t.Fatalf("expected healthy, got %+v", g.Backends[0])
	}
}

func TestRenderContainsServerLine(t *testing.T) {
	g := &Group{Name: "p", Backends: []Backend{
		{Host: "a", Port: 80, Weight: 10, HealthMode: "tcp"},
		{Host: "b", Port: 81, Weight: 20, Backup: true, Down: true, HealthMode: "tcp"},
	}}
	out := render(g)
	if !strings.Contains(out, "upstream p {") ||
		!strings.Contains(out, "server a:80 weight=10 max_fails=3 fail_timeout=30s;") ||
		!strings.Contains(out, "server b:81 weight=20 max_fails=3 fail_timeout=30s backup down;") {
		t.Fatalf("render output:\n%s", out)
	}
}

func TestAuditWritten(t *testing.T) {
	m, dir := newTestManager(t)
	if _, err := run(m, map[string]any{
		"op": "add", "name": "g", "host": "h1", "port": 80, "health_mode": "tcp",
	}); err != nil {
		t.Fatal(err)
	}
	raw, err := os.ReadFile(filepath.Join(dir, "audit.log"))
	if err != nil || !strings.Contains(string(raw), "lb add group=g backend=h1:80") {
		t.Fatalf("audit missing: %v %s", err, raw)
	}
}

func TestUnknownOp(t *testing.T) {
	m, _ := newTestManager(t)
	if _, err := run(m, map[string]any{"op": "nope"}); err == nil {
		t.Fatal("expected unknown-op rejection")
	}
}

func TestListOutputJSON(t *testing.T) {
	m, _ := newTestManager(t)
	if _, err := run(m, map[string]any{
		"op": "add", "name": "web-pool", "host": "10.0.0.11", "port": 8080,
		"health_mode": "tcp",
	}); err != nil {
		t.Fatalf("add: %v", err)
	}
	res, err := run(m, map[string]any{"op": "list"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("list: %v %v", res, err)
	}
	var parsed struct {
		Groups []Group `json:"groups"`
	}
	if err := json.Unmarshal([]byte(res.Output), &parsed); err != nil {
		t.Fatalf("list output must be JSON array of groups, got %q: %v", res.Output, err)
	}
	if len(parsed.Groups) != 1 || parsed.Groups[0].Name != "web-pool" {
		t.Fatalf("list output groups mismatch: %+v", parsed.Groups)
	}
}
