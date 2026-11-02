package media

import (
	"bytes"
	"context"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow/proto/waMmsRetry"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"go.mau.fi/whatsmeow/util/gcmutil"
	"go.mau.fi/whatsmeow/util/hkdfutil"
	"google.golang.org/grpc/codes"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	sidecarstore "github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

const (
	group  = "120363000000000000@g.us"
	sender = "111@lid"
)

func expiredRef() *whatsmeowv1.MediaRef {
	return &whatsmeowv1.MediaRef{
		MediaType: whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE, DirectPath: "/old", Url: "https://old", MediaKey: random(32),
		FileSha256: random(32), FileEncSha256: random(32), FileLength: 42,
	}
}

func (h *harness) requestRetry(media *whatsmeowv1.MediaRef) {
	h.t.Helper()

	_, err := h.client.RequestMediaRetry(h.ctx, &whatsmeowv1.RequestMediaRetryRequest{
		SessionId: "s1", MessageId: "M1", Chat: group, Sender: sender, IsFromMe: true, Media: media,
	})
	if err != nil {
		h.t.Fatalf("RequestMediaRetry: %v", err)
	}
}

func TestARetryRequestIsStoredThenSent(t *testing.T) {
	t.Parallel()

	h := start(t)
	media := expiredRef()

	// The phone can answer at once: the request must be stored by the time the receipt goes out.
	stored := -1
	h.stub.onReceipt = func() {
		_ = h.db.DB.QueryRowContext(context.Background(), "SELECT count(*) FROM media_retry WHERE session_id = 's1' AND message_id = 'M1'").Scan(&stored)
	}

	h.requestRetry(media)

	if stored != 1 {
		t.Fatalf("the request was stored %d times when the receipt went out, want 1", stored)
	}

	want := types.MessageInfo{
		MessageSource: types.MessageSource{Chat: types.NewJID("120363000000000000", types.GroupServer), Sender: types.NewJID("111", types.HiddenUserServer), IsFromMe: true, IsGroup: true},
		ID:            "M1",
	}

	if len(h.stub.receipts) != 1 || !sameReceipt(h.stub.receipts[0].info, want) || string(h.stub.receipts[0].key) != string(media.GetMediaKey()) {
		t.Fatalf("receipts = %+v, want one for %+v with the media key", h.stub.receipts, want)
	}

	// Asking again replaces the request.
	h.requestRetry(expiredRef())

	var requests int
	if err := h.db.DB.QueryRowContext(context.Background(), "SELECT count(*) FROM media_retry").Scan(&requests); err != nil || requests != 1 {
		t.Fatalf("media_retry rows %d, %v; want 1", requests, err)
	}
}

func TestAnInvalidRetryRequestIsRefused(t *testing.T) {
	t.Parallel()

	h := start(t)

	for name, req := range map[string]*whatsmeowv1.RequestMediaRetryRequest{
		"no message id":  {SessionId: "s1", Chat: "1@s.whatsapp.net", Media: expiredRef()},
		"no chat":        {SessionId: "s1", MessageId: "M1", Media: expiredRef()},
		"bad chat":       {SessionId: "s1", MessageId: "M1", Chat: "1:x@s.whatsapp.net", Media: expiredRef()},
		"group, no from": {SessionId: "s1", MessageId: "M1", Chat: group, Media: expiredRef()},
		"no media":       {SessionId: "s1", MessageId: "M1", Chat: "1@s.whatsapp.net"},
		"short key":      {SessionId: "s1", MessageId: "M1", Chat: "1@s.whatsapp.net", Media: &whatsmeowv1.MediaRef{MediaType: whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE, MediaKey: []byte{1}}},
	} {
		_, err := h.client.RequestMediaRetry(h.ctx, req)
		if err == nil {
			t.Fatalf("%s: accepted", name)
		}

		wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", "")
	}

	if len(h.stub.receipts) != 0 {
		t.Fatalf("an invalid request sent %v", h.stub.receipts)
	}
}

// answer is the phone's answer as whatsmeow parses it (mediaretry.go:139-176), encrypted as the phone encrypts it:
// AES-GCM under the media key's retry key, with the message id as additional data (mediaretry.go:24-41, 130).
func answer(t *testing.T, mediaKey []byte, notif *waMmsRetry.MediaRetryNotification, at time.Time) *events.MediaRetry {
	t.Helper()

	plaintext, err := proto.Marshal(notif)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}

	iv := random(12)

	ciphertext, err := gcmutil.Encrypt(hkdfutil.SHA256(mediaKey, nil, []byte("WhatsApp Media Retry Notification"), 32), iv, plaintext, []byte("M1"))
	if err != nil {
		t.Fatalf("encrypt: %v", err)
	}

	return &events.MediaRetry{
		Ciphertext: ciphertext, IV: iv, Timestamp: at, MessageID: "M1",
		ChatID: types.NewJID("120363000000000000", types.GroupServer), SenderID: types.NewJID("111", types.HiddenUserServer), FromMe: true,
	}
}

func TestEveryAnswerMapsToTheNewRefOrItsFailure(t *testing.T) {
	t.Parallel()

	at := time.Unix(1_700_000_000, 0)
	media := expiredRef()
	reuploaded := proto.CloneOf(media)
	reuploaded.DirectPath, reuploaded.Url = "/new", ""

	result := func(r waMmsRetry.MediaRetryNotification_ResultType) *waMmsRetry.MediaRetryNotification {
		return &waMmsRetry.MediaRetryNotification{StanzaID: proto.String("M1"), Result: r.Enum()}
	}

	cases := map[string]struct {
		evt  *events.MediaRetry
		want *whatsmeowv1.MediaRetryEvent
	}{
		"success": {
			answer(t, media.GetMediaKey(), &waMmsRetry.MediaRetryNotification{StanzaID: proto.String("M1"), DirectPath: proto.String("/new"), Result: waMmsRetry.MediaRetryNotification_SUCCESS.Enum()}, at),
			&whatsmeowv1.MediaRetryEvent{Result: &whatsmeowv1.MediaRetryEvent_Media{Media: reuploaded}},
		},
		"not found":        {answer(t, media.GetMediaKey(), result(waMmsRetry.MediaRetryNotification_NOT_FOUND), at), failed(whatsmeowv1.MediaRetryFailure_MEDIA_RETRY_FAILURE_NOT_FOUND)},
		"decryption error": {answer(t, media.GetMediaKey(), result(waMmsRetry.MediaRetryNotification_DECRYPTION_ERROR), at), failed(whatsmeowv1.MediaRetryFailure_MEDIA_RETRY_FAILURE_DECRYPTION_ERROR)},
		"general error":    {answer(t, media.GetMediaKey(), result(waMmsRetry.MediaRetryNotification_GENERAL_ERROR), at), failed(whatsmeowv1.MediaRetryFailure_MEDIA_RETRY_FAILURE_GENERAL_ERROR)},
		"not on the phone": {plainError(2, at), failed(whatsmeowv1.MediaRetryFailure_MEDIA_RETRY_FAILURE_NOT_ON_PHONE)},
		"another code":     {plainError(7, at), failed(whatsmeowv1.MediaRetryFailure_MEDIA_RETRY_FAILURE_UNSPECIFIED)},
		"another key":      {answer(t, random(32), result(waMmsRetry.MediaRetryNotification_SUCCESS), at), failed(whatsmeowv1.MediaRetryFailure_MEDIA_RETRY_FAILURE_UNSPECIFIED)},
		"success, no path": {answer(t, media.GetMediaKey(), result(waMmsRetry.MediaRetryNotification_SUCCESS), at), failed(whatsmeowv1.MediaRetryFailure_MEDIA_RETRY_FAILURE_UNSPECIFIED)},
	}

	h := start(t)
	mapper := Mapper(h.db.DB, zerolog.Nop())

	for name, c := range cases {
		// A final answer settles its request: ask again for each.
		h.requestRetry(media)

		key, event, ok := mapper("s1", c.evt)

		want := proto.CloneOf(c.want)
		want.MessageId, want.Chat, want.Sender, want.IsFromMe, want.RetryTime = "M1", group, sender, true, timestamppb.New(at)

		if !ok || key == "" || !proto.Equal(event.GetMediaRetry(), want) {
			t.Errorf("%s: mapped %q, %v, %v; want %v", name, key, event, ok, want)
		}
	}
}

// sameReceipt compares the fields SendMediaRetryReceipt reads (mediaretry.go:81-96).
func sameReceipt(got, want types.MessageInfo) bool {
	return got.ID == want.ID && got.Chat == want.Chat && got.Sender == want.Sender && got.IsFromMe == want.IsFromMe && got.IsGroup == want.IsGroup
}

func failed(f whatsmeowv1.MediaRetryFailure) *whatsmeowv1.MediaRetryEvent {
	return &whatsmeowv1.MediaRetryEvent{Result: &whatsmeowv1.MediaRetryEvent_Failure{Failure: f}}
}

// plainError is an answer with an unencrypted error and no ciphertext (mediaretry.go:159-165).
func plainError(code int, at time.Time) *events.MediaRetry {
	return &events.MediaRetry{
		Error: &events.MediaRetryError{Code: code}, Timestamp: at, MessageID: "M1",
		ChatID: types.NewJID("120363000000000000", types.GroupServer), SenderID: types.NewJID("111", types.HiddenUserServer), FromMe: true,
	}
}

func TestAnAnswerKeepsItsKeyAndANewAnswerGetsAnother(t *testing.T) {
	t.Parallel()

	h := start(t)
	media := expiredRef()
	h.requestRetry(media)
	mapper := Mapper(h.db.DB, zerolog.Nop())

	// A general error does not settle the request, so the phone's later answers find it too.
	general := &waMmsRetry.MediaRetryNotification{StanzaID: proto.String("M1"), Result: waMmsRetry.MediaRetryNotification_GENERAL_ERROR.Enum()}
	evt := answer(t, media.GetMediaKey(), general, time.Unix(1_700_000_000, 0))
	first, _, _ := mapper("s1", evt)
	again, _, _ := mapper("s1", evt)
	later, _, _ := mapper("s1", answer(t, media.GetMediaKey(), general, time.Unix(1_700_000_001, 0)))

	if first != again || first == later {
		t.Fatalf("keys %q, %q, %q; want a repeat to keep its key and a later answer to get another", first, again, later)
	}
}

func TestAnAnswerToNoRequestIsNotMapped(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.requestRetry(expiredRef())
	mapper := Mapper(h.db.DB, zerolog.Nop())

	other := plainError(2, time.Unix(1_700_000_000, 0))
	other.MessageID = "M2"

	for name, evt := range map[string]any{
		"another message": other,
		"not an answer":   &events.Connected{},
	} {
		if _, _, ok := mapper("s1", evt); ok {
			t.Errorf("%s: mapped", name)
		}
	}

	if _, _, ok := mapper("s2", plainError(2, time.Unix(1_700_000_000, 0))); ok {
		t.Error("another session's answer mapped to this session's request")
	}
}

func TestARequestIsAnsweredAfterARestart(t *testing.T) {
	t.Parallel()

	h := start(t)
	media := expiredRef()
	h.requestRetry(media)

	if err := h.db.Close(); err != nil {
		t.Fatalf("close: %v", err)
	}

	db, err := sidecarstore.Open(context.Background(), h.dbFile, zerolog.Nop())
	if err != nil {
		t.Fatalf("reopen: %v", err)
	}

	t.Cleanup(func() { _ = db.Close() })

	if _, event, ok := Mapper(db.DB, zerolog.Nop())("s1", plainError(2, time.Unix(1_700_000_000, 0))); !ok || event.GetMediaRetry().GetFailure() != whatsmeowv1.MediaRetryFailure_MEDIA_RETRY_FAILURE_NOT_ON_PHONE {
		t.Fatalf("after a restart the answer mapped to %v, %v", event, ok)
	}
}

// requests is the number of stored media retry requests.
func (h *harness) requests() int {
	h.t.Helper()

	var n int
	if err := h.db.DB.QueryRowContext(context.Background(), "SELECT count(*) FROM media_retry").Scan(&n); err != nil {
		h.t.Fatalf("count the requests: %v", err)
	}

	return n
}

func TestAFinalAnswerSettlesItsRequestAndAnyOtherKeepsIt(t *testing.T) {
	t.Parallel()

	at := time.Unix(1_700_000_000, 0)
	media := expiredRef()

	result := func(r waMmsRetry.MediaRetryNotification_ResultType) *waMmsRetry.MediaRetryNotification {
		return &waMmsRetry.MediaRetryNotification{StanzaID: proto.String("M1"), Result: r.Enum()}
	}

	cases := map[string]struct {
		evt  *events.MediaRetry
		kept bool
	}{
		"success":          {answer(t, media.GetMediaKey(), &waMmsRetry.MediaRetryNotification{StanzaID: proto.String("M1"), DirectPath: proto.String("/new"), Result: waMmsRetry.MediaRetryNotification_SUCCESS.Enum()}, at), false},
		"not found":        {answer(t, media.GetMediaKey(), result(waMmsRetry.MediaRetryNotification_NOT_FOUND), at), false},
		"decryption error": {answer(t, media.GetMediaKey(), result(waMmsRetry.MediaRetryNotification_DECRYPTION_ERROR), at), false},
		"not on the phone": {plainError(2, at), false},
		"general error":    {answer(t, media.GetMediaKey(), result(waMmsRetry.MediaRetryNotification_GENERAL_ERROR), at), true},
		"another code":     {plainError(7, at), true},
		"another key":      {answer(t, random(32), result(waMmsRetry.MediaRetryNotification_SUCCESS), at), true},
		"success, no path": {answer(t, media.GetMediaKey(), result(waMmsRetry.MediaRetryNotification_SUCCESS), at), true},
	}

	h := start(t)
	mapper := Mapper(h.db.DB, zerolog.Nop())

	for name, c := range cases {
		h.requestRetry(media)

		if _, _, ok := mapper("s1", c.evt); !ok {
			t.Fatalf("%s: not mapped", name)
		}

		if kept := h.requests() == 1; kept != c.kept {
			t.Errorf("%s: the request kept %v, want %v", name, kept, c.kept)
		}
	}
}

func TestAnAnswerFindsItsRequestByMessageAndPrefersTheSameChat(t *testing.T) {
	t.Parallel()

	h := start(t)
	mapper := Mapper(h.db.DB, zerolog.Nop())
	byPhone, inGroup := expiredRef(), expiredRef()

	for _, r := range []struct {
		chat, sender string
		media        *whatsmeowv1.MediaRef
	}{{"5511999999999@s.whatsapp.net", "", byPhone}, {group, sender, inGroup}} {
		if _, err := h.client.RequestMediaRetry(h.ctx, &whatsmeowv1.RequestMediaRetryRequest{SessionId: "s1", MessageId: "M1", Chat: r.chat, Sender: r.sender, Media: r.media}); err != nil {
			t.Fatalf("RequestMediaRetry: %v", err)
		}
	}

	success := func(media *whatsmeowv1.MediaRef, chat types.JID) *events.MediaRetry {
		evt := answer(t, media.GetMediaKey(), &waMmsRetry.MediaRetryNotification{StanzaID: proto.String("M1"), DirectPath: proto.String("/new"), Result: waMmsRetry.MediaRetryNotification_SUCCESS.Enum()}, time.Unix(1_700_000_000, 0))
		evt.ChatID, evt.SenderID, evt.FromMe = chat, types.EmptyJID, false

		return evt
	}

	pn, lid, groupJID := types.NewJID("5511999999999", types.DefaultUserServer), types.NewJID("123456789", types.HiddenUserServer), types.NewJID("120363000000000000", types.GroupServer)

	// Of two requests for the message, the one in the answer's chat (the group's sorts first).
	for _, c := range []struct {
		media *whatsmeowv1.MediaRef
		chat  types.JID
	}{{byPhone, pn}, {inGroup, groupJID}} {
		if _, event, ok := mapper("s1", success(c.media, c.chat)); !ok || !bytes.Equal(event.GetMediaRetry().GetMedia().GetMediaKey(), c.media.GetMediaKey()) {
			t.Fatalf("the answer in %v mapped to %v, %v; want that chat's request", c.chat, event, ok)
		}
	}

	// The request by phone number, though the answer names the chat by its LID, the other addressing form.
	if _, err := h.client.RequestMediaRetry(h.ctx, &whatsmeowv1.RequestMediaRetryRequest{SessionId: "s1", MessageId: "M1", Chat: pn.String(), Media: byPhone}); err != nil {
		t.Fatalf("RequestMediaRetry: %v", err)
	}

	if _, event, ok := mapper("s1", success(byPhone, lid)); !ok ||
		!bytes.Equal(event.GetMediaRetry().GetMedia().GetMediaKey(), byPhone.GetMediaKey()) || event.GetMediaRetry().GetChat() != lid.String() {
		t.Fatalf("the LID answer mapped to %v, %v; want the phone-number request's media, in the answer's chat", event, ok)
	}

	if n := h.requests(); n != 0 {
		t.Fatalf("%d requests left, want both settled", n)
	}
}
