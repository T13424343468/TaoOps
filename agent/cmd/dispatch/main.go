// 一次性 E2E 客户端：对中控下发 echo 指令并断言回传结果。
package main

import (
	"context"
	"fmt"
	"os"
	"time"

	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
	pb "taoops/agent/pb"
)

func main() {
	addr := os.Args[1]
	nodeID := os.Args[2]
	conn, err := grpc.NewClient(addr, grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		fmt.Println("FAIL: dial", err)
		os.Exit(1)
	}
	defer conn.Close()
	client := pb.NewAgentServiceClient(conn)
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	res, err := client.Dispatch(ctx, &pb.DispatchRequest{NodeId: nodeID, Command: "echo", Args: []string{"e2e-ok"}})
	if err != nil {
		fmt.Println("FAIL: dispatch", err)
		os.Exit(1)
	}
	if res.GetExitCode() != 0 || res.GetOutput() != "e2e-ok\n" {
		fmt.Printf("FAIL: result exit=%d output=%q\n", res.GetExitCode(), res.GetOutput())
		os.Exit(1)
	}
	fmt.Println("PASS: dispatch roundtrip")
}
