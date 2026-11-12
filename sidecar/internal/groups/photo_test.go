package groups

import (
	"bytes"
	"errors"
	"fmt"
	"slices"
	"strings"
	"testing"

	"go.mau.fi/whatsmeow"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// photoHeader is the header of a set of the test group's photo by s1.
func photoHeader() *whatsmeowv1.SetGroupPhotoRequest {
	return &whatsmeowv1.SetGroupPhotoRequest{Part: &whatsmeowv1.SetGroupPhotoRequest_Header_{
		Header: &whatsmeowv1.SetGroupPhotoRequest_Header{SessionId: "s1", Group: group},
	}}
}

func chunk(b []byte) *whatsmeowv1.SetGroupPhotoRequest {
	return &whatsmeowv1.SetGroupPhotoRequest{Part: &whatsmeowv1.SetGroupPhotoRequest_Chunk{Chunk: b}}
}

// setPhoto sends parts, ends the stream and returns the answer.
func setPhoto(t *testing.T, h *harness, parts ...*whatsmeowv1.SetGroupPhotoRequest) (*whatsmeowv1.SetGroupPhotoResponse, error) {
	t.Helper()

	stream, err := h.client.SetGroupPhoto(h.ctx)
	if err != nil {
		t.Fatalf("SetGroupPhoto: %v", err)
	}

	for _, part := range parts {
		if err := stream.Send(part); err != nil {
			break // the sidecar ended the call; its status follows
		}
	}

	return stream.CloseAndRecv() //nolint:wrapcheck // the test reads the status
}

// inChunks is photo in chunks of chunkSize, the last one shorter.
func inChunks(photo []byte) []*whatsmeowv1.SetGroupPhotoRequest {
	var parts []*whatsmeowv1.SetGroupPhotoRequest
	for c := range slices.Chunk(photo, chunkSize) {
		parts = append(parts, chunk(c))
	}

	return parts
}

func TestSetGroupPhotoGivesWhatsmeowTheChunksAsOnePhotoAndAnswersItsID(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.stub.pictureID = "1730000000"
	photo := bytes.Repeat([]byte("\xff\xd8\xff photo "), 20000)

	got, err := setPhoto(t, h, append([]*whatsmeowv1.SetGroupPhotoRequest{photoHeader()}, inChunks(photo)...)...)
	if err != nil {
		t.Fatalf("SetGroupPhoto: %v", err)
	}

	wantProto(t, got, &whatsmeowv1.SetGroupPhotoResponse{PictureId: "1730000000"})
	wantCalls(t, h.stub, fmt.Sprintf("photo %s of %d bytes, removal false", group, len(photo)))

	if !bytes.Equal(h.stub.photo, photo) {
		t.Fatal("whatsmeow got another photo than the chunks")
	}
}

func TestRemoveGroupPhotoSendsNoBytesAndAnswersNoID(t *testing.T) {
	t.Parallel()

	h := start(t)

	got, err := h.client.RemoveGroupPhoto(h.ctx, &whatsmeowv1.RemoveGroupPhotoRequest{SessionId: "s1", Group: group})
	if err != nil {
		t.Fatalf("RemoveGroupPhoto: %v", err)
	}

	wantProto(t, got, &whatsmeowv1.RemoveGroupPhotoResponse{})
	wantCalls(t, h.stub, fmt.Sprintf("photo %s of 0 bytes, removal true", group))
}

func TestAMalformedPhotoCallIsInvalidArgumentAndAsksNothing(t *testing.T) {
	t.Parallel()

	header := func(session, group string) *whatsmeowv1.SetGroupPhotoRequest {
		return &whatsmeowv1.SetGroupPhotoRequest{Part: &whatsmeowv1.SetGroupPhotoRequest_Header_{
			Header: &whatsmeowv1.SetGroupPhotoRequest_Header{SessionId: session, Group: group},
		}}
	}

	for _, tc := range []struct {
		name  string
		parts []*whatsmeowv1.SetGroupPhotoRequest
		field string
	}{
		{"no header", nil, "header"},
		{"a chunk first", []*whatsmeowv1.SetGroupPhotoRequest{chunk([]byte("\xff\xd8\xff"))}, "header"},
		{"a second header", []*whatsmeowv1.SetGroupPhotoRequest{photoHeader(), chunk([]byte("\xff\xd8\xff")), photoHeader()}, "header"},
		// An empty photo is a caller's bug, never a removal (ADR 0070).
		{"no chunk", []*whatsmeowv1.SetGroupPhotoRequest{photoHeader()}, "chunk"},
		{"empty chunks", []*whatsmeowv1.SetGroupPhotoRequest{photoHeader(), chunk(nil), chunk([]byte{})}, "chunk"},
		{"a chunk over 65536 bytes", []*whatsmeowv1.SetGroupPhotoRequest{photoHeader(), chunk(make([]byte, chunkSize+1))}, "chunk"},
		{"no session", []*whatsmeowv1.SetGroupPhotoRequest{header("", group), chunk([]byte("\xff\xd8\xff"))}, "session_id"},
		{"a user for the group", []*whatsmeowv1.SetGroupPhotoRequest{header("s1", "111@lid"), chunk([]byte("\xff\xd8\xff"))}, "group"},
		{"a group with a device", []*whatsmeowv1.SetGroupPhotoRequest{header("s1", "120363777000000001:3@g.us"), chunk([]byte("\xff\xd8\xff"))}, "group"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := start(t)

			_, err := setPhoto(t, h, tc.parts...)
			wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": tc.field})
			wantCalls(t, h.stub)
		})
	}

	h := start(t)
	_, err := h.client.RemoveGroupPhoto(h.ctx, &whatsmeowv1.RemoveGroupPhotoRequest{SessionId: "s1", Group: "111@lid"})
	wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": "group"})
	wantCalls(t, h.stub)
}

func TestAPhotoOverFifteenMiBIsRefusedAsItsChunksArrive(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.stub.pictureID = "1730000000"

	// 15 MiB is the most whatsmeow is given.
	got, err := setPhoto(t, h, append([]*whatsmeowv1.SetGroupPhotoRequest{photoHeader()}, inChunks(make([]byte, 15<<20))...)...)
	if err != nil {
		t.Fatalf("SetGroupPhoto of 15 MiB: %v", err)
	}

	wantProto(t, got, &whatsmeowv1.SetGroupPhotoResponse{PictureId: "1730000000"})

	// One byte more is refused before the client ends the stream: the sidecar answers while it is still open.
	stream, err := h.client.SetGroupPhoto(h.ctx)
	if err != nil {
		t.Fatalf("SetGroupPhoto: %v", err)
	}

	for _, part := range append([]*whatsmeowv1.SetGroupPhotoRequest{photoHeader()}, inChunks(make([]byte, 15<<20+1))...) {
		if err := stream.Send(part); err != nil {
			break
		}
	}

	if _, err := stream.Header(); err != nil {
		t.Fatalf("Header: %v", err)
	}

	_, err = stream.CloseAndRecv()
	wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": "chunk"})
	wantCalls(t, h.stub, fmt.Sprintf("photo %s of %d bytes, removal false", group, 15<<20))
}

func TestAPhotosErrorsBecomeTheirReasons(t *testing.T) {
	t.Parallel()

	// whatsmeow wraps a 406 as ErrInvalidImageFormat, and errors.As still finds the IQError (group.go:341-342,
	// errors.go:158-177).
	refused := fmt.Errorf("%w: %w", whatsmeow.ErrInvalidImageFormat, whatsmeow.ErrIQNotAcceptable)
	// The answer without the picture's id is an untyped error (group.go:349-352).
	noID := errors.New("didn't find picture ID in response") //nolint:err113 // whatsmeow's own

	for _, tc := range []struct {
		name     string
		err      error
		removal  bool
		code     codes.Code
		reason   string
		metadata map[string]string
	}{
		{"an image WhatsApp refused", refused, false, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": "photo"}},
		{"a removal WhatsApp refused with 406", refused, true, codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "406"}},
		{"an answer without the picture's id", noID, false, codes.Unknown, "OUTCOME_UNKNOWN", nil},
		{"a store failure named with %v", namedWithV(), false, codes.Unknown, "OUTCOME_UNKNOWN", nil},
		{"a removal's untyped error, as every group write's", namedWithV(), true, codes.Internal, "INTERNAL", nil},
		{"not an admin of a locked group", &whatsmeow.IQError{Code: 401, Text: "not-authorized"}, false, codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "401"}},
		{"a set that timed out", whatsmeow.ErrIQTimedOut, false, codes.Unknown, "OUTCOME_UNKNOWN", nil},
		{"a removal that timed out", whatsmeow.ErrIQTimedOut, true, codes.Unknown, "OUTCOME_UNKNOWN", nil},
		{"rate limited", whatsmeow.ErrIQRateOverLimit, false, codes.ResourceExhausted, "RATE_LIMITED", nil},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := start(t)
			h.stub.err = tc.err

			var err error
			if tc.removal {
				_, err = h.client.RemoveGroupPhoto(h.ctx, &whatsmeowv1.RemoveGroupPhotoRequest{SessionId: "s1", Group: group})
			} else {
				_, err = setPhoto(t, h, photoHeader(), chunk([]byte("\xff\xd8\xff")))
			}

			wantReason(t, err, tc.code, tc.reason, tc.metadata)

			if msg := status.Convert(err).Message(); strings.Contains(msg, "777000009") {
				t.Fatalf("the message %q carries a JID", msg)
			}
		})
	}
}
