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
			// 下发桥仅回传 Output：结构化结果按 FR-A17 约定落 Output JSON。
			return registry.Result{ExitCode: 0, Output: string(data), Data: map[string]any{"summary": json.RawMessage(data)}}, nil
		case "summary":
			site, _ := args["site"].(string)
			from, _ := args["from"].(string)
			to, _ := args["to"].(string)
			recs, err := m.Summary(site, from, to)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			// 视图只暴露派生字段（date/site/pv/uv/status/bytes/degraded/top_*），
			// 不把 HLL/CMS 草图原始字节塞进回传。
			type topEntry struct {
				Key   string `json:"key"`
				Count uint64 `json:"count"`
			}
			type dayView struct {
				Date     string            `json:"date"`
				Site     string            `json:"site"`
				PV       uint64            `json:"pv"`
				UV       uint64            `json:"uv"`
				Status   map[string]uint64 `json:"status"`
				Bytes    uint64            `json:"bytes"`
				Degraded bool              `json:"degraded"`
				TopIPs   []topEntry        `json:"top_ips"`
				TopURLs  []topEntry        `json:"top_urls"`
			}
			views := make([]dayView, 0, len(recs))
			for _, r := range recs {
				ips := make([]topEntry, 0, len(r.TopIPs))
				for _, e := range r.TopIPs {
					ips = append(ips, topEntry{Key: e.Key, Count: e.Count})
				}
				urls := make([]topEntry, 0, len(r.TopURLs))
				for _, e := range r.TopURLs {
					urls = append(urls, topEntry{Key: e.Key, Count: e.Count})
				}
				views = append(views, dayView{
					Date: r.Date, Site: r.Site, PV: r.PV, UV: r.UV,
					Status: r.Status, Bytes: r.Bytes, Degraded: r.Degraded,
					TopIPs: ips, TopURLs: urls,
				})
			}
			data, _ := json.Marshal(views)
			return registry.Result{ExitCode: 0, Output: string(data), Data: map[string]any{"records": json.RawMessage(data)}}, nil
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
