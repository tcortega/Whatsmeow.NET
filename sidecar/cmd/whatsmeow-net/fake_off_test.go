//go:build !fakewhatsapp

package main

import (
	"strings"
	"testing"
)

func TestFakeWhatsAppIsRefusedInAReleaseBuild(t *testing.T) {
	t.Parallel()

	var stdout, stderr strings.Builder

	// The token never arrives: the refusal comes before the sidecar reads stdin.
	code := run([]string{"--fake-whatsapp", "--nonce", "n"}, strings.NewReader(""), &stdout, &stderr)

	if code != exitUsage || stdout.Len() != 0 || !strings.Contains(stderr.String(), "--fake-whatsapp needs a test build") {
		t.Fatalf("exit code %d, stdout %q, stderr %q; want %d, no ready line and the refusal", code, stdout.String(), stderr.String(), exitUsage)
	}
}
