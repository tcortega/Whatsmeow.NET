//go:build livetest

package contacts

import (
	"context"
	"fmt"
	"strings"
	"testing"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/types"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"

	livetestv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/livetest/v1"
)

// aboutStub is the stub with the about call the live control makes; what it got is kept for the test.
type aboutStub struct {
	*stub

	err  error
	sets []types.SetStatusInput
}

func (s *aboutStub) SetStatusMessage(_ context.Context, input types.SetStatusInput) error {
	s.sets = append(s.sets, input)

	return s.err
}

func startLiveContacts(t *testing.T) (*harness, *aboutStub, livetestv1.LiveContactServiceClient) {
	t.Helper()

	h := start(t)
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

// A GraphQL error's message may echo the input [unknown]; only its code may come back.
func TestTheLiveControlAnswersAGraphQLErrorWithItsCodeOnly(t *testing.T) {
	t.Parallel()

	h, about, live := startLiveContacts(t)
	about.err = fmt.Errorf("graphql error: %w", types.GraphQLErrors{{
		Message: "text wmn about 20261108120000 refused", Extensions: types.GraphQLErrorExtensions{ErrorCode: 400, Severity: "CRITICAL"},
	}})

	_, err := live.SetAbout(h.ctx, &livetestv1.SetAboutRequest{SessionId: "s1", Text: "wmn about 20261108120000"})

	wantReason(t, err, codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "400"})

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
