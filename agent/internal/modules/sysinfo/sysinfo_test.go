package sysinfo

import (
	"errors"
	"testing"
)

func fakeReader(files map[string]string) FileReader {
	return func(path string) ([]byte, error) {
		if s, ok := files[path]; ok {
			return []byte(s), nil
		}
		return nil, errors.New("missing")
	}
}

const (
	loadavg = "0.10 0.25 0.50 1/233 4321\n"
	meminfo = "MemTotal:       16000000 kB\nMemAvailable:    6000000 kB\nSwapTotal:            0 kB\n"
	cpustat = "cpu  100 0 50 200 0 0 0 0 0 0\ncpu0 100 0 50 200 0 0 0 0 0 0\n"
	netdev  = "Inter-|   Receive\n face |bytes packets errs drop fifo frame compressed multicast\n  eth0: 1000 10 0 0 0 0 0 0 2000 20 0 0 0 0 0 0\n    lo: 1 1 0 0 0 0 0 0 1 1 0 0 0 0 0 0\n"
)

func TestCollectParsesProcFixtures(t *testing.T) {
	c := &Collector{
		ReadFile: fakeReader(map[string]string{
			"/proc/loadavg": loadavg,
			"/proc/meminfo": meminfo,
			"/proc/stat":    cpustat,
			"/proc/net/dev": netdev,
		}),
		Disk: func(path string) (uint64, uint64, error) { return 100, 40, nil },
	}
	out, err := c.Collect()
	if err != nil {
		t.Fatalf("Collect: %v", err)
	}
	load := out["load"].(map[string]string)
	if load["load1"] != "0.10" || load["load15"] != "0.50" {
		t.Fatalf("load parse wrong: %v", load)
	}
	mem := out["memory"].(map[string]uint64)
	if mem["total_kb"] != 16000000 || mem["used_kb"] != 10000000 {
		t.Fatalf("mem parse wrong: %v", mem)
	}
	cpu := out["cpu"].(map[string]uint64)
	if cpu["total_ticks"] != 350 || cpu["idle_ticks"] != 200 {
		t.Fatalf("cpu parse wrong: %v", cpu)
	}
	disk := out["disk"].(map[string]uint64)
	if disk["used_bytes"] != 60 {
		t.Fatalf("disk parse wrong: %v", disk)
	}
	ifaces := out["network"].([]NetIface)
	if len(ifaces) != 1 || ifaces[0].Name != "eth0" || ifaces[0].RxBytes != 1000 || ifaces[0].TxBytes != 2000 {
		t.Fatalf("netdev parse wrong: %+v", ifaces)
	}
}

func TestCollectToleratesMissingProcFiles(t *testing.T) {
	c := &Collector{ReadFile: fakeReader(map[string]string{})}
	out, err := c.Collect()
	if err != nil {
		t.Fatalf("Collect should not fail on missing files: %v", err)
	}
	if _, ok := out["load"]; ok {
		t.Fatal("load should be absent when /proc/loadavg missing")
	}
}

func TestCollectNilReaderFails(t *testing.T) {
	c := &Collector{}
	if _, err := c.Collect(); err == nil {
		t.Fatal("nil ReadFile should error")
	}
}

func TestHandlerWrapsCollectError(t *testing.T) {
	c := &Collector{}
	h := Handler(c)
	res, err := h(nil)
	if err == nil {
		t.Fatal("expected error")
	}
	if res.ExitCode != 1 {
		t.Fatalf("ExitCode = %d, want 1", res.ExitCode)
	}
}

func TestHandlerSuccess(t *testing.T) {
	c := &Collector{ReadFile: fakeReader(map[string]string{"/proc/loadavg": loadavg})}
	h := Handler(c)
	res, err := h(map[string]any{})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("handler: res=%+v err=%v", res, err)
	}
	if res.Data == nil {
		t.Fatal("Data should not be nil on success")
	}
}
