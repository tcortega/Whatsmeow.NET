//go:build !fakewhatsapp

package main

import (
	"github.com/rs/zerolog"

	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/session"
)

// fakeBuild is false in a release build, which has no fake WhatsApp and refuses --fake-whatsapp (ADR 0019).
const fakeBuild = false

// serveFake is never called in a release build: run refuses --fake-whatsapp first.
func serveFake(*server.Server, zerolog.Logger) session.NewClient { return nil }
