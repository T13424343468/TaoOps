// Package tamperguard implements FR-A5~A7 (eBPF-grade file tamper guard with
// graceful degradation): a Probe abstraction over an eBPF loader and an
// mtime+size polling fallback, TAOOPS_SEC JSONL event output, and the
// "tamper.guard" module command (PRD v1.1 冻结口径; 断言 A5-A7).
//
// Degradation contract (breakpoint-m3-enterprise.md): sandboxes and runners
// without CAP_BPF / /sys/fs/bpf access cannot load an eBPF probe. Guard
// treats a failed load as the expected degraded path — it emits a
// tamper.degraded event and switches to PollWatcher. Degradation is not an
// error state.
package tamperguard

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

// Defaults (env-overridable, knobs concentrated per tao-code-standard 一.5).
const (
	DefaultEventLogPath = "/var/log/taoops-sec/events.log"
	DefaultPollInterval = 5 * time.Second
	DefaultBpfPin       = "/sys/fs/bpf/taoops_tamper"
	ModeEbpf            = "ebpf"
	ModePoll            = "poll"
	ModeDegraded        = "degraded"
	EventDegraded       = "tamper.degraded"
	EventChange         = "tamper.change"
)

// Probe is the pluggable detection surface (A5): any backend that can watch
// paths and emit change events. Start begins watching; Events delivers
// changes; Stop releases resources.
type Probe interface {
	Start() error
	Events() <-chan ChangeEvent
	Stop()
}

// ChangeEvent is one detected modification (A6).
type ChangeEvent struct {
	Path   string `json:"path"`
	Op     string `json:"op"` // write|create|delete
	Source string `json:"source"`
	SeenAt string `json:"seen_at"` // RFC3339
}

// BpfLoader abstracts the kernel-side load step (A5). A real loader pins a
// compiled probe object under /sys/fs/bpf; the abstraction exists so tests
// and constrained environments never touch the kernel.
type BpfLoader interface {
	Load(pinPath string) error
}

// BpfProbe is the eBPF-backed Probe (A5). Load failure returns an error —
// never a panic — and the caller degrades.
type BpfProbe struct {
	PinPath string
	Loader  BpfLoader
	events  chan ChangeEvent
	mu      sync.Mutex
	started bool
}

// NewBpfProbe builds a probe that requires the pin at pinPath to exist and
// the loader to succeed.
func NewBpfProbe(pinPath string, loader BpfLoader) *BpfProbe {
	return &BpfProbe{PinPath: pinPath, Loader: loader, events: make(chan ChangeEvent, 16)}
}

// Start loads the probe; failure returns error (A5).
func (b *BpfProbe) Start() error {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.started {
		return nil
	}
	if err := b.Loader.Load(b.PinPath); err != nil {
		return fmt.Errorf("tamperguard: bpf load %s: %w", b.PinPath, err)
	}
	b.started = true
	return nil
}

// Events exposes the change channel.
func (b *BpfProbe) Events() <-chan ChangeEvent { return b.events }

// Stop releases the probe.
func (b *BpfProbe) Stop() {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.started {
		close(b.events)
		b.started = false
	}
}

// ParsePaths splits TAOOPS_TAMPER_PATHS (comma-separated files or dirs).
func ParsePaths(env string) []string {
	var out []string
	for _, p := range strings.Split(env, ",") {
		p = strings.TrimSpace(p)
		if p != "" {
			out = append(out, p)
		}
	}
	return out
}

// snapshot captures the observable state of one watched path.
type snapshot struct {
	MtimeNs int64
	Size    int64
	Exists  bool
}

func statPath(p string) snapshot {
	fi, err := os.Stat(p)
	if err != nil {
		return snapshot{Exists: false}
	}
	return snapshot{MtimeNs: fi.ModTime().UnixNano(), Size: fi.Size(), Exists: true}
}

// classify compares two snapshots and names the operation.
func classify(prev, cur snapshot) string {
	switch {
	case !prev.Exists && cur.Exists:
		return "create"
	case prev.Exists && !cur.Exists:
		return "delete"
	case prev.MtimeNs != cur.MtimeNs || prev.Size != cur.Size:
		return "write"
	default:
		return ""
	}
}

// PollWatcher is the degraded watcher (A5/A6): mtime+size polling.
type PollWatcher struct {
	Paths    []string
	Interval time.Duration
	Now      func() time.Time
	events   chan ChangeEvent
	mu       sync.Mutex
	prev     map[string]snapshot
	stop     chan struct{}
	stopped  bool
}

// NewPollWatcher builds a watcher over paths.
func NewPollWatcher(paths []string, interval time.Duration) *PollWatcher {
	if interval <= 0 {
		interval = DefaultPollInterval
	}
	return &PollWatcher{
		Paths:    paths,
		Interval: interval,
		Now:      time.Now,
		events:   make(chan ChangeEvent, 64),
		prev:     make(map[string]snapshot, len(paths)),
		stop:     make(chan struct{}),
	}
}

// Start takes the initial snapshot then polls until Stop.
func (w *PollWatcher) Start() error {
	w.mu.Lock()
	for _, p := range w.Paths {
		w.prev[p] = statPath(p)
	}
	w.mu.Unlock()
	go w.loop()
	return nil
}

func (w *PollWatcher) loop() {
	t := time.NewTicker(w.Interval)
	defer t.Stop()
	for {
		select {
		case <-w.stop:
			return
		case <-t.C:
			w.pollOnce()
		}
	}
}

// pollOnce diffs current state against the previous snapshot (A6).
func (w *PollWatcher) pollOnce() {
	w.mu.Lock()
	defer w.mu.Unlock()
	for _, p := range w.Paths {
		cur := statPath(p)
		prev := w.prev[p]
		if op := classify(prev, cur); op != "" {
			w.prev[p] = cur
			w.events <- ChangeEvent{Path: p, Op: op, Source: "poll", SeenAt: w.Now().UTC().Format(time.RFC3339)}
		}
	}
}

// Events exposes the change channel.
func (w *PollWatcher) Events() <-chan ChangeEvent { return w.events }

// Stop halts polling.
func (w *PollWatcher) Stop() {
	w.mu.Lock()
	defer w.mu.Unlock()
	if !w.stopped {
		close(w.stop)
		w.stopped = true
	}
}

// Guard owns the active probe and degrades on load failure (A5).
type Guard struct {
	Paths        []string
	BpfPin       string
	EventLogPath string
	PollInterval time.Duration
	Now          func() time.Time

	mu            sync.Mutex
	mode          string
	active        Probe
	eventsWritten int
}

// NewGuard builds a Guard from the deployment environment.
func NewGuard(paths []string, bpfPin, eventLogPath string, pollInterval time.Duration) *Guard {
	if bpfPin == "" {
		bpfPin = DefaultBpfPin
	}
	if eventLogPath == "" {
		eventLogPath = DefaultEventLogPath
	}
	if pollInterval <= 0 {
		pollInterval = DefaultPollInterval
	}
	return &Guard{Paths: paths, BpfPin: bpfPin, EventLogPath: eventLogPath, PollInterval: pollInterval, Now: time.Now}
}

// Start attempts the eBPF probe; on failure it degrades to PollWatcher and
// emits tamper.degraded (A5). A pin path that does not exist is the expected
// sandbox outcome, not an error.
func (g *Guard) Start() error {
	g.mu.Lock()
	defer g.mu.Unlock()
	probe := NewBpfProbe(g.BpfPin, &pinLoader{})
	if err := probe.Start(); err != nil {
		g.mode = ModeDegraded
		poll := NewPollWatcher(g.Paths, g.PollInterval)
		poll.Now = g.Now
		g.active = poll
		if err := g.appendEventLocked(EventDegraded, "", "guard", map[string]any{"reason": err.Error()}); err != nil {
			return err
		}
		return poll.Start()
	}
	g.mode = ModeEbpf
	g.active = probe
	return nil
}

// pinLoader is the production loader: the pin must exist (real kernel load
// happens out-of-band via bpftool; see breakpoint dossier). Missing pin =
// load failure = degradation.
type pinLoader struct{}

func (l *pinLoader) Load(pinPath string) error {
	if _, err := os.Stat(pinPath); err != nil {
		return fmt.Errorf("pin not available: %w", err)
	}
	return nil
}

// Status reports the current mode, watched paths and event count (A7).
func (g *Guard) Status() map[string]any {
	g.mu.Lock()
	defer g.mu.Unlock()
	return map[string]any{
		"mode":           g.mode,
		"paths":          g.Paths,
		"events_written": g.eventsWritten,
	}
}

// Events exposes the active probe's channel.
func (g *Guard) Events() <-chan ChangeEvent {
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.active == nil {
		return nil
	}
	return g.active.Events()
}

// Stop releases the active probe.
func (g *Guard) Stop() {
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.active != nil {
		g.active.Stop()
		g.active = nil
	}
}

// appendEventLocked writes one TAOOPS_SEC JSONL line; caller holds g.mu.
func (g *Guard) appendEventLocked(event, path, source string, extra map[string]any) error {
	if err := os.MkdirAll(filepath.Dir(g.EventLogPath), 0o700); err != nil {
		return err
	}
	payload := map[string]any{
		"event":  event,
		"ts":     g.Now().UnixMilli(),
		"source": source,
	}
	if path != "" {
		payload["path"] = path
	}
	for k, v := range extra {
		payload[k] = v
	}
	data, err := json.Marshal(payload)
	if err != nil {
		return err
	}
	f, err := os.OpenFile(g.EventLogPath, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o600)
	if err != nil {
		return err
	}
	defer f.Close()
	if _, err := f.Write(append(data, '\n')); err != nil {
		return err
	}
	g.eventsWritten++
	return nil
}

// RecordChange persists one tamper.change event (A6); used by the event pump
// and directly by tests.
func (g *Guard) RecordChange(ev ChangeEvent) error {
	g.mu.Lock()
	defer g.mu.Unlock()
	return g.appendEventLocked(EventChange, ev.Path, ev.Source, map[string]any{"op": ev.Op, "seen_at": ev.SeenAt})
}
