// Backup policies (FR-A26): named backup jobs with schedule and retain
// rotation. Policies persist to <backupDir>/policies.json; execution is
// dispatched by the server-side scheduler (unified audit + alerting) or
// manually via policy_run.
package backup

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"taoops/agent/internal/modules/registry"
)

// Policy is one scheduled backup job.
type Policy struct {
	ID         string `json:"id"`
	Name       string `json:"name"`
	Source     string `json:"source"`
	Type       string `json:"type"` // full | incremental
	Schedule   string `json:"schedule"`
	Retain     int    `json:"retain"`
	Enabled    bool   `json:"enabled"`
	LastRunMs  int64  `json:"last_run_ms"`
	LastResult string `json:"last_result"`
}

// policiesPath is the JSON store inside the backup directory.
func (m *Manager) policiesPath() string {
	return filepath.Join(m.backupDir, "policies.json")
}

func (m *Manager) loadPolicies() ([]Policy, error) {
	raw, err := os.ReadFile(m.policiesPath())
	if err != nil {
		if os.IsNotExist(err) {
			return []Policy{}, nil
		}
		return nil, err
	}
	var out []Policy
	if err := json.Unmarshal(raw, &out); err != nil {
		return nil, fmt.Errorf("backup: parse policies: %w", err)
	}
	return out, nil
}

func (m *Manager) savePolicies(ps []Policy) error {
	if err := os.MkdirAll(m.backupDir, 0o755); err != nil {
		return err
	}
	raw, err := json.MarshalIndent(ps, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(m.policiesPath(), raw, 0o600)
}

// validateSchedule accepts "interval:<seconds>" (>= 60) or "daily:HH:MM".
func validateSchedule(s string) error {
	switch {
	case strings.HasPrefix(s, "interval:"):
		var sec int
		if _, err := fmt.Sscanf(strings.TrimPrefix(s, "interval:"), "%d", &sec); err != nil || sec < 60 {
			return fmt.Errorf("backup: schedule interval must be >= 60 seconds")
		}
		return nil
	case strings.HasPrefix(s, "daily:"):
		var hh, mm int
		if _, err := fmt.Sscanf(strings.TrimPrefix(s, "daily:"), "%02d:%02d", &hh, &mm); err != nil ||
			hh < 0 || hh > 23 || mm < 0 || mm > 59 {
			return fmt.Errorf("backup: schedule daily must be HH:MM")
		}
		return nil
	}
	return fmt.Errorf("backup: schedule must be interval:<seconds> or daily:HH:MM")
}

func (m *Manager) opPolicyList() (registry.Result, error) {
	ps, err := m.loadPolicies()
	if err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	out, err := json.Marshal(ps)
	if err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	return registry.Result{ExitCode: 0, Output: string(out)}, nil
}

func (m *Manager) opPolicyAdd(args map[string]any) (registry.Result, error) {
	name := str(args, "name")
	source := str(args, "source")
	schedule := str(args, "schedule")
	if name == "" || source == "" || schedule == "" {
		return registry.Result{ExitCode: 2}, fmt.Errorf("backup: policy_add requires name=, source=, schedule=")
	}
	if err := validateSchedule(schedule); err != nil {
		return registry.Result{ExitCode: 2}, err
	}
	ptype := str(args, "type")
	if ptype == "" {
		ptype = "full"
	}
	if ptype != "full" && ptype != "incremental" {
		return registry.Result{ExitCode: 2}, fmt.Errorf("backup: policy type must be full|incremental")
	}
	retain, _ := toInt64(args["retain"])
	if retain <= 0 {
		retain = 7
	}
	ps, err := m.loadPolicies()
	if err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	for _, p := range ps {
		if p.Name == name {
			return registry.Result{ExitCode: 1}, fmt.Errorf("backup: policy %q already exists", name)
		}
	}
	p := Policy{
		ID: fmt.Sprintf("bp-%016x", m.now().UnixNano()), Name: name, Source: source,
		Type: ptype, Schedule: schedule, Retain: int(retain), Enabled: true,
	}
	ps = append(ps, p)
	if err := m.savePolicies(ps); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	return registry.Result{ExitCode: 0, Output: "policy added: " + p.ID}, nil
}

func (m *Manager) opPolicyRemove(args map[string]any) (registry.Result, error) {
	id := str(args, "id")
	if id == "" {
		return registry.Result{ExitCode: 2}, fmt.Errorf("backup: policy_remove requires id=")
	}
	ps, err := m.loadPolicies()
	if err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	out := ps[:0]
	found := false
	for _, p := range ps {
		if p.ID == id {
			found = true
			continue
		}
		out = append(out, p)
	}
	if !found {
		return registry.Result{ExitCode: 1}, fmt.Errorf("backup: policy %q not found", id)
	}
	if err := m.savePolicies(out); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	return registry.Result{ExitCode: 0, Output: "policy removed"}, nil
}

// opPolicyRun executes one policy: create + rotate. Called by the server
// scheduler when due, or manually (Operator+).
func (m *Manager) opPolicyRun(args map[string]any) (registry.Result, error) {
	id := str(args, "id")
	ps, err := m.loadPolicies()
	if err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	idx := -1
	for i, p := range ps {
		if p.ID == id {
			idx = i
			break
		}
	}
	if idx < 0 {
		return registry.Result{ExitCode: 1}, fmt.Errorf("backup: policy %q not found", id)
	}
	p := ps[idx]

	res, err := m.opCreate(map[string]any{
		"name": p.Name, "source": p.Source, "incremental": map[bool]string{true: "true", false: "false"}[p.Type == "incremental"],
	})
	if err != nil {
		ps[idx].LastResult = fmt.Sprintf("failed: %v", err)
		ps[idx].LastRunMs = m.now().UnixMilli()
		_ = m.savePolicies(ps)
		return res, err
	}
	rotateBackupDir(filepath.Join(m.backupDir, p.Name), p.Name, p.Retain)

	ps[idx].LastResult = "ok"
	ps[idx].LastRunMs = m.now().UnixMilli()
	if err := m.savePolicies(ps); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	return res, nil
}

// rotateBackupDir deletes the oldest archives beyond retain. Deletion
// failures never block the current run.
func rotateBackupDir(dir, name string, retain int) {
	if retain <= 0 {
		retain = 7
	}
	matches, err := filepath.Glob(filepath.Join(dir, name+"-*.tar.gz"))
	if err != nil || len(matches) <= retain {
		return
	}
	sort.Strings(matches) // timestamped names sort chronologically
	for _, old := range matches[:len(matches)-retain] {
		_ = os.Remove(old)
		_ = os.Remove(old + ".manifest.json")
	}
}

// Due reports whether a policy should run now, given the wall clock.
// interval: last run + interval <= now; daily: not already run today and
// the scheduled time has passed.
func (p *Policy) Due(now time.Time) bool {
	if !p.Enabled {
		return false
	}
	last := time.UnixMilli(p.LastRunMs)
	switch {
	case strings.HasPrefix(p.Schedule, "interval:"):
		var sec int
		if _, err := fmt.Sscanf(strings.TrimPrefix(p.Schedule, "interval:"), "%d", &sec); err != nil || sec < 60 {
			return false
		}
		return p.LastRunMs == 0 || now.Sub(last) >= time.Duration(sec)*time.Second
	case strings.HasPrefix(p.Schedule, "daily:"):
		var hh, mm int
		if _, err := fmt.Sscanf(strings.TrimPrefix(p.Schedule, "daily:"), "%02d:%02d", &hh, &mm); err != nil {
			return false
		}
		if p.LastRunMs != 0 && last.Format("2006-01-02") == now.Format("2006-01-02") {
			return false
		}
		y, mth, d := now.Date()
		todayAt := time.Date(y, mth, d, hh, mm, 0, 0, now.Location())
		return !now.Before(todayAt)
	}
	return false
}
