package dispatch

import (
	"context"
	"errors"
	"strings"
	"testing"
	"time"

	"taoops/agent/internal/modules/registry"
)

var errBoom = errors.New("boom")

func TestExecute_ModuleCommand_RoutesToRegistry(t *testing.T) {
	r := registry.New()
	if err := r.Register("echo.manage", func(args map[string]any) (registry.Result, error) {
		if args["op"] != "list" {
			t.Fatalf("op = %v, want list", args["op"])
		}
		return registry.Result{ExitCode: 0, Output: "ok"}, nil
	}); err != nil {
		t.Fatal(err)
	}
	d := New(r)
	code, out := d.Execute(context.Background(), "echo.manage", []string{"op=list"})
	if code != 0 || out != "ok" {
		t.Fatalf("code=%d out=%q", code, out)
	}
}

func TestExecute_ModuleCommand_ErrorKeepsExitCode(t *testing.T) {
	r := registry.New()
	if err := r.Register("x.manage", func(args map[string]any) (registry.Result, error) {
		return registry.Result{ExitCode: 3, Output: "boom"}, errBoom
	}); err != nil {
		t.Fatal(err)
	}
	d := New(r)
	code, out := d.Execute(context.Background(), "x.manage", nil)
	if code != 3 || !strings.Contains(out, "boom") {
		t.Fatalf("code=%d out=%q", code, out)
	}
}

func TestExecute_ModuleCommand_ZeroExitOnErrorBecomesOne(t *testing.T) {
	r := registry.New()
	if err := r.Register("y.manage", func(args map[string]any) (registry.Result, error) {
		return registry.Result{}, errBoom
	}); err != nil {
		t.Fatal(err)
	}
	d := New(r)
	code, _ := d.Execute(context.Background(), "y.manage", nil)
	if code != 1 {
		t.Fatalf("code=%d, want 1", code)
	}
}

func TestExecute_ModuleCommand_MissingEqualsSkipped(t *testing.T) {
	r := registry.New()
	if err := r.Register("nothere.manage", func(args map[string]any) (registry.Result, error) {
		return registry.Result{ExitCode: 0, Output: "ok"}, nil
	}); err != nil {
		t.Fatal(err)
	}
	d := New(r)
	code, out := d.Execute(context.Background(), "nothere.manage", []string{"op", "k=v"})
	if code != 2 || !strings.Contains(out, "op") {
		t.Fatalf("code=%d out=%q", code, out)
	}
}

func TestExecute_ModuleCommand_Timeout(t *testing.T) {
	r := registry.New()
	if err := r.Register("slow.manage", func(args map[string]any) (registry.Result, error) {
		time.Sleep(200 * time.Millisecond)
		return registry.Result{ExitCode: 0, Output: "late"}, nil
	}); err != nil {
		t.Fatal(err)
	}
	d := New(r)
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Millisecond)
	defer cancel()
	code, out := d.Execute(ctx, "slow.manage", nil)
	if code != 124 || !strings.Contains(out, "timed out") {
		t.Fatalf("code=%d out=%q", code, out)
	}
}

func TestExecute_ShellWhitelist_Echo(t *testing.T) {
	d := New(registry.New())
	code, out := d.Execute(context.Background(), "echo", []string{"hello", "world"})
	if code != 0 || strings.TrimSpace(out) != "hello world" {
		t.Fatalf("code=%d out=%q", code, out)
	}
}

func TestExecute_UnknownCommand_Exit127(t *testing.T) {
	d := New(registry.New())
	code, out := d.Execute(context.Background(), "rm", []string{"-rf"})
	if code != 127 || !strings.Contains(out, "unknown command") {
		t.Fatalf("code=%d out=%q", code, out)
	}
}

func TestExecute_ShellInjectionCharsInert(t *testing.T) {
	d := New(registry.New())
	// echo 不经 shell，注入字符作为普通参数原样输出
	code, out := d.Execute(context.Background(), "echo", []string{"a;b|c"})
	if code != 0 || strings.TrimSpace(out) != "a;b|c" {
		t.Fatalf("code=%d out=%q", code, out)
	}
}

func TestParseKeyValues(t *testing.T) {
	m, skipped := parseKeyValues([]string{"op=list", "name=a b", "bad", "=novalue"})
	if m["op"] != "list" || m["name"] != "a b" {
		t.Fatalf("m=%v", m)
	}
	if len(skipped) != 2 {
		t.Fatalf("skipped=%v", skipped)
	}
}
