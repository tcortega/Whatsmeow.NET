package main

import (
	"os"
	"testing"
)

// TestMain runs the real main when a test re-executes this binary as the sidecar.
func TestMain(m *testing.M) {
	if os.Getenv("WHATSMEOW_NET_TEST_RUN_MAIN") == "1" {
		main()
	}

	os.Exit(m.Run())
}
