//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"

	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

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
