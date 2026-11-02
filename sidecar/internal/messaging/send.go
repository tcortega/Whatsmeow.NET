package messaging

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"strconv"
	"strings"
	"time"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/types"

	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// sent is a send WhatsApp accepted: the chat it went to, which can be the LID of a phone number (send.go:341-365,
// 384-385), and WhatsApp's timestamp.
type sent struct {
	chat string
	at   time.Time
}

// outcome is how a send ended. sentNothing is a failure that never reached WhatsApp, so a repeat may send.
type outcome struct {
	sent        sent
	err         error
	sentNothing bool
}

// shared is the error a repeat that waited for this send gets: an unknown outcome without the first caller's
// context error, which is not the repeat's.
func (o outcome) shared(id string) error {
	if errors.Is(o.err, server.ErrOutcomeUnknown) {
		return fmt.Errorf("send %q: %w", id, server.ErrOutcomeUnknown)
	}

	return o.err
}

type flightKey struct{ session, id string }

// flight is a send of one client message id in progress; a repeat waits for done.
type flight struct {
	done    chan struct{}
	outcome outcome
}

// queue is one session's sends, sent one at a time in order; tat paces them (a GCRA: the time the next send would be
// due if every send were spaced by pace).
type queue struct {
	jobs    []*job
	running bool
	tat     time.Time
}

// job is one send in a session's queue.
type job struct {
	ctx     context.Context //nolint:containedctx // the call's context, which the send runs under
	session string
	client  whatsApp
	// left is closed once the session stops being online.
	left    <-chan struct{}
	chat    types.JID
	id      string
	message *waE2E.Message
	// started is set, under the Service's lock, once the queue took it; until then its caller may take it back.
	started bool
	done    chan struct{}
	outcome outcome
}

// send sends message to chat as id, at most once per (session, id) (message.proto).
func (svc *Service) send(ctx context.Context, sessionID, chat, id string, message *waE2E.Message) (sent, error) {
	if err := validateSession(sessionID); err != nil {
		return sent{}, err
	}

	to, err := recipient("chat", chat)
	if err != nil {
		return sent{}, err
	}

	if !clientMessageID.MatchString(id) {
		return sent{}, invalid("client_message_id", "%q is not 3EB0 and 18 uppercase hex digits, the id format whatsmeow makes", id)
	}

	for {
		f, mine := svc.claim(sessionID, id)
		if !mine {
			if svc.onWait != nil {
				svc.onWait()
			}

			select {
			case <-f.done:
			case <-ctx.Done():
				return sent{}, fmt.Errorf("send %q: %w", id, ctx.Err())
			}

			// It sent nothing: this repeat sends, unless another claimed it first.
			if f.outcome.sentNothing {
				continue
			}

			return f.outcome.sent, f.outcome.shared(id)
		}

		o := svc.sendOnce(ctx, sessionID, to, id, message)
		svc.land(flightKey{sessionID, id}, f, o)

		return o.sent, o.err
	}
}

// claim makes this call the one that sends id, or returns the flight of the call that does.
func (svc *Service) claim(sessionID, id string) (*flight, bool) {
	svc.mu.Lock()
	defer svc.mu.Unlock()

	key := flightKey{sessionID, id}
	if f, ok := svc.flights[key]; ok {
		return f, false
	}

	f := &flight{done: make(chan struct{})}
	svc.flights[key] = f

	return f, true
}

// land ends a flight with its outcome and wakes its repeats.
func (svc *Service) land(key flightKey, f *flight, o outcome) {
	svc.mu.Lock()
	delete(svc.flights, key)
	svc.mu.Unlock()

	f.outcome = o
	close(f.done)
}

// sendOnce answers a repeat from its record, or queues the send.
func (svc *Service) sendOnce(ctx context.Context, sessionID string, to types.JID, id string, message *waE2E.Message) outcome {
	if o, found, err := svc.recorded(ctx, sessionID, id); err != nil || found {
		return outcome{sent: o.sent, err: errors.Join(o.err, err), sentNothing: err != nil}
	}

	// A send never waits for a connection (message.proto).
	client, left, err := svc.sessions.Online(sessionID)
	if err != nil {
		return outcome{err: fmt.Errorf("send %q: %w", id, err), sentNothing: true}
	}

	wa, ok := client.(whatsApp)
	if !ok {
		return outcome{err: fmt.Errorf("send %q: the client %T of session %q cannot send", id, client, sessionID), sentNothing: true}
	}

	j := &job{ctx: ctx, session: sessionID, client: wa, left: left, chat: to, id: id, message: message, done: make(chan struct{})}
	svc.enqueue(j) //nolint:contextcheck // the job carries ctx, and its queue runs it under ctx

	return svc.await(j)
}

// recorded is the outcome a repeat gets from the send record of id, if there is one: the recorded success, the
// recorded WhatsApp error, or, with neither, an unknown outcome (the send was recorded before its hand-off).
func (svc *Service) recorded(ctx context.Context, sessionID, id string) (outcome, bool, error) {
	var (
		chat        sql.NullString
		at, waError sql.NullInt64
	)

	err := svc.db.QueryRowContext(ctx, "SELECT chat, message_time, whatsapp_error FROM sent_message WHERE session_id = ? AND client_message_id = ?",
		sessionID, id).Scan(&chat, &at, &waError)
	if errors.Is(err, sql.ErrNoRows) {
		return outcome{}, false, nil
	}

	if err != nil {
		return outcome{}, false, fmt.Errorf("send %q: read its record: %w", id, err)
	}

	switch {
	case chat.Valid:
		return outcome{sent: sent{chat: chat.String, at: time.Unix(at.Int64, 0)}}, true, nil
	case waError.Valid:
		return outcome{err: whatsAppError(id, int(waError.Int64))}, true, nil
	default:
		return outcome{err: fmt.Errorf("send %q was handed to WhatsApp and no answer was recorded: %w", id, server.ErrOutcomeUnknown)}, true, nil
	}
}

// enqueue adds j to its session's queue and starts the queue if it is idle.
func (svc *Service) enqueue(j *job) {
	svc.mu.Lock()

	q, ok := svc.queues[j.session]
	if !ok {
		q = &queue{}
		svc.queues[j.session] = q
	}

	q.jobs = append(q.jobs, j)
	waits := q.running

	if !q.running {
		q.running = true

		svc.workers.Go(func() { svc.work(q) })
	}
	svc.mu.Unlock()

	if waits && svc.onQueue != nil {
		svc.onQueue()
	}
}

// await waits for j's outcome. A send that has not left the queue yet fails at once when its call ends, its session
// stops being online, or the slice closes; it sent nothing.
func (svc *Service) await(j *job) outcome {
	var why error

	select {
	case <-j.done:
		return j.outcome
	case <-j.ctx.Done():
		why = j.ctx.Err()
	case <-j.left:
		why = svc.offline(j.session)
	case <-svc.lifetime.Done():
		why = server.ErrNotConnected
	}

	if !svc.takeBack(j) {
		<-j.done

		return j.outcome
	}

	return outcome{err: fmt.Errorf("send %q: %w", j.id, why), sentNothing: true}
}

// takeBack removes j from its queue unless the queue took it already.
func (svc *Service) takeBack(j *job) bool {
	svc.mu.Lock()
	defer svc.mu.Unlock()

	if j.started {
		return false
	}

	q := svc.queues[j.session]
	for i, queued := range q.jobs {
		if queued == j {
			q.jobs = append(q.jobs[:i], q.jobs[i+1:]...)

			break
		}
	}

	return true
}

// offline is why a session stopped being online.
func (svc *Service) offline(sessionID string) error {
	if _, _, err := svc.sessions.Online(sessionID); err != nil {
		return fmt.Errorf("the session stopped being online: %w", err)
	}

	// Online again already: this send still saw it leave.
	return server.ErrNotConnected
}

// work runs a session's queue until it is empty.
func (svc *Service) work(q *queue) {
	for {
		svc.mu.Lock()
		if len(q.jobs) == 0 {
			q.running = false
			svc.mu.Unlock()

			return
		}

		j := q.jobs[0]
		q.jobs = q.jobs[1:]
		j.started = true
		svc.mu.Unlock()

		j.outcome = svc.run(q, j)
		close(j.done)
	}
}

// run paces j, records it, hands it to whatsmeow, and records what came back.
func (svc *Service) run(q *queue, j *job) outcome {
	if err := svc.sleep(j, svc.due(q)); err != nil {
		return outcome{err: fmt.Errorf("send %q: %w", j.id, err), sentNothing: true}
	}

	// Recorded before the hand-off: from here on a crash or a lost answer reads as an unknown outcome.
	if _, err := svc.db.ExecContext(j.ctx, "INSERT INTO sent_message (session_id, client_message_id) VALUES (?, ?)", j.session, j.id); err != nil {
		return outcome{err: fmt.Errorf("send %q: record it: %w", j.id, err), sentNothing: true}
	}

	svc.spend(q)

	for try := 0; ; try++ {
		resp, err := j.client.SendMessage(j.ctx, j.chat, j.message, whatsmeow.SendRequestExtra{ID: j.id})
		if err == nil {
			svc.write(j, "UPDATE sent_message SET chat = ?, message_time = ? WHERE session_id = ? AND client_message_id = ?",
				resp.Chat.String(), resp.Timestamp.Unix(), j.session, j.id)

			return outcome{sent: sent{chat: resp.Chat.String(), at: time.Unix(resp.Timestamp.Unix(), 0)}}
		}

		switch class, reason := classify(err); class {
		case rateLimited:
			if try < len(svc.backoff) && fits(j.ctx, svc.backoff[try]) {
				if err := svc.sleep(j, svc.backoff[try]); err == nil {
					continue
				}
			}

			svc.forget(j)

			return outcome{err: fmt.Errorf("send %q: %w: %w", j.id, server.ErrRateLimited, err), sentNothing: true}
		case notSent:
			svc.forget(j)

			return outcome{err: fmt.Errorf("send %q: %w: %w", j.id, reason, err), sentNothing: true}
		case rejected:
			code := serverErrorCode(err)
			svc.write(j, "UPDATE sent_message SET whatsapp_error = ? WHERE session_id = ? AND client_message_id = ?", code, j.session, j.id)

			return outcome{err: fmt.Errorf("%w: %w", whatsAppError(j.id, code), err)}
		case unknown:
			svc.log.Warn().Err(err).Str("session_id", j.session).Str("client_message_id", j.id).
				Msg("a send was handed to whatsmeow and its outcome is unknown; it is never sent again")

			return outcome{err: fmt.Errorf("send %q: %w: %w", j.id, server.ErrOutcomeUnknown, err)}
		}
	}
}

// forget deletes j's record: nothing of it reached WhatsApp, so a repeat may send.
func (svc *Service) forget(j *job) {
	svc.write(j, "DELETE FROM sent_message WHERE session_id = ? AND client_message_id = ?", j.session, j.id)
}

// write changes j's record. It runs even when the call ended, because what happened has to be recorded; a failure
// leaves the record pending, which a repeat reads as an unknown outcome: never a second send.
func (svc *Service) write(j *job, query string, args ...any) {
	if _, err := svc.db.ExecContext(context.WithoutCancel(j.ctx), query, args...); err != nil {
		svc.log.Error().Err(err).Str("session_id", j.session).Str("client_message_id", j.id).Msg("recording a send's outcome failed")
	}
}

// due is how long the next send of q has to wait for its turn.
func (svc *Service) due(q *queue) time.Duration {
	svc.mu.Lock()
	defer svc.mu.Unlock()

	return max(0, time.Until(q.tat.Add(-time.Duration(svc.burst-1)*svc.pace)))
}

// spend takes the send's turn, now.
func (svc *Service) spend(q *queue) {
	svc.mu.Lock()
	defer svc.mu.Unlock()

	now := time.Now()
	if q.tat.Before(now) {
		q.tat = now
	}

	q.tat = q.tat.Add(svc.pace)
}

// sleep waits d, or fails when j's call ends, its session stops being online, or the slice closes first.
func (svc *Service) sleep(j *job, d time.Duration) error {
	timer := time.NewTimer(d)
	defer timer.Stop()

	select {
	case <-timer.C:
		return nil
	case <-j.ctx.Done():
		return fmt.Errorf("while waiting its turn: %w", j.ctx.Err())
	case <-j.left:
		return svc.offline(j.session)
	case <-svc.lifetime.Done():
		return fmt.Errorf("the sidecar is stopping: %w", server.ErrNotConnected)
	}
}

// fits is a wait of d that ends before ctx's deadline.
func fits(ctx context.Context, d time.Duration) bool {
	deadline, ok := ctx.Deadline()

	return !ok || time.Until(deadline) > d
}

// class is what a SendMessage error says about the send.
type class int

const (
	// unknown: the node may have been written (ADR 0025). Never sent again.
	unknown class = iota
	// notSent: whatsmeow failed before it wrote the node.
	notSent
	// rateLimited: an IQ before the node was refused with 429; tried again after a backoff.
	rateLimited
	// rejected: WhatsApp answered the node with an error code.
	rejected
)

// classify reads a SendMessage error at the pin. Only what provably fails before the node is written is notSent:
//   - ErrNotLoggedIn: the device has no ID (send.go:207-211). The session was online with a paired device, so it was
//     logged out meanwhile: whatsmeow deletes the device on its own goroutine (connectionevents.go:43-44).
//   - ErrRecipientNotOnWhatsApp (send.go:351-357).
//   - an IQ error: every IQ of a send (the LID lookup, group members, devices) runs before sendNodeAndGetData
//     (send.go:310-351, sendDM 875-918, sendGroup 814-840).
//   - ErrNotConnected wrapped: sendDM and sendGroup wrap what comes before the write (send.go:840, 921), and
//     sendNodeAndGetData returns it when there is no socket, before it marshals or writes anything (client.go:981-983).
//
// ErrNotConnected itself, unwrapped, is retryFrame's after the node was written (request.go:207-209, send.go:459-462):
// unknown, as are a timeout, a context end, a DisconnectedError and a failed write (send.go:438-463).
func classify(err error) (class, error) {
	var iq *whatsmeow.IQError

	switch {
	case errors.Is(err, whatsmeow.ErrNotLoggedIn):
		return notSent, server.ErrLoggedOut
	case errors.Is(err, whatsmeow.ErrRecipientNotOnWhatsApp):
		return notSent, server.ErrRecipientNotOnWhatsApp
	case errors.As(err, &iq) && iq.Code == 429:
		// ErrIQRateOverLimit's Is also compares the text (errors.go:197, 229-240), so match the code.
		return rateLimited, nil
	case errors.As(err, &iq):
		return notSent, fmt.Errorf("WhatsApp answered with error %d: %w", iq.Code, server.WithMetadata(server.ErrWhatsAppError, "code", strconv.Itoa(iq.Code)))
	case errors.Is(err, whatsmeow.ErrNotConnected) && err != whatsmeow.ErrNotConnected: //nolint:errorlint // the unwrapped one comes after the write
		return notSent, server.ErrNotConnected
	case errors.Is(err, whatsmeow.ErrServerReturnedError) && serverErrorCode(err) != 0:
		return rejected, nil
	default:
		return unknown, nil
	}
}

// serverErrorCode is the code of whatsmeow's ErrServerReturnedError, which carries it only in its text,
// "server returned error <code>" (send.go:468-470); 0 when there is none.
func serverErrorCode(err error) int {
	code, err := strconv.Atoi(strings.TrimPrefix(err.Error(), whatsmeow.ErrServerReturnedError.Error()+" "))
	if err != nil {
		return 0
	}

	return code
}

// whatsAppError is WHATSAPP_ERROR with WhatsApp's code.
func whatsAppError(id string, code int) error {
	return fmt.Errorf("send %q: WhatsApp answered with error %d: %w", id, code, server.WithMetadata(server.ErrWhatsAppError, "code", strconv.Itoa(code)))
}
