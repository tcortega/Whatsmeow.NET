//go:build fakewhatsapp

// Package fakewhatsapp is the fake WhatsApp of a test build of whatsmeow-net (-tags fakewhatsapp) started with
// --fake-whatsapp (ADR 0019).
//
// It replaces only whatsmeow's network side. A Client has the same method signatures as *whatsmeow.Client, so a
// slice's small whatsmeow interface accepts either, and cmd/whatsmeow-net hands the slice one or the other; the
// store stays real. The Control service scripts what WhatsApp does and observes what the clients sent to it. The
// release build never links this package.
package fakewhatsapp

import (
	"context"
	"errors"
	"fmt"
	"sync"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waCompanionReg"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

// ErrNoClient is returned for a session the sidecar created no client for: the fake never answers on its behalf.
var ErrNoClient = errors.New("the session has no client")

// WhatsApp is the fake server side: the sessions' clients and the log of what they sent.
type WhatsApp struct {
	log     zerolog.Logger
	mu      sync.Mutex
	clients map[string]*Client
	sent    []*fakev1.Sent
	// changed is closed and replaced whenever an entry is appended, which wakes every waiter.
	changed chan struct{}
	// waiting counts the waiters blocked on changed, so a test can deliver only once its wait is in flight.
	waiting int
	// background runs the events dispatched off the queue.
	background sync.WaitGroup
	// scripts are what the controls scripted for each session's next calls.
	scripts map[string]*script
	// appStates are WhatsApp's side of each session's app state (chatstate.go).
	appStates map[string]*appState
	directory
	// mediaDir holds the media server's files (media.go); expired is the status ExpireMedia set for a direct path.
	mediaDir string
	expired  map[string]int32
	// retries are the media retry receipts the phone has not answered, by session and message id.
	retries map[string]retryRequest
	// withhold are the messages DeliverMessage or DeliverUndecryptableMessage marked to fail their first recording
	// (Failing).
	withhold map[marked]bool
	// pairingProps is the store.DeviceProps each session's latest pairing sent (history.go).
	pairingProps map[string]*waCompanionReg.DeviceProps
	groupState
	// record is the sidecar's outbox, which RecordEvent writes to (record.go); nil for none.
	record Recorder
}

// marked is a message of a session that DeliverMessage marked.
type marked struct{ session, id string }

// script is what the controls scripted for a session's next calls. It is the session's, not its client's: each Pair
// call gives the session a new client (ADR 0021), and a script set before it still applies.
type script struct {
	failConnect      bool
	failPreKeyUpload bool
	// failPairPhone is the IQ error code of the next PairPhone; 0 for none.
	failPairPhone int32
	// failContactQuery is the IQ error code of the next contact query; 0 for none.
	failContactQuery int32
	// failUploads is how many of the next uploads fail.
	failUploads uint32
	// holdDownload holds the next download until its call ends.
	holdDownload bool
	// rateLimitSends is how many of the next sends WhatsApp rate limits.
	rateLimitSends uint32
	// holdSend holds the next send; held is the answer of the send being held, closed by ReleaseSend.
	holdSend bool
	held     chan struct{}
	// refuseSend is the error code WhatsApp answers the next message node with; 0 for none.
	refuseSend int32
	// failGroupQuery is the IQ error code of the next group call; 0 for none.
	failGroupQuery int32
	// holdLogin holds the next login's Connected; login is the held one's dispatch, which ReleaseLogin runs.
	holdLogin bool
	login     func()
}

// setScript changes the session's script.
func (w *WhatsApp) setScript(sessionID string, set func(*script)) {
	w.mu.Lock()
	defer w.mu.Unlock()

	s, ok := w.scripts[sessionID]
	if !ok {
		s = &script{}
		w.scripts[sessionID] = s
	}

	set(s)
}

// take reports whether the session's script has what scripted says, and if so clears it with reset: a scripted
// failure applies to the next call only.
func (w *WhatsApp) take(sessionID string, scripted func(*script) bool, reset func(*script)) bool {
	w.mu.Lock()
	defer w.mu.Unlock()

	s, ok := w.scripts[sessionID]
	if !ok || !scripted(s) {
		return false
	}

	reset(s)

	return true
}

// New returns a fake WhatsApp with no clients and an empty log.
func New(log zerolog.Logger, options ...Option) *WhatsApp {
	w := &WhatsApp{
		log: log, clients: map[string]*Client{}, changed: make(chan struct{}), scripts: map[string]*script{}, appStates: map[string]*appState{},
		directory:  directory{users: map[types.JID]*user{}, pictures: map[types.JID]picture{}, blocklists: map[string][]types.BlocklistItem{}},
		groupState: groupState{groups: map[types.JID]*group{}, revoked: map[string]bool{}, refusals: map[types.JID]refusal{}},
		expired:    map[string]int32{}, retries: map[string]retryRequest{}, withhold: map[marked]bool{}, pairingProps: map[string]*waCompanionReg.DeviceProps{},
	}

	for _, option := range options {
		option(w)
	}

	return w
}

// NewClient returns a new client for a session, in place of whatsmeow.NewClient; it replaces the session's previous
// client.
func (w *WhatsApp) NewClient(sessionID string) *Client {
	c := &Client{whatsApp: w, sessionID: sessionID}

	w.mu.Lock()
	w.clients[sessionID] = c
	w.mu.Unlock()

	return c
}

// deliverMessage has the session's client learn what the message's info carries (learn), dispatches the decrypted
// parts of one message stanza to it, in order, and records what whatsmeow answers at the pin: nothing when a handler
// of any part failed (message.go:473-476), and WhatsApp delivers it again at the next login (Client.logIn),
// otherwise one receipt (message.go:503-509), of the type receiptType says.
func (w *WhatsApp) deliverMessage(ctx context.Context, sessionID string, parts ...*events.Message) error {
	w.mu.Lock()
	c, ok := w.clients[sessionID]
	w.mu.Unlock()

	id := parts[len(parts)-1].Info.ID
	if !ok {
		return fmt.Errorf("deliver message %q to session %q: %w", id, sessionID, ErrNoClient)
	}

	c.learn(ctx, &parts[len(parts)-1].Info)

	// whatsmeow stores a message's secret, with its own code, before it dispatches the message (message.go:948-949,
	// 973-994). A test's client without a store has none to keep it in.
	for _, part := range parts {
		if part.RawMessage != nil && c.Store != nil {
			c.secrets().DangerousInternals().StoreMessageSecret(context.WithoutCancel(ctx), &part.Info, part.RawMessage) //nolint:staticcheck // whatsmeow's own storing, which no exported call makes
		}
	}

	c.deliver(parts)

	return nil
}

// deliver dispatches the decrypted parts of one message stanza, in order, and answers it with one receipt once every
// handler succeeded; otherwise it answers nothing, and the stanza is left unanswered for the next login.
func (c *Client) deliver(parts []*events.Message) {
	for _, part := range parts {
		if c.dispatch(part) {
			c.conn.Lock()
			c.unanswered = append(c.unanswered, func() { c.deliver(parts) })
			c.conn.Unlock()

			return
		}
	}

	info := &parts[len(parts)-1].Info
	receipt := &fakev1.Receipt{Id: info.ID, Type: c.receiptType(info)}
	c.whatsApp.append(&fakev1.Sent{SessionId: c.sessionID, Kind: &fakev1.Sent_Receipt{Receipt: receipt}})
}

// deliverUndecryptable dispatches a message whatsmeow could not decrypt, which it answers, with an ack and a retry
// receipt the fake does not record, only once every handler succeeded [observed: message.go:324-337, 419-451];
// otherwise the stanza is left unanswered, and the fake has WhatsApp deliver it again at the next login. That
// redelivery is [observed] for a decrypted message's withheld answer (ARCHITECTURE §14 Question 4, the #10 live runs)
// and [inferred] for an undecryptable one: the server cannot tell whether a stanza was decryptable, but nobody has
// measured it (ADR 0066).
func (c *Client) deliverUndecryptable(evt *events.UndecryptableMessage) {
	if !c.dispatch(evt) {
		return
	}

	c.conn.Lock()
	c.unanswered = append(c.unanswered, func() { c.deliverUndecryptable(evt) })
	c.conn.Unlock()
}

// client returns the session's client.
func (w *WhatsApp) client(sessionID string) (*Client, error) {
	w.mu.Lock()
	c, ok := w.clients[sessionID]
	w.mu.Unlock()

	if !ok {
		return nil, fmt.Errorf("session %q: %w", sessionID, ErrNoClient)
	}

	return c, nil
}

// offQueue dispatches evt to c on its own goroutine, as whatsmeow does for connection events (client.go:554, 623).
func (w *WhatsApp) offQueue(c *Client, evt any) {
	w.background.Go(func() { c.runHandlers(evt) })
}

// Wait waits for the events dispatched off the queue.
func (w *WhatsApp) Wait() { w.background.Wait() }

func (w *WhatsApp) append(sent *fakev1.Sent) {
	w.mu.Lock()
	defer w.mu.Unlock()

	sent.Index = uint64(len(w.sent)) + 1
	w.sent = append(w.sent, sent)
	close(w.changed)
	w.changed = make(chan struct{})
}

// waitForSent returns the session's first entry after the index after, waiting until there is one or ctx ends.
func (w *WhatsApp) waitForSent(ctx context.Context, sessionID string, after uint64) (*fakev1.Sent, error) {
	for {
		w.mu.Lock()
		changed := w.changed

		for _, sent := range w.sent {
			if sent.GetIndex() > after && sent.GetSessionId() == sessionID {
				w.mu.Unlock()

				return sent, nil
			}
		}

		w.waiting++
		w.mu.Unlock()

		var err error

		select {
		case <-ctx.Done():
			err = fmt.Errorf("wait for what session %q sent after %d: %w", sessionID, after, ctx.Err())
		case <-changed:
		}

		w.mu.Lock()
		w.waiting--
		w.mu.Unlock()

		if err != nil {
			return nil, err
		}
	}
}

// waiters is the number of waits blocked until the next entry.
func (w *WhatsApp) waiters() int {
	w.mu.Lock()
	defer w.mu.Unlock()

	return w.waiting
}

// Failing wraps a session's event handler h so that the first delivery of each message DeliverMessage or
// DeliverUndecryptableMessage marked withhold_first_answer fails without running h, as a failed outbox write fails it;
// every later delivery runs h. cmd/whatsmeow-net wraps the Events slice's handler with it in fake mode (ADR 0044).
func (w *WhatsApp) Failing(sessionID string, h whatsmeow.EventHandlerWithSuccessStatus) whatsmeow.EventHandlerWithSuccessStatus {
	return func(evt any) bool {
		switch e := evt.(type) {
		case *events.Message:
			if w.takeWithhold(sessionID, e.Info.ID) {
				return false
			}
		case *events.UndecryptableMessage:
			if w.takeWithhold(sessionID, e.Info.ID) {
				return false
			}
		}

		return h(evt)
	}
}

// takeWithhold reports whether the session's message is marked to fail its first recording, and clears the mark.
func (w *WhatsApp) takeWithhold(sessionID, id string) bool {
	w.mu.Lock()
	defer w.mu.Unlock()

	key := marked{sessionID, id}
	if !w.withhold[key] {
		return false
	}

	delete(w.withhold, key)

	return true
}
