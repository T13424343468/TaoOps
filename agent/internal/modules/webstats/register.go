// Package webstats wires the FR-A12 website statistics handlers into the registry.
package webstats

import (
	"encoding/json"
	"fmt"

	"taoops/agent/internal/modules/registry"
)

// Handler returns the registry handler "webstats.manage".
func Handler(m *Manager) registry.Handler {
	return func(args map[string]any) (registry.Result, error) {
		op, _ := args["op"].(string)
		switch op {
		case "parse":
			site, _ := args["site"].(string)
			path, _ := args["path"].(string)
			summary, err := m.Parse(site, path)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			data, _ := json.Marshal(summary)
			return registry.Result{ExitCode: 0, Output: "parse ok", Data: map[string]any{"summary": json.RawMessage(data)}}, nil
		case "summary":
			site, _ := args["site"].(string)
			from, _ := args["from"].(string)
			to, _ := args["to"].(string)
			recs, err := m.Summary(site, from, to)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			data, _ := json.Marshal(recs)
			return registry.Result{ExitCode: 0, Output: "ok", Data: map[string]any{"records": json.RawMessage(data)}}, nil
		case "clear":
			site, _ := args["site"].(string)
			if err := m.Clear(site); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "cleared"}, nil
		case "export":
			site, _ := args["site"].(string)
			from, _ := args["from"].(string)
			to, _ := args["to"].(string)
			data, err := m.Export(site, from, to)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: string(data)}, nil
		}
		return registry.Result{ExitCode: 2}, fmt.Errorf("webstats: unknown op %q", op)
	}
}

// Register installs the webstats handler into r.
func Register(r *registry.Registry, m *Manager) error {
	return r.Register("webstats.manage", Handler(m))
}
