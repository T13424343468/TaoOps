package modules

import (
	"path/filepath"
	"testing"

	"taoops/agent/internal/modules/registry"
)

func TestRegisterAllInstallsEveryModule(t *testing.T) {
	dir := t.TempDir()
	p := Paths{
		ConfDir:    filepath.Join(dir, "conf"),
		BackupDir:  filepath.Join(dir, "backup"),
		SessionDir: filepath.Join(dir, "sessions"),
		LogDir:     filepath.Join(dir, "logs"),
		PackageDir: filepath.Join(dir, "pkg"),
		InstallDir: filepath.Join(dir, "opt"),
		AuditPath:  filepath.Join(dir, "audit.log"),
	}
	r := registry.New()
	if _, err := RegisterAll(r, p); err != nil {
		t.Fatalf("RegisterAll: %v", err)
	}
	want := []string{
		"backup.manage",
		"cron.manage",
		"db.manage",
		"docker.manage",
		"env.manage",
		"files.manage",
		"ftp.manage",
		"fw.manage",
		"sec.manage",
		"site.manage",
		"sysinfo.overview",
		"tamper.guard",
		"term.manage",
		"webstats.manage",
	}
	got := r.Names()
	if len(got) != len(want) {
		t.Fatalf("registered = %v, want %v", got, want)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("registered = %v, want %v", got, want)
		}
	}
}

func TestRegisterAllTwiceIsIdempotent(t *testing.T) {
	dir := t.TempDir()
	p := Paths{AuditPath: filepath.Join(dir, "a.log")}
	r := registry.New()
	if _, err := RegisterAll(r, p); err != nil {
		t.Fatalf("first RegisterAll: %v", err)
	}
	if _, err := RegisterAll(r, p); err != nil {
		t.Fatalf("second RegisterAll: %v", err)
	}
	if len(r.Names()) != 14 {
		t.Fatalf("names = %v", r.Names())
	}
}
