//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"errors"
	"fmt"
	"time"

	"go.mau.fi/whatsmeow/proto/waSyncAction"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

// errNotLinked is returned by a control that writes the store of a session whose device is not linked.
var errNotLinked = errors.New("the session's device is not linked")

// RegisterUser adds a user to the directory.
func (c *Control) RegisterUser(_ context.Context, req *fakev1.RegisterUserRequest) (*fakev1.RegisterUserResponse, error) {
	pn, err := types.ParseJID(req.GetPhoneJid())
	if err != nil || pn.Server != types.DefaultUserServer {
		return nil, fmt.Errorf("register user: phone_jid %q is not a phone-number JID", req.GetPhoneJid())
	}

	u := &user{pn: pn, about: req.GetAbout(), businessName: req.GetBusinessName()}

	if req.GetLid() != "" {
		if u.lid, err = types.ParseJID(req.GetLid()); err != nil || u.lid.Server != types.HiddenUserServer {
			return nil, fmt.Errorf("register user: lid %q is not a LID", req.GetLid())
		}
	}

	c.whatsApp.registerUser(u)

	return &fakev1.RegisterUserResponse{}, nil
}

// SetProfilePicture sets what WhatsApp answers for a profile picture.
func (c *Control) SetProfilePicture(_ context.Context, req *fakev1.SetProfilePictureRequest) (*fakev1.SetProfilePictureResponse, error) {
	jid, err := types.ParseJID(req.GetJid())
	if err != nil {
		return nil, fmt.Errorf("set profile picture: jid %q: %w", req.GetJid(), err)
	}

	p := picture{id: req.GetSet().GetId(), url: req.GetSet().GetUrl(), hidden: req.GetHidden() != nil}
	c.whatsApp.setPicture(jid, p)

	return &fakev1.SetProfilePictureResponse{}, nil
}

// DeliverContactNotification dispatches a picture or status notification on the client's node queue; whatsmeow acks
// it whatever the handlers return (notification.go:474), so their result changes nothing.
func (c *Control) DeliverContactNotification(_ context.Context, req *fakev1.DeliverContactNotificationRequest) (*fakev1.DeliverContactNotificationResponse, error) {
	client, err := c.whatsApp.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	var evt any

	switch kind := req.GetKind().(type) {
	case *fakev1.DeliverContactNotificationRequest_Picture_:
		jid, err := types.ParseJID(kind.Picture.GetJid())
		if err != nil {
			return nil, fmt.Errorf("deliver a picture notification: jid %q: %w", kind.Picture.GetJid(), err)
		}

		picture := &events.Picture{JID: jid, Timestamp: time.Now(), Remove: kind.Picture.GetRemoved()}
		if !picture.Remove {
			picture.PictureID = kind.Picture.GetId()
		}

		evt = picture
	case *fakev1.DeliverContactNotificationRequest_About_:
		jid, err := types.ParseJID(kind.About.GetJid())
		if err != nil {
			return nil, fmt.Errorf("deliver a status notification: jid %q: %w", kind.About.GetJid(), err)
		}

		evt = &events.UserAbout{JID: jid, Timestamp: time.Now(), Status: kind.About.GetText()}
	default:
		return nil, errors.New("deliver a contact notification: no kind")
	}

	client.dispatch(evt)

	return &fakev1.DeliverContactNotificationResponse{}, nil
}

// SyncContact stores a contact's saved name, then dispatches Contact, as an app state sync does (appstate.go:301-305);
// FetchAppState ignores the handlers' result (appstate.go:31-39).
func (c *Control) SyncContact(ctx context.Context, req *fakev1.SyncContactRequest) (*fakev1.SyncContactResponse, error) {
	client, err := c.whatsApp.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	jid, err := types.ParseJID(req.GetJid())
	if err != nil {
		return nil, fmt.Errorf("sync contact: jid %q: %w", req.GetJid(), err)
	}

	if client.Store == nil || client.Store.Contacts == nil {
		return nil, fmt.Errorf("sync contact of session %q: %w", req.GetSessionId(), errNotLinked)
	}

	if err := client.Store.Contacts.PutContactName(ctx, jid, req.GetFirstName(), req.GetFullName()); err != nil {
		return nil, fmt.Errorf("sync contact: store it: %w", err)
	}

	client.dispatch(&events.Contact{
		JID: jid, Timestamp: time.Now(),
		Action: &waSyncAction.ContactAction{FirstName: proto.String(req.GetFirstName()), FullName: proto.String(req.GetFullName())},
	})

	return &fakev1.SyncContactResponse{}, nil
}

// FailNextContactQuery scripts the session's next contact query to fail with an IQ error of the code.
func (c *Control) FailNextContactQuery(_ context.Context, req *fakev1.FailNextContactQueryRequest) (*fakev1.FailNextContactQueryResponse, error) {
	if req.GetCode() == 0 {
		return nil, errors.New("fail the next contact query: code is 0")
	}

	c.whatsApp.setScript(req.GetSessionId(), func(s *script) { s.failContactQuery = req.GetCode() })

	return &fakev1.FailNextContactQueryResponse{}, nil
}
