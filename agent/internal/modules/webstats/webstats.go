// Package webstats implements FR-A12 (website access statistics): streaming
// OpenResty/Nginx access-log parsing into per-site daily aggregates (PV, UV
// via HyperLogLog, status distribution, traffic, Top-K IPs/URLs) persisted
// under dataDir/webstats/, with inode+offset checkpoints for rotation-safe
// resume, retention cleanup and clear/export entry points.
package webstats

import (
	"bufio"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
)

// SiteName matches the PRD whitelist shape for site identifiers on disk.
var SiteName = regexp.MustCompile(`^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$`)

// MaxLineLen caps one log line at 64KB (PRD: longer lines truncate, never crash).
const MaxLineLen = 64 << 10

// DefaultRetentionDays is the aggregate retention (PRD: 180, configurable).
const DefaultRetentionDays = 180

// TopKCap is the fixed Top-K capacity (PRD: K≤100).
const TopKCap = 100

// logLine matches the combined-ish default format:
// addr - user [time] "request" status bytes ...
var logLine = regexp.MustCompile(`^(\S+)\s+\S+\s+\S+\s+\[([^\]]+)\]\s+"([^"]*)"\s+(\d{3})\s+(\d+|-)`)

// nginxTime is the $time_local layout.
const nginxTime = "02/Jan/2006:15:04:05 -0700"

// DailyRecord is one persisted per-site per-day aggregate. Sketch state is
// kept so later incremental parses of the same day merge instead of
// double-count; API consumers read the derived fields (UV, TopIPs, TopURLs).
type DailyRecord struct {
	Date     string            `json:"date"`
	Site     string            `json:"site"`
	PV       uint64            `json:"pv"`
	UV       uint64            `json:"uv"`
	HLL      []byte            `json:"hll"`
	Status   map[string]uint64 `json:"status"`
	Bytes    uint64            `json:"bytes"`
	Degraded bool              `json:"degraded"` // log_format lacked $body_bytes_sent

	CMSIPs   *CMS    `json:"cms_ips"`
	CMSURLs  *CMS    `json:"cms_urls"`
	TopIPs   []Entry `json:"top_ips"`
	TopURLs  []Entry `json:"top_urls"`
}

// ParseSummary reports what one parse pass consumed.
type ParseSummary struct {
	Lines   uint64   `json:"lines"`
	Parsed  uint64   `json:"parsed"`
	Skipped uint64   `json:"skipped"`
	Days    []string `json:"days"`
	Resumed bool     `json:"resumed"`
}

// checkpoint is the resume point for one log file of one site.
type checkpoint struct {
	Inode  uint64 `json:"inode"`
	Offset int64  `json:"offset"`
}

// dayAcc accumulates one parse pass for one day without per-line allocation.
type dayAcc struct {
	rec  *DailyRecord
	hll  *HLL
	ips  *TopK
	urls *TopK
}

// Manager owns the webstats data directory.
type Manager struct {
	Dir           string
	RetentionDays int
	Now           func() time.Time

	mu sync.Mutex
}

// NewManager wires a Manager over dir with PRD defaults.
func NewManager(dir string) *Manager {
	return &Manager{Dir: dir, RetentionDays: retentionFromEnv(), Now: time.Now}
}

func retentionFromEnv() int {
	v := strings.TrimSpace(os.Getenv("TAOOPS_WEBSTATS_RETENTION_DAYS"))
	if v == "" {
		return DefaultRetentionDays
	}
	n, err := strconv.Atoi(v)
	if err != nil || n < 1 {
		return DefaultRetentionDays
	}
	return n
}

// Parse streams one access log for site into daily aggregates, resuming from
// the stored inode+offset checkpoint when the file was seen before.
func (m *Manager) Parse(site, path string) (*ParseSummary, error) {
	if !SiteName.MatchString(site) {
		return nil, fmt.Errorf("webstats: invalid site name %q", site)
	}
	if !filepath.IsAbs(path) {
		return nil, errors.New("webstats: log path must be absolute")
	}
	st, err := os.Stat(path)
	if err != nil {
		return nil, fmt.Errorf("webstats: stat log: %w", err)
	}
	if !st.Mode().IsRegular() { // rejects dirs, devices, pipes, sockets
		return nil, errors.New("webstats: log path must be a regular file")
	}

	m.mu.Lock()
	defer m.mu.Unlock()

	siteDir := filepath.Join(m.Dir, site)
	cpPath := filepath.Join(siteDir, "checkpoints.json")
	cps, _ := m.loadCheckpoints(cpPath)
	prev, seen := cps[path]
	resume := seen && prev.Inode == inodeOf(st) && prev.Offset <= st.Size()
	if !resume {
		prev = checkpoint{}
	}

	f, err := os.Open(path)
	if err != nil {
		return nil, fmt.Errorf("webstats: open log: %w", err)
	}
	defer f.Close()
	if _, err := f.Seek(prev.Offset, io.SeekStart); err != nil {
		return nil, fmt.Errorf("webstats: seek: %w", err)
	}

	summary := &ParseSummary{Resumed: resume}
	accs := map[string]*dayAcc{}

	r := bufio.NewReaderSize(f, 64<<10)
	var bytesRead int64
	for {
		line, truncated, n, readErr := readLineCapped(r, MaxLineLen)
		bytesRead += n
		if len(line) > 0 {
			summary.Lines++
			if m.accumulate(site, accs, string(line)) {
				summary.Parsed++
			} else {
				summary.Skipped++
			}
		}
		if readErr != nil {
			break
		}
		_ = truncated // over-long lines were cut at MaxLineLen; keep streaming
	}

	for date, acc := range accs {
		acc.rec.UV = acc.hll.Count()
		acc.rec.HLL = acc.hll.regs
		acc.rec.TopIPs = acc.ips.Top()
		acc.rec.TopURLs = acc.urls.Top()
		acc.rec.CMSIPs = acc.ips.cmsSnapshot()
		acc.rec.CMSURLs = acc.urls.cmsSnapshot()
		if existing, err := m.loadRecord(filepath.Join(siteDir, date+".json")); err == nil {
			existing.merge(acc.rec)
			acc.rec = existing
		}
		if err := m.saveRecord(siteDir, acc.rec); err != nil {
			return nil, err
		}
		summary.Days = append(summary.Days, date)
	}
	newOffset := prev.Offset + bytesRead
	if newOffset > st.Size() {
		newOffset = st.Size()
	}
	cps[path] = checkpoint{Inode: inodeOf(st), Offset: newOffset}
	if err := m.saveCheckpoints(cpPath, cps); err != nil {
		return nil, err
	}
	m.cleanup(siteDir)
	return summary, nil
}

// readLineCapped reads one line up to cap bytes; truncated reports whether
// the line was longer than cap (the remainder until \n is skipped). It
// returns the number of bytes consumed from the reader.
func readLineCapped(r *bufio.Reader, cap int) (line []byte, truncated bool, consumed int64, err error) {
	var buf []byte
	var read int64
	for {
		chunk, e := r.ReadSlice('\n')
		read += int64(len(chunk))
		if len(buf)+len(chunk) > cap {
			buf = append(buf, chunk[:cap-len(buf)]...)
			truncated = true
		} else {
			buf = append(buf, chunk...)
		}
		if truncated {
			// drain the rest of this physical line
			for {
				d, e2 := r.ReadSlice('\n')
				read += int64(len(d))
				if e2 != nil && e2 != bufio.ErrBufferFull {
					return buf, true, read, e2
				}
				if len(d) > 0 && d[len(d)-1] == '\n' {
					return buf, true, read, nil
				}
				// ErrBufferFull → chunk had no '\n'; keep draining
			}
		}
		if e == bufio.ErrBufferFull {
			continue // physical line longer than the internal buffer
		}
		if e != nil {
			return buf, truncated, read, e
		}
		n := len(buf)
		if n > 0 && buf[n-1] == '\n' {
			return buf[:n-1], truncated, read, nil
		}
	}
}

// accumulate folds one log line into the per-day accumulator.
func (m *Manager) accumulate(site string, accs map[string]*dayAcc, line string) bool {
	mm := logLine.FindStringSubmatch(line)
	if mm == nil {
		return false
	}
	ts, err := time.Parse(nginxTime, mm[2])
	if err != nil {
		return false
	}
	status := mm[4]
	bytesField := mm[5]
	var nbytes uint64
	degraded := bytesField == "-"
	if !degraded {
		nbytes, err = strconv.ParseUint(bytesField, 10, 64)
		if err != nil {
			nbytes = 0
			degraded = true
		}
	}
	// request = "METHOD SP URL SP PROTO" (or bare URL); take the URL token.
	req := strings.Fields(mm[3])
	url := mm[3]
	if len(req) >= 2 {
		url = req[1]
	}
	ip := mm[1]

	acc, ok := accs[ts.Format("2006-01-02")]
	if !ok {
		acc = &dayAcc{
			rec: &DailyRecord{
				Date:     ts.Format("2006-01-02"),
				Site:     site,
				Status:   map[string]uint64{},
				CMSIPs:   &CMS{},
				CMSURLs:  &CMS{},
			},
			hll:  NewHLL(),
			ips:  NewTopK(TopKCap),
			urls: NewTopK(TopKCap),
		}
		accs[acc.rec.Date] = acc
	}
	acc.rec.PV++
	acc.rec.Bytes += nbytes
	acc.rec.Degraded = acc.rec.Degraded || degraded
	acc.rec.Status[status]++
	acc.hll.Add(ip)
	acc.ips.Add(ip, 1)
	acc.urls.Add(url, 1)
	return true
}

// merge folds rec (a fresh parse-pass aggregate) into r, combining sketch
// state so repeated incremental parses stay consistent.
func (r *DailyRecord) merge(rec *DailyRecord) {
	r.PV += rec.PV
	r.Bytes += rec.Bytes
	r.Degraded = r.Degraded || rec.Degraded
	if r.Status == nil {
		r.Status = map[string]uint64{}
	}
	for k, v := range rec.Status {
		r.Status[k] += v
	}
	if r.HLL == nil {
		r.HLL = rec.HLL
	} else {
		old := &HLL{p: HLLPrecision, regs: r.HLL}
		_ = old.Merge(&HLL{p: HLLPrecision, regs: rec.HLL})
		r.HLL = old.regs
	}
	r.UV = (&HLL{p: HLLPrecision, regs: r.HLL}).Count()

	if r.CMSIPs == nil {
		r.CMSIPs = &CMS{}
	}
	if r.CMSURLs == nil {
		r.CMSURLs = &CMS{}
	}
	ips := topKFrom(r.CMSIPs, r.TopIPs)
	ips.Merge(topKFrom(rec.CMSIPs, rec.TopIPs))
	r.CMSIPs = ips.cmsSnapshot()
	r.TopIPs = ips.Top()

	urls := topKFrom(r.CMSURLs, r.TopURLs)
	urls.Merge(topKFrom(rec.CMSURLs, rec.TopURLs))
	r.CMSURLs = urls.cmsSnapshot()
	r.TopURLs = urls.Top()
}

func topKFrom(cms *CMS, entries []Entry) *TopK {
	t := &TopK{k: TopKCap, cms: *cms, index: make(map[string]int, TopKCap+1)}
	for _, e := range entries {
		t.refresh(e.Key)
	}
	return t
}

// Summary returns daily aggregates for site (empty = all sites) within
// [from,to] inclusive (either may be empty).
func (m *Manager) Summary(site, from, to string) ([]DailyRecord, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	var out []DailyRecord
	sites, err := m.listSites(site)
	if err != nil {
		return nil, err
	}
	for _, s := range sites {
		files, err := os.ReadDir(filepath.Join(m.Dir, s))
		if err != nil {
			continue
		}
		for _, f := range files {
			if !strings.HasSuffix(f.Name(), ".json") || f.Name() == "checkpoints.json" {
				continue
			}
			date := strings.TrimSuffix(f.Name(), ".json")
			if from != "" && date < from {
				continue
			}
			if to != "" && date > to {
				continue
			}
			rec, err := m.loadRecord(filepath.Join(m.Dir, s, f.Name()))
			if err != nil {
				continue
			}
			out = append(out, *rec)
		}
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].Site != out[j].Site {
			return out[i].Site < out[j].Site
		}
		return out[i].Date < out[j].Date
	})
	return out, nil
}

// Clear removes aggregates for site (empty = everything). Checkpoints go too.
func (m *Manager) Clear(site string) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	if site == "" {
		return os.RemoveAll(m.Dir)
	}
	if !SiteName.MatchString(site) {
		return fmt.Errorf("webstats: invalid site name %q", site)
	}
	return os.RemoveAll(filepath.Join(m.Dir, site))
}

// Export renders the filtered aggregates as JSON (UV/Top-IP fields are
// visitor personal data — callers gate this behind admin UI per PRD).
func (m *Manager) Export(site, from, to string) ([]byte, error) {
	recs, err := m.Summary(site, from, to)
	if err != nil {
		return nil, err
	}
	return json.MarshalIndent(recs, "", "  ")
}

func (m *Manager) listSites(site string) ([]string, error) {
	if site != "" {
		if !SiteName.MatchString(site) {
			return nil, fmt.Errorf("webstats: invalid site name %q", site)
		}
		return []string{site}, nil
	}
	entries, err := os.ReadDir(m.Dir)
	if err != nil {
		if os.IsNotExist(err) {
			return nil, nil
		}
		return nil, err
	}
	var out []string
	for _, e := range entries {
		if e.IsDir() {
			out = append(out, e.Name())
		}
	}
	return out, nil
}

func (m *Manager) loadCheckpoints(path string) (map[string]checkpoint, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return map[string]checkpoint{}, nil
		}
		return nil, err
	}
	var cps map[string]checkpoint
	if err := json.Unmarshal(data, &cps); err != nil {
		return map[string]checkpoint{}, nil // corrupt file → start fresh
	}
	return cps, nil
}

func (m *Manager) saveCheckpoints(path string, cps map[string]checkpoint) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	data, err := json.MarshalIndent(cps, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(path, data, 0o600)
}

func (m *Manager) loadRecord(path string) (*DailyRecord, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var rec DailyRecord
	if err := json.Unmarshal(data, &rec); err != nil {
		return nil, err
	}
	return &rec, nil
}

func (m *Manager) saveRecord(siteDir string, rec *DailyRecord) error {
	if err := os.MkdirAll(siteDir, 0o755); err != nil {
		return err
	}
	data, err := json.MarshalIndent(rec, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(filepath.Join(siteDir, rec.Date+".json"), data, 0o600)
}

// cleanup drops daily records older than the retention window.
func (m *Manager) cleanup(siteDir string) {
	cutoff := m.Now().AddDate(0, 0, -m.RetentionDays).Format("2006-01-02")
	files, err := os.ReadDir(siteDir)
	if err != nil {
		return
	}
	for _, f := range files {
		name := strings.TrimSuffix(f.Name(), ".json")
		if f.Name() == "checkpoints.json" || len(name) != 10 {
			continue
		}
		if name < cutoff {
			_ = os.Remove(filepath.Join(siteDir, f.Name()))
		}
	}
}

func inodeOf(st os.FileInfo) uint64 {
	if raw, ok := st.Sys().(*syscall.Stat_t); ok {
		return raw.Ino
	}
	return 0
}
