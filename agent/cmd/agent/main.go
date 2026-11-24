// Package main 是 TaoOps Agent 入口：注册 → 心跳 + 命令流 → 模块分发。
package main

import (
	"context"
	"log"
	"os"
	"os/signal"
	"syscall"

	"taoops/agent/internal/agent"
	"taoops/agent/internal/dispatch"
	"taoops/agent/internal/metrics"
	"taoops/agent/internal/modules"
	"taoops/agent/internal/modules/registry"
	"taoops/agent/internal/relay"
)

func envOr(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}

func main() {
	serverURL := os.Getenv("TAOOPS_SERVER")
	if serverURL == "" {
		serverURL = "localhost:5241"
	}
	agentID := os.Getenv("TAOOPS_AGENT_ID")
	if agentID == "" {
		agentID = agent.NewAgentID()
	}

	// 模块运行时目录：默认 /var/lib/taoops/*，可用环境变量覆盖（离线部署友好）。
	paths := modules.Paths{
		DataDir:    envOr("TAOOPS_DATA_DIR", "/var/lib/taoops"),
		ConfDir:    envOr("TAOOPS_CONF_DIR", "/var/lib/taoops/conf"),
		BackupDir:  envOr("TAOOPS_BACKUP_DIR", "/var/lib/taoops/backup"),
		SessionDir: envOr("TAOOPS_SESSION_DIR", "/var/lib/taoops/sessions"),
		LogDir:     envOr("TAOOPS_LOG_DIR", "/var/lib/taoops/logs"),
		PackageDir: envOr("TAOOPS_PACKAGE_DIR", "/var/lib/taoops/packages"),
		InstallDir: envOr("TAOOPS_INSTALL_DIR", "/var/lib/taoops/install"),
		AuditPath:  envOr("TAOOPS_AUDIT_PATH", "/var/lib/taoops/audit.json"),
	}
	r := registry.New()
	backupMgr, err := modules.RegisterAll(r, paths)
	if err != nil {
		log.Printf("agent exited: %v", err)
		os.Exit(1)
	}

	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()

	// FR-A13：节点间备份中转发送端（走与命令流同一套 TLS+TOFU 传输）。
	backupMgr.SetSender(relay.NewSender(serverURL))
	backupMgr.SetContextFactory(func() context.Context { return ctx })

	log.Printf("TaoOps Agent %s -> %s (modules: %v)", metrics.AgentVersion, serverURL, r.Names())
	w := agent.NewWorker(serverURL, agentID, metrics.Collect, dispatch.New(r), log.Printf)
	if err := w.Run(ctx); err != nil {
		log.Printf("agent exited: %v", err)
		os.Exit(1)
	}
}
