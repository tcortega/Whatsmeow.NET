package chatstate

import (
	"cmp"
	"context"
	"fmt"
	"slices"
	"sync"
	"time"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/appstate"
	"go.mau.fi/whatsmeow/types/events"
)

const (
	// cooldown is how long after a full sync of a collection that failed a failed sync of it starts no other, so a
	// collection that a full sync cannot repair does not loop: each failed full sync dispatches another error
	// (appstate.go:76-77, 167). After one that succeeded, the next failure is recovered at once.
	// An hour, as wuzapi's (appstate_recovery.go) [inferred: no measurement says how soon a retry can succeed].
	cooldown = time.Hour
	// syncTimeout bounds one full sync, the worker's or a write's.
	syncTimeout = 2 * time.Minute
)

// recovery is one session's app state recovery: each collection's full syncs, and the signals of failed syncs its
// worker has not taken yet. Its worker runs while the session is online; the state goes with it.
type recovery struct {
	mu          sync.Mutex
	collections map[appstate.WAPatchName]*collection
	// pending is each collection whose sync failed and how many of its full syncs had started by then.
	pending map[appstate.WAPatchName]uint64
	// wake has room for one signal; a signal while one is pending is dropped, its collection already noted in pending.
	wake chan struct{}
	// done is closed once the worker ended.
	done chan struct{}
}

// collection is the full syncs of one collection.
type collection struct {
	// started counts the full syncs started, so a write knows whether one started after it was sent.
	started uint64
	// last is when the last full sync started.
	last time.Time
	// running is the full sync running now; finished is the last that ended.
	running, finished *fullSync
}

// fullSync is one full sync of a collection: done is closed once it ended, with err its result.
type fullSync struct {
	done chan struct{}
	err  error
}

// Handler is the slice's whatsmeow event handler for a session's client. It only signals the session's recovery when
// a sync of a collection failed, and never blocks: whatsmeow dispatches AppStateSyncError holding its app state lock
// (appstate.go:46-47, 77, 167), which the full sync takes (appstate.go:46), so the handler never syncs itself. It
// always returns true: whatsmeow acks app state whatever the handlers return (appstate.go:36-38).
func (svc *Service) Handler(sessionID string) whatsmeow.EventHandlerWithSuccessStatus {
	return func(evt any) bool {
		if failed, ok := evt.(*events.AppStateSyncError); ok {
			svc.signal(sessionID, failed.Name)
		}

		return true
	}
}

// signal notes a failed sync of the collection for the session's worker, and wakes it without waiting.
func (svc *Service) signal(id string, name appstate.WAPatchName) {
	rec := svc.recovery(id)
	if rec == nil {
		return
	}

	rec.mu.Lock()
	// The latest failure counts: one after a full sync started asks for another.
	if seen := rec.started(name); seen >= rec.pending[name] {
		rec.pending[name] = seen
	}
	rec.mu.Unlock()

	select {
	case rec.wake <- struct{}{}:
	default:
	}
}

// recovery returns the session's recovery, starting its worker when it has none; nil once Close was called.
func (svc *Service) recovery(id string) *recovery {
	svc.mu.Lock()
	defer svc.mu.Unlock()

	if svc.ctx.Err() != nil {
		return nil
	}

	rec, ok := svc.recoveries[id]
	if ok {
		return rec
	}

	rec = &recovery{
		collections: map[appstate.WAPatchName]*collection{}, pending: map[appstate.WAPatchName]uint64{},
		wake: make(chan struct{}, 1), done: make(chan struct{}),
	}
	svc.recoveries[id] = rec

	svc.workers.Add(1)

	go svc.work(id, rec)

	return rec
}

// work is the session's worker: it fully syncs each collection whose sync failed, while the session is online. It
// ends, and the recovery with it, once the session leaves Connected and CaughtUp or is deleted (session.Online's
// channel), or at Close.
func (svc *Service) work(id string, rec *recovery) {
	defer svc.workers.Done()
	defer svc.forget(id, rec)

	c, online, err := svc.sessions.Online(id)
	if err != nil {
		return
	}

	client, ok := c.(Client)
	if !ok {
		svc.log.Error().Str("session_id", id).Msgf("its client %T syncs no app state; nothing recovers it", c)

		return
	}

	ctx, cancel := context.WithCancel(svc.ctx)
	defer cancel()

	for {
		select {
		case <-ctx.Done():
			return
		case <-online:
			return
		case <-rec.wake:
		}

		// One at a time, in order, so a full sync this worker started has ended before it decides on the next.
		for _, failed := range rec.take() {
			if run, start := rec.join(failed.name, failed.seen, svc.now(), false); start {
				svc.sync(id, rec, client, online, failed.name, run)
			}
		}
	}
}

// sync runs a full sync on the slice's own context, never a caller's: a write that started it and gave up does not
// end it for the writes that joined it. It is bounded by syncTimeout, and ends when the session leaves online.
func (svc *Service) sync(id string, rec *recovery, client Client, online <-chan struct{}, name appstate.WAPatchName, run *fullSync) {
	ctx, cancel := context.WithTimeout(svc.ctx, syncTimeout)
	defer cancel()

	stop := make(chan struct{})
	watched := make(chan struct{})

	go func() {
		defer close(watched)

		select {
		case <-online:
			cancel()
		case <-stop:
		}
	}()

	err := client.FetchAppState(ctx, name, true, false)

	close(stop)
	<-watched

	svc.finish(id, rec, name, run, err)
}

// forget removes the session's recovery once its worker ended, unless a newer one took its place.
func (svc *Service) forget(id string, rec *recovery) {
	svc.mu.Lock()
	if svc.recoveries[id] == rec {
		delete(svc.recoveries, id)
	}
	svc.mu.Unlock()

	close(rec.done)
}

// recoverAfter fully syncs a collection for a write that WhatsApp rejected, unless a full sync started since the write
// was sent (seen): then it waits for that one's result. The full sync runs on its own goroutine (sync), and the write
// only waits for it, until its own call ends. A write is not held back by the cooldown.
func (svc *Service) recoverAfter(ctx context.Context, id string, rec *recovery, client Client, online <-chan struct{}, name appstate.WAPatchName, seen uint64) error {
	run, start := rec.join(name, seen, svc.now(), true)
	//nolint:contextcheck // the full sync runs on the slice's context, not the caller's, so a caller that gives up does not end it for the others
	if start && !svc.spawn(func() { svc.sync(id, rec, client, online, name, run) }) {
		svc.finish(id, rec, name, run, fmt.Errorf("the slice is closed: %w", context.Canceled))
	}

	select {
	case <-run.done:
		return run.err
	case <-ctx.Done():
		return fmt.Errorf("wait for the full sync: %w", context.Cause(ctx))
	}
}

// spawn runs f on a goroutine Close waits for, unless Close was called.
func (svc *Service) spawn(f func()) bool {
	svc.mu.Lock()
	defer svc.mu.Unlock()

	if svc.ctx.Err() != nil {
		return false
	}

	svc.workers.Go(f)

	return true
}

// finish records the end of a full sync.
func (svc *Service) finish(id string, rec *recovery, name appstate.WAPatchName, run *fullSync, err error) {
	if err != nil {
		svc.log.Warn().Str("session_id", id).Str("collection", string(name)).Err(bare(err)).Msg("a full sync of an app state collection failed")
	} else {
		svc.log.Info().Str("session_id", id).Str("collection", string(name)).Msg("an app state collection was fully synced again")
	}

	rec.mu.Lock()
	defer rec.mu.Unlock()

	run.err = err
	col := rec.collections[name]
	col.running, col.finished = nil, run
	close(run.done)
}

// seen is how many full syncs of the collection started so far.
func (rec *recovery) seen(name appstate.WAPatchName) uint64 {
	if rec == nil {
		return 0
	}

	rec.mu.Lock()
	defer rec.mu.Unlock()

	return rec.started(name)
}

// started is how many full syncs of the collection started so far. rec.mu is held.
func (rec *recovery) started(name appstate.WAPatchName) uint64 {
	if col, ok := rec.collections[name]; ok {
		return col.started
	}

	return 0
}

// failedSync is a collection whose sync failed, and how many of its full syncs had started by then.
type failedSync struct {
	name appstate.WAPatchName
	seen uint64
}

// take returns the collections whose sync failed since the last take, ordered by name.
func (rec *recovery) take() []failedSync {
	rec.mu.Lock()
	defer rec.mu.Unlock()

	failed := make([]failedSync, 0, len(rec.pending))
	for name, seen := range rec.pending {
		failed = append(failed, failedSync{name, seen})
	}

	clear(rec.pending)
	slices.SortFunc(failed, func(a, b failedSync) int { return cmp.Compare(a.name, b.name) })

	return failed
}

// join returns the full sync of the collection to wait for, and whether the caller starts it. A running one is joined,
// and one that started after the write was sent or the sync failed (seen) is the answer. Otherwise one starts, for a
// write whatever the cooldown, for a failed sync unless the last full sync failed and started within the cooldown.
func (rec *recovery) join(name appstate.WAPatchName, seen uint64, now time.Time, write bool) (*fullSync, bool) {
	rec.mu.Lock()
	defer rec.mu.Unlock()

	col, ok := rec.collections[name]
	if !ok {
		col = &collection{}
		rec.collections[name] = col
	}

	switch {
	case col.running != nil:
		return col.running, false
	case col.started > seen:
		return col.finished, false
	case !write && col.finished != nil && col.finished.err != nil && now.Sub(col.last) < cooldown:
		return nil, false
	}

	col.started++
	col.last = now
	col.running = &fullSync{done: make(chan struct{})}

	return col.running, true
}

// Close ends the recoveries and waits for their workers.
func (svc *Service) Close() {
	svc.mu.Lock()
	svc.cancel()
	svc.mu.Unlock()

	svc.workers.Wait()
}
