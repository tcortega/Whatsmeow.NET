// Package events is the Events slice: the whatsmeow event handler that records each session's events in the outbox,
// and EventService, which streams them to a consumer and moves its cursor (events.proto, ARCHITECTURE §7.3).
package events

import (
	"context"
	"errors"
	"fmt"
	"sync"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"google.golang.org/grpc"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/outbox"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// batch is how many events a stream reads from the outbox at a time.
const batch = 100

// errReplaced is the cause a stream's context ends with when a newer stream of its consumer replaces it.
var errReplaced = errors.New("replaced by a newer stream of the same consumer")

// Service implements whatsmeowv1.EventServiceServer and the whatsmeow event handler.
type Service struct {
	whatsmeowv1.UnimplementedEventServiceServer

	box     *outbox.Outbox
	log     zerolog.Logger
	mappers []Mapper

	mu      sync.Mutex
	streams map[consumer]*registration
	// rings are the sessions' ephemeral events (ring.go).
	rings map[string]*ring
	// held is the live tests' seam, which withholds the first answer to a marked message; a release build's never
	// does (livetest_off.go, ADR 0032).
	held withheld
}

type consumer struct{ session, name string }

// registration is one open stream; it is compared by identity, so a replaced stream never removes its successor.
type registration struct{ cancel context.CancelCauseFunc }

// Mapper is how a slice records its own whatsmeow events (ARCHITECTURE §3: a slice owns its event mapping): it maps
// evt of the session sessionID to the outbox event it records and that event's natural key, or false for an event the
// slice does not record. It is pure: no I/O. An empty key never matches, so each such event is recorded once more.
type Mapper func(sessionID string, evt any) (key string, event *whatsmeowv1.StreamEventsResponse, ok bool)

// New returns the slice on box. Each mapper is a slice's: the handler records what the first that maps an event
// returns, after the events this slice maps itself.
func New(box *outbox.Outbox, log zerolog.Logger, mappers ...Mapper) *Service {
	return &Service{box: box, log: log, mappers: mappers, streams: map[consumer]*registration{}, rings: map[string]*ring{}}
}

// Handler is the whatsmeow event handler of one session's client (ARCHITECTURE §7.3). It maps the event, records it
// in one short transaction, and returns true only after the commit: whatsmeow answers a gated stanza only then
// (message.go:459-497). It returns false when the write fails or anything panics, with its own recover, because
// whatsmeow's dispatchEvent turns a panic into success (client.go:999-1013). It never calls .NET, the network or
// media, and never returns false because no consumer is connected. An event it does not record returns true.
func (svc *Service) Handler(sessionID string) whatsmeow.EventHandlerWithSuccessStatus {
	// Bound now, so a client still running after its session was deleted pushes only into its own dead ring, never
	// into the ring of a session created again with the same id.
	ephemeral := svc.ring(sessionID)

	return func(evt any) (ok bool) {
		defer func() {
			if r := recover(); r != nil {
				svc.log.Error().Str("session_id", sessionID).Interface("panic", r).Msgf("recording a %T panicked; its answer is withheld", evt)

				ok = false
			}
		}()

		// Ephemeral: into the session's ring in memory, never the outbox, and never waiting for a stream.
		if event, ok := toEphemeral(evt); ok {
			ephemeral.push(event)

			return true
		}

		if svc.held.once(svc.log, sessionID, evt) {
			return false
		}

		key, event, record := svc.toEvent(sessionID, evt)
		if !record {
			return true
		}

		// No caller's context: whatsmeow gives the handler none. SQLite's busy_timeout bounds the write.
		differs, err := svc.box.Record(context.Background(), sessionID, key, event)
		if err != nil {
			svc.log.Error().Err(err).Str("session_id", sessionID).Msgf("recording a %T failed; its answer is withheld", evt)

			return false
		}

		if differs {
			// Never overwritten: the first is what a consumer may have handled. Recorded beside, and visible.
			svc.log.Warn().Str("session_id", sessionID).Msgf("a %T repeats a recorded event's key with other content; it is recorded beside it", evt)
		}

		return true
	}
}

// toEvent maps evt with this slice's own mapping, then with each slice's mapper.
func (svc *Service) toEvent(sessionID string, evt any) (string, *whatsmeowv1.StreamEventsResponse, bool) {
	if key, event, ok := toEvent(evt); ok {
		return key, event, true
	}

	for _, mapper := range svc.mappers {
		if key, event, ok := mapper(sessionID, evt); ok {
			return key, event, true
		}
	}

	return "", nil, false
}

// StreamEvents sends the session's events after max(after_seq, cursor), then live ones, until the client cancels,
// a newer stream of the consumer replaces it, or the session is deleted (events.proto).
func (svc *Service) StreamEvents(req *whatsmeowv1.StreamEventsRequest, stream grpc.ServerStreamingServer[whatsmeowv1.StreamEventsResponse]) error {
	id, name := req.GetSessionId(), req.GetConsumer()
	if err := check(id, name); err != nil {
		return err
	}

	// Taken first: a session deleted from here on ends the stream.
	watch, recorded, err := svc.box.Watch(stream.Context(), id)
	if err != nil {
		return fmt.Errorf("stream session %q: %w", id, err)
	}

	if req.GetAfterSeq() > recorded {
		return fmt.Errorf("stream session %q: %w", id, server.InvalidArgument("after_seq", "%d is above the session's highest recorded seq %d", req.GetAfterSeq(), recorded))
	}

	ctx, cancel := context.WithCancelCause(stream.Context())
	defer cancel(nil)

	defer svc.register(consumer{id, name}, cancel)()

	cursor, err := svc.box.Cursor(ctx, id, name)
	if err != nil {
		return fmt.Errorf("stream session %q: %w", id, err)
	}

	return svc.send(ctx, id, max(req.GetAfterSeq(), cursor), watch, stream)
}

// register makes this stream the consumer's one, ending the open one, and returns its unregister.
func (svc *Service) register(key consumer, cancel context.CancelCauseFunc) func() {
	me := &registration{cancel: cancel}

	svc.mu.Lock()
	if open, ok := svc.streams[key]; ok {
		open.cancel(errReplaced)
	}

	svc.streams[key] = me
	svc.mu.Unlock()

	return func() {
		svc.mu.Lock()
		defer svc.mu.Unlock()

		// A newer stream may have replaced this one meanwhile; it stays registered.
		if svc.streams[key] == me {
			delete(svc.streams, key)
		}
	}
}

func (svc *Service) send(ctx context.Context, id string, after uint64, watch outbox.Watch,
	stream grpc.ServerStreamingServer[whatsmeowv1.StreamEventsResponse],
) error {
	for {
		changed := watch.Changed()

		events, err := svc.box.Read(ctx, id, after, batch)
		if err != nil {
			return ended(ctx, id, err)
		}

		for _, event := range events {
			if err := sendOrEnd(ctx, stream, event); err != nil {
				return ended(ctx, id, err)
			}

			after = event.GetSeq()
		}

		if len(events) == batch {
			continue
		}

		select {
		case <-ctx.Done():
			return ended(ctx, id, ctx.Err())
		case <-watch.Deleted():
			return fmt.Errorf("session %q was deleted: %w", id, server.ErrSessionNotFound)
		case <-changed:
		}
	}
}

// sendOrEnd sends event, or returns when ctx ends first. Send blocks while the client does not read: backpressure
// stops here, and the events stay in the outbox, or the ephemeral ones in the ring until it drops them. Its wait ends
// only with the stream itself, not with ctx, a context derived from it (grpc-go http2_server.go:624,
// flowcontrol.go:54-66), so a replaced stream would stay in it; the send runs on its own goroutine, which ends when
// grpc-go finishes the stream as this handler returns.
func sendOrEnd[T any](ctx context.Context, stream grpc.ServerStreamingServer[T], event *T) error {
	sent := make(chan error, 1)

	go func() { sent <- stream.Send(event) }()

	select {
	case err := <-sent:
		return err
	case <-ctx.Done():
		return fmt.Errorf("send: %w", ctx.Err())
	}
}

// ended is why a stream ended: replaced, or its own error.
func ended(ctx context.Context, id string, err error) error {
	if errors.Is(context.Cause(ctx), errReplaced) {
		return fmt.Errorf("stream session %q: %w", id, server.ErrConsumerReplaced)
	}

	return fmt.Errorf("stream session %q: %w", id, err)
}

// AckEvents moves the consumer's cursor (events.proto).
func (svc *Service) AckEvents(ctx context.Context, req *whatsmeowv1.AckEventsRequest) (*whatsmeowv1.AckEventsResponse, error) {
	if err := check(req.GetSessionId(), req.GetConsumer()); err != nil {
		return nil, err
	}

	if err := svc.box.Ack(ctx, req.GetSessionId(), req.GetConsumer(), req.GetUpToSeq()); err != nil {
		return nil, fmt.Errorf("ack events: %w", err)
	}

	return &whatsmeowv1.AckEventsResponse{}, nil
}

func check(id, name string) error {
	if err := server.CheckID("session_id", id); err != nil {
		return fmt.Errorf("session id: %w", err)
	}

	if err := server.CheckID("consumer", name); err != nil {
		return fmt.Errorf("consumer: %w", err)
	}

	return nil
}
