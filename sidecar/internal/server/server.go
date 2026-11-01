// Package server is the gRPC plumbing: the lenient codec, the token check, the shutdown gate, and the one
// place where errors become a gRPC status with an ErrorInfo (docs/guidelines/go.md).
package server

import (
	"context"
	"crypto/subtle"
	"errors"
	"fmt"
	"net"
	"strings"
	"sync"
	"time"

	"github.com/rs/zerolog"
	"google.golang.org/genproto/googleapis/rpc/errdetails"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/health"
	healthpb "google.golang.org/grpc/health/grpc_health_v1"
	"google.golang.org/grpc/status"
	"google.golang.org/grpc/tap"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

const (
	// maxMessageSize is set explicitly on both sides (ARCHITECTURE §8.6).
	maxMessageSize = 16 << 20
	errorDomain    = "whatsmeow.net"
	// stopGrace bounds the graceful stop, below the client's own 5 s grace (ADR 0017).
	stopGrace = 3 * time.Second
)

// Server is the sidecar's gRPC server.
type Server struct {
	grpc          *grpc.Server
	authorization []byte
	log           zerolog.Logger
	done          chan struct{}
	once          sync.Once
	// lifetime ends with Shutdown; every stream's context ends with it, so open streams end and the graceful
	// stop does not wait for them.
	lifetime  context.Context //nolint:containedctx // the server's own lifetime, not a request's
	end       context.CancelFunc
	stopGrace time.Duration
}

// New returns a server that accepts only calls whose authorization metadata is "Bearer <token>".
func New(token string, log zerolog.Logger) *Server {
	lifetime, end := context.WithCancel(context.Background())
	s := &Server{authorization: []byte("Bearer " + token), log: log, done: make(chan struct{}), lifetime: lifetime, end: end, stopGrace: stopGrace}
	s.grpc = grpc.NewServer(
		grpc.ForceServerCodecV2(lenientCodec{}),
		grpc.MaxRecvMsgSize(maxMessageSize),
		grpc.MaxSendMsgSize(maxMessageSize),
		// The token is checked from the headers, before a body is read or decoded.
		grpc.InTapHandle(s.authenticate),
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

// Serve serves lis until Stop. A Stop that comes first, before this goroutine got here, is a normal stop too.
func (s *Server) Serve(lis net.Listener) error {
	if err := s.grpc.Serve(lis); err != nil && !errors.Is(err, grpc.ErrServerStopped) {
		return fmt.Errorf("serve: %w", err)
	}

	return nil
}

// Shutdown starts the graceful stop: from now on every call except Shutdown fails with
// SIDECAR_SHUTTING_DOWN, and open streams end with it. Calling it again does nothing.
func (s *Server) Shutdown() {
	s.once.Do(func() {
		close(s.done)
		s.end()
	})
}

// ShuttingDown is closed once Shutdown was called.
func (s *Server) ShuttingDown() <-chan struct{} { return s.done }

// Stop waits for the calls in flight, at most the stop grace, then stops serving and closes the listener.
func (s *Server) Stop() {
	stopped := make(chan struct{})

	go func() {
		s.grpc.GracefulStop()
		close(stopped)
	}()

	timer := time.NewTimer(s.stopGrace)
	defer timer.Stop()

	select {
	case <-stopped:
	case <-timer.C:
		s.log.Warn().Dur("grace", s.stopGrace).Msg("calls still running after the grace; stopping them")
		// Stop closes every connection; a handler that ignores its context may still run, and ends with the process.
		s.grpc.Stop()
	}
}

func (s *Server) unary(ctx context.Context, req any, info *grpc.UnaryServerInfo, handler grpc.UnaryHandler) (any, error) {
	if err := s.admit(info.FullMethod); err != nil {
		return nil, err
	}

	resp, err := handler(ctx, req)
	if err != nil {
		return nil, s.toStatus(info.FullMethod, err)
	}

	return resp, nil
}

func (s *Server) stream(srv any, ss grpc.ServerStream, info *grpc.StreamServerInfo, handler grpc.StreamHandler) error {
	if err := s.admit(info.FullMethod); err != nil {
		return err
	}

	ctx, cancel := context.WithCancel(ss.Context())
	defer cancel()

	stop := context.AfterFunc(s.lifetime, cancel)
	defer stop()

	err := handler(srv, &serverStream{ServerStream: ss, ctx: ctx})

	select {
	case <-s.done:
		return errorInfo(codes.Unavailable, whatsmeowv1.ErrorReason_ERROR_REASON_SIDECAR_SHUTTING_DOWN, "the sidecar is shutting down")
	default:
	}

	if err != nil {
		return s.toStatus(info.FullMethod, err)
	}

	return nil
}

// serverStream is a stream whose context also ends when the server shuts down.
type serverStream struct {
	grpc.ServerStream

	ctx context.Context //nolint:containedctx // replaces the stream's context, as grpc.ServerStream requires
}

func (s *serverStream) Context() context.Context { return s.ctx }

// authenticate checks the token in constant time, for every call, before its body is decoded.
func (s *Server) authenticate(ctx context.Context, info *tap.Info) (context.Context, error) {
	values := info.Header.Get("authorization")
	if len(values) != 1 || subtle.ConstantTimeCompare([]byte(values[0]), s.authorization) != 1 {
		return ctx, errorInfo(codes.Unauthenticated, whatsmeowv1.ErrorReason_ERROR_REASON_UNAUTHENTICATED, "the call does not carry this launch's token")
	}

	return ctx, nil
}

// admit is the shutdown gate. Shutdown passes it, because calling it while shutting down succeeds
// (sidecar.proto).
func (s *Server) admit(method string) error {
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
// so a status here came from grpc-go or this package and passes through. A context error keeps its own status
// (CANCELLED, DEADLINE_EXCEEDED); every other error is INTERNAL.
func (s *Server) toStatus(method string, err error) error {
	if _, ok := status.FromError(err); ok {
		return err
	}

	// The caller cancelled or its deadline passed: nothing failed here, so it is not INTERNAL.
	if errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
		s.log.Debug().Err(err).Str("method", method).Msg("call ended by its context")

		return status.FromContextError(err).Err() //nolint:wrapcheck // a gRPC status reaches grpc-go unwrapped
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
