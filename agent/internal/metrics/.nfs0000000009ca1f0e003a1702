// Package metrics 采集系统指标（契约 HeartbeatMsg 字段口径）。
package metrics

import (
	"os"
	"runtime"
	"strconv"
	"strings"
)

const AgentVersion = "0.1.0"

// Snapshot 一次采集的心跳指标。
type Snapshot struct {
	Cpu  float64
	Mem  float64
	Disk float64
	Load float64
}

func OsName() string {
	if b, err := os.ReadFile("/etc/os-release"); err == nil {
		for _, line := range strings.Split(string(b), "\n") {
			if v, ok := strings.CutPrefix(line, "PRETTY_NAME="); ok {
				return strings.Trim(v, `"`)
			}
		}
	}
	return runtime.GOOS
}

func Arch() string {
	switch runtime.GOARCH {
	case "amd64":
		return "amd64"
	case "arm64":
		return "arm64"
	default:
		return runtime.GOARCH
	}
}

// Collect 读取 /proc 生成快照；读不到的字段记 0，不阻塞心跳。
func Collect() Snapshot {
	return Snapshot{
		Cpu:  readLoad(),
		Mem:  readMemUsedPct(),
		Disk: readDiskUsedPct(),
		Load: readLoad(),
	}
}

func readLoad() float64 {
	b, err := os.ReadFile("/proc/loadavg")
	if err != nil {
		return 0
	}
	fields := strings.Fields(string(b))
	if len(fields) == 0 {
		return 0
	}
	v, _ := strconv.ParseFloat(fields[0], 64)
	return v
}

func readMemUsedPct() float64 {
	b, err := os.ReadFile("/proc/meminfo")
	if err != nil {
		return 0
	}
	var total, free, available int64
	for _, line := range strings.Split(string(b), "\n") {
		k, v, ok := strings.Cut(line, ":")
		if !ok {
			continue
		}
		fields := strings.Fields(v)
		if len(fields) == 0 {
			continue
		}
		n, _ := strconv.ParseInt(fields[0], 10, 64)
		switch k {
		case "MemTotal":
			total = n
		case "MemFree":
			free = n
		case "MemAvailable":
			available = n
		}
	}
	if total == 0 {
		return 0
	}
	used := total - max(free, available)
	return 100.0 * float64(used) / float64(total)
}

func readDiskUsedPct() float64 {
	b, err := os.ReadFile("/proc/self/mountinfo")
	if err != nil {
		return 0
	}
	_ = b // M1 简化：根分区用量由 statfs 提供，此处先返回 0，M2 接 golang.org/x/sys/unix.Statfs
	return 0
}
