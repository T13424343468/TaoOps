// 会话 ID 与时钟辅助。
package agent

import (
	"crypto/rand"
	"encoding/hex"
	"sync"
	"time"
)

var (
	sessionMu   sync.Mutex
	sessionSeq  uint64
	sessionSalt = newSalt()
)

func newSalt() []byte {
	b := make([]byte, 4)
	_, _ = rand.Read(b)
	return b
}

func newSessionID() string {
	sessionMu.Lock()
	defer sessionMu.Unlock()
	sessionSeq++
	return hex.EncodeToString(sessionSalt) + "-" + hex.EncodeToString([]byte{byte(sessionSeq >> 8), byte(sessionSeq)})
}

func nowMs() int64 { return time.Now().UnixMilli() }
