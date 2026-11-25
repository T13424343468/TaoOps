package db

// FR-A27 MySQL remote-access grant management: db_grant_list /
// db_grant_add / db_grant_revoke (PRD v1.3). Pure SQL operations — no
// my.cnf backup needed. Passwords are generated with crypto/rand and
// shown exactly once (same discipline as FR-A15); identifiers are
// strictly validated before being embedded in SQL.

import (
	"encoding/json"
	"fmt"
	"sort"
	"strings"
)

// grantablePrivileges is the whitelist of privileges the panel may grant.
var grantablePrivileges = []string{
	"SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "INDEX", "ALTER", "ALL PRIVILEGES",
}

// GrantView is one row of the grant listing.
type GrantView struct {
	User       string   `json:"user"`
	Host       string   `json:"host"`
	Database   string   `json:"database"`
	Privileges []string `json:"privileges"`
}

// validateIdent allows MySQL identifier characters only (letters, digits,
// underscore); anything else is rejected outright (no sanitizing — the
// value either is a safe identifier or the op fails).
func validateIdent(kind, v string) error {
	if v == "" {
		return fmt.Errorf("db: %s is required", kind)
	}
	if len(v) > 64 {
		return fmt.Errorf("db: %s too long", kind)
	}
	for _, r := range v {
		switch {
		case r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z', r >= '0' && r <= '9', r == '_':
		case kind == "host" && (r == '.' || r == '%' || r == '-'):
		default:
			return fmt.Errorf("db: %s contains invalid characters", kind)
		}
	}
	return nil
}

// validatePrivileges checks the privilege list against the whitelist.
func validatePrivileges(privs []string) ([]string, error) {
	if len(privs) == 0 {
		return nil, fmt.Errorf("db: at least one privilege is required")
	}
	allowed := map[string]bool{}
	for _, p := range grantablePrivileges {
		allowed[p] = true
	}
	out := make([]string, 0, len(privs))
	seen := map[string]bool{}
	for _, p := range privs {
		u := strings.ToUpper(strings.TrimSpace(p))
		if !allowed[u] {
			return nil, fmt.Errorf("db: privilege %q is not grantable", p)
		}
		if !seen[u] {
			seen[u] = true
			out = append(out, u)
		}
	}
	sort.Strings(out)
	return out, nil
}

// GrantList parses SHOW GRANTS output for non-root accounts into views.
func (m *Manager) GrantList() ([]GrantView, error) {
	stdout, stderr, err := m.Run("mysql", []string{"-e", "SELECT user, host FROM mysql.user WHERE user NOT IN ('root','mysql.sys','mysql.session','mysql.infoschema','debian-sys-maint');"}, "")
	if err != nil {
		return nil, fmt.Errorf("db: db_grant_list: %s", Redact(strings.TrimSpace(stderr)))
	}
	var out []GrantView
	for _, line := range strings.Split(strings.TrimSpace(stdout), "\n") {
		fields := strings.Split(strings.TrimSpace(line), "\t")
		if len(fields) < 2 || fields[0] == "user" {
			continue
		}
		user, host := fields[0], fields[1]
		gOut, _, gErr := m.Run("mysql", []string{"-e", fmt.Sprintf("SHOW GRANTS FOR '%s'@'%s';", user, host)}, "")
		if gErr != nil {
			continue
		}
		var privs []string
		var dbs []string
		for _, gl := range strings.Split(gOut, "\n") {
			gl = strings.TrimSpace(gl)
			if !strings.HasPrefix(gl, "GRANT ") {
				continue
			}
			privPart := strings.TrimPrefix(gl, "GRANT ")
			if i := strings.Index(privPart, " ON "); i >= 0 {
				privPart = privPart[:i]
			}
			for _, p := range strings.Split(privPart, ", ") {
				p = strings.TrimSpace(p)
				if p == "" {
					continue
				}
				privs = append(privs, p)
			}
			if i := strings.Index(gl, " ON "); i >= 0 {
				rest := gl[i+4:]
				if j := strings.Index(rest, " TO "); j >= 0 {
					d := strings.TrimSpace(rest[:j])
					d = strings.TrimSuffix(d, ".*")
					d = strings.ReplaceAll(d, "`", "")
					dbs = append(dbs, d)
				}
			}
		}
		db := "*"
		if len(dbs) > 0 {
			db = dbs[0]
		}
		out = append(out, GrantView{User: user, Host: host, Database: db, Privileges: privs})
	}
	return out, nil
}

// GrantAdd creates an account (if missing) and grants the whitelisted
// privileges on the database. The generated password is returned exactly
// once and never persisted.
func (m *Manager) GrantAdd(database, user, host string, privs []string) (string, error) {
	if err := validateIdent("database", database); err != nil {
		return "", err
	}
	if err := validateIdent("user", user); err != nil {
		return "", err
	}
	if err := validateIdent("host", host); err != nil {
		return "", err
	}
	privs, err := validatePrivileges(privs)
	if err != nil {
		return "", err
	}
	pw, err := GeneratePassword(24)
	if err != nil {
		return "", err
	}
	sql := fmt.Sprintf("CREATE USER IF NOT EXISTS '%s'@'%s' IDENTIFIED BY '%s'; GRANT %s ON `%s`.* TO '%s'@'%s'; FLUSH PRIVILEGES;",
		user, host, pw, strings.Join(privs, ", "), database, user, host)
	if _, stderr, err := m.Run("mysql", []string{"-e", sql}, ""); err != nil {
		return "", fmt.Errorf("db: db_grant_add: %s", Redact(strings.TrimSpace(stderr)))
	}
	return fmt.Sprintf("granted %s on %s to %s@%s password=%s (shown once, store it now)",
		strings.Join(privs, ","), database, user, host, pw), nil
}

// GrantRevoke revokes all privileges of an account on a database and
// drops the account. confirm=true is required (PRD: 回收需确认).
func (m *Manager) GrantRevoke(database, user, host string, confirm bool) (string, error) {
	if !confirm {
		return "", fmt.Errorf("db: db_grant_revoke requires confirm=true")
	}
	if err := validateIdent("user", user); err != nil {
		return "", err
	}
	if err := validateIdent("host", host); err != nil {
		return "", err
	}
	if database != "*" {
		if err := validateIdent("database", database); err != nil {
			return "", err
		}
	}
	sql := fmt.Sprintf("REVOKE ALL PRIVILEGES ON `%s`.* FROM '%s'@'%s'; DROP USER IF EXISTS '%s'@'%s'; FLUSH PRIVILEGES;",
		database, user, host, user, host)
	if _, stderr, err := m.Run("mysql", []string{"-e", sql}, ""); err != nil {
		return "", fmt.Errorf("db: db_grant_revoke: %s", Redact(strings.TrimSpace(stderr)))
	}
	return fmt.Sprintf("revoked %s from %s@%s and dropped account", database, user, host), nil
}

// grantJSON marshals views compactly for the bridge Output channel.
func grantJSON(vs []GrantView) string {
	if len(vs) == 0 {
		return "[]"
	}
	raw, err := json.Marshal(vs)
	if err != nil {
		return "[]"
	}
	return string(raw)
}
