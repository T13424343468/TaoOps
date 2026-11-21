package env

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func fakeRunner(log *[]string, installed map[string]bool) Runner {
	return func(bin string, args []string, stdin string) (string, string, error) {
		*log = append(*log, bin+" "+strings.Join(args, " "))
		if bin == "/bin/sh" && len(args) == 2 && args[0] == "-c" {
			// command -v <name> || true
			name := strings.TrimPrefix(strings.TrimSuffix(args[1], " || true"), "command -v ")
			if installed[name] {
				return "/usr/bin/" + name, "", nil
			}
			return "", "", nil
		}
		return "", "", nil
	}
}

func newMgr(t *testing.T, installed map[string]bool) (*Manager, *[]string) {
	t.Helper()
	dir := t.TempDir()
	log := &[]string{}
	return &Manager{Run: fakeRunner(log, installed), PackageDir: filepath.Join(dir, "pkg"), InstallDir: filepath.Join(dir, "opt")}, log
}

func makePkg(t *testing.T, dir, envName string) string {
	t.Helper()
	src := filepath.Join(dir, envName+".tar.gz")
	if err := os.WriteFile(src, []byte("fake-tarball"), 0o644); err != nil {
		t.Fatal(err)
	}
	return src
}

func TestImportPackageStages(t *testing.T) {
	m, _ := newMgr(t, nil)
	dir := t.TempDir()
	src := makePkg(t, dir, "nginx")
	dst, err := m.ImportPackage("nginx", src)
	if err != nil {
		t.Fatalf("ImportPackage: %v", err)
	}
	if _, err := os.Stat(dst); err != nil {
		t.Fatalf("staged package missing: %v", err)
	}
}

func TestImportValidation(t *testing.T) {
	m, _ := newMgr(t, nil)
	if _, err := m.ImportPackage("oracle", "/x.tar.gz"); err == nil {
		t.Fatal("unknown env should fail")
	}
	if _, err := m.ImportPackage("nginx", "/nonexistent.tar.gz"); err == nil {
		t.Fatal("missing package should fail")
	}
	dir := t.TempDir()
	bad := filepath.Join(dir, "nginx.zip")
	os.WriteFile(bad, []byte("x"), 0o644)
	if _, err := m.ImportPackage("nginx", bad); err == nil {
		t.Fatal("non-tar.gz should fail")
	}
}

func TestInstallWithoutPackageFails(t *testing.T) {
	m, _ := newMgr(t, nil)
	if _, err := m.Install("nginx"); err == nil {
		t.Fatal("install without imported package should fail")
	}
}

func TestInstallRunsInstaller(t *testing.T) {
	m, log := newMgr(t, nil)
	dir := t.TempDir()
	src := makePkg(t, dir, "mysql")
	if _, err := m.ImportPackage("mysql", src); err != nil {
		t.Fatalf("ImportPackage: %v", err)
	}
	// fake the extracted installer: create it before Install runs
	os.MkdirAll(filepath.Join(m.InstallDir, "mysql"), 0o755)
	os.WriteFile(filepath.Join(m.InstallDir, "mysql", "install.sh"), []byte("#!/bin/sh\n"), 0o755)
	// but Install extracts first, which would wipe... our fake tar is not a real tar;
	// so pre-create installer and make tar extraction a no-op by pointing Run at a stub
	m.Run = func(bin string, args []string, stdin string) (string, string, error) {
		*log = append(*log, bin)
		if bin == "tar" {
			return "", "", nil
		}
		if bin == "/bin/sh" {
			return "", "", nil
		}
		return "", "", nil
	}
	if _, err := m.Install("mysql"); err != nil {
		t.Fatalf("Install: %v", err)
	}
}

func TestInstallMissingInstallerFails(t *testing.T) {
	m, _ := newMgr(t, nil)
	dir := t.TempDir()
	src := makePkg(t, dir, "php")
	m.ImportPackage("php", src)
	m.Run = func(bin string, args []string, stdin string) (string, string, error) {
		if bin == "tar" {
			return "", "", nil
		}
		return "", "", nil
	}
	if _, err := m.Install("php"); err == nil {
		t.Fatal("missing install.sh should fail")
	}
}

func TestDetectReportsInstalled(t *testing.T) {
	m, _ := newMgr(t, map[string]bool{"nginx": true})
	found, err := m.Detect("nginx")
	if err != nil || !found {
		t.Fatalf("Detect = %v %v", found, err)
	}
	found, err = m.Detect("docker")
	if err != nil || found {
		t.Fatalf("Detect docker = %v %v", found, err)
	}
	if _, err := m.Detect("redis"); err == nil {
		t.Fatal("unknown env should fail")
	}
}

func TestHandlerOps(t *testing.T) {
	m, _ := newMgr(t, nil)
	h := Handler(m)
	if _, err := h(map[string]any{"op": "bogus"}); err == nil {
		t.Fatal("unknown op should fail")
	}
	dir := t.TempDir()
	src := makePkg(t, dir, "node")
	if _, err := h(map[string]any{"op": "import", "env": "node", "src": src}); err != nil {
		t.Fatalf("import: %v", err)
	}
	if _, err := h(map[string]any{"op": "detect", "env": "node"}); err != nil {
		t.Fatalf("detect: %v", err)
	}
	// install without real installer should fail cleanly
	if _, err := h(map[string]any{"op": "install", "env": "node"}); err == nil {
		t.Fatal("install should fail without install.sh")
	}
	_ = fmt.Sprint("done")
}
