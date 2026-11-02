package server

import (
	"fmt"
	"testing"

	"github.com/rs/zerolog"
	"google.golang.org/genproto/googleapis/rpc/errdetails"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
)

func TestRecipientNotOnWhatsAppBecomesItsStatus(t *testing.T) {
	t.Parallel()

	st, _ := status.FromError(New(token, zerolog.Nop()).toStatus("/test", fmt.Errorf("block: %w", ErrRecipientNotOnWhatsApp)))
	if st.Code() != codes.NotFound || len(st.Details()) != 1 {
		t.Fatalf("status %v, want NOT_FOUND with one ErrorInfo", st)
	}

	if info, _ := st.Details()[0].(*errdetails.ErrorInfo); info.GetReason() != "RECIPIENT_NOT_ON_WHATSAPP" {
		t.Fatalf("ErrorInfo %v, want RECIPIENT_NOT_ON_WHATSAPP", info)
	}
}
