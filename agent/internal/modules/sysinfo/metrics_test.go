package sysinfo

import (
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"testing"
)

func resetPrev() { prevState = prev{} }

// statFixture builds a /proc/stat line with the given total and idle ticks.
func statFixture(total, idle uint64) string {
	// user+nice+system = total-idle, then idle, rest zero.
	user := total - idle
	return fmt.Sprintf("cpu  %d 0 0 %d 0 0 0 0 0 0\ncpu0 %d 0 0 %d 0 0 0 0 0 0\n", user, idle, user, idle)
}

const diskstats = `   7       0 loop0 0 0 0 0 0 0 0 0 0 0 0
   8       0 sda 100 0 200 10 300 0 400 20 0 30 30 0 0 0 0 0
   9       0 ram0 0 0 0 0 0 0 0 0 0 0 0
 253       0 dm-0 5 0 10 1 5 0 10 1 0 2 2 0 0 0 0 0
   8      16 sdb 50 0 60 5 70 0 80 6 0 11 11 0 0 0 0 0
`

func TestMetricsSampleFirstCallZeroRates(t *testing.T) {
	resetPrev()
	c := &Collector{ReadFile: fakeReader(map[string]string{
		"/proc/stat":      statFixture(350, 200),
		"/proc/meminfo":   meminfo,
		"/proc/diskstats": diskstats,
		"/proc/net/dev":   netdev,
	})}
	s, err := MetricsSample(c)
	if err != nil {
		t.Fatalf("MetricsSample: %v", err)
	}
	if s.CPUUsedPct != 0 {
		t.Fatalf("first call CPUUsedPct = %v, want 0", s.CPUUsedPct)
	}
	if s.DiskReadBps != 0 || s.DiskWriteBps != 0 {
		t.Fatalf("first call disk rates = %d/%d, want 0/0", s.DiskReadBps, s.DiskWriteBps)
	}
	if s.MemTotalKB != 16000000 || s.MemUsedKB != 10000000 {
		t.Fatalf("mem wrong: %d/%d", s.MemTotalKB, s.MemUsedKB)
	}
	// Network reports absolute counters on the first call.
	if s.NetRxTotal != 1000 || s.NetTxTotal != 2000 {
		t.Fatalf("net totals wrong: %d/%d", s.NetRxTotal, s.NetTxTotal)
	}
}

func TestMetricsSampleCPUDelta(t *testing.T) {
	resetPrev()
	files := map[string]string{
		"/proc/stat":      statFixture(350, 200),
		"/proc/meminfo":   meminfo,
		"/proc/diskstats": diskstats,
		"/proc/net/dev":   netdev,
	}
	c := &Collector{ReadFile: fakeReader(files)}
	if _, err := MetricsSample(c); err != nil {
		t.Fatalf("first sample: %v", err)
	}
	// Advance: total 350->450 (+100), idle 200->250 (+50) => 50% busy.
	files["/proc/stat"] = statFixture(450, 250)
	s, err := MetricsSample(c)
	if err != nil {
		t.Fatalf("second sample: %v", err)
	}
	if s.CPUUsedPct != 50 {
		t.Fatalf("CPUUsedPct = %v, want 50", s.CPUUsedPct)
	}
}

func TestMetricsSampleDiskIOTicksSkipsVirtualDevices(t *testing.T) {
	resetPrev()
	files := map[string]string{
		"/proc/stat":      statFixture(350, 200),
		"/proc/diskstats": diskstats,
	}
	c := &Collector{ReadFile: fakeReader(files)}
	if _, err := MetricsSample(c); err != nil {
		t.Fatalf("first sample: %v", err)
	}
	// Physical devices only: sda read 200, sdb read 60 => 260 sectors.
	r, w, ok := diskIOTicks(c)
	if !ok {
		t.Fatal("diskIOTicks should succeed")
	}
	if r != 260 || w != 480 { // writes: 400 + 80
		t.Fatalf("diskIOTicks = %d/%d, want 260/480", r, w)
	}
	// Advance both by 10 sectors read / 20 written on sda only.
	files["/proc/diskstats"] = strings.Replace(diskstats,
		"sda 100 0 200 10 300 0 400 20", "sda 100 0 210 10 300 0 420 20", 1)
	s, err := MetricsSample(c)
	if err != nil {
		t.Fatalf("second sample: %v", err)
	}
	if s.DiskReadBps != 10*512 || s.DiskWriteBps != 20*512 {
		t.Fatalf("disk rates = %d/%d, want %d/%d", s.DiskReadBps, s.DiskWriteBps, 10*512, 20*512)
	}
}

func TestMetricsSampleSilentOnMissingFiles(t *testing.T) {
	resetPrev()
	c := &Collector{ReadFile: fakeReader(map[string]string{})}
	s, err := MetricsSample(c)
	if err != nil {
		t.Fatalf("missing /proc files should be silent: %v", err)
	}
	if s.CPUUsedPct != 0 || s.MemTotalKB != 0 || s.NetRxTotal != 0 {
		t.Fatalf("sample should be zeroed: %+v", s)
	}
	if s.Disks == nil {
		t.Fatal("Disks map should be initialized")
	}
}

func TestProcessTopSortsAndTruncates(t *testing.T) {
	resetPrev()
	oldList := listProc
	defer func() { listProc = oldList }()
	listProc = func() ([]string, error) {
		return []string{"123", "notapid", "456", "789"}, nil
	}
	stat := func(pid int, name string, ticks, rssPages uint64) string {
		return fmt.Sprintf("%d (%s) S 1 1 1 0 -1 0 0 0 0 %d %d 0 0 0 0 0 0 0 0 0 %d 0", pid, name, ticks, ticks, rssPages)
	}
	c := &Collector{ReadFile: fakeReader(map[string]string{
		"/proc/123/stat": stat(123, "idle-proc", 10, 100),  // 20 ticks, 400 KB
		"/proc/456/stat": stat(456, "busy-proc", 900, 50),  // 1800 ticks, 200 KB
		"/proc/789/stat": stat(789, "mid-proc", 100, 1000), // 200 ticks, 4000 KB
		// 789 also appears with a name containing spaces and parens
	})}
	top := ProcessTop(c, 2)
	if len(top) != 2 {
		t.Fatalf("len(top) = %d, want 2", len(top))
	}
	if top[0].PID != 456 || top[1].PID != 789 {
		t.Fatalf("order wrong: %+v", top)
	}
	if top[0].CPUPct != 900 || top[0].MemKB != 200 {
		t.Fatalf("top[0] wrong: %+v", top[0])
	}
	if top[1].MemKB != 4000 {
		t.Fatalf("top[1] wrong: %+v", top[1])
	}
	// n=0 defaults to 10.
	all := ProcessTop(c, 0)
	if len(all) != 3 {
		t.Fatalf("len(all) = %d, want 3", len(all))
	}
}

func TestProcessTopParsesNamesWithSpaces(t *testing.T) {
	resetPrev()
	oldList := listProc
	defer func() { listProc = oldList }()
	listProc = func() ([]string, error) { return []string{"42"}, nil }
	c := &Collector{ReadFile: fakeReader(map[string]string{
		"/proc/42/stat": "42 (my web server) R 1 1 1 0 -1 0 0 0 0 5 5 0 0 0 0 0 0 0 0 0 250 0",
	})}
	top := ProcessTop(c, 10)
	if len(top) != 1 || top[0].Name != "my web server" {
		t.Fatalf("name parse wrong: %+v", top)
	}
	if top[0].CPUPct != 5 || top[0].MemKB != 1000 {
		t.Fatalf("fields wrong: %+v", top[0])
	}
}

func TestProcessTopNilOnListFailure(t *testing.T) {
	resetPrev()
	oldList := listProc
	defer func() { listProc = oldList }()
	listProc = func() ([]string, error) { return nil, errors.New("boom") }
	if got := ProcessTop(&Collector{ReadFile: fakeReader(map[string]string{})}, 10); got != nil {
		t.Fatalf("ProcessTop = %+v, want nil", got)
	}
}

func TestHandlerDispatchesMetricsOps(t *testing.T) {
	resetPrev()
	files := map[string]string{
		"/proc/stat":      statFixture(350, 200),
		"/proc/meminfo":   meminfo,
		"/proc/diskstats": diskstats,
		"/proc/net/dev":   netdev,
	}
	c := &Collector{ReadFile: fakeReader(files)}
	h := Handler(c)

	res, err := h(map[string]any{"op": "metrics_sample"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("metrics_sample: res=%+v err=%v", res, err)
	}
	var sample Sample
	if jsonErr := json.Unmarshal([]byte(res.Output), &sample); jsonErr != nil {
		t.Fatalf("metrics_sample output not JSON: %v (%q)", jsonErr, res.Output)
	}
	if sample.MemTotalKB != 16000000 {
		t.Fatalf("sample wrong: %+v", sample)
	}

	oldList := listProc
	defer func() { listProc = oldList }()
	listProc = func() ([]string, error) { return []string{"456"}, nil }
	files["/proc/456/stat"] = "456 (busy-proc) S 1 1 1 0 -1 0 0 0 0 900 0 0 0 0 0 0 0 0 0 50 0"

	res, err = h(map[string]any{"op": "process_top", "n": "1"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("process_top: res=%+v err=%v", res, err)
	}
	var top []ProcTop
	if jsonErr := json.Unmarshal([]byte(res.Output), &top); jsonErr != nil {
		t.Fatalf("process_top output not JSON: %v (%q)", jsonErr, res.Output)
	}
	if len(top) != 1 || top[0].PID != 456 {
		t.Fatalf("process_top data wrong: %+v", top)
	}

	res, err = h(map[string]any{"op": "bogus"})
	if err == nil || res.ExitCode != 2 {
		t.Fatalf("unknown op: res=%+v err=%v", res, err)
	}

	// Empty op keeps overview behavior.
	res, err = h(map[string]any{})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("overview default: res=%+v err=%v", res, err)
	}
}
