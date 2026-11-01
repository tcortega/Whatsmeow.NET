//go:build fakewhatsapp

package fakewhatsapp

import (
	"errors"
	"fmt"
	"testing"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/contacts"
)

var _ contacts.Client = (*Client)(nil)

const (
	pn  = "5511900000001@s.whatsapp.net"
	lid = "111@lid"
)

func parse(t *testing.T, s string) types.JID {
	t.Helper()

	j, err := types.ParseJID(s)
	if err != nil {
		t.Fatalf("jid %q: %v", s, err)
	}

	return j
}

// next returns the next event of type T, skipping others.
func next[T any](t *testing.T, got chan any) T {
	t.Helper()

	for evt := range got {
		if e, ok := evt.(T); ok {
			return e
		}
	}

	var zero T

	return zero
}

func TestIsOnWhatsAppAnswersFromTheDirectoryAndStoresTheLid(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, _, _ := linked(t)
	if _, err := control.RegisterUser(ctx, &fakev1.RegisterUserRequest{PhoneJid: pn, Lid: lid}); err != nil {
		t.Fatalf("RegisterUser: %v", err)
	}

	got, err := client.IsOnWhatsApp(ctx, []string{"+5511900000001", "+5511900000002"})
	if err != nil {
		t.Fatalf("IsOnWhatsApp: %v", err)
	}

	// As whatsmeow answers with LID addressing (user.go:194-217); an unregistered number's answer is [inferred].
	want := fmt.Sprint([]types.IsOnWhatsAppResponse{
		{Query: "+5511900000001", JID: parse(t, lid), PhoneNumber: parse(t, pn), IsIn: true},
		{Query: "+5511900000002", JID: parse(t, "5511900000002@s.whatsapp.net")},
	})
	if fmt.Sprint(got) != want {
		t.Fatalf("IsOnWhatsApp = %v, want %v", got, want)
	}

	if alt, err := client.Store.LIDs.GetPNForLID(ctx, parse(t, lid)); err != nil || alt.String() != pn {
		t.Fatalf("the store resolves the LID to %v, %v; want %s", alt, err, pn)
	}
}

func TestGetUserInfoAnswersProfilesStoresTheLidAndDispatchesAChangedBusinessName(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, got, _ := linked(t)
	if _, err := control.RegisterUser(ctx, &fakev1.RegisterUserRequest{PhoneJid: pn, Lid: lid, About: "hi", BusinessName: "Ana's Shop"}); err != nil {
		t.Fatalf("RegisterUser: %v", err)
	}

	if _, err := control.SetProfilePicture(ctx, &fakev1.SetProfilePictureRequest{
		Jid: pn, Picture: &fakev1.SetProfilePictureRequest_Set{Set: &fakev1.SetProfilePictureRequest_Picture{Id: "P1", Url: "https://pps.example/p1"}},
	}); err != nil {
		t.Fatalf("SetProfilePicture: %v", err)
	}

	for range 2 {
		users, err := client.GetUserInfo(ctx, []types.JID{parse(t, pn), parse(t, "5511900000009@s.whatsapp.net")})
		if err != nil {
			t.Fatalf("GetUserInfo: %v", err)
		}

		info, ok := users[parse(t, pn)]
		if len(users) != 1 || !ok || info.Status != "hi" || info.PictureID != "P1" || info.LID.String() != lid ||
			info.VerifiedName.Details.GetVerifiedName() != "Ana's Shop" {
			t.Fatalf("GetUserInfo = %+v, want Ana's profile only", users)
		}
	}

	// Dispatched once: the second answer did not change the stored name (user.go:709-736).
	if evt := next[*events.BusinessName](t, got); evt.JID.String() != pn || evt.NewBusinessName != "Ana's Shop" {
		t.Fatalf("BusinessName = %+v", evt)
	}

	select {
	case evt := <-got:
		t.Fatalf("dispatched %T after the first BusinessName", evt)
	default:
	}

	if alt, err := client.Store.LIDs.GetLIDForPN(ctx, parse(t, pn)); err != nil || alt.String() != lid {
		t.Fatalf("the store resolves the number to %v, %v; want %s", alt, err, lid)
	}
}

func TestGetProfilePictureInfoIsTheSetPictureOrNotSetOrHidden(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, _, _ := linked(t)
	for _, req := range []*fakev1.SetProfilePictureRequest{
		{Jid: "120363000000000000@g.us", Picture: &fakev1.SetProfilePictureRequest_Set{Set: &fakev1.SetProfilePictureRequest_Picture{Id: "G1", Url: "https://pps.example/g1"}}},
		{Jid: lid, Picture: &fakev1.SetProfilePictureRequest_Hidden_{Hidden: &fakev1.SetProfilePictureRequest_Hidden{}}},
	} {
		if _, err := control.SetProfilePicture(ctx, req); err != nil {
			t.Fatalf("SetProfilePicture: %v", err)
		}
	}

	if info, err := client.GetProfilePictureInfo(ctx, parse(t, "120363000000000000@g.us"), nil); err != nil || info.ID != "G1" || info.URL != "https://pps.example/g1" {
		t.Fatalf("the group's picture = %+v, %v", info, err)
	}

	if _, err := client.GetProfilePictureInfo(ctx, parse(t, lid), nil); !errors.Is(err, whatsmeow.ErrProfilePictureUnauthorized) || !errors.Is(err, whatsmeow.ErrIQNotAuthorized) {
		t.Fatalf("a hidden picture = %v, want ErrProfilePictureUnauthorized (user.go:625-626)", err)
	}

	if _, err := client.GetProfilePictureInfo(ctx, parse(t, pn), nil); !errors.Is(err, whatsmeow.ErrProfilePictureNotSet) {
		t.Fatalf("no picture = %v, want ErrProfilePictureNotSet (user.go:627-628)", err)
	}
}

func TestUpdateBlocklistFetchesTheLidAsWhatsmeowDoesAndGetBlocklistReflectsIt(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, _, _ := linked(t)
	if _, err := control.RegisterUser(ctx, &fakev1.RegisterUserRequest{PhoneJid: pn, Lid: lid}); err != nil {
		t.Fatalf("RegisterUser: %v", err)
	}

	// The LID is not in the store yet: whatsmeow fetches it with GetUserInfo (user.go:1030-1045).
	if _, err := client.UpdateBlocklist(ctx, parse(t, pn), events.BlocklistChangeActionBlock, ""); err != nil {
		t.Fatalf("block: %v", err)
	}

	list, err := client.GetBlocklist(ctx, "")
	if err != nil || fmt.Sprint(list.Items) != fmt.Sprint([]types.BlocklistItem{{LID: parse(t, lid), PN: parse(t, pn), Active: true}}) {
		t.Fatalf("GetBlocklist = %+v, %v; want the user", list, err)
	}

	if _, err := client.UpdateBlocklist(ctx, parse(t, lid), events.BlocklistChangeActionUnblock, ""); err != nil {
		t.Fatalf("unblock: %v", err)
	}

	if list, err := client.GetBlocklist(ctx, ""); err != nil || len(list.Items) != 0 {
		t.Fatalf("GetBlocklist = %+v, %v; want none", list, err)
	}

	if _, err := client.UpdateBlocklist(ctx, parse(t, "5511900000009@s.whatsapp.net"), events.BlocklistChangeActionBlock, ""); !errors.Is(err, whatsmeow.ErrRecipientNotOnWhatsApp) {
		t.Fatalf("blocking an unregistered number = %v, want ErrRecipientNotOnWhatsApp", err)
	}
}

func TestFailNextContactQueryFailsOnlyTheNextQuery(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, _, _ := linked(t)
	if _, err := control.FailNextContactQuery(ctx, &fakev1.FailNextContactQueryRequest{SessionId: "s1", Code: 429}); err != nil {
		t.Fatalf("FailNextContactQuery: %v", err)
	}

	var iqErr *whatsmeow.IQError
	if _, err := client.IsOnWhatsApp(ctx, []string{"+5511900000001"}); !errors.As(err, &iqErr) || iqErr.Code != 429 {
		t.Fatalf("the next query = %v, want an IQ error of code 429", err)
	}

	if _, err := client.GetBlocklist(ctx, ""); err != nil {
		t.Fatalf("the query after = %v, want success", err)
	}
}

func TestDeliverMessageStoresTheLidPairAndAChangedPushNameAsWhatsmeowDoes(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, got, _ := linked(t)

	for _, id := range []string{"M1", "M2"} {
		if _, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{
			SessionId: "s1", Id: id, Chat: lid, Sender: "111:2@lid", SenderAlt: "5511900000001:2@s.whatsapp.net", PushName: "Ana", Text: "hi",
		}); err != nil {
			t.Fatalf("DeliverMessage: %v", err)
		}
	}

	// message.go:51-55: the pair is stored before the message is handled.
	if alt, err := client.Store.LIDs.GetPNForLID(ctx, parse(t, lid)); err != nil || alt.String() != pn {
		t.Fatalf("the store resolves the LID to %v, %v; want %s", alt, err, pn)
	}

	// message.go:59-61, user.go:679-707: stored under both forms, dispatched once with the alt. whatsmeow updates it on
	// a goroutine, so its order with the message is not fixed; the fake updates it first.
	if _, ok := (<-got).(*events.PairSuccess); !ok {
		t.Fatal("the first event is not PairSuccess")
	}

	if evt, ok := (<-got).(*events.PushName); !ok || evt.JID.String() != lid || evt.JIDAlt.String() != pn || evt.NewPushName != "Ana" {
		t.Fatalf("PushName = %+v", evt)
	}

	for _, want := range []string{"M1", "M2"} {
		if evt, ok := (<-got).(*events.Message); !ok || evt.Info.ID != want {
			t.Fatalf("event = %+v, want message %s and no second PushName", evt, want)
		}
	}

	for _, j := range []string{lid, pn} {
		if contact, err := client.Store.Contacts.GetContact(ctx, parse(t, j)); err != nil || contact.PushName != "Ana" {
			t.Fatalf("the stored contact %s = %+v, %v", j, contact, err)
		}
	}
}

func TestDeliverContactNotificationDispatchesPictureAndAbout(t *testing.T) {
	t.Parallel()

	_, control, ctx, _, got, _ := linked(t)

	for _, req := range []*fakev1.DeliverContactNotificationRequest{
		{SessionId: "s1", Kind: &fakev1.DeliverContactNotificationRequest_Picture_{Picture: &fakev1.DeliverContactNotificationRequest_Picture{Jid: lid, Id: "P2"}}},
		{SessionId: "s1", Kind: &fakev1.DeliverContactNotificationRequest_Picture_{Picture: &fakev1.DeliverContactNotificationRequest_Picture{Jid: "120363000000000000@g.us", Removed: true}}},
		{SessionId: "s1", Kind: &fakev1.DeliverContactNotificationRequest_About_{About: &fakev1.DeliverContactNotificationRequest_About{Jid: lid, Text: "busy"}}},
	} {
		if _, err := control.DeliverContactNotification(ctx, req); err != nil {
			t.Fatalf("DeliverContactNotification: %v", err)
		}
	}

	if evt := next[*events.Picture](t, got); evt.JID.String() != lid || evt.PictureID != "P2" || evt.Remove {
		t.Fatalf("Picture = %+v", evt)
	}

	if evt := next[*events.Picture](t, got); evt.JID.String() != "120363000000000000@g.us" || !evt.Remove {
		t.Fatalf("Picture = %+v", evt)
	}

	if evt := next[*events.UserAbout](t, got); evt.JID.String() != lid || evt.Status != "busy" {
		t.Fatalf("UserAbout = %+v", evt)
	}
}

func TestSyncContactStoresTheSavedNameThenDispatchesContact(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, got, _ := linked(t)
	if _, err := control.SyncContact(ctx, &fakev1.SyncContactRequest{SessionId: "s1", Jid: pn, FirstName: "Ana", FullName: "Ana Souza"}); err != nil {
		t.Fatalf("SyncContact: %v", err)
	}

	if evt := next[*events.Contact](t, got); evt.JID.String() != pn || evt.Action.GetFullName() != "Ana Souza" {
		t.Fatalf("Contact = %+v", evt)
	}

	if contact, err := client.Store.Contacts.GetContact(ctx, parse(t, pn)); err != nil || contact.FullName != "Ana Souza" || contact.FirstName != "Ana" {
		t.Fatalf("the stored contact = %+v, %v", contact, err)
	}
}
