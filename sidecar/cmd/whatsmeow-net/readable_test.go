package main

import (
	"context"
	"math"
	"path/filepath"
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
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

// unreadable is the first field of event the client cannot read, as the contract names it, or "" for an event it
// reads. It is the client's mapping restated: each field it reads with SidecarValues (dotnet/src/Whatsmeow.Client,
// EventMapping.ToEvent and the slices' ToEvent), and it reads a JID as server.ReadableJID says and an id when it is
// not empty. A change the client does not know makes it skip the event without reading it, so nothing is required.
// A new SidecarValues read of an event field on the client adds its field here (docs/guidelines/go.md).
func unreadable(event *whatsmeowv1.StreamEventsResponse) string {
	switch p := event.GetPayload().(type) {
	case *whatsmeowv1.StreamEventsResponse_Message:
		if f := info(p.Message.GetInfo()); f != "" {
			return f
		}

		if f := target(p.Message.GetTarget()); f != "" {
			return f
		}

		return target(p.Message.GetReplyTo())
	case *whatsmeowv1.StreamEventsResponse_UndecryptableMessage:
		return info(p.UndecryptableMessage.GetInfo())
	case *whatsmeowv1.StreamEventsResponse_Contact:
		return jid(p.Contact.GetChange() != nil, p.Contact.GetJid(), "ContactEvent.jid")
	case *whatsmeowv1.StreamEventsResponse_ChatState:
		c := p.ChatState
		if f := jid(c.GetChange() != nil, c.GetChat(), "ChatStateEvent.chat"); f != "" {
			return f
		}

		return id(c.GetStar() != nil, c.GetStar().GetMessageId(), "MessageStar.message_id")
	case *whatsmeowv1.StreamEventsResponse_Receipt:
		if f := source(p.Receipt.GetSource()); f != "" {
			return f
		}

		for _, m := range p.Receipt.GetMessageIds() {
			if m == "" {
				return "ReceiptEvent.message_ids"
			}
		}
	case *whatsmeowv1.StreamEventsResponse_Group:
		return group(p.Group)
	case *whatsmeowv1.StreamEventsResponse_MediaRetry:
		r := p.MediaRetry
		if f := id(true, r.GetMessageId(), "MediaRetryEvent.message_id"); f != "" {
			return f
		}

		if f := jid(true, r.GetChat(), "MediaRetryEvent.chat"); f != "" {
			return f
		}

		if m := r.GetMedia(); m != nil && (m.GetMediaType() == whatsmeowv1.MediaType_MEDIA_TYPE_UNSPECIFIED || m.GetFileLength() > math.MaxInt64) {
			return "MediaRef"
		}
	}

	return ""
}

func info(i *whatsmeowv1.MessageInfo) string {
	if f := id(true, i.GetId(), "MessageInfo.id"); f != "" {
		return f
	}

	return source(i.GetSource())
}

func source(s *whatsmeowv1.MessageSource) string {
	if f := jid(true, s.GetChat(), "MessageSource.chat"); f != "" {
		return f
	}

	return jid(true, s.GetSender(), "MessageSource.sender")
}

// target is a reaction's, an edit's, a revoke's or a quote's message: the client reads its id unless the sender is
// one it cannot read, when it drops the key (ContentMapping.ToKey).
func target(t *whatsmeowv1.MessageTarget) string {
	return id(t != nil && (t.GetSender() == "" || server.ReadableJID(t.GetSender())), t.GetId(), "MessageTarget.id")
}

func group(g *whatsmeowv1.GroupEvent) string {
	if joined := g.GetJoined(); joined != nil {
		if f := jid(true, joined.GetGroup().GetJid(), "Group.jid"); f != "" {
			return f
		}

		for _, p := range joined.GetGroup().GetParticipants() {
			if f := jid(true, p.GetJid(), "GroupParticipant.jid"); f != "" {
				return f
			}
		}

		return ""
	}

	changes := g.GetChanged().GetChanges()
	if f := jid(len(changes) > 0, g.GetGroup(), "GroupEvent.group"); f != "" {
		return f
	}

	for _, c := range changes {
		for _, list := range []*whatsmeowv1.GroupChange_Participants{c.GetJoined(), c.GetLeft(), c.GetPromoted(), c.GetDemoted()} {
			for _, j := range list.GetJids() {
				if f := jid(true, j, "GroupChange.Participants.jids"); f != "" {
					return f
				}
			}
		}
	}

	return ""
}

func jid(required bool, value, field string) string {
	if required && !server.ReadableJID(value) {
		return field
	}

	return ""
}

func id(required bool, value, field string) string {
	if required && value == "" {
		return field
	}

	return ""
}

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
func TestNoMapperRecordsAnEventTheClientCannotRead(t *testing.T) {
	ctx := context.Background()

	db, err := store.Open(ctx, filepath.Join(t.TempDir(), "whatsmeow-net.db"), zerolog.Nop())
	if err != nil {
		t.Fatalf("open the store: %v", err)
	}

	t.Cleanup(func() { _ = db.Close() })

	if _, err := db.DB.ExecContext(ctx, "INSERT INTO session (id) VALUES ('s1')"); err != nil {
		t.Fatalf("create the session: %v", err)
	}

	// A media retry request, so the media mapper records the answers to R1 whatever their chat.
	requested, err := proto.Marshal(&whatsmeowv1.MediaRef{MediaType: whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE, DirectPath: "/m/1", FileLength: 1})
	if err != nil {
		t.Fatalf("marshal the request: %v", err)
	}

	if _, err := db.DB.ExecContext(ctx, "INSERT INTO media_retry (session_id, message_id, chat, media) VALUES ('s1', 'R1', '5511900000001@s.whatsapp.net', ?)", requested); err != nil {
		t.Fatalf("store a media retry request: %v", err)
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
		if f := unreadable(event); f != "" {
			t.Errorf("recorded an event whose %s the client cannot read: %v", f, event)
		}
	}
}
