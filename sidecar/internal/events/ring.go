package events

import (
	"sync"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// ringSize is how many ephemeral events each session keeps (events.proto StreamEphemeralEvents). It is fixed until a
// measurement shows it needs tuning.
const ringSize = 256

// ring is one session's ephemeral events, in memory only: the newest ringSize of them. The events are numbered from
// 0 in the order they were pushed; a stream reads them by number, so it sees which ones were dropped before it got
// them.
type ring struct {
	// deleted is the session's, from when the ring was made: a ring never outlives its session.
	deleted <-chan struct{}

	mu     sync.Mutex
	events [ringSize]*whatsmeowv1.StreamEphemeralEventsResponse
	// next is the number of the next event pushed.
	next uint64
	// sent is the number after the newest event sent to any stream, or counted as dropped for one.
	sent uint64
	// changed is closed and replaced by every push.
	changed chan struct{}
}

func newRing(deleted <-chan struct{}) *ring {
	return &ring{deleted: deleted, changed: make(chan struct{})}
}

// push adds an event, dropping the oldest when the ring is full. It never blocks: the whatsmeow event handler calls it
// (ARCHITECTURE §13 invariant 1).
func (r *ring) push(event *whatsmeowv1.StreamEphemeralEventsResponse) {
	r.mu.Lock()
	defer r.mu.Unlock()

	r.events[r.next%ringSize] = event
	r.next++
	close(r.changed)
	r.changed = make(chan struct{})
}

// start is where a stream that opens now begins: after the newest event any stream got. A stream opened while none
// is open so gets the ones no stream got, and one opened while another is open starts where the furthest open one is.
func (r *ring) start() uint64 {
	r.mu.Lock()
	defer r.mu.Unlock()

	return r.sent
}

// read returns how many events from number from on were dropped, the kept ones after them in order, the number after
// the last, and the channel the next push closes.
func (r *ring) read(from uint64) (uint64, []*whatsmeowv1.StreamEphemeralEventsResponse, uint64, <-chan struct{}) {
	r.mu.Lock()
	defer r.mu.Unlock()

	oldest := r.next - min(r.next, ringSize)
	dropped := uint64(0)

	if from < oldest {
		dropped, from = oldest-from, oldest
	}

	events := make([]*whatsmeowv1.StreamEphemeralEventsResponse, 0, r.next-from)
	for n := from; n < r.next; n++ {
		events = append(events, r.events[n%ringSize])
	}

	return dropped, events, r.next, r.changed
}

// got records that a stream was handed every event before number upTo, or told they were dropped: before the send,
// so a stream that opens once the client read one never starts before it.
func (r *ring) got(upTo uint64) {
	r.mu.Lock()
	defer r.mu.Unlock()

	r.sent = max(r.sent, upTo)
}

func (r *ring) gone() bool {
	select {
	case <-r.deleted:
		return true
	default:
		return false
	}
}
