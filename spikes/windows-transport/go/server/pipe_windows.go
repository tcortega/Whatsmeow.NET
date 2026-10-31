package main

import (
	"crypto/rand"
	"encoding/hex"
	"net"

	"github.com/Microsoft/go-winio"
)

func listenPipe() (net.Listener, string, error) {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	name := `\\.\pipe\whatsmeow-net-` + hex.EncodeToString(b)
	// Owner-only DACL: protected, generic-all for OWNER RIGHTS only.
	lis, err := winio.ListenPipe(name, &winio.PipeConfig{SecurityDescriptor: "D:P(A;;GA;;;OW)", InputBufferSize: 65536, OutputBufferSize: 65536})
	return lis, name, err
}
