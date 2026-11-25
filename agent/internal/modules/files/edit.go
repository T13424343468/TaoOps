// Online text file editing (FR-A25): read/write small text files with
// explicit binary rejection and timestamped .bak backups (keep 5).
package files

import (
	"bytes"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

// EditMaxBytes caps both file_read and file_write payloads (PRD FR-A25: ≤1MB).
const EditMaxBytes int64 = 1 << 20

// BackupKeep is the number of most-recent .bak.<ts> copies retained per file.
const BackupKeep = 5

// validateEditPath enforces absolute, canonical form without any ".."
// segment and rejects symbolic-link components (anti-escape, same
// discipline as the migrate module).
func validateEditPath(p string) (string, error) {
	if p == "" {
		return "", fmt.Errorf("files: path is required")
	}
	if !filepath.IsAbs(p) {
		return "", fmt.Errorf("files: path must be an absolute path")
	}
	for _, seg := range strings.Split(p, string(filepath.Separator)) {
		if seg == ".." {
			return "", fmt.Errorf("files: path must not contain .. segments")
		}
	}
	if c := filepath.Clean(p); c != p {
		return "", fmt.Errorf("files: path must be in canonical (clean) form")
	}
	cur := p
	for {
		parent := filepath.Dir(cur)
		if parent == cur {
			return p, nil
		}
		if fi, err := os.Lstat(cur); err == nil && fi.Mode()&os.ModeSymlink != 0 {
			return "", fmt.Errorf("files: path component is a symbolic link: %s", cur)
		}
		cur = parent
	}
}

// ReadTextFile returns the content of a small text file for online
// editing. Files over 1MB and binary content (NUL byte) are rejected
// with explicit errors.
func ReadTextFile(path string) (string, error) {
	p, err := validateEditPath(path)
	if err != nil {
		return "", err
	}
	fi, err := os.Stat(p)
	if err != nil {
		return "", err
	}
	if fi.Size() > EditMaxBytes {
		return "", fmt.Errorf("files: %s exceeds %d bytes, online edit not supported", p, EditMaxBytes)
	}
	data, err := os.ReadFile(p)
	if err != nil {
		return "", err
	}
	if bytes.IndexByte(data, 0) >= 0 {
		return "", fmt.Errorf("files: %s is a binary file, online edit not supported", p)
	}
	return string(data), nil
}

// WriteTextFile saves edited content. Before overwriting an existing
// file it takes a same-directory <name>.bak.<unix-ts> snapshot and
// prunes older backups beyond BackupKeep. File mode is preserved for
// existing files; new files get 0644.
func WriteTextFile(path, content string, now time.Time) error {
	p, err := validateEditPath(path)
	if err != nil {
		return err
	}
	if int64(len(content)) > EditMaxBytes {
		return fmt.Errorf("files: content exceeds %d bytes, online edit not supported", EditMaxBytes)
	}
	if bytes.IndexByte([]byte(content), 0) >= 0 {
		return fmt.Errorf("files: content is binary, online edit not supported")
	}
	mode := os.FileMode(0o644)
	if fi, err := os.Stat(p); err == nil {
		mode = fi.Mode().Perm()
		if err := backupFile(p, now); err != nil {
			return err
		}
	}
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		return err
	}
	return os.WriteFile(p, []byte(content), mode)
}

// backupFile copies path to <name>.bak.<ts> and prunes older backups.
func backupFile(p string, now time.Time) error {
	ts := now.Unix()
	bak := fmt.Sprintf("%s.bak.%d", p, ts)
	data, err := os.ReadFile(p)
	if err != nil {
		return err
	}
	if err := os.WriteFile(bak, data, 0o644); err != nil {
		return err
	}
	pruneBackups(p)
	return nil
}

// pruneBackups removes the oldest .bak.<ts> copies beyond BackupKeep.
// Prune failures do not block the write.
func pruneBackups(p string) {
	matches, err := filepath.Glob(p + ".bak.*")
	if err != nil || len(matches) <= BackupKeep {
		return
	}
	sort.Strings(matches) // lexicographic on unix ts == chronological
	excess := matches[:len(matches)-BackupKeep]
	for _, m := range excess {
		_ = os.Remove(m)
	}
}
