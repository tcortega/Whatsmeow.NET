//go:build !livetest

package contacts

import "google.golang.org/grpc"

// RegisterLive serves nothing in a release build: the contacts slice's part of the live tests' control exists only
// with -tags livetest (ADR 0063), so a call to it answers UNIMPLEMENTED, as for any service the server does not have.
func RegisterLive(grpc.ServiceRegistrar, *Service) {}
