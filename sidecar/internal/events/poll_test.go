package events

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"strings"
	"testing"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waAdv"
	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/types"
	waevents "go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// savedAccount is account() saved in the harness's store, so whatsmeow's message secrets (MsgSecrets) are real.
func (h *harness) savedAccount() *store.Device {
	h.t.Helper()

	device := h.db.Devices.NewDevice()
	device.ID, device.LID = new(jid(accountPN)), jid(accountLID)
	device.Account = &waAdv.ADVSignedDeviceIdentity{
		Details: []byte{}, AccountSignature: make([]byte, 64), AccountSignatureKey: make([]byte, 32), DeviceSignature: make([]byte, 64),
	}

	if err := device.Save(context.Background()); err != nil {
		h.t.Fatalf("save the device: %v", err)
	}

	return device
}

// A vote is decrypted in the handler with whatsmeow's own DecryptPollVote and the poll's secret from the session's
// store, which whatsmeow put there when the poll was sent or received (send.go:403-410, message.go:973-994). A vote the
// sidecar cannot decrypt is still recorded, marked, with its poll; every one is answered.
func TestAPollVoteIsRecordedDecrypted_OrMarkedUndecryptable(t *testing.T) {
	t.Parallel()

	const (
		chat  = "222@lid"
		voter = "222:2@lid"
	)

	h := start(t, "s1")
	device := h.savedAccount()
	wa := whatsmeow.NewClient(device, nil)

	// The account's own poll in the direct chat, as whatsmeow's SendMessage stored its secret: by the chat and the
	// account's LID.
	if err := device.MsgSecrets.PutMessageSecret(context.Background(), jid(chat), jid(accountLID), "POLL1", bytes.Repeat([]byte{7}, 32)); err != nil {
		t.Fatalf("store the poll's secret: %v", err)
	}

	// The vote as the voter's device encrypts it, naming the poll by a key as the voter sees it: the account's in the
	// direct chat (msgsecret.go:130-151).
	vote := func(id, poll string, options []string, corrupt bool) *waevents.Message {
		plaintext, err := proto.Marshal(&waE2E.PollVoteMessage{SelectedOptions: whatsmeow.HashPollOptions(options)})
		if err != nil {
			t.Fatal(err)
		}

		payload, iv := []byte{1, 2, 3}, make([]byte, 12)
		if !corrupt {
			payload, iv, err = wa.DangerousInternals().EncryptMsgSecret(context.Background(), jid(voter), jid(chat), jid(accountLID), "POLL1", whatsmeow.EncSecretPollVote, plaintext) //nolint:staticcheck // the voter's encryption, which no exported call makes
			if err != nil {
				t.Fatalf("encrypt the vote: %v", err)
			}
		}

		return &waevents.Message{
			Info: types.MessageInfo{MessageSource: types.MessageSource{Chat: jid(chat), Sender: jid(voter)}, ID: id},
			Message: &waE2E.Message{PollUpdateMessage: &waE2E.PollUpdateMessage{
				PollCreationMessageKey: &waCommon.MessageKey{RemoteJID: proto.String("900000001@lid"), FromMe: proto.Bool(false), ID: proto.String(poll)},
				Vote:                   &waE2E.PollEncValue{EncPayload: payload, EncIV: iv},
			}},
		}
	}

	cases := []struct {
		name string
		evt  *waevents.Message
		want *whatsmeowv1.PollVote
	}{
		{"a vote", vote("V1", "POLL1", []string{"Sushi", "Tacos"}, false), &whatsmeowv1.PollVote{SelectedOptionHashes: whatsmeow.HashPollOptions([]string{"Sushi", "Tacos"})}},
		{"a cleared vote", vote("V2", "POLL1", nil, false), &whatsmeowv1.PollVote{}},
		{"a vote on a poll without its secret here", vote("V3", "POLL2", []string{"Sushi"}, false), &whatsmeowv1.PollVote{Undecryptable: true}},
		{"a vote that does not decrypt", vote("V4", "POLL1", []string{"Sushi"}, true), &whatsmeowv1.PollVote{Undecryptable: true}},
	}

	handler := h.svc.Handler("s1", device)

	for _, c := range cases {
		if !handler(c.evt) {
			t.Fatalf("%s: the handler withheld the answer", c.name)
		}
	}

	stream := h.open("s1", "c1", 0)

	for _, c := range cases {
		event, err := stream.Recv()
		if err != nil {
			t.Fatalf("Recv: %v", err)
		}

		got := event.GetMessage()
		poll := c.evt.Message.GetPollUpdateMessage().GetPollCreationMessageKey().GetID()

		if !proto.Equal(got.GetPollVote(), c.want) || !proto.Equal(got.GetTarget(), &whatsmeowv1.MessageTarget{Id: poll}) {
			t.Errorf("%s: poll vote %v for %v, want %v for the account's %s", c.name, got.GetPollVote(), got.GetTarget(), c.want, poll)
		}
	}
}

// Only a vote carries one: a text, and a poll itself, have no poll vote.
func TestOnlyAVoteHasAPollVote(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	device := h.savedAccount()
	poll := whatsmeow.NewClient(device, nil).BuildPollCreation("Lunch?", []string{"Pizza", "Sushi"}, 1)

	for _, m := range []*waE2E.Message{{Conversation: proto.String("hi")}, poll} {
		e := &waevents.Message{Info: types.MessageInfo{MessageSource: types.MessageSource{Chat: jid("222@lid"), Sender: jid("222:2@lid")}, ID: "M1"}, Message: m}

		_, event, _, err := toEvent(e, device, whatsmeow.NewClient(device, nil))
		if got := event.GetMessage().GetPollVote(); err != nil || got != nil {
			t.Errorf("%v has poll vote %v, %v; want none", m, got, err)
		}
	}
}

// A vote whose poll secret cannot be read from the store is withheld, never recorded as undecryptable: recording it
// would lose its selection for good, because WhatsApp never redelivers an answered stanza; withheld, it comes again at
// the next login, when the read can succeed (ADR 0041).
func TestAPollVoteWhoseSecretCannotBeReadIsWithheld(t *testing.T) {
	t.Parallel()

	var log bytes.Buffer

	h := start(t, "s1")
	h.svc = New(h.box, zerolog.New(&log))

	// The device's store is another one, closed, so its read fails while the outbox's write still works.
	closed := start(t)
	device := closed.savedAccount()
	closed.stop()

	if h.svc.Handler("s1", device)(unreadVote()) {
		t.Fatal("the handler answered a vote whose poll secret it could not read")
	}

	if highest, err := h.box.Highest(h.ctx, "s1"); err != nil || highest != 0 {
		t.Fatalf("the highest recorded seq is %d, %v; want 0, nothing recorded", highest, err)
	}

	line := log.String()
	if strings.Count(line, "\n") != 1 || !strings.Contains(line, `"level":"error"`) || strings.Contains(line, "222") || strings.Contains(line, "900000001") {
		t.Fatalf("log = %s; want one error line, naming no one", line)
	}
}

// failingDecrypter fails every vote with err.
type failingDecrypter struct{ err error }

func (d failingDecrypter) DecryptPollVote(context.Context, *waevents.Message) (*waE2E.PollVoteMessage, error) {
	return nil, d.err
}

// Only a failed read of the poll's secret fails the mapping, and its error names no one: every other failure is
// permanent, so the vote is recorded undecryptable.
func TestOnlyAFailedSecretReadFailsAVotesMapping(t *testing.T) {
	t.Parallel()

	read := fmt.Errorf("failed to decrypt poll vote: %w", fmt.Errorf("failed to get original message secret key: %w", errors.New("disk I/O error 222")))

	_, event, ok, err := toEvent(unreadVote(), nil, failingDecrypter{read})
	if err == nil || ok || event != nil || strings.Contains(err.Error(), "222") {
		t.Fatalf("a failed read: %v, %v, %v; want an error naming no one", event, ok, err)
	}

	for _, permanent := range []error{
		fmt.Errorf("failed to decrypt poll vote: %w", whatsmeow.ErrOriginalMessageSecretNotFound),
		errors.New("failed to decrypt poll vote: failed to decrypt secret message: cipher: message authentication failed"),
		errors.New("failed to decode poll vote protobuf: proto: cannot parse invalid wire-format data"),
	} {
		_, event, ok, err := toEvent(unreadVote(), nil, failingDecrypter{permanent})
		if err != nil || !ok || !proto.Equal(event.GetMessage().GetPollVote(), &whatsmeowv1.PollVote{Undecryptable: true}) {
			t.Errorf("%v: %v, %v, %v; want it recorded undecryptable", permanent, event, ok, err)
		}
	}
}

// whatsmeow names a failed read of the poll's secret only by its text (msgsecret.go:167), which pollVote matches: this
// fails at a bump that rewords it.
func TestWhatsmeowNamesAFailedSecretReadByItsText(t *testing.T) {
	t.Parallel()

	h := start(t)
	device := h.savedAccount()
	h.stop()

	_, err := whatsmeow.NewClient(device, nil).DecryptPollVote(context.Background(), unreadVote())
	if err == nil || !strings.Contains(err.Error(), secretReadFailed) {
		t.Fatalf("DecryptPollVote on a closed store: %v; want its text to contain %q", err, secretReadFailed)
	}
}

// unreadVote is a vote on the poll POLL1 in a direct chat; its payload is never read, because each test fails or
// stubs the read of the poll's secret before it.
func unreadVote() *waevents.Message {
	return &waevents.Message{
		Info: types.MessageInfo{MessageSource: types.MessageSource{Chat: jid("222@lid"), Sender: jid("222:2@lid")}, ID: "V1"},
		Message: &waE2E.Message{PollUpdateMessage: &waE2E.PollUpdateMessage{
			PollCreationMessageKey: &waCommon.MessageKey{RemoteJID: proto.String("900000001@lid"), FromMe: proto.Bool(false), ID: proto.String("POLL1")},
			Vote:                   &waE2E.PollEncValue{EncPayload: []byte{1, 2, 3}, EncIV: make([]byte, 12)},
		}},
	}
}
