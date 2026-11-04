package session

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"testing"

	"google.golang.org/grpc/codes"
	"google.golang.org/protobuf/proto"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// TestConnectSessionsRecordsEveryConnectingStateInOneCommit is the client's reconnect after a restart: one sync of the
// database however many sessions it connects again (#77). Not parallel: it counts commits (countCommits).
func TestConnectSessionsRecordsEveryConnectingStateInOneCommit(t *testing.T) {
	const sessions = 20

	h := start(t)
	ids := make([]string, sessions)

	for i := range ids {
		ids[i] = fmt.Sprintf("s%02d", i)
		h.connected(ids[i])
	}

	h.stop()

	commits := countCommits(t, h.path)
	restarted := startOn(t, h.path)
	before := commits()

	resp, err := restarted.client.ConnectSessions(restarted.ctx, &whatsmeowv1.ConnectSessionsRequest{SessionIds: ids})
	if err != nil {
		t.Fatalf("ConnectSessions: %v", err)
	}

	if got := commits() - before; got != 1 {
		t.Fatalf("connecting %d sessions made %d commits, want 1", sessions, got)
	}

	for i, result := range resp.GetResults() {
		if result.GetSessionId() != ids[i] || result.GetReason() != whatsmeowv1.ErrorReason_ERROR_REASON_UNSPECIFIED {
			t.Fatalf("result %d = %v, want %s connected", i, result, ids[i])
		}

		restarted.wantRecorded(ids[i], disconnected(), connecting(), connected(), disconnected(), connecting())

		if got := restarted.stub(ids[i]).connects(); got != 1 {
			t.Fatalf("session %s connected %d times, want 1", ids[i], got)
		}
	}
}

func TestConnectSessionsAnswersForEachSessionInOrder(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.connected("again")
	h.disconnect("again")
	h.connected("refused")
	h.disconnect("refused")
	h.stub("refused").connectErr = errors.New("websocket: bad handshake")
	h.connected("live")
	h.create("created")

	ids := []string{"refused", "missing", "again", "created", "live", "bad id!"}

	resp, err := h.client.ConnectSessions(h.ctx, &whatsmeowv1.ConnectSessionsRequest{SessionIds: ids})
	if err != nil {
		t.Fatalf("ConnectSessions: %v", err)
	}

	want := []whatsmeowv1.ErrorReason{
		whatsmeowv1.ErrorReason_ERROR_REASON_CONNECTION_FAILED,
		whatsmeowv1.ErrorReason_ERROR_REASON_SESSION_NOT_FOUND,
		whatsmeowv1.ErrorReason_ERROR_REASON_UNSPECIFIED,
		whatsmeowv1.ErrorReason_ERROR_REASON_NOT_LOGGED_IN,
		whatsmeowv1.ErrorReason_ERROR_REASON_UNSPECIFIED,
		whatsmeowv1.ErrorReason_ERROR_REASON_INVALID_ARGUMENT,
	}

	if len(resp.GetResults()) != len(ids) {
		t.Fatalf("results = %v, want one for each of %v", resp.GetResults(), ids)
	}

	for i, result := range resp.GetResults() {
		if result.GetSessionId() != ids[i] || result.GetReason() != want[i] {
			t.Fatalf("result %d = %v, want %s with %v", i, result, ids[i], want[i])
		}
	}

	// As Connect leaves each: a refused one back where it was, a live one untouched.
	for id, state := range map[string]*whatsmeowv1.SessionState{
		"refused": disconnected(), "again": connecting(), "live": connected(), "created": created(),
	} {
		if got := h.state(id); !proto.Equal(got, state) {
			t.Fatalf("session %s is %v, want %v", id, got, state)
		}
	}

	if got := h.stub("live").connects(); got != 1 {
		t.Fatalf("the live session connected %d times, want 1", got)
	}
}

func TestConnectSessionsRefusesASessionGivenTwice(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.connected("s1")
	h.disconnect("s1")

	_, err := h.client.ConnectSessions(h.ctx, &whatsmeowv1.ConnectSessionsRequest{SessionIds: []string{"s1", "s1"}})
	wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": "session_ids"})

	if got := h.state("s1"); !proto.Equal(got, disconnected()) {
		t.Fatalf("session s1 is %v, want Disconnected", got)
	}
}

func TestConnectSessionsOpensTheConnectionsConcurrently(t *testing.T) {
	t.Parallel()

	const sessions = 4

	h := start(t)
	ids := make([]string, sessions)

	// Each connect call waits for all of them to have begun, at most until the test's bound: in order, the first would
	// wait alone.
	var (
		mu    sync.Mutex
		begun int
	)

	all := make(chan struct{})

	for i := range ids {
		ids[i] = fmt.Sprintf("s%d", i)
		h.connected(ids[i])
		h.disconnect(ids[i])

		s := h.stub(ids[i])
		s.mu.Lock()
		s.connecting = func(context.Context) {
			mu.Lock()
			if begun++; begun == sessions {
				close(all)
			}
			mu.Unlock()

			select {
			case <-all:
			case <-h.ctx.Done():
			}
		}
		s.mu.Unlock()
	}

	resp, err := h.client.ConnectSessions(h.ctx, &whatsmeowv1.ConnectSessionsRequest{SessionIds: ids})
	if err != nil {
		t.Fatalf("ConnectSessions: %v", err)
	}

	for i, result := range resp.GetResults() {
		if result.GetReason() != whatsmeowv1.ErrorReason_ERROR_REASON_UNSPECIFIED {
			t.Fatalf("result %d = %v, want connected", i, result)
		}
	}

	select {
	case <-all:
	default:
		t.Fatalf("the %d connect calls ran one after another, not at once", sessions)
	}
}

func TestConnectSessionsLetsGoOfASessionOnceItsOwnConnectEnded(t *testing.T) {
	t.Parallel()

	h := start(t)

	for _, id := range []string{"fast", "slow"} {
		h.connected(id)
		h.disconnect(id)
	}

	begun, release := make(chan struct{}), make(chan struct{})
	// Also when the test fails before it releases slow: the server's stop waits for the call.
	releaseOnce := sync.OnceFunc(func() { close(release) })
	t.Cleanup(releaseOnce)

	slow := h.stub("slow")
	slow.mu.Lock()
	slow.connecting = func(context.Context) {
		close(begun)
		<-release
	}
	slow.mu.Unlock()

	answered := make(chan error, 1)

	go func() {
		_, err := h.client.ConnectSessions(h.ctx, &whatsmeowv1.ConnectSessionsRequest{SessionIds: []string{"fast", "slow"}})
		answered <- err
	}()

	h.await(begun, "the slow session's connect call")

	// Disconnect takes the session's ops, which ConnectSessions holds until fast's own connect call ended, not slow's.
	disconnected, disconnectErr := make(chan struct{}), make(chan error, 1)

	go func() {
		defer close(disconnected)

		_, err := h.client.Disconnect(h.ctx, &whatsmeowv1.DisconnectRequest{SessionId: "fast"})
		disconnectErr <- err
	}()

	h.await(disconnected, "Disconnect of fast while slow still connects")
	releaseOnce()

	if err := <-disconnectErr; err != nil {
		t.Fatalf("Disconnect: %v", err)
	}

	if err := <-answered; err != nil {
		t.Fatalf("ConnectSessions: %v", err)
	}
}

func (h *harness) disconnect(id string) {
	h.t.Helper()

	if _, err := h.client.Disconnect(h.ctx, &whatsmeowv1.DisconnectRequest{SessionId: id}); err != nil {
		h.t.Fatalf("Disconnect(%q): %v", id, err)
	}
}
