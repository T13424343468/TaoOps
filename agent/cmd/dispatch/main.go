// 一次性 E2E 客户端：对中控下发指令并断言回传结果。
// 用法：dispatch <addr> <node_id> [command [args...]]；command 默认 echo。
// 传输层：TAOOPS_INSECURE=1 走明文（仅本地回环），否则 TLS+TOFU（契约 §1）。
package main

import (
	"context"
	"fmt"
	"os"
	"strings"
	"time"

	"google.golang.org/grpc"
	"taoops/agent/internal/agent"
	pb "taoops/agent/pb"
)

func main() {
	if len(os.Args) < 3 {
		fmt.Println("FAIL: usage: dispatch <addr> <node_id> [command [args...]]")
		os.Exit(1)
	}
	addr, nodeID := os.Args[1], os.Args[2]
	command := "echo"
	var args []string
	if len(os.Args) > 3 {
		command = os.Args[3]
		args = os.Args[4:]
	}

	creds, err := agent.DialCredentials(addr)
	if err != nil {
		fmt.Println("FAIL: credentials", err)
		os.Exit(1)
	}
	conn, err := grpc.NewClient(addr, grpc.WithTransportCredentials(creds))
	if err != nil {
		fmt.Println("FAIL: dial", err)
		os.Exit(1)
	}
	defer conn.Close()
	client := pb.NewAgentServiceClient(conn)
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	res, err := client.Dispatch(ctx, &pb.DispatchRequest{NodeId: nodeID, Command: command, Args: args})
	if err != nil {
		fmt.Printf("FAIL: dispatch %s: %v\n", command, err)
		os.Exit(1)
	}
	fmt.Printf("RESULT exit=%d output=%q\n", res.GetExitCode(), res.GetOutput())
	if res.GetExitCode() != 0 {
		os.Exit(1)
	}
	_ = strings.TrimSpace
}
