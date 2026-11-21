// TOFU 固化单测：首连固化、二连校验、指纹不匹配拒绝（M2 验收标准 1）。
package agent

import (
	"crypto/sha256"
	"os"
	"path/filepath"
	"testing"
)

func writePinFile(t *testing.T) string {
	t.Helper()
	return filepath.Join(t.TempDir(), "known_hosts")
}

func TestPinStore_FirstConnectPins(t *testing.T) {
	ps := NewPinStore(writePinFile(t))
	if !ps.Verify("srv1", "aa") {
		t.Fatal("first connect must pin and pass")
	}
	data, err := os.ReadFile(ps.path)
	if err != nil || string(data) != "srv1 aa\n" {
		t.Fatalf("pin file not persisted: %q err=%v", data, err)
	}
}

func TestPinStore_SecondConnectVerifies(t *testing.T) {
	path := writePinFile(t)
	if !NewPinStore(path).Verify("srv1", "aa") {
		t.Fatal("pin failed")
	}
	if !NewPinStore(path).Verify("srv1", "aa") {
		t.Fatal("second connect with same fingerprint must pass")
	}
	if NewPinStore(path).Verify("srv1", "bb") {
		t.Fatal("second connect with different fingerprint must be rejected")
	}
}

func TestPinStore_MismatchSurvivesReload(t *testing.T) {
	path := writePinFile(t)
	ps1 := NewPinStore(path)
	ps1.Verify("srv1", "aa")
	ps1.Verify("srv2", "cc")
	ps2 := NewPinStore(path)
	if ps2.Verify("srv1", "xx") {
		t.Fatal("mismatch after reload must be rejected")
	}
	if !ps2.Verify("srv2", "cc") {
		t.Fatal("known good pin lost after reload")
	}
}

func TestFingerprint_MatchesSha256(t *testing.T) {
	der := []byte("dummy-cert-bytes")
	sum := sha256.Sum256(der)
	want := ""
	for _, b := range sum {
		want += string("0123456789abcdef"[b>>4]) + string("0123456789abcdef"[b&0x0f])
	}
	if got := FingerprintOf(der); got != want {
		t.Fatalf("fingerprint = %s want %s", got, want)
	}
}

func TestPinnedTLSCredentials_ExtractsHost(t *testing.T) {
	t.Setenv("TAOOPS_KNOWN_HOSTS", writePinFile(t))
	for _, addr := range []string{"127.0.0.1:5242", "srv.corp:5242", "[::1]:5242"} {
		if _, err := PinnedTLSCredentials(addr); err != nil {
			t.Fatalf("PinnedTLSCredentials(%q): %v", addr, err)
		}
	}
	if _, err := PinnedTLSCredentials(""); err == nil {
		t.Fatal("empty serverURL must error")
	}
}
