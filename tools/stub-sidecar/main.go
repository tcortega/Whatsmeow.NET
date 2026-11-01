// Command stub-sidecar stands in for whatsmeow-net in the .NET integration tests, for the startup failures the
// real sidecar never produces. The fixture builds one binary per mode with -ldflags "-X main.mode=<mode>":
//
//   - hang: never prints the ready line.
//   - wrong-nonce: prints a ready line whose nonce is not the one it was given.
//
// Either way it first writes its process id to a file named pid next to the binary, so a test can check that
// the process is gone, writes one line to stderr, and then never exits on its own: it ignores stdin.
package main

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"time"
)

var mode string //nolint:gochecknoglobals // set at link time with -ldflags "-X main.mode=<mode>"

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "stub-sidecar:", err)
		os.Exit(1)
	}

	// Never exit on its own: the client must kill it.
	for {
		time.Sleep(time.Hour)
	}
}

func run() error {
	exe, err := os.Executable()
	if err != nil {
		return fmt.Errorf("find the binary: %w", err)
	}

	err = os.WriteFile(filepath.Join(filepath.Dir(exe), "pid"), []byte(strconv.Itoa(os.Getpid())), 0o600)
	if err != nil {
		return fmt.Errorf("write the pid file: %w", err)
	}

	fmt.Fprintln(os.Stderr, "stub-sidecar: mode "+mode+", this line is the stderr tail")

	switch mode {
	case "hang":
		return nil
	case "wrong-nonce":
		line, err := json.Marshal(map[string]any{"event": "ready", "version": "", "protocol": 1, "whatsmeow": "", "listen": "unused", "nonce": "not-the-nonce"})
		if err != nil {
			return fmt.Errorf("marshal the ready line: %w", err)
		}

		_, err = fmt.Println(string(line))
		if err != nil {
			return fmt.Errorf("write the ready line: %w", err)
		}

		return nil
	default:
		return fmt.Errorf("unknown mode %q", mode)
	}
}
