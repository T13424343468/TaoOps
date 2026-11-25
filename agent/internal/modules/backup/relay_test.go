// relay_test.go covers FR-A13 node-to-node backup ops: target-side
// assembly (begin/put/status/assemble/abort) with disk precheck, chunk and
// whole-archive SHA-256 verification, idempotent resume, plus source-side
// async send state tracking. Contract: docs/protocol-contract.md §7.
package backup

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"taoops/agent/internal/modules/registry"
)

func newRelayTestManager(t *testing.T) (*Manager, string) {
	t.Helper()
	dir := t.TempDir()
	m := NewManager(filepath.Join(dir, "backup"))
	m.now = func() time.Time { return time.Unix(1_700_000_000, 0) }
	return m, dir
}

func chunkSHA(b []byte) string {
	sum := sha256.Sum256(b)
	return hex.EncodeToString(sum[:])
}

func TestRelayBeginPutAssembleHappyPath(t *testing.T) {
	m, _ := newRelayTestManager(t)
	uploadID := "up-test-1"

	c1 := []byte("hello")
	c2 := []byte(" world!")
	whole := sha256.Sum256(append(append([]byte{}, c1...), c2...))
	wholeSHA := hex.EncodeToString(whole[:])

	if res, err := m.opRelayBegin(map[string]any{
		"op": "relay_begin", "upload_id": uploadID, "name": "site-a",
		"total_size": "12", "chunk_count": "2", "file_sha256": wholeSHA,
		"source": "/var/www/a",
	}); err != nil || res.ExitCode != 0 {
		t.Fatalf("relay_begin: %v %v", res, err)
	}

	for i, c := range [][]byte{c1, c2} {
		if res, err := m.opRelayPut(map[string]any{
			"op": "relay_put", "upload_id": uploadID,
			"chunk_index": fmt.Sprintf("%d", i), "chunk_sha256": chunkSHA(c),
			"__blob": c,
		}); err != nil || res.ExitCode != 0 {
			t.Fatalf("relay_put %d: %v %v", i, res, err)
		}
	}

	res, err := m.opRelayAssemble(map[string]any{"op": "relay_assemble", "upload_id": uploadID})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("relay_assemble: %v %v", res, err)
	}
	final := filepath.Join(m.backupDir, "site-a", "site-a-20231114-221320.tar.gz")
	if _, err := os.Stat(final); err != nil {
		t.Fatalf("assembled archive missing: %v", err)
	}
	// 整包校验通过后装配会话应清理。
	if _, err := os.Stat(m.sessionDir(uploadID)); !os.IsNotExist(err) {
		t.Fatalf("session dir not cleaned: %v", err)
	}
	// manifest 记录源端来源（跨节点还原用）。
	raw, _ := os.ReadFile(final + ".manifest.json")
	if !strings.Contains(string(raw), "/var/www/a") {
		t.Fatalf("manifest missing source: %s", raw)
	}
}

func TestRelayPutShaMismatchRejected(t *testing.T) {
	m, _ := newRelayTestManager(t)
	uploadID := "up-test-2"
	_, _ = m.opRelayBegin(map[string]any{
		"upload_id": uploadID, "name": "n", "total_size": "4", "chunk_count": "1", "file_sha256": "x",
	})
	res, err := m.opRelayPut(map[string]any{
		"upload_id": uploadID, "chunk_index": "0",
		"chunk_sha256": chunkSHA([]byte("other")), "__blob": []byte("data"),
	})
	if err == nil || res.ExitCode != 1 {
		t.Fatalf("expected sha mismatch rejection, got %v %v", res, err)
	}
}

func TestRelayPutWithoutSessionFails(t *testing.T) {
	m, _ := newRelayTestManager(t)
	res, err := m.opRelayPut(map[string]any{
		"upload_id": "ghost", "chunk_index": "0",
		"chunk_sha256": chunkSHA([]byte("d")), "__blob": []byte("d"),
	})
	if err == nil || res.ExitCode != 1 {
		t.Fatalf("expected missing-session failure, got %v %v", res, err)
	}
}

func TestRelayPutIdempotent(t *testing.T) {
	m, _ := newRelayTestManager(t)
	uploadID := "up-test-3"
	_, _ = m.opRelayBegin(map[string]any{
		"upload_id": uploadID, "name": "n", "total_size": "2", "chunk_count": "2", "file_sha256": "x",
	})
	blob := []byte("ab")
	for i := 0; i < 2; i++ {
		if res, err := m.opRelayPut(map[string]any{
			"upload_id": uploadID, "chunk_index": "1", "chunk_sha256": chunkSHA(blob), "__blob": blob,
		}); err != nil || res.ExitCode != 0 {
			t.Fatalf("relay_put run %d: %v %v", i, res, err)
		}
	}
	sess, _ := m.readSession(m.sessionDir(uploadID))
	if len(sess.Received) != 1 {
		t.Fatalf("expected 1 received chunk after duplicate put, got %v", sess.Received)
	}
}

func TestRelayPutOversizedBlobRejected(t *testing.T) {
	m, _ := newRelayTestManager(t)
	uploadID := "up-test-4"
	_, _ = m.opRelayBegin(map[string]any{
		"upload_id": uploadID, "name": "n", "total_size": "1", "chunk_count": "1", "file_sha256": "x",
	})
	big := make([]byte, RelayChunkSize+1)
	res, err := m.opRelayPut(map[string]any{
		"upload_id": uploadID, "chunk_index": "0", "chunk_sha256": chunkSHA(big), "__blob": big,
	})
	if err == nil || res.ExitCode != 2 {
		t.Fatalf("expected oversized rejection, got %v %v", res, err)
	}
}

func TestRelayAssembleMissingChunksFails(t *testing.T) {
	m, _ := newRelayTestManager(t)
	uploadID := "up-test-5"
	_, _ = m.opRelayBegin(map[string]any{
		"upload_id": uploadID, "name": "n", "total_size": "2", "chunk_count": "2", "file_sha256": "x",
	})
	res, err := m.opRelayAssemble(map[string]any{"upload_id": uploadID})
	if err == nil || res.ExitCode != 1 {
		t.Fatalf("expected missing-chunk failure, got %v %v", res, err)
	}
}

func TestRelayAssembleWholeArchiveShaMismatchCleansUp(t *testing.T) {
	m, _ := newRelayTestManager(t)
	uploadID := "up-test-6"
	_, _ = m.opRelayBegin(map[string]any{
		"upload_id": uploadID, "name": "n", "total_size": "2", "chunk_count": "1",
		"file_sha256": "deadbeef",
	})
	blob := []byte("ab")
	if res, err := m.opRelayPut(map[string]any{
		"upload_id": uploadID, "chunk_index": "0", "chunk_sha256": chunkSHA(blob), "__blob": blob,
	}); err != nil || res.ExitCode != 0 {
		t.Fatalf("relay_put: %v %v", res, err)
	}
	res, err := m.opRelayAssemble(map[string]any{"upload_id": uploadID})
	if err == nil || res.ExitCode != 1 {
		t.Fatalf("expected whole-archive mismatch failure, got %v %v", res, err)
	}
	if _, err := os.Stat(m.sessionDir(uploadID)); !os.IsNotExist(err) {
		t.Fatalf("session dir not cleaned after integrity failure: %v", err)
	}
	matches, _ := filepath.Glob(filepath.Join(m.backupDir, "**/*.tar.gz"))
	if len(matches) != 0 {
		t.Fatalf("no archive should survive integrity failure: %v", matches)
	}
}

func TestRelayAbort(t *testing.T) {
	m, _ := newRelayTestManager(t)
	uploadID := "up-test-7"
	_, _ = m.opRelayBegin(map[string]any{
		"upload_id": uploadID, "name": "n", "total_size": "1", "chunk_count": "1", "file_sha256": "x",
	})
	if res, err := m.opRelayAbort(map[string]any{"upload_id": uploadID}); err != nil || res.ExitCode != 0 {
		t.Fatalf("relay_abort: %v %v", res, err)
	}
	if _, err := os.Stat(m.sessionDir(uploadID)); !os.IsNotExist(err) {
		t.Fatalf("session dir not removed: %v", err)
	}
}

func TestPrecheckDiskRejectsImpossibleSize(t *testing.T) {
	m, _ := newRelayTestManager(t)
	_ = os.MkdirAll(m.backupDir, 0o755)
	if err := m.precheckDisk(1 << 60); err == nil {
		t.Fatalf("expected precheck rejection for impossible size")
	}
	if err := m.precheckDisk(1024); err != nil {
		t.Fatalf("precheck for small size should pass: %v", err)
	}
}

type fakeSender struct {
	mu       chan struct{}
	progress []int64
	fail     bool
}

func (f *fakeSender) Send(ctx context.Context, target, uploadID, name, filePath, source string, onProgress func(int64)) error {
	if onProgress != nil {
		onProgress(5)
	}
	if f.fail {
		return fmt.Errorf("relay: simulated failure")
	}
	return nil
}

func TestSendValidatesArgsAndPath(t *testing.T) {
	m, dir := newRelayTestManager(t)
	m.SetSender(&fakeSender{})

	// 缺参。
	if res, err := m.opSend(map[string]any{"file": "/x", "target_node": "n"}); err == nil || res.ExitCode != 2 {
		t.Fatalf("expected missing-args failure, got %v %v", res, err)
	}
	// 相对路径。
	if res, err := m.opSend(map[string]any{"file": "rel.tar.gz", "target_node": "n", "name": "n"}); err == nil || res.ExitCode != 2 {
		t.Fatalf("expected relative-path failure, got %v %v", res, err)
	}
	// 备份目录之外的绝对路径。
	if res, err := m.opSend(map[string]any{"file": "/etc/passwd", "target_node": "n", "name": "n"}); err == nil || res.ExitCode != 2 {
		t.Fatalf("expected outside-backup-dir failure, got %v %v", res, err)
	}
	_ = dir
}

func TestSendAsyncStateTransitions(t *testing.T) {
	m, dir := newRelayTestManager(t)
	_ = os.MkdirAll(m.backupDir, 0o755)
	archive := filepath.Join(dir, "backup", "a.tar.gz")
	_ = os.WriteFile(archive, []byte("payload"), 0o644)

	sender := &fakeSender{}
	m.SetSender(sender)
	m.SetContextFactory(func() context.Context { return context.Background() })

	res, err := m.opSend(map[string]any{"file": archive, "target_node": "node-b", "name": "a"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("send: %v %v", res, err)
	}
	relayID, _ := res.Data["relay_id"].(string)
	if relayID == "" {
		t.Fatalf("relay_id missing: %v", res.Data)
	}

	// 在途期间第二个 send 拒绝。
	if res2, err2 := m.opSend(map[string]any{"file": archive, "target_node": "node-b", "name": "a"}); err2 == nil || res2.ExitCode != 2 {
		t.Fatalf("expected busy rejection, got %v %v", res2, err2)
	}

	// 等待后台发送收尾。
	deadline := time.Now().Add(2 * time.Second)
	for {
		res3, _ := m.opSendStatus(map[string]any{"relay_id": relayID})
		if strings.Contains(res3.Output, "status=done") {
			break
		}
		if time.Now().After(deadline) {
			t.Fatalf("send did not finish: %s", res3.Output)
		}
		time.Sleep(5 * time.Millisecond)
	}
}

func TestSendFailureRecordedInStatus(t *testing.T) {
	m, dir := newRelayTestManager(t)
	_ = os.MkdirAll(m.backupDir, 0o755)
	archive := filepath.Join(dir, "backup", "b.tar.gz")
	_ = os.WriteFile(archive, []byte("payload"), 0o644)

	m.SetSender(&fakeSender{fail: true})
	m.SetContextFactory(func() context.Context { return context.Background() })

	res, err := m.opSend(map[string]any{"file": archive, "target_node": "node-c", "name": "b"})
	if err != nil {
		t.Fatalf("send: %v", err)
	}
	relayID, _ := res.Data["relay_id"].(string)

	deadline := time.Now().Add(2 * time.Second)
	for {
		res2, _ := m.opSendStatus(map[string]any{"relay_id": relayID})
		if strings.Contains(res2.Output, "status=failed") {
			if !strings.Contains(res2.Output, "simulated failure") {
				t.Fatalf("failure reason missing: %s", res2.Output)
			}
			return
		}
		if time.Now().After(deadline) {
			t.Fatalf("send did not finish: %s", res2.Output)
		}
		time.Sleep(5 * time.Millisecond)
	}
}

func TestSendWithoutSenderWired(t *testing.T) {
	m, dir := newRelayTestManager(t)
	archive := filepath.Join(dir, "x.tar.gz")
	_ = os.WriteFile(archive, []byte("p"), 0o644)
	res, err := m.opSend(map[string]any{"file": archive, "target_node": "n", "name": "n"})
	if err == nil || res.ExitCode != 2 {
		t.Fatalf("expected unwired-sender failure, got %v %v", res, err)
	}
}

func TestRelayOpsRegistered(t *testing.T) {
	r := registry.New()
	m, _ := newRelayTestManager(t)
	if err := Register(r, m); err != nil {
		t.Fatalf("Register: %v", err)
	}
	for _, op := range []string{"send", "send_status", "relay_begin", "relay_put", "relay_status", "relay_assemble", "relay_abort"} {
		if _, ok := r.Get("backup.manage"); !ok {
			t.Fatalf("backup.manage not registered (op %s)", op)
		}
	}
}
