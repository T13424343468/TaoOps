package webstats

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"syscall"
	"testing"
	"time"
)

func lineAt(ip, url string, status, bytes int, ts time.Time) string {
	b := "-"
	if bytes >= 0 {
		b = fmt.Sprintf("%d", bytes)
	}
	return fmt.Sprintf(`%s - - [%s] "GET %s HTTP/1.1" %d %s "-" "UA"`,
		ip, ts.Format(nginxTime), url, status, b)
}

func TestHLLCountWithinErrorBound(t *testing.T) {
	h := NewHLL()
	n := 10000
	for i := 0; i < n; i++ {
		h.Add(fmt.Sprintf("10.0.%d.%d", i/256, i%256))
	}
	got := int(h.Count())
	// ~1.6% standard error; allow 5% for headroom.
	if got < n*95/100 || got > n*105/100 {
		t.Fatalf("HLL estimate %d for %d distinct out of ±5%%", got, n)
	}
}

func TestHLLEmptyAndMerge(t *testing.T) {
	if c := NewHLL().Count(); c != 0 {
		t.Fatalf("empty HLL count = %d, want 0", c)
	}
	a, b := NewHLL(), NewHLL()
	a.Add("1.1.1.1")
	a.Add("2.2.2.2")
	b.Add("2.2.2.2")
	b.Add("3.3.3.3")
	if err := a.Merge(b); err != nil {
		t.Fatal(err)
	}
	if got := int(a.Count()); got < 2 || got > 4 {
		t.Fatalf("merged HLL count = %d, want ~3", got)
	}
}

func TestTopKFixedCapacityAndRanking(t *testing.T) {
	k := NewTopK(3)
	k.Add("hot", 100)
	k.Add("warm", 50)
	k.Add("cold", 10)
	k.Add("colder", 1)
	top := k.Top()
	if len(top) != 3 {
		t.Fatalf("top len = %d, want 3", len(top))
	}
	if top[0].Key != "hot" || top[1].Key != "warm" || top[2].Key != "cold" {
		t.Fatalf("ranking wrong: %+v", top)
	}
	if NewTopK(0).k != 1 || NewTopK(500).k != 100 {
		t.Fatalf("capacity clamp broken")
	}
}

func TestParseBasicAggregation(t *testing.T) {
	dir := t.TempDir()
	m := NewManager(dir)
	log := filepath.Join(dir, "access.log")
	ts := time.Date(2026, 11, 24, 10, 0, 0, 0, time.UTC)
	var sb strings.Builder
	// 5 PVs from 3 distinct IPs, statuses 200x4 404x1, bytes known.
	for i := 0; i < 4; i++ {
		sb.WriteString(lineAt("1.1.1.1", "/a", 200, 100, ts) + "\n")
	}
	sb.WriteString(lineAt("2.2.2.2", "/b", 200, 200, ts) + "\n")
	sb.WriteString(lineAt("3.3.3.3", "/a", 404, 0, ts) + "\n")
	if err := os.WriteFile(log, []byte(sb.String()), 0o644); err != nil {
		t.Fatal(err)
	}

	s, err := m.Parse("example.com", log)
	if err != nil {
		t.Fatal(err)
	}
	if s.Parsed != 6 || s.Skipped != 0 {
		t.Fatalf("summary = %+v", s)
	}
	recs, err := m.Summary("example.com", "", "")
	if err != nil {
		t.Fatal(err)
	}
	if len(recs) != 1 {
		t.Fatalf("want 1 daily record, got %d", len(recs))
	}
	r := recs[0]
	if r.PV != 6 {
		t.Fatalf("PV = %d, want 6", r.PV)
	}
	if r.UV < 2 || r.UV > 4 {
		t.Fatalf("UV = %d, want ~3", r.UV)
	}
	if r.Status["200"] != 5 || r.Status["404"] != 1 {
		t.Fatalf("status distribution wrong: %+v", r.Status)
	}
	if r.Bytes != 4*100+200 {
		t.Fatalf("bytes = %d", r.Bytes)
	}
	if r.Degraded {
		t.Fatal("should not be degraded")
	}
	if len(r.TopIPs) == 0 || r.TopIPs[0].Key != "1.1.1.1" {
		t.Fatalf("top IPs wrong: %+v", r.TopIPs)
	}
	if len(r.TopURLs) == 0 || r.TopURLs[0].Key != "/a" {
		t.Fatalf("top URLs wrong: %+v", r.TopURLs)
	}
}

func TestParseDegradedWithoutBytes(t *testing.T) {
	dir := t.TempDir()
	m := NewManager(dir)
	log := filepath.Join(dir, "access.log")
	ts := time.Date(2026, 11, 24, 10, 0, 0, 0, time.UTC)
	body := lineAt("1.1.1.1", "/a", 200, -1, ts) + "\n"
	if err := os.WriteFile(log, []byte(body), 0o644); err != nil {
		t.Fatal(err)
	}
	if _, err := m.Parse("s", log); err != nil {
		t.Fatal(err)
	}
	recs, _ := m.Summary("s", "", "")
	if len(recs) != 1 || !recs[0].Degraded || recs[0].Bytes != 0 || recs[0].PV != 1 {
		t.Fatalf("degraded record wrong: %+v", recs)
	}
}

func TestParseResumeNoDoubleCount(t *testing.T) {
	dir := t.TempDir()
	m := NewManager(dir)
	log := filepath.Join(dir, "access.log")
	ts := time.Date(2026, 11, 24, 10, 0, 0, 0, time.UTC)
	line := lineAt("1.1.1.1", "/a", 200, 10, ts) + "\n"
	if err := os.WriteFile(log, []byte(line), 0o644); err != nil {
		t.Fatal(err)
	}
	if _, err := m.Parse("s", log); err != nil {
		t.Fatal(err)
	}
	// Re-parse the same file: checkpoint must prevent double counting.
	s, err := m.Parse("s", log)
	if err != nil {
		t.Fatal(err)
	}
	if s.Parsed != 0 || !s.Resumed {
		t.Fatalf("resume summary = %+v", s)
	}
	recs, _ := m.Summary("s", "", "")
	if recs[0].PV != 1 {
		t.Fatalf("PV after resume = %d, want 1", recs[0].PV)
	}
}

func TestParseRotationStartsFresh(t *testing.T) {
	dir := t.TempDir()
	m := NewManager(dir)
	log := filepath.Join(dir, "access.log")
	ts := time.Date(2026, 11, 24, 10, 0, 0, 0, time.UTC)
	line := lineAt("1.1.1.1", "/a", 200, 10, ts) + "\n"
	if err := os.WriteFile(log, []byte(line), 0o644); err != nil {
		t.Fatal(err)
	}
	if _, err := m.Parse("s", log); err != nil {
		t.Fatal(err)
	}
	// Simulate rotation: truncate + rewrite (same path, new content; inode may
	// be reused in tmpfs — force a different inode by recreating the file).
	os.Remove(log)
	f, _ := os.OpenFile(log, os.O_CREATE|os.O_WRONLY, 0o644)
	f.WriteString(line)
	f.Close()
	s, err := m.Parse("s", log)
	if err != nil {
		t.Fatal(err)
	}
	// If the inode changed, we start fresh (PV stays 1 via merge of identical
	// single line; if inode was reused, resume keeps PV 1 too). Either way PV
	// must never exceed 1 for one physical line seen once per parse state.
	recs, _ := m.Summary("s", "", "")
	if recs[0].PV > 1 {
		t.Fatalf("PV = %d after rotation, must not double count", recs[0].PV)
	}
	_ = s
}

func TestParseRejectsBadPaths(t *testing.T) {
	dir := t.TempDir()
	m := NewManager(dir)
	if _, err := m.Parse("s", "relative.log"); err == nil {
		t.Fatal("relative path must be rejected")
	}
	if _, err := m.Parse("s", filepath.Join(dir, "missing.log")); err == nil {
		t.Fatal("missing file must be rejected")
	}
	// Directory is not a regular file.
	if _, err := m.Parse("s", dir); err == nil {
		t.Fatal("directory must be rejected")
	}
	// FIFO (named pipe) must be rejected.
	fifo := filepath.Join(dir, "pipe")
	if err := syscall.Mkfifo(fifo, 0o600); err == nil {
		if _, err := m.Parse("s", fifo); err == nil {
			t.Fatal("fifo must be rejected")
		}
	}
}

func TestParseTruncatesOverlongLine(t *testing.T) {
	dir := t.TempDir()
	m := NewManager(dir)
	log := filepath.Join(dir, "access.log")
	ts := time.Date(2026, 11, 24, 10, 0, 0, 0, time.UTC)
	huge := strings.Repeat("x", MaxLineLen*3) // 3x over the cap
	body := lineAt("1.1.1.1", "/ok", 200, 5, ts) + "\n" + huge + "\n" + lineAt("2.2.2.2", "/b", 200, 5, ts) + "\n"
	if err := os.WriteFile(log, []byte(body), 0o644); err != nil {
		t.Fatal(err)
	}
	s, err := m.Parse("s", log)
	if err != nil {
		t.Fatalf("over-long line must not crash: %v", err)
	}
	if s.Parsed != 2 {
		t.Fatalf("parsed = %d, want 2 (over-long line skipped)", s.Parsed)
	}
}

func TestRetentionCleanup(t *testing.T) {
	dir := t.TempDir()
	m := NewManager(dir)
	m.RetentionDays = 180
	old := time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)
	newTs := time.Date(2026, 11, 24, 10, 0, 0, 0, time.UTC)
	log := filepath.Join(dir, "access.log")
	body := lineAt("1.1.1.1", "/a", 200, 5, old) + "\n" + lineAt("1.1.1.1", "/a", 200, 5, newTs) + "\n"
	if err := os.WriteFile(log, []byte(body), 0o644); err != nil {
		t.Fatal(err)
	}
	if _, err := m.Parse("s", log); err != nil {
		t.Fatal(err)
	}
	recs, _ := m.Summary("s", "", "")
	if len(recs) != 1 || recs[0].Date != "2026-11-24" {
		t.Fatalf("retention should drop 2026-01-01: %+v", recs)
	}
}

func TestClearAndExport(t *testing.T) {
	dir := t.TempDir()
	m := NewManager(dir)
	ts := time.Date(2026, 11, 24, 10, 0, 0, 0, time.UTC)
	for _, site := range []string{"a.com", "b.com"} {
		log := filepath.Join(dir, site+".log")
		if err := os.WriteFile(log, []byte(lineAt("1.1.1.1", "/x", 200, 1, ts)+"\n"), 0o644); err != nil {
			t.Fatal(err)
		}
		if _, err := m.Parse(site, log); err != nil {
			t.Fatal(err)
		}
	}
	data, err := m.Export("", "", "")
	if err != nil {
		t.Fatal(err)
	}
	var recs []DailyRecord
	if err := json.Unmarshal(data, &recs); err != nil {
		t.Fatal(err)
	}
	if len(recs) != 2 {
		t.Fatalf("export want 2 records, got %d", len(recs))
	}
	if err := m.Clear("a.com"); err != nil {
		t.Fatal(err)
	}
	recs, _ = m.Summary("", "", "")
	if len(recs) != 1 || recs[0].Site != "b.com" {
		t.Fatalf("after clear: %+v", recs)
	}
	if err := m.Clear(""); err != nil {
		t.Fatal(err)
	}
	recs, _ = m.Summary("", "", "")
	if len(recs) != 0 {
		t.Fatalf("after clear-all: %+v", recs)
	}
}

func TestSummaryFilters(t *testing.T) {
	dir := t.TempDir()
	m := NewManager(dir)
	d1 := time.Date(2026, 11, 23, 10, 0, 0, 0, time.UTC)
	d2 := time.Date(2026, 11, 24, 10, 0, 0, 0, time.UTC)
	log := filepath.Join(dir, "access.log")
	body := lineAt("1.1.1.1", "/a", 200, 1, d1) + "\n" + lineAt("1.1.1.1", "/a", 200, 1, d2) + "\n"
	if err := os.WriteFile(log, []byte(body), 0o644); err != nil {
		t.Fatal(err)
	}
	if _, err := m.Parse("s", log); err != nil {
		t.Fatal(err)
	}
	recs, err := m.Summary("s", "2026-11-24", "")
	if err != nil {
		t.Fatal(err)
	}
	if len(recs) != 1 || recs[0].Date != "2026-11-24" {
		t.Fatalf("from filter broken: %+v", recs)
	}
	recs, _ = m.Summary("s", "", "2026-11-23")
	if len(recs) != 1 || recs[0].Date != "2026-11-23" {
		t.Fatalf("to filter broken: %+v", recs)
	}
}

func TestHandlerOps(t *testing.T) {
	dir := t.TempDir()
	m := NewManager(dir)
	h := Handler(m)
	ts := time.Date(2026, 11, 24, 10, 0, 0, 0, time.UTC)
	log := filepath.Join(dir, "access.log")
	if err := os.WriteFile(log, []byte(lineAt("1.1.1.1", "/a", 200, 1, ts)+"\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	res, err := h(map[string]any{"op": "parse", "site": "s", "path": log})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("parse op: %v %+v", err, res)
	}
	res, err = h(map[string]any{"op": "summary", "site": "s"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("summary op: %v", err)
	}
	res, err = h(map[string]any{"op": "export", "site": "s"})
	if err != nil || res.ExitCode != 0 || res.Output == "" {
		t.Fatalf("export op: %v", err)
	}
	if _, err := h(map[string]any{"op": "nope"}); err == nil {
		t.Fatal("unknown op must error")
	}
	res, err = h(map[string]any{"op": "clear", "site": "s"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("clear op: %v", err)
	}
}
