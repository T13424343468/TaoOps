package agent

import (
	"strings"
	"testing"
)

func TestNewAgentIDIsUUIDv4Format(t *testing.T) {
	id := NewAgentID()
	if len(id) != 36 {
		t.Fatalf("agent id length = %d, want 36", len(id))
	}
	if strings.Count(id, "-") != 4 {
		t.Fatalf("agent id = %q, want 4 hyphens", id)
	}
	if id[14] != '4' {
		t.Fatalf("agent id version nibble = %c, want 4", id[14])
	}
}

func TestNewAgentIDUnique(t *testing.T) {
	seen := map[string]bool{}
	for i := 0; i < 100; i++ {
		id := NewAgentID()
		if seen[id] {
			t.Fatalf("duplicate agent id %q", id)
		}
		seen[id] = true
	}
}
