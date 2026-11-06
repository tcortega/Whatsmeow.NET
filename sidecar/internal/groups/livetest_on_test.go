//go:build livetest

package groups

import (
	"context"
	"os"
	"path/filepath"
	"testing"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/types"
	"google.golang.org/grpc/codes"

	livetestv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/livetest/v1"
)

// photoStub is the stub with the photo call the live control makes; what it got is kept for the test.
type photoStub struct {
	*stub

	id      string
	err     error
	group   types.JID
	avatars [][]byte
}

func (s *photoStub) SetGroupPhoto(_ context.Context, jid types.JID, avatar []byte) (string, error) {
	s.group = jid
	s.avatars = append(s.avatars, avatar)

	if avatar == nil && s.err == nil {
		return "remove", nil
	}

	return s.id, s.err
}

func startLive(t *testing.T) (*harness, *photoStub, livetestv1.LiveTestServiceClient) {
	t.Helper()

	h := start(t)
	photos := &photoStub{stub: h.stub, id: "1730000000"}
	h.sessions.client = photos

	return h, photos, livetestv1.NewLiveTestServiceClient(h.conn)
}

func TestTheLiveControlSetsAGroupPhotoFromTheFileAndAnswersItsID(t *testing.T) {
	t.Parallel()

	h, photos, live := startLive(t)
	path := filepath.Join(t.TempDir(), "photo.jpg")

	if err := os.WriteFile(path, []byte("\xff\xd8jpeg"), 0o600); err != nil {
		t.Fatal(err)
	}

	got, err := live.SetGroupPhoto(h.ctx, &livetestv1.SetGroupPhotoRequest{SessionId: "s1", Group: group, JpegPath: path})
	if err != nil {
		t.Fatalf("SetGroupPhoto: %v", err)
	}

	wantProto(t, got, &livetestv1.SetGroupPhotoResponse{PictureId: "1730000000"})

	if photos.group != jid(group) || len(photos.avatars) != 1 || string(photos.avatars[0]) != "\xff\xd8jpeg" {
		t.Fatalf("whatsmeow got %v with %q; want the group with the file's bytes", photos.group, photos.avatars)
	}
}

func TestTheLiveControlRemovesAGroupPhotoWithNoPathAndAnswersNoID(t *testing.T) {
	t.Parallel()

	h, photos, live := startLive(t)

	got, err := live.SetGroupPhoto(h.ctx, &livetestv1.SetGroupPhotoRequest{SessionId: "s1", Group: group})
	if err != nil {
		t.Fatalf("SetGroupPhoto: %v", err)
	}

	wantProto(t, got, &livetestv1.SetGroupPhotoResponse{})

	if len(photos.avatars) != 1 || photos.avatars[0] != nil {
		t.Fatalf("whatsmeow got %q; want one removal", photos.avatars)
	}
}

func TestTheLiveControlRefusesABadRequestBeforeWhatsmeow(t *testing.T) {
	t.Parallel()

	h, photos, live := startLive(t)
	empty := filepath.Join(t.TempDir(), "empty.jpg")

	if err := os.WriteFile(empty, nil, 0o600); err != nil {
		t.Fatal(err)
	}

	for _, c := range []struct {
		req   *livetestv1.SetGroupPhotoRequest
		field string
	}{
		{&livetestv1.SetGroupPhotoRequest{SessionId: "", Group: group}, "session_id"},
		{&livetestv1.SetGroupPhotoRequest{SessionId: "s1", Group: "111@lid"}, "group"},
		{&livetestv1.SetGroupPhotoRequest{SessionId: "s1", Group: group, JpegPath: "photo.jpg"}, "jpeg_path"},
		{&livetestv1.SetGroupPhotoRequest{SessionId: "s1", Group: group, JpegPath: filepath.Join(t.TempDir(), "none.jpg")}, "jpeg_path"},
		{&livetestv1.SetGroupPhotoRequest{SessionId: "s1", Group: group, JpegPath: empty}, "jpeg_path"},
	} {
		_, err := live.SetGroupPhoto(h.ctx, c.req)
		wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": c.field})
	}

	if len(photos.avatars) != 0 {
		t.Fatalf("whatsmeow got %d calls; want none", len(photos.avatars))
	}
}

func TestTheLiveControlGivesWhatsAppsRefusalItsReason(t *testing.T) {
	t.Parallel()

	h, photos, live := startLive(t)
	photos.err = &whatsmeow.IQError{Code: 429, Text: "rate-overlimit"}

	_, err := live.SetGroupPhoto(h.ctx, &livetestv1.SetGroupPhotoRequest{SessionId: "s1", Group: group})
	wantReason(t, err, codes.ResourceExhausted, "RATE_LIMITED", nil)

	photos.err = whatsmeow.ErrIQTimedOut
	_, err = live.SetGroupPhoto(h.ctx, &livetestv1.SetGroupPhotoRequest{SessionId: "s1", Group: group})
	wantReason(t, err, codes.Unknown, "OUTCOME_UNKNOWN", nil)
}
