package main

import (
	"context"
	"fmt"
	"os"
	"os/exec"
	"slices"
	"strings"
	"testing"
)

const runParentEnv = "WHATSMEOW_NET_TEST_RUN_PARENT"

// TestMain runs the real main when a test re-executes this binary as the sidecar, and runParent when a test
// re-executes it as the sidecar's parent.
func TestMain(m *testing.M) {
	if os.Getenv(runParentEnv) == "1" {
		runParent()
	}

	if os.Getenv("WHATSMEOW_NET_TEST_RUN_MAIN") == "1" {
		main()
	}

	os.Exit(m.Run())
}

// runParent starts this binary as the sidecar with the same arguments and this process's own stdin, stdout and
// stderr, prints its process id on stdout, and waits for it: a test kills this process while it keeps the sidecar's
// stdin open itself.
func runParent() {
	env := slices.DeleteFunc(os.Environ(), func(kv string) bool { return strings.HasPrefix(kv, runParentEnv+"=") })
	cmd := exec.CommandContext(context.Background(), os.Args[0], os.Args[1:]...) //nolint:gosec // G204, G702: this test binary itself, with the test's arguments
	cmd.Env = env
	cmd.Stdin, cmd.Stdout, cmd.Stderr = os.Stdin, os.Stdout, os.Stderr

	if err := cmd.Start(); err != nil {
		fmt.Fprintln(os.Stderr, "start the sidecar:", err)
		os.Exit(1)
	}

	fmt.Println(cmd.Process.Pid)

	_ = cmd.Wait()

	os.Exit(0)
}
