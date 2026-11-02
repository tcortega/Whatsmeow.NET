package events

import (
	"bytes"
	"context"
	"testing"

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
// store, which whatsmeow put there when the poll was sent or received (send.go:403-410, message.go:964-985). A vote the
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

		_, event, _ := toEvent(e, device, whatsmeow.NewClient(device, nil))
		if got := event.GetMessage().GetPollVote(); got != nil {
			t.Errorf("%v has poll vote %v, want none", m, got)
		}
	}
}
