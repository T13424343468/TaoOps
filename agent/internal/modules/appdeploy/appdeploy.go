// Package appdeploy implements FR-A24 (one-click app deployment):
// a built-in container template library rendered to Compose files,
// deploy with post-deploy health checks, upgrade with automatic
// rollback, and list/remove — all through the local docker.sock only
// (same socket pin as FR-A11). Every operation is audited.
//
// 自动化运维三件（PRD v1.3）:
//   - 健康检查: deploy/upgrade block until every service with a
//     healthcheck reports healthy, or the operation fails;
//   - 升级自动回滚: upgrade backs up the previous compose file; if the
//     new version fails its health check, the old file is restored and
//     redeployed automatically;
//   - 容器异常自动拉起: every service renders restart: unless-stopped;
//     deployment/upgrade failures surface to the control plane, which
//     feeds the alert center (FR-A19).
package appdeploy

import (
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"

	"taoops/agent/internal/modules/docker"
	"taoops/agent/internal/modules/registry"
)

// AppName matches the PRD whitelist (same shape as container names).
var AppName = docker.ContainerName

// Runner executes external commands; tests inject fakes.
type Runner func(bin string, args []string, stdin string) (stdout, stderr string, err error)

// AuditEntry records one appdeploy operation.
type AuditEntry struct {
	Time   string `json:"time"`
	Action string `json:"action"` // templates|deploy|upgrade|remove|list
	Target string `json:"target"`
	Detail string `json:"detail,omitempty"`
}

// Manager deploys template-based apps through a Runner pinned to the
// local docker socket.
type Manager struct {
	Run       Runner
	Socket    string
	Dir       string // dataDir/apps/<name>/ per app
	AuditPath string
	// Health polling knobs (overridable in tests).
	PollAttempts int
	PollInterval time.Duration
	Now          func() time.Time
	mu           sync.Mutex
}

// NewManager wires a Manager over the default local socket.
func NewManager(dir, auditPath string) *Manager {
	return &Manager{
		Run: runExec, Socket: docker.DefaultSocket, Dir: dir, AuditPath: auditPath,
		PollAttempts: 12, PollInterval: 5 * time.Second, Now: time.Now,
	}
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

func (m *Manager) run(args ...string) (string, error) {
	sock := m.Socket
	if sock == "" {
		sock = docker.DefaultSocket
	}
	args = append([]string{"--host", "unix://" + sock}, args...)
	stdout, stderr, err := m.Run("docker", args, "")
	if err != nil {
		msg := strings.TrimSpace(stderr)
		if msg == "" {
			msg = strings.TrimSpace(stdout)
		}
		if msg == "" {
			msg = err.Error()
		}
		return stdout, fmt.Errorf("docker %s: %s", args[1], msg)
	}
	return stdout, nil
}

// compose runs a docker compose subcommand for one app.
func (m *Manager) compose(app string, args ...string) (string, error) {
	file := m.composeFile(app)
	full := append([]string{"compose", "-f", file, "-p", app}, args...)
	stdout, stderr, err := m.Run("docker", append([]string{"--host", "unix://" + orDefault(m.Socket)}, full...), "")
	if err != nil {
		msg := strings.TrimSpace(stderr)
		if msg == "" {
			msg = strings.TrimSpace(stdout)
		}
		if msg == "" {
			msg = err.Error()
		}
		return stdout, fmt.Errorf("docker compose %s: %s", args[0], msg)
	}
	return stdout, nil
}

func orDefault(s string) string {
	if s == "" {
		return docker.DefaultSocket
	}
	return s
}

func (m *Manager) appDir(app string) string { return filepath.Join(m.Dir, app) }
func (m *Manager) composeFile(app string) string {
	return filepath.Join(m.appDir(app), "docker-compose.yml")
}
func (m *Manager) stateFile(app string) string {
	return filepath.Join(m.appDir(app), "state.json")
}

// State is the persisted per-app record.
type State struct {
	App        string   `json:"app"`
	TemplateID string   `json:"template_id"`
	Images     []string `json:"images"`
	DeployedAt string   `json:"deployed_at"`
	Health     string   `json:"health"` // healthy | unhealthy | unknown
}

// renderCompose renders a template into Compose YAML. Every service
// gets restart: unless-stopped (异常自动拉起) and, when configured, a
// healthcheck block.
func renderCompose(t Template, envOverrides map[string]string) string {
	var b strings.Builder
	b.WriteString("name: app\nservices:\n")
	for _, s := range t.Services {
		fmt.Fprintf(&b, "  %s:\n", s.Name)
		fmt.Fprintf(&b, "    image: %s\n", s.Image)
		b.WriteString("    restart: unless-stopped\n")
		if len(s.Ports) > 0 {
			b.WriteString("    ports:\n")
			for _, p := range s.Ports {
				fmt.Fprintf(&b, "      - %q\n", p)
			}
		}
		env := map[string]string{}
		for k, v := range s.Env {
			env[k] = v
		}
		for k, v := range envOverrides {
			env[k] = v
		}
		if len(env) > 0 {
			b.WriteString("    environment:\n")
			for _, k := range sortedKeys(env) {
				fmt.Fprintf(&b, "      %s: %q\n", k, env[k])
			}
		}
		if len(s.Volumes) > 0 {
			b.WriteString("    volumes:\n")
			for _, v := range s.Volumes {
				fmt.Fprintf(&b,      "      - %q\n", v)
			}
		}
		if s.HealthCmd != "" {
			interval := s.HealthInterval
			if interval <= 0 {
				interval = 30
			}
			b.WriteString("    healthcheck:\n")
			fmt.Fprintf(&b, "      test: [\"CMD-SHELL\", %q]\n", s.HealthCmd)
			fmt.Fprintf(&b, "      interval: %ds\n", interval)
			b.WriteString("      retries: 5\n")
		}
	}
	return b.String()
}

func sortedKeys(m map[string]string) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	for i := 0; i < len(keys); i++ {
		for j := i + 1; j < len(keys); j++ {
			if keys[j] < keys[i] {
				keys[i], keys[j] = keys[j], keys[i]
			}
		}
	}
	return keys
}

func writeCompose(path, content string) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	return os.WriteFile(path, []byte(content), 0o644)
}

// saveState persists the per-app record.
func (m *Manager) saveState(st State) error {
	b, err := json.Marshal(st)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(m.appDir(st.App), 0o755); err != nil {
		return err
	}
	return os.WriteFile(m.stateFile(st.App), b, 0o644)
}

func (m *Manager) loadState(app string) (State, error) {
	b, err := os.ReadFile(m.stateFile(app))
	if err != nil {
		return State{}, err
	}
	var st State
	if jerr := json.Unmarshal(b, &st); jerr != nil {
		return State{}, jerr
	}
	return st, nil
}

// waitForHealthy polls every service container of the app until
// healthy or attempts are exhausted. Returns the final health string
// and an error when unhealthy/timeout.
func (m *Manager) waitForHealthy(app string, services []Service) (string, error) {
	attempts := m.PollAttempts
	if attempts <= 0 {
		attempts = 12
	}
	interval := m.PollInterval
	if interval <= 0 {
		interval = 5 * time.Second
	}
	var last string
	for i := 0; i < attempts; i++ {
		allHealthy := true
		last = "healthy"
		for _, s := range services {
			if s.HealthCmd == "" {
				continue // no healthcheck configured: treat as healthy
			}
			out, err := m.compose(app, "ps", "-q", s.Name)
			if err != nil || strings.TrimSpace(out) == "" {
				allHealthy = false
				last = "unhealthy"
				break
			}
			id := strings.TrimSpace(out)
			hout, herr := m.run("inspect", "-f", "{{.State.Health.Status}}", id)
			status := strings.TrimSpace(hout)
			if herr != nil || status != "healthy" {
				allHealthy = false
				last = "unhealthy"
				break
			}
		}
		if allHealthy {
			return "healthy", nil
		}
		if i < attempts-1 {
			time.Sleep(interval)
		}
	}
	return last, fmt.Errorf("appdeploy: health check failed for %q (status %s)", app, last)
}

// Deploy renders the template and brings the app up, blocking until
// healthy. On health failure the deployment is torn down (no
// half-dead apps left behind).
func (m *Manager) Deploy(app, templateID string, envOverrides map[string]string) (State, error) {
	if !AppName.MatchString(app) {
		return State{}, fmt.Errorf("appdeploy: invalid app name %q", app)
	}
	t, ok := GetTemplate(templateID)
	if !ok {
		return State{}, fmt.Errorf("appdeploy: unknown template %q", templateID)
	}
	content := renderCompose(t, envOverrides)
	if err := writeCompose(m.composeFile(app), content); err != nil {
		return State{}, err
	}
	if _, err := m.compose(app, "up", "-d"); err != nil {
		m.audit("deploy", app, "up failed: "+err.Error())
		return State{}, err
	}
	health, herr := m.waitForHealthy(app, t.Services)
	images := make([]string, 0, len(t.Services))
	for _, s := range t.Services {
		images = append(images, s.Image)
	}
	st := State{App: app, TemplateID: templateID, Images: images, DeployedAt: m.Now().UTC().Format(time.RFC3339), Health: health}
	if herr != nil {
		// 健康检查不过自动标记失败，不留半死不活的应用。
		_, _ = m.compose(app, "down")
		m.saveState(st)
		m.audit("deploy", app, "health failed: "+health)
		return st, herr
	}
	if err := m.saveState(st); err != nil {
		return st, err
	}
	m.audit("deploy", app, templateID)
	return st, nil
}

// Upgrade redeploys an app from its current template (optionally with
// new env), keeping a backup of the previous compose file. If the new
// version fails its health check, the previous file is restored and
// redeployed automatically (升级自动回滚).
func (m *Manager) Upgrade(app string, envOverrides map[string]string) (State, error) {
	if !AppName.MatchString(app) {
		return State{}, fmt.Errorf("appdeploy: invalid app name %q", app)
	}
	prev, err := m.loadState(app)
	if err != nil {
		return State{}, fmt.Errorf("appdeploy: app %q not deployed here", app)
	}
	t, ok := GetTemplate(prev.TemplateID)
	if !ok {
		return State{}, fmt.Errorf("appdeploy: template %q no longer available", prev.TemplateID)
	}
	cf := m.composeFile(app)
	bak := cf + ".bak"
	prevBytes, rerr := os.ReadFile(cf)
	if rerr != nil {
		return State{}, rerr
	}
	if werr := os.WriteFile(bak, prevBytes, 0o644); werr != nil {
		return State{}, werr
	}
	content := renderCompose(t, envOverrides)
	if err := writeCompose(cf, content); err != nil {
		return State{}, err
	}
	if _, err := m.compose(app, "up", "-d"); err != nil {
		m.rollback(app, bak, cf)
		m.audit("upgrade", app, "up failed, rolled back")
		return prev, fmt.Errorf("appdeploy: upgrade up failed (rolled back): %w", err)
	}
	health, herr := m.waitForHealthy(app, t.Services)
	st := State{App: app, TemplateID: prev.TemplateID, Images: prev.Images, DeployedAt: m.Now().UTC().Format(time.RFC3339), Health: health}
	if herr != nil {
		m.rollback(app, bak, cf)
		_, _ = m.compose(app, "up", "-d")
		m.saveState(prev)
		m.audit("upgrade", app, "health failed, rolled back")
		return prev, fmt.Errorf("appdeploy: upgrade health check failed (rolled back): %w", herr)
	}
	if err := m.saveState(st); err != nil {
		return st, err
	}
	_ = os.Remove(bak)
	m.audit("upgrade", app, prev.TemplateID)
	return st, nil
}

// rollback restores the backed-up compose file.
func (m *Manager) rollback(app, bak, cf string) {
	if b, err := os.ReadFile(bak); err == nil {
		_ = os.WriteFile(cf, b, 0o644)
	}
}

// List returns all apps deployed from this data dir with their state.
func (m *Manager) List() ([]State, error) {
	entries, err := os.ReadDir(m.Dir)
	if err != nil {
		if os.IsNotExist(err) {
			return nil, nil
		}
		return nil, err
	}
	var out []State
	for _, e := range entries {
		if !e.IsDir() {
			continue
		}
		if st, err := m.loadState(e.Name()); err == nil {
			out = append(out, st)
		}
	}
	return out, nil
}

// Remove tears an app down (compose down) and deletes its directory.
func (m *Manager) Remove(app string) error {
	if !AppName.MatchString(app) {
		return fmt.Errorf("appdeploy: invalid app name %q", app)
	}
	if _, err := os.Stat(m.composeFile(app)); err != nil {
		return fmt.Errorf("appdeploy: app %q not deployed here", app)
	}
	if _, err := m.compose(app, "down"); err != nil {
		m.audit("remove", app, "down failed: "+err.Error())
		return err
	}
	if err := os.RemoveAll(m.appDir(app)); err != nil {
		return err
	}
	m.audit("remove", app, "")
	return nil
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

// Handler returns the unified module handler for "app.deploy".
func Handler(m *Manager) registry.Handler {
	return func(args map[string]any) (registry.Result, error) {
		op, _ := args["op"].(string)
		app, _ := args["app"].(string)
		switch op {
		case "templates":
			return registry.Result{ExitCode: 0, Output: "ok", Data: map[string]any{"templates": Templates()}}, nil
		case "deploy":
			tid, _ := args["template"].(string)
			env := envOverridesFromArgs(args)
			st, err := m.Deploy(app, tid, env)
			if err != nil {
				return registry.Result{ExitCode: 1, Data: map[string]any{"state": st}}, err
			}
			return registry.Result{ExitCode: 0, Output: "deployed", Data: map[string]any{"state": st}}, nil
		case "upgrade":
			env := envOverridesFromArgs(args)
			st, err := m.Upgrade(app, env)
			if err != nil {
				return registry.Result{ExitCode: 1, Data: map[string]any{"state": st}}, err
			}
			return registry.Result{ExitCode: 0, Output: "upgraded", Data: map[string]any{"state": st}}, nil
		case "list":
			list, err := m.List()
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "ok", Data: map[string]any{"apps": list}}, nil
		case "remove":
			if err := m.Remove(app); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "removed"}, nil
		}
		return registry.Result{ExitCode: 2}, fmt.Errorf("appdeploy: unknown op %q", op)
	}
}

func toStringMap(v any) map[string]string {
	out := map[string]string{}
	switch t := v.(type) {
	case map[string]string:
		return t
	case map[string]any:
		for k, val := range t {
			switch s := val.(type) {
			case string:
				out[k] = s
			default:
				out[k] = fmt.Sprint(s)
			}
		}
	}
	return out
}

// envOverridesFromArgs collects env overrides from the flat key=value
// arg convention used by the HTTP bridge: "env.KEY=value" pairs.
func envOverridesFromArgs(args map[string]any) map[string]string {
	out := toStringMap(args["env"])
	prefix := "env."
	for k, v := range args {
		if strings.HasPrefix(k, prefix) {
			if s, ok := v.(string); ok {
				out[strings.TrimPrefix(k, prefix)] = s
			}
		}
	}
	if len(out) == 0 {
		return nil
	}
	return out
}

// Register installs the appdeploy handler into r.
func Register(r *registry.Registry, m *Manager) error {
	return r.Register("app.deploy", Handler(m))
}

var _ = strconv.Atoi // keep strconv for future numeric args
