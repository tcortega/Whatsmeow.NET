package server

import (
	"context"
	"errors"
	"fmt"
	"testing"

	"github.com/rs/zerolog"
	"google.golang.org/genproto/googleapis/rpc/errdetails"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
)

func TestEachReasonErrorBecomesItsStatus(t *testing.T) {
	t.Parallel()

	cases := []struct {
		err      error
		code     codes.Code
		reason   string
		metadata map[string]string
	}{
		{InvalidArgument("session_id", "too long"), codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": "session_id"}},
		{fmt.Errorf("connect %q: %w", "s1", ErrSessionNotFound), codes.NotFound, "SESSION_NOT_FOUND", nil},
		{ErrNotLoggedIn, codes.FailedPrecondition, "NOT_LOGGED_IN", nil},
		{ErrLoggedOut, codes.FailedPrecondition, "LOGGED_OUT", nil},
		{ErrNotConnected, codes.Unavailable, "NOT_CONNECTED", nil},
		{fmt.Errorf("%w: %w", ErrConnectionFailed, errors.New("dial")), codes.Unavailable, "CONNECTION_FAILED", nil},
		{
			fmt.Errorf("connect: %w", WithMetadata(ErrTemporarilyBanned, "expireTime", "2026-11-01T10:00:00Z")),
			codes.FailedPrecondition, "TEMPORARILY_BANNED",
			map[string]string{"expireTime": "2026-11-01T10:00:00Z"},
		},
		{ErrOutcomeUnknown, codes.Unknown, "OUTCOME_UNKNOWN", nil},
		{WithMetadata(ErrWhatsAppError, "code", "500"), codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "500"}},
		{errors.New("a bug"), codes.Internal, "INTERNAL", nil},
	}

	srv := New(token, zerolog.Nop())

	for _, c := range cases {
		st, _ := status.FromError(srv.toStatus("/test", c.err))
		if st.Code() != c.code || len(st.Details()) != 1 {
			t.Errorf("%v: status %v, want code %v and one ErrorInfo", c.err, st, c.code)

			continue
		}

		info, _ := st.Details()[0].(*errdetails.ErrorInfo)
		if info.GetReason() != c.reason || info.GetDomain() != "whatsmeow.net" || fmt.Sprint(info.GetMetadata()) != fmt.Sprint(c.metadata) {
			t.Errorf("%v: ErrorInfo %v, want %s with %v", c.err, info, c.reason, c.metadata)
		}
	}
}

func TestAContextErrorWithAReasonKeepsTheContextStatus(t *testing.T) {
	t.Parallel()

	st, _ := status.FromError(New(token, zerolog.Nop()).toStatus("/test", fmt.Errorf("%w: %w", ErrOutcomeUnknown, context.Canceled)))
	if st.Code() != codes.Canceled {
		t.Fatalf("code = %v, want %v", st.Code(), codes.Canceled)
	}
}

func TestThePairingReasonsBecomeTheirStatus(t *testing.T) {
	t.Parallel()

	cases := []struct {
		err    error
		code   codes.Code
		reason string
	}{
		{fmt.Errorf("pair %q: %w", "s1", ErrAlreadyPaired), codes.FailedPrecondition, "ALREADY_PAIRED"},
		{ErrPairingInProgress, codes.FailedPrecondition, "PAIRING_IN_PROGRESS"},
		{ErrPairingTimedOut, codes.Aborted, "PAIRING_TIMED_OUT"},
		{fmt.Errorf("%w: passkey-request", ErrPairingFailed), codes.Aborted, "PAIRING_FAILED"},
		{ErrRateLimited, codes.ResourceExhausted, "RATE_LIMITED"},
	}

	srv := New(token, zerolog.Nop())

	for _, c := range cases {
		st, _ := status.FromError(srv.toStatus("/test", c.err))
		if st.Code() != c.code || len(st.Details()) != 1 {
			t.Errorf("%v: status %v, want code %v and one ErrorInfo", c.err, st, c.code)

			continue
		}

		if info, _ := st.Details()[0].(*errdetails.ErrorInfo); info.GetReason() != c.reason || info.GetDomain() != "whatsmeow.net" {
			t.Errorf("%v: ErrorInfo %v, want %s", c.err, info, c.reason)
		}
	}
}
