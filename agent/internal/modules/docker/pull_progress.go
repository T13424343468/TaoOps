// backlog D1：docker pull 进度结构化。原始输出逐行解析为层数/字节进度，
// 供前端进度条替代原始日志展示。解析失败不致命——未识别行原样进 raw。
package docker

import (
	"encoding/json"
	"regexp"
	"strconv"
	"strings"
)

// PullLayer is one layer's progress snapshot.
type PullLayer struct {
	ID           string `json:"id"`
	Status       string `json:"status"`
	CurrentBytes int64  `json:"current_bytes,omitempty"`
	TotalBytes   int64  `json:"total_bytes,omitempty"`
}

// PullProgress is the structured result of one pull.
type PullProgress struct {
	Layers []PullLayer `json:"layers"`
	Digest string      `json:"digest,omitempty"`
	Status string      `json:"status,omitempty"`
	Raw    string      `json:"raw"`
}

var (
	layerLineRe   = regexp.MustCompile(`^([0-9a-f]{6,64}):\s*(.*)$`)
	progressRe    = regexp.MustCompile(`(\d+(?:\.\d+)?)\s*(B|kB|MB|GB|TB)\s*/\s*(\d+(?:\.\d+)?)\s*(B|kB|MB|GB|TB)`)
	digestLineRe  = regexp.MustCompile(`^Digest:\s*(\S+)`)
	statusLineRe  = regexp.MustCompile(`^Status:\s*(.*)$`)
	pullingFromRe = regexp.MustCompile(`^Pulling from\s+(.*)$`)
)

// unitBytes maps docker's decimal byte units (docker progress uses 1000-based).
var unitBytes = map[string]int64{"B": 1, "kB": 1_000, "MB": 1_000_000, "GB": 1_000_000_000, "TB": 1_000_000_000_000}

// parseProgressBytes extracts "1.2MB/10MB" into (current, total) bytes.
func parseProgressBytes(s string) (int64, int64, bool) {
	m := progressRe.FindStringSubmatch(s)
	if m == nil {
		return 0, 0, false
	}
	cur, err1 := strconv.ParseFloat(m[1], 64)
	tot, err2 := strconv.ParseFloat(m[3], 64)
	u1, ok1 := unitBytes[m[2]]
	u2, ok2 := unitBytes[m[4]]
	if err1 != nil || err2 != nil || !ok1 || !ok2 {
		return 0, 0, false
	}
	return int64(cur * float64(u1)), int64(tot * float64(u2)), true
}

// ParsePullProgress converts raw `docker pull` output into structured progress.
// 同层多行进度事件按层 ID 去重，保留最后一次（最终状态）。
func ParsePullProgress(raw string) PullProgress {
	p := PullProgress{Raw: raw}
	latest := map[string]int{} // layer id -> index in p.Layers
	for _, line := range strings.Split(raw, "\n") {
		line = strings.TrimSuffix(line, "\r")
		if line == "" {
			continue
		}
		if m := digestLineRe.FindStringSubmatch(line); m != nil {
			p.Digest = m[1]
			continue
		}
		if m := statusLineRe.FindStringSubmatch(line); m != nil {
			p.Status = m[1]
			continue
		}
		if m := layerLineRe.FindStringSubmatch(line); m != nil {
			layer := PullLayer{ID: m[1], Status: strings.TrimSpace(m[2])}
			if cur, tot, ok := parseProgressBytes(m[2]); ok {
				layer.CurrentBytes, layer.TotalBytes = cur, tot
			}
			if idx, seen := latest[layer.ID]; seen {
				p.Layers[idx] = layer
			} else {
				latest[layer.ID] = len(p.Layers)
				p.Layers = append(p.Layers, layer)
			}
			continue
		}
		if m := pullingFromRe.FindStringSubmatch(line); m != nil {
			// "Pulling from library/nginx"：镜像归属信息，并入 raw 即可。
			continue
		}
	}
	return p
}

// PullProgressJSON marshals ParsePullProgress; handler 直接落 Output。
func PullProgressJSON(raw string) []byte {
	b, err := json.Marshal(ParsePullProgress(raw))
	if err != nil {
		return []byte(`{"layers":[],"raw":` + strconv.Quote(raw) + `}`)
	}
	return b
}
