// Package sysinfo implements FR-A1 (server overview): CPU, memory, disk,
// load and network stats. Data refresh is served on demand; the panel
// polls every 3 seconds per PRD, so the handler must stay cheap and
// allocation-light.
package sysinfo

import (
	"fmt"
	"os"
	"runtime"
	"strconv"
	"strings"
	"syscall"

	"taoops/agent/internal/modules/registry"
)

// FileReader abstracts /proc reads so tests can inject fixtures.
type FileReader func(path string) ([]byte, error)

func osReadFile(path string) ([]byte, error) { return os.ReadFile(path) }

// DiskUsage abstracts filesystem stat so tests can fake capacity.
type DiskUsage func(path string) (total, free uint64, err error)

func statfsUsage(path string) (total, free uint64, err error) {
	var st syscall.Statfs_t
	if err = syscall.Statfs(path, &st); err != nil {
		return 0, 0, err
	}
	total = st.Blocks * uint64(st.Bsize)
	free = st.Bavail * uint64(st.Bsize)
	return total, free, nil
}

// NetIface is one non-loopback interface's byte counters.
type NetIface struct {
	Name    string `json:"name"`
	RxBytes uint64 `json:"rx_bytes"`
	TxBytes uint64 `json:"tx_bytes"`
}

// Collector gathers system stats with injectable sources.
type Collector struct {
	ReadFile FileReader
	Disk     DiskUsage
	RootPath string
}

// NewCollector returns a Collector backed by the real filesystem.
func NewCollector() *Collector {
	return &Collector{ReadFile: osReadFile, Disk: statfsUsage, RootPath: "/"}
}

// Collect gathers a one-shot overview snapshot.
func (c *Collector) Collect() (map[string]any, error) {
	if c.ReadFile == nil {
		return nil, fmt.Errorf("sysinfo: ReadFile not set")
	}
	out := map[string]any{"os": runtime.GOOS, "arch": runtime.GOARCH}

	if b, err := c.ReadFile("/proc/loadavg"); err == nil {
		out["load"] = parseLoadavg(string(b))
	}
	if b, err := c.ReadFile("/proc/meminfo"); err == nil {
		out["memory"] = parseMeminfo(string(b))
	}
	if b, err := c.ReadFile("/proc/stat"); err == nil {
		out["cpu"] = parseCPU(string(b))
	}
	if b, err := c.ReadFile("/proc/net/dev"); err == nil {
		out["network"] = parseNetDev(string(b))
	}
	if c.Disk != nil {
		if total, free, err := c.Disk(c.RootPath); err == nil {
			out["disk"] = map[string]uint64{
				"total_bytes": total,
				"free_bytes":  free,
				"used_bytes":  total - free,
			}
		}
	}
	return out, nil
}

func parseLoadavg(s string) map[string]string {
	f := strings.Fields(s)
	m := map[string]string{}
	if len(f) >= 1 {
		m["load1"] = f[0]
	}
	if len(f) >= 2 {
		m["load5"] = f[1]
	}
	if len(f) >= 3 {
		m["load15"] = f[2]
	}
	return m
}

func parseMeminfo(s string) map[string]uint64 {
	m := map[string]uint64{}
	for _, line := range strings.Split(s, "\n") {
		parts := strings.Fields(line)
		if len(parts) < 2 {
			continue
		}
		kb, _ := strconv.ParseUint(parts[1], 10, 64)
		switch parts[0] {
		case "MemTotal:":
			m["total_kb"] = kb
		case "MemAvailable:":
			m["available_kb"] = kb
		}
	}
	if m["total_kb"] >= m["available_kb"] {
		m["used_kb"] = m["total_kb"] - m["available_kb"]
	}
	return m
}

func parseCPU(s string) map[string]uint64 {
	for _, line := range strings.Split(s, "\n") {
		if !strings.HasPrefix(line, "cpu ") {
			continue
		}
		f := strings.Fields(line)[1:]
		v := make([]uint64, len(f))
		var total uint64
		for i, x := range f {
			v[i], _ = strconv.ParseUint(x, 10, 64)
			total += v[i]
		}
		var idle uint64
		if len(v) > 3 {
			idle = v[3]
		}
		return map[string]uint64{"total_ticks": total, "idle_ticks": idle}
	}
	return nil
}

func parseNetDev(s string) []NetIface {
	var out []NetIface
	for _, line := range strings.Split(s, "\n") {
		idx := strings.Index(line, ":")
		if idx < 0 {
			continue
		}
		name := strings.TrimSpace(line[:idx])
		if name == "lo" {
			continue
		}
		f := strings.Fields(line[idx+1:])
		if len(f) < 9 {
			continue
		}
		rx, _ := strconv.ParseUint(f[0], 10, 64)
		tx, _ := strconv.ParseUint(f[8], 10, 64)
		out = append(out, NetIface{Name: name, RxBytes: rx, TxBytes: tx})
	}
	return out
}

// Handler returns the registry handler "sysinfo.overview".
func Handler(c *Collector) registry.Handler {
	return func(args map[string]any) (registry.Result, error) {
		data, err := c.Collect()
		if err != nil {
			return registry.Result{ExitCode: 1}, fmt.Errorf("sysinfo: %w", err)
		}
		return registry.Result{ExitCode: 0, Output: "ok", Data: data}, nil
	}
}

// Register installs the sysinfo handlers into r.
func Register(r *registry.Registry, c *Collector) error {
	return r.Register("sysinfo.overview", Handler(c))
}
