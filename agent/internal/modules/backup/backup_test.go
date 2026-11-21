package backup

import (
	"archive/tar"
	"compress/gzip"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func newTestManager(t *testing.T) (*Manager, string) {
	t.Helper()
	dir := t.TempDir()
	return NewManager(filepath.Join(dir, "backups")), dir
}

func writeFile(t *testing.T, path, content string) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
		t.Fatal(err)
	}
}

func TestCreate_FullBackup(t *testing.T) {
	m, dir := newTestManager(t)
	src := filepath.Join(dir, "site")
	writeFile(t, filepath.Join(src, "index.html"), "<h1>hi</h1>")
	writeFile(t, filepath.Join(src, "app", "main.js"), "console.log(1)")

	res, err := m.handle(map[string]any{"op": "create", "name": "demo", "source": src})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("create: code=%d err=%v out=%s", res.ExitCode, err, res.Output)
	}
	if !strings.Contains(res.Output, "2 files") {
		t.Fatalf("output=%q", res.Output)
	}
	if _, err := os.Stat(res.Data["file"].(string)); err != nil {
		t.Fatalf("archive missing: %v", err)
	}
}

func TestCreate_Incremental_SkipsUnchanged(t *testing.T) {
	m, dir := newTestManager(t)
	src := filepath.Join(dir, "site")
	writeFile(t, filepath.Join(src, "a.txt"), "aaa")

	if res, err := m.handle(map[string]any{"op": "create", "name": "inc", "source": src}); err != nil || res.ExitCode != 0 {
		t.Fatalf("first create: %v %s", err, res.Output)
	}
	// 第二次增量：无变化 → 0 files included
	res, err := m.handle(map[string]any{"op": "create", "name": "inc", "source": src, "incremental": "true"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("second create: %v %s", err, res.Output)
	}
	if !strings.Contains(res.Output, "0 files, 1 unchanged") {
		t.Fatalf("incremental output=%q", res.Output)
	}
	// 改动一个文件后再增量：只含 1 个文件
	writeFile(t, filepath.Join(src, "b.txt"), "bbb")
	res, err = m.handle(map[string]any{"op": "create", "name": "inc", "source": src, "incremental": "true"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("third create: %v %s", err, res.Output)
	}
	if !strings.Contains(res.Output, "1 files, 1 unchanged") {
		t.Fatalf("incremental output=%q", res.Output)
	}
}

func TestList(t *testing.T) {
	m, dir := newTestManager(t)
	src := filepath.Join(dir, "site")
	writeFile(t, filepath.Join(src, "x.txt"), "x")
	if _, err := m.handle(map[string]any{"op": "create", "name": "l", "source": src}); err != nil {
		t.Fatal(err)
	}
	res, err := m.handle(map[string]any{"op": "list", "name": "l"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("list: %v", err)
	}
	if !strings.Contains(res.Output, "files=1") {
		t.Fatalf("list output=%q", res.Output)
	}
}

func TestRestore_RequiresConfirm(t *testing.T) {
	m, dir := newTestManager(t)
	src := filepath.Join(dir, "site")
	writeFile(t, filepath.Join(src, "a.txt"), "v1")
	created, err := m.handle(map[string]any{"op": "create", "name": "c", "source": src})
	if err != nil {
		t.Fatal(err)
	}

	res, err := m.handle(map[string]any{"op": "restore", "file": created.Data["file"].(string)})
	if err == nil || res.ExitCode != 2 {
		t.Fatalf("restore without confirm should fail: code=%d err=%v", res.ExitCode, err)
	}
	if !strings.Contains(res.Output, "confirm=true") {
		t.Fatalf("prompt output=%q", res.Output)
	}
}

func TestRestore_Extracts_And_CreatesRollback(t *testing.T) {
	m, dir := newTestManager(t)
	src := filepath.Join(dir, "site")
	writeFile(t, filepath.Join(src, "a.txt"), "v1")
	created, err := m.handle(map[string]any{"op": "create", "name": "r", "source": src})
	if err != nil {
		t.Fatal(err)
	}
	archive := created.Data["file"].(string)

	// 当前状态变化后还原到 v1
	writeFile(t, filepath.Join(src, "a.txt"), "v2-broken")
	res, err := m.handle(map[string]any{"op": "restore", "file": archive, "confirm": "true"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("restore: %v %s", err, res.Output)
	}
	got, _ := os.ReadFile(filepath.Join(src, "a.txt"))
	if string(got) != "v1" {
		t.Fatalf("restored content=%q", got)
	}
	rb := res.Data["rollback"].(string)
	if rb == "" {
		t.Fatal("rollback snapshot not created")
	}
	if _, err := os.Stat(rb); err != nil {
		t.Fatalf("rollback file missing: %v", err)
	}

	// 回滚到 v2-broken
	res, err = m.handle(map[string]any{"op": "rollback", "file": rb, "confirm": "true"})
	if err != nil || res.ExitCode != 0 {
		t.Fatalf("rollback: %v %s", err, res.Output)
	}
	got, _ = os.ReadFile(filepath.Join(src, "a.txt"))
	if string(got) != "v2-broken" {
		t.Fatalf("rolled-back content=%q", got)
	}
}

func TestRestore_UnsafePathRejected(t *testing.T) {
	m, dir := newTestManager(t)
	src := filepath.Join(dir, "site")
	writeFile(t, filepath.Join(src, "a.txt"), "v1")
	created, err := m.handle(map[string]any{"op": "create", "name": "u", "source": src})
	if err != nil {
		t.Fatal(err)
	}
	archive := created.Data["file"].(string)

	// 篡改归档内路径为绝对路径：还原必须拒绝
	if err := os.Remove(archive); err != nil {
		t.Fatal(err)
	}
	// 直接构造恶意归档
	bad := filepath.Join(dir, "bad.tar.gz")
	writeEvilArchive(t, bad)

	res, err := m.handle(map[string]any{"op": "restore", "file": bad, "confirm": "true"})
	if err == nil || res.ExitCode != 1 {
		t.Fatalf("evil archive should fail: code=%d err=%v", res.ExitCode, err)
	}
	if _, err := os.Stat(filepath.Join("/", "tmp", "taoops-evil")); err == nil {
		t.Fatal("evil path escaped target dir")
	}
	_ = src
}

func writeEvilArchive(t *testing.T, path string) {
	t.Helper()
	f, err := os.Create(path)
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	gz := gzip.NewWriter(f)
	tw := tar.NewWriter(gz)
	hdr := &tar.Header{Name: "/tmp/taoops-evil", Typeflag: tar.TypeReg, Size: 4}
	if err := tw.WriteHeader(hdr); err != nil {
		t.Fatal(err)
	}
	if _, err := tw.Write([]byte("evil")); err != nil {
		t.Fatal(err)
	}
	tw.Close()
	gz.Close()
}

func TestUnknownOp(t *testing.T) {
	m, _ := newTestManager(t)
	res, err := m.handle(map[string]any{"op": "nope"})
	if err == nil || res.ExitCode != 2 {
		t.Fatalf("code=%d err=%v", res.ExitCode, err)
	}
}
