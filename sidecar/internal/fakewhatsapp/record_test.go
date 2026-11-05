//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/rs/zerolog"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	"google.golang.org/protobuf/proto"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/outbox"
	sidecarstore "github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

// recorded is a recorder that keeps what it was given, and fails with err.
type recorded struct {
	sessionID string
	event     *whatsmeowv1.StreamEventsResponse
	err       error
}

func (r *recorded) record(_ context.Context, sessionID string, event *whatsmeowv1.StreamEventsResponse) error {
	r.sessionID, r.event = sessionID, event

	return r.err
}

func TestRecordEventRecordsTheEventAsItIs(t *testing.T) {
	t.Parallel()

	r := &recorded{}
	_, control, ctx := start(t, WithRecorder(r.record))

	// A message without a chat, which no slice records: the client cannot read it (#122).
	event := &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_Message{Message: &whatsmeowv1.MessageEvent{
		Info: &whatsmeowv1.MessageInfo{Id: "M1", Source: &whatsmeowv1.MessageSource{Sender: "5511900000001@s.whatsapp.net"}},
	}}}

	// JSON as a test writes it, in the contract's own field names.
	json := `{"message": {"info": {"id": "M1", "source": {"sender": "5511900000001@s.whatsapp.net"}}}}`
	if _, err := control.RecordEvent(ctx, &fakev1.RecordEventRequest{SessionId: "s1", EventJson: json}); err != nil {
		t.Fatalf("RecordEvent: %v", err)
	}

	if r.sessionID != "s1" || !proto.Equal(r.event, event) {
		t.Fatalf("recorded %q, %v; want s1, %v", r.sessionID, r.event, event)
	}
}

func TestRecordEventFailsWhenTheEventIsNotOneOrIsNotRecorded(t *testing.T) {
	t.Parallel()

	r := &recorded{err: errors.New("no such session")}
	_, control, ctx := start(t, WithRecorder(r.record))

	if _, err := control.RecordEvent(ctx, &fakev1.RecordEventRequest{SessionId: "s1", EventJson: `{"no_such_case": {}}`}); err == nil || r.event != nil {
		t.Fatalf("RecordEvent of JSON that is no event = %v, recorded %v; want an error and nothing recorded", err, r.event)
	}

	if _, err := control.RecordEvent(ctx, &fakev1.RecordEventRequest{SessionId: "nope", EventJson: "{}"}); err == nil {
		t.Fatal("RecordEvent succeeded where the outbox refused it")
	}
}

// TestRecordEventOfAnUnknownSessionFailsOnTheOutbox: the recorder the sidecar gives the fake is its outbox's Record
// (cmd/whatsmeow-net fake_on.go), which refuses a session that does not exist. A store file, unlike the fake's own
// tests (openDeviceStore): the outbox is the sidecar's.
func TestRecordEventOfAnUnknownSessionFailsOnTheOutbox(t *testing.T) {
	t.Parallel()

	db, err := sidecarstore.Open(context.Background(), filepath.Join(t.TempDir(), "whatsmeow-net.db"), zerolog.Nop())
	if err != nil {
		t.Fatalf("open the store: %v", err)
	}

	t.Cleanup(func() { _ = db.Close() })

	box := outbox.New(db.DB, db.Writer)
	record := func(ctx context.Context, sessionID string, event *whatsmeowv1.StreamEventsResponse) error {
		_, err := box.Record(ctx, sessionID, "", event)

		return err //nolint:wrapcheck // RecordEvent wraps it, as in fake_on.go
	}
	_, control, ctx := start(t, WithRecorder(record))

	// The outbox's refusal, a foreign key the session's row does not satisfy, is INTERNAL, as every fake error is.
	_, err = control.RecordEvent(ctx, &fakev1.RecordEventRequest{SessionId: "nope", EventJson: `{"message": {}}`})
	if status.Code(err) != codes.Internal {
		t.Fatalf("RecordEvent of a session that does not exist = %v; want INTERNAL", err)
	}

	var rows int
	if err := db.DB.QueryRowContext(ctx, "SELECT count(*) FROM outbox").Scan(&rows); err != nil || rows != 0 {
		t.Fatalf("the outbox has %d rows, %v; want none", rows, err)
	}
}
