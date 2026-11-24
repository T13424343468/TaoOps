// Package logclean implements FR-A17: rule-based log cleanup with
// gzip archiving before deletion, an absolute built-in whitelist
// (/etc, /usr, /bin, dataDir), prefix-match path semantics, day-granular
// mtime aging and 7-day archive expiry.
// Contract source: docs/protocol-contract.md §12.
package logclean

import (
	"compress/gzip"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"taoops/agent/internal/modules/registry"
)

// ArchiveRetention is how long archived files are kept (PRD: 7 days).
const ArchiveRetention = 7 * 24 * time.Hour

// Whitelist is the built-in absolute protected directories; dataDir is
// appended at Manager construction (contract §12).
var Whitelist = []string{"/etc", "/usr", "/bin"}

// Rule is one cleanup rule persisted to <dataDir>/logclean/rules.json.
type Rule struct {
	ID         string `json:"id"`
	Path       string `json:"path"`
	MaxSizeMB  int64  `json:"max_size_mb"` // 0 = unlimited
	MaxAgeDays int    `json:"max_age_days"` // 0 = unlimited
	Enabled    bool   `json:"enabled"`
	CreatedMs  int64  `json:"created_ms"`
}

// CleanedEntry is one audit line in cleaned.jsonl.
type CleanedEntry struct {
	TS       string `json:"ts"`
	RuleID   string `json:"rule_id"`
	Path     string `json:"path"`
	Size     int64  `json:"size"`
	Archived string `json:"archived"`
}

// Manager stores rules under dataDir and archives cleaned files under
// dataDir/logclean/archive.
type Manager struct {
	dataDir string
	now     func() time.Time
	// randomID generates rule ids; overridable in tests.
	randomID func() string
}

// NewManager returns a Manager rooted at dataDir.
func NewManager(dataDir string) *Manager {
	return &Manager{dataDir: dataDir, now: time.Now, randomID: newID}
}

// Register installs the "logclean.manage" handler.
func Register(r *registry.Registry, m *Manager) error {
	return r.Register("logclean.manage", registry.Handler(m.handle))
}

func (m *Manager) handle(args map[string]any) (registry.Result, error) {
	op, _ := args["op"].(string)
	switch op {
	case "list":
		return m.opList()
	case "add":
		return m.opAdd(args)
	case "remove":
		return m.opRemove(args)
	case "run":
		return m.opRun(args)
	}
	return registry.Result{ExitCode: 2}, fmt.Errorf("logclean: unknown op %q (want list|add|remove|run)", op)
}

func str(args map[string]any, key string) string {
	v, _ := args[key].(string)
	return v
}

func toInt64(v any) (int64, bool) {
	switch n := v.(type) {
	case int:
		return int64(n), true
	case int64:
		return n, true
	case float64:
		return int64(n), true
	case string:
		var out int64
		if _, err := fmt.Sscanf(n, "%d", &out); err == nil {
			return out, true
		}
	}
	return 0, false
}

func (m *Manager) rulesPath() string  { return filepath.Join(m.dataDir, "logclean", "rules.json") }
func (m *Manager) logPath() string    { return filepath.Join(m.dataDir, "logclean", "cleaned.jsonl") }
func (m *Manager) archiveDir() string { return filepath.Join(m.dataDir, "logclean", "archive") }

func (m *Manager) whitelist() []string {
	return append(append([]string{}, Whitelist...), m.dataDir)
}

func (m *Manager) loadRules() ([]Rule, error) {
	raw, err := os.ReadFile(m.rulesPath())
	if err != nil {
		if os.IsNotExist(err) {
			return nil, nil
		}
		return nil, err
	}
	var rules []Rule
	if err := json.Unmarshal(raw, &rules); err != nil {
		return nil, err
	}
	return rules, nil
}

func (m *Manager) saveRules(rules []Rule) error {
	if err := os.MkdirAll(filepath.Dir(m.rulesPath()), 0o755); err != nil {
		return err
	}
	out, err := json.MarshalIndent(rules, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(m.rulesPath(), out, 0o644)
}

// validatePath enforces the contract §12 path rules: absolute, not root,
// no overly broad wildcard, no "..", not inside the whitelist.
func validatePath(p string, whitelist []string) error {
	if !filepath.IsAbs(p) {
		return fmt.Errorf("logclean: path must be absolute")
	}
	if strings.Contains(p, "..") {
		return fmt.Errorf("logclean: path must not contain ..")
	}
	clean := filepath.Clean(p)
	if clean == "/" {
		return fmt.Errorf("logclean: root path is not allowed")
	}
	if strings.Contains(clean, "*") {
		// Overly broad wildcard: the glob segment must sit behind at least
		// two concrete directory levels (rejects "/*" and "/var/*").
		segs := strings.Split(clean, "/")
		for i, seg := range segs {
			if strings.Contains(seg, "*") {
				depth := 0
				for _, s := range segs[:i] {
					if s != "" {
						depth++
					}
				}
				if depth < 2 {
					return fmt.Errorf("logclean: wildcard too broad (want at least two concrete directory levels before the glob)")
				}
				break
			}
		}
	}
	for _, w := range whitelist {
		if clean == w || strings.HasPrefix(clean, w+"/") {
			return fmt.Errorf("logclean: path %q is inside protected whitelist %q", clean, w)
		}
	}
	return nil
}

// isWhitelisted checks a resolved file path against the whitelist.
func isWhitelisted(resolved string, whitelist []string) bool {
	for _, w := range whitelist {
		if resolved == w || strings.HasPrefix(resolved, w+"/") {
			return true
		}
	}
	return false
}

func (m *Manager) opList() (registry.Result, error) {
	rules, err := m.loadRules()
	if err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	if rules == nil {
		rules = []Rule{}
	}
	out, _ := json.Marshal(rules)
	return registry.Result{ExitCode: 0, Output: fmt.Sprintf("%d rule(s)", len(rules)), Data: map[string]any{"rules": json.RawMessage(out)}}, nil
}

func (m *Manager) opAdd(args map[string]any) (registry.Result, error) {
	path := str(args, "path")
	if path == "" {
		return registry.Result{ExitCode: 2}, fmt.Errorf("logclean: add requires path=")
	}
	if err := validatePath(path, m.whitelist()); err != nil {
		return registry.Result{ExitCode: 2}, err
	}
	var maxSize, maxAge int64
	if v, ok := toInt64(args["max_size_mb"]); ok {
		maxSize = v
	}
	if v, ok := toInt64(args["max_age_days"]); ok {
		maxAge = v
	}
	if maxSize <= 0 && maxAge <= 0 {
		return registry.Result{ExitCode: 2}, fmt.Errorf("logclean: max_size_mb or max_age_days must be > 0")
	}
	rules, err := m.loadRules()
	if err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	rule := Rule{
		ID: m.randomID(), Path: filepath.Clean(path),
		MaxSizeMB: maxSize, MaxAgeDays: int(maxAge),
		Enabled: true, CreatedMs: m.now().UnixMilli(),
	}
	rules = append(rules, rule)
	if err := m.saveRules(rules); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	return registry.Result{ExitCode: 0, Output: "rule added", Data: map[string]any{"id": rule.ID}}, nil
}

func (m *Manager) opRemove(args map[string]any) (registry.Result, error) {
	id := str(args, "id")
	rules, err := m.loadRules()
	if err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	kept := rules[:0]
	found := false
	for _, r := range rules {
		if r.ID == id {
			found = true
			continue
		}
		kept = append(kept, r)
	}
	if !found {
		return registry.Result{ExitCode: 2}, fmt.Errorf("logclean: rule %q not found", id)
	}
	if err := m.saveRules(kept); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	return registry.Result{ExitCode: 0, Output: "rule removed"}, nil
}

func (m *Manager) opRun(args map[string]any) (registry.Result, error) {
	rules, err := m.loadRules()
	if err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	id := str(args, "id")
	var targets []Rule
	for _, r := range rules {
		if id != "" && r.ID != id {
			continue
		}
		if r.Enabled {
			targets = append(targets, r)
		}
	}
	if id != "" && len(targets) == 0 {
		return registry.Result{ExitCode: 2}, fmt.Errorf("logclean: rule %q not found or disabled", id)
	}
	if err := m.expireArchives(); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	var cleaned []CleanedEntry
	for _, r := range targets {
		entries, err := m.cleanRule(r)
		if err != nil {
			return registry.Result{ExitCode: 1}, fmt.Errorf("logclean: rule %s: %w", r.ID, err)
		}
		cleaned = append(cleaned, entries...)
	}
	if err := m.appendLog(cleaned); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	out, _ := json.Marshal(cleaned)
	if cleaned == nil {
		out = []byte("[]")
	}
	return registry.Result{ExitCode: 0, Output: fmt.Sprintf("%d file(s) cleaned", len(cleaned)), Data: map[string]any{"cleaned": json.RawMessage(out)}}, nil
}

// matches reports whether path hits the rule under prefix semantics with
// day-granular mtime aging and size threshold.
func (m *Manager) matches(rule Rule, path string, info os.FileInfo) bool {
	if !strings.HasPrefix(path, rule.Path) {
		return false
	}
	if rule.MaxSizeMB > 0 && info.Size() < rule.MaxSizeMB<<20 {
		return false
	}
	if rule.MaxAgeDays > 0 {
		ageDays := int(m.now().Sub(info.ModTime()).Hours() / 24)
		if ageDays < rule.MaxAgeDays {
			return false
		}
	}
	return true
}

func (m *Manager) cleanRule(rule Rule) ([]CleanedEntry, error) {
	var entries []CleanedEntry
	// Prefix match: walk the closest existing ancestor directory.
	root := rule.Path
	if strings.Contains(root, "*") {
		root = filepath.Dir(root)
	}
	for {
		if fi, err := os.Stat(root); err == nil && !fi.IsDir() {
			break
		}
		parent := filepath.Dir(root)
		if parent == root {
			break
		}
		if fi, err := os.Stat(parent); err == nil && fi.IsDir() {
			root = parent
			break
		}
		root = parent
	}
	filepath.Walk(root, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() {
			return nil
		}
		resolved, rerr := filepath.EvalSymlinks(path)
		if rerr != nil {
			return nil
		}
		if isWhitelisted(resolved, m.whitelist()) {
			return nil
		}
		if !m.matches(rule, path, info) {
			return nil
		}
		archived, aerr := m.archive(path, info)
		if aerr != nil {
			return aerr
		}
		if oerr := os.Remove(path); oerr != nil {
			return oerr
		}
		entries = append(entries, CleanedEntry{
			TS: m.now().UTC().Format(time.RFC3339), RuleID: rule.ID,
			Path: path, Size: info.Size(), Archived: archived,
		})
		return nil
	})
	return entries, nil
}

// archive gzips src into the day directory and returns the archive path.
func (m *Manager) archive(src string, info os.FileInfo) (string, error) {
	dayDir := filepath.Join(m.archiveDir(), m.now().UTC().Format("20060102"))
	if err := os.MkdirAll(dayDir, 0o755); err != nil {
		return "", err
	}
	dst := filepath.Join(dayDir, filepath.Base(src)+".gz")
	f, err := os.Create(dst)
	if err != nil {
		return "", err
	}
	defer f.Close()
	gz := gzip.NewWriter(f)
	s, err := os.Open(src)
	if err != nil {
		return "", err
	}
	defer s.Close()
	if _, err := io.Copy(gz, s); err != nil {
		return "", err
	}
	if err := gz.Close(); err != nil {
		return "", err
	}
	return dst, nil
}

// expireArchives removes archive day directories older than retention.
func (m *Manager) expireArchives() error {
	dayDirs, err := os.ReadDir(m.archiveDir())
	if err != nil {
		if os.IsNotExist(err) {
			return nil
		}
		return err
	}
	cutoff := m.now().Add(-ArchiveRetention)
	for _, d := range dayDirs {
		ts, err := time.Parse("20060102", d.Name())
		if err != nil {
			continue
		}
		if ts.Before(cutoff) {
			_ = os.RemoveAll(filepath.Join(m.archiveDir(), d.Name()))
		}
	}
	return nil
}

func (m *Manager) appendLog(entries []CleanedEntry) error {
	if len(entries) == 0 {
		return nil
	}
	if err := os.MkdirAll(filepath.Dir(m.logPath()), 0o755); err != nil {
		return err
	}
	f, err := os.OpenFile(m.logPath(), os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		return err
	}
	defer f.Close()
	enc := json.NewEncoder(f)
	for _, e := range entries {
		if err := enc.Encode(e); err != nil {
			return err
		}
	}
	return nil
}

func newID() string {
	// Compact id: unix nano in base36 — unique enough for panel use and
	// deterministic in tests via the randomID hook.
	return strings.ToLower(fmt.Sprintf("%x", time.Now().UnixNano()))
}

// sortRules keeps rule order stable for tests.
func sortRules(rules []Rule) {
	sort.Slice(rules, func(i, j int) bool { return rules[i].ID < rules[j].ID })
}
