// Package files implements FR-A4 (file management): directory tree,
// chunked upload with resume, online edit, permission change and
// compress/decompress. Per PRD: files >= 1GB are "large" and upload in
// 4MB chunks with resumable assembly.
package files

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"taoops/agent/internal/modules/registry"
)

// LargeFileThreshold matches PRD: >= 1GB counts as a large file.
const LargeFileThreshold int64 = 1 << 30

// ChunkSize matches PRD: 4MB per chunk.
const ChunkSize int64 = 4 << 20

// SessionDir hosts in-progress chunk assemblies.
type Assembler struct {
	Root string
}

// NewAssembler builds an Assembler over a session directory.
func NewAssembler(root string) *Assembler { return &Assembler{Root: root} }

type chunkManifest struct {
	Name     string  `json:"name"`
	Total    int64   `json:"total"`
	Received []int64 `json:"received"` // chunk indexes present
}

// PutChunk stores one chunk of an upload. Chunks land in
// <Root>/<uploadID>/part-<index>; the manifest tracks progress so a
// client can resume after disconnect.
func (a *Assembler) PutChunk(uploadID string, index int64, data []byte, total int64) (receivedCount int, err error) {
	if uploadID == "" || index < 0 || total <= 0 {
		return 0, fmt.Errorf("files: invalid chunk arguments")
	}
	if int64(len(data)) > ChunkSize {
		return 0, fmt.Errorf("files: chunk exceeds %d bytes", ChunkSize)
	}
	dir := filepath.Join(a.Root, sanitizeID(uploadID))
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return 0, err
	}
	part := filepath.Join(dir, fmt.Sprintf("part-%06d", index))
	if err := os.WriteFile(part, data, 0o644); err != nil {
		return 0, err
	}
	manifest, _ := a.readManifest(dir)
	if manifest == nil {
		manifest = &chunkManifest{Name: uploadID, Total: total}
	}
	if !containsInt64(manifest.Received, index) {
		manifest.Received = append(manifest.Received, index)
	}
	if err := a.writeManifest(dir, manifest); err != nil {
		return 0, err
	}
	return len(manifest.Received), nil
}

// ReceivedChunks reports which chunks of an upload are already stored,
// so the client can resume from the right offset.
func (a *Assembler) ReceivedChunks(uploadID string) ([]int64, error) {
	manifest, err := a.readManifest(filepath.Join(a.Root, sanitizeID(uploadID)))
	if err != nil {
		return nil, err
	}
	if manifest == nil {
		return []int64{}, nil
	}
	return manifest.Received, nil
}

// Finish assembles all chunks into the destination file and cleans the
// session directory. Chunks must be contiguous starting at 0.
func (a *Assembler) Finish(uploadID, destPath string) error {
	dir := filepath.Join(a.Root, sanitizeID(uploadID))
	manifest, err := a.readManifest(dir)
	if err != nil || manifest == nil {
		return fmt.Errorf("files: no upload session %q", uploadID)
	}
	sort.Slice(manifest.Received, func(i, j int) bool { return manifest.Received[i] < manifest.Received[j] })
	for i, idx := range manifest.Received {
		if idx != int64(i) {
			return fmt.Errorf("files: chunk %d missing, cannot finish", i)
		}
	}
	if err := os.MkdirAll(filepath.Dir(destPath), 0o755); err != nil {
		return err
	}
	out, err := os.OpenFile(destPath, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o644)
	if err != nil {
		return err
	}
	defer out.Close()
	for _, idx := range manifest.Received {
		part := filepath.Join(dir, fmt.Sprintf("part-%06d", idx))
		data, err := os.ReadFile(part)
		if err != nil {
			return err
		}
		if _, err := out.Write(data); err != nil {
			return err
		}
	}
	return os.RemoveAll(dir)
}

func (a *Assembler) readManifest(dir string) (*chunkManifest, error) {
	data, err := os.ReadFile(filepath.Join(dir, "manifest.json"))
	if err != nil {
		if os.IsNotExist(err) {
			return nil, nil
		}
		return nil, err
	}
	m := &chunkManifest{}
	for _, line := range strings.Split(string(data), "\n") {
		if strings.HasPrefix(line, "total=") {
			fmt.Sscanf(line, "total=%d", &m.Total)
		}
		if strings.HasPrefix(line, "received=") {
			m.Received = parseInt64s(strings.TrimPrefix(line, "received="))
		}
	}
	return m, nil
}

func (a *Assembler) writeManifest(dir string, m *chunkManifest) error {
	var b strings.Builder
	fmt.Fprintf(&b, "total=%d\n", m.Total)
	fmt.Fprintf(&b, "received=%s\n", joinInt64s(m.Received))
	return os.WriteFile(filepath.Join(dir, "manifest.json"), []byte(b.String()), 0o644)
}

func parseInt64s(s string) []int64 {
	var out []int64
	for _, p := range strings.Split(strings.TrimSpace(s), ",") {
		if p == "" {
			continue
		}
		var v int64
		fmt.Sscanf(p, "%d", &v)
		out = append(out, v)
	}
	return out
}

func joinInt64s(xs []int64) string {
	parts := make([]string, len(xs))
	for i, x := range xs {
		parts[i] = fmt.Sprintf("%d", x)
	}
	return strings.Join(parts, ",")
}

func containsInt64(xs []int64, v int64) bool {
	for _, x := range xs {
		if x == v {
			return true
		}
	}
	return false
}

func sanitizeID(s string) string {
	var b strings.Builder
	for _, r := range s {
		switch {
		case r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z', r >= '0' && r <= '9', r == '-', r == '_':
			b.WriteRune(r)
		default:
			b.WriteRune('-')
		}
	}
	return b.String()
}

// Entry is one row of a directory listing.
type Entry struct {
	Name  string `json:"name"`
	IsDir bool   `json:"is_dir"`
	Size  int64  `json:"size"`
	Mode  string `json:"mode"`
}

// List returns the entries of a directory sorted by name.
func List(dir string) ([]Entry, error) {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil, err
	}
	out := make([]Entry, 0, len(entries))
	for _, e := range entries {
		info, err := e.Info()
		if err != nil {
			continue
		}
		out = append(out, Entry{Name: e.Name(), IsDir: e.IsDir(), Size: info.Size(), Mode: info.Mode().String()})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Name < out[j].Name })
	return out, nil
}

// ReadFile returns file content for online editing.
func ReadFile(path string) (string, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return "", err
	}
	return string(data), nil
}

// WriteFile saves edited content immediately (PRD: 编辑保存即时生效).
func WriteFile(path, content string) error {
	return os.WriteFile(path, []byte(content), 0o644)
}

// Chmod changes file permissions.
func Chmod(path string, mode os.FileMode) error { return os.Chmod(path, mode) }

// IsLarge reports whether a size crosses the PRD large-file threshold.
func IsLarge(size int64) bool { return size >= LargeFileThreshold }

// Handler returns the registry handler "files.manage".
func Handler(a *Assembler) registry.Handler {
	return func(args map[string]any) (registry.Result, error) {
		op, _ := args["op"].(string)
		switch op {
		case "chunk":
			id, _ := args["id"].(string)
			idx, _ := toInt64(args["index"])
			total, _ := toInt64(args["total"])
			data, _ := args["data"].(string)
			n, err := a.PutChunk(id, idx, []byte(data), total)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "ok", Data: map[string]any{"received": n}}, nil
		case "resume":
			id, _ := args["id"].(string)
			got, err := a.ReceivedChunks(id)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "ok", Data: map[string]any{"received": got}}, nil
		case "finish":
			id, _ := args["id"].(string)
			dest, _ := args["dest"].(string)
			if err := a.Finish(id, dest); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "assembled " + dest}, nil
		case "list":
			dir, _ := args["dir"].(string)
			entries, err := List(dir)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "ok", Data: map[string]any{"entries": entries}}, nil
		case "read":
			path, _ := args["path"].(string)
			content, err := ReadFile(path)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: content}, nil
		case "write":
			path, _ := args["path"].(string)
			content, _ := args["content"].(string)
			if err := WriteFile(path, content); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "saved"}, nil
		case "file_read": // FR-A25: online edit read (≤1MB, binary rejected)
			path, _ := args["path"].(string)
			content, err := ReadTextFile(path)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: content}, nil
		case "file_write": // FR-A25: online edit write (≤1MB, binary rejected, .bak.<ts> keep 5)
			path, _ := args["path"].(string)
			content, _ := args["content"].(string)
			if err := WriteTextFile(path, content, time.Now()); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "saved"}, nil
		case "chmod":
			path, _ := args["path"].(string)
			mode, _ := toInt64(args["mode"])
			if err := Chmod(path, os.FileMode(mode)); err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "ok"}, nil
		}
		return registry.Result{ExitCode: 2}, fmt.Errorf("files: unknown op %q", op)
	}
}

// Register installs the files handlers into r.
func Register(r *registry.Registry, a *Assembler) error {
	return r.Register("files.manage", Handler(a))
}

func toInt64(v any) (int64, bool) {
	switch t := v.(type) {
	case int:
		return int64(t), true
	case int64:
		return t, true
	case float64:
		return int64(t), true
	}
	return 0, false
}
