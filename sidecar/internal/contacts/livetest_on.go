//go:build livetest

package contacts

import (
	"context"
	"errors"
	"fmt"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	waBinary "go.mau.fi/whatsmeow/binary"
	"go.mau.fi/whatsmeow/types"
	"google.golang.org/grpc"

	livetestv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/livetest/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// RegisterLive serves the contacts slice's part of the live tests' control on srv, through svc's sessions: the live
// tests' build only (ADR 0063). A release build never links this file (build/ci.sh).
func RegisterLive(srv grpc.ServiceRegistrar, svc *Service, log zerolog.Logger) {
	livetestv1.RegisterLiveContactServiceServer(srv, &live{svc: svc, log: log})
}

// statuses is the one whatsmeow call the control makes that the contract does not.
type statuses interface {
	SetStatusMessage(ctx context.Context, status types.SetStatusInput) error
}

// iqs sends an info query: whatsmeow's DangerousInternals().SendIQ, the only way to send the legacy status IQ that
// SetStatusMessage no longer sends (#1331, ADR 0067).
type iqs interface {
	SendIQ(ctx context.Context, query whatsmeow.DangerousInfoQuery) (*waBinary.Node, error)
}

var (
	_ statuses = (*whatsmeow.Client)(nil)
	_ iqs      = (*whatsmeow.DangerousInternalClient)(nil)
)

// live implements livetestv1.LiveContactServiceServer.
type live struct {
	livetestv1.UnimplementedLiveContactServiceServer

	svc *Service
	log zerolog.Logger
}

// SetAbout sets the session's account's about text, the text only (user.go:160-172): no emoji and no duration, which no
// read gives back (user.go:257-258), so the live test never sets what it cannot restore (ADR 0063). When WhatsApp
// answers the mutation with a GraphQL error, it sends the legacy status IQ #1331 sends, the text alone, as WhatsApp Web
// does after the mutation (ADR 0067). One info line says which path set it and both outcomes, codes only.
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

	err = setter.SetStatusMessage(ctx, types.SetStatusInput{Text: &text})
	if err == nil {
		l.log.Info().Str("path", "mex").Msg("live control: set the about text")

		return &livetestv1.SetAboutResponse{}, nil
	}

	// Only a refusal: a mutation with no answer may have been made, and its wait used whatsmeow's IQ timeout.
	var graphQL types.GraphQLErrors
	if !errors.As(err, &graphQL) || len(graphQL) == 0 {
		return nil, classify("set the about text", err, true)
	}

	sender, ok := legacy(client)
	if !ok {
		return nil, fmt.Errorf("session %q: its client %T cannot send the legacy about IQ", req.GetSessionId(), client)
	}

	// Never the GraphQL error's message: it may echo the text [unknown] (ADR 0063).
	line := l.log.Info().Int("mex_code", graphQL[0].Extensions.ErrorCode).Str("mex_severity", graphQL[0].Extensions.Severity)

	_, err = sender.SendIQ(ctx, whatsmeow.DangerousInfoQuery{
		Namespace: "status",
		Type:      "set",
		To:        types.ServerJID,
		Content:   []waBinary.Node{{Tag: "status", Content: text}},
	})
	if err != nil {
		// classify keeps an IQ error's code and keyword, without its nodes.
		failed := classify("set the about text by the legacy IQ", err, true)
		line.Str("path", "none").Str("legacy", failed.Error()).Msg("live control: set the about text")

		return nil, failed
	}

	line.Str("path", "legacy").Str("legacy", "answered").Msg("live control: set the about text")

	return &livetestv1.SetAboutResponse{}, nil
}

// legacy is what sends client's legacy status IQ: whatsmeow's internals for a real client, the client itself for a
// test's stub.
func legacy(client Client) (iqs, bool) {
	switch c := client.(type) {
	case *whatsmeow.Client:
		return c.DangerousInternals(), true //nolint:staticcheck // SA1019: the legacy status IQ has no other way out at the pin until #1331 merges (ADR 0067)
	case iqs:
		return c, true
	default:
		return nil, false
	}
}
