//go:build fakewhatsapp

package main

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/rs/zerolog"
)

func TestATestBuildDumpsItsGoroutinesOnRequest(t *testing.T) {
	t.Parallel()

	dir := t.TempDir()
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()

	done := make(chan struct{})

	go func() {
		defer close(done)
		dumpOnRequest(ctx, dir, 10*time.Millisecond, zerolog.Nop())
	}()

	request := filepath.Join(dir, fmt.Sprintf("dump-%d", os.Getpid()))
	if err := os.WriteFile(request, nil, 0o600); err != nil {
		t.Fatalf("ask for a dump: %v", err)
	}

	answer := filepath.Join(dir, fmt.Sprintf("goroutines-%d-1.txt", os.Getpid()))

	for {
		if _, err := os.Stat(request); os.IsNotExist(err) {
			break
		}

		if ctx.Err() != nil {
			t.Fatal("the request was never answered")
		}

		time.Sleep(10 * time.Millisecond)
	}

	stacks, err := os.ReadFile(answer) //nolint:gosec // G304: the test's own directory
	if err != nil {
		t.Fatalf("the dump: %v", err)
	}

	if !strings.Contains(string(stacks), "dumpOnRequest") {
		t.Fatalf("the dump does not show the dumping goroutine:\n%s", stacks)
	}

	cancel()
	<-done
}
