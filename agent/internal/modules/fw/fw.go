// Package fw implements FR-A10 (server-level firewall/port management):
// iptables/firewalld rule CRUD with immediate effect, an audit trail of
// every operation and rollback of the last change (PRD: 操作留痕可回滚).
package fw

import (
	"bytes"
	"fmt"
	"os"
	"os/exec"
	"strconv"
	"strings"
	"sync"
	"time"

	"taoops/agent/internal/modules/registry"
)

// Runner executes external commands; tests inject fakes.
type Runner func(bin string, args []string, stdin string) (stdout, stderr string, err error)

// AuditEntry records one firewall change for rollback.
type AuditEntry struct {
	Time     string `json:"time"`
	Action   string `json:"action"` // allow | deny | remove
	Port     int    `json:"port"`
	Proto    string `json:"proto"`
	Rollback string `json:"rollback"` // command line to undo
}

// Manager manages firewall rules through a Runner with an audit log.
type Manager struct {
	Run       Runner
	AuditPath string
	Backend   string // "iptables" or "firewalld"
	mu        sync.Mutex
	Now       func() time.Time
}

// NewManager wires a Manager over iptables with a real audit file.
func NewManager(auditPath string) *Manager {
	return &Manager{Run: runExec, AuditPath: auditPath, Backend: "iptables", Now: time.Now}
}

func runExec(bin string, args []string, stdin string) (string, string, error) {
	cmd := exec.Command(bin, args...)
	var out, errBuf bytes.Buffer
	cmd.Stdout = &out
	cmd.Stderr = &errBuf
	err := cmd.Run()
	return out.String(), errBuf.String(), err
}

// Allow opens a port (input accept rule) and records the audit entry.
func (m *Manager) Allow(port int, proto string) error {
	return m.apply("allow", port, proto)
}

// Deny blocks a port (input drop rule) and records the audit entry.
func (m *Manager) Deny(port int, proto string) error {
	return m.apply("deny", port, proto)
}

// Remove deletes a previously added rule for the port/proto.
func (m *Manager) Remove(port int, proto string) error {
	return m.apply("remove", port, proto)
}

func (m *Manager) apply(action string, port int, proto string) error {
	if port <= 0 || port > 65535 {
		return fmt.Errorf("fw: invalid port %d", port)
	}
	if proto != "tcp" && proto != "udp" {
		return fmt.Errorf("fw: proto must be tcp or udp, got %q", proto)
	}
	m.mu.Lock()
	defer m.mu.Unlock()

	var bin string
	var args []string
	var rollback string
	switch m.Backend {
	case "iptables":
		bin = "iptables"
		switch action {
		case "allow":
			args = []string{"-A", "INPUT", "-p", proto, "--dport", strconv.Itoa(port), "-j", "ACCEPT"}
			rollback = "iptables -D INPUT -p " + proto + " --dport " + strconv.Itoa(port) + " -j ACCEPT"
		case "deny":
			args = []string{"-A", "INPUT", "-p", proto, "--dport", strconv.Itoa(port), "-j", "DROP"}
			rollback = "iptables -D INPUT -p " + proto + " --dport " + strconv.Itoa(port) + " -j DROP"
		case "remove":
			args = []string{"-D", "INPUT", "-p", proto, "--dport", strconv.Itoa(port), "-j", "ACCEPT"}
			rollback = "iptables -A INPUT -p " + proto + " --dport " + strconv.Itoa(port) + " -j ACCEPT"
		}
	case "firewalld":
		bin = "firewall-cmd"
		switch action {
		case "allow":
			args = []string{"--add-port", fmt.Sprintf("%d/%s", port, proto)}
			rollback = "firewall-cmd --remove-port " + fmt.Sprintf("%d/%s", port, proto)
		case "deny":
			return fmt.Errorf("fw: firewalld deny not supported; use remove")
		case "remove":
			args = []string{"--remove-port", fmt.Sprintf("%d/%s", port, proto)}
			rollback = "firewall-cmd --add-port " + fmt.Sprintf("%d/%s", port, proto)
		}
	default:
		return fmt.Errorf("fw: unknown backend %q", m.Backend)
	}
	_, stderr, err := m.Run(bin, args, "")
	if err != nil {
		return fmt.Errorf("fw: %s failed: %s", bin, stderr)
	}
	entry := AuditEntry{
		Time:     m.Now().Format(time.RFC3339),
		Action:   action,
		Port:     port,
		Proto:    proto,
		Rollback: rollback,
	}
	return m.appendAudit(entry)
}

func (m *Manager) appendAudit(e AuditEntry) error {
	f, err := os.OpenFile(m.AuditPath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
	if err != nil {
		return err
	}
	defer f.Close()
	_, err = fmt.Fprintf(f, "%s|%s|%d|%s|%s\n", e.Time, e.Action, e.Port, e.Proto, e.Rollback)
	return err
}

// Audit returns all recorded operations, oldest first.
func (m *Manager) Audit() ([]AuditEntry, error) {
	data, err := os.ReadFile(m.AuditPath)
	if err != nil {
		if os.IsNotExist(err) {
			return nil, nil
		}
		return nil, err
	}
	var out []AuditEntry
	for _, line := range strings.Split(strings.TrimSpace(string(data)), "\n") {
		if line == "" {
			continue
		}
		parts := strings.SplitN(line, "|", 5)
		if len(parts) != 5 {
			continue
		}
		port, _ := strconv.Atoi(parts[2])
		out = append(out, AuditEntry{Time: parts[0], Action: parts[1], Port: port, Proto: parts[3], Rollback: parts[4]})
	}
	return out, nil
}

// RollbackLast undoes the most recent audited operation.
func (m *Manager) RollbackLast() error {
	m.mu.Lock()
	defer m.mu.Unlock()
	entries, err := m.Audit()
	if err != nil {
		return err
	}
	if len(entries) == 0 {
		return fmt.Errorf("fw: nothing to roll back")
	}
	last := entries[len(entries)-1]
	parts := strings.Fields(last.Rollback)
	if len(parts) == 0 {
		return fmt.Errorf("fw: malformed rollback entry")
	}
	_, stderr, err := m.Run(parts[0], parts[1:], "")
	if err != nil {
		return fmt.Errorf("fw: rollback failed: %s", stderr)
	}
	// remove the rolled-back entry from the audit trail
	if err := os.WriteFile(m.AuditPath, []byte(""), 0o644); err != nil {
		return err
	}
	for _, e := range entries[:len(entries)-1] {
		if err := m.appendAudit(e); err != nil {
			return err
		}
	}
	return nil
}

// Handler returns the registry handler "fw.manage".
func Handler(m *Manager) registry.Handler {
	return func(args map[string]any) (registry.Result, error) {
		op, _ := args["op"].(string)
		port, _ := toInt(args["port"])
		proto, _ := args["proto"].(string)
		if proto == "" {
			proto = "tcp"
		}
		switch op {
		case "allow", "deny", "remove":
			var err error
			switch op {
			case "allow":
				err = m.Allow(port, proto)
			case "deny":
				err = m.Deny(port, proto)
			case "remove":
				err = m.Remove(port, proto)
			}
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: op + " ok"}, nil
		case "audit":
			entries, err := m.Audit()
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "ok", Data: map[string]any{"entries": entries}}, nil
		case "rollback":
			if err := m.RollbackLast(); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "rolled back"}, nil
		}
		return registry.Result{ExitCode: 2}, fmt.Errorf("fw: unknown op %q", op)
	}
}

// Register installs the fw handlers into r.
func Register(r *registry.Registry, m *Manager) error {
	return r.Register("fw.manage", Handler(m))
}

func toInt(v any) (int, bool) {
	switch t := v.(type) {
	case int:
		return t, true
	case int64:
		return int(t), true
	case float64:
		return int(t), true
	}
	return 0, false
}
