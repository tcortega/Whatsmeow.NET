package contacts

// This file works around #153: at the pin, whatsmeow's GetUserInfo parses the verified business name and never sets it
// on its answer (user.go:253, 269-272). The upstream fix is tulir/whatsmeow#1334 (ADR 0065). Delete this file, and call
// client.GetUserInfo again, once the pin's GetUserInfo sets VerifiedName (ARCHITECTURE §14, Contacts).

import (
	"context"
	"fmt"

	"go.mau.fi/whatsmeow"
	waBinary "go.mau.fi/whatsmeow/binary"
	"go.mau.fi/whatsmeow/proto/waVnameCert"
	"go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/types"
	waLog "go.mau.fi/whatsmeow/util/log"
	"google.golang.org/protobuf/proto"
)

// internals is what getUserInfo needs of whatsmeow's DangerousInternalClient: the usync query GetUserInfo sends, and
// the business name update it runs on the answer.
type internals interface {
	Usync(ctx context.Context, jids []types.JID, mode, context string, query []waBinary.Node, extra ...whatsmeow.UsyncQueryExtras) (*waBinary.Node, error)
	UpdateBusinessName(ctx context.Context, user, userAlt types.JID, messageInfo *types.MessageInfo, name string)
}

var _ internals = (*whatsmeow.DangerousInternalClient)(nil)

// getUserInfo is whatsmeow's GetUserInfo with the verified name on the answer. A *whatsmeow.Client goes through
// userInfo; any other client, the fake WhatsApp's or a test's, answers with its own GetUserInfo.
func getUserInfo(ctx context.Context, client Client, jids []types.JID) (map[types.JID]types.UserInfo, error) {
	wa, ok := client.(*whatsmeow.Client)
	if !ok {
		return client.GetUserInfo(ctx, jids) //nolint:wrapcheck // classified by the caller, as whatsmeow's own
	}

	//nolint:staticcheck // SA1019: DangerousInternals is deprecated; its Usync is the only way to read the verified name GetUserInfo drops (#153, tulir/whatsmeow#1334); removed with this file per ARCHITECTURE §14, Contacts
	return userInfo(ctx, wa.DangerousInternals(), wa.Store.LIDs, wa.Log, jids)
}

// userInfo is GetUserInfo at the pin (user.go:232-280), step for step, with the parsed verified name set on each entry.
// It skips the device list, which the contract does not carry.
func userInfo(ctx context.Context, in internals, lids store.LIDStore, log waLog.Logger, jids []types.JID) (map[types.JID]types.UserInfo, error) {
	list, err := in.Usync(ctx, jids, "full", "background", []waBinary.Node{
		{Tag: "business", Content: []waBinary.Node{{Tag: "verified_name"}}},
		{Tag: "status"},
		{Tag: "picture"},
		{Tag: "devices", Attrs: waBinary.Attrs{"version": "2"}},
		{Tag: "lid"},
	}, whatsmeow.UsyncQueryExtras{
		IncludePrivacyToken: true,
	})
	if err != nil {
		return nil, err //nolint:wrapcheck // classified by the caller, as GetUserInfo's own
	}

	answers := make(map[types.JID]types.UserInfo, len(jids))
	mappings := make([]store.LIDMapping, 0, len(jids))

	for _, child := range list.GetChildren() {
		jid, ok := child.Attrs["jid"].(types.JID)
		if child.Tag != "user" || !ok {
			continue
		}

		var info types.UserInfo

		verifiedName, err := parseVerifiedName(child.GetChildByTag("business"))
		if err != nil {
			log.Warnf("Failed to parse %s's verified name details: %v", jid, err)
		}

		status, _ := child.GetChildByTag("status").Content.([]byte)
		info.Status = string(status)
		info.PictureID, _ = child.GetChildByTag("picture").Attrs["id"].(string)
		lid := child.GetChildByTag("lid")
		info.LID = lid.AttrGetter().OptionalJIDOrEmpty("val")

		if !info.LID.IsEmpty() {
			mappings = append(mappings, store.LIDMapping{PN: jid, LID: info.LID})
		}

		// The one line GetUserInfo lacks at the pin (#153).
		info.VerifiedName = verifiedName

		if verifiedName != nil {
			in.UpdateBusinessName(ctx, jid, info.LID, nil, verifiedName.Details.GetVerifiedName())
		}

		answers[jid] = info
	}

	if err := lids.PutManyLIDMappings(ctx, mappings); err != nil {
		// whatsmeow logs this and answers anyway (user.go:275-279).
		log.Errorf("Failed to place LID mappings from USync call")
	}

	return answers, nil
}

// parseVerifiedName is whatsmeow's (user.go:737-775), unexported there: the certificate and its details, unchecked, as
// whatsmeow reads them; the attributes the contract does not carry are left out.
func parseVerifiedName(business waBinary.Node) (*types.VerifiedName, error) {
	if business.Tag != "business" {
		return nil, nil
	}

	node, ok := business.GetOptionalChildByTag("verified_name")
	if !ok {
		return nil, nil
	}

	raw, ok := node.Content.([]byte)
	if !ok {
		return nil, nil
	}

	var cert waVnameCert.VerifiedNameCertificate
	if err := proto.Unmarshal(raw, &cert); err != nil {
		return nil, fmt.Errorf("verified name certificate: %w", err)
	}

	var details waVnameCert.VerifiedNameCertificate_Details
	if err := proto.Unmarshal(cert.GetDetails(), &details); err != nil {
		return nil, fmt.Errorf("verified name details: %w", err)
	}

	return &types.VerifiedName{Certificate: &cert, Details: &details}, nil
}
