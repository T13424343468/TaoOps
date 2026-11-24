package appdeploy

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// fakeRunner dispatches docker invocations and records every call.
type fakeRunner struct {
	calls     []string
	inspectOK bool   // health status returned by inspect
	upFails   bool   // compose up returns error
	auditPath string
}

func (f *fakeRunner) Run(bin string, args []string, stdin string) (string, string, error) {
	f.calls = append(f.calls, strings.Join(args, " "))
	joined := strings.Join(args, " ")
	switch {
	case strings.HasPrefix(joined, "--host unix:///var/run/docker.sock compose"):
		if f.upFails && strings.Contains(joined, " up") {
			return "", "compose up boom", fmt.Errorf("exit 1")
		}
		if strings.Contains(joined, " ps -q") {
			return "abc123\n", "", nil
		}
		return "", "", nil
	case strings.HasPrefix(joined, "--host unix:///var/run/docker.sock inspect"):
		if f.inspectOK {
			return "healthy\n", "", nil
		}
		return "starting\n", "", nil
	}
	return "", "", fmt.Errorf("unexpected docker call: %s", joined)
}

func (f *fakeRunner) hasCall(substr string) bool {
	for _, c := range f.calls {
		if strings.Contains(c, substr) {
			return true
		}
	}
	return false
}

func newTestManager(t *testing.T, fr *fakeRunner) *Manager {
	t.Helper()
	dir := t.TempDir()
	m := NewManager(filepath.Join(dir, "apps"), filepath.Join(dir, "audit.jsonl"))
	m.Run = fr.Run
	m.PollAttempts = 2
	m.PollInterval = time.Millisecond
	return m
}

func TestTemplatesLibrary(t *testing.T) {
	ts := Templates()
	if len(ts) < 8 || len(ts) > 10 {
		t.Fatalf("template count %d out of first-phase range 8-10", len(ts))
	}
	seen := map[string]bool{}
	for _, tpl := range ts {
		if seen[tpl.ID] {
			t.Fatalf("duplicate template id %q", tpl.ID)
		}
		seen[tpl.ID] = true
		if tpl.Arch == "" {
			t.Fatalf("template %q missing arch annotation", tpl.ID)
		}
		if len(tpl.Services) == 0 {
			t.Fatalf("template %q has no services", tpl.ID)
		}
		for _, s := range tpl.Services {
			if s.Image == "" || s.Name == "" {
				t.Fatalf("template %q service missing image/name", tpl.ID)
			}
		}
	}
	if _, ok := GetTemplate("nginx"); !ok {
		t.Fatal("nginx template missing")
	}
	if _, ok := GetTemplate("nope"); ok {
		t.Fatal("unknown template should not resolve")
	}
}

func TestRenderCompose(t *testing.T) {
	tpl, _ := GetTemplate("wordpress")
	out := renderCompose(tpl, map[string]string{"MYSQL_ROOT_PASSWORD": "s3cret"})
	for _, want := range []string{
		"restart: unless-stopped",
		"healthcheck:",
		`"healthcheck.sh --connect --innodb_initialized"`,
		`MYSQL_ROOT_PASSWORD: "s3cret"`,
		`WORDPRESS_DB_HOST: "db"`,
	} {
		if !strings.Contains(out, want) {
			t.Fatalf("compose render missing %q:\n%s", want, out)
		}
	}
}

func TestDeployHealthy(t *testing.T) {
	fr := &fakeRunner{inspectOK: true}
	m := newTestManager(t, fr)
	st, err := m.Deploy("myapp", "redis", nil)
	if err != nil {
		t.Fatalf("deploy: %v", err)
	}
	if st.Health != "healthy" || st.TemplateID != "redis" {
		t.Fatalf("bad state: %+v", st)
	}
	if !fr.hasCall(" up -d") {
		t.Fatal("compose up not called")
	}
	// state persisted
	if _, err := os.Stat(m.stateFile("myapp")); err != nil {
		t.Fatalf("state file missing: %v", err)
	}
	// audit written
	ab, _ := os.ReadFile(m.AuditPath)
	if !strings.Contains(string(ab), `"deploy"`) {
		t.Fatalf("audit missing deploy entry: %s", ab)
	}
}

func TestDeployHealthFailureTearsDown(t *testing.T) {
	fr := &fakeRunner{inspectOK: false}
	m := newTestManager(t, fr)
	st, err := m.Deploy("myapp", "redis", nil)
	if err == nil {
		t.Fatal("expected health failure error")
	}
	if st.Health != "unhealthy" {
		t.Fatalf("state health = %q, want unhealthy", st.Health)
	}
	if !fr.hasCall(" down") {
		t.Fatal("compose down not called after failed health check")
	}
}

func TestDeployInvalidName(t *testing.T) {
	fr := &fakeRunner{inspectOK: true}
	m := newTestManager(t, fr)
	if _, err := m.Deploy("../evil", "redis", nil); err == nil {
		t.Fatal("expected invalid app name error")
	}
	if _, err := m.Deploy("ok", "nope", nil); err == nil {
		t.Fatal("expected unknown template error")
	}
}

func TestUpgradeRollbackOnHealthFailure(t *testing.T) {
	fr := &fakeRunner{inspectOK: true}
	m := newTestManager(t, fr)
	if _, err := m.Deploy("myapp", "redis", nil); err != nil {
		t.Fatalf("initial deploy: %v", err)
	}
	cf := m.composeFile("myapp")
	good, _ := os.ReadFile(cf)

	// Upgrade with failing health → rollback.
	fr.inspectOK = false
	_, err := m.Upgrade("myapp", nil)
	if err == nil || !strings.Contains(err.Error(), "rolled back") {
		t.Fatalf("expected rollback error, got %v", err)
	}
	restored, _ := os.ReadFile(cf)
	if string(restored) != string(good) {
		t.Fatal("compose file not restored after rollback")
	}
	// state restored to previous (healthy) record
	st, err := m.loadState("myapp")
	if err != nil || st.Health != "healthy" {
		t.Fatalf("state not restored: %+v err=%v", st, err)
	}
}

func TestUpgradeUpFailureRollsBack(t *testing.T) {
	fr := &fakeRunner{inspectOK: true}
	m := newTestManager(t, fr)
	if _, err := m.Deploy("myapp", "redis", nil); err != nil {
		t.Fatalf("initial deploy: %v", err)
	}
	cf := m.composeFile("myapp")
	good, _ := os.ReadFile(cf)

	fr.upFails = true
	_, err := m.Upgrade("myapp", nil)
	if err == nil || !strings.Contains(err.Error(), "rolled back") {
		t.Fatalf("expected rollback error, got %v", err)
	}
	restored, _ := os.ReadFile(cf)
	if string(restored) != string(good) {
		t.Fatal("compose file not restored after up failure")
	}
}

func TestUpgradeUnknownApp(t *testing.T) {
	fr := &fakeRunner{inspectOK: true}
	m := newTestManager(t, fr)
	if _, err := m.Upgrade("ghost", nil); err == nil {
		t.Fatal("expected not-deployed error")
	}
}

func TestListAndRemove(t *testing.T) {
	fr := &fakeRunner{inspectOK: true}
	m := newTestManager(t, fr)
	if _, err := m.Deploy("app1", "redis", nil); err != nil {
		t.Fatalf("deploy: %v", err)
	}
	list, err := m.List()
	if err != nil || len(list) != 1 || list[0].App != "app1" {
		t.Fatalf("list = %+v err=%v", list, err)
	}
	if err := m.Remove("app1"); err != nil {
		t.Fatalf("remove: %v", err)
	}
	if _, err := os.Stat(m.appDir("app1")); !os.IsNotExist(err) {
		t.Fatal("app dir not removed")
	}
	list, _ = m.List()
	if len(list) != 0 {
		t.Fatalf("list after remove = %+v", list)
	}
	if err := m.Remove("ghost"); err == nil {
		t.Fatal("expected remove of unknown app to fail")
	}
}

func TestHandlerOps(t *testing.T) {
	fr := &fakeRunner{inspectOK: true}
	m := newTestManager(t, fr)
	h := Handler(m)

	res, err := h(map[string]any{"op": "templates"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("templates op: %v", err)
	}
	raw, _ := json.Marshal(res.Data["templates"])
	if !strings.Contains(string(raw), "wordpress") {
		t.Fatalf("templates payload missing wordpress: %s", raw)
	}

	res, err = h(map[string]any{"op": "deploy", "app": "web", "template": "redis", "env": map[string]any{"FOO": "bar"}})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("deploy op: %v", err)
	}

	res, err = h(map[string]any{"op": "list"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("list op: %v", err)
	}

	if _, err := h(map[string]any{"op": "bogus"}); err == nil {
		t.Fatal("unknown op should error")
	}
}

func TestAuditTrail(t *testing.T) {
	fr := &fakeRunner{inspectOK: true}
	m := newTestManager(t, fr)
	_, _ = m.Deploy("a1", "nginx", nil)
	_ = m.Remove("a1")
	ab, err := os.ReadFile(m.AuditPath)
	if err != nil {
		t.Fatalf("audit read: %v", err)
	}
	var entries []AuditEntry
	for _, line := range strings.Split(strings.TrimSpace(string(ab)), "\n") {
		var e AuditEntry
		if jerr := json.Unmarshal([]byte(line), &e); jerr != nil {
			t.Fatalf("audit line parse: %v", jerr)
		}
		entries = append(entries, e)
	}
	if len(entries) < 2 || entries[0].Action != "deploy" || entries[len(entries)-1].Action != "remove" {
		t.Fatalf("audit entries = %+v", entries)
	}
}
