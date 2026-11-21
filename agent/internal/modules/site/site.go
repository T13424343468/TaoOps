// Package site implements FR-A2 (website management): site CRUD, domain
// binding and reverse proxy via Nginx-style config files. Every mutating
// operation writes a timestamped backup of the previous config first,
// per PRD acceptance "配置变更自动备份".
package site

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"taoops/agent/internal/modules/registry"
)

// Manager operates on a directory of per-site nginx config files.
type Manager struct {
	ConfDir string
	BackupDir string
	Now     func() time.Time
}

// NewManager wires a Manager over real directories.
func NewManager(confDir, backupDir string) *Manager {
	return &Manager{ConfDir: confDir, BackupDir: backupDir, Now: time.Now}
}

func (m *Manager) sitePath(name string) string {
	return filepath.Join(m.ConfDir, sanitizeName(name)+".conf")
}

func sanitizeName(name string) string {
	var b strings.Builder
	for _, r := range name {
		switch {
		case r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z', r >= '0' && r <= '9', r == '-', r == '_', r == '.':
			b.WriteRune(r)
		default:
			b.WriteRune('-')
		}
	}
	return b.String()
}

// backup copies the current config (if any) to BackupDir before a change.
func (m *Manager) backup(name string) error {
	src := m.sitePath(name)
	if _, err := os.Stat(src); err != nil {
		if os.IsNotExist(err) {
			return nil
		}
		return err
	}
	if err := os.MkdirAll(m.BackupDir, 0o755); err != nil {
		return err
	}
	data, err := os.ReadFile(src)
	if err != nil {
		return err
	}
	stamp := m.Now().Format("20060102-150405")
	dst := filepath.Join(m.BackupDir, fmt.Sprintf("%s.%s.conf", sanitizeName(name), stamp))
	return os.WriteFile(dst, data, 0o644)
}

// Create writes a new site config with server_name and optional reverse
// proxy target. It fails if the site already exists.
func (m *Manager) Create(name string, domains []string, proxyTarget string, port int) error {
	if name == "" || len(domains) == 0 {
		return fmt.Errorf("site: name and at least one domain are required")
	}
	if port <= 0 {
		port = 80
	}
	p := m.sitePath(name)
	if _, err := os.Stat(p); err == nil {
		return fmt.Errorf("site %q already exists", name)
	}
	var b strings.Builder
	fmt.Fprintf(&b, "server {\n    listen %d;\n    server_name %s;\n", port, strings.Join(domains, " "))
	if proxyTarget != "" {
		fmt.Fprintf(&b, "    location / {\n        proxy_pass %s;\n        proxy_set_header Host $host;\n        proxy_set_header X-Real-IP $remote_addr;\n    }\n", proxyTarget)
	} else {
		fmt.Fprintf(&b, "    location / {\n        root /www/%s;\n        index index.html;\n    }\n", sanitizeName(name))
	}
	b.WriteString("}\n")
	if err := os.MkdirAll(m.ConfDir, 0o755); err != nil {
		return err
	}
	return os.WriteFile(p, []byte(b.String()), 0o644)
}

// List returns every site with its domains and proxy target.
func (m *Manager) List() ([]map[string]string, error) {
	entries, err := os.ReadDir(m.ConfDir)
	if err != nil {
		if os.IsNotExist(err) {
			return nil, nil
		}
		return nil, err
	}
	var out []map[string]string
	names := []string{}
	for _, e := range entries {
		if !e.IsDir() && strings.HasSuffix(e.Name(), ".conf") {
			names = append(names, e.Name())
		}
	}
	sort.Strings(names)
	for _, n := range names {
		data, err := os.ReadFile(filepath.Join(m.ConfDir, n))
		if err != nil {
			return nil, err
		}
		out = append(out, map[string]string{
			"name":        strings.TrimSuffix(n, ".conf"),
			"config":      string(data),
			"server_name": extractDirective(string(data), "server_name"),
			"proxy_pass":  extractDirective(string(data), "proxy_pass"),
		})
	}
	return out, nil
}

func extractDirective(cfg, directive string) string {
	for _, line := range strings.Split(cfg, "\n") {
		t := strings.TrimSpace(line)
		if strings.HasPrefix(t, directive+" ") {
			return strings.TrimSuffix(strings.TrimSpace(strings.TrimPrefix(t, directive+" ")), ";")
		}
	}
	return ""
}

// Delete removes a site config after backing it up.
func (m *Manager) Delete(name string) error {
	if err := m.backup(name); err != nil {
		return err
	}
	err := os.Remove(m.sitePath(name))
	if os.IsNotExist(err) {
		return fmt.Errorf("site %q does not exist", name)
	}
	return err
}

// Update rewrites domains/proxy target for an existing site, backing up first.
func (m *Manager) Update(name string, domains []string, proxyTarget string, port int) error {
	if _, err := os.Stat(m.sitePath(name)); err != nil {
		return fmt.Errorf("site %q does not exist", name)
	}
	if err := m.backup(name); err != nil {
		return err
	}
	if err := os.Remove(m.sitePath(name)); err != nil {
		return err
	}
	return m.Create(name, domains, proxyTarget, port)
}

// ListBackups returns backup file names for a site, newest first.
func (m *Manager) ListBackups(name string) ([]string, error) {
	entries, err := os.ReadDir(m.BackupDir)
	if err != nil {
		if os.IsNotExist(err) {
			return nil, nil
		}
		return nil, err
	}
	prefix := sanitizeName(name) + "."
	var out []string
	for _, e := range entries {
		if strings.HasPrefix(e.Name(), prefix) && strings.HasSuffix(e.Name(), ".conf") {
			out = append(out, e.Name())
		}
	}
	sort.Sort(sort.Reverse(sort.StringSlice(out)))
	return out, nil
}

// Handler returns the registry handler "site.manage".
// args: op = create|list|delete|update|backups, plus name/domains/proxy/port.
func Handler(m *Manager) registry.Handler {
	return func(args map[string]any) (registry.Result, error) {
		op, _ := args["op"].(string)
		name, _ := args["name"].(string)
		var res registry.Result
		switch op {
		case "create", "update":
			domains := toStrings(args["domains"])
			proxy, _ := args["proxy"].(string)
			port := toInt(args["port"])
			var err error
			if op == "create" {
				err = m.Create(name, domains, proxy, port)
			} else {
				err = m.Update(name, domains, proxy, port)
			}
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			res = registry.Result{ExitCode: 0, Output: op + " ok"}
		case "delete":
			if err := m.Delete(name); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			res = registry.Result{ExitCode: 0, Output: "deleted " + name}
		case "list":
			sites, err := m.List()
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			res = registry.Result{ExitCode: 0, Output: "ok", Data: map[string]any{"sites": sites}}
		case "backups":
			files, err := m.ListBackups(name)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			res = registry.Result{ExitCode: 0, Output: "ok", Data: map[string]any{"backups": files}}
		default:
			return registry.Result{ExitCode: 2}, fmt.Errorf("site: unknown op %q", op)
		}
		return res, nil
	}
}

// Register installs the site handlers into r.
func Register(r *registry.Registry, m *Manager) error {
	return r.Register("site.manage", Handler(m))
}

func toStrings(v any) []string {
	switch t := v.(type) {
	case []string:
		return t
	case []any:
		out := make([]string, 0, len(t))
		for _, x := range t {
			if s, ok := x.(string); ok {
				out = append(out, s)
			}
		}
		return out
	case string:
		return []string{t}
	}
	return nil
}

func toInt(v any) int {
	switch t := v.(type) {
	case int:
		return t
	case int64:
		return int(t)
	case float64:
		return int(t)
	}
	return 0
}
