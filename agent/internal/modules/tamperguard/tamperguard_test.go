package tamperguard

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// --- A6: config parsing (table-driven) ---

func TestParsePaths(t *testing.T) {
	cases := []struct {
		name string
		env  string
		want []string
	}{
		{"empty", "", nil},
		{"single", "/etc/passwd", []string{"/etc/passwd"}},
		{"multi", "/etc/passwd, /var/www ,/home", []string{"/etc/passwd", "/var/www", "/home"}},
		{"blanks", " , , ", nil},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got := ParsePaths(tc.env)
			if len(got) != len(tc.want) {
				t.Fatalf("ParsePaths(%q) = %v, want %v", tc.env, got, tc.want)
			}
			for i := range got {
				if got[i] != tc.want[i] {
					t.Fatalf("ParsePaths(%q)[%d] = %q, want %q", tc.env, i, got[i], tc.want[i])
				}
			}
		})
	}
}

// --- A6: change detection (table-driven over operations) ---

func TestPollWatcherDetectsChanges(t *testing.T) {
	dir := t.TempDir()
	target := filepath.Join(dir, "watched.txt")
	if err := os.WriteFile(target, []byte("v1"), 0o600); err != nil {
		t.Fatal(err)
	}
	w := NewPollWatcher([]string{target}, time.Hour) // no background ticks
	w.Now = func() time.Time { return time.Unix(1700000000, 0) }
	if err := w.Start(); err != nil {
		t.Fatal(err)
	}
	// Drain nothing yet: initial snapshot only.
	select {
	case ev := <-w.Events():
		t.Fatalf("unexpected initial event: %+v", ev)
	default:
	}

	cases := []struct {
		name string
		mut  func()
		want string
	}{
		{"write", func() { _ = os.WriteFile(target, []byte("v2-longer"), 0o600) }, "write"},
		{"delete", func() { _ = os.Remove(target) }, "delete"},
		{"create", func() { _ = os.WriteFile(target, []byte("v3"), 0o600) }, "create"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			tc.mut()
			w.pollOnce()
			select {
			case ev := <-w.Events():
				if ev.Op != tc.want {
					t.Fatalf("op = %q, want %q", ev.Op, tc.want)
				}
				if ev.Path != target {
					t.Fatalf("path = %q, want %q", ev.Path, target)
				}
				if ev.Source != "poll" {
					t.Fatalf("source = %q, want poll", ev.Source)
				}
				if _, err := time.Parse(time.RFC3339, ev.SeenAt); err != nil {
					t.Fatalf("seen_at not RFC3339: %q", ev.SeenAt)
				}
			default:
				t.Fatalf("no event for %s", tc.name)
			}
		})
	}
}

func TestPollWatcherNoChangeNoEvent(t *testing.T) {
	dir := t.TempDir()
	target := filepath.Join(dir, "stable.txt")
	if err := os.WriteFile(target, []byte("same"), 0o600); err != nil {
		t.Fatal(err)
	}
	w := NewPollWatcher([]string{target}, time.Hour)
	if err := w.Start(); err != nil {
		t.Fatal(err)
	}
	w.pollOnce()
	select {
	case ev := <-w.Events():
		t.Fatalf("unexpected event without change: %+v", ev)
	default:
	}
}

// --- A5: degradation path ---

func TestGuardDegradesWhenPinMissing(t *testing.T) {
	dir := t.TempDir()
	eventLog := filepath.Join(dir, "sec", "events.log")
	g := NewGuard([]string{filepath.Join(dir, "x")}, filepath.Join(dir, "no-such-pin"), eventLog, time.Hour)
	if err := g.Start(); err != nil {
		t.Fatalf("Start should degrade gracefully, got error: %v", err)
	}
	defer g.Stop()
	st := g.Status()
	if st["mode"] != ModeDegraded {
		t.Fatalf("mode = %v, want degraded", st["mode"])
	}
	// tamper.degraded event must be on the TAOOPS_SEC log.
	raw, err := os.ReadFile(eventLog)
	if err != nil {
		t.Fatal(err)
	}
	var line map[string]any
	if err := json.Unmarshal([]byte(strings.SplitN(strings.TrimSpace(string(raw)), "\n", 2)[0]), &line); err != nil {
		t.Fatal(err)
	}
	if line["event"] != EventDegraded {
		t.Fatalf("first event = %v, want %v", line["event"], EventDegraded)
	}
	if line["reason"] == nil {
		t.Fatal("degraded event missing reason")
	}
}

func TestGuardUsesEbpfWhenPinPresent(t *testing.T) {
	dir := t.TempDir()
	pin := filepath.Join(dir, "pin")
	if err := os.WriteFile(pin, []byte(""), 0o600); err != nil {
		t.Fatal(err)
	}
	g := NewGuard(nil, pin, filepath.Join(dir, "e.log"), time.Hour)
	if err := g.Start(); err != nil {
		t.Fatal(err)
	}
	defer g.Stop()
	if st := g.Status(); st["mode"] != ModeEbpf {
		t.Fatalf("mode = %v, want ebpf", st["mode"])
	}
}

// --- A6: event line format ---

func TestRecordChangeWritesJSONL(t *testing.T) {
	dir := t.TempDir()
	eventLog := filepath.Join(dir, "events.log")
	g := NewGuard(nil, filepath.Join(dir, "p"), eventLog, time.Hour)
	now := time.Unix(1700000000, 0)
	g.Now = func() time.Time { return now }
	if err := g.RecordChange(ChangeEvent{Path: "/etc/hosts", Op: "write", Source: "poll", SeenAt: now.UTC().Format(time.RFC3339)}); err != nil {
		t.Fatal(err)
	}
	if err := g.RecordChange(ChangeEvent{Path: "/etc/hosts", Op: "write", Source: "poll", SeenAt: now.UTC().Format(time.RFC3339)}); err != nil {
		t.Fatal(err)
	}
	raw, err := os.ReadFile(eventLog)
	if err != nil {
		t.Fatal(err)
	}
	lines := strings.Split(strings.TrimSpace(string(raw)), "\n")
	if len(lines) != 2 {
		t.Fatalf("lines = %d, want 2", len(lines))
	}
	var ev map[string]any
	if err := json.Unmarshal([]byte(lines[0]), &ev); err != nil {
		t.Fatal(err)
	}
	if ev["event"] != EventChange || ev["path"] != "/etc/hosts" || ev["op"] != "write" || ev["source"] != "poll" {
		t.Fatalf("event line = %v", ev)
	}
	if _, ok := ev["ts"].(float64); !ok {
		t.Fatalf("ts not numeric: %v", ev["ts"])
	}
	// status counter reflects writes
	if st := g.Status(); st["events_written"] != 2 {
		t.Fatalf("events_written = %v, want 2", st["events_written"])
	}
}

// --- A5: BpfProbe load failure returns error, not panic ---

func TestBpfProbeLoadFailureReturnsError(t *testing.T) {
	p := NewBpfProbe("/no/such/pin", &pinLoader{})
	err := p.Start()
	if err == nil {
		t.Fatal("expected error on missing pin")
	}
	if !strings.Contains(err.Error(), "pin not available") {
		t.Fatalf("error = %v, want pin-not-available cause", err)
	}
}

// --- A7: module command ---

func TestTamperGuardHandlerStatus(t *testing.T) {
	dir := t.TempDir()
	g := NewGuard([]string{"/a", "/b"}, "", filepath.Join(dir, "e.log"), time.Hour)
	h := Handler(g)
	res, err := h(map[string]any{"op": "status"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("status op: res=%+v err=%v", res, err)
	}
	if res.Data["status"] == nil {
		t.Fatal("status payload missing")
	}
	res, err = h(map[string]any{"op": "bogus"})
	if err == nil || res.ExitCode != 2 {
		t.Fatalf("bogus op should exit 2 with error, got %+v %v", res, err)
	}
}

// FR-A17：下发桥仅回传 Output，status 结构化结果必须落 Output JSON（mode/paths/events_written）。
func TestTamperGuardHandlerStatusOutputJSON(t *testing.T) {
	dir := t.TempDir()
	g := NewGuard([]string{"/a", "/b"}, "", filepath.Join(dir, "e.log"), time.Hour)
	h := Handler(g)
	res, err := h(map[string]any{"op": "status"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("status op: res=%+v err=%v", res, err)
	}
	if res.Output == "" {
		t.Fatal("Output empty: FR-A17 要求结构化结果落 Output")
	}
	var parsed map[string]any
	if err := json.Unmarshal([]byte(res.Output), &parsed); err != nil {
		t.Fatalf("Output is not valid JSON: %v (output=%q)", err, res.Output)
	}
	if parsed["mode"] == nil {
		t.Fatal("Output JSON missing mode")
	}
	paths, ok := parsed["paths"].([]any)
	if !ok || len(paths) != 2 {
		t.Fatalf("Output JSON paths = %v, want 2 entries", parsed["paths"])
	}
	if _, ok := parsed["events_written"].(float64); !ok {
		t.Fatalf("Output JSON events_written missing: %v", parsed["events_written"])
	}
}
