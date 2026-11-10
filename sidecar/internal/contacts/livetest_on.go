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
// refuses the mutation with a CRITICAL GraphQL error, the refusal #1331 names, it sends the legacy status IQ #1331
// sends, the text alone; #1331 says WhatsApp Web sends this IQ after the mutation (ADR 0067). One info line per set
// says which path set it and each call's outcome, codes only.
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

	line := l.log.Info()
	path, err := setAbout(ctx, client, text, line)
	line.Str("path", path).Msg("live control: set the about text")

	if err != nil {
		return nil, err
	}

	return &livetestv1.SetAboutResponse{}, nil
}

// setAbout sets text through client, the mutation and then, after a CRITICAL refusal, the legacy IQ, and adds each
// call's outcome to line: the mutation's as mex (or its refusal's mex_code and mex_severity), the IQ's as legacy. It
// returns the path that set the text: mex or legacy, none when nothing did, unknown when the last call may have.
// Never a GraphQL error's message: it may echo the text [unknown] (ADR 0063).
func setAbout(ctx context.Context, client Client, text string, line *zerolog.Event) (string, error) {
	setter, ok := client.(statuses)
	if !ok {
		return "none", fmt.Errorf("its client %T cannot set the about text", client)
	}

	err := setter.SetStatusMessage(ctx, types.SetStatusInput{Text: &text})
	if err == nil {
		return "mex", nil
	}

	var graphQL types.GraphQLErrors
	if !errors.As(err, &graphQL) || len(graphQL) == 0 {
		// A mutation with no answer may have been made, and its wait used whatsmeow's IQ timeout: no IQ after it.
		failed := classify("set the about text", err, true)
		line.Str("mex", failed.Error())

		return pathOf(failed), failed
	}

	refusal := graphQL[0].Extensions
	line.Int("mex_code", refusal.ErrorCode).Str("mex_severity", refusal.Severity)

	if refusal.Severity != "CRITICAL" {
		return "none", fmt.Errorf("set the about text: %w", server.WithMetadata(server.ErrWhatsAppError, "code", fmt.Sprint(refusal.ErrorCode)))
	}

	sender, ok := legacy(client)
	if !ok {
		failed := fmt.Errorf("its client %T cannot send the legacy about IQ", client)
		line.Str("legacy", failed.Error())

		return "none", failed
	}

	_, err = sender.SendIQ(ctx, whatsmeow.DangerousInfoQuery{
		Namespace: "status",
		Type:      "set",
		To:        types.ServerJID,
		Content:   []waBinary.Node{{Tag: "status", Content: text}},
	})
	if err != nil {
		// classify keeps an IQ error's code and keyword, without its nodes.
		failed := classify("set the about text by the legacy IQ", err, true)
		line.Str("legacy", failed.Error())

		return pathOf(failed), failed
	}

	line.Str("legacy", "answered")

	return "legacy", nil
}

// pathOf is the path of a set whose last call failed: unknown when that call may have been made, none otherwise.
func pathOf(failed error) string {
	if errors.Is(failed, server.ErrOutcomeUnknown) || errors.Is(failed, context.Canceled) || errors.Is(failed, context.DeadlineExceeded) {
		return "unknown"
	}

	return "none"
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
