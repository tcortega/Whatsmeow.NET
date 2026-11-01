//go:build fakewhatsapp

package fakewhatsapp

import (
	"sync"

	"go.mau.fi/whatsmeow"
)

// Client stands in for *whatsmeow.Client. Each method has whatsmeow's signature at the pin, so the small
// interface a slice declares is satisfied by both. A slice adds the methods it needs.
type Client struct {
	whatsApp  *WhatsApp
	sessionID string
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

// dispatch runs the handlers as whatsmeow's dispatchEvent does at the pin (client.go:999-1013): in order, stopping
// at the first that returns false, which is a failure; a panic is logged and is not a failure, so the answer to the
// stanza goes out.
func (c *Client) dispatch(evt any) (handlerFailed bool) {
	c.queue.Lock()
	defer c.queue.Unlock()

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
