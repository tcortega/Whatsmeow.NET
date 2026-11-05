package media

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"strconv"
	"strings"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waMmsRetry"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

// RequestMediaRetry asks the phone to upload a message's media again (media.proto). The phone's answer is encrypted
// with the media key (mediaretry.go:123-137) and names only the message (mediaretry.go:139-176), so the request is
// stored first, with the media, for the Mapper to decrypt the answer and give it its new path; an answer can come
// as soon as the receipt is out, or after a restart.
func (svc *Service) RequestMediaRetry(ctx context.Context, req *whatsmeowv1.RequestMediaRetryRequest) (*whatsmeowv1.RequestMediaRetryResponse, error) {
	info, err := retryTarget(req)
	if err != nil {
		return nil, err
	}

	if _, err := checkRef("media", req.GetMedia()); err != nil {
		return nil, err
	}

	client, err := svc.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	media, err := proto.Marshal(req.GetMedia())
	if err != nil {
		return nil, fmt.Errorf("media retry: %w", err)
	}

	_, err = svc.w.Exec(ctx, `INSERT INTO media_retry (session_id, message_id, chat, media) VALUES (?, ?, ?, ?)
		ON CONFLICT (session_id, message_id, chat) DO UPDATE SET media = excluded.media`, req.GetSessionId(), info.ID, info.Chat.String(), media)
	if err != nil {
		return nil, fmt.Errorf("media retry: store the request: %w", err)
	}

	if err := client.SendMediaRetryReceipt(ctx, info, req.GetMedia().GetMediaKey()); err != nil {
		return nil, fmt.Errorf("media retry: %w", sessionError(err))
	}

	return &whatsmeowv1.RequestMediaRetryResponse{}, nil
}

// retryTarget is the message SendMediaRetryReceipt names (mediaretry.go:81-96): its id, chat, and in a group its sender.
func retryTarget(req *whatsmeowv1.RequestMediaRetryRequest) (*types.MessageInfo, error) {
	if req.GetMessageId() == "" {
		return nil, invalid("message_id", "no message id")
	}

	chat, err := types.ParseJID(req.GetChat())
	if err != nil || chat.IsEmpty() {
		return nil, invalid("chat", "%q is not a JID", req.GetChat())
	}

	info := &types.MessageInfo{ID: req.GetMessageId(), MessageSource: types.MessageSource{Chat: chat, IsFromMe: req.GetIsFromMe(), IsGroup: chat.Server == types.GroupServer}}

	if req.GetSender() != "" {
		if info.Sender, err = types.ParseJID(req.GetSender()); err != nil {
			return nil, invalid("sender", "%q is not a JID", req.GetSender())
		}
	}

	if info.IsGroup && info.Sender.IsEmpty() {
		return nil, invalid("sender", "a group message needs its sender")
	}

	return info, nil
}

// Mapper maps the phone's answers to a session's media retry requests to MediaRetryEvents, for the Events slice's
// handler (events.Mapper). It reads the stored request and decrypts the answer with its media key: SQLite and a few
// bytes of AES-GCM, nothing else (ARCHITECTURE invariant 1). An answer to no stored request is not recorded.
//
// The request is the session's for the answer's message id. The chat is only a tiebreak between requests for the same
// id: the answer names the chat as the phone's notification does (<rmr jid>, mediaretry.go:151-155), which may be the
// other addressing form, LID or phone number, than the request's [unknown: the live run, #10]. A final answer settles the request, and
// its row, with its media key, is deleted (ADR 0026); whatsmeow acks the notification whatever the handler returns
// (notification.go:510-511), so no answer comes again for a request whose event failed to record.
func Mapper(db *sql.DB, w *store.Writer, log zerolog.Logger) func(sessionID string, evt any) (string, *whatsmeowv1.StreamEventsResponse, bool) {
	return func(sessionID string, evt any) (string, *whatsmeowv1.StreamEventsResponse, bool) {
		retry, ok := evt.(*events.MediaRetry)
		if !ok {
			return "", nil, false
		}

		// whatsmeow requires the chat (mediaretry.go:152-157), and the request is found by its message id whatever the
		// chat: one the client cannot read is not recorded (#122).
		chat := retry.ChatID.String()
		if !server.ReadableJID(server.Text(chat)) {
			return "", nil, false
		}

		var (
			stored    []byte
			requested string
		)

		// No caller's context: whatsmeow gives the handler none. SQLite's busy_timeout bounds the read and the delete.
		err := db.QueryRowContext(context.Background(), `SELECT chat, media FROM media_retry WHERE session_id = ? AND message_id = ?
			ORDER BY chat = ? DESC, chat LIMIT 1`, sessionID, retry.MessageID, chat).Scan(&requested, &stored)
		if err != nil {
			level := log.Error()
			if errors.Is(err, sql.ErrNoRows) {
				level = log.Warn()
			}

			level.Err(err).Str("session_id", sessionID).Msg("a media retry answer matches no stored request; it is not recorded")

			return "", nil, false
		}

		media := &whatsmeowv1.MediaRef{}
		if err := proto.Unmarshal(stored, media); err != nil {
			log.Error().Err(err).Str("session_id", sessionID).Msg("a stored media retry request does not parse; the answer is not recorded")

			return "", nil, false
		}

		event := &whatsmeowv1.MediaRetryEvent{
			MessageId: server.Text(retry.MessageID), Chat: server.Text(chat), Sender: server.Text(retry.SenderID.String()), IsFromMe: retry.FromMe, RetryTime: timestamppb.New(retry.Timestamp),
		}

		if answered(log, sessionID, retry, media, event) {
			// In the Events slice's handler, which whatsmeow gives no context: bounded as its own write is (ADR 0040).
			ctx, cancel := context.WithTimeout(context.Background(), store.BusyTimeout)
			_, err := w.Exec(ctx, "DELETE FROM media_retry WHERE session_id = ? AND message_id = ? AND chat = ?",
				sessionID, retry.MessageID, requested)
			cancel()
			if err != nil {
				log.Error().Err(err).Str("session_id", sessionID).Msg("deleting a settled media retry request failed; it stays until its session is deleted")
			}
		}

		key := strings.Join([]string{"r", chat, retry.SenderID.String(), retry.MessageID, strconv.FormatInt(retry.Timestamp.UnixNano(), 10)}, "\x00")

		return key, &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_MediaRetry{MediaRetry: event}}, true
	}
}

// answered sets the event's result: the requested media with the new path, or why there is none. It reports whether
// the answer is final: the media at its new path, not found, not on the phone, or the phone could not decrypt the
// request, which asking again with the same key would only repeat [inferred: WAMmsRetry.proto names no retry]. A
// general error, an unknown code, or an answer that does not decrypt keep the request for a later answer.
func answered(log zerolog.Logger, sessionID string, retry *events.MediaRetry, media *whatsmeowv1.MediaRef, event *whatsmeowv1.MediaRetryEvent) (final bool) {
	failure := func(f whatsmeowv1.MediaRetryFailure) {
		event.Result = &whatsmeowv1.MediaRetryEvent_Failure{Failure: f}
	}

	notif, err := whatsmeow.DecryptMediaRetryNotification(retry, media.GetMediaKey())

	switch {
	case errors.Is(err, whatsmeow.ErrMediaNotAvailableOnPhone):
		failure(whatsmeowv1.MediaRetryFailure_MEDIA_RETRY_FAILURE_NOT_ON_PHONE)

		return true
	case err != nil:
		log.Warn().Err(err).Str("session_id", sessionID).Msg("a media retry answer did not decrypt or names an unknown error")
		failure(whatsmeowv1.MediaRetryFailure_MEDIA_RETRY_FAILURE_UNSPECIFIED)

		return false
	case notif.GetResult() == waMmsRetry.MediaRetryNotification_SUCCESS && notif.GetDirectPath() != "":
		reuploaded := proto.CloneOf(media)
		reuploaded.DirectPath, reuploaded.Url = notif.GetDirectPath(), ""
		event.Result = &whatsmeowv1.MediaRetryEvent_Media{Media: reuploaded}

		return true
	case notif.GetResult() == waMmsRetry.MediaRetryNotification_NOT_FOUND:
		failure(whatsmeowv1.MediaRetryFailure_MEDIA_RETRY_FAILURE_NOT_FOUND)

		return true
	case notif.GetResult() == waMmsRetry.MediaRetryNotification_DECRYPTION_ERROR:
		failure(whatsmeowv1.MediaRetryFailure_MEDIA_RETRY_FAILURE_DECRYPTION_ERROR)

		return true
	case notif.GetResult() == waMmsRetry.MediaRetryNotification_GENERAL_ERROR:
		failure(whatsmeowv1.MediaRetryFailure_MEDIA_RETRY_FAILURE_GENERAL_ERROR)

		return false
	default:
		log.Warn().Str("session_id", sessionID).Stringer("result", notif.GetResult()).Msg("a media retry answer has no new path")
		failure(whatsmeowv1.MediaRetryFailure_MEDIA_RETRY_FAILURE_UNSPECIFIED)

		return false
	}
}
