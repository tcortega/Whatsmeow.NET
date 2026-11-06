// Package chatstate is the Chat state slice: muting, pinning, archiving and marking chats read, starring messages, and
// the chat state events of the app state sync (chatstate.proto, ADR 0031). It also recovers an app state collection
// whose copy got out of step with WhatsApp's (recovery.go).
//
// Chat state here is app state. In whatsmeow and on the wire, "chatstate" is typing (composing, paused), which is the
// Presence slice's ChatPresence.
//
// Chat JIDs are other people's personal data: nothing here logs them, and no error this package returns carries one,
// whatsmeow's text included (classify, ARCHITECTURE §7.8).
package chatstate

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/appstate"
	"go.mau.fi/whatsmeow/socket"
	"go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/types"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/session"
)

// rateOverLimit is the code of whatsmeow's ErrIQRateOverLimit (errors.go:198).
const rateOverLimit = 429

// conflict is the code WhatsApp rejects an app state write with when the patch does not follow its copy
// (appstate.go:616).
const conflict = "409"

// Client is what the slice needs of a whatsmeow client. *whatsmeow.Client and the fake WhatsApp's client both
// satisfy it (ADR 0019).
type Client interface {
	SendAppState(ctx context.Context, patch appstate.PatchInfo) error
	FetchAppState(ctx context.Context, name appstate.WAPatchName, fullSync, onlyIfNotSynced bool) error
}

var _ Client = (*whatsmeow.Client)(nil)

// Sessions is what the slice needs of the Sessions slice: a session's client while it is online, and its device for
// the app state keys.
type Sessions interface {
	Online(id string) (session.Client, <-chan struct{}, error)
	Device(id string) (*store.Device, error)
}

// Service implements whatsmeowv1.ChatStateServiceServer, and recovers the sessions' app state (recovery.go).
type Service struct {
	whatsmeowv1.UnimplementedChatStateServiceServer

	log      zerolog.Logger
	sessions Sessions
	// now is the clock of the recoveries' cooldown.
	now func() time.Time

	// ctx ends the recoveries at Close.
	ctx    context.Context //nolint:containedctx // the recoveries' lifetime, ended by Close
	cancel context.CancelFunc
	mu     sync.Mutex
	// recoveries is each session's recovery state and its worker, while it runs.
	recoveries map[string]*recovery
	workers    sync.WaitGroup
}

// New returns the slice. Use gives it the sessions before any client connects.
func New(log zerolog.Logger) *Service {
	ctx, cancel := context.WithCancel(context.Background())

	return &Service{log: log, now: time.Now, ctx: ctx, cancel: cancel, recoveries: map[string]*recovery{}}
}

// Use gives the slice the sessions. The Sessions slice makes the clients, whose handler is this slice's (Handler), so
// it comes after; it is called once, before any client connects.
func (svc *Service) Use(sessions Sessions) { svc.sessions = sessions }

// SetChatState sets one chat state, or stars one message (chatstate.proto).
func (svc *Service) SetChatState(ctx context.Context, req *whatsmeowv1.SetChatStateRequest) (*whatsmeowv1.SetChatStateResponse, error) {
	if err := server.CheckID("session_id", req.GetSessionId()); err != nil {
		return nil, fmt.Errorf("session id: %w", err)
	}

	patch, err := build(req)
	if err != nil {
		return nil, fmt.Errorf("set the chat state: %w", err)
	}

	client, online, err := svc.client(ctx, req.GetSessionId())
	if err != nil {
		return nil, err
	}

	if err := svc.write(ctx, req.GetSessionId(), client, online, patch); err != nil {
		return nil, err
	}

	return &whatsmeowv1.SetChatStateResponse{}, nil
}

// client is the client of a session that is online and can write app state, and the channel Online closes once it
// is not: whatsmeow writes with the latest app state key, which the phone shares once after pairing
// (message.go:827-870), and without one fails with an error that has no sentinel (appstate.go:566-571). Until then the
// session is not connected to its account's app state: NOT_CONNECTED.
func (svc *Service) client(ctx context.Context, id string) (Client, <-chan struct{}, error) {
	c, online, err := svc.sessions.Online(id)
	if err != nil {
		return nil, nil, fmt.Errorf("chat state: %w", err)
	}

	client, ok := c.(Client)
	if !ok {
		return nil, nil, fmt.Errorf("session %q: its client %T writes no app state", id, c)
	}

	device, err := svc.sessions.Device(id)
	if err != nil {
		return nil, nil, fmt.Errorf("chat state: %w", err)
	}

	key, err := device.AppStateKeys.GetLatestAppStateSyncKeyID(ctx)
	if err != nil {
		return nil, nil, fmt.Errorf("session %q: read the app state key: %w", id, err)
	}

	if key == nil {
		return nil, nil, fmt.Errorf("session %q has no app state key yet: %w", id, server.ErrNotConnected)
	}

	return client, online, nil
}

// write sends the patch. When WhatsApp rejected it because the session's copy of the collection is out of step, it
// syncs the collection again in full and sends it once more: rejected, it did not take effect, so its outcome is known
// (ARCHITECTURE §13 invariant 10 is about unknown ones). When WhatsApp took it and only the sync after it found the copy
// out of step, the write is done, on the first send as on the second; the collection is synced again all the same.
func (svc *Service) write(ctx context.Context, id string, client Client, online <-chan struct{}, patch appstate.PatchInfo) error {
	rec := svc.recovery(id) //nolint:contextcheck // the worker outlives the call: Close ends it, not the call's context
	if rec == nil {
		return svc.result(id, patch, client.SendAppState(ctx, patch))
	}

	for attempt := range 2 {
		seen := rec.seen(patch.Type)

		err := client.SendAppState(ctx, patch)
		if err == nil || !outOfStep(err) {
			return svc.result(id, patch, err)
		}

		if attempt == 1 && errors.Is(err, whatsmeow.ErrAppStateUpdate) {
			// Rejected again after a recovery: final.
			return fmt.Errorf("set the chat state: %w: %w", server.WithMetadata(server.ErrWhatsAppError, "code", conflict), whatsmeow.ErrAppStateUpdate)
		}

		recovered := svc.recoverAfter(ctx, id, rec, client, online, patch.Type, seen)

		if !errors.Is(err, whatsmeow.ErrAppStateUpdate) {
			// Sent and taken (appstate.go:636-638): only the sync after it failed.
			if recovered != nil {
				svc.log.Warn().Str("session_id", id).Str("collection", string(patch.Type)).Err(bare(recovered)).Msg("a chat state write was taken, but its collection is still out of step")
			}

			return nil
		}

		if recovered != nil {
			if ctx.Err() != nil {
				return fmt.Errorf("set the chat state: %w", context.Cause(ctx))
			}

			return fmt.Errorf("set the chat state: %w: %w", server.WithMetadata(server.ErrWhatsAppError, "code", conflict), whatsmeow.ErrAppStateUpdate)
		}
	}

	return nil
}

// result is what a send that needs no recovery returned, as the RPC's result. whatsmeow returns
// ErrAppStateRateLimited from a send only when WhatsApp took the write and the sync after it met the backoff a 429
// set: the write is done, and nothing is sent or synced again, since a full sync would meet the same backoff
// (appstate.go:48-50, 636-638).
func (svc *Service) result(id string, patch appstate.PatchInfo, err error) error {
	if errors.Is(err, whatsmeow.ErrAppStateRateLimited) {
		svc.log.Warn().Str("session_id", id).Str("collection", string(patch.Type)).Err(bare(err)).Msg("a chat state write was taken, but WhatsApp holds back the sync after it")

		return nil
	}

	return classify("set the chat state", err)
}

// outOfStep is an error of a session's copy of a collection that is out of step with WhatsApp's: one of the MACs that
// verify it does not match (appstate/errors.go). A missing key is not: a full sync cannot fix it, and whatsmeow asks
// the phone for it itself (appstate.go:173-175).
func outOfStep(err error) bool {
	return errors.Is(err, appstate.ErrMismatchingLTHash) || errors.Is(err, appstate.ErrMismatchingPatchMAC) ||
		errors.Is(err, appstate.ErrMismatchingContentMAC) || errors.Is(err, appstate.ErrMismatchingIndexMAC)
}

// classify gives what SendAppState returned its reason, nil for nil. A write that went out and got no answer may
// have taken effect: OUTCOME_UNKNOWN. A context error stays one.
//
// The error keeps whatsmeow's sentinel or a bare copy of its typed error, never the text whatsmeow wraps around it:
// a rejected write's text carries WhatsApp's nodes (appstate.go:612-614), and so does an IQ error's (errors.go:217-227).
// The status message reaches the application, and an INTERNAL one the error log (server.toStatus).
func classify(op string, err error) error {
	var (
		iqErr        *whatsmeow.IQError
		disconnected *whatsmeow.DisconnectedError
	)

	switch {
	case err == nil:
		return nil
	case errors.Is(err, context.Canceled):
		return fmt.Errorf("%s: %w", op, context.Canceled)
	case errors.Is(err, context.DeadlineExceeded):
		return fmt.Errorf("%s: %w", op, context.DeadlineExceeded)
	case errors.Is(err, whatsmeow.ErrAppStateUpdate) && outOfStep(err):
		// whatsmeow's own retry after a 409 found the copy out of step (appstate.go:616-623).
		return fmt.Errorf("%s: %w: %w", op, server.WithMetadata(server.ErrWhatsAppError, "code", conflict), whatsmeow.ErrAppStateUpdate)
	case errors.Is(err, whatsmeow.ErrAppStateUpdate):
		// WhatsApp's error code is only in the node's text (appstate.go:612-614), which is not kept.
		return fmt.Errorf("%s: %w: %w", op, server.ErrWhatsAppError, whatsmeow.ErrAppStateUpdate)
	case errors.As(err, &iqErr):
		bare := &whatsmeow.IQError{Code: iqErr.Code, Text: iqErr.Text}
		if iqErr.Code == rateOverLimit {
			return fmt.Errorf("%s: %w: %w", op, server.ErrRateLimited, bare)
		}

		return fmt.Errorf("%s: %w: %w", op, server.WithMetadata(server.ErrWhatsAppError, "code", fmt.Sprint(iqErr.Code)), bare)
	case errors.Is(err, whatsmeow.ErrNotConnected), errors.Is(err, socket.ErrSocketClosed):
		// The write did not go out (request.go:133-141, framesocket.go:128), or it was taken and the sync after it could
		// not go out: whatsmeow wraps that sync's error without a sentinel to tell the two apart (appstate.go:638).
		return fmt.Errorf("%s: %w: %w", op, server.ErrNotConnected, cause(err))
	case errors.Is(err, whatsmeow.ErrIQTimedOut):
		// Also a write taken whose sync after it got no answer (appstate.go:638).
		return fmt.Errorf("%s: %w: %w", op, server.ErrOutcomeUnknown, whatsmeow.ErrIQTimedOut)
	case errors.As(err, &disconnected):
		return fmt.Errorf("%s: %w: %w", op, server.ErrOutcomeUnknown, &whatsmeow.DisconnectedError{Action: disconnected.Action})
	default:
		return fmt.Errorf("%s: %w", op, cause(err))
	}
}

// bare is err without the text whatsmeow wrapped around it, for a log line: its sentinel when it has one, else the end
// of its chain.
func bare(err error) error {
	for _, sentinel := range []error{
		appstate.ErrMismatchingLTHash, appstate.ErrMismatchingPatchMAC, appstate.ErrMismatchingContentMAC, appstate.ErrMismatchingIndexMAC,
		whatsmeow.ErrIQTimedOut, whatsmeow.ErrNotConnected, context.Canceled, context.DeadlineExceeded,
	} {
		if errors.Is(err, sentinel) {
			return sentinel
		}
	}

	return cause(err)
}

// cause is the error at the end of err's chain: a store or socket error, or whatsmeow's own typed one, without the
// text whatsmeow wrapped around it. At the pin none of those an app state write ends with names a JID [inferred: read
// at the pin, appstate.go:557-647, request.go, store/sqlstore/store.go].
func cause(err error) error {
	for {
		next := errors.Unwrap(err)
		if next == nil {
			return err
		}

		err = next
	}
}

// build is the request's app state patch, as whatsmeow's appstate.Build functions make it.
func build(req *whatsmeowv1.SetChatStateRequest) (appstate.PatchInfo, error) {
	chat, err := types.ParseJID(req.GetChat())
	if err != nil || chat.User == "" || (chat.Server != types.DefaultUserServer && chat.Server != types.HiddenUserServer && chat.Server != types.GroupServer) {
		return appstate.PatchInfo{}, fmt.Errorf("chat: %w", server.InvalidArgument("chat", "not a user or group JID"))
	}

	chat = chat.ToNonAD()

	switch change := req.GetChange().(type) {
	case *whatsmeowv1.SetChatStateRequest_Mute:
		return mute(chat, change.Mute)
	case *whatsmeowv1.SetChatStateRequest_Pin:
		return appstate.BuildPin(chat, change.Pin.GetPinned()), nil
	case *whatsmeowv1.SetChatStateRequest_Archive:
		// No message range: whatsmeow then sends now as the last message's time (appstate/encode.go:361-375).
		return appstate.BuildArchive(chat, change.Archive.GetArchived(), time.Time{}, nil), nil
	case *whatsmeowv1.SetChatStateRequest_Read:
		return appstate.BuildMarkChatAsRead(chat, change.Read.GetRead(), time.Time{}, nil), nil
	case *whatsmeowv1.SetChatStateRequest_Star:
		return star(chat, change.Star)
	default:
		return appstate.PatchInfo{}, fmt.Errorf("change: %w", server.InvalidArgument("change", "mute, pin, archive, read or star"))
	}
}

// mute is a mute until a time, forever, or an unmute. The end is in milliseconds, as whatsmeow writes it
// (appstate/encode.go:43-69) and reads it (appstate.go:278-285) [inferred: what WhatsApp's other clients write is
// unmeasured; mautrix-whatsapp 5dd621f reads seconds, pkg/connector/handlewhatsapp.go:917].
func mute(chat types.JID, m *whatsmeowv1.ChatMute) (appstate.PatchInfo, error) {
	if !m.GetMuted() {
		return appstate.BuildMuteAbs(chat, false, nil), nil
	}

	if m.GetUntil() == nil {
		// -1, forever (appstate/encode.go:53-55).
		return appstate.BuildMuteAbs(chat, true, nil), nil
	}

	// An end of 0 or less reads as forever (appstate.go:280-284, mautrix's handlewhatsapp.go:915-917).
	end := m.GetUntil().AsTime().UnixMilli()
	if end <= 0 {
		return appstate.PatchInfo{}, fmt.Errorf("mute: %w", server.InvalidArgument("until", "at or before the Unix epoch"))
	}

	return appstate.BuildMuteAbs(chat, true, &end), nil
}

// star is a star or an unstar. The index names the sender only for a group message from someone else, and "0"
// otherwise: whatsmeow writes "0" when the sender is the chat's user (appstate/encode.go:246-253), and its Star event
// has a sender only in groups and not for the account's own messages (types/events/appstate.go:55) [inferred: what
// WhatsApp's other clients write is unmeasured].
func star(chat types.JID, s *whatsmeowv1.MessageStar) (appstate.PatchInfo, error) {
	if s.GetMessageId() == "" {
		return appstate.PatchInfo{}, fmt.Errorf("star: %w", server.InvalidArgument("message_id", "empty"))
	}

	sender := chat
	if chat.Server == types.GroupServer && !s.GetIsFromMe() {
		parsed, err := types.ParseJID(s.GetSender())
		if err != nil || parsed.User == "" || (parsed.Server != types.DefaultUserServer && parsed.Server != types.HiddenUserServer) {
			return appstate.PatchInfo{}, fmt.Errorf("star: %w", server.InvalidArgument("sender", "a group message from someone else needs its sender's user JID"))
		}

		sender = parsed.ToNonAD()
	}

	return appstate.BuildStar(chat, sender, s.GetMessageId(), s.GetIsFromMe(), s.GetStarred()), nil
}
