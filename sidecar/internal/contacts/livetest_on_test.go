//go:build livetest

package contacts

import (
	"bytes"
	"context"
	"encoding/json"
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
}

func (s *aboutStub) SetStatusMessage(_ context.Context, input types.SetStatusInput) error {
	s.sets = append(s.sets, input)

	return s.err
}

func (s *aboutStub) SendIQ(_ context.Context, query whatsmeow.DangerousInfoQuery) (*waBinary.Node, error) {
	s.iqs = append(s.iqs, query)

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

	if strings.Contains(logged.String(), "wmn about") || strings.Contains(logged.String(), "refused") {
		t.Fatalf("the log carries the about text or WhatsApp's message:\n%s", logged.String())
	}

	var lines []map[string]any

	for line := range strings.Lines(logged.String()) {
		var entry map[string]any
		if err := json.Unmarshal([]byte(line), &entry); err != nil {
			t.Fatalf("log line %q: %v", line, err)
		}

		if entry["message"] == "live control: set the about text" {
			lines = append(lines, entry)
		}
	}

	if len(lines) != 1 {
		t.Fatalf("the log has %d about lines; want 1:\n%s", len(lines), logged.String())
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

	want := fmt.Sprintf("%+v", whatsmeow.DangerousInfoQuery{
		Namespace: "status", Type: "set", To: types.ServerJID,
		Content: []waBinary.Node{{Tag: "status", Content: "wmn about 20261108120000"}},
	})
	if fmt.Sprintf("%+v", about.iqs[0]) != want {
		t.Fatalf("whatsmeow got the IQ %+v; want %s", about.iqs[0], want)
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
