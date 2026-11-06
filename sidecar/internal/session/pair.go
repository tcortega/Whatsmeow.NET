package session

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"regexp"
	"strings"
	"time"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/grpc"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// Pairing (ARCHITECTURE §9, ADR 0021). A Pair call gives the session a new device and client, opens whatsmeow's QR
// channel and connects, then streams what the channel sends until the phone links the device. whatsmeow then logs
// in again after WhatsApp's 515 (connectionevents.go:26-38), uploads the pre-keys and only then dispatches Connected
// (connectionevents.go:187-204), so at Connected PreKeysUploaded already says whether the device is usable.

const (
	// linkTimeout bounds the wait for that login: a failed 515 reconnect is only logged, with no event and no retry
	// (connectionevents.go:33-38). mautrix-whatsapp waits 10 s for the 515 and 5 s for the pre-keys (login.go:398,
	// 432); this bound is wider, and the live test measures the real time.
	linkTimeout = 60 * time.Second

	// pairClientType and pairClientName are what PairPhone tells WhatsApp the companion is. WhatsApp validates the
	// name, "Browser (OS)" (pair-code.go:87-89); these are mautrix-whatsapp's (login.go:223).
	pairClientType = whatsmeow.PairClientChrome
	pairClientName = "Chrome (Linux)"

	// rateOverLimit is the code of whatsmeow's ErrIQRateOverLimit (errors.go:198). Its Is also compares the text
	// (errors.go:230-236), so the code alone is matched.
	rateOverLimit = 429
)

// notDigits is whatsmeow's own phone number cleanup (pair-code.go:49, 97).
var notDigits = regexp.MustCompile(`[^0-9]`)

// attempt is one Pair call's progress. The session's mu guards it.
type attempt struct {
	// previous is the state before Pair, restored when pairing ends before the link.
	previous *whatsmeowv1.SessionState
	// linked: whatsmeow dispatched PairSuccess; the device is saved (pair.go:222-226, 157).
	linked bool
	// dropped: the connection dropped before the link, which whatsmeow's QR channel reports as a timeout
	// (qrchan.go:196-197).
	dropped bool
	// stopped is closed, with stopReason, when WhatsApp ended the connection before the link.
	stopped    chan struct{}
	stopReason string
	// loggedIn is closed when the login after the link ended: with Connected, or with a stopping state.
	loggedIn chan struct{}
	ended    bool
	// usable: the login came with the pre-keys uploaded, and the session is Connected.
	usable bool
	// disconnected: Disconnect ended the wait for the login.
	disconnected bool
}

func (a *attempt) end() {
	if !a.ended {
		a.ended = true
		close(a.loggedIn)
	}
}

// whilePairing applies the events that mean something else while a Pair call is open, and reports whether it did.
// Once the login after the link ended, the session's state machine has them again, even before the Pair call closed
// the attempt. s.mu is held.
func (svc *Service) whilePairing(s *session, a *attempt, evt any) bool {
	if a.ended {
		return false
	}

	switch e := evt.(type) {
	case *events.PairSuccess:
		s.jid, s.lid = e.ID.String(), ""
		if !e.LID.IsEmpty() {
			s.lid = e.LID.String()
		}

		a.linked = true

		// A restart now finds a paired session: Disconnected, not Created. The device is saved already
		// (pair.go:222-226), so this is recorded during a shutdown too.
		if err := svc.w.Tx(svc.recording, func(ctx context.Context, tx *sql.Tx) error { return svc.persist(ctx, tx, s) }); err != nil {
			svc.log.Error().Err(err).Str("session_id", s.id).Msg("recording the linked device failed")
		}
	case *events.Disconnected:
		// After the link, whatsmeow reconnects a paired client on its own (client.go:614-624, 650).
		a.dropped = a.dropped || !a.linked
	case *events.Connected:
		if !a.linked {
			return true
		}

		if uploadedPreKeys(s.client) {
			a.usable = true
			if err := svc.loggedIn(s); err != nil {
				svc.log.Error().Err(err).Str("session_id", s.id).Msg("recording Connected after pairing failed")
			}
		}

		a.end()
	case events.PermanentDisconnect:
		// After the link, the stopping state is the session's (ARCHITECTURE §13 invariant 4). Before it there is no
		// account to stop: the pairing ends with PAIRING_FAILED and the session goes back to its previous state. The
		// QR channel ends on some of these (qrchan.go:187-199), not on StreamReplaced or CATRefreshError, so the
		// pairing does not wait for it.
		if a.linked {
			svc.apply(s, evt)
			a.end()

			return true
		}

		if a.stopReason == "" {
			a.stopReason = fmt.Sprintf("%T", evt)
			close(a.stopped)
		}
	default:
		return false
	}

	return true
}

// stopLogin ends a linked pairing's wait for the login, for Disconnect, and reports whether there was one: also a
// login that ended without the pre-keys, which the wait has not disconnected yet. s.mu is held.
func stopLogin(s *session) bool {
	a := s.pairing
	if a == nil || !a.linked {
		return false
	}

	a.disconnected = true
	a.end()
	s.pairing = nil

	return true
}

// uploadedPreKeys reports whether the client's first pre-key upload happened. whatsmeow keeps that in a field,
// PreKeysUploaded (client.go:117, prekeys.go:95); the fake WhatsApp and the tests' stub have a method.
func uploadedPreKeys(client Client) bool {
	switch c := client.(type) {
	case *whatsmeow.Client:
		return c.PreKeysUploaded.IsSet()
	case interface{ HasUploadedPreKeys() bool }:
		return c.HasUploadedPreKeys()
	default:
		return false
	}
}

// Pair links a session in state Created or LoggedOut to an account (session.proto).
func (svc *Service) Pair(req *whatsmeowv1.PairRequest, stream grpc.ServerStreamingServer[whatsmeowv1.PairResponse]) error {
	s, err := svc.get(req.GetSessionId())
	if err != nil {
		return err
	}

	phone := ""
	if req.GetPhone() != nil {
		if phone, err = phoneDigits(req.GetPhone().GetPhoneNumber()); err != nil {
			return fmt.Errorf("pair session %q: %w", s.id, err)
		}
	}

	ctx := stream.Context()

	a, client, qr, err := svc.startPairing(ctx, s)
	if err != nil {
		return err
	}

	if err := svc.showCodes(ctx, s, a, client, qr, phone, stream); err != nil && !svc.unpair(ctx, s, a) {
		return err
	}

	// Linked. A cancel from now on never disconnects: the login goes on without the call (session.proto).
	result := make(chan error, 1)

	if !svc.spawn(func() { result <- svc.awaitLogin(s, a) }) {
		return fmt.Errorf("pair session %q: the sidecar is stopping: %w", s.id, svc.lifetime.Err())
	}

	select {
	case <-ctx.Done():
		return fmt.Errorf("pair session %q: %w", s.id, ctx.Err())
	case err := <-result:
		if err != nil {
			return err
		}
	}

	s.mu.Lock()
	success := &whatsmeowv1.PairResponse_Success{Jid: s.jid, Lid: s.lid}
	s.mu.Unlock()

	if err := stream.Send(&whatsmeowv1.PairResponse{Step: &whatsmeowv1.PairResponse_Success_{Success: success}}); err != nil {
		return fmt.Errorf("pair session %q: %w", s.id, err)
	}

	return nil
}

// phoneDigits checks a phone number as whatsmeow's PairPhone does (pair-code.go:97-102), before anything connects.
// The number is never in the message: it is the account's.
func phoneDigits(number string) (string, error) {
	digits := notDigits.ReplaceAllString(number, "")

	switch {
	case len(digits) <= 6:
		return "", fmt.Errorf("phone number: %w", server.InvalidArgument("phone.phone_number", "it is too short"))
	case strings.HasPrefix(digits, "0"):
		return "", fmt.Errorf("phone number: %w", server.InvalidArgument("phone.phone_number", "it must be in international format, not start with 0"))
	}

	return digits, nil
}

// startPairing checks the state, gives the session a new device and client, records Pairing, then opens whatsmeow's
// QR channel and connects. Each Pair call gets a new client, as mautrix-whatsapp's logins do (login.go:157-158): a
// logged-out device is deleted (store.go:286-299), and an old client's QR channel would still react to events.
func (svc *Service) startPairing(ctx context.Context, s *session) (*attempt, Client, <-chan whatsmeow.QRChannelItem, error) {
	s.ops.Lock()
	defer s.ops.Unlock()

	s.mu.Lock()

	// Deleted after Pair found it: nobody would end what this connects.
	if s.gone() {
		s.mu.Unlock()

		return nil, nil, nil, fmt.Errorf("pair session %q: %w", s.id, server.ErrSessionNotFound)
	}

	previous := s.state
	switch previous.GetKind().(type) {
	case *whatsmeowv1.SessionState_Created_, *whatsmeowv1.SessionState_LoggedOut_:
	case *whatsmeowv1.SessionState_Pairing_:
		s.mu.Unlock()

		return nil, nil, nil, fmt.Errorf("pair session %q: %w", s.id, server.ErrPairingInProgress)
	default:
		s.mu.Unlock()

		return nil, nil, nil, fmt.Errorf("pair session %q: %w", s.id, server.ErrAlreadyPaired)
	}

	svc.attach(s, svc.devices.NewDevice())

	a := &attempt{previous: previous, stopped: make(chan struct{}), loggedIn: make(chan struct{})}
	s.pairing = a
	client := s.client

	if err := svc.transition(ctx, s, pairing()); err != nil {
		s.pairing = nil
		if restoreErr := svc.transition(ctx, s, previous); restoreErr != nil {
			err = errors.Join(err, restoreErr)
		}

		s.mu.Unlock()

		return nil, nil, nil, err
	}
	s.mu.Unlock()

	// The sidecar's lifetime, never the call's: whatsmeow's QR channel disconnects when its context ends
	// (qrchan.go:125-131), which would race the 515 reconnect after a link, and the connection outlives the call
	// (client.go:543-601).
	qr, err := client.GetQRChannel(svc.lifetime) //nolint:contextcheck // see above
	if err == nil {
		err = client.ConnectContext(svc.lifetime) //nolint:contextcheck // see above
		if err != nil {
			err = fmt.Errorf("%w: %w", server.ErrConnectionFailed, err)
		}
	}

	if err != nil {
		s.mu.Lock()
		s.pairing = nil
		if restoreErr := svc.transition(ctx, s, previous); restoreErr != nil {
			err = errors.Join(err, restoreErr)
		}
		s.mu.Unlock()

		client.Disconnect()

		return nil, nil, nil, fmt.Errorf("pair session %q: %w", s.id, err)
	}

	return a, client, qr, nil
}

// showCodes streams what the QR channel sends until the phone links the device (nil) or pairing ends (an error).
// In phone mode, the first QR code is the cue for PairPhone (pair-code.go:78-81) and no code is streamed; the QR
// channel goes on rotating, and the phone code stays usable until it runs out (pair-code.go:83-85).
func (svc *Service) showCodes(ctx context.Context, s *session, a *attempt, client Client, qr <-chan whatsmeow.QRChannelItem, phone string,
	stream grpc.ServerStreamingServer[whatsmeowv1.PairResponse],
) error {
	codeSent := false

	for {
		var item whatsmeow.QRChannelItem

		select {
		case <-ctx.Done():
			return fmt.Errorf("pair session %q: %w", s.id, ctx.Err())
		case <-s.deleted:
			return fmt.Errorf("pair session %q: %w", s.id, server.ErrSessionNotFound)
		case <-a.stopped:
			return fmt.Errorf("pair session %q: WhatsApp ended the connection (%s): %w", s.id, a.stopReason, server.ErrPairingFailed)
		case next, open := <-qr:
			if !open {
				return fmt.Errorf("pair session %q: the QR channel closed: %w", s.id, server.ErrPairingFailed)
			}

			item = next
		}

		var step *whatsmeowv1.PairResponse

		switch {
		case item.Event == whatsmeow.QRChannelEventCode && phone == "":
			step = &whatsmeowv1.PairResponse{Step: &whatsmeowv1.PairResponse_QrCode_{QrCode: &whatsmeowv1.PairResponse_QrCode{Code: item.Code}}}
		case item.Event == whatsmeow.QRChannelEventCode && codeSent:
			continue
		case item.Event == whatsmeow.QRChannelEventCode:
			code, err := client.PairPhone(ctx, phone, true, pairClientType, pairClientName)
			if err != nil {
				return fmt.Errorf("pair session %q by phone: %w", s.id, pairPhoneError(err))
			}

			codeSent = true
			step = &whatsmeowv1.PairResponse{Step: &whatsmeowv1.PairResponse_PairingCode_{PairingCode: &whatsmeowv1.PairResponse_PairingCode{Code: code}}}
		case item.Event == whatsmeow.QRChannelSuccess.Event:
			return nil
		case item.Event == whatsmeow.QRChannelTimeout.Event:
			s.mu.Lock()
			dropped := a.dropped
			s.mu.Unlock()

			if dropped {
				return fmt.Errorf("pair session %q: the connection dropped: %w", s.id, server.ErrConnectionFailed)
			}

			return fmt.Errorf("pair session %q: %w", s.id, server.ErrPairingTimedOut)
		default:
			// Passkey pairing included: how often WhatsApp asks for it is unknown (ADR 0021); the live test measures it.
			svc.log.Warn().Err(item.Error).Str("session_id", s.id).Str("qr_event", item.Event).Msg("pairing ended by the QR channel")

			return fmt.Errorf("pair session %q: QR channel %q: %w", s.id, item.Event, errors.Join(server.ErrPairingFailed, item.Error))
		}

		if err := stream.Send(step); err != nil {
			return fmt.Errorf("pair session %q: %w", s.id, err)
		}
	}
}

// pairPhoneError classifies what PairPhone's request returned (pair-code.go:97-127).
func pairPhoneError(err error) error {
	var iqErr *whatsmeow.IQError

	switch {
	case errors.Is(err, context.Canceled), errors.Is(err, context.DeadlineExceeded):
		return err
	case errors.Is(err, whatsmeow.ErrPhoneNumberTooShort), errors.Is(err, whatsmeow.ErrPhoneNumberIsNotInternational):
		return fmt.Errorf("%w: %w", server.InvalidArgument("phone.phone_number", "WhatsApp refused the phone number"), err)
	case errors.As(err, &iqErr) && iqErr.Code == rateOverLimit:
		return fmt.Errorf("%w: %w", server.ErrRateLimited, err)
	case errors.As(err, &iqErr):
		return fmt.Errorf("%w: %w", server.WithMetadata(server.ErrWhatsAppError, "code", fmt.Sprint(iqErr.Code)), err)
	default:
		return fmt.Errorf("%w: %w", server.ErrPairingFailed, err)
	}
}

// unpair ends a pairing before the link: the previous state is recorded first, so a PairSuccess that still comes
// records the device it saved (state.go next), then the client is disconnected. It reports true, doing nothing, when
// the device was linked meanwhile.
func (svc *Service) unpair(ctx context.Context, s *session, a *attempt) bool {
	s.ops.Lock()
	defer s.ops.Unlock()

	s.mu.Lock()

	if a.linked {
		s.mu.Unlock()

		return true
	}

	client := s.client

	if s.gone() {
		s.mu.Unlock()
		// Delete disconnected the client already; again costs nothing, and holds if that ever changes.
		client.Disconnect()

		return false
	}

	if s.pairing != a {
		s.mu.Unlock()

		return false
	}

	s.pairing = nil
	if err := svc.transition(ctx, s, a.previous); err != nil {
		svc.log.Error().Err(err).Str("session_id", s.id).Msg("recording the state before pairing failed")
	}

	s.mu.Unlock()

	client.Disconnect()

	return false
}

// awaitLogin waits for whatsmeow's login after the link, at most linkTimeout. A login without the pre-keys, or none
// in time, leaves the session paired and Disconnected; a stopping state stays the session's.
func (svc *Service) awaitLogin(s *session, a *attempt) error {
	timer := time.NewTimer(svc.linkTimeout)
	defer timer.Stop()

	select {
	case <-svc.lifetime.Done():
		// The sidecar stops: a restart finds the linked session Disconnected.
		return fmt.Errorf("pair session %q: %w", s.id, svc.lifetime.Err())
	case <-s.deleted:
		return fmt.Errorf("pair session %q: %w", s.id, server.ErrSessionNotFound)
	case <-a.loggedIn:
	case <-timer.C:
	}

	s.ops.Lock()
	defer s.ops.Unlock()

	s.mu.Lock()
	defer s.mu.Unlock()

	if s.gone() {
		return fmt.Errorf("pair session %q: %w", s.id, server.ErrSessionNotFound)
	}

	timedOut := !a.ended
	a.end()

	if a.disconnected {
		return fmt.Errorf("pair session %q: the session was disconnected before the login: %w", s.id, server.ErrPairingFailed)
	}

	s.pairing = nil

	switch {
	case a.usable:
		return nil
	case !timedOut && !is[*whatsmeowv1.SessionState_Pairing_](s.state):
		return fmt.Errorf("pair session %q: WhatsApp ended the login: %w", s.id, server.ErrPairingFailed)
	}

	// whatsmeow's Disconnect waits for the event handlers (client.go:728-747), which take s.mu.
	client := s.client
	s.mu.Unlock()
	client.Disconnect()
	s.mu.Lock()

	if err := svc.transition(svc.lifetime, s, disconnected()); err != nil {
		svc.log.Error().Err(err).Str("session_id", s.id).Msg("recording Disconnected after pairing failed")
	}

	if timedOut {
		return fmt.Errorf("pair session %q: no login within %s of the link: %w", s.id, svc.linkTimeout, server.ErrPairingFailed)
	}

	return fmt.Errorf("pair session %q: the login after the link uploaded no pre-keys: %w", s.id, server.ErrPairingFailed)
}
