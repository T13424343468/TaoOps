// Package secguard wires the FR-C2 SSH brute-force handlers into the registry.
package secguard

import (
	"encoding/json"
	"fmt"

	"taoops/agent/internal/modules/registry"
)

// Handler returns the registry handler "sec.manage".
func Handler(m *Manager) registry.Handler {
	return func(args map[string]any) (registry.Result, error) {
		op, _ := args["op"].(string)
		switch op {
		case "scan":
			summary, err := m.Scan()
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			data, _ := json.Marshal(summary)
			return registry.Result{ExitCode: 0, Output: "scan ok", Data: map[string]any{"summary": json.RawMessage(data)}}, nil
		case "lockouts":
			lockouts, err := m.Lockouts()
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			data, _ := json.Marshal(lockouts)
			return registry.Result{ExitCode: 0, Output: "ok", Data: map[string]any{"lockouts": json.RawMessage(data)}}, nil
		}
		return registry.Result{ExitCode: 2}, fmt.Errorf("secguard: unknown op %q", op)
	}
}

// Register installs the secguard handlers into r.
func Register(r *registry.Registry, m *Manager) error {
	return r.Register("sec.manage", Handler(m))
}
