// Module command wiring for tamper.guard (A7): op=status reports the current
// probe mode, watched paths and event counters. Registration follows the
// secguard pattern (agent/internal/modules/secguard/register.go).
package tamperguard

import (
	"encoding/json"
	"fmt"

	"taoops/agent/internal/modules/registry"
)

// Handler returns the registry handler "tamper.guard".
func Handler(g *Guard) registry.Handler {
	return func(args map[string]any) (registry.Result, error) {
		op, _ := args["op"].(string)
		switch op {
		case "status":
			status := g.Status()
			data, err := json.Marshal(status)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "ok", Data: map[string]any{"status": json.RawMessage(data)}}, nil
		}
		return registry.Result{ExitCode: 2}, fmt.Errorf("tamperguard: unknown op %q", op)
	}
}

// Register installs the tamper.guard handler into r.
func Register(r *registry.Registry, g *Guard) error {
	return r.Register("tamper.guard", Handler(g))
}
