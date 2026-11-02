package server

import (
	"errors"
	"fmt"

	"google.golang.org/grpc/codes"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// The errors a slice wraps with %w to fail a call with an ErrorInfo reason (error.proto). They live here, in core,
// because a reason is shared across slices and core never imports a slice (ARCHITECTURE §3). An error that wraps
// none of them is INTERNAL.
var (
	ErrInvalidArgument   = errors.New("invalid argument")
	ErrSessionNotFound   = errors.New("no session has this id")
	ErrNotLoggedIn       = errors.New("the session has never been paired")
	ErrLoggedOut         = errors.New("the session was logged out")
	ErrNotConnected      = errors.New("the session is not connected")
	ErrConnectionFailed  = errors.New("connecting to WhatsApp failed")
	ErrTemporarilyBanned = errors.New("the account is temporarily banned")
	ErrOutcomeUnknown    = errors.New("the outcome is unknown")
	ErrWhatsAppError     = errors.New("WhatsApp answered with an error")
	ErrAlreadyPaired     = errors.New("the session is already paired")
	ErrPairingInProgress = errors.New("the session is being paired")
	ErrPairingTimedOut   = errors.New("no pairing code was used in time")
	ErrPairingFailed     = errors.New("pairing failed")
	ErrRateLimited       = errors.New("WhatsApp rate limited the request")
	ErrConsumerReplaced  = errors.New("a newer stream for the same consumer replaced this one")
	// ErrRecipientNotOnWhatsApp is a user WhatsApp does not know: nothing was done.
	ErrRecipientNotOnWhatsApp = errors.New("the recipient is not on WhatsApp")
	// ErrWhatsAppTimedOut is a read WhatsApp did not answer in time: nothing was done. Never for a write, which is
	// ErrOutcomeUnknown.
	ErrWhatsAppTimedOut = errors.New("WhatsApp did not answer in time")
	// ErrNoPushName is SendPresence before the app state sync set the account's push name: nothing was sent.
	ErrNoPushName = errors.New("the account has no push name yet")
	// ErrMediaExpired is media WhatsApp no longer has (403, 404 or 410 on a download).
	ErrMediaExpired = errors.New("WhatsApp no longer has the media")
	// ErrInviteLinkInvalid is a group invite link WhatsApp does not accept: malformed, unknown or reset.
	ErrInviteLinkInvalid = errors.New("the group invite link is not valid")
)

// reasons is each error's gRPC code and reason, as error.proto lists them.
var reasons = []struct { //nolint:gochecknoglobals // a read-only table, the one mapping from slice errors to reasons
	err    error
	code   codes.Code
	reason whatsmeowv1.ErrorReason
}{
	{ErrInvalidArgument, codes.InvalidArgument, whatsmeowv1.ErrorReason_ERROR_REASON_INVALID_ARGUMENT},
	{ErrSessionNotFound, codes.NotFound, whatsmeowv1.ErrorReason_ERROR_REASON_SESSION_NOT_FOUND},
	{ErrNotLoggedIn, codes.FailedPrecondition, whatsmeowv1.ErrorReason_ERROR_REASON_NOT_LOGGED_IN},
	{ErrLoggedOut, codes.FailedPrecondition, whatsmeowv1.ErrorReason_ERROR_REASON_LOGGED_OUT},
	{ErrNotConnected, codes.Unavailable, whatsmeowv1.ErrorReason_ERROR_REASON_NOT_CONNECTED},
	{ErrConnectionFailed, codes.Unavailable, whatsmeowv1.ErrorReason_ERROR_REASON_CONNECTION_FAILED},
	{ErrTemporarilyBanned, codes.FailedPrecondition, whatsmeowv1.ErrorReason_ERROR_REASON_TEMPORARILY_BANNED},
	{ErrOutcomeUnknown, codes.Unknown, whatsmeowv1.ErrorReason_ERROR_REASON_OUTCOME_UNKNOWN},
	{ErrWhatsAppError, codes.FailedPrecondition, whatsmeowv1.ErrorReason_ERROR_REASON_WHATSAPP_ERROR},
	{ErrAlreadyPaired, codes.FailedPrecondition, whatsmeowv1.ErrorReason_ERROR_REASON_ALREADY_PAIRED},
	{ErrPairingInProgress, codes.FailedPrecondition, whatsmeowv1.ErrorReason_ERROR_REASON_PAIRING_IN_PROGRESS},
	{ErrPairingTimedOut, codes.Aborted, whatsmeowv1.ErrorReason_ERROR_REASON_PAIRING_TIMED_OUT},
	{ErrPairingFailed, codes.Aborted, whatsmeowv1.ErrorReason_ERROR_REASON_PAIRING_FAILED},
	{ErrRateLimited, codes.ResourceExhausted, whatsmeowv1.ErrorReason_ERROR_REASON_RATE_LIMITED},
	{ErrConsumerReplaced, codes.Aborted, whatsmeowv1.ErrorReason_ERROR_REASON_CONSUMER_REPLACED},
	{ErrRecipientNotOnWhatsApp, codes.NotFound, whatsmeowv1.ErrorReason_ERROR_REASON_RECIPIENT_NOT_ON_WHATSAPP},
	{ErrWhatsAppTimedOut, codes.Unavailable, whatsmeowv1.ErrorReason_ERROR_REASON_WHATSAPP_TIMED_OUT},
	{ErrNoPushName, codes.FailedPrecondition, whatsmeowv1.ErrorReason_ERROR_REASON_NO_PUSH_NAME},
	{ErrMediaExpired, codes.NotFound, whatsmeowv1.ErrorReason_ERROR_REASON_MEDIA_EXPIRED},
	{ErrInviteLinkInvalid, codes.NotFound, whatsmeowv1.ErrorReason_ERROR_REASON_INVITE_LINK_INVALID},
}

// metadataError adds one ErrorInfo metadata entry to the error it wraps.
type metadataError struct {
	err        error
	key, value string
}

func (e *metadataError) Error() string { return e.err.Error() }

func (e *metadataError) Unwrap() error { return e.err }

// WithMetadata adds the ErrorInfo metadata entry key (error.proto names the keys each reason sets) to err.
func WithMetadata(err error, key, value string) error {
	return &metadataError{err: err, key: key, value: value}
}

// InvalidArgument is ErrInvalidArgument for the request field field.
func InvalidArgument(field, format string, args ...any) error {
	return WithMetadata(fmt.Errorf("%w: %s", ErrInvalidArgument, fmt.Sprintf(format, args...)), "field", field)
}

// reasonOf finds the reason err wraps, and the metadata added to it.
func reasonOf(err error) (codes.Code, whatsmeowv1.ErrorReason, map[string]string, bool) {
	for _, r := range reasons {
		if !errors.Is(err, r.err) {
			continue
		}

		metadata := map[string]string{}
		collectMetadata(err, metadata)

		if len(metadata) == 0 {
			metadata = nil
		}

		return r.code, r.reason, metadata, true
	}

	return codes.OK, whatsmeowv1.ErrorReason_ERROR_REASON_UNSPECIFIED, nil, false
}

// collectMetadata adds every metadataError in err's tree to metadata.
func collectMetadata(err error, metadata map[string]string) {
	switch e := err.(type) { //nolint:errorlint // walks the tree itself, to collect every entry
	case *metadataError:
		metadata[e.key] = e.value
		collectMetadata(e.err, metadata)
	case interface{ Unwrap() []error }:
		for _, inner := range e.Unwrap() {
			collectMetadata(inner, metadata)
		}
	case interface{ Unwrap() error }:
		collectMetadata(e.Unwrap(), metadata)
	}
}
