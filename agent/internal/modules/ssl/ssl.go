// Package ssl implements FR-A21: Let's Encrypt ACME (HTTP-01) issuance
// and renewal via certbot, uploaded certificate management, self-signed
// fallback for offline scenarios, and a certificate inventory with
// expiry tracking. Private keys are stored with 0600 permissions.
// Contract source: docs/protocol-contract.md §14.
package ssl

import (
	"crypto/rand"
	"crypto/rsa"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/json"
	"encoding/pem"
	"errors"
	"fmt"
	"math/big"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"

	"taoops/agent/internal/modules/registry"
)

// RenewWindow is how close to expiry auto-renewal kicks in (PRD: 30 days).
const RenewWindow = 30 * 24 * time.Hour

// PrecheckTimeout bounds the port-80 dial.
const PrecheckTimeout = 5 * time.Second

// PrecheckHint is the guidance text returned when port 80 is unreachable
// (PRD: 失败给出明确报错与指引文案).
const PrecheckHint = "80 端口不可达：请检查域名解析是否指向本机、防火墙/安全组是否放行 80 端口、站点 80 端口是否公网可达（Let's Encrypt HTTP-01 挑战要求）"

// Entry is one inventory record in certs.json.
type Entry struct {
	Domain    string `json:"domain"`
	CertPath  string `json:"cert_path"`
	KeyPath   string `json:"key_path,omitempty"`
	Issuer    string `json:"issuer"` // letsencrypt | uploaded | selfsigned
	NotAfter  string `json:"not_after"`
	UpdatedMs int64  `json:"updated_ms"`
}

// Manager manages node-side certificates under dataDir.
type Manager struct {
	dataDir string
	now     func() time.Time
	// run executes external commands (certbot); overridable in tests.
	run  func(bin string, args []string) (stdout, stderr string, err error)
	dial func(network, addr string, timeout time.Duration) (net.Conn, error)
}

// NewManager returns a Manager rooted at dataDir.
func NewManager(dataDir string) *Manager {
	return &Manager{
		dataDir: dataDir,
		now:     time.Now,
		run:     runCmd,
		dial:    net.DialTimeout,
	}
}

// Register installs the "ssl.manage" handler.
func Register(r *registry.Registry, m *Manager) error {
	return r.Register("ssl.manage", registry.Handler(m.handle))
}

func (m *Manager) handle(args map[string]any) (registry.Result, error) {
	op, _ := args["op"].(string)
	switch op {
	case "list":
		return m.opList()
	case "precheck":
		return m.opPrecheck(args)
	case "issue":
		return m.opIssue(args)
	case "renew":
		return m.opRenew(args)
	case "upload":
		return m.opUpload(args)
	case "selfsign":
		return m.opSelfsign(args)
	}
	return registry.Result{ExitCode: 2}, fmt.Errorf("ssl: unknown op %q (want list|precheck|issue|renew|upload|selfsign)", op)
}

func str(args map[string]any, key string) string {
	v, _ := args[key].(string)
	return v
}

func toInt(v any) int {
	switch n := v.(type) {
	case int:
		return n
	case int64:
		return int(n)
	case float64:
		return int(n)
	case string:
		out, err := strconv.Atoi(n)
		if err != nil {
			return 0
		}
		return out
	}
	return 0
}

func (m *Manager) inventoryPath() string { return filepath.Join(m.dataDir, "ssl", "certs.json") }
func (m *Manager) sslDir() string        { return filepath.Join(m.dataDir, "ssl") }

func (m *Manager) loadInventory() ([]Entry, error) {
	raw, err := os.ReadFile(m.inventoryPath())
	if err != nil {
		if os.IsNotExist(err) {
			return nil, nil
		}
		return nil, err
	}
	var entries []Entry
	if err := json.Unmarshal(raw, &entries); err != nil {
		return nil, err
	}
	return entries, nil
}

func (m *Manager) saveInventory(entries []Entry) error {
	if err := os.MkdirAll(m.sslDir(), 0o755); err != nil {
		return err
	}
	out, err := json.MarshalIndent(entries, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(m.inventoryPath(), out, 0o644)
}

func (m *Manager) upsert(entry Entry) error {
	entries, err := m.loadInventory()
	if err != nil {
		return err
	}
	replaced := false
	for i := range entries {
		if entries[i].Domain == entry.Domain {
			entries[i] = entry
			replaced = true
			break
		}
	}
	if !replaced {
		entries = append(entries, entry)
	}
	return m.saveInventory(entries)
}

// Precheck dials domain:80; returns nil when reachable.
func (m *Manager) Precheck(domain string) error {
	if domain == "" {
		return fmt.Errorf("ssl: domain required")
	}
	conn, err := m.dial("tcp", domain+":80", PrecheckTimeout)
	if err != nil {
		return fmt.Errorf("ssl: precheck failed for %s:80: %v — %s", domain, err, PrecheckHint)
	}
	_ = conn.Close()
	return nil
}

func (m *Manager) opList() (registry.Result, error) {
	entries, err := m.loadInventory()
	if err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	if entries == nil {
		entries = []Entry{}
	}
	sort.Slice(entries, func(i, j int) bool { return entries[i].Domain < entries[j].Domain })
	out, _ := json.Marshal(entries)
	// Output carries the JSON payload (dispatch bridge surfaces only Output).
	return registry.Result{ExitCode: 0, Output: string(out), Data: map[string]any{"certs": json.RawMessage(out)}}, nil
}

func (m *Manager) opPrecheck(args map[string]any) (registry.Result, error) {
	domain := str(args, "domain")
	if err := m.Precheck(domain); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	return registry.Result{ExitCode: 0, Output: "80 reachable"}, nil
}

func (m *Manager) opIssue(args map[string]any) (registry.Result, error) {
	domain := str(args, "domain")
	email := str(args, "email")
	webroot := str(args, "webroot")
	if domain == "" || email == "" || webroot == "" {
		return registry.Result{ExitCode: 2}, fmt.Errorf("ssl: issue requires domain=, email=, webroot=")
	}
	if err := m.Precheck(domain); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	stdout, stderr, err := m.run("certbot", []string{
		"certonly", "--webroot", "-w", webroot, "-d", domain,
		"--non-interactive", "--agree-tos", "-m", email,
	})
	if err != nil {
		if isCertbotMissing(err) {
			return registry.Result{ExitCode: 1}, fmt.Errorf("ssl: certbot not installed — 离线包部署或系统包管理器安装 certbot 后重试（如 apt install certbot）")
		}
		return registry.Result{ExitCode: 1}, fmt.Errorf("ssl: issue failed: %s", strings.TrimSpace(stderr))
	}
	certPath := fmt.Sprintf("/etc/letsencrypt/live/%s/fullchain.pem", domain)
	keyPath := fmt.Sprintf("/etc/letsencrypt/live/%s/privkey.pem", domain)
	notAfter, err := m.enforceKeyPerm(keyPath)
	if err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	if err := m.upsert(Entry{Domain: domain, CertPath: certPath, KeyPath: keyPath, Issuer: "letsencrypt", NotAfter: notAfter, UpdatedMs: m.now().UnixMilli()}); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	return registry.Result{ExitCode: 0, Output: firstLine(stdout, "issued " + domain)}, nil
}

func (m *Manager) opRenew(args map[string]any) (registry.Result, error) {
	domain := str(args, "domain")
	if domain == "" {
		return registry.Result{ExitCode: 2}, fmt.Errorf("ssl: renew requires domain=")
	}
	entries, err := m.loadInventory()
	if err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	var entry *Entry
	for i := range entries {
		if entries[i].Domain == domain {
			entry = &entries[i]
			break
		}
	}
	if entry == nil {
		return registry.Result{ExitCode: 2}, fmt.Errorf("ssl: domain %q not in inventory", domain)
	}
	if notAfter, err := m.parseNotAfter(entry.CertPath); err == nil {
		if time.Until(notAfter) > RenewWindow {
			return registry.Result{ExitCode: 0, Output: "skipped: more than 30 days until expiry"}, nil
		}
	}
	if err := m.Precheck(domain); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	_, stderr, err := m.run("certbot", []string{"renew", "--cert-name", domain, "--non-interactive"})
	if err != nil {
		return registry.Result{ExitCode: 1}, fmt.Errorf("ssl: renew failed: %s", strings.TrimSpace(stderr))
	}
	notAfter, err := m.enforceKeyPerm(entry.KeyPath)
	if err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	entry.NotAfter = notAfter
	entry.UpdatedMs = m.now().UnixMilli()
	if err := m.upsert(*entry); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	return registry.Result{ExitCode: 0, Output: "renewed " + domain}, nil
}

func (m *Manager) opUpload(args map[string]any) (registry.Result, error) {
	domain := str(args, "domain")
	certPem := str(args, "cert_pem")
	keyPem := str(args, "key_pem")
	if domain == "" || certPem == "" || keyPem == "" {
		return registry.Result{ExitCode: 2}, fmt.Errorf("ssl: upload requires domain=, cert_pem=, key_pem=")
	}
	notAfter, err := parsePEMNotAfter(certPem)
	if err != nil {
		return registry.Result{ExitCode: 2}, fmt.Errorf("ssl: invalid cert pem: %v", err)
	}
	certPath := filepath.Join(m.sslDir(), domain+".crt")
	keyPath := filepath.Join(m.sslDir(), domain+".key")
	if err := os.MkdirAll(m.sslDir(), 0o755); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	if err := os.WriteFile(certPath, []byte(certPem), 0o644); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	if err := os.WriteFile(keyPath, []byte(keyPem), 0o600); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	if err := m.upsert(Entry{Domain: domain, CertPath: certPath, KeyPath: keyPath, Issuer: "uploaded", NotAfter: notAfter, UpdatedMs: m.now().UnixMilli()}); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	return registry.Result{ExitCode: 0, Output: "uploaded " + domain}, nil
}

func (m *Manager) opSelfsign(args map[string]any) (registry.Result, error) {
	domain := str(args, "domain")
	days := toInt(args["days"])
	if domain == "" {
		return registry.Result{ExitCode: 2}, fmt.Errorf("ssl: selfsign requires domain=")
	}
	if days <= 0 {
		days = 3650
	}
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	tpl := x509.Certificate{
		Subject:     pkix.Name{CommonName: domain},
		DNSNames:    []string{domain},
		NotBefore:   m.now().Add(-time.Hour),
		NotAfter:    m.now().Add(time.Duration(days) * 24 * time.Hour),
		KeyUsage:    x509.KeyUsageDigitalSignature | x509.KeyUsageKeyEncipherment,
		ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
		SerialNumber: big.NewInt(m.now().UnixNano()),
	}
	der, err := x509.CreateCertificate(rand.Reader, &tpl, &tpl, &key.PublicKey, key)
	if err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	if err := os.MkdirAll(m.sslDir(), 0o755); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	certPath := filepath.Join(m.sslDir(), domain+".self.crt")
	keyPath := filepath.Join(m.sslDir(), domain+".self.key")
	if err := os.WriteFile(certPath, pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}), 0o644); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	if err := os.WriteFile(keyPath, pem.EncodeToMemory(&pem.Block{Type: "RSA PRIVATE KEY", Bytes: x509.MarshalPKCS1PrivateKey(key)}), 0o600); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	if err := m.upsert(Entry{Domain: domain, CertPath: certPath, KeyPath: keyPath, Issuer: "selfsigned", NotAfter: tpl.NotAfter.UTC().Format(time.RFC3339), UpdatedMs: m.now().UnixMilli()}); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	return registry.Result{ExitCode: 0, Output: fmt.Sprintf("self-signed %s (%d days)", domain, days)}, nil
}

// enforceKeyPerm tightens the private key to 0600 and returns the cert
// expiry (RFC3339) read from the certificate.
func (m *Manager) enforceKeyPerm(keyPath string) (string, error) {
	if keyPath != "" {
		if fi, err := os.Stat(keyPath); err == nil {
			if fi.Mode().Perm() != 0o600 {
				if err := os.Chmod(keyPath, 0o600); err != nil {
					return "", err
				}
			}
		}
	}
	// Read expiry from the sibling fullchain.pem.
	certPath := filepath.Join(filepath.Dir(keyPath), "fullchain.pem")
	notAfter, err := m.parseNotAfter(certPath)
	if err != nil {
		return "", nil // cert not readable; leave NotAfter empty rather than fail
	}
	return notAfter.UTC().Format(time.RFC3339), nil
}

func (m *Manager) parseNotAfter(certPath string) (time.Time, error) {
	raw, err := os.ReadFile(certPath)
	if err != nil {
		return time.Time{}, err
	}
	return parsePEMNotAfterTime(string(raw))
}

func parsePEMNotAfter(certPem string) (string, error) {
	t, err := parsePEMNotAfterTime(certPem)
	if err != nil {
		return "", err
	}
	return t.UTC().Format(time.RFC3339), nil
}

func parsePEMNotAfterTime(certPem string) (time.Time, error) {
	block, _ := pem.Decode([]byte(certPem))
	if block == nil {
		return time.Time{}, fmt.Errorf("no PEM block found")
	}
	cert, err := x509.ParseCertificate(block.Bytes)
	if err != nil {
		return time.Time{}, err
	}
	return cert.NotAfter, nil
}

func isCertbotMissing(err error) bool {
	var ee *exec.Error
	if errors.As(err, &ee) {
		return errors.Is(ee.Err, exec.ErrNotFound)
	}
	return false
}

func firstLine(s, fallback string) string {
	s = strings.TrimSpace(s)
	if s == "" {
		return fallback
	}
	if i := strings.IndexByte(s, '\n'); i >= 0 {
		return s[:i]
	}
	return s
}

func runCmd(bin string, args []string) (string, string, error) {
	cmd := exec.Command(bin, args...)
	var out, errBuf strings.Builder
	cmd.Stdout = &out
	cmd.Stderr = &errBuf
	err := cmd.Run()
	return out.String(), errBuf.String(), err
}
