package webstats

import (
	"errors"
	"math"
)

// HLL is a HyperLogLog sketch for cardinality estimation (distinct source IPs).
// Precision p=12 → 4096 registers, standard error ≈ 1.04/sqrt(4096) ≈ 1.6%,
// matching the PRD "~2%" bound. Mergeable: registers combine element-wise max.
type HLL struct {
	p    uint8
	regs []byte
}

// HLLPrecision is the number of index bits; 12 → 4096 registers.
const HLLPrecision = 12

// NewHLL returns an empty sketch.
func NewHLL() *HLL {
	return &HLL{p: HLLPrecision, regs: make([]byte, 1<<HLLPrecision)}
}

// fnv1a64 hashes s with FNV-1a 64-bit plus a splitmix64 finalizer.
// The finalizer is required: raw FNV-1a has weak avalanche on inputs that
// differ only in trailing bytes (e.g. sequential IPs), which collapses the
// high bits the HLL index uses.
func fnv1a64(s string) uint64 {
	h := uint64(14695981039346656037)
	for i := 0; i < len(s); i++ {
		h ^= uint64(s[i])
		h *= 1099511628211
	}
	h ^= h >> 30
	h *= 0xbf58476d1ce4e5b9
	h ^= h >> 27
	h *= 0x94d049bb133111eb
	h ^= h >> 31
	return h
}

// Add inserts one element into the sketch.
func (h *HLL) Add(s string) {
	x := fnv1a64(s)
	idx := x >> (64 - h.p)
	rest := x << (64 - h.p) // low p bits (after the index) at the top of the word
	rho := uint8(1)
	for rest&(1<<63) == 0 && rho < uint8(h.p)+1 {
		rest <<= 1
		rho++
	}
	if h.regs[idx] < rho {
		h.regs[idx] = rho
	}
}

// Count returns the cardinality estimate with small/large range corrections.
func (h *HLL) Count() uint64 {
	m := uint64(len(h.regs))
	sum := 0.0
	zeros := 0
	for _, r := range h.regs {
		sum += 1.0 / float64(uint64(1)<<r)
		if r == 0 {
			zeros++
		}
	}
	alpha := alphaM(m)
	est := alpha * float64(m) * float64(m) / sum
	if est <= 2.5*float64(m) {
		// Linear counting for the small range.
		if zeros > 0 {
			est = float64(m) * math.Log(float64(m)/float64(zeros))
		} else {
			est = 0
		}
	}
	if est > 0 {
		return uint64(est + 0.5)
	}
	return 0
}

// alphaM is the HLL bias constant for m registers.
func alphaM(m uint64) float64 {
	switch {
	case m == 16:
		return 0.673
	case m == 32:
		return 0.697
	case m == 64:
		return 0.709
	default:
		return 0.7213 / (1.0 + 1.079/float64(m))
	}
}

// Merge unions another sketch into h. Sketches must share precision.
func (h *HLL) Merge(o *HLL) error {
	if h.p != o.p || len(h.regs) != len(o.regs) {
		return errPrecisionMismatch
	}
	for i := range h.regs {
		if o.regs[i] > h.regs[i] {
			h.regs[i] = o.regs[i]
		}
	}
	return nil
}

var errPrecisionMismatch = errors.New("webstats: HLL precision mismatch")
