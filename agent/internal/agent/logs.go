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

// PumpLogs 维持日志流：中控下发 subscribe(path, tail) 后开始追尾；
// 新订阅替换旧订阅；unsubscribe 停止追尾。
func PumpLogs(ctx context.Context, client pb.AgentServiceClient, nodeID string, logf func(string, ...any)) error {
	streamCtx, cancel := context.WithCancel(metadata.AppendToOutgoingContext(ctx, "x-node-id", nodeID))
	defer cancel()

	stream, err := client.LogStream(streamCtx)
	if err != nil {
		return err
	}

	var (
		mu       sync.Mutex
		stopTail context.CancelFunc = func() {}
	)
	defer stopTail()

	for {
		ctrl, err := stream.Recv()
		if err != nil {
			return err
		}
		switch ctrl.GetAction() {
		case "subscribe":
			mu.Lock()
			stopTail()
			tailCtx, stop := context.WithCancel(streamCtx)
			stopTail = stop
			mu.Unlock()
			go tailFile(tailCtx, stream, nodeID, ctrl.GetPath(), int(ctrl.GetTailLines()), logf)
		case "unsubscribe":
			mu.Lock()
			stopTail()
			stopTail = func() {}
			mu.Unlock()
		}
	}
}

// tailFile 先回放文件尾部 tailLines 行，再轮询推送新增字节。
func tailFile(ctx context.Context, stream pb.AgentService_LogStreamClient, nodeID, path string, tailLines int, logf func(string, ...any)) {
	if path == "" || !strings.HasPrefix(path, "/") {
		_ = stream.Send(&pb.LogChunk{NodeId: nodeID, Ts: nowMs(), Data: []byte("logs: path must be absolute")})
		return
	}
	offset := int64(0)
	if tailLines > 0 {
		if data, err := os.ReadFile(path); err == nil {
			lines := strings.Split(string(data), "\n")
			if len(lines) > tailLines {
				lines = lines[len(lines)-tailLines:]
			}
			offset = int64(len(data))
			_ = stream.Send(&pb.LogChunk{NodeId: nodeID, Ts: nowMs(), Data: []byte(strings.Join(lines, "\n"))})
		}
	} else if fi, err := os.Stat(path); err == nil {
		offset = fi.Size()
	}

	ticker := time.NewTicker(500 * time.Millisecond)
	defer ticker.Stop()
	f, err := os.Open(path)
	if err != nil {
		logf("logs: open %s: %v", path, err)
		return
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
		if err := stream.Send(&pb.LogChunk{NodeId: nodeID, Ts: nowMs(), Data: buf}); err != nil {
			return
		}
	}
}
