package messaging

import (
	"context"
	"math"
	"strings"

	"go.mau.fi/whatsmeow/proto/waE2E"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/durationpb"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// SendMedia sends an image, video, audio, document or sticker message of a MediaRef (message.proto), through the same
// queue and send records as a text.
//
// The message is built as mautrix-whatsapp builds it (pkg/msgconv/from-matrix.go:220-340 at 5dd621f): the MediaRef's
// fields, the caller's metadata unchanged, PTT always set, and a captioned document wrapped in a
// DocumentWithCaptionMessage. A video's or an audio's streaming sidecar goes in too, which mautrix leaves out (RESEARCH
// "Media should travel"). Empty metadata is not sent.
func (svc *Service) SendMedia(ctx context.Context, req *whatsmeowv1.SendMediaRequest) (*whatsmeowv1.SendMediaResponse, error) {
	message, err := mediaMessage(req)
	if err != nil {
		return nil, err
	}

	info, err := svc.contextInfo(req.GetSessionId(), req.GetReplyTo(), req.GetMentions())
	if err != nil {
		return nil, err
	}

	withContext(message, info)

	sent, err := svc.send(ctx, req.GetSessionId(), req.GetChat(), req.GetClientMessageId(), as(message))
	if err != nil {
		return nil, err
	}

	return &whatsmeowv1.SendMediaResponse{Chat: sent.chat, MessageTime: timestamppb.New(sent.at)}, nil
}

// mediaMessage is the message of req, or INVALID_ARGUMENT.
func mediaMessage(req *whatsmeowv1.SendMediaRequest) (*waE2E.Message, error) {
	media := req.GetMedia()
	if err := checkMedia(media); err != nil {
		return nil, err
	}

	want, err := mediaTypeOf(req)
	if err != nil {
		return nil, err
	}

	if media.GetMediaType() != want {
		return nil, invalid("media.media_type", "%v; this kind of message takes %v", media.GetMediaType(), want)
	}

	switch m := req.GetMetadata().(type) {
	case *whatsmeowv1.SendMediaRequest_Image:
		return &waE2E.Message{ImageMessage: &waE2E.ImageMessage{
			URL: text(media.GetUrl()), DirectPath: proto.String(media.GetDirectPath()), MediaKey: media.GetMediaKey(),
			FileSHA256: media.GetFileSha256(), FileEncSHA256: media.GetFileEncSha256(), FileLength: proto.Uint64(media.GetFileLength()),
			Mimetype: text(m.Image.GetMimetype()), Caption: text(m.Image.GetCaption()), Width: m.Image.Width, Height: m.Image.Height,
			JPEGThumbnail: bytesOrNil(m.Image.GetJpegThumbnail()),
		}}, nil
	case *whatsmeowv1.SendMediaRequest_Video:
		seconds, err := seconds("video.duration", m.Video.GetDuration())
		if err != nil {
			return nil, err
		}

		return &waE2E.Message{VideoMessage: &waE2E.VideoMessage{
			URL: text(media.GetUrl()), DirectPath: proto.String(media.GetDirectPath()), MediaKey: media.GetMediaKey(),
			FileSHA256: media.GetFileSha256(), FileEncSHA256: media.GetFileEncSha256(), FileLength: proto.Uint64(media.GetFileLength()),
			StreamingSidecar: bytesOrNil(media.GetStreamingSidecar()),
			Mimetype:         text(m.Video.GetMimetype()), Caption: text(m.Video.GetCaption()), Width: m.Video.Width, Height: m.Video.Height,
			Seconds: seconds, JPEGThumbnail: bytesOrNil(m.Video.GetJpegThumbnail()),
		}}, nil
	case *whatsmeowv1.SendMediaRequest_Audio:
		seconds, err := seconds("audio.duration", m.Audio.GetDuration())
		if err != nil {
			return nil, err
		}

		// PTT is always set, false too, as mautrix sets it (from-matrix.go:249); whether a receiver tells an unset one
		// from false is unknown.
		return &waE2E.Message{AudioMessage: &waE2E.AudioMessage{
			URL: text(media.GetUrl()), DirectPath: proto.String(media.GetDirectPath()), MediaKey: media.GetMediaKey(),
			FileSHA256: media.GetFileSha256(), FileEncSHA256: media.GetFileEncSha256(), FileLength: proto.Uint64(media.GetFileLength()),
			StreamingSidecar: bytesOrNil(media.GetStreamingSidecar()),
			Mimetype:         text(m.Audio.GetMimetype()), Seconds: seconds, PTT: proto.Bool(m.Audio.GetVoiceNote()),
			Waveform: bytesOrNil(m.Audio.GetWaveform()),
		}}, nil
	case *whatsmeowv1.SendMediaRequest_Document:
		document := &waE2E.DocumentMessage{
			URL: text(media.GetUrl()), DirectPath: proto.String(media.GetDirectPath()), MediaKey: media.GetMediaKey(),
			FileSHA256: media.GetFileSha256(), FileEncSHA256: media.GetFileEncSha256(), FileLength: proto.Uint64(media.GetFileLength()),
			Mimetype: text(m.Document.GetMimetype()), FileName: text(m.Document.GetFileName()), Caption: text(m.Document.GetCaption()),
			JPEGThumbnail: bytesOrNil(m.Document.GetJpegThumbnail()),
		}
		if document.Caption == nil {
			return &waE2E.Message{DocumentMessage: document}, nil
		}

		// A caption shows only on a wrapped document (from-matrix.go:330-337).
		return &waE2E.Message{DocumentWithCaptionMessage: &waE2E.FutureProofMessage{Message: &waE2E.Message{DocumentMessage: document}}}, nil
	default:
		s := req.GetSticker()

		return &waE2E.Message{StickerMessage: &waE2E.StickerMessage{
			URL: text(media.GetUrl()), DirectPath: proto.String(media.GetDirectPath()), MediaKey: media.GetMediaKey(),
			FileSHA256: media.GetFileSha256(), FileEncSHA256: media.GetFileEncSha256(), FileLength: proto.Uint64(media.GetFileLength()),
			Mimetype: text(s.GetMimetype()), Width: s.Width, Height: s.Height, PngThumbnail: bytesOrNil(s.GetPngThumbnail()),
		}}, nil
	}
}

// checkMedia checks that media has what the recipient needs to download it (download-to-file.go:80-98), as the
// media slice checks a download.
func checkMedia(media *whatsmeowv1.MediaRef) error {
	switch {
	case media == nil:
		return invalid("media", "no media")
	case !strings.HasPrefix(media.GetDirectPath(), "/"):
		return invalid("media", "the direct path does not start with /")
	case len(media.GetMediaKey()) != 32:
		return invalid("media", "a media key of %d bytes; want 32", len(media.GetMediaKey()))
	case len(media.GetFileSha256()) != 32, len(media.GetFileEncSha256()) != 32:
		return invalid("media", "the file hashes must be 32 bytes each")
	default:
		return nil
	}
}

// mediaTypeOf is the key type the kind of req is encrypted with: a sticker takes the image keys (download.go:95-106).
func mediaTypeOf(req *whatsmeowv1.SendMediaRequest) (whatsmeowv1.MediaType, error) {
	switch req.GetMetadata().(type) {
	case *whatsmeowv1.SendMediaRequest_Image, *whatsmeowv1.SendMediaRequest_Sticker:
		return whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE, nil
	case *whatsmeowv1.SendMediaRequest_Video:
		return whatsmeowv1.MediaType_MEDIA_TYPE_VIDEO, nil
	case *whatsmeowv1.SendMediaRequest_Audio:
		return whatsmeowv1.MediaType_MEDIA_TYPE_AUDIO, nil
	case *whatsmeowv1.SendMediaRequest_Document:
		return whatsmeowv1.MediaType_MEDIA_TYPE_DOCUMENT, nil
	default:
		return whatsmeowv1.MediaType_MEDIA_TYPE_UNSPECIFIED, invalid("metadata", "no metadata: say which kind of message it is")
	}
}

// seconds is d in whole seconds, the fraction dropped as mautrix drops it (from-matrix.go:285); nil when unset.
func seconds(field string, d *durationpb.Duration) (*uint32, error) {
	if d == nil {
		return nil, nil //nolint:nilnil // no duration is not sent
	}

	if err := d.CheckValid(); err != nil || d.GetSeconds() < 0 || d.GetNanos() < 0 || d.GetSeconds() > math.MaxUint32 {
		return nil, invalid(field, "a duration from 0 to %d seconds", uint32(math.MaxUint32))
	}

	return proto.Uint32(uint32(d.GetSeconds())), nil //nolint:gosec // G115: checked to be within 0 and MaxUint32 above
}

// text is s, or unset when it is empty.
func text(s string) *string {
	if s == "" {
		return nil
	}

	return proto.String(s)
}

// bytesOrNil is b, or unset when it is empty.
func bytesOrNil(b []byte) []byte {
	if len(b) == 0 {
		return nil
	}

	return b
}
