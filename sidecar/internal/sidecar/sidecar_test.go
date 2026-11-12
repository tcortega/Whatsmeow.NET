package sidecar

import (
	"context"
	"runtime/debug"
	"testing"

	"go.mau.fi/whatsmeow/store"
	"google.golang.org/protobuf/proto"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

func TestGetInfoReportsTheBuild(t *testing.T) {
	t.Parallel()

	build := &debug.BuildInfo{
		GoVersion: "go1.27.3",
		Deps: []*debug.Module{
			{Path: "google.golang.org/grpc", Version: "v1.85.0"},
			{Path: "go.mau.fi/whatsmeow", Version: "v0.0.0-20261030195514-56e5ad0581ce"},
		},
		Settings: []debug.BuildSetting{{Key: "vcs.time", Value: "2026-11-01T00:00:00Z"}, {Key: "vcs.revision", Value: "0123abcd"}},
	}

	got, err := New("1.2.3", build, func() {}).GetInfo(context.Background(), &whatsmeowv1.GetInfoRequest{})
	if err != nil {
		t.Fatalf("GetInfo: %v", err)
	}

	want := &whatsmeowv1.GetInfoResponse{
		Version:          "1.2.3",
		Protocol:         2,
		WhatsmeowVersion: "v0.0.0-20261030195514-56e5ad0581ce",
		WaWebVersion:     store.GetWAVersion().String(),
		GoVersion:        "go1.27.3",
		Commit:           "0123abcd",
	}
	if !proto.Equal(got, want) {
		t.Fatalf("GetInfo = %v, want %v", got, want)
	}
}

func TestShutdownStartsTheStop(t *testing.T) {
	t.Parallel()

	calls := 0

	_, err := New("1.2.3", &debug.BuildInfo{}, func() { calls++ }).Shutdown(context.Background(), &whatsmeowv1.ShutdownRequest{})
	if err != nil || calls != 1 {
		t.Fatalf("Shutdown = %v, %d calls, want nil and 1", err, calls)
	}
}
