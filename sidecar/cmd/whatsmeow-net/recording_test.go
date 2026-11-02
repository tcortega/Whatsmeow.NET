package main

import (
	"context"
	"path/filepath"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waE2E"
	wastore "go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/types"
	waevents "go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"

	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/chatstate"
	eventsslice "github.com/tcortega/Whatsmeow.NET/sidecar/internal/events"
	historyslice "github.com/tcortega/Whatsmeow.NET/sidecar/internal/history"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/outbox"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/session"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

// handlers is a client that only keeps the handlers registered on it, run as whatsmeow's dispatchEvent runs them:
// in order, up to the first that fails (client.go:999-1013).
type handlers struct {
	session.Client

	registered []whatsmeow.EventHandlerWithSuccessStatus
}

func (h *handlers) AddEventHandlerWithSuccessStatus(handler whatsmeow.EventHandlerWithSuccessStatus) uint32 {
	h.registered = append(h.registered, handler)

	return uint32(len(h.registered)) //nolint:gosec // G115: three handlers
}

func (h *handlers) dispatch(evt any) (answered bool) {
	for _, handler := range h.registered {
		if !handler(evt) {
			return false
		}
	}

	return true
}

// recorded is session s1's client as recording makes it on a real store, and how many times it called withheld.
func recorded(t *testing.T) (*store.Store, *handlers, *int) {
	t.Helper()

	log := zerolog.Nop()

	db, err := store.Open(context.Background(), filepath.Join(t.TempDir(), "whatsmeow-net.db"), log)
	if err != nil {
		t.Fatalf("open the store: %v", err)
	}

	box := outbox.New(db.DB, db.Writer)
	chats := chatstate.New(log)
	histories := historyslice.New(db.DB, db.Writer, box, eventsslice.MessageEvent, log)

	t.Cleanup(func() {
		histories.Close()
		chats.Close()
		_ = db.Close()
	})

	if _, err := db.DB.ExecContext(context.Background(), "INSERT INTO session (id) VALUES ('s1')"); err != nil {
		t.Fatalf("create the session: %v", err)
	}

	withheld := 0
	client := &handlers{}
	newClient := recording(func(string, *wastore.Device, func()) session.Client { return client }, nil, eventsslice.New(box, log), chats, histories)
	_ = newClient("s1", db.Devices.NewDevice(), func() { withheld++ })

	return db, client, &withheld
}

// text is a text message from someone else, which the Events slice records.
func text(id string) any {
	chat := types.NewJID("5511000000001", types.DefaultUserServer)

	return &waevents.Message{
		Info:    types.MessageInfo{MessageSource: types.MessageSource{Chat: chat, Sender: chat}, ID: id, Timestamp: time.Unix(1_700_000_000, 0)},
		Message: &waE2E.Message{Conversation: proto.String("hello")},
	}
}

// fromPhone is a history sync notification from the account's own phone, which the History slice queues.
func fromPhone(id string) any {
	chat := types.NewJID("5511000000000", types.DefaultUserServer)

	return &waevents.Message{
		Info: types.MessageInfo{MessageSource: types.MessageSource{Chat: chat, Sender: chat, IsFromMe: true}, ID: id, Timestamp: time.Unix(1_700_000_000, 0)},
		Message: &waE2E.Message{ProtocolMessage: &waE2E.ProtocolMessage{
			Type:                    waE2E.ProtocolMessage_HISTORY_SYNC_NOTIFICATION.Enum(),
			HistorySyncNotification: &waE2E.HistorySyncNotification{DirectPath: proto.String("/blob/1"), FileEncSHA256: []byte{1}},
		}},
	}
}

// Not parallel: pii_test.go sets libsignal's logger, which a device created at once would race with
// (ARCHITECTURE §7.2).
func TestEachHandlerThatWithholdsAnAnswerWakesTheRedelivery(t *testing.T) {
	for _, c := range []struct {
		name, table string
		event       func(id string) any
	}{
		{"the Events slice's", "outbox", text},
		{"the History slice's", "history_blob", fromPhone},
	} {
		t.Run(c.name, func(t *testing.T) {
			db, client, withheld := recorded(t)

			if !client.dispatch(c.event("N1")) || *withheld != 0 {
				t.Fatalf("answered a recorded event with withheld called %d times, want answered and 0", *withheld)
			}

			if _, err := db.DB.ExecContext(context.Background(), "DROP TABLE "+c.table); err != nil {
				t.Fatalf("drop %s: %v", c.table, err)
			}

			if client.dispatch(c.event("N2")) {
				t.Fatalf("answered an event %s handler could not write", c.name)
			}

			if *withheld != 1 {
				t.Fatalf("withheld called %d times, want 1", *withheld)
			}
		})
	}
}
