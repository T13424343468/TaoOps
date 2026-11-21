// Package cron implements FR-A6 (scheduled tasks): shell/backup/logrotate
// jobs executed on schedule with per-run logs and success/fail status.
// Crontab access goes through a Runner so tests can fake it; job runs are
// executed locally and logged under LogDir.
package cron

import (
	"bytes"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"taoops/agent/internal/modules/registry"
)

// Runner executes external commands; tests inject fakes.
type Runner func(bin string, args []string, stdin string) (stdout, stderr string, err error)

// Manager manages crontab entries and run logs.
type Manager struct {
	Run      Runner
	LogDir   string
	Now      func() time.Time
	schedule string // cron schedule for run-now marker; tests may pin
}

// NewManager wires a Manager with the real runner.
func NewManager(logDir string) *Manager {
	return &Manager{Run: runExec, LogDir: logDir, Now: time.Now}
}

func runExec(bin string, args []string, stdin string) (string, string, error) {
	cmd := exec.Command(bin, args...)
	if stdin != "" {
		cmd.Stdin = strings.NewReader(stdin)
	}
	var out, errBuf bytes.Buffer
	cmd.Stdout = &out
	cmd.Stderr = &errBuf
	err := cmd.Run()
	return out.String(), errBuf.String(), err
}

// Job is one scheduled task.
type Job struct {
	Name     string `json:"name"`
	Schedule string `json:"schedule"`
	Command  string `json:"command"`
}

const markerPrefix = "# taoops:"

func (m *Manager) readCrontab() (string, error) {
	stdout, stderr, err := m.Run("crontab", []string{"-l"}, "")
	if err != nil {
		// empty crontab exits non-zero on some systems; treat as empty
		if strings.Contains(stderr, "no crontab") {
			return "", nil
		}
		return "", fmt.Errorf("cron: crontab -l failed: %s", stderr)
	}
	return stdout, nil
}

func (m *Manager) writeCrontab(content string) error {
	_, stderr, err := m.Run("crontab", []string{"-"}, content)
	if err != nil {
		return fmt.Errorf("cron: crontab - failed: %s", stderr)
	}
	return nil
}

// List parses taoops-managed jobs from the crontab.
func (m *Manager) List() ([]Job, error) {
	content, err := m.readCrontab()
	if err != nil {
		return nil, err
	}
	var out []Job
	var lastName, lastSchedule, lastCommand string
	for _, line := range strings.Split(content, "\n") {
		t := strings.TrimSpace(line)
		if strings.HasPrefix(t, markerPrefix) {
			name := strings.TrimSpace(strings.TrimPrefix(t, markerPrefix))
			if lastName != "" {
				out = append(out, Job{lastName, lastSchedule, lastCommand})
			}
			lastName, lastSchedule, lastCommand = name, "", ""
			continue
		}
		if t == "" || strings.HasPrefix(t, "#") {
			continue
		}
		fields := strings.SplitN(t, " ", 6)
		if len(fields) < 6 {
			continue
		}
		if lastName != "" && lastSchedule == "" {
			lastSchedule = strings.Join(fields[:5], " ")
			lastCommand = fields[5]
		}
	}
	if lastName != "" {
		out = append(out, Job{lastName, lastSchedule, lastCommand})
	}
	return out, nil
}

// Add inserts a job with a taoops marker, replacing any job of the same name.
func (m *Manager) Add(name, schedule, command string) error {
	if name == "" || schedule == "" || command == "" {
		return fmt.Errorf("cron: name, schedule and command are required")
	}
	content, err := m.readCrontab()
	if err != nil {
		return err
	}
	var kept []string
	for _, line := range strings.Split(content, "\n") {
		t := strings.TrimSpace(line)
		if strings.HasPrefix(t, markerPrefix) && strings.TrimSpace(strings.TrimPrefix(t, markerPrefix)) == name {
			continue // drop old marker
		}
		if strings.HasPrefix(t, markerPrefix) {
			kept = append(kept, line)
			continue
		}
		// drop the command line that followed a dropped marker
		if strings.HasPrefix(t, "#") || t == "" {
			kept = append(kept, line)
			continue
		}
		fields := strings.SplitN(t, " ", 6)
		if len(fields) == 6 && fields[5] == lastCommandOf(content, name) {
			continue
		}
		kept = append(kept, line)
	}
	kept = append(kept, markerPrefix+" "+name, schedule+" "+command)
	return m.writeCrontab(strings.Join(kept, "\n"))
}

func lastCommandOf(content, name string) string {
	lines := strings.Split(content, "\n")
	for i, line := range lines {
		t := strings.TrimSpace(line)
		if strings.HasPrefix(t, markerPrefix) && strings.TrimSpace(strings.TrimPrefix(t, markerPrefix)) == name {
			for j := i + 1; j < len(lines); j++ {
				t2 := strings.TrimSpace(lines[j])
				if t2 != "" && !strings.HasPrefix(t2, "#") {
					return t2
				}
			}
		}
	}
	return "\x00never-matches"
}

// Remove deletes a taoops job by name.
func (m *Manager) Remove(name string) error {
	content, err := m.readCrontab()
	if err != nil {
		return err
	}
	var kept []string
	skipNext := false
	found := false
	for _, line := range strings.Split(content, "\n") {
		t := strings.TrimSpace(line)
		if strings.HasPrefix(t, markerPrefix) {
			if strings.TrimSpace(strings.TrimPrefix(t, markerPrefix)) == name {
				skipNext = true
				found = true
				continue
			}
		}
		if skipNext {
			if t == "" || strings.HasPrefix(t, "#") {
				kept = append(kept, line)
				continue
			}
			skipNext = false
			continue
		}
		kept = append(kept, line)
	}
	if !found {
		return fmt.Errorf("cron: job %q not found", name)
	}
	return m.writeCrontab(strings.Join(kept, "\n"))
}

// RunNow executes a command immediately and records a log file with
// success/fail status (PRD: 任务执行有日志、有成功/失败状态).
func (m *Manager) RunNow(name, command string) (string, error) {
	if err := os.MkdirAll(m.LogDir, 0o755); err != nil {
		return "", err
	}
	stamp := m.Now().Format("20060102-150405")
	logPath := filepath.Join(m.LogDir, fmt.Sprintf("%s-%s.log", sanitize(name), stamp))
	stdout, stderr, err := m.Run("/bin/sh", []string{"-c", command}, "")
	status := "success"
	if err != nil {
		status = "fail"
	}
	log := fmt.Sprintf("time=%s\nstatus=%s\nstdout=%s\nstderr=%s\n", m.Now().Format(time.RFC3339), status, stdout, stderr)
	if werr := os.WriteFile(logPath, []byte(log), 0o644); werr != nil {
		return "", werr
	}
	if err != nil {
		return logPath, fmt.Errorf("cron: job failed: %s", stderr)
	}
	return logPath, nil
}

// ListLogs returns log files for a job, newest first.
func (m *Manager) ListLogs(name string) ([]string, error) {
	entries, err := os.ReadDir(m.LogDir)
	if err != nil {
		if os.IsNotExist(err) {
			return nil, nil
		}
		return nil, err
	}
	prefix := sanitize(name) + "-"
	var out []string
	for _, e := range entries {
		if strings.HasPrefix(e.Name(), prefix) && strings.HasSuffix(e.Name(), ".log") {
			out = append(out, e.Name())
		}
	}
	sort.Sort(sort.Reverse(sort.StringSlice(out)))
	return out, nil
}

func sanitize(s string) string {
	var b strings.Builder
	for _, r := range s {
		switch {
		case r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z', r >= '0' && r <= '9', r == '-', r == '_':
			b.WriteRune(r)
		default:
			b.WriteRune('-')
		}
	}
	return b.String()
}

// Handler returns the registry handler "cron.manage".
func Handler(m *Manager) registry.Handler {
	return func(args map[string]any) (registry.Result, error) {
		op, _ := args["op"].(string)
		name, _ := args["name"].(string)
		switch op {
		case "list":
			jobs, err := m.List()
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "ok", Data: map[string]any{"jobs": jobs}}, nil
		case "add":
			schedule, _ := args["schedule"].(string)
			command, _ := args["command"].(string)
			if err := m.Add(name, schedule, command); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "added " + name}, nil
		case "remove":
			if err := m.Remove(name); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "removed " + name}, nil
		case "run":
			command, _ := args["command"].(string)
			logPath, err := m.RunNow(name, command)
			if err != nil {
				return registry.Result{ExitCode: 1, Output: logPath}, err
			}
			return registry.Result{ExitCode: 0, Output: logPath, Data: map[string]any{"log": logPath}}, nil
		case "logs":
			files, err := m.ListLogs(name)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "ok", Data: map[string]any{"logs": files}}, nil
		}
		return registry.Result{ExitCode: 2}, fmt.Errorf("cron: unknown op %q", op)
	}
}

// Register installs the cron handlers into r.
func Register(r *registry.Registry, m *Manager) error {
	return r.Register("cron.manage", Handler(m))
}
