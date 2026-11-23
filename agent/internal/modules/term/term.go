// Package term implements FR-A5 (web terminal) agent side: session
// create/close/exec with reconnect tokens. The P99 <= 200ms latency
// budget is a transport-level property; this module keeps per-command
// overhead minimal (single exec, no polling) so the budget is reserved
// for the gRPC/WebSocket layer.
package term

import (
	"bytes"
	"fmt"
	"os/exec"
	"regexp"
	"sync"
	"time"

	"taoops/agent/internal/modules/registry"
)

// containerNameRe mirrors the FR-A11 whitelist: ^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$
var containerNameRe = regexp.MustCompile(`^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$`)

// Session is one terminal session with a reconnect token.
type Session struct {
	ID      string
	Token   string
	Created time.Time
	// Container pins exec to a container (FR-A11 attach_container); empty = host shell.
	Container string
	closed    bool
}

// Manager tracks terminal sessions.
type Manager struct {
	mu       sync.Mutex
	sessions map[string]*Session
	Now      func() time.Time
	nextID   int
}

// NewManager returns an empty session manager.
func NewManager() *Manager {
	return &Manager{sessions: make(map[string]*Session), Now: time.Now}
}

// Create opens a session and returns it with a reconnect token.
func (m *Manager) Create() *Session {
	return m.CreateInContainer("")
}

// CreateInContainer opens a session optionally bound to a container
// (FR-A11: op=attach_container); session routing stays unchanged.
func (m *Manager) CreateInContainer(container string) *Session {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.nextID++
	s := &Session{
		ID:        fmt.Sprintf("term-%04d", m.nextID),
		Token:     fmt.Sprintf("tok-%d-%d", m.Now().UnixNano(), m.nextID),
		Created:   m.Now(),
		Container: container,
	}
	m.sessions[s.ID] = s
	return s
}

// Reconnect validates a token and reattaches to the session (PRD:
// 会话断线可重连). A wrong token never reattaches.
func (m *Manager) Reconnect(id, token string) (*Session, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	s, ok := m.sessions[id]
	if !ok {
		return nil, fmt.Errorf("term: session %q not found", id)
	}
	if s.closed {
		return nil, fmt.Errorf("term: session %q closed", id)
	}
	if s.Token != token {
		return nil, fmt.Errorf("term: invalid token for session %q", id)
	}
	return s, nil
}

// Close terminates a session; further reconnects fail.
func (m *Manager) Close(id string) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	s, ok := m.sessions[id]
	if !ok {
		return fmt.Errorf("term: session %q not found", id)
	}
	s.closed = true
	delete(m.sessions, id)
	return nil
}

// Exec runs a command inside a session and returns combined output.
// Container-bound sessions exec through docker (FR-A11).
func (m *Manager) Exec(id string, command string) (string, int, error) {
	s, err := m.Reconnect(id, m.tokenOf(id))
	if err != nil {
		return "", 1, err
	}
	var cmd *exec.Cmd
	if s.Container != "" {
		if !containerNameRe.MatchString(s.Container) {
			return "", 1, fmt.Errorf("term: invalid container name %q", s.Container)
		}
		cmd = exec.Command("docker", "--host", "unix:///var/run/docker.sock",
			"exec", s.Container, "sh", "-c", command)
	} else {
		cmd = exec.Command("/bin/sh", "-c", command)
	}
	var out bytes.Buffer
	cmd.Stdout = &out
	cmd.Stderr = &out
	code := 0
	if err := cmd.Run(); err != nil {
		if ee, ok := err.(*exec.ExitError); ok {
			code = ee.ExitCode()
		} else {
			return out.String(), 1, err
		}
	}
	_ = s
	return out.String(), code, nil
}

func (m *Manager) tokenOf(id string) string {
	m.mu.Lock()
	defer m.mu.Unlock()
	if s, ok := m.sessions[id]; ok {
		return s.Token
	}
	return ""
}

// Handler returns the registry handler "term.manage".
func Handler(m *Manager) registry.Handler {
	return func(args map[string]any) (registry.Result, error) {
		op, _ := args["op"].(string)
		switch op {
		case "create":
			s := m.Create()
			return registry.Result{ExitCode: 0, Output: s.ID, Data: map[string]any{"id": s.ID, "token": s.Token}}, nil
		case "attach_container":
			container, _ := args["container"].(string)
			if !containerNameRe.MatchString(container) {
				return registry.Result{ExitCode: 1}, fmt.Errorf("term: invalid container name %q", container)
			}
			s := m.CreateInContainer(container)
			return registry.Result{ExitCode: 0, Output: s.ID, Data: map[string]any{"id": s.ID, "token": s.Token, "container": s.Container}}, nil
		case "reconnect":
			id, _ := args["id"].(string)
			token, _ := args["token"].(string)
			if _, err := m.Reconnect(id, token); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "reconnected " + id}, nil
		case "close":
			id, _ := args["id"].(string)
			if err := m.Close(id); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "closed " + id}, nil
		case "exec":
			id, _ := args["id"].(string)
			command, _ := args["command"].(string)
			out, code, err := m.Exec(id, command)
			if err != nil {
				return registry.Result{ExitCode: 1, Output: out}, err
			}
			return registry.Result{ExitCode: code, Output: out}, nil
		}
		return registry.Result{ExitCode: 2}, fmt.Errorf("term: unknown op %q", op)
	}
}

// Register installs the term handlers into r.
func Register(r *registry.Registry, m *Manager) error {
	return r.Register("term.manage", Handler(m))
}
