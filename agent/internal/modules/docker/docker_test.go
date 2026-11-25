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

// ---- FR-A14 扩展（backlog A）：镜像加速器与私有仓库登录 ----

func newMgrCfg(t *testing.T, fail bool) (*Manager, *[]string, string) {
	t.Helper()
	m, log := newMgr(t, fail)
	dir := t.TempDir()
	m.DaemonJSON = filepath.Join(dir, "daemon.json")
	m.DockerConfig = filepath.Join(dir, "config.json")
	return m, log, dir
}

func TestMirrorList_MissingFile_ReturnsEmpty(t *testing.T) {
	m, _, _ := newMgrCfg(t, false)
	list, err := m.MirrorList()
	if err != nil || len(list) != 0 {
		t.Fatalf("MirrorList = %v, %v", list, err)
	}
}

func TestMirrorAdd_WritesConfigPreservingKeys(t *testing.T) {
	m, _, dir := newMgrCfg(t, false)
	os.WriteFile(m.DaemonJSON, []byte("{\"debug\":true,\"registry-mirrors\":[\"https://old.example.com\"]}"), 0o644)
	if err := m.MirrorAdd("https://docker.m.daocloud.io"); err != nil {
		t.Fatalf("MirrorAdd: %v", err)
	}
	b, _ := os.ReadFile(m.DaemonJSON)
	var cfg map[string]any
	if json.Unmarshal(b, &cfg) != nil {
		t.Fatalf("daemon.json invalid json: %s", b)
	}
	if cfg["debug"] != true {
		t.Fatalf("other keys lost: %s", b)
	}
	mirrors := cfg["registry-mirrors"].([]any)
	if len(mirrors) != 2 || mirrors[1] != "https://docker.m.daocloud.io" {
		t.Fatalf("mirrors = %v", mirrors)
	}
	// 备份文件存在
	entries, _ := filepath.Glob(dir + "/daemon.json.bak.*")
	if len(entries) != 1 {
		t.Fatalf("backup missing: %v", entries)
	}
}

func TestMirrorAdd_Duplicate_Idempotent(t *testing.T) {
	m, _, _ := newMgrCfg(t, false)
	_ = m.MirrorAdd("https://a.example.com")
	if err := m.MirrorAdd("https://a.example.com"); err != nil {
		t.Fatalf("dup add: %v", err)
	}
	list, _ := m.MirrorList()
	if len(list) != 1 {
		t.Fatalf("mirrors = %v", list)
	}
}

func TestMirrorAdd_InvalidURL_Rejected(t *testing.T) {
	m, _, _ := newMgrCfg(t, false)
	for _, bad := range []string{"ftp://x.example.com", "https://", "https://a b.com", "a.example.com; rm -rf /"} {
		if err := m.MirrorAdd(bad); err == nil {
			t.Fatalf("accepted bad mirror %q", bad)
		}
	}
}

func TestMirrorRemove_RemovesEntry(t *testing.T) {
	m, _, _ := newMgrCfg(t, false)
	_ = m.MirrorAdd("https://a.example.com")
	_ = m.MirrorAdd("https://b.example.com")
	if err := m.MirrorRemove("https://a.example.com"); err != nil {
		t.Fatalf("MirrorRemove: %v", err)
	}
	list, _ := m.MirrorList()
	if len(list) != 1 || list[0] != "https://b.example.com" {
		t.Fatalf("mirrors = %v", list)
	}
	if err := m.MirrorRemove("https://gone.example.com"); err != nil {
		t.Fatalf("remove missing should be idempotent: %v", err)
	}
}

func TestDaemonRestart_UsesSystemctl(t *testing.T) {
	m, log, _ := newMgrCfg(t, false)
	if err := m.DaemonRestart(); err != nil {
		t.Fatalf("DaemonRestart: %v", err)
	}
	if !strings.Contains((*log)[0], "systemctl restart docker") {
		t.Fatalf("log = %v", *log)
	}
}

func TestRegistryLogin_PasswordViaStdinNotArgv(t *testing.T) {
	var gotStdin string
	var argv []string
	m, _, _ := newMgrCfg(t, false)
	m.Run = func(bin string, args []string, stdin string) (string, string, error) {
		argv = args
		gotStdin = stdin
		return "", "", nil
	}
	if err := m.RegistryLogin("registry.example.com:5000", "ops-user", "s3cret-pw"); err != nil {
		t.Fatalf("RegistryLogin: %v", err)
	}
	if gotStdin != "s3cret-pw" {
		t.Fatalf("password not via stdin: %q", gotStdin)
	}
	joined := strings.Join(argv, " ")
	if strings.Contains(joined, "s3cret-pw") {
		t.Fatalf("password leaked into argv: %v", argv)
	}
	if !strings.Contains(joined, "--password-stdin") || !strings.Contains(joined, "registry.example.com:5000") {
		t.Fatalf("argv = %v", argv)
	}
}

func TestRegistryLogin_EmptyRegistryMeansDockerHub(t *testing.T) {
	var argv []string
	m, _, _ := newMgrCfg(t, false)
	m.Run = func(bin string, args []string, stdin string) (string, string, error) {
		argv = args
		return "", "", nil
	}
	if err := m.RegistryLogin("", "user", "pw"); err != nil {
		t.Fatalf("RegistryLogin: %v", err)
	}
	if strings.Join(argv, " ") != "--host unix:///var/run/docker.sock login -u user --password-stdin" {
		t.Fatalf("argv = %v", argv)
	}
}

func TestRegistryLogin_InvalidInputs_Rejected(t *testing.T) {
	m, _, _ := newMgrCfg(t, false)
	if err := m.RegistryLogin("bad host", "u", "p"); err == nil {
		t.Fatal("accepted bad registry")
	}
	if err := m.RegistryLogin("reg.example.com", "u\tp", "p"); err == nil {
		t.Fatal("accepted bad username")
	}
	if err := m.RegistryLogin("reg.example.com", "u", "p\nrm -rf /"); err == nil {
		t.Fatal("accepted password with newline")
	}
}

func TestRegistryLogin_AuditWithoutPassword(t *testing.T) {
	m, _, dir := newMgrCfg(t, false)
	m.Run = func(bin string, args []string, stdin string) (string, string, error) { return "", "", nil }
	if err := m.RegistryLogin("reg.example.com", "ops-user", "s3cret-pw"); err != nil {
		t.Fatalf("RegistryLogin: %v", err)
	}
	b, _ := os.ReadFile(filepath.Join(dir, "audit.jsonl"))
	if strings.Contains(string(b), "s3cret-pw") {
		t.Fatalf("audit leaked password: %s", b)
	}
	if !strings.Contains(string(b), "registry_login") || !strings.Contains(string(b), "ops-user") {
		t.Fatalf("audit missing action/user: %s", b)
	}
}

func TestRegistryList_ReadsConfigJSON(t *testing.T) {
	m, _, _ := newMgrCfg(t, false)
	os.WriteFile(m.DockerConfig, []byte(`{"auths":{"registry.example.com:5000":{"auth":"xxx"},"https://index.docker.io/v1/":{"auth":"yyy"}}}`), 0o600)
	hosts, err := m.RegistryList()
	if err != nil {
		t.Fatalf("RegistryList: %v", err)
	}
	if len(hosts) != 2 || hosts[0] != "https://index.docker.io/v1/" || hosts[1] != "registry.example.com:5000" {
		t.Fatalf("hosts = %v", hosts)
	}
}

func TestRegistryList_MissingConfig_ReturnsEmpty(t *testing.T) {
	m, _, _ := newMgrCfg(t, false)
	hosts, err := m.RegistryList()
	if err != nil || len(hosts) != 0 {
		t.Fatalf("RegistryList = %v, %v", hosts, err)
	}
}

func TestHandler_RegistryAndMirrorOps(t *testing.T) {
	m, _, _ := newMgrCfg(t, false)
	h := Handler(m)
	r, err := h(map[string]any{"op": "mirror_add", "addr": "https://m.example.com"})
	if err != nil || r.ExitCode != 0 {
		t.Fatalf("mirror_add: %v %v", r, err)
	}
	r, err = h(map[string]any{"op": "mirror_list"})
	if err != nil || r.ExitCode != 0 {
		t.Fatalf("mirror_list: %v %v", r, err)
	}
	r, err = h(map[string]any{"op": "registry_login", "registry": "reg.example.com", "username": "u", "password": "p"})
	if err != nil || r.ExitCode != 0 {
		t.Fatalf("registry_login: %v %v", r, err)
	}
	r, err = h(map[string]any{"op": "registry_list"})
	if err != nil || r.ExitCode != 0 {
		t.Fatalf("registry_list: %v %v", r, err)
	}
	r, _ = h(map[string]any{"op": "registry_logout", "registry": "reg.example.com"})
	if r.ExitCode != 0 {
		t.Fatalf("registry_logout: %v", r)
	}
	r, _ = h(map[string]any{"op": "daemon_restart"})
	if r.ExitCode != 0 {
		t.Fatalf("daemon_restart: %v", r)
	}
	r, _ = h(map[string]any{"op": "nope"})
	if r.ExitCode != 2 {
		t.Fatalf("unknown op should exit 2: %v", r)
	}
}
