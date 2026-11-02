package session

import (
	"errors"
	"testing"
	"time"

	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

func TestOnlineGivesTheClientOnlyWhileConnected(t *testing.T) {
	t.Parallel()

	expire := timestamppb.New(time.Date(2099, 1, 2, 3, 4, 5, 0, time.UTC))

	for _, tc := range []struct {
		name   string
		state  *whatsmeowv1.SessionState
		paired bool
		want   error
	}{
		{"connected", connected(), true, nil},
		{"caught up", caughtUp(), true, nil},
		{"created", created(), false, server.ErrNotLoggedIn},
		{"pairing a new session", pairing(), false, server.ErrNotLoggedIn},
		{"logged out", loggedOut(401), false, server.ErrLoggedOut},
		{"temporarily banned", tempBanned(101, expire), true, server.ErrTemporarilyBanned},
		{"connecting", connecting(), true, server.ErrNotConnected},
		{"reconnecting", reconnecting(), true, server.ErrNotConnected},
		{"disconnected", disconnected(), true, server.ErrNotConnected},
		{"replaced", replaced(), true, server.ErrNotConnected},
		{"outdated", outdated(), true, server.ErrNotConnected},
		{"error", failed(500, "x"), true, server.ErrNotConnected},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := start(t)
			h.create("s1")

			if tc.paired {
				h.link("s1", "5511999999999:1@s.whatsapp.net")
			}

			h.setState("s1", tc.state)

			client, gone, err := h.svc.Online("s1")
			if !errors.Is(err, tc.want) {
				t.Fatalf("Online = %v, want %v", err, tc.want)
			}

			if tc.want != nil {
				if client != nil || gone != nil {
					t.Fatalf("Online failed but returned %v, %v", client, gone)
				}

				return
			}

			if client != h.stub("s1") {
				t.Fatalf("Online returned %v, want the session's client", client)
			}
		})
	}
}

func TestOnlinesChannelClosesWhenTheSessionLeavesConnected(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.connected("s1")

	_, gone, err := h.svc.Online("s1")
	if err != nil {
		t.Fatalf("Online: %v", err)
	}

	// CaughtUp is still online.
	h.stub("s1").emit(&events.OfflineSyncCompleted{})

	select {
	case <-gone:
		t.Fatal("the channel closed while the session was still online")
	default:
	}

	if _, err := h.client.Disconnect(h.ctx, &whatsmeowv1.DisconnectRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("Disconnect: %v", err)
	}

	select {
	case <-gone:
	case <-h.ctx.Done():
		t.Fatal("the channel never closed after Disconnect")
	}

	// Online again is a new channel, open.
	h.connect("s1")
	h.stub("s1").emit(&events.Connected{})

	_, again, err := h.svc.Online("s1")
	if err != nil {
		t.Fatalf("Online after reconnecting: %v", err)
	}

	select {
	case <-again:
		t.Fatal("the new channel is closed")
	default:
	}
}

func TestOnlinesChannelClosesWhenTheSessionIsDeleted(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.connected("s1")

	_, gone, err := h.svc.Online("s1")
	if err != nil {
		t.Fatalf("Online: %v", err)
	}

	// The stub's Disconnect dispatches nothing, so only the delete itself can close the channel.
	if _, err := h.client.DeleteSession(h.ctx, &whatsmeowv1.DeleteSessionRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("DeleteSession: %v", err)
	}

	select {
	case <-gone:
	case <-h.ctx.Done():
		t.Fatal("the channel never closed after DeleteSession")
	}
}

func TestOnlineOfASessionDeletedAfterItWasFoundIsSessionNotFound(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.connected("s1")

	// Online found the session, then DeleteSession ran before it took s.mu: the session still says Connected, because
	// the stub's Disconnect dispatches nothing.
	s := h.svc.sessions["s1"]
	if _, err := h.client.DeleteSession(h.ctx, &whatsmeowv1.DeleteSessionRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("DeleteSession: %v", err)
	}

	client, gone, err := s.clientOnline()
	if !errors.Is(err, server.ErrSessionNotFound) || client != nil || gone != nil {
		t.Fatalf("clientOnline = %v, %v, %v; want SESSION_NOT_FOUND and no channel, which would never close", client, gone, err)
	}

	s.mu.Lock()
	made := s.online != nil
	s.mu.Unlock()

	if made {
		t.Fatal("clientOnline made a channel for a deleted session")
	}
}

func TestOnlineOfAnUnknownSessionIsSessionNotFound(t *testing.T) {
	t.Parallel()

	h := start(t)

	if _, _, err := h.svc.Online("nobody"); !errors.Is(err, server.ErrSessionNotFound) {
		t.Fatalf("Online = %v, want SESSION_NOT_FOUND", err)
	}

	if _, err := h.svc.Device("nobody"); !errors.Is(err, server.ErrSessionNotFound) {
		t.Fatalf("Device = %v, want SESSION_NOT_FOUND", err)
	}
}

func TestDeviceGivesAPairedSessionsDeviceInAnyState(t *testing.T) {
	t.Parallel()

	for _, tc := range []struct {
		name   string
		state  *whatsmeowv1.SessionState
		paired bool
		want   error
	}{
		{"disconnected", disconnected(), true, nil},
		{"connected", connected(), true, nil},
		{"temporarily banned", tempBanned(101, nil), true, nil},
		{"replaced", replaced(), true, nil},
		{"created", created(), false, server.ErrNotLoggedIn},
		{"logged out", loggedOut(0), false, server.ErrLoggedOut},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := start(t)
			h.create("s1")

			if tc.paired {
				h.link("s1", "5511999999999:1@s.whatsapp.net")
			}

			h.setState("s1", tc.state)

			device, err := h.svc.Device("s1")
			if !errors.Is(err, tc.want) {
				t.Fatalf("Device = %v, want %v", err, tc.want)
			}

			if tc.want == nil && device != h.stub("s1").device {
				t.Fatalf("Device returned %v, want the session's device", device)
			}
		})
	}
}
