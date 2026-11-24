package db

import (
	"fmt"
	"strings"
	"testing"
)

func grantFakeRunner(log *[]string) func(string, []string, string) (string, string, error) {
	return func(bin string, args []string, stdin string) (string, string, error) {
		*log = append(*log, args[1])
		if strings.Contains(args[1], "SELECT user, host") {
			return "user\thost\napp_user\t10.0.0.%\n", "", nil
		}
		if strings.HasPrefix(args[1], "SHOW GRANTS") {
			return "GRANT SELECT, INSERT ON `app_db`.* TO 'app_user'@'10.0.0.%'\n", "", nil
		}
		return "Query OK", "", nil
	}
}

func TestGrantListParsesUsers(t *testing.T) {
	var log []string
	m := &Manager{Run: grantFakeRunner(&log), BackupDir: t.TempDir()}
	out, err := m.GrantList()
	if err != nil {
		t.Fatalf("GrantList: %v", err)
	}
	if len(out) != 1 || out[0].User != "app_user" || out[0].Host != "10.0.0.%" {
		t.Fatalf("views = %+v", out)
	}
	if out[0].Database != "app_db" || len(out[0].Privileges) != 2 {
		t.Fatalf("view = %+v", out[0])
	}
}

func TestGrantAddBuildsSQLAndShowsPasswordOnce(t *testing.T) {
	var log []string
	m := &Manager{Run: grantFakeRunner(&log), BackupDir: t.TempDir()}
	out, err := m.GrantAdd("app_db", "web_read", "10.0.0.%", []string{"select", "INSERT"})
	if err != nil {
		t.Fatalf("GrantAdd: %v", err)
	}
	if !strings.Contains(out, "password=") || !strings.Contains(out, "shown once") {
		t.Fatalf("output = %q", out)
	}
	joined := strings.Join(log, "\n")
	if !strings.Contains(joined, "CREATE USER IF NOT EXISTS 'web_read'@'10.0.0.%'") {
		t.Fatalf("sql = %s", joined)
	}
	if !strings.Contains(joined, "GRANT INSERT, SELECT ON `app_db`.*") {
		t.Fatalf("grant sql missing: %s", joined)
	}
	if !strings.Contains(joined, "FLUSH PRIVILEGES") {
		t.Fatal("FLUSH PRIVILEGES missing")
	}
}

func TestGrantAddValidation(t *testing.T) {
	var log []string
	m := &Manager{Run: grantFakeRunner(&log), BackupDir: t.TempDir()}
	cases := []struct {
		db, user, host string
		privs          []string
		want           string
	}{
		{"app;db", "u", "h", []string{"SELECT"}, "invalid characters"}, // injection attempt
		{"app_db", "u'; DROP", "h", []string{"SELECT"}, "invalid characters"},
		{"app_db", "u", "h;--", []string{"SELECT"}, "invalid characters"},
		{"app_db", "u", "h", nil, "at least one privilege"},
		{"app_db", "u", "h", []string{"GRANT OPTION"}, "not grantable"},
		{"app_db", "u", "h", []string{"SHUTDOWN"}, "not grantable"},
	}
	for _, c := range cases {
		if _, err := m.GrantAdd(c.db, c.user, c.host, c.privs); err == nil || !strings.Contains(err.Error(), c.want) {
			t.Fatalf("GrantAdd(%q,%q,%q,%v) err=%v, want %q", c.db, c.user, c.host, c.privs, err, c.want)
		}
	}
	if len(log) != 0 {
		t.Fatal("no SQL should run for rejected inputs")
	}
}

func TestGrantRevokeRequiresConfirm(t *testing.T) {
	var log []string
	m := &Manager{Run: grantFakeRunner(&log), BackupDir: t.TempDir()}
	if _, err := m.GrantRevoke("app_db", "u", "h", false); err == nil || !strings.Contains(err.Error(), "confirm=true") {
		t.Fatalf("err = %v", err)
	}
	if len(log) != 0 {
		t.Fatal("no SQL should run without confirm")
	}
	if _, err := m.GrantRevoke("app_db", "u", "h", true); err != nil {
		t.Fatalf("GrantRevoke confirm: %v", err)
	}
	joined := strings.Join(log, "\n")
	if !strings.Contains(joined, "REVOKE ALL PRIVILEGES ON `app_db`.* FROM 'u'@'h'") {
		t.Fatalf("revoke sql: %s", joined)
	}
	if !strings.Contains(joined, "DROP USER IF EXISTS 'u'@'h'") {
		t.Fatal("DROP USER missing")
	}
}

func TestGrantRevokeWildcardDatabase(t *testing.T) {
	var log []string
	m := &Manager{Run: grantFakeRunner(&log), BackupDir: t.TempDir()}
	if _, err := m.GrantRevoke("*", "u", "h", true); err != nil {
		t.Fatalf("wildcard revoke: %v", err)
	}
	if !strings.Contains(strings.Join(log, "\n"), "ON `*`.*") {
		t.Fatalf("sql: %s", strings.Join(log, "\n"))
	}
}

func TestGrantJSONEmpty(t *testing.T) {
	if grantJSON(nil) != "[]" {
		t.Fatal("empty views should marshal to []")
	}
}

func TestGrantHandlerOps(t *testing.T) {
	var log []string
	m := &Manager{Run: grantFakeRunner(&log), BackupDir: t.TempDir()}
	h := Handler(m)
	res, err := h(map[string]any{"op": "db_grant_list"})
	if err != nil || !strings.Contains(res.Output, "app_user") {
		t.Fatalf("db_grant_list: %q err=%v", res.Output, err)
	}
	if _, err := h(map[string]any{"op": "db_grant_add", "database": "app_db", "user": "u1", "host": "10.%", "privileges": []any{"SELECT"}}); err != nil {
		t.Fatalf("db_grant_add: %v", err)
	}
	if _, err := h(map[string]any{"op": "db_grant_revoke", "database": "app_db", "user": "u1", "host": "10.%"}); err == nil {
		t.Fatal("revoke without confirm should fail")
	}
	if _, err := h(map[string]any{"op": "db_grant_revoke", "database": "app_db", "user": "u1", "host": "10.%", "confirm": "true"}); err != nil {
		t.Fatalf("db_grant_revoke confirm: %v", err)
	}
	fmt.Sprint() // keep fmt import
}
