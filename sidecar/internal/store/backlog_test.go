package store_test

import (
	"context"
	"database/sql"
	"fmt"
	"path/filepath"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/rs/zerolog"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/outbox"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

// After a restart every connected session reopens its event stream (a cursor write), records its new state and acks
// it: three writes a session, all queued ahead of the first send. On a slow disk each commit costs, so the send's wait
// is the number of commits before its own (#176: 0 acks in 210 s after one restart on win-x64 CI). Grouped into
// batches, a send behind 900 writes waits for ceil(901/64) commits, not 901. Counted in commits, not time: the commit
// is the disk's cost, whatever it is.
func TestASendAfterA300SessionRestartWaitsForFewCommits(t *testing.T) {
	t.Parallel()

	const sessions, consumer = 300, "dotnet"

	s, err := store.Open(context.Background(), filepath.Join(t.TempDir(), "whatsmeow-net.db"), zerolog.Nop())
	if err != nil {
		t.Fatalf("open the store: %v", err)
	}

	t.Cleanup(func() { _ = s.Close() })

	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	t.Cleanup(cancel)

	box := outbox.New(s.DB, s.Writer)
	ids := make([]string, sessions)
	seqs := make([]uint64, sessions)

	for i := range ids {
		ids[i] = fmt.Sprintf("restarted-%03d", i)
		if _, err := s.DB.ExecContext(ctx, "INSERT INTO session (id) VALUES (?)", ids[i]); err != nil {
			t.Fatalf("create session %s: %v", ids[i], err)
		}

		if _, err := box.Record(ctx, ids[i], "", connected()); err != nil {
			t.Fatalf("record session %s's state: %v", ids[i], err)
		}

		if seqs[i], err = box.Highest(ctx, ids[i]); err != nil {
			t.Fatalf("session %s's seq: %v", ids[i], err)
		}
	}

	var commits atomic.Int64

	store.SetBeforeCommit(s.Writer, func() { commits.Add(1) })
	store.StopTheClock(s.Writer)

	// The restart's first write holds the gate while the rest queue behind it, until the test lets it go or ended.
	hold, holding := make(chan struct{}), make(chan struct{})
	release := sync.OnceFunc(func() { close(hold) })
	t.Cleanup(release)

	first := make(chan error, 1)

	go func() {
		first <- s.Writer.Tx(ctx, func(context.Context, *sql.Tx) error {
			close(holding)
			<-hold

			return nil
		})
	}()

	<-holding

	var (
		wg       sync.WaitGroup
		failures = make(chan error, 3*sessions)
	)

	for i, id := range ids {
		wg.Go(func() {
			if _, err := box.Cursor(ctx, id, consumer); err != nil {
				failures <- err
			}
		})
		wg.Go(func() {
			if _, err := box.Record(ctx, id, "", connected()); err != nil {
				failures <- err
			}
		})
		wg.Go(func() {
			if err := box.Ack(ctx, id, consumer, seqs[i]); err != nil {
				failures <- err
			}
		})
	}

	deadline := time.Now().Add(30 * time.Second)
	store.WaitQueued(t, s.Writer, 3*sessions, deadline)

	// The send's record, as messaging writes it before the hand-off.
	sent := make(chan int64, 1)

	go func() {
		if _, err := s.Writer.Exec(ctx, "INSERT INTO sent_message (session_id, client_message_id) VALUES (?, ?)", ids[0], "3EB0SENDAFTERRESTART"); err != nil {
			failures <- err
		}

		sent <- commits.Load()
	}()

	store.WaitQueued(t, s.Writer, 3*sessions+1, deadline)
	release()

	waited := <-sent

	wg.Wait()
	close(failures)

	for err := range failures {
		t.Errorf("a queued write: %v", err)
	}

	if err := <-first; err != nil {
		t.Fatalf("the first write: %v", err)
	}

	// The first write's batch, then the 901 queued writes at most 64 a commit.
	const bound = 1 + (3*sessions+1+63)/64
	if waited > bound {
		t.Fatalf("the send was durable after %d commits, want at most %d: it waited for every write queued before it", waited, bound)
	}

	var n int
	if err := s.DB.QueryRowContext(ctx, "SELECT count(*) FROM sent_message").Scan(&n); err != nil || n != 1 {
		t.Fatalf("the send's record: %d, %v; want 1", n, err)
	}
}

func connected() *whatsmeowv1.StreamEventsResponse {
	return &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_ConnectionState{
		ConnectionState: &whatsmeowv1.ConnectionStateEvent{State: &whatsmeowv1.SessionState{
			Kind: &whatsmeowv1.SessionState_Connected_{Connected: &whatsmeowv1.SessionState_Connected{}},
		}},
	}}
}
