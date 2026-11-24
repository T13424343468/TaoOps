// relay.go implements FR-A13 node-to-node backup: source-side async send
// with progress tracking, and target-side chunk assembly with disk
// precheck, per-chunk SHA-256 verification and whole-archive integrity
// confirmation. Contract source: docs/protocol-contract.md §7.
package backup

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"syscall"
	"time"

	"taoops/agent/internal/modules/registry"
)

// RelayChunkSize matches the files module 4MB chunk semantics (contract §7).
const RelayChunkSize int64 = 4 << 20

// Sender streams a local archive to a target node through the control-plane
// relay (gRPC BackupRelay). Implemented by agent/internal/relay.
type Sender interface {
	// Send streams filePath in 4MB chunks to targetNode. onProgress is
	// invoked after each acknowledged chunk with the cumulative byte count.
	Send(ctx context.Context, targetNode, uploadID, name, filePath, source string, onProgress func(sent int64)) error
}

// relayState tracks one in-flight or finished send operation.
type relayState struct {
	ID       string    `json:"relay_id"`
	Target   string    `json:"target_node"`
	UploadID string    `json:"upload_id"`
	File     string    `json:"file"`
	Total    int64     `json:"total_size"`
	Sent     int64     `json:"sent_bytes"`
	Status   string    `json:"status"` // sending | done | failed
	Error    string    `json:"error,omitempty"`
	Started  time.Time `json:"started"`
}

// relaySession is the target-side assembly manifest, persisted as
// <backupDir>/.relay-sessions/<uploadID>/session.json.
type relaySession struct {
	UploadID   string  `json:"upload_id"`
	Name       string  `json:"name"`
	Source     string  `json:"source"`
	TotalSize  int64   `json:"total_size"`
	ChunkCount int64   `json:"chunk_count"`
	FileSHA256 string  `json:"file_sha256"`
	Received   []int64 `json:"received"`
}

func (m *Manager) relayRoot() string {
	return filepath.Join(m.backupDir, ".relay-sessions")
}

// SetSender wires the control-plane relay client; called once at startup.
func (m *Manager) SetSender(s Sender) {
	m.relaySender = s
}

// SetContextFactory supplies the context used by background sends (the
// agent main wires a process-lifetime context here).
func (m *Manager) SetContextFactory(fn func() context.Context) {
	m.relayCtxFn = fn
}

func (m *Manager) relayCtx() context.Context {
	if m.relayCtxFn != nil {
		return m.relayCtxFn()
	}
	return context.Background()
}

func (m *Manager) opSend(args map[string]any) (registry.Result, error) {
	if m.relaySender == nil {
		return registry.Result{ExitCode: 2}, fmt.Errorf("backup: send unavailable: relay sender not wired")
	}
	file := str(args, "file")
	target := str(args, "target_node")
	name := str(args, "name")
	if file == "" || target == "" || name == "" {
		return registry.Result{ExitCode: 2}, fmt.Errorf("backup: send requires file=, target_node= and name=")
	}
	if !filepath.IsAbs(file) {
		return registry.Result{ExitCode: 2}, fmt.Errorf("backup: send file must be an absolute path")
	}
	// 只允许发送备份目录内的档案（与 webstats 绝对路径校验同口径）。
	abs, err := filepath.Abs(file)
	if err != nil {
		return registry.Result{ExitCode: 2}, fmt.Errorf("backup: resolve file: %w", err)
	}
	absBackup, _ := filepath.Abs(m.backupDir)
	if abs != absBackup && !strings.HasPrefix(abs, absBackup+string(filepath.Separator)) {
		return registry.Result{ExitCode: 2}, fmt.Errorf("backup: send file must live under backup dir")
	}
	info, err := os.Stat(abs)
	if err != nil {
		return registry.Result{ExitCode: 1}, fmt.Errorf("backup: stat file: %w", err)
	}
	if !info.Mode().IsRegular() {
		return registry.Result{ExitCode: 2}, fmt.Errorf("backup: send file must be a regular file")
	}

	m.relayMu.Lock()
	if m.activeSend != nil && (m.activeSend.Status == "sending") {
		m.relayMu.Unlock()
		return registry.Result{ExitCode: 2}, fmt.Errorf("backup: another send is in progress (%s)", m.activeSend.ID)
	}
	uploadID := fmt.Sprintf("up-%d", m.now().UnixNano())
	st := &relayState{
		ID:       fmt.Sprintf("rl-%d", m.now().UnixNano()),
		Target:   target,
		UploadID: uploadID,
		File:     abs,
		Total:    info.Size(),
		Status:   "sending",
		Started:  m.now(),
	}
	m.activeSend = st
	m.relayMu.Unlock()

	// 源端原始来源：读档侧 manifest（如有），跨节点还原时目标端据此还原。
	source := ""
	if raw, err := os.ReadFile(abs + ".manifest.json"); err == nil {
		var man Manifest
		if json.Unmarshal(raw, &man) == nil {
			source = man.Source
		}
	}

	go func() {
		err := m.relaySender.Send(m.relayCtx(), target, uploadID, name, abs, source,
			func(sent int64) {
				m.relayMu.Lock()
				st.Sent = sent
				m.relayMu.Unlock()
			})
		m.relayMu.Lock()
		if err != nil {
			st.Status = "failed"
			st.Error = err.Error()
		} else {
			st.Status = "done"
		}
		m.relayMu.Unlock()
	}()

	return registry.Result{
		ExitCode: 0,
		Output:   fmt.Sprintf("relay send started: %s -> %s (upload=%s)", st.ID, target, uploadID),
		Data:     map[string]any{"relay_id": st.ID, "upload_id": uploadID},
	}, nil
}

func (m *Manager) opSendStatus(args map[string]any) (registry.Result, error) {
	id := str(args, "relay_id")
	m.relayMu.Lock()
	defer m.relayMu.Unlock()
	st := m.activeSend
	if st == nil || (id != "" && st.ID != id) {
		return registry.Result{ExitCode: 1}, fmt.Errorf("backup: no relay send found (relay_id=%s)", id)
	}
	return registry.Result{
		ExitCode: 0,
		Output: fmt.Sprintf("relay_id=%s status=%s target=%s sent=%d/%d error=%s",
			st.ID, st.Status, st.Target, st.Sent, st.Total, st.Error),
		Data: map[string]any{"status": st.Status, "sent": st.Sent, "total": st.Total},
	}, nil
}

func (m *Manager) opRelayBegin(args map[string]any) (registry.Result, error) {
	uploadID := str(args, "upload_id")
	name := str(args, "name")
	if uploadID == "" || name == "" {
		return registry.Result{ExitCode: 2}, fmt.Errorf("backup: relay_begin requires upload_id= and name=")
	}
	totalSize, _ := toInt64(args["total_size"])
	chunkCount, _ := toInt64(args["chunk_count"])
	fileSHA := str(args, "file_sha256")
	source := str(args, "source")
	if totalSize <= 0 || chunkCount <= 0 || fileSHA == "" {
		return registry.Result{ExitCode: 2}, fmt.Errorf("backup: relay_begin requires total_size=, chunk_count= and file_sha256=")
	}

	// 磁盘预检：剩余空间 < 声明大小即拒收（非写满才失败）。
	if err := m.precheckDisk(totalSize); err != nil {
		return registry.Result{ExitCode: 1}, err
	}

	dir := m.sessionDir(uploadID)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return registry.Result{ExitCode: 1}, fmt.Errorf("backup: relay session mkdir: %w", err)
	}
	sess := &relaySession{
		UploadID: uploadID, Name: name, Source: source,
		TotalSize: totalSize, ChunkCount: chunkCount, FileSHA256: fileSHA,
	}
	if err := m.writeSession(dir, sess); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	return registry.Result{ExitCode: 0, Output: fmt.Sprintf("relay session opened: %s (%d bytes, %d chunks)", uploadID, totalSize, chunkCount)}, nil
}

func (m *Manager) opRelayPut(args map[string]any) (registry.Result, error) {
	uploadID := str(args, "upload_id")
	if uploadID == "" {
		return registry.Result{ExitCode: 2}, fmt.Errorf("backup: relay_put requires upload_id=")
	}
	idx, _ := toInt64(args["chunk_index"])
	wantSHA := str(args, "chunk_sha256")
	blob, _ := args["__blob"].([]byte)
	if blob == nil {
		return registry.Result{ExitCode: 2}, fmt.Errorf("backup: relay_put requires chunk blob")
	}
	if int64(len(blob)) > RelayChunkSize {
		return registry.Result{ExitCode: 2}, fmt.Errorf("backup: chunk exceeds %d bytes", RelayChunkSize)
	}
	sum := sha256.Sum256(blob)
	if got := hex.EncodeToString(sum[:]); wantSHA == "" || got != wantSHA {
		return registry.Result{ExitCode: 1}, fmt.Errorf("backup: chunk %d sha mismatch (want %s got %s)", idx, wantSHA, got)
	}

	dir := m.sessionDir(uploadID)
	sess, err := m.readSession(dir)
	if err != nil || sess == nil {
		return registry.Result{ExitCode: 1}, fmt.Errorf("backup: relay session %s not found (run relay_begin first)", uploadID)
	}
	if idx < 0 || idx >= sess.ChunkCount {
		return registry.Result{ExitCode: 2}, fmt.Errorf("backup: chunk index %d out of range (count=%d)", idx, sess.ChunkCount)
	}
	// 幂等：已收分片直接确认（断点续传重跑 send 时跳过重复写入）。
	if !containsInt64(sess.Received, idx) {
		part := filepath.Join(dir, fmt.Sprintf("part-%012d", idx))
		if err := os.WriteFile(part, blob, 0o644); err != nil {
			return registry.Result{ExitCode: 1}, fmt.Errorf("backup: write chunk: %w", err)
		}
		sess.Received = append(sess.Received, idx)
		sort.Slice(sess.Received, func(i, j int) bool { return sess.Received[i] < sess.Received[j] })
		if err := m.writeSession(dir, sess); err != nil {
			return registry.Result{ExitCode: 1}, err
		}
	}
	return registry.Result{ExitCode: 0, Output: fmt.Sprintf("chunk %d stored (%d/%d)", idx, len(sess.Received), sess.ChunkCount)}, nil
}

func (m *Manager) opRelayStatus(args map[string]any) (registry.Result, error) {
	uploadID := str(args, "upload_id")
	if uploadID == "" {
		return registry.Result{ExitCode: 2}, fmt.Errorf("backup: relay_status requires upload_id=")
	}
	sess, err := m.readSession(m.sessionDir(uploadID))
	if err != nil || sess == nil {
		return registry.Result{ExitCode: 1}, fmt.Errorf("backup: relay session %s not found", uploadID)
	}
	return registry.Result{
		ExitCode: 0,
		Output:   fmt.Sprintf("received=%d/%d", len(sess.Received), sess.ChunkCount),
		Data:     map[string]any{"received": sess.Received},
	}, nil
}

func (m *Manager) opRelayAssemble(args map[string]any) (registry.Result, error) {
	uploadID := str(args, "upload_id")
	if uploadID == "" {
		return registry.Result{ExitCode: 2}, fmt.Errorf("backup: relay_assemble requires upload_id=")
	}
	dir := m.sessionDir(uploadID)
	sess, err := m.readSession(dir)
	if err != nil || sess == nil {
		return registry.Result{ExitCode: 1}, fmt.Errorf("backup: relay session %s not found", uploadID)
	}
	if int64(len(sess.Received)) < sess.ChunkCount {
		return registry.Result{ExitCode: 1}, fmt.Errorf("backup: assemble aborted: %d/%d chunks present", len(sess.Received), sess.ChunkCount)
	}

	stamp := m.now().UTC().Format("20060102-150405")
	finalPath := filepath.Join(m.backupDir, sess.Name, fmt.Sprintf("%s-%s.tar.gz", sess.Name, stamp))
	if err := os.MkdirAll(filepath.Dir(finalPath), 0o755); err != nil {
		return registry.Result{ExitCode: 1}, fmt.Errorf("backup: mkdir dest: %w", err)
	}
	if err := m.assemble(dir, sess, finalPath); err != nil {
		_ = os.Remove(finalPath)
		_ = os.RemoveAll(dir)
		return registry.Result{ExitCode: 1}, err
	}

	man := &Manifest{
		Name: sess.Name, Source: sess.Source, File: finalPath,
		Created: m.now().UTC().Format(time.RFC3339),
	}
	if err := m.writeManifest(finalPath, man); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	_ = os.RemoveAll(dir)
	return registry.Result{
		ExitCode: 0,
		Output:   fmt.Sprintf("relay backup assembled: %s (sha256 verified)", finalPath),
		Data:     map[string]any{"file": finalPath},
	}, nil
}

// assemble concatenates parts in order while verifying the whole-archive
// SHA-256; any mismatch is a final integrity failure.
func (m *Manager) assemble(dir string, sess *relaySession, finalPath string) error {
	out, err := os.Create(finalPath)
	if err != nil {
		return fmt.Errorf("backup: create assembled archive: %w", err)
	}
	defer out.Close()
	hasher := sha256.New()
	for idx := int64(0); idx < sess.ChunkCount; idx++ {
		part := filepath.Join(dir, fmt.Sprintf("part-%012d", idx))
		f, err := os.Open(part)
		if err != nil {
			return fmt.Errorf("backup: open chunk %d: %w", idx, err)
		}
		if _, err := io.Copy(io.MultiWriter(out, hasher), f); err != nil {
			f.Close()
			return fmt.Errorf("backup: copy chunk %d: %w", idx, err)
		}
		f.Close()
	}
	got := hex.EncodeToString(hasher.Sum(nil))
	if got != sess.FileSHA256 {
		return fmt.Errorf("backup: whole-archive sha256 mismatch (want %s got %s)", sess.FileSHA256, got)
	}
	return nil
}

func (m *Manager) opRelayAbort(args map[string]any) (registry.Result, error) {
	uploadID := str(args, "upload_id")
	if uploadID == "" {
		return registry.Result{ExitCode: 2}, fmt.Errorf("backup: relay_abort requires upload_id=")
	}
	if err := os.RemoveAll(m.sessionDir(uploadID)); err != nil {
		return registry.Result{ExitCode: 1}, err
	}
	return registry.Result{ExitCode: 0, Output: fmt.Sprintf("relay session %s aborted", uploadID)}, nil
}

// precheckDisk rejects the transfer when free space on the backup volume is
// below the declared archive size.
func (m *Manager) precheckDisk(need int64) error {
	if err := os.MkdirAll(m.backupDir, 0o755); err != nil {
		return fmt.Errorf("backup: mkdir backup dir: %w", err)
	}
	var st syscall.Statfs_t
	if err := syscall.Statfs(m.backupDir, &st); err != nil {
		return fmt.Errorf("backup: disk precheck: %w", err)
	}
	free := int64(st.Bavail) * int64(st.Bsize)
	if free < need {
		return fmt.Errorf("backup: disk precheck failed: free %d bytes < archive %d bytes; transfer rejected", free, need)
	}
	return nil
}

func (m *Manager) sessionDir(uploadID string) string {
	return filepath.Join(m.relayRoot(), sanitizeID(uploadID))
}

func (m *Manager) writeSession(dir string, sess *relaySession) error {
	raw, err := json.MarshalIndent(sess, "", "  ")
	if err != nil {
		return fmt.Errorf("backup: marshal session: %w", err)
	}
	if err := os.WriteFile(filepath.Join(dir, "session.json"), raw, 0o644); err != nil {
		return fmt.Errorf("backup: write session: %w", err)
	}
	return nil
}

func (m *Manager) readSession(dir string) (*relaySession, error) {
	raw, err := os.ReadFile(filepath.Join(dir, "session.json"))
	if err != nil {
		if os.IsNotExist(err) {
			return nil, nil
		}
		return nil, err
	}
	var sess relaySession
	if err := json.Unmarshal(raw, &sess); err != nil {
		return nil, err
	}
	return &sess, nil
}

func containsInt64(list []int64, v int64) bool {
	for _, x := range list {
		if x == v {
			return true
		}
	}
	return false
}

func toInt64(v any) (int64, bool) {
	switch n := v.(type) {
	case int64:
		return n, true
	case int:
		return int64(n), true
	case float64:
		return int64(n), true
	case string:
		var out int64
		if _, err := fmt.Sscanf(n, "%d", &out); err == nil {
			return out, true
		}
	}
	return 0, false
}

// sanitizeID strips path separators from identifiers.
func sanitizeID(id string) string {
	return strings.Map(func(r rune) rune {
		if r == '/' || r == '\\' {
			return '_'
		}
		return r
	}, id)
}
