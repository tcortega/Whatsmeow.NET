// Package server is the gRPC plumbing: the lenient codec, the token check, the shutdown gate, and the one
// place where errors become a gRPC status with an ErrorInfo (docs/guidelines/go.md).
package server

import (
	"context"
	"crypto/subtle"
	"fmt"
	"net"
	"strings"
	"sync"

	"github.com/rs/zerolog"
	"google.golang.org/genproto/googleapis/rpc/errdetails"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/health"
	healthpb "google.golang.org/grpc/health/grpc_health_v1"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

const (
	// maxMessageSize is set explicitly on both sides (ARCHITECTURE §8.6).
	maxMessageSize = 16 << 20
	errorDomain    = "whatsmeow.net"
)

// Server is the sidecar's gRPC server.
type Server struct {
	grpc          *grpc.Server
	authorization []byte
	log           zerolog.Logger
	done          chan struct{}
	once          sync.Once
}

// New returns a server that accepts only calls whose authorization metadata is "Bearer <token>".
func New(token string, log zerolog.Logger) *Server {
	s := &Server{authorization: []byte("Bearer " + token), log: log, done: make(chan struct{})}
	s.grpc = grpc.NewServer(
		grpc.ForceServerCodecV2(lenientCodec{}),
		grpc.MaxRecvMsgSize(maxMessageSize),
		grpc.MaxSendMsgSize(maxMessageSize),
		grpc.UnaryInterceptor(s.unary),
		grpc.StreamInterceptor(s.stream),
	)
	healthpb.RegisterHealthServer(s.grpc, health.NewServer())

	return s
}

// RegisterService registers a service implementation; it makes Server a grpc.ServiceRegistrar.
func (s *Server) RegisterService(desc *grpc.ServiceDesc, impl any) {
	s.grpc.RegisterService(desc, impl)
}

// Serve serves lis until Stop.
func (s *Server) Serve(lis net.Listener) error {
	if err := s.grpc.Serve(lis); err != nil {
		return fmt.Errorf("serve: %w", err)
	}

	return nil
}

// Shutdown starts the graceful stop: from now on every call except Shutdown fails with
// SIDECAR_SHUTTING_DOWN. Calling it again does nothing.
func (s *Server) Shutdown() { s.once.Do(func() { close(s.done) }) }

// ShuttingDown is closed once Shutdown was called.
func (s *Server) ShuttingDown() <-chan struct{} { return s.done }

// Stop waits for the calls in flight, then stops serving and closes the listener.
func (s *Server) Stop() { s.grpc.GracefulStop() }

func (s *Server) unary(ctx context.Context, req any, info *grpc.UnaryServerInfo, handler grpc.UnaryHandler) (any, error) {
	if err := s.admit(ctx, info.FullMethod); err != nil {
		return nil, err
	}

	resp, err := handler(ctx, req)
	if err != nil {
		return nil, s.toStatus(info.FullMethod, err)
	}

	return resp, nil
}

func (s *Server) stream(srv any, ss grpc.ServerStream, info *grpc.StreamServerInfo, handler grpc.StreamHandler) error {
	if err := s.admit(ss.Context(), info.FullMethod); err != nil {
		return err
	}

	if err := handler(srv, ss); err != nil {
		return s.toStatus(info.FullMethod, err)
	}

	return nil
}

// admit checks the token in constant time, then the shutdown gate. Shutdown passes the gate, because calling
// it while shutting down succeeds (sidecar.proto).
func (s *Server) admit(ctx context.Context, method string) error {
	md, _ := metadata.FromIncomingContext(ctx)

	values := md.Get("authorization")
	if len(values) != 1 || subtle.ConstantTimeCompare([]byte(values[0]), s.authorization) != 1 {
		return errorInfo(codes.Unauthenticated, whatsmeowv1.ErrorReason_ERROR_REASON_UNAUTHENTICATED, "the call does not carry this launch's token")
	}

	select {
	case <-s.done:
		if method != whatsmeowv1.SidecarService_Shutdown_FullMethodName {
			return errorInfo(codes.Unavailable, whatsmeowv1.ErrorReason_ERROR_REASON_SIDECAR_SHUTTING_DOWN, "the sidecar is shutting down")
		}
	default:
	}

	return nil
}

// toStatus maps a slice error to a status with an ErrorInfo. Slices return %w errors and never build a status,
// so a status here came from grpc-go or this package and passes through. Every other error is INTERNAL.
func (s *Server) toStatus(method string, err error) error {
	if _, ok := status.FromError(err); ok {
		return err
	}

	s.log.Error().Err(err).Str("method", method).Msg("call failed")

	return errorInfo(codes.Internal, whatsmeowv1.ErrorReason_ERROR_REASON_INTERNAL, err.Error())
}

// errorInfo is a status of code carrying one ErrorInfo of reason, sent without its ERROR_REASON_ prefix
// (error.proto).
func errorInfo(code codes.Code, reason whatsmeowv1.ErrorReason, message string) error {
	st := status.New(code, message)

	detailed, err := st.WithDetails(&errdetails.ErrorInfo{
		Reason: strings.TrimPrefix(reason.String(), "ERROR_REASON_"),
		Domain: errorDomain,
	})
	if err != nil {
		return st.Err() //nolint:wrapcheck // a gRPC status reaches grpc-go unwrapped
	}

	return detailed.Err() //nolint:wrapcheck // a gRPC status reaches grpc-go unwrapped
}
