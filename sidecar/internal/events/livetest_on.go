//go:build livetest

package events

import (
	"strings"
	"sync"

	"github.com/rs/zerolog"
	waevents "go.mau.fi/whatsmeow/types/events"
)

// WithholdPrefix marks a text message whose first answer the live tests' build withholds (ADR 0032).
const WithholdPrefix = "wmn-withhold "

// withheld is the live tests' seam only: it withholds the answer to the first delivery of each message whose text
// starts with WithholdPrefix, as a failed outbox write would, so a live test measures when WhatsApp delivers it again
// (ARCHITECTURE §7.3). Every later delivery is recorded. A message is its session and its id: the same id in another
// session is withheld once there too. A release build never links this file (build/ci.sh).
type withheld struct {
	mu   sync.Mutex
	seen map[withheldKey]bool
}

type withheldKey struct{ session, id string }

func (w *withheld) once(log zerolog.Logger, sessionID string, evt any) bool {
	msg, ok := evt.(*waevents.Message)
	if !ok || msg == nil {
		return false
	}

	text := msg.Message.GetConversation()
	if text == "" {
		text = msg.Message.GetExtendedTextMessage().GetText()
	}

	if !strings.HasPrefix(text, WithholdPrefix) {
		return false
	}

	w.mu.Lock()
	defer w.mu.Unlock()

	key := withheldKey{sessionID, msg.Info.ID}
	if w.seen[key] {
		return false
	}

	if w.seen == nil {
		w.seen = map[withheldKey]bool{}
	}

	w.seen[key] = true

	log.Warn().Str("session_id", sessionID).Str("message_id", msg.Info.ID).Msg("livetest: the answer to this message is withheld once")

	return true
}
