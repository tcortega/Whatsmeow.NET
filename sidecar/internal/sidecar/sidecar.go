// Package sidecar is the Sidecar slice: the process's identity (GetInfo) and its graceful stop (Shutdown).
package sidecar

import (
	"context"
	"runtime/debug"

	"go.mau.fi/whatsmeow/store"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// Protocol is the contract revision: 1 until the first published v<semver> tag, then incremented by every
// additive change (ADR 0015).
const Protocol = 1

const whatsmeowModule = "go.mau.fi/whatsmeow"

// Service implements whatsmeowv1.SidecarServiceServer.
type Service struct {
	whatsmeowv1.UnimplementedSidecarServiceServer

	info     *whatsmeowv1.GetInfoResponse
	shutdown func()
}

// New returns the service. version is the release version, build the binary's build info, and shutdown starts
// the graceful stop.
func New(version string, build *debug.BuildInfo, shutdown func()) *Service {
	info := &whatsmeowv1.GetInfoResponse{
		Version:      version,
		Protocol:     Protocol,
		WaWebVersion: store.GetWAVersion().String(),
		GoVersion:    build.GoVersion,
	}

	for _, dep := range build.Deps {
		if dep.Path == whatsmeowModule {
			info.WhatsmeowVersion = dep.Version
		}
	}

	for _, setting := range build.Settings {
		if setting.Key == "vcs.revision" {
			info.Commit = setting.Value
		}
	}

	return &Service{info: info, shutdown: shutdown}
}

// WhatsmeowVersion is the linked whatsmeow module version, for the ready line.
func (s *Service) WhatsmeowVersion() string { return s.info.GetWhatsmeowVersion() }

// GetInfo reports the sidecar's versions.
func (s *Service) GetInfo(context.Context, *whatsmeowv1.GetInfoRequest) (*whatsmeowv1.GetInfoResponse, error) {
	return s.info, nil
}

// Shutdown starts the graceful stop and returns before the process exits.
func (s *Service) Shutdown(context.Context, *whatsmeowv1.ShutdownRequest) (*whatsmeowv1.ShutdownResponse, error) {
	s.shutdown()

	return &whatsmeowv1.ShutdownResponse{}, nil
}
