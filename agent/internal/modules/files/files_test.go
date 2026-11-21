package files

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestPutChunkAndFinish(t *testing.T) {
	root := t.TempDir()
	a := NewAssembler(filepath.Join(root, "sessions"))
	dest := filepath.Join(root, "out.bin")

	if _, err := a.PutChunk("u1", 0, []byte("AAAA"), 8); err != nil {
		t.Fatalf("PutChunk: %v", err)
	}
	if _, err := a.PutChunk("u1", 1, []byte("BBBB"), 8); err != nil {
		t.Fatalf("PutChunk: %v", err)
	}
	if err := a.Finish("u1", dest); err != nil {
		t.Fatalf("Finish: %v", err)
	}
	data, err := os.ReadFile(dest)
	if err != nil || string(data) != "AAAABBBB" {
		t.Fatalf("assembled = %q err=%v", string(data), err)
	}
	// session dir cleaned up
	if _, err := os.Stat(filepath.Join(a.Root, "u1")); !os.IsNotExist(err) {
		t.Fatal("session dir should be removed after Finish")
	}
}

func TestResumeReportsMissingChunks(t *testing.T) {
	root := t.TempDir()
	a := NewAssembler(root)
	if _, err := a.PutChunk("u2", 0, []byte("x"), 3); err != nil {
		t.Fatalf("PutChunk: %v", err)
	}
	if _, err := a.PutChunk("u2", 2, []byte("z"), 3); err != nil {
		t.Fatalf("PutChunk: %v", err)
	}
	got, err := a.ReceivedChunks("u2")
	if err != nil {
		t.Fatalf("ReceivedChunks: %v", err)
	}
	if len(got) != 2 || got[0] != 0 || got[1] != 2 {
		t.Fatalf("received = %v", got)
	}
	// chunk 1 missing -> Finish must fail
	if err := a.Finish("u2", filepath.Join(root, "out")); err == nil {
		t.Fatal("Finish with gap should fail")
	}
}

func TestPutChunkValidation(t *testing.T) {
	a := NewAssembler(t.TempDir())
	if _, err := a.PutChunk("", 0, []byte("x"), 1); err == nil {
		t.Fatal("empty id should fail")
	}
	if _, err := a.PutChunk("u", -1, []byte("x"), 1); err == nil {
		t.Fatal("negative index should fail")
	}
	if _, err := a.PutChunk("u", 0, []byte("x"), 0); err == nil {
		t.Fatal("zero total should fail")
	}
	big := make([]byte, ChunkSize+1)
	if _, err := a.PutChunk("u", 0, big, 1); err == nil {
		t.Fatal("oversized chunk should fail")
	}
}

func TestReceivedChunksEmptySession(t *testing.T) {
	a := NewAssembler(t.TempDir())
	got, err := a.ReceivedChunks("ghost")
	if err != nil {
		t.Fatalf("ReceivedChunks: %v", err)
	}
	if len(got) != 0 {
		t.Fatalf("expected empty, got %v", got)
	}
}

func TestListSorted(t *testing.T) {
	dir := t.TempDir()
	os.WriteFile(filepath.Join(dir, "b.txt"), []byte("bb"), 0o644)
	os.WriteFile(filepath.Join(dir, "a.txt"), []byte("a"), 0o644)
	os.Mkdir(filepath.Join(dir, "sub"), 0o755)
	entries, err := List(dir)
	if err != nil {
		t.Fatalf("List: %v", err)
	}
	if len(entries) != 3 || entries[0].Name != "a.txt" || entries[2].Name != "sub" || !entries[2].IsDir {
		t.Fatalf("entries = %+v", entries)
	}
}

func TestReadWriteChmod(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "f.txt")
	if err := WriteFile(p, "hello"); err != nil {
		t.Fatalf("WriteFile: %v", err)
	}
	content, err := ReadFile(p)
	if err != nil || content != "hello" {
		t.Fatalf("ReadFile = %q err=%v", content, err)
	}
	if err := Chmod(p, 0o755); err != nil {
		t.Fatalf("Chmod: %v", err)
	}
	info, _ := os.Stat(p)
	if info.Mode().Perm() != 0o755 {
		t.Fatalf("mode = %v", info.Mode())
	}
}

func TestThresholds(t *testing.T) {
	if !IsLarge(1 << 30) {
		t.Fatal("1GB should be large")
	}
	if IsLarge((1 << 30) - 1) {
		t.Fatal("just under 1GB should not be large")
	}
	if ChunkSize != 4<<20 {
		t.Fatalf("ChunkSize = %d", ChunkSize)
	}
}

func TestHandlerOps(t *testing.T) {
	root := t.TempDir()
	a := NewAssembler(filepath.Join(root, "s"))
	h := Handler(a)
	if _, err := h(map[string]any{"op": "bogus"}); err == nil {
		t.Fatal("unknown op should fail")
	}
	if _, err := h(map[string]any{"op": "chunk", "id": "u9", "index": 0, "total": 1, "data": "hi"}); err != nil {
		t.Fatalf("chunk: %v", err)
	}
	res, err := h(map[string]any{"op": "resume", "id": "u9"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("resume: %v", err)
	}
	if _, err := h(map[string]any{"op": "finish", "id": "u9", "dest": filepath.Join(root, "done.bin")}); err != nil {
		t.Fatalf("finish: %v", err)
	}
	if _, err := h(map[string]any{"op": "list", "dir": root}); err != nil {
		t.Fatalf("list: %v", err)
	}
	if _, err := h(map[string]any{"op": "write", "path": filepath.Join(root, "w.txt"), "content": "x"}); err != nil {
		t.Fatalf("write: %v", err)
	}
	if _, err := h(map[string]any{"op": "read", "path": filepath.Join(root, "w.txt")}); err != nil {
		t.Fatalf("read: %v", err)
	}
	if _, err := h(map[string]any{"op": "chmod", "path": filepath.Join(root, "w.txt"), "mode": 0o644}); err != nil {
		t.Fatalf("chmod: %v", err)
	}
	if !strings.Contains(a.Root, "s") {
		t.Fatal("unexpected assembler root")
	}
}
