package db

import (
	"encoding/json"
	"os"
	"strings"
	"testing"
	"time"
)

func replicaRunner(sqlLog *[]string) Runner {
	return func(bin string, args []string, stdin string) (string, string, error) {
		*sqlLog = append(*sqlLog, strings.Join(args, " "))
		if len(args) >= 2 && strings.Contains(args[1], "SHOW SLAVE STATUS") {
			return "Slave_IO_Running: Yes\nSlave_SQL_Running: Yes\nSeconds_Behind_Master: 5\nMaster_Host: 10.0.0.11\nLast_Error: \n", "", nil
		}
		return "", "", nil
	}
}

func TestGeneratePasswordStrength(t *testing.T) {
	pw, err := GeneratePassword(24)
	if err != nil {
		t.Fatal(err)
	}
	if len(pw) != 24 {
		t.Fatalf("want 24 chars, got %d", len(pw))
	}
	if pw == "aaaaaaaaaaaaaaaaaaaaaaaa" || pw == strings.Repeat(pw[0:1], 24) {
		t.Fatalf("password looks constant: %q", pw)
	}
	pw2, _ := GeneratePassword(24)
	if pw == pw2 {
		t.Fatalf("two calls must not collide")
	}
	if _, err := GeneratePassword(4); err == nil {
		t.Fatalf("short password must be rejected")
	}
}

func TestRedact(t *testing.T) {
	in := "op=replica_config master_host=10.0.0.11 master_password=SuperSecret!42 master_user=repl_ab12"
	out := Redact(in)
	if strings.Contains(out, "SuperSecret!42") {
		t.Fatalf("password leaked: %s", out)
	}
	if !strings.Contains(out, "master_password=***") || !strings.Contains(out, "master_host=10.0.0.11") {
		t.Fatalf("unexpected redaction: %s", out)
	}
}

func TestMasterGrantShowsPasswordOnce(t *testing.T) {
	var log []string
	m := &Manager{Run: replicaRunner(&log), BackupDir: t.TempDir(), Now: nowFn}
	out, err := m.MasterGrant("10.0.0.22")
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out, "password=") || strings.Contains(out, "(shown once") == false {
		t.Fatalf("password must be shown once: %s", out)
	}
	// SQL contains the credentials (executed via CLI), but no echo path.
	joined := strings.Join(log, "\n")
	if !strings.Contains(joined, "REPLICATION SLAVE") {
		t.Fatalf("grant SQL missing: %s", joined)
	}
}

func TestReplicaConfigBacksUpMyCnfAndRedacts(t *testing.T) {
	var log []string
	backupDir := t.TempDir()
	// Inject a writable my.cnf candidate so the backup path is exercised.
	cnf := t.TempDir() + "/my.cnf"
	if err := os.WriteFile(cnf, []byte("[mysqld]\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	old := confCandidates
	confCandidates = []string{cnf}
	t.Cleanup(func() { confCandidates = old })

	m := &Manager{Run: replicaRunner(&log), BackupDir: backupDir, Now: nowFn}
	out, err := m.ReplicaConfig("10.0.0.11", "", "repl_ab", "S3cret!!", "binlog.000001", 1540)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(out, "S3cret!!") {
		t.Fatalf("credentials leaked in output: %s", out)
	}
	// Timestamped backup of the previous config landed in BackupDir.
	entries, err := os.ReadDir(backupDir)
	if err != nil || len(entries) == 0 {
		t.Fatalf("my.cnf backup missing: %v", err)
	}
	if !strings.HasSuffix(entries[0].Name(), ".bak") {
		t.Fatalf("backup must carry timestamped .bak suffix: %s", entries[0].Name())
	}
	joined := strings.Join(log, "\n")
	if !strings.Contains(joined, "CHANGE MASTER TO") || !strings.Contains(joined, "MASTER_HOST='10.0.0.11'") {
		t.Fatalf("CHANGE MASTER SQL missing: %s", joined)
	}
}

func TestReplicaStatusParses(t *testing.T) {
	var log []string
	m := &Manager{Run: replicaRunner(&log), BackupDir: t.TempDir(), Now: nowFn}
	out, err := m.ReplicaStatus()
	if err != nil {
		t.Fatal(err)
	}
	var st SlaveStatus
	if err := json.Unmarshal([]byte(out), &st); err != nil {
		t.Fatalf("status not JSON: %v", err)
	}
	if !st.Configured || st.IORunning != "Yes" || st.SQLRunning != "Yes" || st.SecondsBehindMaster != 5 || st.MasterHost != "10.0.0.11" {
		t.Fatalf("unexpected status: %+v", st)
	}
}

func TestReplicaStatusNotConfigured(t *testing.T) {
	m := &Manager{Run: func(bin string, args []string, stdin string) (string, string, error) {
		return "", "", nil
	}, BackupDir: t.TempDir(), Now: nowFn}
	out, err := m.ReplicaStatus()
	if err != nil {
		t.Fatal(err)
	}
	var st SlaveStatus
	if err := json.Unmarshal([]byte(out), &st); err != nil {
		t.Fatal(err)
	}
	if st.Configured {
		t.Fatalf("empty SHOW SLAVE STATUS must be unconfigured: %+v", st)
	}
}

func TestReplicaReset(t *testing.T) {
	var log []string
	m := &Manager{Run: replicaRunner(&log), BackupDir: t.TempDir(), Now: nowFn}
	out, err := m.ReplicaReset()
	if err != nil || out != "replication reset" {
		t.Fatalf("reset failed: %v %s", err, out)
	}
	if !strings.Contains(strings.Join(log, "\n"), "RESET SLAVE ALL") {
		t.Fatalf("RESET SLAVE ALL missing")
	}
}

func TestReplicaConfigValidation(t *testing.T) {
	var log []string
	m := &Manager{Run: replicaRunner(&log), BackupDir: t.TempDir(), Now: nowFn}
	if _, err := m.ReplicaConfig("", "", "u", "p", "", 0); err == nil {
		t.Fatalf("missing master_host must fail")
	}
	if _, err := m.ReplicaConfig("h", "", "", "p", "", 0); err == nil {
		t.Fatalf("missing master_user must fail")
	}
}

func nowFn() time.Time { return time.Now() }
