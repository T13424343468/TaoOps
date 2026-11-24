package webstats

// Entry is one ranked item (source IP or URL) with its approximate count.
type Entry struct {
	Key   string `json:"key"`
	Count uint64 `json:"count"`
}

// cmsRows / cmsWidth size the count-min sketch backing Top-K counts.
// 4 rows x 4096 uint32 counters = 64KB per sketch — bounded memory per PRD.
const (
	cmsRows  = 4
	cmsWidth = 4096
)

// CMS is a count-min sketch: mergeable approximate counters with bounded size.
type CMS struct {
	cells [cmsRows][cmsWidth]uint32
}

func (c *CMS) add(key string, n uint64) {
	for r := 0; r < cmsRows; r++ {
		idx := (fnv1a64(key) + uint64(r)*0x9e3779b97f4a7c15) % cmsWidth
		sum := uint64(c.cells[r][idx]) + n
		if sum > uint64(^uint32(0)) {
			sum = uint64(^uint32(0))
		}
		c.cells[r][idx] = uint32(sum)
	}
}

func (c *CMS) count(key string) uint64 {
	min := uint64(^uint32(0))
	for r := 0; r < cmsRows; r++ {
		idx := (fnv1a64(key) + uint64(r)*0x9e3779b97f4a7c15) % cmsWidth
		if uint64(c.cells[r][idx]) < min {
			min = uint64(c.cells[r][idx])
		}
	}
	return min
}

func (c *CMS) merge(o *CMS) {
	for r := 0; r < cmsRows; r++ {
		for i := 0; i < cmsWidth; i++ {
			if o.cells[r][i] > c.cells[r][i] {
				c.cells[r][i] = o.cells[r][i]
			}
		}
	}
}

// TopK keeps the K heaviest keys using a fixed-capacity min-heap over
// count-min estimates — no full hash set of distinct keys, per PRD.
type TopK struct {
	k     int
	cms   CMS
	heap  []Entry
	index map[string]int // positions inside heap, capacity ≤ k
}

// NewTopK returns a Top-K with capacity clamped to [1,100] (PRD: K≤100).
func NewTopK(k int) *TopK {
	if k < 1 {
		k = 1
	}
	if k > 100 {
		k = 100
	}
	return &TopK{k: k, index: make(map[string]int, k+1)}
}

// Add counts n occurrences of key and refreshes the heap.
func (t *TopK) Add(key string, n uint64) {
	if n == 0 {
		return
	}
	t.cms.add(key, n)
	t.refresh(key)
}

func (t *TopK) refresh(key string) {
	if pos, ok := t.index[key]; ok {
		t.heap[pos].Count = t.cms.count(key)
		t.siftDown(pos)
		t.siftUp(pos)
		return
	}
	if len(t.heap) < t.k {
		t.heap = append(t.heap, Entry{Key: key, Count: t.cms.count(key)})
		t.index[key] = len(t.heap) - 1
		t.siftUp(len(t.heap) - 1)
		return
	}
	est := t.cms.count(key)
	minPos := 0
	for i := 1; i < len(t.heap); i++ {
		if t.heap[i].Count < t.heap[minPos].Count {
			minPos = i
		}
	}
	if est > t.heap[minPos].Count {
		delete(t.index, t.heap[minPos].Key)
		t.heap[minPos] = Entry{Key: key, Count: est}
		t.index[key] = minPos
		t.siftDown(minPos)
	}
}

// Top returns the entries sorted by count descending.
func (t *TopK) Top() []Entry {
	out := make([]Entry, len(t.heap))
	copy(out, t.heap)
	// insertion sort: k ≤ 100, fine.
	for i := 1; i < len(out); i++ {
		for j := i; j > 0 && out[j].Count > out[j-1].Count; j-- {
			out[j], out[j-1] = out[j-1], out[j]
		}
	}
	return out
}

// cmsSnapshot returns a copy of the underlying sketch for persistence.
func (t *TopK) cmsSnapshot() *CMS {
	c := t.cms
	return &c
}

// Merge unions another sketch/heap pair into t (element-wise max on CMS,
// union-and-trim on the heap) so partial-day parses combine losslessly enough.
func (t *TopK) Merge(o *TopK) {
	t.cms.merge(&o.cms)
	for _, e := range o.heap {
		if pos, ok := t.index[e.Key]; ok {
			if o.cms.count(e.Key) > t.heap[pos].Count {
				t.heap[pos].Count = o.cms.count(e.Key)
				t.siftDown(pos)
				t.siftUp(pos)
			}
			continue
		}
		t.refresh(e.Key)
	}
}

func (t *TopK) siftUp(i int) {
	for i > 0 {
		p := (i - 1) / 2
		if t.heap[p].Count <= t.heap[i].Count {
			break
		}
		t.heap[p], t.heap[i] = t.heap[i], t.heap[p]
		t.index[t.heap[p].Key] = p
		t.index[t.heap[i].Key] = i
		i = p
	}
}

func (t *TopK) siftDown(i int) {
	for {
		l, r := 2*i+1, 2*i+2
		s := i
		if l < len(t.heap) && t.heap[l].Count < t.heap[s].Count {
			s = l
		}
		if r < len(t.heap) && t.heap[r].Count < t.heap[s].Count {
			s = r
		}
		if s == i {
			return
		}
		t.heap[s], t.heap[i] = t.heap[i], t.heap[s]
		t.index[t.heap[s].Key] = s
		t.index[t.heap[i].Key] = i
		i = s
	}
}
