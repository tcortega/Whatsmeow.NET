package main

import (
	"context"
	"path/filepath"
	"slices"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/types"
	waevents "go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	eventsslice "github.com/tcortega/Whatsmeow.NET/sidecar/internal/events"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/outbox"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

// badJIDs are the JIDs whatsmeow can hand a mapper that the client cannot read: the empty JID, one without a server
// (types.ParseJID gives both for "", "@" and "x@"), and what ParseJID keeps when it fails or splits oddly, which
// JID.String writes back as is (ARCHITECTURE §14, #122).
func badJIDs() []types.JID {
	return []types.JID{
		{},
		{User: "5511900000001"},
		{User: "a.b.c", Server: types.DefaultUserServer},
		{User: "a:b:c", Server: types.DefaultUserServer},
		{User: "a:1", RawAgent: 2, Server: types.DefaultUserServer},
	}
}

// corpus is every whatsmeow event a mapper records, with each JID the client requires set in turn to each of
// badJIDs, each id the client requires empty, and the zero value of each.
func corpus() []any {
	pn := types.NewJID("5511900000001", types.DefaultUserServer)
	gr := types.NewJID("120363000000000000", types.GroupServer)
	at := time.Unix(1_700_000_000, 0)
	hello := &waE2E.Message{Conversation: proto.String("hello")}
	msg := func(chat, sender types.JID, id string) types.MessageInfo {
		return types.MessageInfo{MessageSource: types.MessageSource{Chat: chat, Sender: sender}, ID: id, Timestamp: at}
	}

	// An answer with an error code: whatsmeow's decryption panics on an IV of another length (gcmutil.Decrypt), which
	// is not this test's subject.
	retryError := &waevents.MediaRetryError{Code: 1}

	evts := []any{
		&waevents.Message{}, &waevents.UndecryptableMessage{}, &waevents.Receipt{}, &waevents.PushName{}, &waevents.BusinessName{},
		&waevents.Picture{}, &waevents.UserAbout{}, &waevents.Mute{}, &waevents.Pin{}, &waevents.Archive{}, &waevents.MarkChatAsRead{},
		&waevents.Star{}, &waevents.GroupInfo{}, &waevents.JoinedGroup{}, &waevents.MediaRetry{}, &waevents.MediaRetry{MessageID: "R1", Error: retryError},
		&waevents.Message{Info: msg(pn, pn, ""), Message: hello},
		&waevents.UndecryptableMessage{Info: msg(pn, pn, "")},
		&waevents.Receipt{MessageSource: types.MessageSource{Chat: pn, Sender: pn}, MessageIDs: []string{""}, Timestamp: at},
		&waevents.Star{ChatJID: pn, MessageID: "", Timestamp: at},
	}

	for _, bad := range badJIDs() {
		evts = append(evts,
			&waevents.Message{Info: msg(bad, pn, "M1"), Message: hello},
			&waevents.Message{Info: msg(pn, bad, "M1"), Message: hello},
			&waevents.UndecryptableMessage{Info: msg(bad, pn, "U1")},
			&waevents.UndecryptableMessage{Info: msg(pn, bad, "U1")},
			&waevents.Receipt{MessageSource: types.MessageSource{Chat: bad, Sender: pn}, MessageIDs: []string{"M1"}, Timestamp: at},
			&waevents.Receipt{MessageSource: types.MessageSource{Chat: pn, Sender: bad}, MessageIDs: []string{"M1"}, Timestamp: at},
			&waevents.PushName{JID: bad, NewPushName: "Ana"},
			&waevents.BusinessName{JID: bad, NewBusinessName: "Ana"},
			&waevents.Picture{JID: bad, PictureID: "P1"},
			&waevents.UserAbout{JID: bad, Status: "hi"},
			&waevents.Mute{JID: bad, Timestamp: at},
			&waevents.Pin{JID: bad, Timestamp: at},
			&waevents.Archive{JID: bad, Timestamp: at},
			&waevents.MarkChatAsRead{JID: bad, Timestamp: at},
			&waevents.Star{ChatJID: bad, MessageID: "M1", Timestamp: at},
			&waevents.GroupInfo{JID: bad, Name: &types.GroupName{Name: "Team"}},
			&waevents.GroupInfo{JID: gr, Join: []types.JID{bad, pn}},
			&waevents.GroupInfo{JID: gr, Leave: []types.JID{bad}},
			&waevents.GroupInfo{JID: gr, Promote: []types.JID{bad}, Name: &types.GroupName{Name: "Team"}},
			&waevents.GroupInfo{JID: gr, Demote: []types.JID{pn, bad}},
			&waevents.JoinedGroup{GroupInfo: types.GroupInfo{JID: bad}},
			&waevents.JoinedGroup{GroupInfo: types.GroupInfo{JID: gr, Participants: []types.GroupParticipant{{JID: bad}, {JID: pn}}}},
			&waevents.MediaRetry{MessageID: "R1", ChatID: bad, Timestamp: at, Error: retryError},
		)
	}

	return evts
}

// Not parallel: pii_test.go sets libsignal's logger, which a device created at once would race with
// (ARCHITECTURE §7.2).
func TestNoEventTheClientCannotReadIsRecorded(t *testing.T) {
	ctx := context.Background()

	db, err := store.Open(ctx, filepath.Join(t.TempDir(), "whatsmeow-net.db"), zerolog.Nop())
	if err != nil {
		t.Fatalf("open the store: %v", err)
	}

	t.Cleanup(func() { _ = db.Close() })

	if _, err := db.DB.ExecContext(ctx, "INSERT INTO session (id) VALUES ('s1')"); err != nil {
		t.Fatalf("create the session: %v", err)
	}

	// A media retry request, so the media mapper maps the answers to R1 whatever their chat.
	requested, err := proto.Marshal(&whatsmeowv1.MediaRef{MediaType: whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE, DirectPath: "/m/1", FileLength: 1})
	if err != nil {
		t.Fatalf("marshal the request: %v", err)
	}

	if _, err := db.DB.ExecContext(ctx, "INSERT INTO media_retry (session_id, message_id, chat, media) VALUES ('s1', 'R1', '5511900000001@s.whatsapp.net', ?)", requested); err != nil {
		t.Fatalf("store a media retry request: %v", err)
	}

	// Each mapper main wires maps some of the corpus, so the corpus reaches every one: a new event type joins it.
	for i, mapper := range mappers(db, zerolog.Nop()) {
		if !slices.ContainsFunc(corpus(), func(evt any) bool { _, _, ok := mapper("s1", evt); return ok }) {
			t.Errorf("mapper %d maps nothing of the corpus", i)
		}
	}

	// The gate refuses a payload case it does not know, so one a new mapper adds is never recorded unchecked.
	if eventsslice.Unreadable(&whatsmeowv1.StreamEventsResponse{}) == "" {
		t.Error("Unreadable passes an event without a payload case")
	}

	box := outbox.New(db.DB, db.Writer)
	handler := eventsslice.New(box, zerolog.Nop(), mappers(db, zerolog.Nop())...).Handler("s1", db.Devices.NewDevice())

	for _, evt := range corpus() {
		if !handler(evt) {
			t.Fatalf("a %T was not answered: %+v", evt, evt)
		}
	}

	recorded, err := box.Read(ctx, "s1", 0, 10_000)
	if err != nil {
		t.Fatalf("read the outbox: %v", err)
	}

	// The corpus has readable parts too (the readable members of a group change), so this proves the read.
	if len(recorded) == 0 {
		t.Fatal("nothing was recorded; this test proves nothing")
	}

	for _, event := range recorded {
		if f := eventsslice.Unreadable(event); f != "" {
			t.Errorf("recorded an event whose %s the client cannot read: %v", f, event)
		}
	}
}
