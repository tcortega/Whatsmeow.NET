package transport

import (
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"net"

	"github.com/Microsoft/go-winio"
)

// listenPipe listens on \\.\pipe\whatsmeow-net- plus a 128-bit random suffix, with an owner-only DACL; the
// client connects with PipeOptions.CurrentUserOnly (ADR 0014).
func listenPipe() (net.Listener, string, error) {
	suffix := make([]byte, 16)
	_, _ = rand.Read(suffix) // crypto/rand.Read never returns an error

	name := `\\.\pipe\whatsmeow-net-` + hex.EncodeToString(suffix)

	lis, err := winio.ListenPipe(name, &winio.PipeConfig{SecurityDescriptor: "D:P(A;;GA;;;OW)", InputBufferSize: 65536, OutputBufferSize: 65536})
	if err != nil {
		return nil, "", fmt.Errorf("listen on %s: %w", name, err)
	}

	return lis, name, nil
}
