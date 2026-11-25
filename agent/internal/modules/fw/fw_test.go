package fw

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func fakeFw(log *[]string, fail bool) Runner {
	return func(bin string, args []string, stdin string) (string, string, error) {
		*log = append(*log, bin+" "+strings.Join(args, " "))
		if fail {
			return "", "permission denied", os.ErrPermission
		}
		return "", "", nil
	}
}

func newMgr(t *testing.T, fail bool) (*Manager, *[]string) {
	t.Helper()
	log := &[]string{}
	return &Manager{
		Run:       fakeFw(log, fail),
		AuditPath: filepath.Join(t.TempDir(), "audit.log"),
		Backend:   "iptables",
		Now:       func() time.Time { return time.Unix(1700000000, 0) },
	}, log
}

func TestAllowRecordsAudit(t *testing.T) {
	m, log := newMgr(t, false)
	if err := m.Allow(8080, "tcp"); err != nil {
		t.Fatalf("Allow: %v", err)
	}
	if len(*log) != 1 || !strings.Contains((*log)[0], "--dport 8080") || !strings.Contains((*log)[0], "ACCEPT") {
		t.Fatalf("calls = %v", *log)
	}
	entries, err := m.Audit()
	if err != nil || len(entries) != 1 {
		t.Fatalf("Audit: %v %v", entries, err)
	}
	if entries[0].Action != "allow" || entries[0].Port != 8080 || entries[0].Proto != "tcp" {
		t.Fatalf("entry = %+v", entries[0])
	}
	if !strings.Contains(entries[0].Rollback, "-D INPUT") {
		t.Fatalf("rollback = %q", entries[0].Rollback)
	}
}

func TestDenyRecordsAudit(t *testing.T) {
	m, _ := newMgr(t, false)
	if err := m.Deny(23, "tcp"); err != nil {
		t.Fatalf("Deny: %v", err)
	}
	entries, _ := m.Audit()
	if entries[0].Action != "deny" {
		t.Fatalf("action = %q", entries[0].Action)
	}
}

func TestValidation(t *testing.T) {
	m, _ := newMgr(t, false)
	if err := m.Allow(0, "tcp"); err == nil {
		t.Fatal("port 0 should fail")
	}
	if err := m.Allow(70000, "tcp"); err == nil {
		t.Fatal("port > 65535 should fail")
	}
	if err := m.Allow(80, "sctp"); err == nil {
		t.Fatal("bad proto should fail")
	}
}

func TestRollbackLast(t *testing.T) {
	m, log := newMgr(t, false)
	if err := m.Allow(80, "tcp"); err != nil {
		t.Fatalf("Allow: %v", err)
	}
	if err := m.Deny(22, "tcp"); err != nil {
		t.Fatalf("Deny: %v", err)
	}
	before := len(*log)
	if err := m.RollbackLast(); err != nil {
		t.Fatalf("RollbackLast: %v", err)
	}
	// rollback should have run one more command
	if len(*log) != before+1 {
		t.Fatalf("expected rollback command, log = %v", *log)
	}
	entries, _ := m.Audit()
	if len(entries) != 1 || entries[0].Action != "allow" {
		t.Fatalf("audit after rollback = %+v", entries)
	}
}

func TestRollbackEmptyFails(t *testing.T) {
	m, _ := newMgr(t, false)
	if err := m.RollbackLast(); err == nil {
		t.Fatal("rollback with empty audit should fail")
	}
}

func TestCommandFailurePropagates(t *testing.T) {
	m, _ := newMgr(t, true)
	if err := m.Allow(80, "tcp"); err == nil {
		t.Fatal("command failure should propagate")
	}
	entries, _ := m.Audit()
	if len(entries) != 0 {
		t.Fatal("failed command must not be audited")
	}
}

func TestFirewalldBackend(t *testing.T) {
	m, log := newMgr(t, false)
	m.Backend = "firewalld"
	if err := m.Allow(443, "tcp"); err != nil {
		t.Fatalf("Allow: %v", err)
	}
	if !strings.Contains((*log)[0], "--add-port 443/tcp") {
		t.Fatalf("call = %v", (*log)[0])
	}
	if err := m.Deny(80, "tcp"); err == nil {
		t.Fatal("firewalld deny should be rejected")
	}
}

func TestHandlerOps(t *testing.T) {
	m, _ := newMgr(t, false)
	h := Handler(m)
	if _, err := h(map[string]any{"op": "bogus"}); err == nil {
		t.Fatal("unknown op should fail")
	}
	if _, err := h(map[string]any{"op": "allow", "port": 8080}); err != nil {
		t.Fatalf("allow: %v", err)
	}
	if _, err := h(map[string]any{"op": "audit"}); err != nil {
		t.Fatalf("audit: %v", err)
	}
	if _, err := h(map[string]any{"op": "rollback"}); err != nil {
		t.Fatalf("rollback: %v", err)
	}
	if _, err := h(map[string]any{"op": "remove", "port": 8080}); err != nil {
		t.Fatalf("remove: %v", err)
	}
}

func TestHandlerAuditOutputJSON(t *testing.T) {
	m, _ := newMgr(t, false)
	h := Handler(m)
	if _, err := h(map[string]any{"op": "allow", "port": 8080}); err != nil {
		t.Fatalf("allow: %v", err)
	}
	res, err := h(map[string]any{"op": "audit"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("audit: %v %v", res, err)
	}
	var parsed struct {
		Entries []map[string]any `json:"entries"`
	}
	if err := json.Unmarshal([]byte(res.Output), &parsed); err != nil {
		t.Fatalf("audit output must be JSON object, got %q: %v", res.Output, err)
	}
	if len(parsed.Entries) != 1 {
		t.Fatalf("audit output entries mismatch: %v", parsed.Entries)
	}
}
