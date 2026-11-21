// TLS+TOFU 传输凭据：首连固化中控证书 SHA-256 指纹，此后每次连接校验，
// 不匹配立即拒绝（契约 §1）。TAOOPS_INSECURE=1 仅限本地回环测试。
package agent

import (
	"crypto/tls"
	"crypto/x509"
	"fmt"
	"net"
	"os"
	"path/filepath"
	"strings"
	"sync"

	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/credentials/insecure"
)

// PinStore persists pinned server certificate fingerprints, one
// "host fingerprint" pair per line. Safe for concurrent use.
type PinStore struct {
	path string
	mu   sync.Mutex
	pins map[string]string
}

// NewPinStore loads (without creating) the pin file at path.
func NewPinStore(path string) *PinStore {
	ps := &PinStore{path: path, pins: map[string]string{}}
	data, err := os.ReadFile(path)
	if err == nil {
		for _, line := range strings.Split(string(data), "\n") {
			host, fp, ok := strings.Cut(strings.TrimSpace(line), " ")
			if ok && host != "" && fp != "" {
				ps.pins[host] = fp
			}
		}
	}
	return ps
}

// DefaultPinPath returns the TOFU pin file location (TAOOPS_KNOWN_HOSTS
// override, else ~/.taoops/known_hosts).
func DefaultPinPath() string {
	if p := os.Getenv("TAOOPS_KNOWN_HOSTS"); p != "" {
		return p
	}
	home, err := os.UserHomeDir()
	if err != nil {
		home = "."
	}
	return filepath.Join(home, ".taoops", "known_hosts")
}

// FingerprintOf returns the SHA-256 hex fingerprint of a DER certificate.
func FingerprintOf(der []byte) string {
	return Fingerprint(der)
}

// Verify checks the fingerprint for host; on first sight it pins (TOFU)
// and returns true; on mismatch it returns false.
func (ps *PinStore) Verify(host, fp string) bool {
	ps.mu.Lock()
	defer ps.mu.Unlock()
	known, ok := ps.pins[host]
	if !ok {
		ps.pins[host] = fp
		_ = ps.save()
		return true
	}
	return known == fp
}

func (ps *PinStore) save() error {
	if err := os.MkdirAll(filepath.Dir(ps.path), 0o700); err != nil {
		return err
	}
	var b strings.Builder
	for host, fp := range ps.pins {
		b.WriteString(host + " " + fp + "\n")
	}
	return os.WriteFile(ps.path, []byte(b.String()), 0o600)
}

// PinnedTLSCredentials returns gRPC transport credentials that enforce
// TOFU pinning of the server certificate for the host in serverURL.
// Certificate chain validation is replaced by fingerprint pinning
// (self-hosted, no external CA by design; contract §1).
func PinnedTLSCredentials(serverURL string) (credentials.TransportCredentials, error) {
	host := serverURL
	if h, _, err := net.SplitHostPort(host); err == nil {
		host = h
	} else {
		host = strings.Trim(host, "[]")
	}
	host = strings.Trim(host, "[]")
	if host == "" {
		return nil, fmt.Errorf("pinning: cannot extract host from %q", serverURL)
	}
	ps := NewPinStore(DefaultPinPath())
	cfg := &tls.Config{
		// Fingerprint pinning replaces CA chain validation (TOFU, contract §1).
		InsecureSkipVerify: true,
		VerifyPeerCertificate: func(rawCerts [][]byte, _ [][]*x509.Certificate) error {
			if len(rawCerts) == 0 {
				return fmt.Errorf("pinning: server sent no certificate")
			}
			fp := FingerprintOf(rawCerts[0])
			if !ps.Verify(host, fp) {
				return fmt.Errorf("pinning: fingerprint mismatch for %s: pinned %s got %s",
					host, ps.pins[host], fp)
			}
			return nil
		},
	}
	return credentials.NewTLS(cfg), nil
}

// DialCredentials picks transport credentials for serverURL: plaintext
// when TAOOPS_INSECURE=1 (local loopback only), TLS+TOFU otherwise.
func DialCredentials(serverURL string) (credentials.TransportCredentials, error) {
	if os.Getenv("TAOOPS_INSECURE") == "1" {
		return insecure.NewCredentials(), nil
	}
	return PinnedTLSCredentials(serverURL)
}
