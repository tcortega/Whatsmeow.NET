//go:build !livetest

package groups

import "google.golang.org/grpc"

// RegisterLive serves nothing in a release build: the live tests' control exists only with -tags livetest (ADR 0060),
// so a call to it answers UNIMPLEMENTED, as for any service the server does not have.
func RegisterLive(grpc.ServiceRegistrar, *Service) {}
