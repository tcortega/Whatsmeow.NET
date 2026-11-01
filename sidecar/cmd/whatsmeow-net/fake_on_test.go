//go:build fakewhatsapp && !windows

package main

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"strings"
	"testing"

	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/metadata"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

// deliverToNoSession starts this test binary as whatsmeow-net with args and calls DeliverMessage for a session
// that has no client, over the real socket with the token.
func deliverToNoSession(t *testing.T, args ...string) error {
	t.Helper()

	s := startSidecar(t, args...)

	if _, err := io.WriteString(s.stdin, testToken+"\n"); err != nil {
		s.fatalf(t, "write the token: %v", err)
	}

	line, err := s.stdout.ReadString('\n')
	if err != nil {
		s.fatalf(t, "read the ready line: %v", err)
	}

	var ready readyLine
	if err := json.Unmarshal([]byte(line), &ready); err != nil {
		s.fatalf(t, "ready line %q: %v", line, err)
	}

	conn, err := grpc.NewClient("unix://"+ready.Listen, grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		s.fatalf(t, "dial: %v", err)
	}

	t.Cleanup(func() { _ = conn.Close() })

	ctx := metadata.AppendToOutgoingContext(context.Background(), "authorization", "Bearer "+testToken)
	_, err = fakev1.NewFakeWhatsAppServiceClient(conn).DeliverMessage(ctx, &fakev1.DeliverMessageRequest{SessionId: "s1", Id: "M1"})

	_ = s.stdin.Close()

	if code := s.exitCode(t); code != exitStopped {
		s.fatalf(t, "exit code = %d, want %d", code, exitStopped)
	}

	if err != nil {
		return fmt.Errorf("DeliverMessage: %w", err)
	}

	return nil
}

func TestFakeWhatsAppServesTheControl(t *testing.T) {
	t.Parallel()

	// The fake answers: it refuses the session, which has no client.
	if err := deliverToNoSession(t, "--fake-whatsapp"); err == nil || !strings.Contains(err.Error(), "the session has no client") {
		t.Fatalf("DeliverMessage = %v, want the fake's no-client error", err)
	}
}

func TestATestBuildWithoutTheFlagServesNoControl(t *testing.T) {
	t.Parallel()

	if err := deliverToNoSession(t); err == nil || !strings.Contains(err.Error(), "unknown service whatsmeow.fake.v1.FakeWhatsAppService") {
		t.Fatalf("DeliverMessage = %v, want an unknown service", err)
	}
}
