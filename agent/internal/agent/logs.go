// 日志流泵：维持一条 LogStream，接收中控订阅控制，按 path 轮询追尾
// 日志文件并推送增量（契约 §2 LogStream/LogControl）。
package agent

import (
	"context"
	"io"
	"os"
	"strings"
	"sync"
	"time"

	"google.golang.org/grpc/metadata"

	pb "taoops/agent/pb"
)

// PumpLogs 维持日志流：中控下发 subscribe(path, tail) 后开始追尾。
// 订阅按 path 多路并存（契约 §2 LogControl 按 path 订阅/退订）：
// 事件回流自动订阅（WAF/SEC）与控制台看日志互不顶掉；
// 同 path 重复 subscribe 视为刷新（停旧起新）；unsubscribe 停对应 path。
func PumpLogs(ctx context.Context, client pb.AgentServiceClient, nodeID string, logf func(string, ...any)) error {
	streamCtx, cancel := context.WithCancel(metadata.AppendToOutgoingContext(ctx, "x-node-id", nodeID))
	defer cancel()

	stream, err := client.LogStream(streamCtx)
	if err != nil {
		return err
	}

	var (
		mu    sync.Mutex
		tails = make(map[string]context.CancelFunc) // path → stop
	)
	defer func() {
		mu.Lock()
		for _, stop := range tails {
			stop()
		}
		mu.Unlock()
	}()

	// 同一条 gRPC 流上有多个尾随协程（中控订阅 + 事件回流自动订阅），
	// Send 必须串行，否则并发写流会打挂整条 LogStream（e2e_m2 ws logs 红灯根因）。
	sendChunk := func(chunk *pb.LogChunk) error {
		mu.Lock()
		defer mu.Unlock()
		return stream.Send(chunk)
	}

	for {
		ctrl, err := stream.Recv()
		if err != nil {
			return err
		}
		path := ctrl.GetPath()
		switch ctrl.GetAction() {
		case "subscribe":
			mu.Lock()
			if stop, ok := tails[path]; ok {
				stop()
				delete(tails, path)
			}
			tailCtx, stop := context.WithCancel(streamCtx)
			tails[path] = stop
			mu.Unlock()
			go tailFile(tailCtx, sendChunk, nodeID, path, int(ctrl.GetTailLines()), logf)
		case "unsubscribe":
			mu.Lock()
			if stop, ok := tails[path]; ok {
				stop()
				delete(tails, path)
			}
			mu.Unlock()
		}
	}
}

// tailFile 先回放文件尾部 tailLines 行，再轮询推送新增字节。
// 文件尚不存在时等待出现（事件回流订阅的日志路径由 OpenResty/防爆破
// 首次写事件时创建），不退出——退出会让订阅静默失效。
func tailFile(ctx context.Context, send func(*pb.LogChunk) error, nodeID, path string, tailLines int, logf func(string, ...any)) {
	if path == "" || !strings.HasPrefix(path, "/") {
		_ = send(&pb.LogChunk{NodeId: nodeID, Ts: nowMs(), Data: []byte("logs: path must be absolute")})
		return
	}
	ticker := time.NewTicker(500 * time.Millisecond)
	defer ticker.Stop()

	offset := int64(0)
	if tailLines > 0 {
		if data, err := os.ReadFile(path); err == nil {
			lines := strings.Split(string(data), "\n")
			if len(lines) > tailLines {
				lines = lines[len(lines)-tailLines:]
			}
			offset = int64(len(data))
			if err := send(&pb.LogChunk{NodeId: nodeID, Ts: nowMs(), Data: []byte(strings.Join(lines, "\n"))}); err != nil {
				return
			}
		}
	} else if fi, err := os.Stat(path); err == nil {
		offset = fi.Size()
	}

	var f *os.File
	for {
		if ctx.Err() != nil {
			return
		}
		var err error
		f, err = os.Open(path)
		if err == nil {
			break
		}
		logf("logs: open %s: %v (waiting for file)", path, err)
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
	}
	defer f.Close()

	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
		fi, err := f.Stat()
		if err != nil {
			return
		}
		size := fi.Size()
		if size < offset { // 文件被轮转，从头读
			offset = 0
			if _, err := f.Seek(0, io.SeekStart); err != nil {
				return
			}
		}
		if size == offset {
			continue
		}
		buf := make([]byte, size-offset)
		if _, err := io.ReadFull(f, buf); err != nil {
			return
		}
		offset = size
		if err := send(&pb.LogChunk{NodeId: nodeID, Ts: nowMs(), Data: buf}); err != nil {
			return
		}
	}
}
