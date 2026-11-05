package media

import (
	"testing"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow/proto/waMmsRetry"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	eventsslice "github.com/tcortega/Whatsmeow.NET/sidecar/internal/events"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/outbox"
)

func TestMalformedMediaRetryIsRecordedAsFailureAndAnswered(t *testing.T) {
	t.Parallel()

	cases := map[string]struct {
		iv, ciphertext []byte
		err            *events.MediaRetryError
	}{
		"missing IV":                {nil, []byte{1}, nil},
		"one-byte IV":               {make([]byte, 1), []byte{1}, nil},
		"short IV":                  {make([]byte, 11), []byte{1}, nil},
		"long IV":                   {make([]byte, 13), []byte{1}, nil},
		"block-sized IV":            {make([]byte, 16), []byte{1}, nil},
		"missing ciphertext":        {make([]byte, 12), nil, nil},
		"empty ciphertext":          {make([]byte, 12), []byte{}, nil},
		"missing both":              {nil, nil, nil},
		"error and encrypted bytes": {nil, []byte{1}, &events.MediaRetryError{Code: 2}},
	}
	for name, c := range cases {
		t.Run(name, func(t *testing.T) {
			t.Parallel()

			h := start(t)
			media := expiredRef()
			h.requestRetry(media)
			at := time.Unix(1_700_000_000, 0)
			evt := answer(t, media.GetMediaKey(), &waMmsRetry.MediaRetryNotification{Result: waMmsRetry.MediaRetryNotification_GENERAL_ERROR.Enum()}, at)
			evt.IV, evt.Ciphertext, evt.Error = c.iv, c.ciphertext, c.err
			box := outbox.New(h.db.DB, h.db.Writer)
			handler := eventsslice.New(box, zerolog.New(h.logs), Mapper(h.db.DB, h.db.Writer, zerolog.New(h.logs))).Handler("s1", nil)

			if !handler(evt) {
				t.Fatalf("the malformed answer failed the handler: %s", h.logs.String())
			}

			recorded, err := box.Read(h.ctx, "s1", 0, 10)
			if err != nil || len(recorded) != 1 {
				t.Fatalf("outbox = %v, %v; want one failure", recorded, err)
			}

			want := &whatsmeowv1.MediaRetryEvent{
				MessageId: "M1", Chat: group, Sender: sender, IsFromMe: true, RetryTime: timestamppb.New(at),
				Result: &whatsmeowv1.MediaRetryEvent_Failure{Failure: whatsmeowv1.MediaRetryFailure_MEDIA_RETRY_FAILURE_UNSPECIFIED},
			}
			if !proto.Equal(recorded[0].GetMediaRetry(), want) {
				t.Fatalf("retry = %v; want %v", recorded[0].GetMediaRetry(), want)
			}

			// Without another request, a later valid answer must still find the stored media key.
			later := at.Add(time.Second)
			valid := answer(t, media.GetMediaKey(), &waMmsRetry.MediaRetryNotification{
				Result: waMmsRetry.MediaRetryNotification_SUCCESS.Enum(), DirectPath: proto.String("/new"),
			}, later)
			if !handler(valid) {
				t.Fatalf("the later valid answer failed the handler: %s", h.logs.String())
			}

			recovered, err := box.Read(h.ctx, "s1", recorded[0].GetSeq(), 10)
			if err != nil || len(recovered) != 1 {
				t.Fatalf("outbox after the failure = %v, %v; want the recovery", recovered, err)
			}

			reuploaded := proto.CloneOf(media)
			reuploaded.DirectPath, reuploaded.Url = "/new", ""
			want.RetryTime = timestamppb.New(later)
			want.Result = &whatsmeowv1.MediaRetryEvent_Media{Media: reuploaded}
			if !proto.Equal(recovered[0].GetMediaRetry(), want) {
				t.Fatalf("recovery = %v; want %v", recovered[0].GetMediaRetry(), want)
			}
		})
	}
}
