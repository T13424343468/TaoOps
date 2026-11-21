// Package agent 实现 Agent 主循环：注册、心跳、断链指数退避重连。
// 契约正源：src/docs/protocol-contract.md。
package agent

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"math"
	"os"
	"time"

	"google.golang.org/grpc"
	"google.golang.org/grpc/metadata"

	"taoops/agent/internal/dispatch"
	"taoops/agent/internal/metrics"
	pb "taoops/agent/pb"
)

const (
	HeartbeatIntervalMs = 15000
	ReconnectInitialMs  = 1 * time.Second
	ReconnectMaxMs      = 60 * time.Second
)

// Worker 持有与服务端的会话状态；并发安全由 gRPC 连接保证。
type Worker struct {
	serverURL string
	agentID   string
	collect   func() metrics.Snapshot
	dispatch  *dispatch.Dispatcher
	logf      func(string, ...any)
}

func NewWorker(serverURL, agentID string, collect func() metrics.Snapshot, d *dispatch.Dispatcher, logf func(string, ...any)) *Worker {
	return &Worker{serverURL: serverURL, agentID: agentID, collect: collect, dispatch: d, logf: logf}
}

// Run 阻塞运行直到 ctx 取消；断链按指数退避重连（1s 倍增，上限 60s）。
func (w *Worker) Run(ctx context.Context) error {
	delay := ReconnectInitialMs
	for {
		err := w.runSession(ctx)
		if ctx.Err() != nil {
			return nil
		}
		if err != nil {
			w.logf("session ended: %v", err)
		}
		w.logf("reconnecting in %v", delay)
		select {
		case <-ctx.Done():
			return nil
		case <-time.After(delay):
		}
		delay = time.Duration(math.Min(float64(delay*2), float64(ReconnectMaxMs)))
	}
}

func (w *Worker) runSession(ctx context.Context) error {
	conn, err := w.dial(ctx)
	if err != nil {
		return err
	}
	defer conn.Close()

	client := pb.NewAgentServiceClient(conn)
	reg, err := client.Register(ctx, &pb.RegisterRequest{
		AgentId:  w.agentID,
		Hostname: hostname(),
		Os:       metrics.OsName(),
		Arch:     metrics.Arch(),
		Version:  metrics.AgentVersion,
	})
	if err != nil {
		return err
	}
	w.logf("registered as node %s", reg.GetNodeId())

	interval := HeartbeatIntervalMs
	if cfg := reg.GetConfig(); cfg != nil && cfg.GetHeartbeatIntervalMs() > 0 {
		interval = int(cfg.GetHeartbeatIntervalMs())
	}

	// 心跳、命令流、终端流、日志流并行；任一通道失败即结束会话，触发退避重连。
	errCh := make(chan error, 4)
	go func() { errCh <- w.pumpHeartbeats(ctx, client, reg.GetNodeId(), interval) }()
	go func() { errCh <- w.pumpCommands(ctx, client, reg.GetNodeId()) }()
	go func() { errCh <- PumpTerms(ctx, client, reg.GetNodeId(), w.logf) }()
	go func() { errCh <- PumpLogs(ctx, client, reg.GetNodeId(), w.logf) }()
	return <-errCh
}

// dial 建立连接：TAOOPS_INSECURE=1 时走明文（本地回环）；否则 TLS+TOFU 指纹固化（契约 §1）。
func (w *Worker) dial(ctx context.Context) (*grpc.ClientConn, error) {
	creds, err := DialCredentials(w.serverURL)
	if err != nil {
		return nil, err
	}
	return grpc.NewClient(w.serverURL, grpc.WithTransportCredentials(creds))
}

func (w *Worker) pumpHeartbeats(ctx context.Context, client pb.AgentServiceClient, nodeID string, intervalMs int) error {
	stream, err := client.Heartbeat(ctx)
	if err != nil {
		return err
	}
	ticker := time.NewTicker(time.Duration(intervalMs) * time.Millisecond)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			_ = stream.CloseSend()
			return nil
		case <-ticker.C:
		}
		m := w.collect()
		if err := stream.Send(&pb.HeartbeatMsg{
			NodeId: nodeID,
			Ts:     time.Now().UnixMilli(),
			Cpu:    m.Cpu,
			Mem:    m.Mem,
			Disk:   m.Disk,
			Load:   m.Load,
		}); err != nil {
			return err
		}
	}
}

// Fingerprint 返回证书 SHA-256 指纹（TOFU 固化用，M2 接入校验链路）。
func Fingerprint(raw []byte) string {
	sum := sha256.Sum256(raw)
	return hex.EncodeToString(sum[:])
}

// pumpCommands 维持 CommandStream：接收中控下发的指令，经统一分发器执行后按
// request_id 回传。发送侧单 goroutine 串行化（gRPC 流 Send 非并发安全）。
func (w *Worker) pumpCommands(ctx context.Context, client pb.AgentServiceClient, nodeID string) error {
	streamCtx, cancel := context.WithCancel(metadata.AppendToOutgoingContext(ctx, "x-node-id", nodeID))
	defer cancel()

	stream, err := client.CommandStream(streamCtx)
	if err != nil {
		return err
	}

	results := make(chan *pb.DispatchResult, 16)
	senderDone := make(chan error, 1)
	go func() {
		for {
			select {
			case r := <-results:
				if err := stream.Send(r); err != nil {
					senderDone <- err
					return
				}
			case <-streamCtx.Done():
				senderDone <- streamCtx.Err()
				return
			}
		}
	}()

	for {
		req, err := stream.Recv()
		if err != nil {
			cancel()
			<-senderDone
			return err
		}
		go func() {
			execCtx, execCancel := dispatch.TimeoutContext(streamCtx, req.GetTimeoutMs())
			defer execCancel()
			code, out := w.dispatch.Execute(execCtx, req.GetCommand(), req.GetArgs())
			select {
			case results <- &pb.DispatchResult{RequestId: req.GetRequestId(), ExitCode: code, Output: out}:
			case <-streamCtx.Done():
			}
		}()
	}
}

func hostname() string {
	h, err := os.Hostname()
	if err != nil {
		return "unknown"
	}
	return h
}
