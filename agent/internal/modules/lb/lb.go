// Package lb implements FR-A18 load balancing: Nginx upstream fragment
// management (backend add/remove, weights, drain marks, TCP/HTTP health
// checks) with nginx -t precheck and rollback before any fragment lands.
// Contract source: docs/protocol-contract.md §9.
package lb

import (
	"encoding/json"
	"fmt"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"time"

	"taoops/agent/internal/modules/registry"
)

// HostPattern validates backend hosts (contract §9; IPv6 bracket form is
// rejected with a dedicated message this iteration).
var HostPattern = regexp.MustCompile(`^[a-zA-Z0-9._-]+$`)

// HealthCheckInterval is the panel/periodic trigger cadence (PRD: 30s).
const HealthCheckInterval = 30 * time.Second

// HealthFailThreshold is the consecutive-failure count before a backend is
// marked down (PRD: 3).
const HealthFailThreshold = 3

// Backend is one upstream server line.
type Backend struct {
	Host       string `json:"host"`
	Port       int    `json:"port"`
	Weight     int    `json:"weight"`
	Backup     bool   `json:"backup"`
	Down       bool   `json:"down"`
	HealthMode string `json:"health_mode"` // tcp | http
	HealthPath string `json:"health_path,omitempty"`
	FailCount  int    `json:"fail_count"`
}

// Group is one upstream fragment: <confDir>/lb/<name>.conf.
type Group struct {
	Name     string    `json:"name"`
	Backends []Backend `json:"backends"`
}

// Manager renders and audits upstream fragments under confDir/lb.
type Manager struct {
	confDir string
	audit   string
	now     func() time.Time
	// nginxBin is overridable in tests; empty means "nginx not available".
	nginxBin string
	// dial and httpGet are overridable for health-check tests.
	dial    func(network, addr string, timeout time.Duration) (net.Conn, error)
	httpGet func(url string, timeout time.Duration) (int, error)
}

// NewManager returns a Manager writing fragments under confDir/lb and
// auditing changes to auditPath.
func NewManager(confDir, auditPath string) *Manager {
	return &Manager{
		confDir: confDir, audit: auditPath, now: time.Now,
		nginxBin: "nginx",
		dial:     net.DialTimeout,
		httpGet:  httpProbe,
	}
}

// Register installs the "lb.manage" handler.
func Register(r *registry.Registry, m *Manager) error {
	return r.Register("lb.manage", registry.Handler(m.handle))
}

func (m *Manager) handle(args map[string]any) (registry.Result, error) {
	op, _ := args["op"].(string)
	switch op {
	case "list":
		return m.opList(args)
	case "add":
		return m.opAdd(args)
	case "remove":
		return m.opRemove(args)
	case "drain":
		return m.opDrain(args, true)
	case "undrain":
		return m.opDrain(args, false)
	case "health":
		return m.opHealth(args)
	}
	return registry.Result{ExitCode: 2}, fmt.Errorf("lb: unknown op %q (want list|add|remove|drain|undrain|health)", op)
}

func str(args map[string]any, key string) string {
	v, _ := args[key].(string)
	return v
}

func toInt(v any) (int, bool) {
	switch n := v.(type) {
	case int:
		return n, true
	case int64:
		return int(n), true
	case float64:
		return int(n), true
	case string:
		if out, err := strconv.Atoi(n); err == nil {
			return out, true
		}
	}
	return 0, false
}

func toBool(v any) bool {
	switch b := v.(type) {
	case bool:
		return b
	case string:
		return b == "true" || b == "1"
	}
	return false
}

func (m *Manager) lbDir() string { return filepath.Join(m.confDir, "lb") }

func (m *Manager) groupPath(name string) string {
	return filepath.Join(m.lbDir(), sanitizeName(name)+".conf")
}

func sanitizeName(name string) string {
	return strings.Map(func(r rune) rune {
		if HostPattern.MatchString(string(r)) {
			return r
		}
		return '_'
	}, name)
}

func (m *Manager) opList(args map[string]any) (registry.Result, error) {
	name := str(args, "name")
	matches, err := filepath.Glob(filepath.Join(m.lbDir(), "*.conf"))
	if err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	sort.Strings(matches)
	var groups []Group
	for _, p := range matches {
		g, err := readGroup(p)
		if err != nil {
			continue
		}
		if name != "" && g.Name != name {
			continue
		}
		groups = append(groups, *g)
	}
	out, _ := json.Marshal(map[string]any{"groups": groups})
	if groups == nil {
		out = []byte(`{"groups":[]}`)
	}
	// 下发桥仅回传 Output：结构化结果按 FR-A17 约定落 Output JSON（Data 保留兼容）。
	return registry.Result{ExitCode: 0, Output: string(out), Data: map[string]any{"groups": json.RawMessage(out)}}, nil
}

func (m *Manager) opAdd(args map[string]any) (registry.Result, error) {
	name := str(args, "name")
	host := str(args, "host")
	if name == "" || host == "" {
		return registry.Result{ExitCode: 2}, fmt.Errorf("lb: add requires name= and host=")
	}
	if !HostPattern.MatchString(host) {
		if strings.HasPrefix(host, "[") {
			return registry.Result{ExitCode: 2}, fmt.Errorf("lb: IPv6 bracket form not supported this iteration")
		}
		return registry.Result{ExitCode: 2}, fmt.Errorf("lb: host must match ^[a-zA-Z0-9._-]+$")
	}
	port, ok := toInt(args["port"])
	if !ok || port < 1 || port > 65535 {
		return registry.Result{ExitCode: 2}, fmt.Errorf("lb: port must be an integer in [1, 65535]")
	}
	weight := 1
	if w, ok := toInt(args["weight"]); ok {
		weight = w
	}
	if weight < 1 || weight > 100 {
		return registry.Result{ExitCode: 2}, fmt.Errorf("lb: weight must be an integer in [1, 100]")
	}
	healthMode := str(args, "health_mode")
	if healthMode != "tcp" && healthMode != "http" {
		return registry.Result{ExitCode: 2}, fmt.Errorf("lb: health_mode must be tcp or http")
	}
	healthPath := str(args, "health_path")
	if healthMode == "http" {
		if !strings.HasPrefix(healthPath, "/") {
			return registry.Result{ExitCode: 2}, fmt.Errorf("lb: health_path must start with / for http mode")
		}
	}
	backup := toBool(args["backup"])

	g := m.loadOrCreate(name)
	for i := range g.Backends {
		if g.Backends[i].Host == host && g.Backends[i].Port == port {
			return registry.Result{ExitCode: 2}, fmt.Errorf("lb: backend %s:%d already in group %s", host, port, name)
		}
	}
	g.Backends = append(g.Backends, Backend{
		Host: host, Port: port, Weight: weight, Backup: backup,
		HealthMode: healthMode, HealthPath: healthPath,
	})
	if err := m.writeGroup(g); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	m.auditChange("add", name, host, port)
	return registry.Result{ExitCode: 0, Output: fmt.Sprintf("backend added: %s:%d -> %s (weight=%d, health=%s)", host, port, name, weight, healthMode)}, nil
}

func (m *Manager) opRemove(args map[string]any) (registry.Result, error) {
	name, host, port, err := m.target(args)
	if err != nil {
		return registry.Result{ExitCode: 2}, err
	}
	g, ok := m.load(name)
	if !ok {
		return registry.Result{ExitCode: 1}, fmt.Errorf("lb: upstream group %s not found", name)
	}
	found := false
	kept := g.Backends[:0]
	for _, b := range g.Backends {
		if b.Host == host && b.Port == port {
			found = true
			continue
		}
		kept = append(kept, b)
	}
	if !found {
		return registry.Result{ExitCode: 1}, fmt.Errorf("lb: backend %s:%d not in group %s", host, port, name)
	}
	g.Backends = kept
	if len(g.Backends) == 0 {
		if err := os.Remove(m.groupPath(name)); err != nil {
			return registry.Result{ExitCode: 1}, err
		}
	} else if err := m.writeGroup(g); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	m.auditChange("remove", name, host, port)
	return registry.Result{ExitCode: 0, Output: fmt.Sprintf("backend removed: %s:%d from %s", host, port, name)}, nil
}

func (m *Manager) opDrain(args map[string]any, down bool) (registry.Result, error) {
	name, host, port, err := m.target(args)
	if err != nil {
		return registry.Result{ExitCode: 2}, err
	}
	g, ok := m.load(name)
	if !ok {
		return registry.Result{ExitCode: 1}, fmt.Errorf("lb: upstream group %s not found", name)
	}
	found := false
	for i := range g.Backends {
		if g.Backends[i].Host == host && g.Backends[i].Port == port {
			g.Backends[i].Down = down
			if down {
				g.Backends[i].FailCount = 0
			}
			found = true
		}
	}
	if !found {
		return registry.Result{ExitCode: 1}, fmt.Errorf("lb: backend %s:%d not in group %s", host, port, name)
	}
	if err := m.writeGroup(g); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	verb := "undrained"
	if down {
		verb = "drained"
	}
	m.auditChange(verb, name, host, port)
	return registry.Result{ExitCode: 0, Output: fmt.Sprintf("backend %s: %s:%d (%s)", verb, host, port, name)}, nil
}

// opHealth probes each backend (optionally one group) and marks a backend
// down after HealthFailThreshold consecutive failures.
func (m *Manager) opHealth(args map[string]any) (registry.Result, error) {
	filter := str(args, "name")
	matches, err := filepath.Glob(filepath.Join(m.lbDir(), "*.conf"))
	if err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	sort.Strings(matches)
	type healthRow struct {
		Group  string `json:"group"`
		Host   string `json:"host"`
		Port   int    `json:"port"`
		Status string `json:"status"` // healthy | down
	}
	var rows []healthRow
	for _, p := range matches {
		g, err := readGroup(p)
		if err != nil || (filter != "" && g.Name != filter) {
			continue
		}
		changed := false
		for i := range g.Backends {
			b := &g.Backends[i]
			ok := m.probe(b)
			status := "healthy"
			if ok {
				if b.FailCount != 0 {
					b.FailCount = 0
					changed = true
				}
			} else {
				b.FailCount++
				changed = true
				if b.FailCount >= HealthFailThreshold && !b.Down {
					b.Down = true
				}
			}
			if b.Down {
				status = "down"
			}
			rows = append(rows, healthRow{Group: g.Name, Host: b.Host, Port: b.Port, Status: status})
		}
		if changed {
			if err := m.writeGroup(g); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
		}
	}
	out, _ := json.Marshal(map[string]any{"health": rows})
	if rows == nil {
		out = []byte(`{"health":[]}`)
	}
	// 下发桥仅回传 Output：结构化结果按 FR-A17 约定落 Output JSON（Data 保留兼容）。
	return registry.Result{ExitCode: 0, Output: string(out), Data: map[string]any{"health": json.RawMessage(out)}}, nil
}

func (m *Manager) probe(b *Backend) bool {
	addr := net.JoinHostPort(b.Host, strconv.Itoa(b.Port))
	if b.HealthMode == "http" {
		path := b.HealthPath
		if path == "" {
			path = "/"
		}
		code, err := m.httpGet(fmt.Sprintf("http://%s%s", addr, path), 3*time.Second)
		return err == nil && code >= 200 && code < 400
	}
	conn, err := m.dial("tcp", addr, 3*time.Second)
	if err != nil {
		return false
	}
	_ = conn.Close()
	return true
}

func httpProbe(url string, timeout time.Duration) (int, error) {
	type res struct {
		code int
		err  error
	}
	ch := make(chan res, 1)
	go func() {
		client := &net.Dialer{Timeout: timeout}
		// Minimal HTTP/1.0 probe: no external dependency.
		conn, err := net.DialTimeout("tcp", hostOf(url), timeout)
		if err != nil {
			ch <- res{0, err}
			return
		}
		defer conn.Close()
		_ = conn.SetDeadline(time.Now().Add(timeout))
		req := "GET " + pathOf(url) + " HTTP/1.0\r\nHost: " + hostOf(url) + "\r\n\r\n"
		if _, err := conn.Write([]byte(req)); err != nil {
			ch <- res{0, err}
			return
		}
		buf := make([]byte, 128)
		n, err := conn.Read(buf)
		if err != nil && n == 0 {
			ch <- res{0, err}
			return
		}
		line := strings.SplitN(string(buf[:n]), "\r\n", 2)[0]
		parts := strings.Fields(line)
		if len(parts) < 2 {
			ch <- res{0, fmt.Errorf("bad status line: %q", line)}
			return
		}
		code, err := strconv.Atoi(parts[1])
		ch <- res{code, err}
		_ = client
	}()
	r := <-ch
	return r.code, r.err
}

func hostOf(url string) string {
	s := strings.TrimPrefix(strings.TrimPrefix(url, "http://"), "https://")
	if i := strings.Index(s, "/"); i >= 0 {
		s = s[:i]
	}
	return s
}

func pathOf(url string) string {
	s := strings.TrimPrefix(url, "http://")
	if i := strings.Index(s, "/"); i >= 0 {
		return s[i:]
	}
	return "/"
}

// writeGroup renders the fragment, runs nginx -t on the staged file and
// only then lands it; any failure leaves the previous fragment intact.
func (m *Manager) writeGroup(g *Group) error {
	if err := os.MkdirAll(m.lbDir(), 0o755); err != nil {
		return fmt.Errorf("lb: mkdir: %w", err)
	}
	path := m.groupPath(g.Name)
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, []byte(render(g)), 0o644); err != nil {
		return fmt.Errorf("lb: write tmp: %w", err)
	}
	// nginx -t 校验（nginx 可用时）；失败不生效并回滚。
	if m.nginxBin != "" {
		if p, err := exec.LookPath(m.nginxBin); err == nil {
			cmd := exec.Command(p, "-t", "-c", tmp)
			if out, err := cmd.CombinedOutput(); err != nil {
				_ = os.Remove(tmp)
				return fmt.Errorf("lb: nginx -t failed, fragment not applied: %s: %w", strings.TrimSpace(string(out)), err)
			}
		}
	}
	if err := os.Rename(tmp, path); err != nil {
		_ = os.Remove(tmp)
		return fmt.Errorf("lb: apply fragment: %w", err)
	}
	return nil
}

// render emits the upstream block; down/backup flags and health-derived
// max_fails/fail_timeout follow the contract §9 line shape.
func render(g *Group) string {
	var sb strings.Builder
	sb.WriteString("# generated by TaoOps lb module; do not edit by hand\n")
	sb.WriteString(fmt.Sprintf("# %s\n", groupJSON(g)))
	sb.WriteString(fmt.Sprintf("upstream %s {\n", g.Name))
	for _, b := range g.Backends {
		line := fmt.Sprintf("  server %s:%d weight=%d", b.Host, b.Port, b.Weight)
		if b.HealthMode != "" {
			line += fmt.Sprintf(" max_fails=%d fail_timeout=%ds", HealthFailThreshold, int(HealthCheckInterval.Seconds()))
		}
		if b.Backup {
			line += " backup"
		}
		if b.Down {
			line += " down"
		}
		sb.WriteString(line + ";\n")
	}
	sb.WriteString("}\n")
	return sb.String()
}

func groupJSON(g *Group) string {
	raw, _ := json.Marshal(g)
	return string(raw)
}

func readGroup(path string) (*Group, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	for _, line := range strings.Split(string(raw), "\n") {
		line = strings.TrimSpace(line)
		if strings.HasPrefix(line, "# ") && strings.Contains(line, `"name"`) {
			var g Group
			if err := json.Unmarshal([]byte(strings.TrimPrefix(line, "# ")), &g); err == nil {
				return &g, nil
			}
		}
	}
	return nil, fmt.Errorf("lb: fragment %s has no embedded state", path)
}

func (m *Manager) load(name string) (*Group, bool) {
	g, err := readGroup(m.groupPath(name))
	if err != nil {
		return nil, false
	}
	return g, true
}

func (m *Manager) loadOrCreate(name string) *Group {
	if g, ok := m.load(name); ok {
		return g
	}
	return &Group{Name: name}
}

func (m *Manager) target(args map[string]any) (name, host string, port int, err error) {
	name, host = str(args, "name"), str(args, "host")
	if name == "" || host == "" {
		return "", "", 0, fmt.Errorf("lb: requires name= and host=")
	}
	port, ok := toInt(args["port"])
	if !ok || port < 1 || port > 65535 {
		return "", "", 0, fmt.Errorf("lb: port must be an integer in [1, 65535]")
	}
	return name, host, port, nil
}

// auditChange appends one change record to the audit file (best effort:
// audit failure never blocks the fragment change itself).
func (m *Manager) auditChange(verb, name, host string, port int) {
	if m.audit == "" {
		return
	}
	_ = os.MkdirAll(filepath.Dir(m.audit), 0o755)
	f, err := os.OpenFile(m.audit, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o600)
	if err != nil {
		return
	}
	defer f.Close()
	fmt.Fprintf(f, "%s lb %s group=%s backend=%s:%d\n", m.now().UTC().Format(time.RFC3339), verb, name, host, port)
}
