//go:build livetest

package contacts

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"testing"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	waBinary "go.mau.fi/whatsmeow/binary"
	"go.mau.fi/whatsmeow/types"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"

	livetestv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/livetest/v1"
)

// aboutStub is the stub with the about calls the live control makes, the mutation and the legacy IQ; what they got is
// kept for the test.
type aboutStub struct {
	*stub

	err   error
	sets  []types.SetStatusInput
	iqErr error
	iqs   []whatsmeow.DangerousInfoQuery
	// iqHangs makes the IQ wait for its ctx to end, after closing iqSent; iqCtx is the ctx it got.
	iqHangs bool
	iqSent  chan struct{}
	iqCtx   context.Context //nolint:containedctx // the ctx the IQ got, for the test
}

func (s *aboutStub) SetStatusMessage(_ context.Context, input types.SetStatusInput) error {
	s.sets = append(s.sets, input)

	return s.err
}

func (s *aboutStub) SendIQ(ctx context.Context, query whatsmeow.DangerousInfoQuery) (*waBinary.Node, error) {
	s.iqs = append(s.iqs, query)
	s.iqCtx = ctx

	if s.iqHangs {
		close(s.iqSent)
		<-ctx.Done()

		return nil, ctx.Err() //nolint:wrapcheck // as whatsmeow's sendIQ returns it (request.go:178-179)
	}

	return &waBinary.Node{Tag: "iq"}, s.iqErr
}

func startLiveContacts(t *testing.T) (*harness, *aboutStub, livetestv1.LiveContactServiceClient) {
	t.Helper()

	return startLiveContactsLogging(t, zerolog.Nop())
}

// startLiveContactsLogging is startLiveContacts with the server and the control logging to log.
func startLiveContactsLogging(t *testing.T, log zerolog.Logger) (*harness, *aboutStub, livetestv1.LiveContactServiceClient) {
	t.Helper()

	h := startLogging(t, log)
	about := &aboutStub{stub: h.stub}
	h.sessions.client = about

	return h, about, livetestv1.NewLiveContactServiceClient(h.conn)
}

func TestTheLiveControlSetsTheAboutTextOnly(t *testing.T) {
	t.Parallel()

	h, about, live := startLiveContacts(t)

	got, err := live.SetAbout(h.ctx, &livetestv1.SetAboutRequest{SessionId: "s1", Text: "wmn about 20261108120000"})
	if err != nil {
		t.Fatalf("SetAbout: %v", err)
	}

	wantProto(t, got, &livetestv1.SetAboutResponse{})

	if len(about.sets) != 1 {
		t.Fatalf("whatsmeow got %d sets; want 1", len(about.sets))
	}

	set := about.sets[0]
	if set.Text == nil || *set.Text != "wmn about 20261108120000" || set.Emoji != nil || !set.Duration.IsZero() {
		t.Fatalf("whatsmeow got %+v; want the text alone, no emoji, no duration", set)
	}
}

func TestTheLiveControlRefusesAnEmptyAboutBeforeWhatsmeow(t *testing.T) {
	t.Parallel()

	h, about, live := startLiveContacts(t)

	_, err := live.SetAbout(h.ctx, &livetestv1.SetAboutRequest{SessionId: "s1"})

	wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": "text"})

	if len(about.sets) != 0 {
		t.Fatalf("whatsmeow got %d sets; want none", len(about.sets))
	}
}

func TestTheLiveControlRefusesAnUnknownSession(t *testing.T) {
	t.Parallel()

	h, about, live := startLiveContacts(t)

	_, err := live.SetAbout(h.ctx, &livetestv1.SetAboutRequest{SessionId: "s2", Text: "wmn about"})

	wantReason(t, err, codes.NotFound, "SESSION_NOT_FOUND", nil)

	if len(about.sets) != 0 {
		t.Fatalf("whatsmeow got %d sets; want none", len(about.sets))
	}
}

// A GraphQL error's message may echo the input [unknown]: when the mutation and the legacy IQ are both refused, only the
// IQ's code comes back, and neither message.
func TestTheLiveControlAnswersARefusedMutationAndLegacyIQWithTheIQsCodeOnly(t *testing.T) {
	t.Parallel()

	h, about, live := startLiveContacts(t)
	about.err = refusedMutation()
	about.iqErr = &whatsmeow.IQError{Code: 406, Text: "not-acceptable"}

	_, err := live.SetAbout(h.ctx, &livetestv1.SetAboutRequest{SessionId: "s1", Text: "wmn about 20261108120000"})

	wantReason(t, err, codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "406"})

	if strings.Contains(status.Convert(err).Message(), "wmn about") || strings.Contains(status.Convert(err).Message(), "refused") {
		t.Fatalf("SetAbout's status message %q carries the GraphQL error's message", status.Convert(err).Message())
	}
}

// A write whatsmeow got no answer to may have been made: OUTCOME_UNKNOWN, as every slice's write (classify).
func TestTheLiveControlAnswersATimedOutSetAsOutcomeUnknown(t *testing.T) {
	t.Parallel()

	h, about, live := startLiveContacts(t)
	about.err = fmt.Errorf("mex: %w", whatsmeow.ErrIQTimedOut)

	_, err := live.SetAbout(h.ctx, &livetestv1.SetAboutRequest{SessionId: "s1", Text: "wmn about"})

	wantReason(t, err, codes.Unknown, "OUTCOME_UNKNOWN", nil)
}

// refusedMutation is the answer the business account got in #36's live run, a GraphQL error (newsletter.go:234-239),
// with a message that echoes the text.
func refusedMutation() error {
	return fmt.Errorf("graphql error: %w", types.GraphQLErrors{{
		Message: "text wmn about 20261108120000 refused", Extensions: types.GraphQLErrorExtensions{ErrorCode: 400, Severity: "CRITICAL"},
	}})
}

// aboutLog is the control's one log line of a set, decoded; it fails unless there is exactly one and it carries no text.
func aboutLog(t *testing.T, logged *bytes.Buffer) map[string]any {
	t.Helper()

	return aboutLine(t, logged.String())
}

// aboutLine is aboutLog of the log text logged.
func aboutLine(t *testing.T, logged string) map[string]any {
	t.Helper()

	if strings.Contains(logged, "wmn about") || strings.Contains(logged, "refused") {
		t.Fatalf("the log carries the about text or WhatsApp's message:\n%s", logged)
	}

	var lines []map[string]any

	for line := range strings.Lines(logged) {
		var entry map[string]any
		if err := json.Unmarshal([]byte(line), &entry); err != nil {
			t.Fatalf("log line %q: %v", line, err)
		}

		if entry["message"] == "live control: set the about text" {
			lines = append(lines, entry)
		}
	}

	if len(lines) != 1 {
		t.Fatalf("the log has %d about lines; want 1:\n%s", len(lines), logged)
	}

	return lines[0]
}

// The legacy IQ #1331 sends, for a text-only call, once the mutation was answered with a GraphQL error: the business
// account's answer in #36's live run.
func TestTheLiveControlSendsTheLegacyIQWhenTheMutationIsRefused(t *testing.T) {
	t.Parallel()

	var logged bytes.Buffer

	h, about, live := startLiveContactsLogging(t, zerolog.New(&logged).Level(zerolog.InfoLevel))
	about.err = refusedMutation()

	got, err := live.SetAbout(h.ctx, &livetestv1.SetAboutRequest{SessionId: "s1", Text: "wmn about 20261108120000"})
	if err != nil {
		t.Fatalf("SetAbout: %v", err)
	}

	wantProto(t, got, &livetestv1.SetAboutResponse{})

	if len(about.sets) != 1 || len(about.iqs) != 1 {
		t.Fatalf("whatsmeow got %d mutations and %d IQs; want 1 and 1", len(about.sets), len(about.iqs))
	}

	// #1331's node for a text-only call: no id, target, timeout or retry of its own, one <status> with the text.
	iq := about.iqs[0]
	content, _ := iq.Content.([]waBinary.Node)
	iq.Content = nil

	if iq != (whatsmeow.DangerousInfoQuery{Namespace: "status", Type: "set", To: types.ServerJID}) {
		t.Fatalf("whatsmeow got the IQ %+v; want a status set to %s", iq, types.ServerJID)
	}

	if len(content) != 1 || content[0].Tag != "status" || len(content[0].Attrs) != 0 || content[0].Content != "wmn about 20261108120000" {
		t.Fatalf("whatsmeow got the IQ's content %+v; want one <status> with the text", content)
	}

	line := aboutLog(t, &logged)
	if fmt.Sprintf("%v %v %v %v", line["mex_code"], line["mex_severity"], line["path"], line["legacy"]) != "400 CRITICAL legacy answered" {
		t.Fatalf("the about line is %v; want the mutation's code and severity, and the legacy IQ answered", line)
	}
}

// A mutation that was answered sets the about: no legacy IQ.
func TestTheLiveControlSendsNoLegacyIQWhenTheMutationIsAnswered(t *testing.T) {
	t.Parallel()

	var logged bytes.Buffer

	h, about, live := startLiveContactsLogging(t, zerolog.New(&logged).Level(zerolog.InfoLevel))

	if _, err := live.SetAbout(h.ctx, &livetestv1.SetAboutRequest{SessionId: "s1", Text: "wmn about 20261108120000"}); err != nil {
		t.Fatalf("SetAbout: %v", err)
	}

	if len(about.iqs) != 0 {
		t.Fatalf("whatsmeow got %d IQs; want none", len(about.iqs))
	}

	if line := aboutLog(t, &logged); fmt.Sprintf("%v %v %v", line["path"], line["mex_code"], line["legacy"]) != "mex <nil> <nil>" {
		t.Fatalf("the about line is %v; want the mutation's path alone", line)
	}
}

// Both refused: the line has both outcomes, the IQ's by its code and keyword.
func TestTheLiveControlLogsBothOutcomesWhenTheLegacyIQIsRefusedToo(t *testing.T) {
	t.Parallel()

	var logged bytes.Buffer

	h, about, live := startLiveContactsLogging(t, zerolog.New(&logged).Level(zerolog.InfoLevel))
	about.err = refusedMutation()
	about.iqErr = &whatsmeow.IQError{Code: 406, Text: "not-acceptable"}

	_, _ = live.SetAbout(h.ctx, &livetestv1.SetAboutRequest{SessionId: "s1", Text: "wmn about 20261108120000"})

	line := aboutLog(t, &logged)
	if fmt.Sprintf("%v %v %v", line["mex_code"], line["mex_severity"], line["path"]) != "400 CRITICAL none" ||
		!strings.Contains(fmt.Sprint(line["legacy"]), "406: not-acceptable") {
		t.Fatalf("the about line is %v; want the mutation's code and severity, and the IQ's code and keyword", line)
	}
}

// A mutation with no answer may have been made, and its wait used whatsmeow's IQ timeout: no legacy IQ after it.
func TestTheLiveControlSendsNoLegacyIQAfterATimedOutMutation(t *testing.T) {
	t.Parallel()

	h, about, live := startLiveContacts(t)
	about.err = fmt.Errorf("mex: %w", whatsmeow.ErrIQTimedOut)

	_, _ = live.SetAbout(h.ctx, &livetestv1.SetAboutRequest{SessionId: "s1", Text: "wmn about"})

	if len(about.iqs) != 0 {
		t.Fatalf("whatsmeow got %d IQs; want none", len(about.iqs))
	}
}

// A mutation that failed without a CRITICAL refusal may have been made, was never answered, or failed otherwise: no
// legacy IQ follows, and the one line has the mutation's classified outcome.
func TestTheLiveControlSendsNoLegacyIQUnlessTheMutationIsRefused(t *testing.T) {
	t.Parallel()

	for _, tc := range []struct {
		name string
		err  error
		path string
	}{
		{"disconnected", &whatsmeow.DisconnectedError{Action: "info query"}, "unknown"},
		{"not connected", whatsmeow.ErrNotConnected, "none"},
		{"cancelled", context.Canceled, "unknown"},
		// WhatsApp answered the mutation, without its result (newsletter.go:183-201): it may have been made.
		{"no result", &whatsmeow.ElementMissingError{Tag: "result", In: "mex response"}, "unknown"},
		// The mex answer in argo, which whatsmeow does not decode at the pin (newsletter.go:206-209).
		{"argo", errors.New("argo decoding is currently broken"), "none"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			var logged bytes.Buffer

			h, about, live := startLiveContactsLogging(t, zerolog.New(&logged).Level(zerolog.InfoLevel))
			about.err = fmt.Errorf("mex: %w", tc.err)

			_, _ = live.SetAbout(h.ctx, &livetestv1.SetAboutRequest{SessionId: "s1", Text: "wmn about 20261108120000"})

			if len(about.iqs) != 0 {
				t.Fatalf("whatsmeow got %d IQs; want none", len(about.iqs))
			}

			line := aboutLog(t, &logged)
			if line["path"] != tc.path || line["mex"] == nil || line["legacy"] != nil {
				t.Fatalf("the about line is %v; want path %s with the mutation's outcome alone", line, tc.path)
			}

			// The mutation's error comes wrapped: its outcome names whatsmeow's type, not the wrapper's.
			if mex, _ := line["mex"].(string); strings.Contains(mex, "wrapError") {
				t.Fatalf("the mutation's outcome %q names the wrapper", mex)
			}
		})
	}
}

// Only the CRITICAL refusal #1331 names falls back; any other GraphQL error comes back with its code, and the line has
// its code and severity.
func TestTheLiveControlSendsNoLegacyIQAfterARefusalThatIsNotCritical(t *testing.T) {
	t.Parallel()

	var logged bytes.Buffer

	h, about, live := startLiveContactsLogging(t, zerolog.New(&logged).Level(zerolog.InfoLevel))
	about.err = fmt.Errorf("graphql error: %w", types.GraphQLErrors{{
		Message: "text wmn about 20261108120000 refused", Extensions: types.GraphQLErrorExtensions{ErrorCode: 429, Severity: "WARNING"},
	}})

	_, err := live.SetAbout(h.ctx, &livetestv1.SetAboutRequest{SessionId: "s1", Text: "wmn about 20261108120000"})

	wantReason(t, err, codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "429"})

	if strings.Contains(status.Convert(err).Message(), "wmn about") || strings.Contains(status.Convert(err).Message(), "refused") {
		t.Fatalf("SetAbout's status message %q carries the GraphQL error's message", status.Convert(err).Message())
	}

	if len(about.iqs) != 0 {
		t.Fatalf("whatsmeow got %d IQs; want none", len(about.iqs))
	}

	line := aboutLog(t, &logged)
	if fmt.Sprintf("%v %v %v %v", line["mex_code"], line["mex_severity"], line["path"], line["legacy"]) != "429 WARNING none <nil>" {
		t.Fatalf("the about line is %v; want the refusal's code and severity, and no legacy IQ", line)
	}
}

// A legacy IQ with no answer may have been made: path unknown, and the set answers OUTCOME_UNKNOWN.
func TestTheLiveControlLogsATimedOutLegacyIQAsUnknown(t *testing.T) {
	t.Parallel()

	var logged bytes.Buffer

	h, about, live := startLiveContactsLogging(t, zerolog.New(&logged).Level(zerolog.InfoLevel))
	about.err = refusedMutation()
	about.iqErr = whatsmeow.ErrIQTimedOut

	_, err := live.SetAbout(h.ctx, &livetestv1.SetAboutRequest{SessionId: "s1", Text: "wmn about 20261108120000"})

	wantReason(t, err, codes.Unknown, "OUTCOME_UNKNOWN", nil)

	if line := aboutLog(t, &logged); line["path"] != "unknown" {
		t.Fatalf("the about line is %v; want path unknown", line)
	}
}

// .NET's cancellation reaches the legacy IQ through the call's ctx, and the line says the IQ may have been made.
func TestTheLiveControlsLegacyIQEndsWithACancelledCall(t *testing.T) {
	t.Parallel()

	logged := make(chan string, 16)

	h, about, live := startLiveContactsLogging(t, zerolog.New(lineWriter(logged)).Level(zerolog.InfoLevel))
	about.err = refusedMutation()
	about.iqHangs = true
	about.iqSent = make(chan struct{})

	ctx, cancel := context.WithCancel(h.ctx)
	called := make(chan error, 1)

	go func() {
		_, err := live.SetAbout(ctx, &livetestv1.SetAboutRequest{SessionId: "s1", Text: "wmn about 20261108120000"})
		called <- err
	}()

	select {
	case <-about.iqSent:
	case <-h.ctx.Done():
		t.Fatal("the legacy IQ was never sent")
	}

	cancel()

	if err := <-called; status.Code(err) != codes.Canceled {
		t.Fatalf("SetAbout: %v; want CANCELED", err)
	}

	// The line is written after the IQ returned, on the server's goroutine.
	for {
		select {
		case line := <-logged:
			if !strings.Contains(line, "live control: set the about text") {
				continue
			}

			if got := aboutLine(t, line); got["path"] != "unknown" || !errors.Is(about.iqCtx.Err(), context.Canceled) {
				t.Fatalf("the about line is %v and the IQ's ctx ended %v; want path unknown, cancelled", got, about.iqCtx.Err())
			}

			return
		case <-h.ctx.Done():
			t.Fatal("no about line after the cancelled call")
		}
	}
}

// lineWriter sends each log line written to it on lines, so a test reads a line the server logs after its call returned.
type lineWriter chan string

func (w lineWriter) Write(p []byte) (int, error) {
	w <- string(p)

	return len(p), nil
}

// A real client sends the legacy IQ through whatsmeow's internals.
func TestTheLiveControlSendsTheLegacyIQThroughARealClient(t *testing.T) {
	t.Parallel()

	h := start(t)

	if _, ok := legacy(whatsmeow.NewClient(h.device, nil)); !ok {
		t.Fatal("a *whatsmeow.Client cannot send the legacy IQ")
	}
}
