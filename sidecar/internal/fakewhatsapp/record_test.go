//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"errors"
	"testing"

	"google.golang.org/protobuf/proto"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
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

	wire, err := proto.Marshal(event)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}

	if _, err := control.RecordEvent(ctx, &fakev1.RecordEventRequest{SessionId: "s1", Event: wire}); err != nil {
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

	if _, err := control.RecordEvent(ctx, &fakev1.RecordEventRequest{SessionId: "s1", Event: []byte{0xff}}); err == nil || r.event != nil {
		t.Fatalf("RecordEvent of bytes that are no event = %v, recorded %v; want an error and nothing recorded", err, r.event)
	}

	if _, err := control.RecordEvent(ctx, &fakev1.RecordEventRequest{SessionId: "nope"}); err == nil {
		t.Fatal("RecordEvent succeeded where the outbox refused it")
	}
}
