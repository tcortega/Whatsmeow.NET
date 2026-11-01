//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"errors"
	"fmt"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waAdv"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

// errNoQRChannel is returned by a QR control for a session whose client has no QR channel open.
var errNoQRChannel = errors.New("no QR channel is open: the client is not pairing")

// Control is the control service: it scripts the fake and reads what the clients sent.
type Control struct {
	fakev1.UnimplementedFakeWhatsAppServiceServer

	whatsApp *WhatsApp
}

// NewControl returns the control service of w.
func NewControl(w *WhatsApp) *Control { return &Control{whatsApp: w} }

// DeliverMessage delivers a decrypted message to the session's client.
func (c *Control) DeliverMessage(_ context.Context, req *fakev1.DeliverMessageRequest) (*fakev1.DeliverMessageResponse, error) {
	evt := &events.Message{Info: types.MessageInfo{ID: req.GetId()}}
	if err := c.whatsApp.deliverMessage(req.GetSessionId(), evt); err != nil {
		return nil, err
	}

	return &fakev1.DeliverMessageResponse{}, nil
}

// WaitForSent returns the session's first entry after the request's index, waiting for it.
func (c *Control) WaitForSent(ctx context.Context, req *fakev1.WaitForSentRequest) (*fakev1.WaitForSentResponse, error) {
	sent, err := c.whatsApp.waitForSent(ctx, req.GetSessionId(), req.GetAfter())
	if err != nil {
		return nil, err
	}

	return &fakev1.WaitForSentResponse{Sent: sent}, nil
}

// LinkDevice does what whatsmeow does when the phone links the device (handlePair, pair.go:222-240): it stores the
// device under jid, then dispatches PairSuccess (pair.go:157). While pairing, the QR channel then sends success and
// closes (qrchan.go:189-190), and the client logs in again after WhatsApp's 515 (connectionevents.go:26-38).
func (c *Control) LinkDevice(ctx context.Context, req *fakev1.LinkDeviceRequest) (*fakev1.LinkDeviceResponse, error) {
	client, err := c.whatsApp.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	jid, err := types.ParseJID(req.GetJid())
	if err != nil {
		return nil, fmt.Errorf("link session %q: jid %q: %w", req.GetSessionId(), req.GetJid(), err)
	}

	client.Store.ID = &jid
	// The ADV identity WhatsApp signs; the schema checks only the lengths (00-latest-schema.sql).
	client.Store.Account = &waAdv.ADVSignedDeviceIdentity{
		Details: []byte{}, AccountSignature: make([]byte, 64), AccountSignatureKey: make([]byte, 32), DeviceSignature: make([]byte, 64),
	}

	if err := client.Store.Save(ctx); err != nil {
		return nil, fmt.Errorf("link session %q: save the device: %w", req.GetSessionId(), err)
	}

	client.dispatch(&events.PairSuccess{ID: jid})

	client.conn.Lock()
	pairing := client.qr != nil
	client.endQR(whatsmeow.QRChannelSuccess)
	client.conn.Unlock()

	if pairing {
		client.logInAfterLink()
	}

	return &fakev1.LinkDeviceResponse{}, nil
}

// pairingClient returns the session's client with its QR channel open, under its conn lock; the caller unlocks.
func (c *Control) pairingClient(sessionID string) (*Client, error) {
	client, err := c.whatsApp.client(sessionID)
	if err != nil {
		return nil, err
	}

	client.conn.Lock()
	if client.qr == nil {
		client.conn.Unlock()

		return nil, fmt.Errorf("session %q: %w", sessionID, errNoQRChannel)
	}

	return client, nil
}

// RotateQrCode makes the session's QR channel send the next code.
func (c *Control) RotateQrCode(_ context.Context, req *fakev1.RotateQrCodeRequest) (*fakev1.RotateQrCodeResponse, error) {
	client, err := c.pairingClient(req.GetSessionId())
	if err != nil {
		return nil, err
	}
	defer client.conn.Unlock()

	client.sendCode()

	return &fakev1.RotateQrCodeResponse{}, nil
}

// ExpireQrCodes makes the session's QR codes run out: timeout, then a disconnect without an event (qrchan.go:77-84).
func (c *Control) ExpireQrCodes(_ context.Context, req *fakev1.ExpireQrCodesRequest) (*fakev1.ExpireQrCodesResponse, error) {
	client, err := c.pairingClient(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	client.endQR(whatsmeow.QRChannelTimeout)
	client.conn.Unlock()
	client.Disconnect()

	return &fakev1.ExpireQrCodesResponse{}, nil
}

// DropConnection drops the connection of a session that is pairing: Disconnected off the node queue, whose handlers
// run in order, the QR channel's last (client.go:612-622, qrchan.go:196-197).
func (c *Control) DropConnection(_ context.Context, req *fakev1.DropConnectionRequest) (*fakev1.DropConnectionResponse, error) {
	client, err := c.pairingClient(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	client.connected = false
	client.conn.Unlock()

	c.whatsApp.background.Go(func() {
		client.runHandlers(&events.Disconnected{})

		client.conn.Lock()
		client.endQR(whatsmeow.QRChannelTimeout)
		client.conn.Unlock()
	})

	return &fakev1.DropConnectionResponse{}, nil
}

// FailNextPreKeyUpload makes the session's next login after a link upload no pre-keys.
func (c *Control) FailNextPreKeyUpload(_ context.Context, req *fakev1.FailNextPreKeyUploadRequest) (*fakev1.FailNextPreKeyUploadResponse, error) {
	client, err := c.whatsApp.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	c.whatsApp.setScript(client.sessionID, func(s *script) { s.failPreKeyUpload = true })

	return &fakev1.FailNextPreKeyUploadResponse{}, nil
}

// FailNextPairPhone makes the session's next PairPhone fail with an IQ error of the request's code.
func (c *Control) FailNextPairPhone(_ context.Context, req *fakev1.FailNextPairPhoneRequest) (*fakev1.FailNextPairPhoneResponse, error) {
	client, err := c.whatsApp.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	c.whatsApp.setScript(client.sessionID, func(s *script) { s.failPairPhone = req.GetCode() })

	return &fakev1.FailNextPairPhoneResponse{}, nil
}

// FailNextConnect makes the session's next connect fail with an error whatsmeow does not retry.
func (c *Control) FailNextConnect(_ context.Context, req *fakev1.FailNextConnectRequest) (*fakev1.FailNextConnectResponse, error) {
	client, err := c.whatsApp.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	c.whatsApp.setScript(client.sessionID, func(s *script) { s.failConnect = true })

	return &fakev1.FailNextConnectResponse{}, nil
}

// FailNextLogout makes the session's next logout time out, with the client still connected.
func (c *Control) FailNextLogout(_ context.Context, req *fakev1.FailNextLogoutRequest) (*fakev1.FailNextLogoutResponse, error) {
	client, err := c.whatsApp.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	client.conn.Lock()
	client.failLogout = true
	client.conn.Unlock()

	return &fakev1.FailNextLogoutResponse{}, nil
}

// RaisePermanentDisconnect ends the session's connection with a PermanentDisconnect event, dispatched off the queue.
func (c *Control) RaisePermanentDisconnect(ctx context.Context, req *fakev1.RaisePermanentDisconnectRequest) (*fakev1.RaisePermanentDisconnectResponse, error) {
	client, err := c.whatsApp.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	var evt events.PermanentDisconnect

	switch kind := req.GetKind().(type) {
	case *fakev1.RaisePermanentDisconnectRequest_LoggedOut_:
		evt = &events.LoggedOut{OnConnect: true, Reason: events.ConnectFailureReason(kind.LoggedOut.GetReason())}
	case *fakev1.RaisePermanentDisconnectRequest_StreamReplaced_:
		evt = &events.StreamReplaced{}
	case *fakev1.RaisePermanentDisconnectRequest_ClientOutdated_:
		evt = &events.ClientOutdated{}
	case *fakev1.RaisePermanentDisconnectRequest_CatRefreshError_:
		evt = &events.CATRefreshError{Error: errors.New(kind.CatRefreshError.GetMessage())} //nolint:err113 // the scripted error's text
	case *fakev1.RaisePermanentDisconnectRequest_TemporaryBan_:
		evt = &events.TemporaryBan{Code: events.TempBanReason(kind.TemporaryBan.GetCode()), Expire: kind.TemporaryBan.GetExpire().AsDuration()}
	case *fakev1.RaisePermanentDisconnectRequest_ConnectFailure_:
		evt = &events.ConnectFailure{Reason: events.ConnectFailureReason(kind.ConnectFailure.GetReason()), Message: kind.ConnectFailure.GetMessage()}
	default:
		return nil, fmt.Errorf("raise a permanent disconnect for session %q: no kind", req.GetSessionId())
	}

	client.drop()
	c.whatsApp.offQueue(client, evt)

	// whatsmeow deletes a logged-out device while the event is being dispatched (connectionevents.go:43-44, 132-133).
	if _, ok := evt.(*events.LoggedOut); ok {
		if err := client.Store.Delete(ctx); err != nil {
			return nil, fmt.Errorf("raise LoggedOut for session %q: delete the device: %w", req.GetSessionId(), err)
		}
	}

	return &fakev1.RaisePermanentDisconnectResponse{}, nil
}
