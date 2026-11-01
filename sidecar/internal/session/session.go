// Package session is the Sessions slice: sessions, their connection, and their state machine (ARCHITECTURE §7.4).
//
// Every state change goes through transition, the events whatsmeow dispatches and the ones the sidecar synthesizes
// for Connect, Disconnect, Logout and a restart alike, so each one is recorded in the outbox as a
// ConnectionStateEvent, in order with the session's other events.
package session

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"slices"
	"sync"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/store/sqlstore"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/grpc"
	"google.golang.org/protobuf/proto"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/outbox"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// Client is what the slice needs of a whatsmeow client. *whatsmeow.Client and the fake WhatsApp's client both
// satisfy it (ADR 0019).
type Client interface {
	AddEventHandlerWithSuccessStatus(handler whatsmeow.EventHandlerWithSuccessStatus) uint32
	ConnectContext(ctx context.Context) error
	Disconnect()
	IsConnected() bool
	Logout(ctx context.Context) error
	GetQRChannel(ctx context.Context) (<-chan whatsmeow.QRChannelItem, error)
	PairPhone(ctx context.Context, phone string, showPushNotification bool, clientType whatsmeow.PairClientType, clientDisplayName string) (string, error)
}

var _ Client = (*whatsmeow.Client)(nil)

// NewClient returns the client of one session's device.
type NewClient func(sessionID string, device *store.Device) Client

// Service implements whatsmeowv1.SessionServiceServer.
type Service struct {
	whatsmeowv1.UnimplementedSessionServiceServer

	db        *sql.DB
	devices   *sqlstore.Container
	box       *outbox.Outbox
	newClient NewClient
	log       zerolog.Logger
	// lifetime scopes every connection: whatsmeow ties keepalive, the node queue and reconnects to the context
	// ConnectContext gets (client.go:541-599), so it is the sidecar's, never a call's.
	lifetime context.Context //nolint:containedctx // the sidecar's lifetime, not a request's
	end      context.CancelFunc
	// recording is the lifetime without its end, for what an event handler records: a state that happened is
	// recorded during a shutdown too.
	recording context.Context //nolint:containedctx // derived from the lifetime, not a request's
	// linkTimeout bounds the wait for whatsmeow's login after the phone linked a device (pair.go).
	linkTimeout time.Duration

	mu       sync.Mutex
	sessions map[string]*session

	// background runs the goroutines the Service owns, the waits for a login after a link (pair.go) and the
	// disconnects of retired clients; Close waits for them. spawning guards closed, so none starts once Close waits.
	background sync.WaitGroup
	spawning   sync.Mutex
	closed     bool
}

// session is one session. ops serializes the calls that drive its connection; mu guards the rest and is never
// held while calling into whatsmeow, whose Disconnect waits for the event handlers (client.go:726-745). device and
// client change only under both, when a Pair call starts.
type session struct {
	id     string
	device *store.Device
	client Client
	ops    sync.Mutex

	mu       sync.Mutex
	state    *whatsmeowv1.SessionState
	jid, lid string
	watchers map[chan *whatsmeowv1.SessionState]struct{}
	deleted  chan struct{}
	// pairing is the open Pair call's attempt, nil when none is open.
	pairing *attempt
}

// New loads every session from the database. It never connects one: the client calls Connect (session.proto).
func New(ctx context.Context, db *sql.DB, devices *sqlstore.Container, box *outbox.Outbox, newClient NewClient, log zerolog.Logger) (*Service, error) {
	lifetime, end := context.WithCancel(context.Background())
	svc := &Service{
		db: db, devices: devices, box: box, newClient: newClient, log: log, lifetime: lifetime, end: end, recording: context.WithoutCancel(lifetime),
		linkTimeout: linkTimeout,
		sessions:    map[string]*session{},
	}

	if err := svc.load(ctx); err != nil {
		end()

		return nil, err
	}

	return svc, nil
}

func (svc *Service) load(ctx context.Context) error {
	rows, err := svc.db.QueryContext(ctx, "SELECT id, jid, lid, stop_state FROM session")
	if err != nil {
		return fmt.Errorf("load the sessions: %w", err)
	}
	defer func() { _ = rows.Close() }()

	for rows.Next() {
		var (
			id       string
			jid, lid sql.NullString
			stopped  []byte
		)

		if err := rows.Scan(&id, &jid, &lid, &stopped); err != nil {
			return fmt.Errorf("load the sessions: %w", err)
		}

		if err := svc.restore(ctx, id, jid.String, lid.String, stopped); err != nil {
			return err
		}
	}

	if err := rows.Err(); err != nil {
		return fmt.Errorf("load the sessions: %w", err)
	}

	return nil
}

// restore rebuilds one session as a restart leaves it (session.proto Disconnected): never paired is Created, a
// stopping state comes back as it was, and any other paired one is Disconnected.
func (svc *Service) restore(ctx context.Context, id, jid, lid string, stopped []byte) error {
	state := created()

	if stopped != nil {
		state = &whatsmeowv1.SessionState{}
		if err := proto.Unmarshal(stopped, state); err != nil {
			return fmt.Errorf("load session %q: its state: %w", id, err)
		}
	}

	device := svc.devices.NewDevice()
	// changed is a paired session the restart moves: a live one comes back Disconnected, one without its device
	// LoggedOut. A never-paired one and a stopped one come back as they were.
	changed := false

	if jid != "" {
		paired, err := svc.pairedDevice(ctx, jid)
		if err != nil {
			return fmt.Errorf("load session %q: %w", id, err)
		}

		if stopped == nil {
			state, changed = disconnected(), true
		}

		device = paired

		// whatsmeow deletes a logged-out device's rows on its own goroutine (connectionevents.go:37-44), so a crash can
		// leave the session's jid behind without them.
		if paired == nil {
			svc.log.Warn().Str("session_id", id).Msg("the session's device is gone from the store; it is logged out")

			device, state, jid, lid, changed = svc.devices.NewDevice(), loggedOut(0), "", "", true
		}
	}

	s := svc.add(id, device, state, jid, lid)

	svc.mu.Lock()
	svc.sessions[id] = s
	svc.mu.Unlock()

	if !changed {
		return nil
	}

	// The restart moved the session: a transition like any other, recorded so the next start finds it as it is and
	// a consumer of its events sees it (ARCHITECTURE §7.4).
	s.mu.Lock()
	defer s.mu.Unlock()

	return svc.transition(ctx, s, state)
}

func (svc *Service) pairedDevice(ctx context.Context, jid string) (*store.Device, error) {
	parsed, err := types.ParseJID(jid)
	if err != nil {
		return nil, fmt.Errorf("its device JID %q: %w", jid, err)
	}

	device, err := svc.devices.GetDevice(ctx, parsed)
	if err != nil {
		return nil, fmt.Errorf("its device %s: %w", jid, err)
	}

	return device, nil
}

// add creates the session's client and registers its event handler; the caller puts it in svc.sessions.
func (svc *Service) add(id string, device *store.Device, state *whatsmeowv1.SessionState, jid, lid string) *session {
	s := &session{
		id: id, state: state, jid: jid, lid: lid,
		watchers: map[chan *whatsmeowv1.SessionState]struct{}{}, deleted: make(chan struct{}),
	}
	svc.attach(s, device)

	return s
}

// attach gives the session device and a new client for it, whose events the session handles until the next attach.
// A new session is not shared yet; otherwise s.ops and s.mu are held.
func (svc *Service) attach(s *session, device *store.Device) {
	client := svc.newClient(s.id, device)
	client.AddEventHandlerWithSuccessStatus(func(evt any) bool {
		svc.handle(s, client, evt)

		// Connection events are not stanzas: whatsmeow acks nothing on their result.
		return true
	})

	s.device, s.client = device, client
}

// handle applies a whatsmeow event. A server-side LoggedOut only records the transition: whatsmeow deletes the
// device rows itself, concurrently, so the handler never reads, writes or deletes them (ARCHITECTURE §7.4).
func (svc *Service) handle(s *session, client Client, evt any) {
	s.mu.Lock()

	// A client of a deleted session, or one a later Pair call replaced, is about nothing. If it dispatches, it is
	// alive again: whatsmeow reconnects on its own after WhatsApp's 515 (connectionevents.go:33-36), which Delete
	// cannot stop, because deleting by the recorded JID leaves the client's Store.Deleted unset (client.go:567).
	if s.gone() || client != s.client {
		s.mu.Unlock()
		svc.retire(client)

		return
	}
	defer s.mu.Unlock()

	if p := s.pairing; p != nil && svc.whilePairing(s, p, evt) {
		return
	}

	svc.apply(s, evt)
}

// apply moves the session to the state evt maps to, if any. s.mu is held.
func (svc *Service) apply(s *session, evt any) {
	state, ok := next(s.state, evt, time.Now())
	if !ok {
		// A login the session did not ask for: whatsmeow's 515 reconnect ignores a Disconnect that came before it
		// (connectionevents.go:33-36), and the device would be online under a state that says it is not.
		if _, login := evt.(*events.Connected); login && !live(s.state) && !is[*whatsmeowv1.SessionState_Pairing_](s.state) {
			svc.retire(s.client)
		}

		return
	}

	switch e := evt.(type) {
	case *events.PairSuccess:
		s.jid, s.lid = e.ID.String(), ""
		if !e.LID.IsEmpty() {
			s.lid = e.LID.String()
		}
	case *events.LoggedOut:
		s.jid, s.lid = "", ""
	}

	if err := svc.transition(svc.lifetime, s, state); err != nil {
		svc.log.Error().Err(err).Str("session_id", s.id).Msgf("recording the state after a %T failed", evt)
	}
}

// transition is the one place a session's state changes: in one transaction it records the state a restart keeps
// (the stopping states) and a ConnectionStateEvent in the outbox, then publishes the state to every watcher. s.mu is
// held. The state is published even when recording fails, because it is what whatsmeow reports. A state that
// happened is recorded even when the call that caused it was cancelled meanwhile.
func (svc *Service) transition(ctx context.Context, s *session, state *whatsmeowv1.SessionState) error {
	s.state = state
	ctx = context.WithoutCancel(ctx)
	event := &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_ConnectionState{
		ConnectionState: &whatsmeowv1.ConnectionStateEvent{State: state},
	}}
	_, err := svc.box.Record(ctx, s.id, "", event, func(tx *sql.Tx) error { return svc.persist(ctx, tx, s) })
	if err != nil {
		err = fmt.Errorf("record the state of session %q: %w", s.id, err)
	}

	for w := range s.watchers {
		// One slot per watcher: a watcher that falls behind finds only the latest state.
		select {
		case <-w:
		default:
		}

		w <- state
	}

	return err
}

// execer is the database or a transaction.
type execer interface {
	ExecContext(ctx context.Context, query string, args ...any) (sql.Result, error)
}

// persist writes what a restart needs: the device's JIDs and a stopping state. s.mu is held.
func (svc *Service) persist(ctx context.Context, db execer, s *session) error {
	var stopped []byte

	if kept(s.state) {
		var err error
		if stopped, err = proto.Marshal(s.state); err != nil {
			return fmt.Errorf("record session %q: %w", s.id, err)
		}
	}

	if _, err := db.ExecContext(ctx, "UPDATE session SET jid = ?, lid = ?, stop_state = ? WHERE id = ?",
		nullable(s.jid), nullable(s.lid), stopped, s.id); err != nil {
		return fmt.Errorf("record session %q: %w", s.id, err)
	}

	return nil
}

func nullable(s string) sql.NullString { return sql.NullString{String: s, Valid: s != ""} }

func (s *session) gone() bool {
	select {
	case <-s.deleted:
		return true
	default:
		return false
	}
}

// retire disconnects a client the Service no longer uses. Not on the handler's goroutine: whatsmeow's Disconnect
// waits for the event handlers (client.go:726-745).
func (svc *Service) retire(client Client) {
	svc.spawn(client.Disconnect)
}

// spawn runs f on a goroutine Close waits for, and reports whether it did: once Close began, it runs nothing.
func (svc *Service) spawn(f func()) bool {
	svc.spawning.Lock()
	defer svc.spawning.Unlock()

	if svc.closed {
		return false
	}

	svc.background.Go(f)

	return true
}

// Close ends the connections' context, which ends a connect in flight, then disconnects every session, before the
// store closes.
func (svc *Service) Close() {
	svc.end()

	svc.spawning.Lock()
	svc.closed = true
	svc.spawning.Unlock()

	svc.mu.Lock()
	all := make([]*session, 0, len(svc.sessions))

	for _, s := range svc.sessions {
		all = append(all, s)
	}
	svc.mu.Unlock()

	for _, s := range all {
		s.ops.Lock()
		s.client.Disconnect()
		s.ops.Unlock()
	}

	// The waits for a login end with the lifetime; they and the retired clients' disconnects must not outlive the
	// store.
	svc.background.Wait()
}

// get returns the session with id, or an error for a malformed or unknown id.
func (svc *Service) get(id string) (*session, error) {
	if err := validate(id); err != nil {
		return nil, err
	}

	svc.mu.Lock()
	s, ok := svc.sessions[id]
	svc.mu.Unlock()

	if !ok {
		return nil, fmt.Errorf("session %q: %w", id, server.ErrSessionNotFound)
	}

	return s, nil
}

func validate(id string) error {
	if err := server.CheckID("session_id", id); err != nil {
		return fmt.Errorf("session id: %w", err)
	}

	return nil
}

func (s *session) proto() *whatsmeowv1.Session {
	s.mu.Lock()
	defer s.mu.Unlock()

	return &whatsmeowv1.Session{Id: s.id, State: s.state, Jid: s.jid, Lid: s.lid}
}

// CreateSession creates an unpaired session, or returns the existing one.
func (svc *Service) CreateSession(ctx context.Context, req *whatsmeowv1.CreateSessionRequest) (*whatsmeowv1.CreateSessionResponse, error) {
	id := req.GetSessionId()
	if err := validate(id); err != nil {
		return nil, err
	}

	svc.mu.Lock()
	defer svc.mu.Unlock()

	if s, ok := svc.sessions[id]; ok {
		return &whatsmeowv1.CreateSessionResponse{Session: s.proto()}, nil
	}

	if _, err := svc.db.ExecContext(ctx, "INSERT INTO session (id) VALUES (?)", id); err != nil {
		return nil, fmt.Errorf("create session %q: %w", id, err)
	}

	s := svc.add(id, svc.devices.NewDevice(), created(), "", "")
	svc.sessions[id] = s

	return &whatsmeowv1.CreateSessionResponse{Session: s.proto()}, nil
}

// ListSessions returns every session, ordered by id.
func (svc *Service) ListSessions(context.Context, *whatsmeowv1.ListSessionsRequest) (*whatsmeowv1.ListSessionsResponse, error) {
	svc.mu.Lock()
	all := make([]*session, 0, len(svc.sessions))

	for _, s := range svc.sessions {
		all = append(all, s)
	}
	svc.mu.Unlock()

	slices.SortFunc(all, func(a, b *session) int {
		if a.id < b.id {
			return -1
		}

		if a.id > b.id {
			return 1
		}

		return 0
	})

	resp := &whatsmeowv1.ListSessionsResponse{Sessions: make([]*whatsmeowv1.Session, 0, len(all))}
	for _, s := range all {
		resp.Sessions = append(resp.Sessions, s.proto())
	}

	return resp, nil
}

// DeleteSession disconnects the session if it is connected, deletes its device and its row, with its outbox events and
// cursors, and ends its watches and event streams.
func (svc *Service) DeleteSession(ctx context.Context, req *whatsmeowv1.DeleteSessionRequest) (*whatsmeowv1.DeleteSessionResponse, error) {
	s, err := svc.get(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	s.ops.Lock()
	defer s.ops.Unlock()

	s.mu.Lock()
	connected, jid := live(s.state) || is[*whatsmeowv1.SessionState_Pairing_](s.state), s.jid
	s.mu.Unlock()

	if connected {
		s.client.Disconnect()
	}

	// By the JID the session recorded, not the device's own fields: whatsmeow writes those on its own goroutine on a
	// server-side logout (store.go:286-297). Deleting rows that whatsmeow already deleted is a no-op.
	if jid != "" {
		parsed, err := types.ParseJID(jid)
		if err != nil {
			return nil, fmt.Errorf("delete session %q: its device JID %q: %w", s.id, jid, err)
		}

		if err := svc.devices.DeleteDevice(ctx, &store.Device{ID: &parsed}); err != nil {
			return nil, fmt.Errorf("delete session %q: its device: %w", s.id, err)
		}
	}

	if _, err := svc.db.ExecContext(ctx, "DELETE FROM session WHERE id = ?", s.id); err != nil {
		return nil, fmt.Errorf("delete session %q: %w", s.id, err)
	}

	// Its outbox events and cursors went with the row (ON DELETE CASCADE); its event streams end. Before the session
	// leaves svc.sessions, so a session created again with this id never gets the old one's ended watch.
	svc.box.Delete(s.id)

	svc.mu.Lock()
	delete(svc.sessions, s.id)
	svc.mu.Unlock()

	s.mu.Lock()
	close(s.deleted)
	s.mu.Unlock()

	return &whatsmeowv1.DeleteSessionResponse{}, nil
}

// Connect connects a paired session (session.proto). It is Connecting while whatsmeow's connect call runs, and
// back in its previous state when that fails.
func (svc *Service) Connect(ctx context.Context, req *whatsmeowv1.ConnectRequest) (*whatsmeowv1.ConnectResponse, error) {
	s, err := svc.get(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	s.ops.Lock()
	defer s.ops.Unlock()

	s.mu.Lock()
	previous := s.state

	if err := connectable(s.id, previous); err != nil || live(previous) {
		s.mu.Unlock()

		return &whatsmeowv1.ConnectResponse{}, err
	}

	if err := svc.transition(ctx, s, connecting()); err != nil {
		// Not connecting after all: stuck in Connecting, every later Connect would do nothing.
		if restoreErr := svc.transition(ctx, s, previous); restoreErr != nil {
			err = errors.Join(err, restoreErr)
		}

		s.mu.Unlock()

		return nil, err
	}
	s.mu.Unlock()

	if err := s.client.ConnectContext(svc.lifetime); err != nil { //nolint:contextcheck // the connection outlives the call (client.go:541-599)
		s.mu.Lock()
		defer s.mu.Unlock()

		// An event that came in meanwhile is the newer truth.
		if is[*whatsmeowv1.SessionState_Connecting_](s.state) {
			if restoreErr := svc.transition(ctx, s, previous); restoreErr != nil {
				err = errors.Join(err, restoreErr)
			}
		}

		return nil, fmt.Errorf("connect session %q: %w: %w", s.id, server.ErrConnectionFailed, err)
	}

	return &whatsmeowv1.ConnectResponse{}, nil
}

// connectable fails for the states Connect refuses: never paired, logged out, being paired, and banned with a known
// end that has not passed.
func connectable(id string, state *whatsmeowv1.SessionState) error {
	switch kind := state.GetKind().(type) {
	case *whatsmeowv1.SessionState_Pairing_:
		return fmt.Errorf("connect session %q: %w", id, server.ErrPairingInProgress)
	case *whatsmeowv1.SessionState_Created_:
		return fmt.Errorf("connect session %q: %w", id, server.ErrNotLoggedIn)
	case *whatsmeowv1.SessionState_LoggedOut_:
		return fmt.Errorf("connect session %q: %w", id, server.ErrLoggedOut)
	case *whatsmeowv1.SessionState_TempBanned_:
		expire := kind.TempBanned.GetExpireTime()
		if expire != nil && expire.AsTime().After(time.Now()) {
			end := expire.AsTime().UTC().Format(time.RFC3339)

			return fmt.Errorf("connect session %q until %s: %w", id, end, server.WithMetadata(server.ErrTemporarilyBanned, "expireTime", end))
		}
	}

	return nil
}

// Disconnect closes the connection and stops reconnecting; whatsmeow raises no event for it (client.go:695-703),
// so the sidecar records Disconnected itself.
func (svc *Service) Disconnect(ctx context.Context, req *whatsmeowv1.DisconnectRequest) (*whatsmeowv1.DisconnectResponse, error) {
	s, err := svc.get(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	s.ops.Lock()
	defer s.ops.Unlock()

	s.mu.Lock()
	connected, pairing := live(s.state), is[*whatsmeowv1.SessionState_Pairing_](s.state)
	// After the link, the login is the session's to stop, even with the Pair call gone (pair.go).
	if pairing && stopLogin(s) {
		connected, pairing = true, false
	}
	s.mu.Unlock()

	if pairing {
		return nil, fmt.Errorf("disconnect session %q: cancel the Pair call instead: %w", s.id, server.ErrPairingInProgress)
	}

	if !connected {
		return &whatsmeowv1.DisconnectResponse{}, nil
	}

	s.client.Disconnect()

	s.mu.Lock()
	defer s.mu.Unlock()

	if err := svc.transition(ctx, s, disconnected()); err != nil {
		return nil, err
	}

	return &whatsmeowv1.DisconnectResponse{}, nil
}

// Logout unlinks the device on WhatsApp; whatsmeow then deletes the device and raises no event
// (client.go:749-779), so the sidecar records LoggedOut with code 0 itself.
func (svc *Service) Logout(ctx context.Context, req *whatsmeowv1.LogoutRequest) (*whatsmeowv1.LogoutResponse, error) {
	s, err := svc.get(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	s.ops.Lock()
	defer s.ops.Unlock()

	s.mu.Lock()
	state := s.state
	s.mu.Unlock()

	switch state.GetKind().(type) {
	case *whatsmeowv1.SessionState_Created_:
		return nil, fmt.Errorf("log out session %q: %w", s.id, server.ErrNotLoggedIn)
	case *whatsmeowv1.SessionState_LoggedOut_:
		return nil, fmt.Errorf("log out session %q: %w", s.id, server.ErrLoggedOut)
	case *whatsmeowv1.SessionState_Connected_, *whatsmeowv1.SessionState_CaughtUp_:
	default:
		return nil, fmt.Errorf("log out session %q: %w", s.id, server.ErrNotConnected)
	}

	if err := s.client.Logout(ctx); err != nil {
		err = fmt.Errorf("log out session %q: %w", s.id, logoutError(err))

		// whatsmeow disconnects only once WhatsApp unlinked the device (client.go:771-772), so an error with no
		// connection left is a failure after the unlink, deleting the local store: the session is not connected.
		if !s.client.IsConnected() && !isClassified(err) {
			s.mu.Lock()
			defer s.mu.Unlock()

			if recordErr := svc.transition(ctx, s, disconnected()); recordErr != nil {
				err = errors.Join(err, recordErr)
			}
		}

		return nil, err
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	s.jid, s.lid = "", ""
	if err := svc.transition(ctx, s, loggedOut(0)); err != nil {
		return nil, err
	}

	return &whatsmeowv1.LogoutResponse{}, nil
}

// isClassified is an error logoutError gave a reason, or a context error.
func isClassified(err error) bool {
	return errors.Is(err, server.ErrOutcomeUnknown) || errors.Is(err, server.ErrWhatsAppError) || errors.Is(err, server.ErrNotConnected) ||
		errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded)
}

// logoutError classifies what whatsmeow's unlink request returned (request.go:152-182). A context error stays
// one, and keeps its own status.
func logoutError(err error) error {
	var (
		disconnected *whatsmeow.DisconnectedError
		iqErr        *whatsmeow.IQError
	)

	switch {
	case errors.Is(err, context.Canceled), errors.Is(err, context.DeadlineExceeded):
		return err
	case errors.Is(err, whatsmeow.ErrIQTimedOut), errors.As(err, &disconnected):
		// The request was sent and no answer came: the device may or may not be unlinked.
		return fmt.Errorf("%w: %w", server.ErrOutcomeUnknown, err)
	case errors.As(err, &iqErr):
		return fmt.Errorf("%w: %w", server.WithMetadata(server.ErrWhatsAppError, "code", fmt.Sprint(iqErr.Code)), err)
	case errors.Is(err, whatsmeow.ErrNotConnected):
		return fmt.Errorf("%w: %w", server.ErrNotConnected, err)
	default:
		return err
	}
}

// WatchSessionState sends the current state, then each change, until the client cancels or the session is
// deleted. A watcher that falls behind finds only the latest state.
func (svc *Service) WatchSessionState(req *whatsmeowv1.WatchSessionStateRequest, stream grpc.ServerStreamingServer[whatsmeowv1.WatchSessionStateResponse]) error {
	s, err := svc.get(req.GetSessionId())
	if err != nil {
		return err
	}

	latest := make(chan *whatsmeowv1.SessionState, 1)

	s.mu.Lock()
	if s.gone() {
		s.mu.Unlock()

		return fmt.Errorf("session %q: %w", s.id, server.ErrSessionNotFound)
	}

	latest <- s.state
	s.watchers[latest] = struct{}{}
	s.mu.Unlock()

	defer func() {
		s.mu.Lock()
		delete(s.watchers, latest)
		s.mu.Unlock()
	}()

	ctx := stream.Context()

	for {
		select {
		case <-ctx.Done():
			return fmt.Errorf("watch session %q: %w", s.id, ctx.Err())
		case <-s.deleted:
			return fmt.Errorf("session %q was deleted: %w", s.id, server.ErrSessionNotFound)
		case state := <-latest:
			if err := stream.Send(&whatsmeowv1.WatchSessionStateResponse{State: state}); err != nil {
				return fmt.Errorf("watch session %q: %w", s.id, err)
			}
		}
	}
}
