package db

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func fakeRunner(log *[]string, failBin string) Runner {
	return func(bin string, args []string, stdin string) (string, string, error) {
		*log = append(*log, bin+" "+strings.Join(args, " "))
		if bin == failBin {
			return "", "boom", fmt.Errorf("exit 1")
		}
		switch bin {
		case "mysqldump", "pg_dump":
			return "-- dump of database\n", "", nil
		}
		return "", "", nil
	}
}

func newMgr(t *testing.T, failBin string) (*Manager, *[]string) {
	t.Helper()
	dir := t.TempDir()
	log := &[]string{}
	return &Manager{Run: fakeRunner(log, failBin), BackupDir: filepath.Join(dir, "bk"), Now: func() time.Time { return time.Unix(1700000000, 0) }}, log
}

func TestCreateMySQL(t *testing.T) {
	m, log := newMgr(t, "")
	if err := m.Create("mysql", "shop"); err != nil {
		t.Fatalf("Create: %v", err)
	}
	if len(*log) != 1 || !strings.Contains((*log)[0], "CREATE DATABASE IF NOT EXISTS `shop`") {
		t.Fatalf("unexpected calls: %v", *log)
	}
}

func TestCreatePostgreSQL(t *testing.T) {
	m, log := newMgr(t, "")
	if err := m.Create("postgresql", "shop"); err != nil {
		t.Fatalf("Create: %v", err)
	}
	if !strings.Contains((*log)[0], `CREATE DATABASE "shop"`) {
		t.Fatalf("unexpected call: %v", *log)
	}
}

func TestCreateValidation(t *testing.T) {
	m, _ := newMgr(t, "")
	if err := m.Create("oracle", "x"); err == nil {
		t.Fatal("unsupported engine should fail")
	}
	if err := m.Create("mysql", ""); err == nil {
		t.Fatal("empty name should fail")
	}
}

func TestDropMySQL(t *testing.T) {
	m, log := newMgr(t, "")
	if err := m.Drop("mysql", "shop"); err != nil {
		t.Fatalf("Drop: %v", err)
	}
	if !strings.Contains((*log)[0], "DROP DATABASE IF EXISTS `shop`") {
		t.Fatalf("unexpected call: %v", *log)
	}
}

func TestBackupWritesFile(t *testing.T) {
	m, _ := newMgr(t, "")
	file, err := m.Backup("mysql", "shop")
	if err != nil {
		t.Fatalf("Backup: %v", err)
	}
	data, err := os.ReadFile(file)
	if err != nil {
		t.Fatalf("read dump: %v", err)
	}
	if !strings.Contains(string(data), "dump of database") {
		t.Fatalf("dump content wrong: %q", string(data))
	}
	files, _ := m.ListBackups("mysql")
	if len(files) != 1 {
		t.Fatalf("backups = %v", files)
	}
}

func TestBackupEmptyDumpFails(t *testing.T) {
	m, _ := newMgr(t, "")
	m.Run = func(bin string, args []string, stdin string) (string, string, error) { return "", "", nil }
	if _, err := m.Backup("mysql", "shop"); err == nil {
		t.Fatal("empty dump should fail")
	}
}

func TestBackupToolFailure(t *testing.T) {
	m, _ := newMgr(t, "mysqldump")
	if _, err := m.Backup("mysql", "shop"); err == nil {
		t.Fatal("tool failure should propagate")
	}
}

func TestRestoreSendsStdin(t *testing.T) {
	dir := t.TempDir()
	dump := filepath.Join(dir, "d.sql")
	if err := os.WriteFile(dump, []byte("INSERT 1;"), 0o600); err != nil {
		t.Fatal(err)
	}
	var gotStdin string
	m := &Manager{BackupDir: dir, Run: func(bin string, args []string, stdin string) (string, string, error) {
		gotStdin = stdin
		return "", "", nil
	}}
	if err := m.Restore("mysql", "shop", dump); err != nil {
		t.Fatalf("Restore: %v", err)
	}
	if gotStdin != "INSERT 1;" {
		t.Fatalf("restore stdin = %q", gotStdin)
	}
}

func TestRestoreMissingDumpFails(t *testing.T) {
	m, _ := newMgr(t, "")
	if err := m.Restore("mysql", "shop", "/nonexistent.sql"); err == nil {
		t.Fatal("missing dump should fail")
	}
}

func TestHandlerOps(t *testing.T) {
	m, _ := newMgr(t, "")
	h := Handler(m)
	if _, err := h(map[string]any{"op": "bogus"}); err == nil {
		t.Fatal("unknown op should fail")
	}
	if _, err := h(map[string]any{"op": "create", "engine": "mysql", "name": "x"}); err != nil {
		t.Fatalf("create: %v", err)
	}
	res, err := h(map[string]any{"op": "backup", "engine": "mysql", "name": "x"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("backup: %v", err)
	}
	if _, err := h(map[string]any{"op": "backups", "engine": "mysql"}); err != nil {
		t.Fatalf("backups: %v", err)
	}
	if _, err := h(map[string]any{"op": "drop", "engine": "mysql", "name": "x"}); err != nil {
		t.Fatalf("drop: %v", err)
	}
}

// 下发桥仅回传 Output：backups 结构化结果须落 Output JSON（FR-A17 约定）。
func TestHandlerBackupsOutputJSON(t *testing.T) {
	m, _ := newMgr(t, "")
	h := Handler(m)
	if _, err := h(map[string]any{"op": "create", "engine": "mysql", "name": "x"}); err != nil {
		t.Fatalf("create: %v", err)
	}
	if _, err := h(map[string]any{"op": "backup", "engine": "mysql", "name": "x"}); err != nil {
		t.Fatalf("backup: %v", err)
	}
	res, err := h(map[string]any{"op": "backups", "engine": "mysql"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("backups: %v %v", res, err)
	}
	var parsed []string
	if err := json.Unmarshal([]byte(res.Output), &parsed); err != nil {
		t.Fatalf("backups output must be JSON array, got %q: %v", res.Output, err)
	}
	if len(parsed) != 1 {
		t.Fatalf("backups output mismatch: %v", parsed)
	}
}
