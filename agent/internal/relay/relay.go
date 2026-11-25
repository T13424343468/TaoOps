// Package relay implements the source-side gRPC sender for FR-A13
// node-to-node backup: it streams a local archive to the control plane in
// 4MB chunks (files-module chunk semantics), verifying per-chunk SHA-256
// acks and retrying failed chunks before the whole-archive confirmation.
// Contract source: docs/protocol-contract.md §7.
package relay

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"os"

	"google.golang.org/grpc"

	"taoops/agent/internal/agent"
	"taoops/agent/pb"
)

// ChunkSize matches files.ChunkSize / backup.RelayChunkSize (4MB).
const ChunkSize int64 = 4 << 20

// maxChunkRetries bounds retransmission of a single failed chunk.
const maxChunkRetries = 3

// Sender dials the control plane and pushes archive chunks over BackupRelay.
type Sender struct {
	serverURL string
	dial      func(ctx context.Context) (pb.AgentServiceClient, func(), error)
}

// NewSender returns a Sender dialing serverURL with the agent's pinned-TLS
// credentials (same transport as the worker).
func NewSender(serverURL string) *Sender {
	return &Sender{serverURL: serverURL, dial: defaultDial(serverURL)}
}

// Send streams filePath to targetNode under uploadID. name/source travel on
// the first frame for the target-side relay_begin; onProgress reports
// cumulative acknowledged bytes. Returns after the final whole-archive
// confirmation ack.
func (s *Sender) Send(ctx context.Context, targetNode, uploadID, name, filePath, source string, onProgress func(sent int64)) error {
	client, closeConn, err := s.dial(ctx)
	if err != nil {
		return fmt.Errorf("relay: dial: %w", err)
	}
	defer closeConn()

	// 第一遍：整包 SHA-256 与大小（relay_begin 磁盘预检与最终确认都要用）。
	total, fileSHA, err := hashFile(filePath)
	if err != nil {
		return err
	}
	chunkCount := (total + ChunkSize - 1) / ChunkSize

	f, err := os.Open(filePath)
	if err != nil {
		return fmt.Errorf("relay: open archive: %w", err)
	}
	defer f.Close()

	stream, err := client.BackupRelay(ctx)
	if err != nil {
		return fmt.Errorf("relay: open stream: %w", err)
	}

	sendChunk := func(idx int64, data []byte, eof bool) error {
		sum := sha256.Sum256(data)
		frame := &pb.RelayChunk{
			RelayId:      uploadID,
			TargetNodeId: targetNode,
			UploadId:     uploadID,
			Name:         name,
			ChunkIndex:   idx,
			TotalSize:    total,
			ChunkCount:   chunkCount,
			FileSha256:   fileSHA,
			ChunkSha256:  hex.EncodeToString(sum[:]),
			Data:         data,
			Eof:          eof,
			Source:       source,
		}
		if err := stream.Send(frame); err != nil {
			return fmt.Errorf("relay: send chunk %d: %w", idx, err)
		}
		// 逐分片等回执：ok 继续；nack 重试同一分片（断点续传语义）。
		for attempt := 0; attempt <= maxChunkRetries; attempt++ {
			ack, recvErr := stream.Recv()
			if recvErr != nil {
				return fmt.Errorf("relay: ack chunk %d: %w", idx, recvErr)
			}
			if ack.GetOk() {
				if onProgress != nil {
					onProgress(ack.GetRelayedBytes())
				}
				return nil
			}
			if attempt == maxChunkRetries {
				return fmt.Errorf("relay: chunk %d failed after %d retries: %s", idx, maxChunkRetries, ack.GetError())
			}
			// 重传同一分片。
			if err := stream.Send(frame); err != nil {
				return fmt.Errorf("relay: resend chunk %d: %w", idx, err)
			}
		}
		return nil
	}

	buf := make([]byte, ChunkSize)
	var idx int64
	for {
		n, readErr := io.ReadFull(f, buf)
		if readErr == io.EOF {
			break
		}
		if readErr != nil && readErr != io.ErrUnexpectedEOF {
			return fmt.Errorf("relay: read chunk %d: %w", idx, readErr)
		}
		eof := readErr == io.ErrUnexpectedEOF || int64(n) < ChunkSize
		data := buf[:n]
		if err := sendChunk(idx, data, eof); err != nil {
			return err
		}
		idx++
		if eof {
			break
		}
	}
	// 空档案或整除边界：显式补一个 eof 帧（data 为空）请求整包校验。
	if total == 0 || total%ChunkSize == 0 {
		if err := sendChunk(idx, nil, true); err != nil {
			return err
		}
	}
	return stream.CloseSend()
}

func hashFile(path string) (int64, string, error) {
	f, err := os.Open(path)
	if err != nil {
		return 0, "", fmt.Errorf("relay: open for hash: %w", err)
	}
	defer f.Close()
	h := sha256.New()
	n, err := io.Copy(h, f)
	if err != nil {
		return 0, "", fmt.Errorf("relay: hash: %w", err)
	}
	return n, hex.EncodeToString(h.Sum(nil)), nil
}

func defaultDial(serverURL string) func(ctx context.Context) (pb.AgentServiceClient, func(), error) {
	return func(ctx context.Context) (pb.AgentServiceClient, func(), error) {
		creds, err := agent.DialCredentials(serverURL)
		if err != nil {
			return nil, nil, err
		}
		conn, err := grpc.NewClient(serverURL, grpc.WithTransportCredentials(creds))
		if err != nil {
			return nil, nil, err
		}
		return pb.NewAgentServiceClient(conn), func() { _ = conn.Close() }, nil
	}
}
