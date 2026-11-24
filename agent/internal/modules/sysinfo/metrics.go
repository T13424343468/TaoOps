// Metrics sampling (FR-A23): one-shot CPU/memory/disk-IO/network sample
// and process Top10, both parsed straight from /proc with no external
// dependencies. Sampling failure is silent (PRD: 采样失败静默) — the
// scheduler leaves a gap instead of storing garbage.
package sysinfo

import (
	"os"
	"sort"
	"strconv"
	"strings"
)

// Sample is one metrics snapshot (PRD FR-A23).
type Sample struct {
	CPUUsedPct   float64           `json:"cpu_used_pct"`
	MemTotalKB   uint64            `json:"mem_total_kb"`
	MemUsedKB    uint64            `json:"mem_used_kb"`
	DiskReadBps  uint64            `json:"disk_read_bps"`
	DiskWriteBps uint64            `json:"disk_write_bps"`
	NetRxTotal   uint64            `json:"net_rx_total"`
	NetTxTotal   uint64            `json:"net_tx_total"`
	Disks        map[string]uint64 `json:"disks,omitempty"`
}

// ProcTop is one row of the process Top10.
type ProcTop struct {
	PID    int     `json:"pid"`
	Name   string  `json:"name"`
	CPUPct float64 `json:"cpu_pct"`
	MemKB  uint64  `json:"mem_kb"`
}

// cpuTicks reads cumulative CPU ticks from /proc/stat.
func cpuTicks(c *Collector) (total, idle uint64, ok bool) {
	b, err := c.ReadFile("/proc/stat")
	if err != nil {
		return 0, 0, false
	}
	m := parseCPU(string(b))
	if m == nil {
		return 0, 0, false
	}
	return m["total_ticks"], m["idle_ticks"], true
}

// diskIOTicks reads cumulative disk IO sector counts from /proc/diskstats
// (sum over physical devices, skip loop/ram devices).
func diskIOTicks(c *Collector) (readSectors, writeSectors uint64, ok bool) {
	b, err := c.ReadFile("/proc/diskstats")
	if err != nil {
		return 0, 0, false
	}
	for _, line := range strings.Split(string(b), "\n") {
		f := strings.Fields(line)
		if len(f) < 14 {
			continue
		}
		name := f[2]
		if strings.HasPrefix(name, "loop") || strings.HasPrefix(name, "ram") || strings.HasPrefix(name, "dm-") {
			continue
		}
		r, _ := strconv.ParseUint(f[5], 10, 64)
		w, _ := strconv.ParseUint(f[9], 10, 64)
		readSectors += r
		writeSectors += w
	}
	return readSectors, writeSectors, true
}

// netTotals sums non-loopback interface byte counters.
func netTotals(c *Collector) (rx, tx uint64, ok bool) {
	b, err := c.ReadFile("/proc/net/dev")
	if err != nil {
		return 0, 0, false
	}
	for _, iface := range parseNetDev(string(b)) {
		rx += iface.RxBytes
		tx += iface.TxBytes
	}
	return rx, tx, true
}

// prev holds the previous sample state for delta computation.
type prev struct {
	cpuTotal, cpuIdle   uint64
	diskRead, diskWrite uint64
	hasCPU, hasDisk     bool
}

var prevState = prev{}

// MetricsSample returns one snapshot. CPU% and disk B/s are deltas since
// the previous call in this process; the first call reports zeros for
// rates but absolute counters for network.
func MetricsSample(c *Collector) (*Sample, error) {
	if c.ReadFile == nil {
		c.ReadFile = osReadFile
	}
	s := &Sample{Disks: map[string]uint64{}}
	if total, idle, ok := cpuTicks(c); ok {
		if prevState.hasCPU {
			dTotal := total - prevState.cpuTotal
			dIdle := idle - prevState.cpuIdle
			if dTotal > 0 {
				s.CPUUsedPct = float64(dTotal-dIdle) / float64(dTotal) * 100
			}
		}
		prevState.cpuTotal, prevState.cpuIdle, prevState.hasCPU = total, idle, true
	}
	if b, err := c.ReadFile("/proc/meminfo"); err == nil {
		m := parseMeminfo(string(b))
		s.MemTotalKB = m["total_kb"]
		s.MemUsedKB = m["used_kb"]
	}
	if r, w, ok := diskIOTicks(c); ok {
		if prevState.hasDisk {
			// sectors are 512 bytes; rate over the poll interval is computed
			// panel-side from consecutive samples — report raw deltas here.
			s.DiskReadBps = (r - prevState.diskRead) * 512
			s.DiskWriteBps = (w - prevState.diskWrite) * 512
		}
		prevState.diskRead, prevState.diskWrite, prevState.hasDisk = r, w, true
	}
	if rx, tx, ok := netTotals(c); ok {
		s.NetRxTotal, s.NetTxTotal = rx, tx
	}
	return s, nil
}

// ProcessTop returns the top-N processes by CPU ticks from /proc.
func ProcessTop(c *Collector, n int) []ProcTop {
	if c.ReadFile == nil {
		c.ReadFile = osReadFile
	}
	if n <= 0 {
		n = 10
	}
	var out []ProcTop
	entries, listErr := listProc()
	if listErr != nil {
		return nil
	}
	for _, e := range entries {
		if !isNumeric(e) {
			continue
		}
		stat, err := c.ReadFile("/proc/" + e + "/stat")
		if err != nil {
			continue
		}
		p := parseProcStat(string(stat))
		if p != nil {
			out = append(out, *p)
		}
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].CPUPct != out[j].CPUPct {
			return out[i].CPUPct > out[j].CPUPct
		}
		return out[i].MemKB > out[j].MemKB
	})
	if len(out) > n {
		out = out[:n]
	}
	return out
}

// listProc lists /proc entries (overridable in tests).
var listProc = func() ([]string, error) {
	es, err := os.ReadDir("/proc")
	if err != nil {
		return nil, err
	}
	out := make([]string, 0, len(es))
	for _, e := range es {
		out = append(out, e.Name())
	}
	return out, nil
}

func isNumeric(s string) bool {
	if s == "" {
		return false
	}
	for _, r := range s {
		if r < '0' || r > '9' {
			return false
		}
	}
	return true
}

// parseProcStat parses one /proc/<pid>/stat line. utime+stime are ticks
// since boot; the panel normalizes to % over the sampling interval.
func parseProcStat(s string) *ProcTop {
	open := strings.Index(s, "(")
	close_ := strings.LastIndex(s, ")")
	if open < 0 || close_ < 0 || close_ <= open {
		return nil
	}
	pid, _ := strconv.Atoi(strings.TrimSpace(s[:open]))
	name := s[open+1 : close_]
	f := strings.Fields(s[close_+1:])
	// fields after comm: state(0) ppid(1) ... utime(11) stime(12) rss(21 in KB pages)
	if len(f) < 22 {
		return nil
	}
	utime, _ := strconv.ParseUint(f[11], 10, 64)
	stime, _ := strconv.ParseUint(f[12], 10, 64)
	rssPages, _ := strconv.ParseUint(f[21], 10, 64)
	return &ProcTop{
		PID:    pid,
		Name:   name,
		CPUPct: float64(utime + stime),
		MemKB:  rssPages * 4,
	}
}
