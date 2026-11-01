package main

import (
	"context"

	"github.com/rs/zerolog"
)

// tetherToParent does nothing on Windows: there the client puts the sidecar in a Job Object that kills it when the
// client's last handle to the job closes, which its death does (ARCHITECTURE §8.5). The returned context is ctx.
func tetherToParent(ctx context.Context, _ zerolog.Logger, _ int) (context.Context, func(), error) {
	return ctx, func() {}, nil
}
