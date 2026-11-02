package session

import (
	"time"

	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// The states without fields, shared: a state is never modified once built.
func created() *whatsmeowv1.SessionState {
	return &whatsmeowv1.SessionState{Kind: &whatsmeowv1.SessionState_Created_{Created: &whatsmeowv1.SessionState_Created{}}}
}

func pairing() *whatsmeowv1.SessionState {
	return &whatsmeowv1.SessionState{Kind: &whatsmeowv1.SessionState_Pairing_{Pairing: &whatsmeowv1.SessionState_Pairing{}}}
}

func connecting() *whatsmeowv1.SessionState {
	return &whatsmeowv1.SessionState{Kind: &whatsmeowv1.SessionState_Connecting_{Connecting: &whatsmeowv1.SessionState_Connecting{}}}
}

func connected() *whatsmeowv1.SessionState {
	return &whatsmeowv1.SessionState{Kind: &whatsmeowv1.SessionState_Connected_{Connected: &whatsmeowv1.SessionState_Connected{}}}
}

func caughtUp() *whatsmeowv1.SessionState {
	return &whatsmeowv1.SessionState{Kind: &whatsmeowv1.SessionState_CaughtUp_{CaughtUp: &whatsmeowv1.SessionState_CaughtUp{}}}
}

func reconnecting() *whatsmeowv1.SessionState {
	return &whatsmeowv1.SessionState{Kind: &whatsmeowv1.SessionState_Reconnecting_{Reconnecting: &whatsmeowv1.SessionState_Reconnecting{}}}
}

func disconnected() *whatsmeowv1.SessionState {
	return &whatsmeowv1.SessionState{Kind: &whatsmeowv1.SessionState_Disconnected_{Disconnected: &whatsmeowv1.SessionState_Disconnected{}}}
}

func loggedOut(code int32) *whatsmeowv1.SessionState {
	return &whatsmeowv1.SessionState{Kind: &whatsmeowv1.SessionState_LoggedOut_{LoggedOut: &whatsmeowv1.SessionState_LoggedOut{Code: code}}}
}

func replaced() *whatsmeowv1.SessionState {
	return &whatsmeowv1.SessionState{Kind: &whatsmeowv1.SessionState_Replaced_{Replaced: &whatsmeowv1.SessionState_Replaced{}}}
}

func tempBanned(code int32, expire *timestamppb.Timestamp) *whatsmeowv1.SessionState {
	return &whatsmeowv1.SessionState{Kind: &whatsmeowv1.SessionState_TempBanned_{TempBanned: &whatsmeowv1.SessionState_TempBanned{Code: code, ExpireTime: expire}}}
}

func outdated() *whatsmeowv1.SessionState {
	return &whatsmeowv1.SessionState{Kind: &whatsmeowv1.SessionState_Outdated_{Outdated: &whatsmeowv1.SessionState_Outdated{}}}
}

func failed(code int32, message string) *whatsmeowv1.SessionState {
	return &whatsmeowv1.SessionState{Kind: &whatsmeowv1.SessionState_Error_{Error: &whatsmeowv1.SessionState_Error{Code: code, Message: message}}}
}

// live is a state with a connection open or being opened: Connecting, Connected, CaughtUp or Reconnecting.
func live(state *whatsmeowv1.SessionState) bool {
	switch state.GetKind().(type) {
	case *whatsmeowv1.SessionState_Connecting_, *whatsmeowv1.SessionState_Connected_, *whatsmeowv1.SessionState_CaughtUp_,
		*whatsmeowv1.SessionState_Reconnecting_:
		return true
	default:
		return false
	}
}

// loggingIn is a state whose login may still dispatch Connected: Connecting, Reconnecting or Pairing.
func loggingIn(state *whatsmeowv1.SessionState) bool {
	switch state.GetKind().(type) {
	case *whatsmeowv1.SessionState_Connecting_, *whatsmeowv1.SessionState_Reconnecting_, *whatsmeowv1.SessionState_Pairing_:
		return true
	default:
		return false
	}
}

// kept is a state that a restart restores as it is: the ones that stop a session (session.proto Disconnected).
// Every other paired state comes back Disconnected.
func kept(state *whatsmeowv1.SessionState) bool {
	switch state.GetKind().(type) {
	case *whatsmeowv1.SessionState_LoggedOut_, *whatsmeowv1.SessionState_Replaced_, *whatsmeowv1.SessionState_Outdated_,
		*whatsmeowv1.SessionState_TempBanned_, *whatsmeowv1.SessionState_Error_:
		return true
	default:
		return false
	}
}

// next maps a whatsmeow event to the state it moves the session to from current (ARCHITECTURE §7.4), or false when
// it moves nothing: an event that is not about the connection, or one that arrives late, after the session left
// the states it applies to (whatsmeow dispatches connection events on their own goroutines, client.go:552, 621).
// now is when the event arrived.
func next(current *whatsmeowv1.SessionState, evt any, now time.Time) (*whatsmeowv1.SessionState, bool) {
	_, isLoggedOut := current.GetKind().(*whatsmeowv1.SessionState_LoggedOut_)

	switch e := evt.(type) {
	case *events.Connected:
		return connected(), is[*whatsmeowv1.SessionState_Connecting_](current) || is[*whatsmeowv1.SessionState_Reconnecting_](current)
	case *events.OfflineSyncCompleted:
		return caughtUp(), is[*whatsmeowv1.SessionState_Connected_](current)
	case *events.Disconnected:
		// whatsmeow reconnects on its own: the sidecar never sets an AutoReconnectHook that stops it (ADR 0013).
		return reconnecting(), live(current) && !is[*whatsmeowv1.SessionState_Reconnecting_](current)
	case *events.PairSuccess:
		// No Pair call is open (#7): the device is linked and not connected.
		return disconnected(), is[*whatsmeowv1.SessionState_Created_](current) || isLoggedOut
	case *events.LoggedOut:
		// A stream-error logout carries ConnectFailureLoggedOut, 401 (connectionevents.go:40).
		return loggedOut(int32(e.Reason)), true //nolint:gosec // G115: WhatsApp's failure codes are three digits
	case *events.StreamReplaced:
		return replaced(), !isLoggedOut
	case *events.ClientOutdated:
		return outdated(), !isLoggedOut
	case *events.CATRefreshError:
		return failed(0, e.Error.Error()), !isLoggedOut
	case *events.TemporaryBan:
		var expire *timestamppb.Timestamp
		if e.Expire > 0 {
			// WhatsApp sends whole seconds; the end is kept to the second, so its RFC 3339 form is exact.
			expire = timestamppb.New(now.Truncate(time.Second).Add(e.Expire))
		}

		return tempBanned(int32(e.Code), expire), !isLoggedOut //nolint:gosec // G115: WhatsApp's ban codes are three digits
	case *events.ConnectFailure:
		return failed(int32(e.Reason), e.Message), !isLoggedOut //nolint:gosec // G115: WhatsApp's failure codes are three digits
	default:
		return nil, false
	}
}

func is[T any](state *whatsmeowv1.SessionState) bool {
	_, ok := state.GetKind().(T)

	return ok
}
