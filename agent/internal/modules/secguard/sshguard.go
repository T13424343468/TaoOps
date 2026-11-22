// Package secguard implements FR-C2 (SSH brute-force detection): parse SSH
// auth-failure log lines, count failures per source IP within a time window,
// and record lockout events as TAOOPS_SEC JSONL lines for server-side ingest
// via the LogStream channel (PRD v1.1 冻结口径).
package secguard

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"
)

// Defaults (env-overridable, knobs concentrated per tao-code-standard 一.5).
const (
	DefaultLogPath      = "/var/log/auth.log"
	DefaultEventLogPath = "/var/log/taoops-sec/events.log"
	DefaultWindow       = 10 * time.Minute
	DefaultThreshold    = 5
)

// Config groups the tunables; tests inject temp paths.
type Config struct {
	LogPath      string        // SSH auth log (TAOOPS_SSH_LOG_PATH)
	EventLogPath string        // TAOOPS_SEC event log (TAOOPS_SEC_LOG_PATH)
	StatePath    string        // active-lockout state JSON
	Window       time.Duration // counting window (TAOOPS_SEC_WINDOW_SECONDS)
	Threshold    int           // failures within window that trigger lockout (TAOOPS_SEC_THRESHOLD)
	Whitelist    []string      // IPs never locked (TAOOPS_SEC_WHITELIST, comma-separated)
}

// Failure is one parsed auth-failure line.
type Failure struct {
	IP string
	Ts time.Time
}

// Lockout is an active lockout record (kept in state, reported to the server).
type Lockout struct {
	IP       string `json:"ip"`
	Failures int    `json:"failures"`
	FirstSeen string `json:"first_seen"`
	LastSeen  string `json:"last_seen"`
	LockedAt  string `json:"locked_at"`
}

// Summary is the scan result payload.
type Summary struct {
	Failures      int        `json:"failures"`
	Lockouts      []Lockout  `json:"lockouts"`
	EventsWritten int        `json:"events_written"`
	WindowSeconds int        `json:"window_seconds"`
	Threshold     int        `json:"threshold"`
	LogPath       string     `json:"log_path"`
	Note          string     `json:"note,omitempty"`
}

// Manager runs scans over a Config; Now is injectable for tests.
type Manager struct {
	Cfg Config
	Now func() time.Time
	mu  sync.Mutex
}

// NewManager wires a Manager with env-driven defaults.
func NewManager(cfg Config) *Manager {
	return &Manager{Cfg: cfg, Now: time.Now}
}

// FromEnv builds the deployment Config.
func FromEnv(statePath string) Config {
	window := DefaultWindow
	if s := os.Getenv("TAOOPS_SEC_WINDOW_SECONDS"); s != "" {
		if n, err := strconv.Atoi(s); err == nil && n > 0 {
			window = time.Duration(n) * time.Second
		}
	}
	threshold := DefaultThreshold
	if s := os.Getenv("TAOOPS_SEC_THRESHOLD"); s != "" {
		if n, err := strconv.Atoi(s); err == nil && n > 0 {
			threshold = n
		}
	}
	whitelist := []string{}
	for _, ip := range strings.Split(os.Getenv("TAOOPS_SEC_WHITELIST"), ",") {
		if ip = strings.TrimSpace(ip); ip != "" {
			whitelist = append(whitelist, ip)
		}
	}
	logPath := os.Getenv("TAOOPS_SSH_LOG_PATH")
	if logPath == "" {
		logPath = DefaultLogPath
	}
	eventPath := os.Getenv("TAOOPS_SEC_LOG_PATH")
	if eventPath == "" {
		eventPath = DefaultEventLogPath
	}
	return Config{
		LogPath:      logPath,
		EventLogPath: eventPath,
		StatePath:    statePath,
		Window:       window,
		Threshold:    threshold,
		Whitelist:    whitelist,
	}
}

var (
	// syslog ("Nov 22 11:00:01", no year → current year) and RFC3339 prefixes both accepted.
	failRe = regexp.MustCompile(`(?:Failed password|Invalid user)` + // auth-failure markers
		`.*?\bfrom\s+([0-9]{1,3}(?:\.[0-9]{1,3}){3})`)
)

// ParseFailures extracts auth failures from log lines whose timestamp falls
// inside [now-window, now]. Unparseable lines are skipped (counted as skipped).
func ParseFailures(lines []string, now time.Time, window time.Duration) (failures []Failure, skipped int) {
	from := now.Add(-window)
	for _, line := range lines {
		m := failRe.FindStringSubmatch(line)
		if m == nil {
			continue
		}
		ts, ok := parseLineTime(line, now)
		if !ok {
			skipped++
			continue
		}
		if ts.Before(from) || ts.After(now) {
			continue
		}
		failures = append(failures, Failure{IP: m[1], Ts: ts})
	}
	return failures, skipped
}

// parseLineTime understands "RFC3339 ..." and "Jan 2 15:04:05 ..." prefixes.
func parseLineTime(line string, now time.Time) (time.Time, bool) {
	if ts, err := time.Parse(time.RFC3339, firstField(line)); err == nil {
		return ts, true
	}
	// syslog: "Nov 22 11:00:01 host ..." — no year, assume current year.
	fields := strings.Fields(line)
	if len(fields) >= 3 {
		stamp := fmt.Sprintf("%s %s %s %d", fields[0], fields[1], fields[2], now.Year())
		if ts, err := time.Parse("Jan 2 15:04:05 2006", stamp); err == nil {
			// Future stamp at year boundary → previous year.
			if ts.After(now.Add(24*time.Hour)) {
				stamp = fmt.Sprintf("%s %s %s %d", fields[0], fields[1], fields[2], now.Year()-1)
				if ts2, err2 := time.Parse("Jan 2 15:04:05 2006", stamp); err2 == nil {
					return ts2, true
				}
			}
			return ts, true
		}
	}
	return time.Time{}, false
}

func firstField(line string) string {
	return strings.Fields(line)[0]
}

func (m *Manager) loadState() map[string]Lockout {
	state := map[string]Lockout{}
	data, err := os.ReadFile(m.Cfg.StatePath)
	if err != nil {
		return state
	}
	_ = json.Unmarshal(data, &state)
	return state
}

func (m *Manager) saveState(state map[string]Lockout) error {
	if err := os.MkdirAll(filepath.Dir(m.Cfg.StatePath), 0o700); err != nil {
		return err
	}
	data, err := json.MarshalIndent(state, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(m.Cfg.StatePath, data, 0o600)
}

func (m *Manager) appendEvent(l Lockout, now time.Time) error {
	if err := os.MkdirAll(filepath.Dir(m.Cfg.EventLogPath), 0o700); err != nil {
		return err
	}
	payload, err := json.Marshal(map[string]any{
		"event":    "ssh_lockout",
		"ip":       l.IP,
		"failures": l.Failures,
		"ts":       now.UnixMilli(),
	})
	if err != nil {
		return err
	}
	f, err := os.OpenFile(m.Cfg.EventLogPath, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o600)
	if err != nil {
		return err
	}
	defer f.Close()
	_, err = f.Write(append(payload, '\n'))
	return err
}

// Scan reads the auth log, counts failures per whitelisted-out IP within the
// window, locks new offenders (threshold), records TAOOPS_SEC events (deduped
// against active lockouts), and persists state.
func (m *Manager) Scan() (Summary, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	now := m.Now()
	cfg := m.Cfg

	data, err := os.ReadFile(cfg.LogPath)
	if err != nil {
		return Summary{}, fmt.Errorf("secguard: read log: %w", err)
	}
	lines := strings.Split(string(data), "\n")
	failures, _ := ParseFailures(lines, now, cfg.Window)

	whitelisted := map[string]bool{}
	for _, ip := range cfg.Whitelist {
		whitelisted[ip] = true
	}
	type agg struct {
		n         int
		firstSeen time.Time
		lastSeen  time.Time
	}
	perIP := map[string]*agg{}
	for _, f := range failures {
		if whitelisted[f.IP] {
			continue
		}
		a := perIP[f.IP]
		if a == nil {
			a = &agg{firstSeen: f.Ts}
			perIP[f.IP] = a
		}
		a.n++
		a.lastSeen = f.Ts
	}

	state := m.loadState()
	summary := Summary{
		WindowSeconds: int(cfg.Window / time.Second),
		Threshold:     cfg.Threshold,
		LogPath:       cfg.LogPath,
	}
	for ip, a := range perIP {
		summary.Failures += a.n
		if a.n < cfg.Threshold {
			continue
		}
		if _, active := state[ip]; active {
			continue // dedupe: already locked, no duplicate event
		}
		l := Lockout{
			IP:        ip,
			Failures:  a.n,
			FirstSeen: a.firstSeen.Format(time.RFC3339),
			LastSeen:  a.lastSeen.Format(time.RFC3339),
			LockedAt:  now.Format(time.RFC3339),
		}
		state[ip] = l
		summary.Lockouts = append(summary.Lockouts, l)
		if err := m.appendEvent(l, now); err != nil {
			return summary, fmt.Errorf("secguard: append event: %w", err)
		}
		summary.EventsWritten++
	}
	if err := m.saveState(state); err != nil {
		return summary, fmt.Errorf("secguard: save state: %w", err)
	}
	return summary, nil
}

// Lockouts lists active lockout records from state.
func (m *Manager) Lockouts() ([]Lockout, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	state := m.loadState()
	out := make([]Lockout, 0, len(state))
	for _, l := range state {
		out = append(out, l)
	}
	return out, nil
}
