//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"errors"
	"fmt"
	"sync"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/types/events"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

// errConnectFailed is the error of a connect FailNextConnect scripted: one whatsmeow does not retry.
var errConnectFailed = errors.New("websocket: bad handshake (fake WhatsApp: FailNextConnect)")

// Client stands in for *whatsmeow.Client. Each method has whatsmeow's signature at the pin, so the small
// interface a slice declares is satisfied by both. A slice adds the methods it needs.
type Client struct {
	// Store is the session's device, as on *whatsmeow.Client; the store behind it is real.
	Store *store.Device

	whatsApp  *WhatsApp
	sessionID string
	// conn guards the connection's state.
	conn        sync.Mutex
	connected   bool
	failConnect bool
	failLogout  bool
	// queue runs one dispatch at a time, as whatsmeow's per-client node queue does (ARCHITECTURE §7.3).
	queue    sync.Mutex
	mu       sync.RWMutex
	handlers []whatsmeow.EventHandlerWithSuccessStatus
	lastID   uint32
}

// AddEventHandlerWithSuccessStatus registers a handler; returning false withholds the answer to the stanza (for a
// message, its receipt).
func (c *Client) AddEventHandlerWithSuccessStatus(handler whatsmeow.EventHandlerWithSuccessStatus) uint32 {
	c.mu.Lock()
	defer c.mu.Unlock()

	c.handlers = append(c.handlers, handler)
	c.lastID++

	return c.lastID
}

// ConnectContext opens the connection as whatsmeow's does after the handshake: it returns, and WhatsApp's login
// answer arrives later as an event dispatched off the queue (connectionevents.go:204). An unpaired device gets no
// Connected (connectionevents.go:166-169).
func (c *Client) ConnectContext(context.Context) error {
	c.conn.Lock()
	defer c.conn.Unlock()

	switch {
	case c.Store.Deleted:
		return store.ErrDeviceDeleted
	case c.connected:
		return whatsmeow.ErrAlreadyConnected
	case c.failConnect:
		c.failConnect = false

		return fmt.Errorf("connect session %q: %w", c.sessionID, errConnectFailed)
	}

	c.connected = true
	if c.Store.ID != nil {
		c.whatsApp.offQueue(c, &events.Connected{})
	}

	return nil
}

// Disconnect closes an open connection; like whatsmeow's, it raises no event (client.go:695-703).
func (c *Client) Disconnect() {
	c.conn.Lock()
	defer c.conn.Unlock()

	if !c.connected {
		return
	}

	c.connected = false
	c.whatsApp.append(&fakev1.Sent{SessionId: c.sessionID, Kind: &fakev1.Sent_Disconnect{Disconnect: &fakev1.Disconnect{}}})
}

// IsConnected reports whether the connection is open, as whatsmeow's does.
func (c *Client) IsConnected() bool {
	c.conn.Lock()
	defer c.conn.Unlock()

	return c.connected
}

// Logout sends the unlink request, then disconnects and deletes the device, raising no event, as whatsmeow's does
// (client.go:749-779).
func (c *Client) Logout(ctx context.Context) error {
	if c.Store.ID == nil {
		return whatsmeow.ErrNotLoggedIn
	}

	c.conn.Lock()
	connected, fail := c.connected, c.failLogout
	c.failLogout = false
	c.conn.Unlock()

	if !connected {
		return fmt.Errorf("error sending logout request: %w", whatsmeow.ErrNotConnected)
	}

	if fail {
		return fmt.Errorf("error sending logout request: %w", whatsmeow.ErrIQTimedOut)
	}

	c.whatsApp.append(&fakev1.Sent{SessionId: c.sessionID, Kind: &fakev1.Sent_RemoveCompanionDevice{RemoveCompanionDevice: &fakev1.RemoveCompanionDevice{}}})
	c.Disconnect()

	if err := c.Store.Delete(ctx); err != nil {
		return fmt.Errorf("error deleting data from store: %w", err)
	}

	return nil
}

// drop ends the connection from WhatsApp's side, after which whatsmeow does not reconnect: it expects the
// disconnect, so it raises no Disconnected (connectionevents.go:37-38, 120-127).
func (c *Client) drop() {
	c.conn.Lock()
	c.connected = false
	c.conn.Unlock()
}

// dispatch runs the handlers as whatsmeow's dispatchEvent does at the pin (client.go:999-1013): in order, stopping
// at the first that returns false, which is a failure; a panic is logged and is not a failure, so the answer to the
// stanza goes out.
func (c *Client) dispatch(evt any) (handlerFailed bool) {
	c.queue.Lock()
	defer c.queue.Unlock()

	return c.runHandlers(evt)
}

// runHandlers runs the handlers without the queue, as whatsmeow does for the connection events it dispatches on
// their own goroutines (ARCHITECTURE §7.3).
func (c *Client) runHandlers(evt any) (handlerFailed bool) {
	c.mu.RLock()
	defer func() {
		c.mu.RUnlock()

		if err := recover(); err != nil {
			c.whatsApp.log.Error().Str("session_id", c.sessionID).Interface("panic", err).Msgf("event handler panicked while handling a %T", evt)
		}
	}()

	for _, handler := range c.handlers {
		if !handler(evt) {
			return true
		}
	}

	return false
}
