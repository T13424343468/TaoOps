package secguard

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// 断言清单正源：docs/breakpoint-m3-security.md A6。

var fixedNow = time.Date(2026, 11, 22, 11, 0, 0, 0, time.UTC)

func syslogLine(t time.Time, ip string) string {
	return t.Format("Jan 2 15:04:05") + " host sshd[123]: Failed password for root from " + ip + " port 22 ssh2"
}

func TestParseFailures_SyslogAndRFC3339_WithinWindow(t *testing.T) {
	lines := []string{
		syslogLine(fixedNow.Add(-1*time.Minute), "1.2.3.4"),                       // in window
		fixedNow.Format(time.RFC3339) + " host sshd[9]: Invalid user admin from 5.6.7.8", // in window
		syslogLine(fixedNow.Add(-2*time.Hour), "9.9.9.9"),                         // outside window
		"Nov 22 10:00:00 host sshd[1]: Accepted password for root from 1.2.3.4",  // not a failure
		"",                                                                        // blank
	}
	failures, _ := ParseFailures(lines, fixedNow, 10*time.Minute)
	if len(failures) != 2 {
		t.Fatalf("expect 2 failures in window, got %d", len(failures))
	}
	if failures[0].IP != "1.2.3.4" || failures[1].IP != "5.6.7.8" {
		t.Fatalf("unexpected ips: %v", failures)
	}
}

func TestParseFailures_YearBoundarySyslog(t *testing.T) {
	// Jan 1 stamp seen in November must resolve to previous Jan (not future).
	jan := time.Date(2026, 1, 1, 3, 0, 0, 0, time.UTC)
	lines := []string{syslogLine(jan, "1.2.3.4")}
	failures, skipped := ParseFailures(lines, fixedNow, 24*time.Hour)
	if len(failures) != 0 || skipped != 0 {
		t.Fatalf("old failure must fall outside window, got %d failures %d skipped", len(failures), skipped)
	}
}

func TestScan_LocksAtThreshold_WritesEventOnce(t *testing.T) {
	dir := t.TempDir()
	logPath := filepath.Join(dir, "auth.log")
	var b strings.Builder
	// 造数全部落在 Now 之前（窗口 [Now-10m, Now]），i 越大越接近 Now。
	for i := 0; i < 5; i++ {
		b.WriteString(syslogLine(fixedNow.Add(time.Duration(i-5)*time.Second), "203.0.113.9") + "\n")
	}
	b.WriteString(syslogLine(fixedNow, "198.51.100.1") + "\n") // below threshold
	if err := os.WriteFile(logPath, []byte(b.String()), 0o600); err != nil {
		t.Fatal(err)
	}
	m := &Manager{
		Cfg: Config{
			LogPath:      logPath,
			EventLogPath: filepath.Join(dir, "events.log"),
			StatePath:    filepath.Join(dir, "state.json"),
			Window:       10 * time.Minute,
			Threshold:    5,
		},
		Now: func() time.Time { return fixedNow },
	}

	s1, err := m.Scan()
	if err != nil {
		t.Fatal(err)
	}
	if s1.EventsWritten != 1 || len(s1.Lockouts) != 1 || s1.Lockouts[0].IP != "203.0.113.9" {
		t.Fatalf("expect one lockout for 203.0.113.9, got %+v", s1)
	}

	// Rescan: same offender must not produce a duplicate event (A6 dedupe).
	s2, err := m.Scan()
	if err != nil {
		t.Fatal(err)
	}
	if s2.EventsWritten != 0 || len(s2.Lockouts) != 0 {
		t.Fatalf("rescan must dedupe lockouts, got %+v", s2)
	}

	events, err := os.ReadFile(filepath.Join(dir, "events.log"))
	if err != nil {
		t.Fatal(err)
	}
	got := strings.Count(string(events), `"event":"ssh_lockout"`)
	if got != 1 {
		t.Fatalf("expect exactly 1 TAOOPS_SEC event line, got %d", got)
	}
	if !strings.Contains(string(events), `"ip":"203.0.113.9"`) {
		t.Fatalf("event must carry offender ip, got %s", events)
	}
}

func TestScan_WhitelistNeverLocked(t *testing.T) {
	dir := t.TempDir()
	logPath := filepath.Join(dir, "auth.log")
	var b strings.Builder
	for i := 0; i < 7; i++ {
		b.WriteString(syslogLine(fixedNow.Add(time.Duration(i-8)*time.Second), "203.0.113.9") + "\n")
	}
	if err := os.WriteFile(logPath, []byte(b.String()), 0o600); err != nil {
		t.Fatal(err)
	}
	m := &Manager{
		Cfg: Config{
			LogPath:      logPath,
			EventLogPath: filepath.Join(dir, "events.log"),
			StatePath:    filepath.Join(dir, "state.json"),
			Window:       10 * time.Minute,
			Threshold:    5,
			Whitelist:    []string{"203.0.113.9"},
		},
		Now: func() time.Time { return fixedNow },
	}
	s, err := m.Scan()
	if err != nil {
		t.Fatal(err)
	}
	if len(s.Lockouts) != 0 || s.EventsWritten != 0 {
		t.Fatalf("whitelisted ip must never lock, got %+v", s)
	}
}

func TestLockouts_ListsActiveState(t *testing.T) {
	dir := t.TempDir()
	m := &Manager{
		Cfg: Config{
			LogPath:      filepath.Join(dir, "auth.log"),
			EventLogPath: filepath.Join(dir, "events.log"),
			StatePath:    filepath.Join(dir, "state.json"),
			Window:       10 * time.Minute,
			Threshold:    5,
		},
		Now: func() time.Time { return fixedNow },
	}
	// 写超阈值日志后 Scan 落锁定状态。
	logPath := filepath.Join(dir, "auth.log")
	var b strings.Builder
	for i := 0; i < 5; i++ {
		b.WriteString(syslogLine(fixedNow.Add(time.Duration(i-6)*time.Second), "203.0.113.9") + "\n")
	}
	if err := os.WriteFile(logPath, []byte(b.String()), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := m.Scan(); err != nil {
		t.Fatal(err)
	}
	lockouts, err := m.Lockouts()
	if err != nil {
		t.Fatal(err)
	}
	if len(lockouts) != 1 || lockouts[0].IP != "203.0.113.9" {
		t.Fatalf("expect 1 active lockout, got %+v", lockouts)
	}
}
