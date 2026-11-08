//go:build livetest

package contacts

import (
	"context"
	"errors"
	"fmt"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/types"
	"google.golang.org/grpc"

	livetestv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/livetest/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// RegisterLive serves the contacts slice's part of the live tests' control on srv, through svc's sessions: the live
// tests' build only (ADR 0063). A release build never links this file (build/ci.sh).
func RegisterLive(srv grpc.ServiceRegistrar, svc *Service) {
	livetestv1.RegisterLiveContactServiceServer(srv, &live{svc: svc})
}

// statuses is the one whatsmeow call the control makes that the contract does not.
type statuses interface {
	SetStatusMessage(ctx context.Context, status types.SetStatusInput) error
}

var _ statuses = (*whatsmeow.Client)(nil)

// live implements livetestv1.LiveContactServiceServer.
type live struct {
	livetestv1.UnimplementedLiveContactServiceServer

	svc *Service
}

// SetAbout sets the session's account's about text, the text only (user.go:160-172): no emoji and no duration, which no
// read gives back (user.go:257-258), so the live test never sets what it cannot restore (ADR 0063).
func (l *live) SetAbout(ctx context.Context, req *livetestv1.SetAboutRequest) (*livetestv1.SetAboutResponse, error) {
	if err := checkSession(req.GetSessionId()); err != nil {
		return nil, err
	}

	text := req.GetText()
	if text == "" {
		return nil, fmt.Errorf("set the about text: %w", server.InvalidArgument("text", "empty"))
	}

	client, err := l.svc.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	setter, ok := client.(statuses)
	if !ok {
		return nil, fmt.Errorf("session %q: its client %T cannot set the about text", req.GetSessionId(), client)
	}

	if err := setter.SetStatusMessage(ctx, types.SetStatusInput{Text: &text}); err != nil {
		return nil, classifyAbout(err)
	}

	return &livetestv1.SetAboutResponse{}, nil
}

// classifyAbout is classify for a write, but a GraphQL error (newsletter.go:234-239) keeps only its first code: its
// message is WhatsApp's text, which may echo the about text [unknown] (ADR 0063).
func classifyAbout(err error) error {
	var graphQL types.GraphQLErrors
	if errors.As(err, &graphQL) && len(graphQL) > 0 {
		return fmt.Errorf("set the about text: %w", server.WithMetadata(server.ErrWhatsAppError, "code", fmt.Sprint(graphQL[0].Extensions.ErrorCode)))
	}

	return classify("set the about text", err, true)
}
