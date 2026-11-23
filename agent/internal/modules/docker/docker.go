// Package docker implements FR-A11 (Docker container & image management):
// container list/start/stop/restart/logs/terminal and image list/pull/remove,
// all through the local docker.sock only (PRD: Agent 仅经本地套接字访问,
// 禁止 daemon TCP 监听), with an audit trail of every operation.
package docker

import (
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"regexp"
	"strings"
	"sync"
	"time"

	"taoops/agent/internal/modules/registry"
)

// ContainerName matches the PRD whitelist: ^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$
var ContainerName = regexp.MustCompile(`^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$`)

// ImageRef matches the PRD whitelist shape (repo[:tag][@sha256:digest])
// extended for registry hosts with ports: host[:port][/path...][:tag][@digest].
var ImageRef = regexp.MustCompile(`^[a-zA-Z0-9][a-zA-Z0-9._-]*(:[0-9]+)?(/[a-zA-Z0-9._/-]+)*(:[a-zA-Z0-9._-]+)?(@sha256:[a-f0-9]{64})?$`)

// MirrorAddr matches a user-configured registry/mirror base address
// (same character class as ImageRef, no tag/digest).
var MirrorAddr = regexp.MustCompile(`^[a-zA-Z0-9][a-zA-Z0-9._/-]*$`)

// DefaultSocket is the only daemon endpoint the agent may use.
const DefaultSocket = "/var/run/docker.sock"

// Runner executes external commands; tests inject fakes.
type Runner func(bin string, args []string, stdin string) (stdout, stderr string, err error)

// AuditEntry records one docker operation.
type AuditEntry struct {
	Time   string `json:"time"`
	Action string `json:"action"` // list|start|stop|restart|logs|terminal|image_list|image_pull|image_remove
	Target string `json:"target"`
	Detail string `json:"detail,omitempty"`
}

// Manager manages Docker through a Runner pinned to the local socket.
type Manager struct {
	Run       Runner
	Socket    string // docker.sock path; never a TCP endpoint
	Mirror    string // optional user-configured pull-through base address
	AuditPath string
	mu        sync.Mutex
	Now       func() time.Time
}

// NewManager wires a Manager over the default local socket with a real audit file.
func NewManager(auditPath string) *Manager {
	return &Manager{Run: runExec, Socket: DefaultSocket, AuditPath: auditPath, Now: time.Now}
}

func runExec(bin string, args []string, stdin string) (string, string, error) {
	cmd := exec.Command(bin, args...)
	var out, errBuf strings.Builder
	cmd.Stdin = strings.NewReader(stdin)
	cmd.Stdout = &out
	cmd.Stderr = &errBuf
	err := cmd.Run()
	return out.String(), errBuf.String(), err
}

// baseArgs pins every docker invocation to the local unix socket.
func (m *Manager) baseArgs() []string {
	sock := m.Socket
	if sock == "" {
		sock = DefaultSocket
	}
	return []string{"--host", "unix://" + sock}
}

func (m *Manager) run(args ...string) (string, error) {
	stdout, stderr, err := m.Run("docker", append(m.baseArgs(), args...), "")
	if err != nil {
		msg := strings.TrimSpace(stderr)
		if msg == "" {
			msg = strings.TrimSpace(stdout)
		}
		if msg == "" {
			msg = err.Error()
		}
		return stdout, fmt.Errorf("docker %s: %s", args[0], msg)
	}
	return stdout, nil
}

// Container is one row of the container list.
type Container struct {
	ID     string   `json:"id"`
	Names  []string `json:"names"`
	Image  string   `json:"image"`
	State  string   `json:"state"`
	Status string   `json:"status"`
	Ports  string   `json:"ports,omitempty"`
}

// Image is one row of the image list.
type Image struct {
	Repo       string   `json:"repo"`
	Tag        string   `json:"tag"`
	ID         string   `json:"id"`
	Size       string   `json:"size"`
	Created    string   `json:"created"`
	Containers []string `json:"containers,omitempty"`
}

// ListContainers returns all containers (running and stopped).
func (m *Manager) ListContainers() ([]Container, error) {
	out, err := m.run("ps", "-a", "--format", "{{json .}}")
	if err != nil {
		return nil, err
	}
	m.audit("list", "*", "")
	var list []Container
	for _, line := range strings.Split(strings.TrimSpace(out), "\n") {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		var row struct {
			ID     string   `json:"ID"`
			Names  []string `json:"Names"`
			Image  string   `json:"Image"`
			State  string   `json:"State"`
			Status string   `json:"Status"`
			Ports  string   `json:"Ports"`
		}
		if jerr := json.Unmarshal([]byte(line), &row); jerr != nil {
			return nil, fmt.Errorf("docker: parse ps row: %w", jerr)
		}
		list = append(list, Container{
			ID: row.ID, Names: row.Names, Image: row.Image,
			State: row.State, Status: row.Status, Ports: row.Ports,
		})
	}
	return list, nil
}

// ContainerAction starts/stops/restarts a whitelisted container.
func (m *Manager) ContainerAction(action, name string) error {
	if !ContainerName.MatchString(name) {
		return fmt.Errorf("docker: invalid container name %q", name)
	}
	switch action {
	case "start", "stop", "restart":
	default:
		return fmt.Errorf("docker: unknown container action %q", action)
	}
	_, err := m.run(action, name)
	if err == nil {
		m.audit(action, name, "")
	}
	return err
}

// Logs returns the last N lines (default 100) of a container's logs.
func (m *Manager) Logs(name string, tail int) (string, error) {
	if !ContainerName.MatchString(name) {
		return "", fmt.Errorf("docker: invalid container name %q", name)
	}
	if tail <= 0 {
		tail = 100
	}
	if tail > 10000 {
		tail = 10000
	}
	out, err := m.run("logs", "--tail", fmt.Sprint(tail), name)
	if err == nil {
		m.audit("logs", name, fmt.Sprint(tail))
	}
	return out, err
}

// Terminal runs one command inside a container (exec backend for the
// web terminal; session routing stays on term.manage).
func (m *Manager) Terminal(name, command string) (string, error) {
	if !ContainerName.MatchString(name) {
		return "", fmt.Errorf("docker: invalid container name %q", name)
	}
	if strings.TrimSpace(command) == "" {
		return "", fmt.Errorf("docker: empty terminal command")
	}
	out, err := m.run("exec", name, "sh", "-c", command)
	if err == nil {
		m.audit("terminal", name, command)
	}
	return out, err
}

// ListImages returns all local images.
func (m *Manager) ListImages() ([]Image, error) {
	out, err := m.run("images", "--format", "{{json .}}")
	if err != nil {
		return nil, err
	}
	m.audit("image_list", "*", "")
	var list []Image
	for _, line := range strings.Split(strings.TrimSpace(out), "\n") {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		var row struct {
			Repository string `json:"Repository"`
			Tag        string `json:"Tag"`
			ID         string `json:"ID"`
			Size       string `json:"Size"`
			CreatedAt  string `json:"CreatedAt"`
		}
		if jerr := json.Unmarshal([]byte(line), &row); jerr != nil {
			return nil, fmt.Errorf("docker: parse images row: %w", jerr)
		}
		list = append(list, Image{
			Repo: row.Repository, Tag: row.Tag, ID: row.ID,
			Size: row.Size, Created: row.CreatedAt,
		})
	}
	return list, nil
}

// PullImage pulls a whitelisted image reference, optionally through the
// configured mirror, and returns the raw progress output (layers/bytes).
func (m *Manager) PullImage(ref string) (string, error) {
	if !ImageRef.MatchString(ref) {
		return "", fmt.Errorf("docker: invalid image reference %q", ref)
	}
	target := ref
	if m.Mirror != "" {
		if !MirrorAddr.MatchString(m.Mirror) {
			return "", fmt.Errorf("docker: invalid mirror address %q", m.Mirror)
		}
		// Split repo:tag@digest into mirror + original path.
		target = m.Mirror + "/" + ref
	}
	out, err := m.run("pull", target)
	if err == nil {
		m.audit("image_pull", ref, "")
	}
	return out, err
}

// RemoveImage removes a whitelisted image reference (Admin only upstream).
func (m *Manager) RemoveImage(ref string) error {
	if !ImageRef.MatchString(ref) {
		return fmt.Errorf("docker: invalid image reference %q", ref)
	}
	_, err := m.run("rmi", ref)
	if err == nil {
		m.audit("image_remove", ref, "")
	}
	return err
}

func (m *Manager) audit(action, target, detail string) {
	if m.AuditPath == "" {
		return
	}
	e := AuditEntry{Time: m.Now().UTC().Format(time.RFC3339), Action: action, Target: target, Detail: detail}
	b, jerr := json.Marshal(e)
	if jerr != nil {
		return
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	f, err := os.OpenFile(m.AuditPath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
	if err != nil {
		return
	}
	defer f.Close()
	f.Write(append(b, '\n'))
}

// Handler returns the unified module handler for "docker.manage".
func Handler(m *Manager) registry.Handler {
	return func(args map[string]any) (registry.Result, error) {
		op, _ := args["op"].(string)
		name, _ := args["name"].(string)
		switch op {
		case "list":
			list, err := m.ListContainers()
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "ok", Data: map[string]any{"containers": list}}, nil
		case "start", "stop", "restart":
			if err := m.ContainerAction(op, name); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: op + " ok"}, nil
		case "logs":
			tail := 100
			if v, ok := args["tail"]; ok {
				if n, ok := toInt(v); ok {
					tail = n
				}
			}
			out, err := m.Logs(name, tail)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: out}, nil
		case "terminal":
			cmd, _ := args["command"].(string)
			out, err := m.Terminal(name, cmd)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: out}, nil
		case "image_list":
			list, err := m.ListImages()
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "ok", Data: map[string]any{"images": list}}, nil
		case "image_pull":
			ref, _ := args["ref"].(string)
			out, err := m.PullImage(ref)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: out}, nil
		case "image_remove":
			ref, _ := args["ref"].(string)
			if err := m.RemoveImage(ref); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "removed"}, nil
		}
		return registry.Result{ExitCode: 2}, fmt.Errorf("docker: unknown op %q", op)
	}
}

// Register installs the docker handlers into r.
func Register(r *registry.Registry, m *Manager) error {
	return r.Register("docker.manage", Handler(m))
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
