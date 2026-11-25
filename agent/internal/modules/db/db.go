// Package db implements FR-A3 (database management): MySQL/PostgreSQL
// create/drop database, backup and restore. External tools (mysql,
// mysqldump, psql, pg_dump) are invoked through a Runner interface so
// tests can fake them; backup files land in a managed backup directory.
package db

import (
	"bytes"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"taoops/agent/internal/modules/registry"
)

// Runner executes external commands; stdin carries SQL input, tests inject fakes.
type Runner func(bin string, args []string, stdin string) (stdout, stderr string, err error)

func runExec(bin string, args []string, stdin string) (string, string, error) {
	cmd := exec.Command(bin, args...)
	if stdin != "" {
		cmd.Stdin = strings.NewReader(stdin)
	}
	var out, errBuf bytes.Buffer
	cmd.Stdout = &out
	cmd.Stderr = &errBuf
	err := cmd.Run()
	return out.String(), errBuf.String(), err
}

// Manager manages databases via CLI tools.
type Manager struct {
	Run       Runner
	BackupDir string
	Now       func() time.Time
}

// NewManager wires a Manager with the real command runner.
func NewManager(backupDir string) *Manager {
	return &Manager{Run: runExec, BackupDir: backupDir, Now: time.Now}
}

func checkEngine(engine string) error {
	if engine != "mysql" && engine != "postgresql" {
		return fmt.Errorf("db: unsupported engine %q", engine)
	}
	return nil
}

// Create creates a database of the given engine.
func (m *Manager) Create(engine, name string) error {
	if err := checkEngine(engine); err != nil {
		return err
	}
	if name == "" {
		return fmt.Errorf("db: database name required")
	}
	if engine == "mysql" {
		_, stderr, err := m.Run("mysql", []string{"--execute",
			fmt.Sprintf("CREATE DATABASE IF NOT EXISTS `%s` CHARACTER SET utf8mb4", name)}, "")
		if err != nil {
			return fmt.Errorf("db: mysql failed: %s", stderr)
		}
		return nil
	}
	_, stderr, err := m.Run("psql", []string{"-c", fmt.Sprintf("CREATE DATABASE %q", name)}, "")
	if err != nil {
		return fmt.Errorf("db: psql failed: %s", stderr)
	}
	return nil
}

// Drop removes a database.
func (m *Manager) Drop(engine, name string) error {
	if err := checkEngine(engine); err != nil {
		return err
	}
	if engine == "mysql" {
		_, stderr, err := m.Run("mysql", []string{"--execute",
			fmt.Sprintf("DROP DATABASE IF EXISTS `%s`", name)}, "")
		if err != nil {
			return fmt.Errorf("db: mysql failed: %s", stderr)
		}
		return nil
	}
	_, stderr, err := m.Run("psql", []string{"-c", fmt.Sprintf("DROP DATABASE IF EXISTS %q", name)}, "")
	if err != nil {
		return fmt.Errorf("db: psql failed: %s", stderr)
	}
	return nil
}

// Backup dumps a database into BackupDir with a timestamped name and
// returns the file path.
func (m *Manager) Backup(engine, name string) (string, error) {
	if err := checkEngine(engine); err != nil {
		return "", err
	}
	if err := os.MkdirAll(m.BackupDir, 0o755); err != nil {
		return "", err
	}
	stamp := m.Now().Format("20060102-150405")
	file := filepath.Join(m.BackupDir, fmt.Sprintf("%s-%s-%s.sql", engine, sanitize(name), stamp))
	var bin string
	var args []string
	if engine == "mysql" {
		bin, args = "mysqldump", []string{"--databases", name}
	} else {
		bin, args = "pg_dump", []string{name}
	}
	stdout, stderr, err := m.Run(bin, args, "")
	if err != nil {
		return "", fmt.Errorf("db: %s failed: %s", bin, stderr)
	}
	if stdout == "" {
		return "", fmt.Errorf("db: %s produced empty dump", bin)
	}
	if err := os.WriteFile(file, []byte(stdout), 0o600); err != nil {
		return "", err
	}
	return file, nil
}

// Restore loads a dump file back into a database via the engine CLI.
func (m *Manager) Restore(engine, name, dumpPath string) error {
	if err := checkEngine(engine); err != nil {
		return err
	}
	data, err := os.ReadFile(dumpPath)
	if err != nil {
		return fmt.Errorf("db: cannot read dump: %w", err)
	}
	if engine == "mysql" {
		if _, stderr, err := m.Run("mysql", []string{name}, string(data)); err != nil {
			return fmt.Errorf("db: mysql restore failed: %s", stderr)
		}
		return nil
	}
	if _, stderr, err := m.Run("psql", []string{name}, string(data)); err != nil {
		return fmt.Errorf("db: psql restore failed: %s", stderr)
	}
	return nil
}

// ListBackups returns dump files for an engine, newest first.
func (m *Manager) ListBackups(engine string) ([]string, error) {
	entries, err := os.ReadDir(m.BackupDir)
	if err != nil {
		if os.IsNotExist(err) {
			return nil, nil
		}
		return nil, err
	}
	prefix := engine + "-"
	var out []string
	for _, e := range entries {
		if strings.HasPrefix(e.Name(), prefix) && strings.HasSuffix(e.Name(), ".sql") {
			out = append(out, e.Name())
		}
	}
	sort.Sort(sort.Reverse(sort.StringSlice(out)))
	return out, nil
}

func sanitize(s string) string {
	var b strings.Builder
	for _, r := range s {
		switch {
		case r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z', r >= '0' && r <= '9', r == '-', r == '_':
			b.WriteRune(r)
		default:
			b.WriteRune('-')
		}
	}
	return b.String()
}

// Handler returns the registry handler "db.manage".
// args: op = create|drop|backup|restore|backups, engine, name, dump.
func Handler(m *Manager) registry.Handler {
	return func(args map[string]any) (registry.Result, error) {
		op, _ := args["op"].(string)
		engine, _ := args["engine"].(string)
		name, _ := args["name"].(string)
		switch op {
		case "create":
			if err := m.Create(engine, name); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "created " + name}, nil
		case "drop":
			if err := m.Drop(engine, name); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "dropped " + name}, nil
		case "backup":
			file, err := m.Backup(engine, name)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: file, Data: map[string]any{"file": file}}, nil
		case "restore":
			dump, _ := args["dump"].(string)
			if err := m.Restore(engine, name, dump); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "restored"}, nil
		case "backups":
			files, err := m.ListBackups(engine)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "ok", Data: map[string]any{"backups": files}}, nil
		// FR-A15 replication ops (contract §13); MySQL only.
		case "master_grant":
			replicaHost, _ := args["replica_host"].(string)
			out, err := m.MasterGrant(replicaHost)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: out}, nil
		case "replica_config":
			masterHost, _ := args["master_host"].(string)
			masterPort, _ := args["master_port"].(string)
			masterUser, _ := args["master_user"].(string)
			masterPassword, _ := args["master_password"].(string)
			logFile, _ := args["master_log_file"].(string)
			logPos := toInt(args["master_log_pos"])
			out, err := m.ReplicaConfig(masterHost, masterPort, masterUser, masterPassword, logFile, logPos)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: out}, nil
		case "replica_status":
			out, err := m.ReplicaStatus()
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: out}, nil
		case "replica_reset":
			out, err := m.ReplicaReset()
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: out}, nil
		// FR-A27 remote-access grant ops; MySQL only.
		case "db_grant_list":
			out, err := m.GrantList()
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: grantJSON(out)}, nil
		case "db_grant_add":
			database, _ := args["database"].(string)
			user, _ := args["user"].(string)
			host, _ := args["host"].(string)
			privs, _ := args["privileges"].([]any)
			ps := make([]string, 0, len(privs))
			for _, p := range privs {
				if s, ok := p.(string); ok {
					ps = append(ps, s)
				}
			}
			out, err := m.GrantAdd(database, user, host, ps)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: out}, nil
		case "db_grant_revoke":
			database, _ := args["database"].(string)
			user, _ := args["user"].(string)
			host, _ := args["host"].(string)
			confirm, _ := args["confirm"].(string)
			if confirm == "" {
				if b, ok := args["confirm"].(bool); ok && b {
					confirm = "true"
				}
			}
			out, err := m.GrantRevoke(database, user, host, confirm == "true")
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: out}, nil
		}
		return registry.Result{ExitCode: 2}, fmt.Errorf("db: unknown op %q", op)
	}
}

// Register installs the db handlers into r.
func Register(r *registry.Registry, m *Manager) error {
	return r.Register("db.manage", Handler(m))
}
