// Package ftp implements FR-A8 (FTP management): account add/remove,
// directory authorization and connection testing (PRD v1.1 冻结口径).
// Accounts persist as JSON (TAOOPS_CONF_DIR/ftp.json); vsftpd backend
// wiring lands with M3 deployment packaging.
package ftp

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"net"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
	"time"

	"taoops/agent/internal/modules/registry"
)

// Dialer opens a TCP connection for op=test; tests inject fakes.
type Dialer func(host string, port int, timeout time.Duration) (net.Conn, error)

// User is one FTP account with its authorized directories.
type User struct {
	Name         string   `json:"name"`
	PasswordHash string   `json:"password_hash"`
	RootDir      string   `json:"root_dir"`
	AllowedDirs  []string `json:"allowed_dirs"`
	CreatedAt    string   `json:"created_at"`
}

// Manager manages the FTP account store.
type Manager struct {
	StorePath string
	Dial      Dialer
	Now       func() time.Time
	mu        sync.Mutex
}

// NewManager wires a Manager over a JSON store with real TCP dialing.
func NewManager(storePath string) *Manager {
	return &Manager{StorePath: storePath, Dial: dialTCP, Now: time.Now}
}

func dialTCP(host string, port int, timeout time.Duration) (net.Conn, error) {
	return net.DialTimeout("tcp", net.JoinHostPort(host, fmt.Sprint(port)), timeout)
}

var userNameRe = regexp.MustCompile(`^[a-z][a-z0-9_-]{0,31}$`)

func (m *Manager) load() map[string]*User {
	users := map[string]*User{}
	data, err := os.ReadFile(m.StorePath)
	if err != nil {
		return users
	}
	_ = json.Unmarshal(data, &users)
	return users
}

func (m *Manager) save(users map[string]*User) error {
	if err := os.MkdirAll(filepath.Dir(m.StorePath), 0o700); err != nil {
		return err
	}
	data, err := json.MarshalIndent(users, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(m.StorePath, data, 0o600)
}

func hashPassword(pw string) string {
	sum := sha256.Sum256([]byte("taoops-ftp:" + pw))
	return hex.EncodeToString(sum[:])
}

// cleanDir validates an absolute directory without traversal.
func cleanDir(dir string) (string, error) {
	if !filepath.IsAbs(dir) || strings.Contains(dir, "..") {
		return "", fmt.Errorf("ftp: dir must be absolute without '..': %q", dir)
	}
	return filepath.Clean(dir), nil
}

// AddUser creates an account with a root directory.
func (m *Manager) AddUser(name, password, root string) error {
	if !userNameRe.MatchString(name) {
		return fmt.Errorf("ftp: invalid user name %q", name)
	}
	if password == "" {
		return fmt.Errorf("ftp: password must not be empty")
	}
	root, err := cleanDir(root)
	if err != nil {
		return err
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	users := m.load()
	if _, exists := users[name]; exists {
		return fmt.Errorf("ftp: user %q already exists", name)
	}
	users[name] = &User{
		Name:         name,
		PasswordHash: hashPassword(password),
		RootDir:      root,
		AllowedDirs:  []string{root},
		CreatedAt:    m.Now().Format(time.RFC3339),
	}
	return m.save(users)
}

// DeleteUser removes an account.
func (m *Manager) DeleteUser(name string) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	users := m.load()
	if _, exists := users[name]; !exists {
		return fmt.Errorf("ftp: user %q not found", name)
	}
	delete(users, name)
	return m.save(users)
}

// Grant authorizes a directory for a user; Revoke removes it.
func (m *Manager) Grant(name, dir string) error { return m.changeDirs(name, dir, true) }
func (m *Manager) Revoke(name, dir string) error { return m.changeDirs(name, dir, false) }

func (m *Manager) changeDirs(name, dir string, grant bool) error {
	clean, err := cleanDir(dir)
	if err != nil {
		return err
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	users := m.load()
	u, exists := users[name]
	if !exists {
		return fmt.Errorf("ftp: user %q not found", name)
	}
	found := false
	var kept []string
	for _, d := range u.AllowedDirs {
		if d == clean {
			found = true
			continue
		}
		kept = append(kept, d)
	}
	if grant {
		if found {
			return fmt.Errorf("ftp: dir %q already granted to %q", clean, name)
		}
		u.AllowedDirs = append(kept, clean)
	} else {
		if !found {
			return fmt.Errorf("ftp: dir %q not granted to %q", clean, name)
		}
		u.AllowedDirs = kept
	}
	return m.save(users)
}

// List returns all accounts sorted by name.
func (m *Manager) List() ([]*User, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	users := m.load()
	names := make([]string, 0, len(users))
	for n := range users {
		names = append(names, n)
	}
	sortStrings(names)
	out := make([]*User, 0, len(names))
	for _, n := range names {
		out = append(out, users[n])
	}
	return out, nil
}

// TestConn probes an FTP endpoint reachability (TCP connect, 3s timeout).
func (m *Manager) TestConn(host string, port int) error {
	if host == "" {
		host = "127.0.0.1"
	}
	if port == 0 {
		port = 21
	}
	conn, err := m.Dial(host, port, 3*time.Second)
	if err != nil {
		return fmt.Errorf("ftp: connect %s:%d failed: %w", host, port, err)
	}
	_ = conn.Close()
	return nil
}

func sortStrings(s []string) {
	for i := 1; i < len(s); i++ {
		for j := i; j > 0 && s[j] < s[j-1]; j-- {
			s[j], s[j-1] = s[j-1], s[j]
		}
	}
}

// Handler returns the registry handler "ftp.manage".
func Handler(m *Manager) registry.Handler {
	return func(args map[string]any) (registry.Result, error) {
		op, _ := args["op"].(string)
		str := func(k string) string { v, _ := args[k].(string); return v }
		switch op {
		case "adduser":
			if err := m.AddUser(str("user"), str("password"), str("root")); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "user added"}, nil
		case "deluser":
			if err := m.DeleteUser(str("user")); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "user deleted"}, nil
		case "grant", "revoke":
			var err error
			if op == "grant" {
				err = m.Grant(str("user"), str("dir"))
			} else {
				err = m.Revoke(str("user"), str("dir"))
			}
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: op + " ok"}, nil
		case "list":
			users, err := m.List()
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			// 下发桥仅回传 Output：结构化结果按 FR-A17 约定落 Output JSON（Data 保留兼容）。
			data, _ := json.Marshal(users)
			return registry.Result{ExitCode: 0, Output: string(data), Data: map[string]any{"users": json.RawMessage(data)}}, nil
		case "test":
			port := 0
			switch t := args["port"].(type) {
			case string:
				fmt.Sscanf(t, "%d", &port)
			case float64:
				port = int(t)
			}
			if err := m.TestConn(str("host"), port); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "connect ok"}, nil
		}
		return registry.Result{ExitCode: 2}, fmt.Errorf("ftp: unknown op %q", op)
	}
}

// Register installs the ftp handlers into r.
func Register(r *registry.Registry, m *Manager) error {
	return r.Register("ftp.manage", Handler(m))
}
