package files

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
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

func TestReadTextFileOK(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "nginx.conf")
	os.WriteFile(p, []byte("server {\n}\n"), 0o644)
	got, err := ReadTextFile(p)
	if err != nil || got != "server {\n}\n" {
		t.Fatalf("ReadTextFile = %q err=%v", got, err)
	}
}

func TestReadTextFileSizeLimit(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "big.log")
	os.WriteFile(p, make([]byte, EditMaxBytes+1), 0o644)
	if _, err := ReadTextFile(p); err == nil || !strings.Contains(err.Error(), "exceeds") {
		t.Fatalf("oversize read should fail with explicit error, got %v", err)
	}
}

func TestReadTextFileBinaryRejected(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "bin.dat")
	os.WriteFile(p, []byte("ab\x00cd"), 0o644)
	if _, err := ReadTextFile(p); err == nil || !strings.Contains(err.Error(), "binary") {
		t.Fatalf("binary read should fail with explicit error, got %v", err)
	}
}

func TestEditPathValidation(t *testing.T) {
	if _, err := ReadTextFile("relative/path.txt"); err == nil {
		t.Fatal("relative path should fail")
	}
	if _, err := ReadTextFile("/etc/../etc/passwd"); err == nil {
		t.Fatal(".. segment should fail")
	}
	if _, err := ReadTextFile("/etc//passwd"); err == nil {
		t.Fatal("non-canonical path should fail")
	}
	// symlink escape: /tmp link component
	link := filepath.Join(t.TempDir(), "link")
	target := t.TempDir()
	os.Symlink(target, link)
	if _, err := ReadTextFile(filepath.Join(link, "x.txt")); err == nil || !strings.Contains(err.Error(), "symbolic link") {
		t.Fatalf("symlink component should fail, got %v", err)
	}
}

func TestWriteTextFileBackupRotation(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "app.conf")
	base := time.Unix(1_700_000_000, 0)
	if err := WriteTextFile(p, "v1", base); err != nil {
		t.Fatalf("first write: %v", err)
	}
	// overwrite existing file 6 times -> keep 5 backups
	for i := 0; i < 6; i++ {
		if err := WriteTextFile(p, fmt.Sprintf("v%d", i+2), base.Add(time.Duration(i+1)*time.Second)); err != nil {
			t.Fatalf("write %d: %v", i, err)
		}
	}
	got, _ := os.ReadFile(p)
	if string(got) != "v7" {
		t.Fatalf("content = %q", string(got))
	}
	baks, _ := filepath.Glob(p + ".bak.*")
	if len(baks) != BackupKeep {
		t.Fatalf("backups = %d, want %d (%v)", len(baks), BackupKeep, baks)
	}
	// oldest (ts=...001) pruned, newest (...006) kept
	os.ReadFile(filepath.Join(dir, "app.conf.bak.1700000006"))
	if _, err := os.Stat(p + ".bak.1700000001"); !os.IsNotExist(err) {
		t.Fatal("oldest backup should be pruned")
	}
	if _, err := os.Stat(p + ".bak.1700000006"); err != nil {
		t.Fatalf("newest backup missing: %v", err)
	}
}

func TestWriteTextFilePreservesMode(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "script.sh")
	os.WriteFile(p, []byte("#!/bin/sh\n"), 0o755)
	if err := WriteTextFile(p, "#!/bin/sh\necho hi\n", time.Unix(1_700_000_000, 0)); err != nil {
		t.Fatalf("write: %v", err)
	}
	fi, _ := os.Stat(p)
	if fi.Mode().Perm() != 0o755 {
		t.Fatalf("mode = %v, want 0755", fi.Mode())
	}
}

func TestWriteTextFileBinaryAndSizeRejected(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "f")
	if err := WriteTextFile(p, "a\x00b", time.Unix(1_700_000_000, 0)); err == nil || !strings.Contains(err.Error(), "binary") {
		t.Fatalf("binary write should fail, got %v", err)
	}
	if err := WriteTextFile(p, strings.Repeat("x", int(EditMaxBytes)+1), time.Unix(1_700_000_000, 0)); err == nil || !strings.Contains(err.Error(), "exceeds") {
		t.Fatalf("oversize write should fail, got %v", err)
	}
}

func TestHandlerFileEditOps(t *testing.T) {
	root := t.TempDir()
	h := Handler(NewAssembler(filepath.Join(root, "s")))
	p := filepath.Join(root, "a.conf")
	if _, err := h(map[string]any{"op": "file_write", "path": p, "content": "k=v"}); err != nil {
		t.Fatalf("file_write: %v", err)
	}
	res, err := h(map[string]any{"op": "file_read", "path": p})
	if err != nil || res.Output != "k=v" {
		t.Fatalf("file_read: %q err=%v", res.Output, err)
	}
	if _, err := h(map[string]any{"op": "file_read", "path": "nope"}); err == nil {
		t.Fatal("relative path should fail")
	}
}
