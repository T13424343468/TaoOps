package ssl

import (
	"crypto/rand"
	"crypto/rsa"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/json"
	"encoding/pem"
	"errors"
	"math/big"
	"net"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"taoops/agent/internal/modules/registry"
)

var errDial = errors.New("dial failed")

// dialOK is a fake dial that succeeds with a real (pipe) connection so
// Precheck can Close it safely.
func dialOK(network, addr string, timeout time.Duration) (net.Conn, error) {
	client, server := net.Pipe()
	go func() { _ = server.Close() }()
	return client, nil
}

// makeSelfSignedPEM returns a self-signed cert PEM (~1 year validity) and a
// matching key PEM for upload-path tests.
func makeSelfSignedPEM(t *testing.T) (string, string) {
	t.Helper()
	return makeSelfSignedPEMWithValidity(t, 365*24*time.Hour)
}

func makeSelfSignedPEMWithValidity(t *testing.T, validity time.Duration) (string, string) {
	t.Helper()
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	now := time.Now()
	tpl := x509.Certificate{
		Subject:      pkix.Name{CommonName: "test.local"},
		DNSNames:     []string{"test.local"},
		NotBefore:    now.Add(-time.Hour),
		NotAfter:     now.Add(validity),
		KeyUsage:     x509.KeyUsageDigitalSignature | x509.KeyUsageKeyEncipherment,
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
		SerialNumber: big.NewInt(now.UnixNano()),
	}
	der, err := x509.CreateCertificate(rand.Reader, &tpl, &tpl, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	certPem := string(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}))
	keyPem := string(pem.EncodeToMemory(&pem.Block{Type: "RSA PRIVATE KEY", Bytes: x509.MarshalPKCS1PrivateKey(key)}))
	return certPem, keyPem
}

func newTestManager(t *testing.T) (*Manager, string) {
	t.Helper()
	dataDir := t.TempDir()
	m := NewManager(dataDir)
	m.now = func() time.Time { return time.Date(2026, 11, 24, 12, 0, 0, 0, time.UTC) }
	return m, dataDir
}

func runOp(t *testing.T, m *Manager, args map[string]any) registry.Result {
	t.Helper()
	h := m.handle
	res, err := h(args)
	if err != nil {
		t.Fatalf("handler error: %v", err)
	}
	return res
}

func TestPrecheckUnreachable(t *testing.T) {
	m, _ := newTestManager(t)
	m.dial = func(network, addr string, timeout time.Duration) (net.Conn, error) {
		return nil, errDial
	}
	err := m.Precheck("example.com")
	if err == nil || !strings.Contains(err.Error(), PrecheckHint) {
		t.Fatalf("precheck failure must carry guidance: %v", err)
	}
}

func TestPrecheckReachable(t *testing.T) {
	m, _ := newTestManager(t)
	m.dial = dialOK
	if err := m.Precheck("example.com"); err != nil {
		t.Fatalf("precheck should pass: %v", err)
	}
}

func TestIssueRequiresPrecheckAndCertbot(t *testing.T) {
	m, _ := newTestManager(t)
	m.dial = func(network, addr string, timeout time.Duration) (net.Conn, error) {
		return nil, errDial
	}
	res, err := m.handle(map[string]any{"op": "issue", "domain": "example.com", "email": "a@b.c", "webroot": "/var/www"})
	if err == nil || res.ExitCode != 1 || !strings.Contains(err.Error(), PrecheckHint) {
		t.Fatalf("issue must fail when port 80 unreachable: %+v %v", res, err)
	}

	// Reachable but certbot missing → install guidance.
	m.dial = dialOK
	res, err = m.handle(map[string]any{"op": "issue", "domain": "example.com", "email": "a@b.c", "webroot": "/var/www"})
	if err == nil || !strings.Contains(err.Error(), "certbot not installed") {
		t.Fatalf("missing certbot must give install guidance: %v", err)
	}
}

func TestIssueHappyPathRegistersInventory(t *testing.T) {
	m, dataDir := newTestManager(t)
	m.dial = dialOK
	// Fake certbot: succeed without touching the real filesystem; the
	// inventory upsert goes to dataDir, expiry read is best-effort.
	m.run = func(bin string, args []string) (string, string, error) {
		return "obtained", "", nil
	}
	res := runOp(t, m, map[string]any{"op": "issue", "domain": "example.com", "email": "a@b.c", "webroot": "/var/www"})
	if res.ExitCode != 0 {
		t.Fatalf("issue should pass with fake certbot: %+v", res)
	}
	// Inventory must register the letsencrypt entry.
	var entries []Entry
	raw, err := os.ReadFile(filepath.Join(dataDir, "ssl", "certs.json"))
	if err != nil {
		t.Fatal(err)
	}
	if err := json.Unmarshal(raw, &entries); err != nil {
		t.Fatal(err)
	}
	if len(entries) != 1 || entries[0].Issuer != "letsencrypt" || entries[0].Domain != "example.com" {
		t.Fatalf("unexpected inventory: %s", raw)
	}
}

func TestUploadStoresKeyWith600(t *testing.T) {
	m, dataDir := newTestManager(t)
	certPem, _ := makeSelfSignedPEM(t)
	res := runOp(t, m, map[string]any{"op": "upload", "domain": "example.com", "cert_pem": certPem, "key_pem": "KEY"})
	if res.ExitCode != 0 {
		t.Fatalf("upload failed: %+v", res)
	}
	keyPath := filepath.Join(dataDir, "ssl", "example.com.key")
	fi, err := os.Stat(keyPath)
	if err != nil {
		t.Fatal(err)
	}
	if fi.Mode().Perm() != 0o600 {
		t.Fatalf("private key must be 0600, got %v", fi.Mode().Perm())
	}
	var entries []Entry
	raw, _ := os.ReadFile(filepath.Join(dataDir, "ssl", "certs.json"))
	_ = json.Unmarshal(raw, &entries)
	if len(entries) != 1 || entries[0].Issuer != "uploaded" || entries[0].NotAfter == "" {
		t.Fatalf("unexpected inventory: %s", raw)
	}
}

func TestUploadInvalidPem(t *testing.T) {
	m, _ := newTestManager(t)
	res, err := m.handle(map[string]any{"op": "upload", "domain": "x.com", "cert_pem": "junk", "key_pem": "k"})
	if err == nil || res.ExitCode != 2 {
		t.Fatalf("invalid pem must be rejected: %+v %v", res, err)
	}
}

func TestSelfsignKey600AndInventory(t *testing.T) {
	m, dataDir := newTestManager(t)
	res := runOp(t, m, map[string]any{"op": "selfsign", "domain": "offline.local", "days": 365})
	if res.ExitCode != 0 {
		t.Fatalf("selfsign failed: %+v", res)
	}
	keyPath := filepath.Join(dataDir, "ssl", "offline.local.self.key")
	fi, err := os.Stat(keyPath)
	if err != nil {
		t.Fatal(err)
	}
	if fi.Mode().Perm() != 0o600 {
		t.Fatalf("self-signed key must be 0600, got %v", fi.Mode().Perm())
	}
	var entries []Entry
	raw, _ := os.ReadFile(filepath.Join(dataDir, "ssl", "certs.json"))
	_ = json.Unmarshal(raw, &entries)
	if len(entries) != 1 || entries[0].Issuer != "selfsigned" {
		t.Fatalf("unexpected inventory: %s", raw)
	}
	notAfter, err := time.Parse(time.RFC3339, entries[0].NotAfter)
	if err != nil {
		t.Fatalf("bad not_after: %v", err)
	}
	if d := time.Until(notAfter).Hours() / 24; d < 300 || d > 430 {
		t.Fatalf("self-signed validity should be ~365 days: %v", d)
	}
}

func TestRenewSkipsWhenFarFromExpiry(t *testing.T) {
	m, _ := newTestManager(t)
	// Upload a cert valid ~1 year → renew must skip.
	certPem, _ := makeSelfSignedPEM(t)
	runOp(t, m, map[string]any{"op": "upload", "domain": "example.com", "cert_pem": certPem, "key_pem": "KEY"})
	res := runOp(t, m, map[string]any{"op": "renew", "domain": "example.com"})
	if res.ExitCode != 0 || !strings.Contains(res.Output, "skipped") {
		t.Fatalf("renew should skip far-from-expiry certs: %+v", res)
	}
}

func TestRenewRunsCertbotNearExpiry(t *testing.T) {
	m, _ := newTestManager(t)
	// Upload a cert expiring in 10 days.
	certPem, _ := makeSelfSignedPEMWithValidity(t, 10*24*time.Hour)
	runOp(t, m, map[string]any{"op": "upload", "domain": "example.com", "cert_pem": certPem, "key_pem": "KEY"})
	certbotRan := false
	m.run = func(bin string, args []string) (string, string, error) {
		certbotRan = true
		return "renewed", "", nil
	}
	m.dial = dialOK
	res := runOp(t, m, map[string]any{"op": "renew", "domain": "example.com"})
	if res.ExitCode != 0 || !certbotRan {
		t.Fatalf("renew should invoke certbot near expiry: %+v ran=%v", res, certbotRan)
	}
}

func TestRenewPrecheckFailure(t *testing.T) {
	m, _ := newTestManager(t)
	certPem, _ := makeSelfSignedPEMWithValidity(t, 10*24*time.Hour)
	runOp(t, m, map[string]any{"op": "upload", "domain": "example.com", "cert_pem": certPem, "key_pem": "KEY"})
	m.dial = func(network, addr string, timeout time.Duration) (net.Conn, error) { return nil, errDial }
	_, err := m.handle(map[string]any{"op": "renew", "domain": "example.com"})
	if err == nil || !strings.Contains(err.Error(), PrecheckHint) {
		t.Fatalf("renew must precheck port 80: %v", err)
	}
}

func TestListEmpty(t *testing.T) {
	m, _ := newTestManager(t)
	res := runOp(t, m, map[string]any{"op": "list"})
	if res.ExitCode != 0 || !strings.Contains(res.Output, "[]") {
		t.Fatalf("empty list: %+v", res)
	}
}

func TestUnknownOp(t *testing.T) {
	m, _ := newTestManager(t)
	res, err := m.handle(map[string]any{"op": "nope"})
	if err == nil || res.ExitCode != 2 {
		t.Fatalf("unknown op must fail: %+v", err)
	}
}
