package contacts

import (
	"context"
	"errors"
	"slices"
	"strings"
	"testing"

	"go.mau.fi/whatsmeow"
	waBinary "go.mau.fi/whatsmeow/binary"
	"go.mau.fi/whatsmeow/proto/waVnameCert"
	"go.mau.fi/whatsmeow/types"
	waLog "go.mau.fi/whatsmeow/util/log"
	"google.golang.org/grpc/codes"
	"google.golang.org/protobuf/proto"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// usync stands in for whatsmeow's internals: it answers the usync query with list and records the business name
// updates.
type usync struct {
	list    *waBinary.Node
	err     error
	asked   []string
	updated []string
}

func (u *usync) Usync(_ context.Context, jids []types.JID, mode, context string, query []waBinary.Node, extra ...whatsmeow.UsyncQueryExtras) (*waBinary.Node, error) {
	tags := make([]string, 0, len(query))
	for _, q := range query {
		tags = append(tags, q.Tag)
	}

	u.asked = append(u.asked, mode, context, strings.Join(tags, ""), jids[0].String())
	if len(extra) != 1 || !extra[0].IncludePrivacyToken {
		u.asked = append(u.asked, "no privacy token")
	}

	return u.list, u.err
}

func (u *usync) UpdateBusinessName(_ context.Context, user, userAlt types.JID, _ *types.MessageInfo, name string) {
	u.updated = append(u.updated, user.String()+" "+userAlt.String()+" "+name)
}

// certificate is a verified name certificate as WhatsApp sends it: the details, marshalled, inside the certificate.
func certificate(t *testing.T, name string) []byte {
	t.Helper()

	details, err := proto.Marshal(&waVnameCert.VerifiedNameCertificate_Details{VerifiedName: proto.String(name)})
	if err != nil {
		t.Fatalf("marshal the details: %v", err)
	}

	raw, err := proto.Marshal(&waVnameCert.VerifiedNameCertificate{Details: details})
	if err != nil {
		t.Fatalf("marshal the certificate: %v", err)
	}

	return raw
}

// userNode is a usync answer's user node with the given children.
func userNode(j string, children ...waBinary.Node) waBinary.Node {
	return waBinary.Node{Tag: "user", Attrs: waBinary.Attrs{"jid": jid(j)}, Content: children}
}

func business(content any) waBinary.Node {
	return waBinary.Node{Tag: "business", Content: []waBinary.Node{{Tag: "verified_name", Content: content}}}
}

func name(info types.UserInfo) string {
	if info.VerifiedName == nil {
		return "<nil>"
	}

	return info.VerifiedName.Details.GetVerifiedName()
}

// The answer carries the verified name WhatsApp sent in it, keyed as asked, by phone number or LID (#153).
func TestUserInfoCarriesTheVerifiedNameOfTheAnswer(t *testing.T) {
	t.Parallel()

	h := start(t)
	list := &waBinary.Node{Tag: "list", Content: []waBinary.Node{
		userNode("5511900000001@s.whatsapp.net",
			business(certificate(t, "Ana's Shop")),
			waBinary.Node{Tag: "status", Content: []byte("hi")},
			waBinary.Node{Tag: "picture", Attrs: waBinary.Attrs{"id": "P1"}},
			waBinary.Node{Tag: "lid", Attrs: waBinary.Attrs{"val": jid("111@lid")}},
		),
		userNode("222@lid", business(certificate(t, "Bia's Shop"))),
	}}
	in := &usync{list: list}

	answers, err := userInfo(h.ctx, in, h.device.LIDs, waLog.Noop, []types.JID{jid("5511900000001@s.whatsapp.net"), jid("222@lid")})
	if err != nil {
		t.Fatalf("userInfo: %v", err)
	}

	pn := answers[jid("5511900000001@s.whatsapp.net")]
	if len(answers) != 2 || name(pn) != "Ana's Shop" || pn.Status != "hi" || pn.PictureID != "P1" || pn.LID != jid("111@lid") {
		t.Fatalf("the phone number's entry = %+v (name %s) of %d, want Ana's Shop, hi, P1 and 111@lid", pn, name(pn), len(answers))
	}

	if lid := answers[jid("222@lid")]; name(lid) != "Bia's Shop" || !lid.LID.IsEmpty() {
		t.Fatalf("the LID's entry = %+v (name %s), want Bia's Shop and no LID", lid, name(lid))
	}

	// whatsmeow's own steps on the answer: the stored names, with the event a change dispatches, and the LID pair.
	want := []string{"5511900000001@s.whatsapp.net 111@lid Ana's Shop", "222@lid  Bia's Shop"}
	if !slices.Equal(in.updated, want) {
		t.Fatalf("business name updates = %q, want %q", in.updated, want)
	}

	if got, err := h.device.LIDs.GetPNForLID(h.ctx, jid("111@lid")); err != nil || got != jid("5511900000001@s.whatsapp.net") {
		t.Fatalf("the stored pair of 111@lid = %v, %v", got, err)
	}

	// GetUserInfo's query, privacy token included (user.go:233-241).
	if want := []string{"full", "background", "businessstatuspicturedeviceslid", "5511900000001@s.whatsapp.net"}; !slices.Equal(in.asked, want) {
		t.Fatalf("asked %q, want %q", in.asked, want)
	}
}

// No verified name in the answer is no name, whatever the store holds; a certificate that does not parse is no name
// either, and the rest of the entry stays; a node that is not a user with a JID is skipped (user.go:248-256).
func TestUserInfoHasNoVerifiedNameWhenTheAnswerCarriesNone(t *testing.T) {
	t.Parallel()

	h := start(t)
	if _, _, err := h.device.Contacts.PutBusinessName(h.ctx, jid("5511900000001@s.whatsapp.net"), "Earlier Name"); err != nil {
		t.Fatalf("store an earlier name: %v", err)
	}

	list := &waBinary.Node{Tag: "list", Content: []waBinary.Node{
		userNode("5511900000001@s.whatsapp.net", waBinary.Node{Tag: "status", Content: []byte("hi")}),
		userNode("5511900000002@s.whatsapp.net", waBinary.Node{Tag: "business"}),
		userNode("5511900000003@s.whatsapp.net", business([]byte{0xff}), waBinary.Node{Tag: "picture", Attrs: waBinary.Attrs{"id": "P3"}}),
		{Tag: "user", Attrs: waBinary.Attrs{"jid": "5511900000004@s.whatsapp.net"}},
		{Tag: "other", Attrs: waBinary.Attrs{"jid": jid("5511900000005@s.whatsapp.net")}},
	}}
	in := &usync{list: list}

	answers, err := userInfo(h.ctx, in, h.device.LIDs, waLog.Noop, []types.JID{jid("5511900000001@s.whatsapp.net")})
	if err != nil {
		t.Fatalf("userInfo: %v", err)
	}

	if len(answers) != 3 {
		t.Fatalf("answers = %v, want the three users with a JID", answers)
	}

	for j, info := range answers {
		if info.VerifiedName != nil {
			t.Fatalf("%s has verified name %s, want none", j, name(info))
		}
	}

	if answers[jid("5511900000001@s.whatsapp.net")].Status != "hi" || answers[jid("5511900000003@s.whatsapp.net")].PictureID != "P3" {
		t.Fatalf("answers = %v, want the about and the picture kept", answers)
	}

	if len(in.updated) != 0 {
		t.Fatalf("business name updates = %q, want none", in.updated)
	}
}

func TestUserInfoFailsAsTheUsyncQuery(t *testing.T) {
	t.Parallel()

	h := start(t)
	in := &usync{err: whatsmeow.ErrIQTimedOut}

	if _, err := userInfo(h.ctx, in, h.device.LIDs, waLog.Noop, []types.JID{jid("111@lid")}); !errors.Is(err, whatsmeow.ErrIQTimedOut) {
		t.Fatalf("err = %v, want whatsmeow's", err)
	}
}

// A real whatsmeow client goes through the usync query, which fails as GetUserInfo's when not connected.
func TestGetUserInfoOnAWhatsmeowClientNotConnectedIsNotConnected(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.sessions.client = whatsmeow.NewClient(h.device, waLog.Noop)

	_, err := h.client.GetUserInfo(h.ctx, &whatsmeowv1.GetUserInfoRequest{SessionId: "s1", Jids: []string{"111@lid"}})
	wantReason(t, err, codes.Unavailable, "NOT_CONNECTED", nil)
}
