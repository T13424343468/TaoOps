// Package dispatch routes server-dispatched commands to module handlers
// or the shell whitelist. Contract source: src/docs/protocol-contract.md §4.
package dispatch

import (
	"context"
	"fmt"
	"os/exec"
	"strings"
	"time"

	"taoops/agent/internal/modules/registry"
)

// MaxOutputBytes caps merged command output at 1MB (contract §2).
const MaxOutputBytes = 1 << 20

// ShellWhitelist mirrors the server-side shell allowlist (contract §4).
var ShellWhitelist = map[string]bool{
	"echo": true, "uname": true, "df": true, "free": true, "uptime": true,
}

// Dispatcher executes commands on behalf of the server: module commands
// go through the module registry, shell commands run via direct exec
// (never through a shell, so injection characters are inert).
type Dispatcher struct {
	reg *registry.Registry
}

// New returns a Dispatcher backed by r.
func New(r *registry.Registry) *Dispatcher { return &Dispatcher{reg: r} }

// Execute runs command with args under ctx and returns (exitCode, output).
// Module commands receive args as key=value pairs; unknown commands exit 127.
func (d *Dispatcher) Execute(ctx context.Context, command string, args []string) (int32, string) {
	if h, ok := d.reg.Get(command); ok {
		return d.runModule(ctx, command, h, args)
	}
	if ShellWhitelist[command] {
		return runShell(ctx, command, args)
	}
	return 127, fmt.Sprintf("unknown command: %s", command)
}

func (d *Dispatcher) runModule(ctx context.Context, command string, h registry.Handler, args []string) (int32, string) {
	moduleArgs, skipped := parseKeyValues(args)
	if len(skipped) > 0 {
		return 2, fmt.Sprintf("dispatch: %s: args without '=' ignored: %s", command, strings.Join(skipped, ", "))
	}

	type outcome struct {
		res registry.Result
		err error
	}
	done := make(chan outcome, 1)
	go func() {
		res, err := h(moduleArgs)
		done <- outcome{res, err}
	}()
	select {
	case <-ctx.Done():
		return 124, fmt.Sprintf("dispatch: %s: timed out", command)
	case o := <-done:
		if o.err != nil {
			code := o.res.ExitCode
			if code == 0 {
				code = 1
			}
			out := o.res.Output
			if out == "" {
				out = o.err.Error()
			} else {
				out = out + "\n" + o.err.Error()
			}
			return int32(code), truncate(out)
		}
		return int32(o.res.ExitCode), truncate(o.res.Output)
	}
}

// runShell executes a whitelisted command directly (argv, no shell).
func runShell(ctx context.Context, command string, args []string) (int32, string) {
	cmd := exec.CommandContext(ctx, command, args...)
	out, err := cmd.CombinedOutput()
	if err != nil {
		if ctx.Err() != nil {
			return 124, "dispatch: timed out"
		}
		if _, isExit := err.(*exec.ExitError); !isExit {
			return 126, truncate(fmt.Sprintf("%s\n%v", out, err))
		}
	}
	return exitCodeOf(cmd), truncate(string(out))
}

func exitCodeOf(cmd *exec.Cmd) int32 {
	if cmd.ProcessState == nil {
		return 1
	}
	return int32(cmd.ProcessState.ExitCode())
}

// parseKeyValues converts ["op=list","name=x"] to {"op":"list","name":"x"};
// entries without '=' are returned separately for reporting.
func parseKeyValues(args []string) (map[string]any, []string) {
	m := make(map[string]any, len(args))
	var skipped []string
	for _, a := range args {
		k, v, ok := strings.Cut(a, "=")
		if !ok || k == "" {
			skipped = append(skipped, a)
			continue
		}
		m[k] = v
	}
	return m, skipped
}

func truncate(s string) string {
	if len(s) > MaxOutputBytes {
		return s[:MaxOutputBytes]
	}
	return s
}

// TimeoutContext wraps a deadline derived from timeoutMs (0 → default 30s).
func TimeoutContext(parent context.Context, timeoutMs int32) (context.Context, context.CancelFunc) {
	if timeoutMs <= 0 {
		timeoutMs = 30000
	}
	return context.WithTimeout(parent, time.Duration(timeoutMs)*time.Millisecond)
}
