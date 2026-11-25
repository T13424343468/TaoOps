// backlog D1：docker pull 进度结构化解析测试。
package docker

import (
	"encoding/json"
	"strings"
	"testing"
)

const samplePull = `1b9b4a8738e4: Pulling fs layer
c8b0a4aa6e6d: Waiting
1b9b4a8738e4: Downloading [====>      ]  1.2MB/10MB
1b9b4a8738e4: Verifying Checksum
1b9b4a8738e4: Pull complete
c8b0a4aa6e6d: Pull complete
Digest: sha256:abc123def456
Status: Downloaded newer image for nginx:1.25`

func TestParsePullProgress_LayersAndBytes(t *testing.T) {
	p := ParsePullProgress(samplePull)
	if len(p.Layers) != 2 {
		t.Fatalf("want 2 layers (deduped by id), got %d: %+v", len(p.Layers), p.Layers)
	}
	first := p.Layers[0]
	if first.ID != "1b9b4a8738e4" {
		t.Fatalf("layer id = %q", first.ID)
	}
	// 同层多事件去重后保留最终状态：Pull complete（进度字节不再保留）。
	if first.Status != "Pull complete" {
		t.Fatalf("final status = %q", first.Status)
	}
	if p.Digest != "sha256:abc123def456" {
		t.Fatalf("digest = %q", p.Digest)
	}
	if !strings.Contains(p.Status, "Downloaded newer image") {
		t.Fatalf("status = %q", p.Status)
	}
	if p.Raw != samplePull {
		t.Fatalf("raw not preserved")
	}
}

func TestParsePullProgress_ProgressBytes(t *testing.T) {
	raw := "1b9b4a8738e4: Downloading [====>      ]  1.2MB/10MB\n"
	p := ParsePullProgress(raw)
	if len(p.Layers) != 1 {
		t.Fatalf("layers = %d", len(p.Layers))
	}
	if p.Layers[0].CurrentBytes != 1_200_000 || p.Layers[0].TotalBytes != 10_000_000 {
		t.Fatalf("bytes = %d/%d", p.Layers[0].CurrentBytes, p.Layers[0].TotalBytes)
	}
}

func TestParsePullProgress_EmptyAndGarbage(t *testing.T) {
	p := ParsePullProgress("")
	if len(p.Layers) != 0 {
		t.Fatalf("empty input should have no layers")
	}
	p2 := ParsePullProgress("random noise line\nanother line")
	if len(p2.Layers) != 0 {
		t.Fatalf("garbage should not produce layers, got %+v", p2.Layers)
	}
}

func TestPullProgressJSON_Valid(t *testing.T) {
	var v map[string]any
	if err := json.Unmarshal(PullProgressJSON(samplePull), &v); err != nil {
		t.Fatalf("json: %v", err)
	}
	if _, ok := v["layers"]; !ok {
		t.Fatalf("missing layers key: %v", v)
	}
}
