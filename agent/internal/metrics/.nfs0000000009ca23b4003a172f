package metrics

import "testing"

func TestCollectReturnsNonNegative(t *testing.T) {
	s := Collect()
	if s.Cpu < 0 || s.Mem < 0 || s.Disk < 0 || s.Load < 0 {
		t.Fatalf("negative metric in snapshot: %+v", s)
	}
}

func TestArchIsKnown(t *testing.T) {
	switch Arch() {
	case "amd64", "arm64":
	default:
		t.Fatalf("unexpected arch %q", Arch())
	}
}

func TestOsNameNotEmpty(t *testing.T) {
	if OsName() == "" {
		t.Fatal("os name is empty")
	}
}
