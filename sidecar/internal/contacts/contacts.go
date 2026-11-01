// Package contacts is the Contacts & LID slice: lookups of WhatsApp users, the two address forms of a user (phone
// number and LID), the session's contacts and blocklist, and the contact change events (contact.proto, ADR 0028).
//
// Phone numbers and JIDs are other people's personal data: nothing here logs them, and no error text of this package
// carries one (ARCHITECTURE §7.8).
package contacts

import (
	"cmp"
	"context"
	"errors"
	"fmt"
	"regexp"
	"slices"
	"strings"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/session"
)

// rateOverLimit is the code of whatsmeow's ErrIQRateOverLimit (errors.go:197). Its Is also compares the text
// (errors.go:229-238), so the code alone is matched.
const rateOverLimit = 429

// phoneNumber is a number as the contract takes it: digits with an optional leading +.
var phoneNumber = regexp.MustCompile(`^\+?[0-9]+$`)

// Client is what the slice needs of a whatsmeow client. *whatsmeow.Client and the fake WhatsApp's client both
// satisfy it (ADR 0019).
type Client interface {
	IsOnWhatsApp(ctx context.Context, phones []string) ([]types.IsOnWhatsAppResponse, error)
	GetUserInfo(ctx context.Context, jids []types.JID) (map[types.JID]types.UserInfo, error)
	GetProfilePictureInfo(ctx context.Context, jid types.JID, params *whatsmeow.GetProfilePictureParams) (*types.ProfilePictureInfo, error)
	GetBlocklist(ctx context.Context, dhash string) (*types.Blocklist, error)
	UpdateBlocklist(ctx context.Context, jid types.JID, action events.BlocklistChangeAction, dhash string) (*types.Blocklist, error)
}

var _ Client = (*whatsmeow.Client)(nil)

// Sessions is what the slice needs of the Sessions slice: a session's client while it is online, and its device for
// what the store learned.
type Sessions interface {
	Online(id string) (session.Client, <-chan struct{}, error)
	Device(id string) (*store.Device, error)
}

// Service implements whatsmeowv1.ContactServiceServer.
type Service struct {
	whatsmeowv1.UnimplementedContactServiceServer

	sessions Sessions
}

// New returns the slice over sessions.
func New(sessions Sessions) *Service { return &Service{sessions: sessions} }

// client is the session's client for a call to WhatsApp; a session that is not online fails with its reason.
func (svc *Service) client(id string) (Client, error) {
	c, _, err := svc.sessions.Online(id)
	if err != nil {
		return nil, fmt.Errorf("contacts: %w", err)
	}

	contacts, ok := c.(Client)
	if !ok {
		return nil, fmt.Errorf("session %q: its client %T has no contact queries", id, c)
	}

	return contacts, nil
}

func checkSession(id string) error {
	if err := server.CheckID("session_id", id); err != nil {
		return fmt.Errorf("session id: %w", err)
	}

	return nil
}

// IsOnWhatsApp checks phone numbers, one result per number in request order (contact.proto).
func (svc *Service) IsOnWhatsApp(ctx context.Context, req *whatsmeowv1.IsOnWhatsAppRequest) (*whatsmeowv1.IsOnWhatsAppResponse, error) {
	if err := checkSession(req.GetSessionId()); err != nil {
		return nil, err
	}

	// whatsmeow wants the international form with its + (user.go:174-175), and answers with it as the query.
	queries := make([]string, len(req.GetPhoneNumbers()))
	for i, phone := range req.GetPhoneNumbers() {
		if !phoneNumber.MatchString(phone) {
			return nil, fmt.Errorf("is on WhatsApp: %w", server.InvalidArgument("phone_numbers", "entry %d is not digits with an optional leading +", i))
		}

		queries[i] = "+" + strings.TrimPrefix(phone, "+")
	}

	resp := &whatsmeowv1.IsOnWhatsAppResponse{Results: make([]*whatsmeowv1.PhoneNumberCheck, 0, len(queries))}
	if len(queries) == 0 {
		return resp, nil
	}

	client, err := svc.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	answers, err := client.IsOnWhatsApp(ctx, queries)
	if err != nil {
		return nil, classify("is on WhatsApp", err, false)
	}

	byQuery := make(map[string]types.IsOnWhatsAppResponse, len(answers))
	for _, answer := range answers {
		byQuery[answer.Query] = answer
	}

	for i, query := range queries {
		check := &whatsmeowv1.PhoneNumberCheck{PhoneNumber: req.GetPhoneNumbers()[i]}

		// A number WhatsApp did not answer for is taken as not on WhatsApp.
		if answer, ok := byQuery[query]; ok && answer.IsIn {
			check.OnWhatsapp = true

			// With LID addressing the JID is the LID, and the phone number comes beside it (user.go:194-206).
			if answer.JID.Server == types.HiddenUserServer {
				check.Jid, check.Lid = text(answer.PhoneNumber), answer.JID.String()
			} else {
				check.Jid = answer.JID.String()
			}
		}

		resp.Results = append(resp.Results, check)
	}

	return resp, nil
}

// GetUserInfo reads users' profiles, ordered by JID (contact.proto).
func (svc *Service) GetUserInfo(ctx context.Context, req *whatsmeowv1.GetUserInfoRequest) (*whatsmeowv1.GetUserInfoResponse, error) {
	if err := checkSession(req.GetSessionId()); err != nil {
		return nil, err
	}

	jids, err := users("jids", req.GetJids())
	if err != nil {
		return nil, err
	}

	resp := &whatsmeowv1.GetUserInfoResponse{Users: []*whatsmeowv1.UserInfo{}}
	if len(jids) == 0 {
		return resp, nil
	}

	client, err := svc.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	answers, err := client.GetUserInfo(ctx, jids)
	if err != nil {
		return nil, classify("get user info", err, false)
	}

	for jid, info := range answers {
		resp.Users = append(resp.Users, &whatsmeowv1.UserInfo{
			Jid: jid.String(), About: info.Status, PictureId: info.PictureID, VerifiedBusinessName: verifiedName(info.VerifiedName),
		})
	}

	slices.SortFunc(resp.Users, func(a, b *whatsmeowv1.UserInfo) int { return cmp.Compare(a.GetJid(), b.GetJid()) })

	return resp, nil
}

func verifiedName(name *types.VerifiedName) string {
	if name == nil {
		return ""
	}

	return name.Details.GetVerifiedName()
}

// GetProfilePicture reads a user's or a group's profile picture (contact.proto).
func (svc *Service) GetProfilePicture(ctx context.Context, req *whatsmeowv1.GetProfilePictureRequest) (*whatsmeowv1.GetProfilePictureResponse, error) {
	if err := checkSession(req.GetSessionId()); err != nil {
		return nil, err
	}

	jid, err := types.ParseJID(req.GetJid())
	if err != nil || (!isUser(jid) && jid.Server != types.GroupServer) {
		return nil, fmt.Errorf("get the profile picture: %w", server.InvalidArgument("jid", "not a user or group JID"))
	}

	client, err := svc.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	picture, err := client.GetProfilePictureInfo(ctx, jid, nil)

	switch {
	case errors.Is(err, whatsmeow.ErrProfilePictureUnauthorized):
		return &whatsmeowv1.GetProfilePictureResponse{Result: &whatsmeowv1.GetProfilePictureResponse_Hidden_{Hidden: &whatsmeowv1.GetProfilePictureResponse_Hidden{}}}, nil
	case errors.Is(err, whatsmeow.ErrProfilePictureNotSet), err == nil && picture == nil:
		// nil without an error is "not changed", which needs an ExistingID this slice never sends (user.go:632-644).
		return &whatsmeowv1.GetProfilePictureResponse{Result: &whatsmeowv1.GetProfilePictureResponse_None_{None: &whatsmeowv1.GetProfilePictureResponse_None{}}}, nil
	case err != nil:
		return nil, classify("get the profile picture", err, false)
	}

	return &whatsmeowv1.GetProfilePictureResponse{Result: &whatsmeowv1.GetProfilePictureResponse_Picture_{
		Picture: &whatsmeowv1.GetProfilePictureResponse_Picture{Url: picture.URL, Id: picture.ID},
	}}, nil
}

// ResolveJids gives each JID its other form from the store, in request order (contact.proto). The store's LID map is
// the container's, shared by every session (store/sqlstore/container.go:260).
func (svc *Service) ResolveJids(ctx context.Context, req *whatsmeowv1.ResolveJidsRequest) (*whatsmeowv1.ResolveJidsResponse, error) {
	if err := checkSession(req.GetSessionId()); err != nil {
		return nil, err
	}

	jids, err := users("jids", req.GetJids())
	if err != nil {
		return nil, err
	}

	resp := &whatsmeowv1.ResolveJidsResponse{Results: make([]*whatsmeowv1.ResolvedJid, 0, len(jids))}
	if len(jids) == 0 {
		return resp, nil
	}

	device, err := svc.sessions.Device(req.GetSessionId())
	if err != nil {
		return nil, fmt.Errorf("contacts: %w", err)
	}

	for i, jid := range jids {
		// An unknown pair is the empty JID, not an error (store/sqlstore/lidmap.go:91-116).
		alt, err := device.GetAltJID(ctx, jid)
		if err != nil {
			return nil, fmt.Errorf("resolve entry %d: %w", i, err)
		}

		resp.Results = append(resp.Results, &whatsmeowv1.ResolvedJid{Jid: req.GetJids()[i], Alt: text(alt)})
	}

	return resp, nil
}

// ListContacts lists the session's contacts as WhatsApp synced them, ordered by JID (contact.proto).
func (svc *Service) ListContacts(ctx context.Context, req *whatsmeowv1.ListContactsRequest) (*whatsmeowv1.ListContactsResponse, error) {
	if err := checkSession(req.GetSessionId()); err != nil {
		return nil, err
	}

	device, err := svc.sessions.Device(req.GetSessionId())
	if err != nil {
		return nil, fmt.Errorf("contacts: %w", err)
	}

	all, err := device.Contacts.GetAllContacts(ctx)
	if err != nil {
		return nil, fmt.Errorf("list the contacts: %w", err)
	}

	resp := &whatsmeowv1.ListContactsResponse{Contacts: make([]*whatsmeowv1.Contact, 0, len(all))}
	for jid, contact := range all {
		resp.Contacts = append(resp.Contacts, &whatsmeowv1.Contact{
			Jid: jid.String(), SavedName: contact.FullName, PushName: contact.PushName, BusinessName: contact.BusinessName,
		})
	}

	slices.SortFunc(resp.Contacts, func(a, b *whatsmeowv1.Contact) int { return cmp.Compare(a.GetJid(), b.GetJid()) })

	return resp, nil
}

// GetBlocklist lists the blocked users (contact.proto).
func (svc *Service) GetBlocklist(ctx context.Context, req *whatsmeowv1.GetBlocklistRequest) (*whatsmeowv1.GetBlocklistResponse, error) {
	if err := checkSession(req.GetSessionId()); err != nil {
		return nil, err
	}

	client, err := svc.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	// No dhash, so WhatsApp always sends the whole list (user.go:988-1022).
	list, err := client.GetBlocklist(ctx, "")
	if err != nil {
		return nil, classify("get the blocklist", err, false)
	}

	resp := &whatsmeowv1.GetBlocklistResponse{Users: []*whatsmeowv1.BlockedUser{}}
	for _, item := range list.Items {
		resp.Users = append(resp.Users, &whatsmeowv1.BlockedUser{Jid: item.LID.String(), JidAlt: text(item.PN)})
	}

	return resp, nil
}

// UpdateBlocklist blocks or unblocks a user (contact.proto).
func (svc *Service) UpdateBlocklist(ctx context.Context, req *whatsmeowv1.UpdateBlocklistRequest) (*whatsmeowv1.UpdateBlocklistResponse, error) {
	if err := checkSession(req.GetSessionId()); err != nil {
		return nil, err
	}

	jids, err := users("jid", []string{req.GetJid()})
	if err != nil {
		return nil, err
	}

	action, ok := map[whatsmeowv1.BlocklistAction]events.BlocklistChangeAction{
		whatsmeowv1.BlocklistAction_BLOCKLIST_ACTION_BLOCK:   events.BlocklistChangeActionBlock,
		whatsmeowv1.BlocklistAction_BLOCKLIST_ACTION_UNBLOCK: events.BlocklistChangeActionUnblock,
	}[req.GetAction()]
	if !ok {
		return nil, fmt.Errorf("update the blocklist: %w", server.InvalidArgument("action", "block or unblock"))
	}

	client, err := svc.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	if _, err := client.UpdateBlocklist(ctx, jids[0], action, ""); err != nil {
		return nil, classify("update the blocklist", err, true)
	}

	return &whatsmeowv1.UpdateBlocklistResponse{}, nil
}

// users parses user JIDs, by phone number or LID, of the request field field.
func users(field string, values []string) ([]types.JID, error) {
	jids := make([]types.JID, len(values))

	for i, value := range values {
		jid, err := types.ParseJID(value)
		if err != nil || !isUser(jid) {
			return nil, fmt.Errorf("contacts: %w", server.InvalidArgument(field, "entry %d is not a user JID (phone number or LID)", i))
		}

		jids[i] = jid
	}

	return jids, nil
}

func isUser(jid types.JID) bool {
	return jid.User != "" && (jid.Server == types.DefaultUserServer || jid.Server == types.HiddenUserServer)
}

// text is a JID's string form, empty for the empty JID.
func text(jid types.JID) string {
	if jid.IsEmpty() {
		return ""
	}

	return jid.String()
}

// classify gives what a contact query returned its reason (request.go:152-182, errors.go). A write that went out and
// got no answer may have taken effect: OUTCOME_UNKNOWN; a read that got none did nothing. A context error stays one.
func classify(op string, err error, write bool) error {
	var (
		iqErr        *whatsmeow.IQError
		disconnected *whatsmeow.DisconnectedError
	)

	switch {
	case errors.Is(err, context.Canceled), errors.Is(err, context.DeadlineExceeded):
		return fmt.Errorf("%s: %w", op, err)
	case errors.Is(err, whatsmeow.ErrRecipientNotOnWhatsApp):
		return fmt.Errorf("%s: %w: %w", op, server.ErrRecipientNotOnWhatsApp, err)
	case errors.As(err, &iqErr) && iqErr.Code == rateOverLimit:
		return fmt.Errorf("%s: %w: %w", op, server.ErrRateLimited, err)
	case errors.As(err, &iqErr):
		return fmt.Errorf("%s: %w: %w", op, server.WithMetadata(server.ErrWhatsAppError, "code", fmt.Sprint(iqErr.Code)), err)
	case errors.Is(err, whatsmeow.ErrNotConnected):
		// Never sent (request.go:133-141).
		return fmt.Errorf("%s: %w: %w", op, server.ErrNotConnected, err)
	case write && (errors.As(err, &disconnected) || errors.Is(err, whatsmeow.ErrIQTimedOut)):
		return fmt.Errorf("%s: %w: %w", op, server.ErrOutcomeUnknown, err)
	case errors.As(err, &disconnected):
		return fmt.Errorf("%s: %w: %w", op, server.ErrNotConnected, err)
	default:
		return fmt.Errorf("%s: %w", op, err)
	}
}
