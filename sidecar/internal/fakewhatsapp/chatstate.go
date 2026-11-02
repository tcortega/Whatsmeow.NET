//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"errors"
	"fmt"
	"slices"
	"time"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/appstate"
	"go.mau.fi/whatsmeow/proto/waSyncAction"
	"go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

// errNoAppStateKeys is whatsmeow's error for a write before the phone shared an app state key; it has no sentinel
// (appstate.go:560-561).
var errNoAppStateKeys = errors.New("no app state keys found, creating app state keys is not yet supported")

// mutation is one entry of an app state collection, as WhatsApp keeps it: its index and its value.
type mutation struct {
	index []string
	value *waSyncAction.SyncActionValue
}

// appState is WhatsApp's side of a session's app state: each collection's entries by index, and what the controls
// did to the session's copy. w.mu guards it.
type appState struct {
	collections map[appstate.WAPatchName]map[string]mutation
	// corrupted is the collections whose copy is out of step: true when a full sync does not repair it.
	corrupted map[appstate.WAPatchName]bool
	// hold makes full syncs wait until released is closed.
	hold     bool
	released chan struct{}
}

// appStateOf returns the session's app state. w.mu is held.
func (w *WhatsApp) appStateOf(sessionID string) *appState {
	s, ok := w.appStates[sessionID]
	if !ok {
		s = &appState{collections: map[appstate.WAPatchName]map[string]mutation{}, corrupted: map[appstate.WAPatchName]bool{}, released: make(chan struct{})}
		w.appStates[sessionID] = s
	}

	return s
}

// apply stores the patch's mutations, as WhatsApp does once it took them, and returns them with the patch's time.
func (w *WhatsApp) apply(sessionID string, patch appstate.PatchInfo) []mutation {
	w.mu.Lock()
	defer w.mu.Unlock()

	at := patch.Timestamp
	if at.IsZero() {
		at = time.Now()
	}

	collection, ok := w.appStateOf(sessionID).collections[patch.Type]
	if !ok {
		collection = map[string]mutation{}
		w.appStateOf(sessionID).collections[patch.Type] = collection
	}

	applied := make([]mutation, 0, len(patch.Mutations))

	for _, m := range patch.Mutations {
		// EncodePatch stamps each value with the patch's time (appstate/encode.go:274).
		m.Value.Timestamp = new(at.UnixMilli())
		collection[indexKey(m.Index)] = mutation{index: m.Index, value: m.Value}
		applied = append(applied, mutation{index: m.Index, value: m.Value})
	}

	return applied
}

// corruption reports whether the session's copy of the collection is out of step, and whether a full sync repairs it.
func (w *WhatsApp) corruption(sessionID string, name appstate.WAPatchName) (corrupted, repairable bool) {
	w.mu.Lock()
	defer w.mu.Unlock()

	unrecoverable, ok := w.appStateOf(sessionID).corrupted[name]

	return ok, !unrecoverable
}

// mismatch is the error whatsmeow's patch decoding returns for a copy out of step (appstate.go:169,
// appstate/decode.go:253).
func mismatch(name appstate.WAPatchName) error {
	return fmt.Errorf("failed to decode app state %s patches: %w", name, fmt.Errorf("failed to verify patch v7: %w", appstate.ErrMismatchingLTHash))
}

// SendAppState sends the patch as whatsmeow's does (appstate.go:548-638): without an app state key it fails before
// anything goes out; to a corrupted collection WhatsApp answers 409 with patches that do not verify, so whatsmeow
// dispatches AppStateSyncError on the caller's goroutine without the app state lock (appstate.go:607-614, 167) and
// fails with both; otherwise WhatsApp takes it, and whatsmeow's sync after it dispatches the change's events on a
// goroutine (appstate.go:627-635).
func (c *Client) SendAppState(ctx context.Context, patch appstate.PatchInfo) error {
	key, err := c.Store.AppStateKeys.GetLatestAppStateSyncKeyID(ctx)
	if err != nil {
		return fmt.Errorf("failed to get latest app state key ID: %w", err)
	}

	if key == nil {
		return errNoAppStateKeys
	}

	if !c.IsConnected() {
		return whatsmeow.ErrNotConnected
	}

	if corrupted, _ := c.whatsApp.corruption(c.sessionID, patch.Type); corrupted {
		decodeErr := mismatch(patch.Type)
		c.runHandlers(&events.AppStateSyncError{Name: patch.Type, Error: decodeErr})

		mainErr := fmt.Errorf("%w (%s): %s", whatsmeow.ErrAppStateUpdate, patch.Type, `<error code="409" text="conflict"/>`)

		return fmt.Errorf("%w (also, applying patches in the response failed: %w)", mainErr, decodeErr)
	}

	c.appStateLock.Lock()
	applied := c.whatsApp.apply(c.sessionID, patch)
	c.appStateLock.Unlock()

	c.whatsApp.background.Go(func() {
		for _, m := range applied {
			if evt := chatEvent(m, false); evt != nil {
				c.runHandlers(evt)
			}
		}
	})

	return nil
}

// FetchAppState syncs a collection. The fake keeps no patches the client has not seen, so only a full sync does
// anything: it records the request, waits while HoldFullSync holds it, all under the app state lock (appstate.go:46),
// then repairs a corrupted copy and dispatches every entry of the collection marked FromFullSync, and
// AppStateSyncComplete, once it let go of the lock (appstate.go:36-38, 92-94), as whatsmeow does with
// EmitAppStateEventsOnFullSync. A copy it cannot repair fails as a snapshot that does not verify, with
// AppStateSyncError dispatched under the lock (appstate.go:167).
func (c *Client) FetchAppState(ctx context.Context, name appstate.WAPatchName, fullSync, _ bool) error {
	if !fullSync {
		return nil
	}

	c.appStateLock.Lock()
	c.whatsApp.append(&fakev1.Sent{SessionId: c.sessionID, Kind: &fakev1.Sent_AppStateFullSync{AppStateFullSync: &fakev1.AppStateFullSync{Collection: string(name)}}})

	if err := c.whatsApp.waitWhileHeld(ctx, c.sessionID); err != nil {
		c.appStateLock.Unlock()

		return fmt.Errorf("failed to fetch app state %s patches: %w", name, err)
	}

	if corrupted, repairable := c.whatsApp.corruption(c.sessionID, name); corrupted && !repairable {
		err := fmt.Errorf("failed to decode app state %s patches: failed to verify snapshot: %w", name, fmt.Errorf("failed to verify patch v7: %w", appstate.ErrMismatchingLTHash))
		c.runHandlers(&events.AppStateSyncError{Name: name, FullSync: true, Error: err})
		c.appStateLock.Unlock()

		return err
	}

	entries := c.whatsApp.repair(c.sessionID, name)
	c.appStateLock.Unlock()

	for _, m := range entries {
		if evt := chatEvent(m, true); evt != nil {
			c.runHandlers(evt)
		}
	}

	c.runHandlers(&events.AppStateSyncComplete{Name: name, Version: 7})

	return nil
}

// waitWhileHeld waits while HoldFullSync holds the session's full syncs, or until ctx ends.
func (w *WhatsApp) waitWhileHeld(ctx context.Context, sessionID string) error {
	w.mu.Lock()
	s := w.appStateOf(sessionID)
	hold, released := s.hold, s.released
	w.mu.Unlock()

	if !hold {
		return nil
	}

	select {
	case <-released:
		return nil
	case <-ctx.Done():
		return fmt.Errorf("held: %w", ctx.Err())
	}
}

// repair puts the session's copy of the collection back in step, and returns its entries, ordered by index.
func (w *WhatsApp) repair(sessionID string, name appstate.WAPatchName) []mutation {
	w.mu.Lock()
	defer w.mu.Unlock()

	s := w.appStateOf(sessionID)
	delete(s.corrupted, name)

	keys := make([]string, 0, len(s.collections[name]))
	for key := range s.collections[name] {
		keys = append(keys, key)
	}

	slices.Sort(keys)

	entries := make([]mutation, 0, len(keys))
	for _, key := range keys {
		entries = append(entries, s.collections[name][key])
	}

	return entries
}

// chatEvent is the chat state event whatsmeow dispatches for a mutation (appstate.go:254-363), or nil for one this
// fake does not make.
func chatEvent(m mutation, fullSync bool) any {
	var jid types.JID
	if len(m.index) > 1 {
		jid, _ = types.ParseJID(m.index[1])
	}

	ts := time.UnixMilli(m.value.GetTimestamp())

	switch m.index[0] {
	case appstate.IndexMute:
		return &events.Mute{JID: jid, Timestamp: ts, Action: m.value.GetMuteAction(), FromFullSync: fullSync}
	case appstate.IndexPin:
		return &events.Pin{JID: jid, Timestamp: ts, Action: m.value.GetPinAction(), FromFullSync: fullSync}
	case appstate.IndexArchive:
		return &events.Archive{JID: jid, Timestamp: ts, Action: m.value.GetArchiveChatAction(), FromFullSync: fullSync}
	case appstate.IndexMarkChatAsRead:
		return &events.MarkChatAsRead{JID: jid, Timestamp: ts, Action: m.value.GetMarkChatAsReadAction(), FromFullSync: fullSync}
	case appstate.IndexStar:
		if len(m.index) < 5 {
			return nil
		}

		evt := &events.Star{
			ChatJID: jid, MessageID: m.index[2], Timestamp: ts, Action: m.value.GetStarAction(), IsFromMe: m.index[3] == "1", FromFullSync: fullSync,
		}
		if m.index[4] != "0" {
			evt.SenderJID, _ = types.ParseJID(m.index[4])
		}

		return evt
	default:
		return nil
	}
}

// syncFromPhone is the session's client syncing a change the phone made, as after a server_sync notification: on the
// node queue, holding the app state lock while it fetches (notification.go:73-89, 481-483; appstate.go:46). A
// corrupted copy fails to decode the patch: AppStateSyncError is dispatched under the lock (appstate.go:167), and the
// change does not arrive. Otherwise the change's events are dispatched once the lock is free (appstate.go:36-38).
func (c *Client) syncFromPhone(patch appstate.PatchInfo, fullSync bool) {
	c.queue.Lock()
	defer c.queue.Unlock()

	c.appStateLock.Lock()
	applied := c.whatsApp.apply(c.sessionID, patch)

	if corrupted, _ := c.whatsApp.corruption(c.sessionID, patch.Type); corrupted {
		c.runHandlers(&events.AppStateSyncError{Name: patch.Type, Error: mismatch(patch.Type)})
		c.appStateLock.Unlock()

		return
	}

	c.appStateLock.Unlock()

	for _, m := range applied {
		if evt := chatEvent(m, fullSync); evt != nil {
			c.runHandlers(evt)
		}
	}
}

// shareAppStateKey stores an app state key for the device, as the phone's key share does once after pairing
// (message.go:827-861).
func shareAppStateKey(ctx context.Context, device *store.Device) error {
	key := store.AppStateSyncKey{Data: make([]byte, 32), Fingerprint: []byte{}, Timestamp: time.Now().UnixMilli()}
	if err := device.AppStateKeys.PutAppStateSyncKey(ctx, []byte{0, 0, 0, 1}, key); err != nil {
		return fmt.Errorf("store the app state key: %w", err)
	}

	return nil
}
