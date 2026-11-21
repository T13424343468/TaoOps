package cron

import (
	"fmt"
	"os"
	"strings"
	"testing"
	"time"
)

func fakeCron(initial string, log *[]string) Runner {
	current := initial
	return func(bin string, args []string, stdin string) (string, string, error) {
		*log = append(*log, bin+" "+strings.Join(args, " "))
		if bin == "crontab" && len(args) > 0 && args[0] == "-l" {
			if current == "" {
				return "", "no crontab for user", fmt.Errorf("exit 1")
			}
			return current, "", nil
		}
		if bin == "crontab" && len(args) > 0 && args[0] == "-" {
			current = stdin
			return "", "", nil
		}
		if bin == "/bin/sh" {
			return "ran", "", nil
		}
		return "", "", nil
	}
}

func newMgr(t *testing.T, initial string) (*Manager, *[]string) {
	t.Helper()
	log := &[]string{}
	return &Manager{Run: fakeCron(initial, log), LogDir: t.TempDir(), Now: func() time.Time { return time.Unix(1700000000, 0) }}, log
}

const initialCrontab = `# taoops: nightly-backup
30 2 * * * /usr/local/bin/backup.sh
`

func TestListParsesJobs(t *testing.T) {
	m, _ := newMgr(t, initialCrontab)
	jobs, err := m.List()
	if err != nil {
		t.Fatalf("List: %v", err)
	}
	if len(jobs) != 1 || jobs[0].Name != "nightly-backup" || jobs[0].Schedule != "30 2 * * *" || jobs[0].Command != "/usr/local/bin/backup.sh" {
		t.Fatalf("jobs = %+v", jobs)
	}
}

func TestListEmptyCrontab(t *testing.T) {
	m, _ := newMgr(t, "")
	jobs, err := m.List()
	if err != nil {
		t.Fatalf("List: %v", err)
	}
	if len(jobs) != 0 {
		t.Fatalf("expected no jobs, got %+v", jobs)
	}
}

func TestAddAppendsJob(t *testing.T) {
	m, _ := newMgr(t, "")
	if err := m.Add("logrotate", "0 3 * * *", "/usr/bin/logrotate /etc/logrotate.conf"); err != nil {
		t.Fatalf("Add: %v", err)
	}
	jobs, _ := m.List()
	if len(jobs) != 1 || jobs[0].Name != "logrotate" {
		t.Fatalf("jobs = %+v", jobs)
	}
}

func TestAddReplacesSameName(t *testing.T) {
	m, _ := newMgr(t, initialCrontab)
	if err := m.Add("nightly-backup", "0 4 * * *", "/usr/local/bin/backup2.sh"); err != nil {
		t.Fatalf("Add: %v", err)
	}
	jobs, _ := m.List()
	if len(jobs) != 1 || jobs[0].Schedule != "0 4 * * *" {
		t.Fatalf("replace failed: %+v", jobs)
	}
}

func TestAddValidation(t *testing.T) {
	m, _ := newMgr(t, "")
	if err := m.Add("", "0 3 * * *", "x"); err == nil {
		t.Fatal("empty name should fail")
	}
	if err := m.Add("j", "", "x"); err == nil {
		t.Fatal("empty schedule should fail")
	}
	if err := m.Add("j", "0 3 * * *", ""); err == nil {
		t.Fatal("empty command should fail")
	}
}

func TestRemoveDeletesJob(t *testing.T) {
	m, _ := newMgr(t, initialCrontab)
	if err := m.Remove("nightly-backup"); err != nil {
		t.Fatalf("Remove: %v", err)
	}
	jobs, _ := m.List()
	if len(jobs) != 0 {
		t.Fatalf("jobs after remove = %+v", jobs)
	}
	if err := m.Remove("nightly-backup"); err == nil {
		t.Fatal("removing missing job should fail")
	}
}

func TestRunNowWritesLogWithStatus(t *testing.T) {
	m, _ := newMgr(t, "")
	logPath, err := m.RunNow("backup", "echo hi")
	if err != nil {
		t.Fatalf("RunNow: %v", err)
	}
	data, _ := os.ReadFile(logPath)
	if !strings.Contains(string(data), "status=success") || !strings.Contains(string(data), "stdout=ran") {
		t.Fatalf("log = %q", string(data))
	}
	files, _ := m.ListLogs("backup")
	if len(files) != 1 {
		t.Fatalf("logs = %v", files)
	}
}

func TestRunNowFailureLogged(t *testing.T) {
	dir := t.TempDir()
	m := &Manager{Run: func(bin string, args []string, stdin string) (string, string, error) {
		if bin == "/bin/sh" {
			return "", "command not found", fmt.Errorf("exit 127")
		}
		return "", "", nil
	}, LogDir: dir, Now: func() time.Time { return time.Unix(1700000000, 0) }}
	logPath, err := m.RunNow("badjob", "definitely-not-a-command")
	if err == nil {
		t.Fatal("failing command should return error")
	}
	data, _ := os.ReadFile(logPath)
	if !strings.Contains(string(data), "status=fail") {
		t.Fatalf("log = %q", string(data))
	}
}

func TestHandlerOps(t *testing.T) {
	m, _ := newMgr(t, initialCrontab)
	h := Handler(m)
	if _, err := h(map[string]any{"op": "bogus"}); err == nil {
		t.Fatal("unknown op should fail")
	}
	if _, err := h(map[string]any{"op": "list"}); err != nil {
		t.Fatalf("list: %v", err)
	}
	if _, err := h(map[string]any{"op": "add", "name": "j1", "schedule": "0 0 * * *", "command": "true"}); err != nil {
		t.Fatalf("add: %v", err)
	}
	if _, err := h(map[string]any{"op": "run", "name": "j1", "command": "true"}); err != nil {
		t.Fatalf("run: %v", err)
	}
	if _, err := h(map[string]any{"op": "logs", "name": "j1"}); err != nil {
		t.Fatalf("logs: %v", err)
	}
	if _, err := h(map[string]any{"op": "remove", "name": "j1"}); err != nil {
		t.Fatalf("remove: %v", err)
	}
}
