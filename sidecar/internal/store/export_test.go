package store

import (
	"runtime"
	"testing"
	"time"
)

// SetBeforeCommit runs f before each batch's COMMIT: the tests' disk, which counts commits or holds one. Set it before
// the first write.
func SetBeforeCommit(w *Writer, f func()) { w.beforeCommit = f }

// StopTheClock freezes the gate's clock, so a batch's time budget never runs out and a batch's size is its count limit
// alone. Set it before the first write.
func StopTheClock(w *Writer) {
	at := time.Now()
	w.now = func() time.Time { return at }
}

// WaitQueued waits until n writes wait for the gate, failing t once deadline passes.
func WaitQueued(t *testing.T, w *Writer, n int, deadline time.Time) {
	t.Helper()

	for {
		w.mu.Lock()
		got := len(w.waiting)
		w.mu.Unlock()

		if got == n {
			return
		}

		if time.Now().After(deadline) {
			t.Fatalf("%d writes wait for the gate, want %d", got, n)
		}

		runtime.Gosched()
	}
}
