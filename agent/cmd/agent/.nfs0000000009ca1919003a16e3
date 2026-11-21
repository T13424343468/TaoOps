// Package main 是 TaoOps Agent 入口：注册 → 心跳 → （M2）指令通道。
package main

import (
	"context"
	"log"
	"os"
	"os/signal"
	"syscall"

	"taoops/agent/internal/agent"
	"taoops/agent/internal/metrics"
)

func main() {
	serverURL := os.Getenv("TAOOPS_SERVER")
	if serverURL == "" {
		serverURL = "localhost:5241"
	}
	agentID := os.Getenv("TAOOPS_AGENT_ID")
	if agentID == "" {
		agentID = agent.NewAgentID()
	}

	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()

	log.Printf("TaoOps Agent %s -> %s", metrics.AgentVersion, serverURL)
	w := agent.NewWorker(serverURL, agentID, metrics.Collect, log.Printf)
	if err := w.Run(ctx); err != nil {
		log.Printf("agent exited: %v", err)
		os.Exit(1)
	}
}
