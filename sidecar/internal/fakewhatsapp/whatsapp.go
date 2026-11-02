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
	directory
	// mediaDir holds the media server's files (media.go); expired is the status ExpireMedia set for a direct path.
	mediaDir string
	expired  map[string]int32
	// retries are the media retry receipts the phone has not answered, by session and message id.
	retries map[string]retryRequest
}

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
		log: log, clients: map[string]*Client{}, changed: make(chan struct{}), scripts: map[string]*script{},
		directory: directory{users: map[types.JID]*user{}, pictures: map[types.JID]picture{}, blocklists: map[string][]types.BlocklistItem{}},
		expired:   map[string]int32{}, retries: map[string]retryRequest{},
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
// parts of one message stanza to it, in order, and records
// what whatsmeow answers at the pin: nothing when a handler of any part failed (message.go:459-462), otherwise one
// receipt (message.go:489-495), of type inactive for a message from someone else while the client sends no active
// receipts, and of no type once SendPresence turned them on (receipt.go:280-296).
func (w *WhatsApp) deliverMessage(ctx context.Context, sessionID string, parts ...*events.Message) error {
	w.mu.Lock()
	c, ok := w.clients[sessionID]
	w.mu.Unlock()

	id := parts[len(parts)-1].Info.ID
	if !ok {
		return fmt.Errorf("deliver message %q to session %q: %w", id, sessionID, ErrNoClient)
	}

	c.learn(ctx, &parts[len(parts)-1].Info)

	for _, part := range parts {
		if c.dispatch(part) {
			return nil
		}
	}

	receipt := &fakev1.Receipt{Id: id, Type: c.receiptType()}
	w.append(&fakev1.Sent{SessionId: sessionID, Kind: &fakev1.Sent_Receipt{Receipt: receipt}})

	return nil
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

// offQueue dispatches evt to c on its own goroutine, as whatsmeow does for connection events (client.go:552, 621).
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
