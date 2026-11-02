//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"fmt"
	"slices"
	"strings"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waVnameCert"
	"go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
)

// user is one WhatsApp user of the directory RegisterUser fills.
type user struct {
	pn, lid      types.JID
	about        string
	businessName string
}

// picture is what WhatsApp answers for one profile picture.
type picture struct {
	id, url string
	hidden  bool
}

// directory is WhatsApp's side of the contact queries, shared by every session: the users, by either form, the
// pictures, and each session's blocklist. w.mu guards it.
type directory struct {
	users      map[types.JID]*user
	pictures   map[types.JID]picture
	blocklists map[string][]types.BlocklistItem
}

func (w *WhatsApp) registerUser(u *user) {
	w.mu.Lock()
	defer w.mu.Unlock()

	w.users[u.pn] = u
	if !u.lid.IsEmpty() {
		w.users[u.lid] = u
	}
}

func (w *WhatsApp) lookUp(jid types.JID) (*user, bool) {
	w.mu.Lock()
	defer w.mu.Unlock()

	u, ok := w.users[jid.ToNonAD()]

	return u, ok
}

func (w *WhatsApp) setPicture(jid types.JID, p picture) {
	w.mu.Lock()
	defer w.mu.Unlock()

	w.pictures[jid.ToNonAD()] = p
}

func (w *WhatsApp) pictureOf(jid types.JID) (picture, bool) {
	w.mu.Lock()
	defer w.mu.Unlock()

	p, ok := w.pictures[jid.ToNonAD()]

	return p, ok
}

// query is WhatsApp answering one contact query of the client: the IQ error FailNextContactQuery scripted, or nil.
// A failed usync query (IsOnWhatsApp, GetUserInfo) is wrapped as whatsmeow wraps it (user.go:950); the others return
// it as sendIQ does (request.go:152-182).
func (c *Client) query() error {
	var code int32

	if c.whatsApp.take(c.sessionID, func(s *script) bool { code = s.failContactQuery; return code != 0 }, func(s *script) { s.failContactQuery = 0 }) {
		return &whatsmeow.IQError{Code: int(code), Text: "fake WhatsApp: FailNextContactQuery"}
	}

	return nil
}

// IsOnWhatsApp answers from the directory as whatsmeow parses WhatsApp's answer with LID addressing: the LID as the
// JID when the user has one, the phone number beside it, and the query as sent (user.go:176-229). It stores the LID
// pairs. An unregistered number is answered with its phone-number JID and not in [inferred: unmeasured].
func (c *Client) IsOnWhatsApp(ctx context.Context, phones []string) ([]types.IsOnWhatsAppResponse, error) {
	if err := c.query(); err != nil {
		return nil, fmt.Errorf("failed to send usync query: %w", err)
	}

	answers := make([]types.IsOnWhatsAppResponse, 0, len(phones))
	mappings := make([]store.LIDMapping, 0, len(phones))

	for _, phone := range phones {
		pn := types.NewJID(strings.TrimPrefix(phone, "+"), types.DefaultUserServer)
		answer := types.IsOnWhatsAppResponse{Query: phone, JID: pn}

		if u, ok := c.whatsApp.lookUp(pn); ok {
			answer.IsIn, answer.PhoneNumber = true, u.pn
			if !u.lid.IsEmpty() {
				answer.JID = u.lid
				mappings = append(mappings, store.LIDMapping{LID: u.lid, PN: u.pn})
			}
		}

		answers = append(answers, answer)
	}

	if err := c.Store.LIDs.PutManyLIDMappings(ctx, mappings); err != nil {
		return answers, fmt.Errorf("failed to store LID mappings: %w", err)
	}

	return answers, nil
}

// GetUserInfo answers from the directory, for the users it has (user.go:232-280): the about text, the picture's id,
// the LID and the verified business name. It stores the LID pairs, and stores a business name, dispatching
// BusinessName when it changed, as updateBusinessName does (user.go:709-736). A user the directory lacks is not in the
// answer [inferred: unmeasured].
func (c *Client) GetUserInfo(ctx context.Context, jids []types.JID) (map[types.JID]types.UserInfo, error) {
	if err := c.query(); err != nil {
		return nil, fmt.Errorf("failed to send usync query: %w", err)
	}

	answers := make(map[types.JID]types.UserInfo, len(jids))
	mappings := make([]store.LIDMapping, 0, len(jids))

	for _, jid := range jids {
		u, ok := c.whatsApp.lookUp(jid)
		if !ok {
			continue
		}

		info := types.UserInfo{Status: u.about, LID: u.lid}
		if p, ok := c.whatsApp.pictureOf(jid); ok && !p.hidden {
			info.PictureID = p.id
		}

		if !u.lid.IsEmpty() {
			mappings = append(mappings, store.LIDMapping{LID: u.lid, PN: u.pn})
		}

		if u.businessName != "" {
			info.VerifiedName = &types.VerifiedName{Details: &waVnameCert.VerifiedNameCertificate_Details{VerifiedName: proto.String(u.businessName)}}
			c.updateBusinessName(ctx, jid, u.lid, u.businessName)
		}

		answers[jid] = info
	}

	if err := c.Store.LIDs.PutManyLIDMappings(ctx, mappings); err != nil {
		c.whatsApp.log.Error().Str("session_id", c.sessionID).Msg("fake WhatsApp: storing the LID mappings failed")
	}

	return answers, nil
}

// updateBusinessName is whatsmeow's (user.go:709-736): it stores the name and dispatches BusinessName when it changed.
func (c *Client) updateBusinessName(ctx context.Context, jid, alt types.JID, name string) {
	changed, previous, err := c.Store.Contacts.PutBusinessName(ctx, jid, name)
	if err != nil || !changed {
		return
	}

	if !alt.IsEmpty() {
		_, _, _ = c.Store.Contacts.PutBusinessName(ctx, alt, name)
	}

	c.runHandlers(&events.BusinessName{JID: jid, OldBusinessName: previous, NewBusinessName: name})
}

// GetProfilePictureInfo answers what SetProfilePicture set, with whatsmeow's errors for a hidden picture and for none
// (user.go:624-629).
func (c *Client) GetProfilePictureInfo(_ context.Context, jid types.JID, _ *whatsmeow.GetProfilePictureParams) (*types.ProfilePictureInfo, error) {
	if err := c.query(); err != nil {
		return nil, err
	}

	p, ok := c.whatsApp.pictureOf(jid)

	switch {
	case !ok:
		return nil, fmt.Errorf("%w: %w", whatsmeow.ErrProfilePictureNotSet, whatsmeow.ErrIQNotFound)
	case p.hidden:
		return nil, fmt.Errorf("%w: %w", whatsmeow.ErrProfilePictureUnauthorized, whatsmeow.ErrIQNotAuthorized)
	default:
		return &types.ProfilePictureInfo{ID: p.id, URL: p.url, Type: "image"}, nil
	}
}

// GetBlocklist answers the session's blocklist, all of it (no dhash).
func (c *Client) GetBlocklist(context.Context, string) (*types.Blocklist, error) {
	if err := c.query(); err != nil {
		return nil, err
	}

	c.whatsApp.mu.Lock()
	defer c.whatsApp.mu.Unlock()

	return &types.Blocklist{Items: slices.Clone(c.whatsApp.blocklists[c.sessionID])}, nil
}

// lidForPN is a phone number's LID as whatsmeow's UpdateBlocklist and SendMessage get it (user.go:1030-1045,
// send.go:341-365): from the store, else from GetUserInfo, and without one ErrRecipientNotOnWhatsApp. Its errors carry
// whatsmeow's text, the JID in it.
func (c *Client) lidForPN(ctx context.Context, pn types.JID) (types.JID, error) {
	lid, err := c.Store.LIDs.GetLIDForPN(ctx, pn)
	if err != nil {
		return types.JID{}, fmt.Errorf("failed to get LID for PN %s: %w", pn, err)
	}

	if !lid.IsEmpty() {
		return lid, nil
	}

	info, err := c.GetUserInfo(ctx, []types.JID{pn})
	if err != nil {
		return types.JID{}, fmt.Errorf("failed to get user info for %s to fill LID cache: %w", pn, err)
	}

	if lid = info[pn].LID; lid.IsEmpty() {
		return types.JID{}, fmt.Errorf("%w: no LID found for %s from server", whatsmeow.ErrRecipientNotOnWhatsApp, pn)
	}

	return lid, nil
}

// UpdateBlocklist does what whatsmeow's does before its request (user.go:1027-1066): a phone number's LID comes from
// the store, else from GetUserInfo, and without one it is ErrRecipientNotOnWhatsApp; blocking a LID looks its phone
// number up. Its errors carry whatsmeow's text, the JID in it. WhatsApp then changes the session's list and answers it.
func (c *Client) UpdateBlocklist(ctx context.Context, jid types.JID, action events.BlocklistChangeAction, _ string) (*types.Blocklist, error) {
	var lid, pn types.JID

	switch jid.Server {
	case types.DefaultUserServer:
		pn = jid

		var err error
		if lid, err = c.lidForPN(ctx, jid); err != nil {
			return nil, err
		}
	case types.HiddenUserServer:
		lid = jid
		if action == events.BlocklistChangeActionBlock {
			var err error
			if pn, err = c.Store.LIDs.GetPNForLID(ctx, jid); err != nil {
				return nil, fmt.Errorf("failed to resolve PN for %s: %w", jid, err)
			}
		}
	}

	if err := c.query(); err != nil {
		return nil, err
	}

	c.whatsApp.mu.Lock()
	defer c.whatsApp.mu.Unlock()

	list := slices.DeleteFunc(c.whatsApp.blocklists[c.sessionID], func(item types.BlocklistItem) bool { return item.LID == lid })
	if action == events.BlocklistChangeActionBlock {
		list = append(list, types.BlocklistItem{LID: lid, PN: pn, Active: true})
	}

	c.whatsApp.blocklists[c.sessionID] = list

	return &types.Blocklist{Items: slices.Clone(list)}, nil
}

// learn is what whatsmeow does with a message's info before it decrypts it (message.go:51-61): it stores the LID pair
// the message carries, and the sender's push name. whatsmeow updates the push name on a goroutine, so its order with
// the message is not fixed; the fake updates it first. A client without a store (a fake test's) learns nothing.
func (c *Client) learn(ctx context.Context, info *types.MessageInfo) {
	if c.Store == nil || c.Store.LIDs == nil {
		return
	}

	// The store writes are WhatsApp's to finish, not the control call's.
	ctx = context.WithoutCancel(ctx)

	switch {
	case !info.SenderAlt.IsEmpty():
		c.storeLIDPNMapping(ctx, info.SenderAlt, info.Sender)
	case !info.RecipientAlt.IsEmpty():
		c.storeLIDPNMapping(ctx, info.RecipientAlt, info.Chat)
	}

	if info.PushName != "" && info.PushName != "-" {
		c.updatePushName(ctx, info.Sender, info.SenderAlt, info, info.PushName)
	}
}

// storeLIDPNMapping is whatsmeow's (client.go:1085-1100).
func (c *Client) storeLIDPNMapping(ctx context.Context, first, second types.JID) {
	var lid, pn types.JID

	switch {
	case first.Server == types.HiddenUserServer && second.Server == types.DefaultUserServer:
		lid, pn = first, second
	case first.Server == types.DefaultUserServer && second.Server == types.HiddenUserServer:
		lid, pn = second, first
	default:
		return
	}

	if err := c.Store.LIDs.PutLIDMapping(ctx, lid, pn); err != nil {
		c.whatsApp.log.Error().Str("session_id", c.sessionID).Msg("fake WhatsApp: storing a LID mapping failed")
	}
}

// updatePushName is whatsmeow's (user.go:679-707): it stores the name under both forms and dispatches PushName when it
// changed.
func (c *Client) updatePushName(ctx context.Context, sender, alt types.JID, info *types.MessageInfo, name string) {
	sender = sender.ToNonAD()

	changed, previous, err := c.Store.Contacts.PutPushName(ctx, sender, name)
	if err != nil || !changed {
		return
	}

	alt = alt.ToNonAD()
	if alt.IsEmpty() {
		alt, _ = c.Store.GetAltJID(ctx, sender)
	}

	if !alt.IsEmpty() {
		_, _, _ = c.Store.Contacts.PutPushName(ctx, alt, name)
	}

	c.runHandlers(&events.PushName{JID: sender, JIDAlt: alt, Message: info, OldPushName: previous, NewPushName: name})
}
