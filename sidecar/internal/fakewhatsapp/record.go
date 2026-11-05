//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"errors"
	"fmt"

	"google.golang.org/protobuf/encoding/protojson"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// Recorder records an event in a session's outbox as it is, under no key: the sidecar's outbox.Record.
type Recorder func(ctx context.Context, sessionID string, event *whatsmeowv1.StreamEventsResponse) error

// WithRecorder gives RecordEvent the sidecar's outbox.
func WithRecorder(record Recorder) Option { return func(w *WhatsApp) { w.record = record } }

// RecordEvent records the event in the session's outbox as it is, past every slice's mapping (fake.proto).
func (c *Control) RecordEvent(ctx context.Context, req *fakev1.RecordEventRequest) (*fakev1.RecordEventResponse, error) {
	if c.whatsApp.record == nil {
		return nil, errors.New("record an event: the fake has no outbox")
	}

	event := &whatsmeowv1.StreamEventsResponse{}
	if err := protojson.Unmarshal([]byte(req.GetEventJson()), event); err != nil {
		return nil, fmt.Errorf("record an event: not a StreamEventsResponse: %w", err)
	}

	if err := c.whatsApp.record(ctx, req.GetSessionId(), event); err != nil {
		return nil, fmt.Errorf("RecordEvent: %w", err)
	}

	return &fakev1.RecordEventResponse{}, nil
}
