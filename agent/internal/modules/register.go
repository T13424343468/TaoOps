// Package modules wires every base-layer module into a single registry
// for command dispatch. Each module keeps its own package and tests.
package modules

import (
	"fmt"
	"os"
	"path/filepath"

	"taoops/agent/internal/modules/backup"
	"taoops/agent/internal/modules/cron"
	"taoops/agent/internal/modules/db"
	"taoops/agent/internal/modules/docker"
	"taoops/agent/internal/modules/env"
	"taoops/agent/internal/modules/files"
	"taoops/agent/internal/modules/ftp"
	"taoops/agent/internal/modules/fw"
	"taoops/agent/internal/modules/registry"
	"taoops/agent/internal/modules/secguard"
	"taoops/agent/internal/modules/tamperguard"
	"taoops/agent/internal/modules/site"
	"taoops/agent/internal/modules/sysinfo"
	"taoops/agent/internal/modules/term"
)

// Paths groups the runtime directories the modules need.
type Paths struct {
	ConfDir    string
	BackupDir  string
	SessionDir string
	LogDir     string
	PackageDir string
	InstallDir string
	AuditPath  string
}

// RegisterAll installs every base-layer module handler into r.
func RegisterAll(r *registry.Registry, p Paths) error {
	steps := []struct {
		name string
		fn   func() error
	}{
		{"sysinfo", func() error { return sysinfo.Register(r, sysinfo.NewCollector()) }},
		{"site", func() error { return site.Register(r, site.NewManager(p.ConfDir, p.BackupDir)) }},
		{"db", func() error { return db.Register(r, db.NewManager(p.BackupDir)) }},
		{"docker", func() error { return docker.Register(r, docker.NewManager(filepath.Join(p.LogDir, "docker.audit"))) }},
		{"files", func() error { return files.Register(r, files.NewAssembler(p.SessionDir)) }},
		{"term", func() error { return term.Register(r, term.NewManager()) }},
		{"cron", func() error { return cron.Register(r, cron.NewManager(p.LogDir)) }},
		{"env", func() error { return env.Register(r, env.NewManager(p.PackageDir, p.InstallDir)) }},
		{"fw", func() error { return fw.Register(r, fw.NewManager(p.AuditPath)) }},
		{"ftp", func() error { return ftp.Register(r, ftp.NewManager(filepath.Join(p.ConfDir, "ftp.json"))) }},
		{"backup", func() error { return backup.Register(r, backup.NewManager(p.BackupDir)) }},
		{"secguard", func() error {
			return secguard.Register(r, secguard.NewManager(secguard.FromEnv(filepath.Join(p.ConfDir, "secguard.json"))))
		}},
		{"tamperguard", func() error {
			return tamperguard.Register(r, tamperguard.NewGuard(tamperguard.ParsePaths(os.Getenv("TAOOPS_TAMPER_PATHS")), os.Getenv("TAOOPS_TAMPER_BPF_PIN"), "", 0))
		}},
	}
	for _, s := range steps {
		if err := s.fn(); err != nil {
			return fmt.Errorf("modules: register %s: %w", s.name, err)
		}
	}
	return nil
}
