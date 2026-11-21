// 终端会话泵：把中控经 TermStream 转发的键盘输入写入 bash 进程，
// 把终端输出按块回传（契约 §2 TermStream）。会话由 session_id 标识。
package agent

import (
	"context"
	"io"
	"os/exec"
	"sync"

	"google.golang.org/grpc/metadata"

	pb "taoops/agent/pb"
)

// PumpTerms 维持一条 TermStream：输入写入 shell，输出回传中控。
// shell 进程随流结束而终止；返回时流已不可用。
func PumpTerms(ctx context.Context, client pb.AgentServiceClient, nodeID string, logf func(string, ...any)) error {
	sessionID := newSessionID()
	streamCtx, cancel := context.WithCancel(metadata.AppendToOutgoingContext(ctx,
		"x-node-id", nodeID, "x-session-id", sessionID))
	defer cancel()

	stream, err := client.TermStream(streamCtx)
	if err != nil {
		return err
	}
	logf("term session attached: %s", sessionID)

	cmd := exec.Command("bash", "-i")
	if err := cmd.Err; err != nil {
		cmd = exec.Command("sh", "-i")
	}
	stdin, err := cmd.StdinPipe()
	if err != nil {
		return err
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return err
	}
	cmd.Stderr = cmd.Stdout // 合并 stderr 进终端输出
	if err := cmd.Start(); err != nil {
		// 无 bash/sh 的环境：会话保持但无 shell，输入被丢弃并回显提示。
		logf("term: shell unavailable: %v", err)
	}

	var wg sync.WaitGroup
	wg.Add(1)
	go func() { // shell 输出 → 中控
		defer wg.Done()
		buf := make([]byte, 4096)
		for {
			n, err := stdout.Read(buf)
			if n > 0 {
				chunk := make([]byte, n)
				copy(chunk, buf[:n])
				_ = stream.Send(&pb.TermData{NodeId: nodeID, SessionId: sessionID, Data: chunk})
			}
			if err != nil {
				if err != io.EOF {
					logf("term stdout: %v", err)
				}
				return
			}
		}
	}()

	// 中控输入 → shell stdin；流结束或 shell 退出即收尾。
	go func() {
		defer func() {
			if cmd.Process != nil {
				_ = cmd.Process.Kill()
			}
			cancel()
		}()
		for {
			in, err := stream.Recv()
			if err != nil {
				return
			}
			if _, err := stdin.Write(in.GetData()); err != nil {
				return
			}
		}
	}()

	_ = cmd.Wait()
	wg.Wait()
	return nil
}
