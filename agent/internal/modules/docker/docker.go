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
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
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

// MirrorURL matches a daemon.json registry-mirrors entry: scheme required,
// host[:port][/path]. Docker accepts full URLs here.
var MirrorURL = regexp.MustCompile(`^https?://[a-zA-Z0-9][a-zA-Z0-9._-]*(:[0-9]+)?(/[a-zA-Z0-9._/-]*)?$`)

// RegistryHost matches a docker login registry host: host[:port][/namespace].
var RegistryHost = regexp.MustCompile(`^[a-zA-Z0-9][a-zA-Z0-9._-]*(:[0-9]+)?(/[a-zA-Z0-9._/-]+)?$`)

// LoginUser matches a docker login username: printable, no whitespace/control.
var LoginUser = regexp.MustCompile(`^[^\s\x00-\x1f\x7f]{1,128}$`)

// LoginSecret matches a docker login password: printable, no control chars.
var LoginSecret = regexp.MustCompile(`^[^\x00-\x1f\x7f]{1,512}$`)

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
	// DaemonJSON is the daemon.json path (default /etc/docker/daemon.json);
	// DockerConfig is the docker client config.json path (default from
	// DOCKER_CONFIG env or ~/.docker/config.json). Tests override both.
	DaemonJSON   string
	DockerConfig string
	mu           sync.Mutex
	Now          func() time.Time
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

// daemonJSONPath resolves the daemon.json path (field override or default).
func (m *Manager) daemonJSONPath() string {
	if m.DaemonJSON != "" {
		return m.DaemonJSON
	}
	return "/etc/docker/daemon.json"
}

// dockerConfigPath resolves the docker client config.json path:
// field override, then DOCKER_CONFIG env, then ~/.docker/config.json.
func (m *Manager) dockerConfigPath() string {
	if m.DockerConfig != "" {
		return m.DockerConfig
	}
	if env := os.Getenv("DOCKER_CONFIG"); env != "" {
		return filepath.Join(env, "config.json")
	}
	home, err := os.UserHomeDir()
	if err != nil || home == "" {
		return ""
	}
	return filepath.Join(home, ".docker", "config.json")
}

// readMirrors loads the current registry-mirrors list from daemon.json
// (missing file or missing key => empty list).
func (m *Manager) readMirrors() ([]string, error) {
	b, err := os.ReadFile(m.daemonJSONPath())
	if err != nil {
		if os.IsNotExist(err) {
			return []string{}, nil
		}
		return nil, fmt.Errorf("docker: read daemon.json: %w", err)
	}
	var cfg map[string]any
	if err := json.Unmarshal(b, &cfg); err != nil {
		return nil, fmt.Errorf("docker: parse daemon.json: %w", err)
	}
	raw, _ := cfg["registry-mirrors"].([]any)
	list := make([]string, 0, len(raw))
	for _, v := range raw {
		if s, ok := v.(string); ok && s != "" {
			list = append(list, s)
		}
	}
	return list, nil
}

// writeMirrors rewrites daemon.json with the given registry-mirrors list,
// preserving every other key. The previous file is backed up as
// <path>.bak.<unix-ts> first; write is 0644. registry-mirrors changes only
// take effect after a daemon restart (see DaemonRestart).
func (m *Manager) writeMirrors(list []string) error {
	path := m.daemonJSONPath()
	cfg := map[string]any{}
	if b, err := os.ReadFile(path); err == nil {
		if err := json.Unmarshal(b, &cfg); err != nil {
			return fmt.Errorf("docker: parse daemon.json: %w", err)
		}
		if bak := path + ".bak." + strconv.FormatInt(m.Now().Unix(), 10); os.WriteFile(bak, b, 0o644) != nil {
			// Backup failure must not block the update; the audit trail
			// records the action either way.
			_ = bak
		}
	}
	anyList := make([]any, len(list))
	for i, s := range list {
		anyList[i] = s
	}
	cfg["registry-mirrors"] = anyList
	out, err := json.MarshalIndent(cfg, "", "  ")
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return fmt.Errorf("docker: mkdir for daemon.json: %w", err)
	}
	return os.WriteFile(path, append(out, '\n'), 0o644)
}

// MirrorList returns the current registry-mirrors entries.
func (m *Manager) MirrorList() ([]string, error) {
	list, err := m.readMirrors()
	if err != nil {
		return nil, err
	}
	m.audit("mirror_list", "*", "")
	return list, nil
}

// MirrorAdd appends a mirror URL (dedup, order preserved). The change is
// written to daemon.json but only applies after DaemonRestart.
func (m *Manager) MirrorAdd(addr string) error {
	if !MirrorURL.MatchString(addr) {
		return fmt.Errorf("docker: invalid mirror url %q", addr)
	}
	m.mu.Lock()
	list, err := m.readMirrors()
	if err != nil {
		m.mu.Unlock()
		return err
	}
	dup := false
	for _, s := range list {
		if s == addr {
			dup = true // idempotent
			break
		}
	}
	if dup {
		m.mu.Unlock()
		return nil
	}
	list = append(list, addr)
	if err := m.writeMirrors(list); err != nil {
		m.mu.Unlock()
		return err
	}
	// audit 内部也取 m.mu，须先放锁再落审计（非重入锁，持锁调用即自死锁）。
	m.mu.Unlock()
	m.audit("mirror_add", addr, "")
	return nil
}

// MirrorRemove drops a mirror URL (missing entry is idempotent success).
func (m *Manager) MirrorRemove(addr string) error {
	if !MirrorURL.MatchString(addr) {
		return fmt.Errorf("docker: invalid mirror url %q", addr)
	}
	m.mu.Lock()
	list, err := m.readMirrors()
	if err != nil {
		m.mu.Unlock()
		return err
	}
	out := list[:0]
	found := false
	for _, s := range list {
		if s == addr {
			found = true
			continue
		}
		out = append(out, s)
	}
	if !found {
		m.mu.Unlock()
		return nil
	}
	if err := m.writeMirrors(out); err != nil {
		m.mu.Unlock()
		return err
	}
	// 同 MirrorAdd：先放锁再落审计，避免与 audit 内部加锁自死锁。
	m.mu.Unlock()
	m.audit("mirror_remove", addr, "")
	return nil
}

// DaemonRestart restarts the docker daemon so daemon.json changes
// (registry-mirrors) take effect. Running containers come back per their
// restart policy; the panel surfaces this warning before calling.
func (m *Manager) DaemonRestart() error {
	_, _, err := m.Run("systemctl", []string{"restart", "docker"}, "")
	if err != nil {
		return fmt.Errorf("docker: daemon restart failed: %v", err)
	}
	m.audit("daemon_restart", "*", "")
	return nil
}

// RegistryLogin authenticates against a registry via --password-stdin; the
// password never appears in argv or the audit trail. Empty registry means
// Docker Hub. Credentials are stored by the docker client itself.
func (m *Manager) RegistryLogin(registry, username, password string) error {
	if registry != "" && !RegistryHost.MatchString(registry) {
		return fmt.Errorf("docker: invalid registry host %q", registry)
	}
	if !LoginUser.MatchString(username) {
		return fmt.Errorf("docker: invalid username")
	}
	if !LoginSecret.MatchString(password) {
		return fmt.Errorf("docker: invalid password")
	}
	args := m.baseArgs()
	args = append(args, "login")
	if registry != "" {
		args = append(args, registry)
	}
	args = append(args, "-u", username, "--password-stdin")
	if _, _, err := m.Run("docker", args, password); err != nil {
		return fmt.Errorf("docker: login failed: %v", err)
	}
	m.audit("registry_login", registryHostLabel(registry), "user="+username)
	return nil
}

// RegistryLogout removes stored credentials for a registry (empty = Docker Hub).
func (m *Manager) RegistryLogout(registry string) error {
	if registry != "" && !RegistryHost.MatchString(registry) {
		return fmt.Errorf("docker: invalid registry host %q", registry)
	}
	args := m.baseArgs()
	args = append(args, "logout")
	if registry != "" {
		args = append(args, registry)
	}
	if _, _, err := m.Run("docker", args, ""); err != nil {
		return fmt.Errorf("docker: logout failed: %v", err)
	}
	m.audit("registry_logout", registryHostLabel(registry), "")
	return nil
}

// RegistryList returns the registry hosts with stored credentials, from the
// docker client config.json "auths" keys. Only host names are returned —
// never the credential material itself.
func (m *Manager) RegistryList() ([]string, error) {
	path := m.dockerConfigPath()
	if path == "" {
		return []string{}, nil
	}
	b, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return []string{}, nil
		}
		return nil, fmt.Errorf("docker: read config.json: %w", err)
	}
	var cfg struct {
		Auths map[string]json.RawMessage `json:"auths"`
	}
	if err := json.Unmarshal(b, &cfg); err != nil {
		return nil, fmt.Errorf("docker: parse config.json: %w", err)
	}
	hosts := make([]string, 0, len(cfg.Auths))
	for host := range cfg.Auths {
		hosts = append(hosts, host)
	}
	sort.Strings(hosts)
	m.audit("registry_list", "*", "")
	return hosts, nil
}

func registryHostLabel(registry string) string {
	if registry == "" {
		return "docker.io"
	}
	return registry
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
		case "mirror_list":
			list, err := m.MirrorList()
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			// 下发桥仅回传 Output：结构化结果按 FR-A17 约定落 Output JSON。
			b, _ := json.Marshal(map[string]any{"mirrors": list})
			return registry.Result{ExitCode: 0, Output: string(b)}, nil
		case "mirror_add":
			addr, _ := args["addr"].(string)
			if err := m.MirrorAdd(addr); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "mirror added (restart docker to apply)"}, nil
		case "mirror_remove":
			addr, _ := args["addr"].(string)
			if err := m.MirrorRemove(addr); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "mirror removed (restart docker to apply)"}, nil
		case "daemon_restart":
			if err := m.DaemonRestart(); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "docker daemon restarted"}, nil
		case "registry_list":
			hosts, err := m.RegistryList()
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			b, _ := json.Marshal(map[string]any{"registries": hosts})
			return registry.Result{ExitCode: 0, Output: string(b)}, nil
		case "registry_login":
			reg, _ := args["registry"].(string)
			user, _ := args["username"].(string)
			pass, _ := args["password"].(string)
			if err := m.RegistryLogin(reg, user, pass); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "login ok"}, nil
		case "registry_logout":
			reg, _ := args["registry"].(string)
			if err := m.RegistryLogout(reg); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "logout ok"}, nil
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
	case string:
		n, err := strconv.Atoi(strings.TrimSpace(t))
		return n, err == nil
	}
	return 0, false
}
