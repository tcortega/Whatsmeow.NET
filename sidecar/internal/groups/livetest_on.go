//go:build livetest

package groups

import (
	"context"
	"fmt"
	"os"
	"path/filepath"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/types"
	"google.golang.org/grpc"

	livetestv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/livetest/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// RegisterLive serves the live tests' control on srv, through svc's sessions: the live tests' build only (ADR 0060).
// A release build never links this file (build/ci.sh).
func RegisterLive(srv grpc.ServiceRegistrar, svc *Service) {
	livetestv1.RegisterLiveTestServiceServer(srv, &live{svc: svc})
}

// photos is the one whatsmeow call the control makes that the contract does not.
type photos interface {
	SetGroupPhoto(ctx context.Context, jid types.JID, avatar []byte) (string, error)
}

var _ photos = (*whatsmeow.Client)(nil)

// live implements livetestv1.LiveTestServiceServer.
type live struct {
	livetestv1.UnimplementedLiveTestServiceServer

	svc *Service
}

// SetGroupPhoto sets a group's photo from a JPEG file, or removes it given no path (group.go:325-354).
func (l *live) SetGroupPhoto(ctx context.Context, req *livetestv1.SetGroupPhotoRequest) (*livetestv1.SetGroupPhotoResponse, error) {
	client, jid, err := l.svc.clientFor(req.GetSessionId(), req.GetGroup())
	if err != nil {
		return nil, err
	}

	var avatar []byte

	if path := req.GetJpegPath(); path != "" {
		if !filepath.IsAbs(path) {
			return nil, fmt.Errorf("set a group's photo: %w", server.InvalidArgument("jpeg_path", "not absolute"))
		}

		if avatar, err = os.ReadFile(path); err != nil || len(avatar) == 0 { //nolint:gosec // G304: the live test's own file
			return nil, fmt.Errorf("set a group's photo: %w", server.InvalidArgument("jpeg_path", "not a readable file"))
		}
	}

	setter, ok := client.(photos)
	if !ok {
		return nil, fmt.Errorf("session %q: its client %T cannot set a group's photo", req.GetSessionId(), client)
	}

	id, err := setter.SetGroupPhoto(ctx, jid, avatar)
	if err != nil {
		return nil, classify("set a group's photo", err, true)
	}

	if avatar == nil {
		// whatsmeow answers a removal with "remove" (group.go:345-347), not an id.
		id = ""
	}

	return &livetestv1.SetGroupPhotoResponse{PictureId: id}, nil
}
