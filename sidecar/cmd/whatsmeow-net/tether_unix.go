//go:build linux || darwin

package main

import "errors"

// errParentExited is the cause the tether backup cancels the sidecar's context with: the process that started the
// sidecar is no longer its parent (ARCHITECTURE §7.8).
var errParentExited = errors.New("the parent exited")
