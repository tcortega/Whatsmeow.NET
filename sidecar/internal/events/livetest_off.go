//go:build !livetest

package events

import "github.com/rs/zerolog"

// withheld is the live tests' seam (livetest_on.go); in a release build it withholds nothing.
type withheld struct{}

func (*withheld) once(zerolog.Logger, string, any) bool { return false }
