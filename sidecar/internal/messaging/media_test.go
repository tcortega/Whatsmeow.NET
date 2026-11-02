package messaging

import (
	"bytes"
	"strings"
	"testing"
	"time"

	"go.mau.fi/whatsmeow/proto/waE2E"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/durationpb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// ref is an uploaded file of type t, with a streaming sidecar when sidecar is set.
func ref(t whatsmeowv1.MediaType, sidecar []byte) *whatsmeowv1.MediaRef {
	return &whatsmeowv1.MediaRef{
		MediaType: t, DirectPath: "/v/t62.7118-24/1_2_3.enc", Url: "https://mmg.whatsapp.net/v/t62.7118-24/1_2_3.enc?ccb=11-4",
		MediaKey: bytes.Repeat([]byte{1}, 32), FileSha256: bytes.Repeat([]byte{2}, 32), FileEncSha256: bytes.Repeat([]byte{3}, 32),
		FileLength: 1234, StreamingSidecar: sidecar,
	}
}

func (h *harness) sendMedia(n int, media *whatsmeowv1.MediaRef, metadata func(*whatsmeowv1.SendMediaRequest)) (*whatsmeowv1.SendMediaResponse, error) {
	req := &whatsmeowv1.SendMediaRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: id(n), Media: media}
	if metadata != nil {
		metadata(req)
	}

	return h.client.SendMedia(h.ctx, req) //nolint:wrapcheck // the test reads the status
}

func image(m *whatsmeowv1.ImageMetadata) func(*whatsmeowv1.SendMediaRequest) {
	return func(r *whatsmeowv1.SendMediaRequest) { r.Metadata = &whatsmeowv1.SendMediaRequest_Image{Image: m} }
}

func video(m *whatsmeowv1.VideoMetadata) func(*whatsmeowv1.SendMediaRequest) {
	return func(r *whatsmeowv1.SendMediaRequest) { r.Metadata = &whatsmeowv1.SendMediaRequest_Video{Video: m} }
}

func audio(m *whatsmeowv1.AudioMetadata) func(*whatsmeowv1.SendMediaRequest) {
	return func(r *whatsmeowv1.SendMediaRequest) { r.Metadata = &whatsmeowv1.SendMediaRequest_Audio{Audio: m} }
}

func document(m *whatsmeowv1.DocumentMetadata) func(*whatsmeowv1.SendMediaRequest) {
	return func(r *whatsmeowv1.SendMediaRequest) {
		r.Metadata = &whatsmeowv1.SendMediaRequest_Document{Document: m}
	}
}

func sticker(m *whatsmeowv1.StickerMetadata) func(*whatsmeowv1.SendMediaRequest) {
	return func(r *whatsmeowv1.SendMediaRequest) { r.Metadata = &whatsmeowv1.SendMediaRequest_Sticker{Sticker: m} }
}

// The message shapes are mautrix-whatsapp's (pkg/msgconv/from-matrix.go:220-340 at 5dd621f), with the MediaRef's
// streaming sidecar for video and audio (RESEARCH "Media should travel").
func TestSendMediaBuildsEachKindFromTheRefAndTheMetadata(t *testing.T) {
	t.Parallel()

	h := start(t)
	url, path := proto.String("https://mmg.whatsapp.net/v/t62.7118-24/1_2_3.enc?ccb=11-4"), proto.String("/v/t62.7118-24/1_2_3.enc")
	key, sum, encSum, length := bytes.Repeat([]byte{1}, 32), bytes.Repeat([]byte{2}, 32), bytes.Repeat([]byte{3}, 32), proto.Uint64(1234)
	thumb, waveform, sidecar := []byte{0xFF, 0xD8, 0xFF}, []byte{0, 50, 100, 50}, []byte("sidecar")

	cases := []struct {
		name     string
		media    *whatsmeowv1.MediaRef
		metadata func(*whatsmeowv1.SendMediaRequest)
		want     *waE2E.Message
	}{
		{
			"an image", ref(whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE, nil),
			image(&whatsmeowv1.ImageMetadata{Mimetype: "image/jpeg", Caption: "look", Width: proto.Uint32(640), Height: proto.Uint32(480), JpegThumbnail: thumb}),
			&waE2E.Message{ImageMessage: &waE2E.ImageMessage{
				URL: url, DirectPath: path, MediaKey: key, FileSHA256: sum, FileEncSHA256: encSum, FileLength: length,
				Mimetype: proto.String("image/jpeg"), Caption: proto.String("look"), Width: proto.Uint32(640), Height: proto.Uint32(480), JPEGThumbnail: thumb,
			}},
		},
		{
			"an image with no metadata and no URL, as after a media retry",
			func() *whatsmeowv1.MediaRef {
				r := ref(whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE, nil)
				r.Url = ""
				return r
			}(),
			image(&whatsmeowv1.ImageMetadata{}),
			&waE2E.Message{ImageMessage: &waE2E.ImageMessage{DirectPath: path, MediaKey: key, FileSHA256: sum, FileEncSHA256: encSum, FileLength: length}},
		},
		{
			"a video, with its streaming sidecar and its length in whole seconds", ref(whatsmeowv1.MediaType_MEDIA_TYPE_VIDEO, sidecar),
			video(&whatsmeowv1.VideoMetadata{
				Mimetype: "video/mp4", Caption: "watch", Width: proto.Uint32(1280), Height: proto.Uint32(720),
				Duration: durationpb.New(12*time.Second + 900*time.Millisecond), JpegThumbnail: thumb,
			}),
			&waE2E.Message{VideoMessage: &waE2E.VideoMessage{
				URL: url, DirectPath: path, MediaKey: key, FileSHA256: sum, FileEncSHA256: encSum, FileLength: length, StreamingSidecar: sidecar,
				Mimetype: proto.String("video/mp4"), Caption: proto.String("watch"), Width: proto.Uint32(1280), Height: proto.Uint32(720),
				Seconds: proto.Uint32(12), JPEGThumbnail: thumb,
			}},
		},
		{
			"a voice note", ref(whatsmeowv1.MediaType_MEDIA_TYPE_AUDIO, nil),
			audio(&whatsmeowv1.AudioMetadata{Mimetype: "audio/ogg; codecs=opus", Duration: durationpb.New(3 * time.Second), VoiceNote: true, Waveform: waveform}),
			&waE2E.Message{AudioMessage: &waE2E.AudioMessage{
				URL: url, DirectPath: path, MediaKey: key, FileSHA256: sum, FileEncSHA256: encSum, FileLength: length,
				Mimetype: proto.String("audio/ogg; codecs=opus"), Seconds: proto.Uint32(3), PTT: proto.Bool(true), Waveform: waveform,
			}},
		},
		{
			"an audio file, PTT false as mautrix sends it, and a streaming sidecar a received audio carried",
			ref(whatsmeowv1.MediaType_MEDIA_TYPE_AUDIO, sidecar),
			audio(&whatsmeowv1.AudioMetadata{Mimetype: "audio/mpeg"}),
			&waE2E.Message{AudioMessage: &waE2E.AudioMessage{
				URL: url, DirectPath: path, MediaKey: key, FileSHA256: sum, FileEncSHA256: encSum, FileLength: length, StreamingSidecar: sidecar,
				Mimetype: proto.String("audio/mpeg"), PTT: proto.Bool(false),
			}},
		},
		{
			"a document without a caption", ref(whatsmeowv1.MediaType_MEDIA_TYPE_DOCUMENT, nil),
			document(&whatsmeowv1.DocumentMetadata{Mimetype: "application/pdf", FileName: "report.pdf", JpegThumbnail: thumb}),
			&waE2E.Message{DocumentMessage: &waE2E.DocumentMessage{
				URL: url, DirectPath: path, MediaKey: key, FileSHA256: sum, FileEncSHA256: encSum, FileLength: length,
				Mimetype: proto.String("application/pdf"), FileName: proto.String("report.pdf"), JPEGThumbnail: thumb,
			}},
		},
		{
			"a document with a caption, wrapped so the recipient sees both", ref(whatsmeowv1.MediaType_MEDIA_TYPE_DOCUMENT, nil),
			document(&whatsmeowv1.DocumentMetadata{Mimetype: "application/pdf", FileName: "report.pdf", Caption: "the numbers"}),
			&waE2E.Message{DocumentWithCaptionMessage: &waE2E.FutureProofMessage{Message: &waE2E.Message{DocumentMessage: &waE2E.DocumentMessage{
				URL: url, DirectPath: path, MediaKey: key, FileSHA256: sum, FileEncSHA256: encSum, FileLength: length,
				Mimetype: proto.String("application/pdf"), FileName: proto.String("report.pdf"), Caption: proto.String("the numbers"),
			}}}},
		},
		{
			"a sticker, with a PNG thumbnail", ref(whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE, nil),
			sticker(&whatsmeowv1.StickerMetadata{Mimetype: "image/webp", Width: proto.Uint32(512), Height: proto.Uint32(512), PngThumbnail: []byte{0x89, 'P', 'N', 'G'}}),
			&waE2E.Message{StickerMessage: &waE2E.StickerMessage{
				URL: url, DirectPath: path, MediaKey: key, FileSHA256: sum, FileEncSHA256: encSum, FileLength: length,
				Mimetype: proto.String("image/webp"), Width: proto.Uint32(512), Height: proto.Uint32(512), PngThumbnail: []byte{0x89, 'P', 'N', 'G'},
			}},
		},
	}

	for n, c := range cases {
		resp, err := h.sendMedia(n, c.media, c.metadata)
		if err != nil || resp.GetChat() != "111@lid" || !resp.GetMessageTime().AsTime().Equal(at) {
			t.Fatalf("%s: SendMedia = %v, %v; want 111@lid at %v", c.name, resp, err, at)
		}

		if got := h.next(); got.id != id(n) || !proto.Equal(got.message, c.want) {
			t.Errorf("%s: whatsmeow got %v as %s, want %v", c.name, got.message, got.id, c.want)
		}
	}
}

func TestAMalformedMediaSendIsInvalidAndSendsNothing(t *testing.T) {
	t.Parallel()

	h := start(t)
	img := image(&whatsmeowv1.ImageMetadata{})
	with := func(change func(*whatsmeowv1.MediaRef)) *whatsmeowv1.MediaRef {
		r := ref(whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE, nil)
		change(r)

		return r
	}
	typed := func(t whatsmeowv1.MediaType) *whatsmeowv1.MediaRef { return ref(t, nil) }
	send := func(media *whatsmeowv1.MediaRef, metadata func(*whatsmeowv1.SendMediaRequest)) error {
		_, err := h.sendMedia(1, media, metadata)

		return err
	}

	cases := []struct {
		err   error
		field string
	}{
		{send(nil, img), "media"},
		{send(with(func(r *whatsmeowv1.MediaRef) { r.DirectPath = "v/t62/1.enc" }), img), "media"},
		{send(with(func(r *whatsmeowv1.MediaRef) { r.MediaKey = r.MediaKey[:31] }), img), "media"},
		{send(with(func(r *whatsmeowv1.MediaRef) { r.FileSha256 = nil }), img), "media"},
		{send(with(func(r *whatsmeowv1.MediaRef) { r.FileEncSha256 = r.FileEncSha256[:16] }), img), "media"},
		{send(typed(whatsmeowv1.MediaType_MEDIA_TYPE_UNSPECIFIED), img), "media.media_type"},
		{send(typed(whatsmeowv1.MediaType_MEDIA_TYPE_VIDEO), img), "media.media_type"},
		{send(typed(whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE), video(&whatsmeowv1.VideoMetadata{})), "media.media_type"},
		{send(typed(whatsmeowv1.MediaType_MEDIA_TYPE_DOCUMENT), audio(&whatsmeowv1.AudioMetadata{})), "media.media_type"},
		{send(typed(whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE), document(&whatsmeowv1.DocumentMetadata{})), "media.media_type"},
		// A sticker is encrypted with the image keys (download.go:95-106).
		{send(typed(whatsmeowv1.MediaType_MEDIA_TYPE_DOCUMENT), sticker(&whatsmeowv1.StickerMetadata{})), "media.media_type"},
		{send(typed(whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE), nil), "metadata"},
		{send(typed(whatsmeowv1.MediaType_MEDIA_TYPE_VIDEO), video(&whatsmeowv1.VideoMetadata{Duration: durationpb.New(-time.Second)})), "video.duration"},
		{send(typed(whatsmeowv1.MediaType_MEDIA_TYPE_AUDIO), audio(&whatsmeowv1.AudioMetadata{Duration: durationpb.New(1 << 32 * time.Second)})), "audio.duration"},
		{send(typed(whatsmeowv1.MediaType_MEDIA_TYPE_AUDIO), audio(&whatsmeowv1.AudioMetadata{Duration: &durationpb.Duration{Seconds: 1, Nanos: -1}})), "audio.duration"},
	}

	for n, c := range cases {
		wantReason(t, c.err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": c.field})

		// The texts name the field, never the chat.
		if strings.Contains(status.Convert(c.err).Message(), "@") {
			t.Errorf("case %d: %q carries a JID", n, status.Convert(c.err).Message())
		}
	}

	// The send's own rules hold too.
	_, err := h.client.SendMedia(h.ctx, &whatsmeowv1.SendMediaRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: "1", Media: ref(whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE, nil), Metadata: &whatsmeowv1.SendMediaRequest_Image{Image: &whatsmeowv1.ImageMetadata{}}})
	wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": "client_message_id"})

	if exists, _, _, _ := h.record(1); exists || len(h.stub.sent()) != 0 {
		t.Fatal("a malformed media send left a record or reached whatsmeow")
	}
}

func TestMediaAndTextSendsShareTheQueueInOrder(t *testing.T) {
	t.Parallel()

	h := start(t)
	release := h.hold()

	first := h.async(h.ctx, 1)
	h.next()

	media := make(chan error, 1)

	go func() {
		_, err := h.sendMedia(2, ref(whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE, nil), image(&whatsmeowv1.ImageMetadata{}))
		media <- err
	}()

	h.wait(h.queued, "the media send queued")

	third := h.async(h.ctx, 3)
	h.wait(h.queued, "the third queued")
	close(release)

	if r := h.outcome(first); r.err != nil {
		t.Fatalf("the first text failed: %v", r.err)
	}

	select {
	case err := <-media:
		if err != nil {
			t.Fatalf("the media send failed: %v", err)
		}
	case <-h.ctx.Done():
		t.Fatal("the media send did not return")
	}

	if r := h.outcome(third); r.err != nil {
		t.Fatalf("the third text failed: %v", r.err)
	}

	if sent := h.stub.sent(); len(sent) != 3 || sent[0].id != id(1) || sent[1].message.GetImageMessage() == nil || sent[1].id != id(2) || sent[2].id != id(3) {
		t.Fatalf("whatsmeow got %v, want the text, the image and the text in order", h.stub.ids())
	}
}

func TestARepeatOfAMediaIdIsAnsweredFromItsRecordWhateverItsKind(t *testing.T) {
	t.Parallel()

	h := start(t)

	first, err := h.sendMedia(1, ref(whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE, nil), image(&whatsmeowv1.ImageMetadata{}))
	if err != nil {
		t.Fatalf("SendMedia: %v", err)
	}

	again, err := h.sendMedia(1, ref(whatsmeowv1.MediaType_MEDIA_TYPE_VIDEO, nil), video(&whatsmeowv1.VideoMetadata{}))
	if err != nil || !proto.Equal(again, first) {
		t.Fatalf("the media repeat = %v, %v; want %v", again, err, first)
	}

	text, err := h.sendText(h.ctx, 1, "hi")
	if err != nil || text.GetChat() != first.GetChat() || !proto.Equal(text.GetMessageTime(), first.GetMessageTime()) {
		t.Fatalf("the text repeat = %v, %v; want %v", text, err, first)
	}

	if sent := h.stub.sent(); len(sent) != 1 {
		t.Fatalf("whatsmeow got %v, want one send", h.stub.ids())
	}
}
