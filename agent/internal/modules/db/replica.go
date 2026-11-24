package db

// FR-A15 MySQL replication ops: master_grant / replica_config /
// replica_status / replica_reset (contract §13). Credentials are
// generated with crypto/rand, shown once at creation and redacted from
// every output and error path.

import (
	"crypto/rand"
	"encoding/json"
	"fmt"
	"math/big"
	"os"
	"strings"
	"time"
)

const (
	// ReplicaLagNote documents the panel-side lag threshold (PRD: 300s
	// default, env-configurable on the control plane).
	ReplicaLagNote = "lag threshold enforced panel-side (default 300s)"
	// MonitorInterval is the panel-side replication poll cadence (PRD: 60s).
	MonitorInterval = 60 * time.Second
)

// passwordChars excludes quote/escape-prone characters; 24 chars ≈ 147 bits.
const passwordChars = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#$%^&*-_=+"

// GeneratePassword returns a strong random password (crypto/rand).
func GeneratePassword(n int) (string, error) {
	if n < 12 {
		return "", fmt.Errorf("db: password length must be >= 12")
	}
	out := make([]byte, n)
	max := big.NewInt(int64(len(passwordChars)))
	for i := range out {
		v, err := rand.Int(rand.Reader, max)
		if err != nil {
			return "", err
		}
		out[i] = passwordChars[v.Int64()]
	}
	return string(out), nil
}

// Redact masks credential fields in key=value strings (contract §13:
// protocol args with credential fields are uniformly redacted in audit
// and logs).
func Redact(s string) string {
	fields := strings.Fields(s)
	for i, f := range fields {
		k, _, ok := strings.Cut(f, "=")
		if ok && isCredentialKey(k) {
			fields[i] = k + "=***"
		}
	}
	return strings.Join(fields, " ")
}

func isCredentialKey(k string) bool {
	kl := strings.ToLower(k)
	return strings.Contains(kl, "password") || strings.Contains(kl, "passwd") || strings.Contains(kl, "pwd")
}

// mysqlConfCandidates are the my.cnf paths backed up before replica_config.
var mysqlConfCandidates = []string{"/etc/my.cnf", "/etc/mysql/my.cnf"}

// confCandidates is overridable in tests.
var confCandidates = mysqlConfCandidates

// backupMySQLConf copies the first existing my.cnf to BackupDir with a
// timestamped suffix (same convention as FR-A2 site config backups).
func (m *Manager) backupMySQLConf() (string, error) {
	for _, src := range confCandidates {
		if _, err := os.Stat(src); err != nil {
			if os.IsNotExist(err) {
				continue
			}
			return "", err
		}
		if err := os.MkdirAll(m.BackupDir, 0o755); err != nil {
			return "", err
		}
		dst := fmt.Sprintf("%s/my.cnf.%s.bak", m.BackupDir, m.Now().Format("20060102150405"))
		data, err := os.ReadFile(src)
		if err != nil {
			return "", err
		}
		if err := os.WriteFile(dst, data, 0o600); err != nil {
			return "", err
		}
		return dst, nil
	}
	return "", nil // no my.cnf found; nothing to back up
}

// MasterGrant creates a replication account on the master and returns the
// credentials; the password is surfaced exactly once via Output.
func (m *Manager) MasterGrant(replicaHost string) (string, error) {
	if replicaHost == "" {
		return "", fmt.Errorf("db: replica_host required")
	}
	user := "repl_" + randHex(4)
	pw, err := GeneratePassword(24)
	if err != nil {
		return "", err
	}
	sql := fmt.Sprintf("CREATE USER '%s'@'%s' IDENTIFIED BY '%s'; GRANT REPLICATION SLAVE ON *.* TO '%s'@'%s';",
		user, replicaHost, pw, user, replicaHost)
	if _, _, err := m.Run("mysql", []string{"-e", sql}, ""); err != nil {
		return "", fmt.Errorf("db: master_grant: %s", Redact(err.Error()))
	}
	// Password shown once, never persisted or echoed again.
	return fmt.Sprintf("replication user created: user=%s password=%s (shown once, store it now)", user, pw), nil
}

// ReplicaConfig runs CHANGE MASTER TO on the replica after backing up
// my.cnf; credentials are redacted from all outputs.
func (m *Manager) ReplicaConfig(masterHost, masterPort, masterUser, masterPassword, logFile string, logPos int) (string, error) {
	if masterHost == "" || masterUser == "" || masterPassword == "" {
		return "", fmt.Errorf("db: master_host, master_user and master_password are required")
	}
	if masterPort == "" {
		masterPort = "3306"
	}
	if bak, err := m.backupMySQLConf(); err != nil {
		return "", fmt.Errorf("db: config backup failed: %w", err)
	} else if bak != "" {
		_ = bak // backup path intentionally not echoed with credentials nearby
	}
	var parts []string
	parts = append(parts,
		fmt.Sprintf("MASTER_HOST='%s'", masterHost),
		fmt.Sprintf("MASTER_PORT=%s", masterPort),
		fmt.Sprintf("MASTER_USER='%s'", masterUser),
		fmt.Sprintf("MASTER_PASSWORD='%s'", masterPassword),
	)
	if logFile != "" {
		parts = append(parts, fmt.Sprintf("MASTER_LOG_FILE='%s'", logFile))
	}
	if logPos > 0 {
		parts = append(parts, fmt.Sprintf("MASTER_LOG_POS=%d", logPos))
	}
	sql := "STOP SLAVE; CHANGE MASTER TO " + strings.Join(parts, ", ") + "; START SLAVE;"
	if _, _, err := m.Run("mysql", []string{"-e", sql}, ""); err != nil {
		return "", fmt.Errorf("db: replica_config: %s", Redact(err.Error()))
	}
	return "replica configured (credentials redacted)", nil
}

// ReplicaStatus parses SHOW SLAVE STATUS into a JSON payload.
func (m *Manager) ReplicaStatus() (string, error) {
	out, _, err := m.Run("mysql", []string{"-e", "SHOW SLAVE STATUS\\G"}, "")
	if err != nil {
		return "", fmt.Errorf("db: replica_status: %s", Redact(err.Error()))
	}
	st := parseSlaveStatus(out)
	b, _ := json.Marshal(st)
	return string(b), nil
}

// SlaveStatus is the parsed replication state surfaced to the panel.
type SlaveStatus struct {
	Configured          bool   `json:"configured"`
	IORunning           string `json:"io_running"`
	SQLRunning          string `json:"sql_running"`
	SecondsBehindMaster int64  `json:"seconds_behind_master"`
	MasterHost          string `json:"master_host"`
	LastError           string `json:"last_error"`
}

// parseSlaveStatus reads the \G key: value lines.
func parseSlaveStatus(out string) SlaveStatus {
	st := SlaveStatus{IORunning: "No", SQLRunning: "No"}
	for _, line := range strings.Split(out, "\n") {
		k, v, ok := strings.Cut(strings.TrimSpace(line), ":")
		if !ok {
			continue
		}
		k, v = strings.TrimSpace(k), strings.TrimSpace(v)
		switch k {
		case "Slave_IO_Running":
			st.Configured = true
			st.IORunning = v
		case "Slave_SQL_Running":
			st.Configured = true
			st.SQLRunning = v
		case "Seconds_Behind_Master":
			var n int64
			if _, err := fmt.Sscanf(v, "%d", &n); err == nil {
				st.SecondsBehindMaster = n
			}
		case "Master_Host":
			st.MasterHost = v
		case "Last_Error":
			st.LastError = v
		}
	}
	return st
}

// ReplicaReset stops and clears replication configuration.
func (m *Manager) ReplicaReset() (string, error) {
	if _, _, err := m.Run("mysql", []string{"-e", "STOP SLAVE; RESET SLAVE ALL;"}, ""); err != nil {
		return "", fmt.Errorf("db: replica_reset: %s", Redact(err.Error()))
	}
	return "replication reset", nil
}

func randHex(n int) string {
	b := make([]byte, n)
	_, _ = rand.Read(b)
	return fmt.Sprintf("%x", b)
}

// toInt coerces dispatch arg values (strings from key=value parsing) to int.
func toInt(v any) int {
	switch n := v.(type) {
	case int:
		return n
	case int64:
		return int(n)
	case float64:
		return int(n)
	case string:
		var out int
		if _, err := fmt.Sscanf(n, "%d", &out); err == nil {
			return out
		}
	}
	return 0
}
