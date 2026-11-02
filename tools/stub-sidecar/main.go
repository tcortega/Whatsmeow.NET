// Command stub-sidecar stands in for whatsmeow-net in the .NET integration tests, for the startup failures the
// real sidecar never produces. The fixture builds one binary per mode with -ldflags "-X main.mode=<mode>":
//
//   - hang: never prints the ready line.
//   - wrong-nonce: prints a ready line whose nonce is not the one it was given.
//   - ready: prints a correct ready line whose endpoint serves nothing, then one more stdout line.
//
// Either way it first writes its process id to a file named pid next to the binary, so a test can check that
// the process is gone, and writes one zerolog-shaped warn line to stderr. It ignores stdin and waits for the
// client to kill it, and exits on its own after two minutes, so a crashed test host never leaks it.
package main

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

var mode string //nolint:gochecknoglobals // set at link time with -ldflags "-X main.mode=<mode>"

// lifetime is longer than any test waits for a stub, and short enough that a leaked one goes away.
const lifetime = 2 * time.Minute

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "stub-sidecar:", err)
		os.Exit(1)
	}

	time.Sleep(lifetime)
}

func run() error {
	exe, err := os.Executable()
	if err != nil {
		return fmt.Errorf("find the binary: %w", err)
	}

	// The arguments first, so a test that sees the pid file can read them too.
	err = os.WriteFile(filepath.Join(filepath.Dir(exe), "args"), []byte(strings.Join(os.Args[1:], "\n")), 0o600) //nolint:gosec // G703: the path is fixed, next to this binary; only the content comes from the arguments
	if err != nil {
		return fmt.Errorf("write the args file: %w", err)
	}

	err = os.WriteFile(filepath.Join(filepath.Dir(exe), "pid"), []byte(strconv.Itoa(os.Getpid())), 0o600)
	if err != nil {
		return fmt.Errorf("write the pid file: %w", err)
	}

	fmt.Fprintln(os.Stderr, `{"level":"warn","message":"stub-sidecar: mode `+mode+`, this line is the stderr tail"}`)

	switch mode {
	case "hang":
		return nil
	case "wrong-nonce":
		return ready("not-the-nonce")
	case "ready":
		if err := ready(nonce()); err != nil {
			return err
		}

		if _, err := fmt.Println("stub-sidecar: a stdout line after the ready line"); err != nil {
			return fmt.Errorf("write stdout: %w", err)
		}

		return nil
	default:
		return fmt.Errorf("unknown mode %q", mode)
	}
}

// ready prints a ready line with nonce, whose endpoint serves nothing.
func ready(nonce string) error {
	line, err := json.Marshal(map[string]any{"event": "ready", "version": "", "protocol": 1, "whatsmeow": "", "listen": "unused", "nonce": nonce})
	if err != nil {
		return fmt.Errorf("marshal the ready line: %w", err)
	}

	if _, err := fmt.Println(string(line)); err != nil {
		return fmt.Errorf("write the ready line: %w", err)
	}

	return nil
}

// nonce is the --nonce argument the client passed.
func nonce() string {
	for i, arg := range os.Args[:len(os.Args)-1] {
		if arg == "--nonce" {
			return os.Args[i+1]
		}
	}

	return ""
}
