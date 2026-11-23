package docker

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"taoops/agent/internal/modules/registry"
)

func fakeRun(log *[]string, fail bool) Runner {
	return func(bin string, args []string, stdin string) (string, string, error) {
		*log = append(*log, bin+" "+strings.Join(args, " "))
		if fail {
			return "", "Cannot connect to the Docker daemon", os.ErrPermission
		}
		// Minimal canned responses per subcommand (args[2] is the
		// subcommand after --host and the socket URL).
		switch {
		case len(args) > 2 && args[2] == "ps":
			return `{"ID":"abc123","Names":["web"],"Image":"nginx:1.25","State":"running","Status":"Up 2 hours","Ports":"80/tcp"}`, "", nil
		case len(args) > 2 && args[2] == "images":
			return `{"Repository":"nginx","Tag":"1.25","ID":"a1b2c3","Size":"187MB","CreatedAt":"2 weeks ago"}`, "", nil
		case len(args) > 2 && args[2] == "pull":
			return "Pulling from library/nginx\nStatus: Downloaded newer image", "", nil
		case len(args) > 2 && args[2] == "logs":
			return "2026-11-23 log line", "", nil
		case len(args) > 2 && args[2] == "exec":
			return "total 0", "", nil
		}
		return "", "", nil
	}
}

func newMgr(t *testing.T, fail bool) (*Manager, *[]string) {
	t.Helper()
	log := &[]string{}
	return &Manager{
		Run:       fakeRun(log, fail),
		Socket:    DefaultSocket,
		AuditPath: filepath.Join(t.TempDir(), "audit.jsonl"),
		Now:       func() time.Time { return time.Unix(1700000000, 0) },
	}, log
}

func TestSocketPinned(t *testing.T) {
	m, log := newMgr(t, false)
	if _, err := m.ListContainers(); err != nil {
		t.Fatalf("ListContainers: %v", err)
	}
	if len(*log) != 1 {
		t.Fatalf("calls = %v", *log)
	}
	if !strings.Contains((*log)[0], "--host unix:///var/run/docker.sock") {
		t.Fatalf("socket not pinned: %v", (*log)[0])
	}
}

func TestListContainers(t *testing.T) {
	m, _ := newMgr(t, false)
	list, err := m.ListContainers()
	if err != nil || len(list) != 1 {
		t.Fatalf("ListContainers: %v %v", list, err)
	}
	if list[0].ID != "abc123" || list[0].Names[0] != "web" || list[0].State != "running" {
		t.Fatalf("row = %+v", list[0])
	}
}

func TestContainerActionValidation(t *testing.T) {
	m, _ := newMgr(t, false)
	if err := m.ContainerAction("start", ""); err == nil {
		t.Fatal("empty name should fail")
	}
	if err := m.ContainerAction("start", "-bad/name"); err == nil {
		t.Fatal("invalid name should fail")
	}
	if err := m.ContainerAction("rm", "web"); err == nil {
		t.Fatal("unknown action should fail")
	}
	if err := m.ContainerAction("start", strings.Repeat("a", 129)); err == nil {
		t.Fatal("name > 128 chars should fail")
	}
}

func TestContainerLifecycleAndAudit(t *testing.T) {
	m, log := newMgr(t, false)
	for _, a := range []string{"start", "stop", "restart"} {
		if err := m.ContainerAction(a, "web"); err != nil {
			t.Fatalf("%s: %v", a, err)
		}
	}
	if len(*log) != 3 {
		t.Fatalf("calls = %v", *log)
	}
	entries := readAudit(t, m.AuditPath)
	if len(entries) != 3 || entries[0].Action != "start" || entries[2].Action != "restart" {
		t.Fatalf("audit = %+v", entries)
	}
	if entries[0].Target != "web" || entries[0].Time == "" {
		t.Fatalf("entry = %+v", entries[0])
	}
}

func TestLogs(t *testing.T) {
	m, log := newMgr(t, false)
	out, err := m.Logs("web", 50)
	if err != nil || !strings.Contains(out, "log line") {
		t.Fatalf("Logs: %q %v", out, err)
	}
	if !strings.Contains((*log)[0], "--tail 50") {
		t.Fatalf("tail arg missing: %v", (*log)[0])
	}
	if _, err := m.Logs("bad name", 10); err == nil {
		t.Fatal("invalid name should fail")
	}
}

func TestTerminal(t *testing.T) {
	m, log := newMgr(t, false)
	out, err := m.Terminal("web", "ls /")
	if err != nil || !strings.Contains(out, "total") {
		t.Fatalf("Terminal: %q %v", out, err)
	}
	if !strings.Contains((*log)[0], "sh -c ls /") {
		t.Fatalf("exec args = %v", (*log)[0])
	}
	if _, err := m.Terminal("web", "  "); err == nil {
		t.Fatal("empty command should fail")
	}
}

func TestListImages(t *testing.T) {
	m, _ := newMgr(t, false)
	list, err := m.ListImages()
	if err != nil || len(list) != 1 {
		t.Fatalf("ListImages: %v %v", list, err)
	}
	if list[0].Repo != "nginx" || list[0].Tag != "1.25" || list[0].Size != "187MB" {
		t.Fatalf("row = %+v", list[0])
	}
}

func TestPullImageValidation(t *testing.T) {
	m, log := newMgr(t, false)
	if _, err := m.PullImage("Nginx bad"); err == nil {
		t.Fatal("invalid ref should fail")
	}
	if _, err := m.PullImage("nginx:1.25"); err != nil {
		t.Fatalf("PullImage: %v", err)
	}
	if len(*log) != 1 || !strings.Contains((*log)[0], "pull nginx:1.25") {
		t.Fatalf("calls = %v", *log)
	}
}

func TestPullViaMirror(t *testing.T) {
	m, log := newMgr(t, false)
	m.Mirror = "mirror.example.com/library"
	if _, err := m.PullImage("nginx:1.25"); err != nil {
		t.Fatalf("PullImage: %v", err)
	}
	if !strings.Contains((*log)[0], "pull mirror.example.com/library/nginx:1.25") {
		t.Fatalf("mirror not used: %v", (*log)[0])
	}
	m.Mirror = "bad mirror//"
	if _, err := m.PullImage("nginx:1.25"); err == nil {
		t.Fatal("invalid mirror should fail")
	}
}

func TestRemoveImage(t *testing.T) {
	m, log := newMgr(t, false)
	if err := m.RemoveImage("nginx:1.25"); err != nil {
		t.Fatalf("RemoveImage: %v", err)
	}
	if !strings.Contains((*log)[0], "rmi nginx:1.25") {
		t.Fatalf("calls = %v", (*log)[0])
	}
	if err := m.RemoveImage("bad ref"); err == nil {
		t.Fatal("invalid ref should fail")
	}
}

func TestImageRefRegex(t *testing.T) {
	ok := []string{"nginx", "nginx:1.25", "library/nginx:latest", "registry.example.com:5000/app:v1", "nginx@sha256:" + strings.Repeat("a", 64)}
	for _, r := range ok {
		if !ImageRef.MatchString(r) {
			t.Errorf("%q should match", r)
		}
	}
	bad := []string{"", "-lead", "nginx tag", "nginx:tag extra", "a b"}
	for _, r := range bad {
		if ImageRef.MatchString(r) {
			t.Errorf("%q should not match", r)
		}
	}
}

func TestHandlerOps(t *testing.T) {
	m, _ := newMgr(t, false)
	h := Handler(m)
	res, err := h(map[string]any{"op": "list"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("list: %v %v", res, err)
	}
	if _, ok := res.Data["containers"]; !ok {
		t.Fatal("containers data missing")
	}
	res, err = h(map[string]any{"op": "image_list"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("image_list: %v %v", res, err)
	}
	res, err = h(map[string]any{"op": "start", "name": "web"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("start: %v %v", res, err)
	}
	if _, err := h(map[string]any{"op": "nope"}); err == nil {
		t.Fatal("unknown op should fail")
	}
	if _, err := h(map[string]any{"op": "start", "name": "-bad"}); err == nil {
		t.Fatal("invalid name should fail")
	}
}

func TestHandlerPullRemoveAudit(t *testing.T) {
	m, _ := newMgr(t, false)
	h := Handler(m)
	if _, err := h(map[string]any{"op": "image_pull", "ref": "nginx:1.25"}); err != nil {
		t.Fatalf("image_pull: %v", err)
	}
	if _, err := h(map[string]any{"op": "image_remove", "ref": "nginx:1.25"}); err != nil {
		t.Fatalf("image_remove: %v", err)
	}
	entries := readAudit(t, m.AuditPath)
	if len(entries) != 2 || entries[0].Action != "image_pull" || entries[1].Action != "image_remove" {
		t.Fatalf("audit = %+v", entries)
	}
}

func TestHandlerLogsTail(t *testing.T) {
	m, log := newMgr(t, false)
	h := Handler(m)
	if _, err := h(map[string]any{"op": "logs", "name": "web", "tail": float64(20)}); err != nil {
		t.Fatalf("logs: %v", err)
	}
	if !strings.Contains((*log)[0], "--tail 20") {
		t.Fatalf("tail = %v", (*log)[0])
	}
}

func TestHandlerTerminal(t *testing.T) {
	m, _ := newMgr(t, false)
	h := Handler(m)
	res, err := h(map[string]any{"op": "terminal", "name": "web", "command": "uptime"})
	if err != nil || res.ExitCode != 0 || !strings.Contains(res.Output, "total") {
		t.Fatalf("terminal: %v %v", res, err)
	}
}

func TestRegister(t *testing.T) {
	m, _ := newMgr(t, false)
	r := registry.New()
	if err := Register(r, m); err != nil {
		t.Fatalf("Register: %v", err)
	}
	if _, ok := r.Get("docker.manage"); !ok {
		t.Fatal("docker.manage not registered")
	}
}

func TestFailRunnerSurfacesStderr(t *testing.T) {
	m, _ := newMgr(t, true)
	_, err := m.ListContainers()
	if err == nil || !strings.Contains(err.Error(), "Cannot connect") {
		t.Fatalf("err = %v", err)
	}
}

func readAudit(t *testing.T, path string) []AuditEntry {
	t.Helper()
	b, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read audit: %v", err)
	}
	var entries []AuditEntry
	for _, line := range strings.Split(strings.TrimSpace(string(b)), "\n") {
		if line == "" {
			continue
		}
		var e AuditEntry
		if err := json.Unmarshal([]byte(line), &e); err != nil {
			t.Fatalf("parse audit: %v", err)
		}
		entries = append(entries, e)
	}
	return entries
}
