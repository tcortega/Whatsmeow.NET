// Package server is the gRPC plumbing: the lenient codec, the token check, the shutdown gate, and the one
// place where errors become a gRPC status with an ErrorInfo (docs/guidelines/go.md).
package server

import (
	"context"
	"crypto/subtle"
	"errors"
	"fmt"
	"net"
	"runtime/debug"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/rs/zerolog"
	"google.golang.org/genproto/googleapis/rpc/errdetails"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/health"
	healthpb "google.golang.org/grpc/health/grpc_health_v1"
	"google.golang.org/grpc/keepalive"
	"google.golang.org/grpc/status"
	"google.golang.org/grpc/tap"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	sidecarlog "github.com/tcortega/Whatsmeow.NET/sidecar/internal/log"
)

const (
	// maxMessageSize is set explicitly on both sides (ARCHITECTURE §8.6).
	maxMessageSize = 16 << 20
	errorDomain    = "whatsmeow.net"
	// stopGrace bounds the graceful stop, below the client's own 5 s grace (ADR 0017).
	stopGrace = 3 * time.Second
	// keepaliveMinTime is the shortest ping interval the server accepts, far below the External-mode client's (ADR 0046).
	keepaliveMinTime = time.Second
)

// Server is the sidecar's gRPC server.
type Server struct {
	grpc *grpc.Server
	// authorization is "Bearer <token>"; a standalone sidecar swaps it when its token file changes (ReloadToken).
	authorization atomic.Pointer[[]byte]
	log           zerolog.Logger
	// redactor takes the numbers out of a status message and its error line (toStatus).
	redactor *sidecarlog.Redactor
	done     chan struct{}
	once     sync.Once
	// lifetime ends with Shutdown; every stream's context ends with it, so open streams end and the graceful
	// stop does not wait for them.
	lifetime  context.Context //nolint:containedctx // the server's own lifetime, not a request's
	end       context.CancelFunc
	stopGrace time.Duration
	// reload reads the token file again; nil for a token from stdin, which never changes. reloadMu serializes the
	// reads, and reloaded is when the last one was.
	reload   func() (string, error)
	reloadMu sync.Mutex
	reloaded time.Time
	now      func() time.Time
}

// tokenReload is how often a refused token may make the server read its token file again: a flood of wrong tokens
// reads it once a second at most.
const tokenReload = time.Second

// ReloadToken makes a refused call read the token from read again, at most once per tokenReload, and accept the
// token read from then on, so a standalone sidecar follows a rotated secret without a restart (ADR 0046). Call it
// before Serve.
func (s *Server) ReloadToken(read func() (string, error)) {
	s.reload = read
}

// New returns a server that accepts only calls whose authorization metadata is "Bearer <token>". The redactor is
// the process's, the one its log writer has, so a status message and a log line give a user the same token.
func New(token string, log zerolog.Logger, redactor *sidecarlog.Redactor) *Server {
	lifetime, end := context.WithCancel(context.Background())
	s := &Server{log: log, redactor: redactor, done: make(chan struct{}), lifetime: lifetime, end: end, stopGrace: stopGrace, now: time.Now}
	s.setToken(token)
	s.grpc = grpc.NewServer(
		grpc.ForceServerCodecV2(lenientCodec{}),
		grpc.MaxRecvMsgSize(maxMessageSize),
		grpc.MaxSendMsgSize(maxMessageSize),
		// An External-mode client pings to notice a sidecar that is gone, also between calls (ADR 0046).
		grpc.KeepaliveEnforcementPolicy(keepalive.EnforcementPolicy{MinTime: keepaliveMinTime, PermitWithoutStream: true}),
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

func (s *Server) unary(ctx context.Context, req any, info *grpc.UnaryServerInfo, handler grpc.UnaryHandler) (resp any, err error) {
	if err := s.admit(info.FullMethod); err != nil {
		return nil, err
	}

	defer s.recovered(info.FullMethod, &err)

	resp, err = handler(ctx, req)
	if err != nil {
		return nil, s.toStatus(info.FullMethod, err)
	}

	return resp, nil
}

func (s *Server) stream(srv any, ss grpc.ServerStream, info *grpc.StreamServerInfo, handler grpc.StreamHandler) (err error) {
	if err := s.admit(info.FullMethod); err != nil {
		return err
	}

	defer s.recovered(info.FullMethod, &err)

	ctx, cancel := context.WithCancel(ss.Context())
	defer cancel()

	stop := context.AfterFunc(s.lifetime, cancel)
	defer stop()

	err = handler(srv, &serverStream{ServerStream: ss, ctx: ctx})

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

// recovered, deferred in an interceptor, fails a call whose handler panicked with INTERNAL, so the panic ends that
// call and not the process with every session in it: grpc-go calls the interceptors and handlers without a recover
// (grpc server.go:1284 and 1457, at v1.85.0). The log line carries the panic's type and the stack, never its value, which may
// carry a JID (ADR 0039), and the status message says only that the call panicked.
func (s *Server) recovered(method string, err *error) {
	r := recover()
	if r == nil {
		return
	}

	s.log.Error().Str("method", method).Str("panic", fmt.Sprintf("%T", r)).Bytes("stack", debug.Stack()).Msg("the call panicked")

	*err = errorInfo(codes.Internal, whatsmeowv1.ErrorReason_ERROR_REASON_INTERNAL, "the call panicked")
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
	if len(values) == 1 && (s.carries(values[0]) || (s.reloadToken() && s.carries(values[0]))) {
		return ctx, nil
	}

	return ctx, errorInfo(codes.Unauthenticated, whatsmeowv1.ErrorReason_ERROR_REASON_UNAUTHENTICATED, "the call does not carry this launch's token")
}

func (s *Server) carries(value string) bool {
	return subtle.ConstantTimeCompare([]byte(value), *s.authorization.Load()) == 1
}

func (s *Server) setToken(token string) {
	authorization := []byte("Bearer " + token)
	s.authorization.Store(&authorization)
}

// reloadToken reads the token file again unless that happened less than tokenReload ago, and reports whether it took
// a token from it. A file that cannot be read, or holds no token, keeps the current one: the next refused call after
// the interval tries again.
func (s *Server) reloadToken() bool {
	if s.reload == nil {
		return false
	}

	s.reloadMu.Lock()
	defer s.reloadMu.Unlock()

	now := s.now()
	if !s.reloaded.IsZero() && now.Sub(s.reloaded) < tokenReload {
		return false
	}

	s.reloaded = now

	token, err := s.reload()
	if err != nil {
		s.log.Warn().Err(err).Msg("reading the token file again failed; the current token stays")

		return false
	}

	s.setToken(token)

	return true
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
// so a status here came from grpc-go or this package and passes through with its code and details. A context error
// keeps its own status (CANCELLED, DEADLINE_EXCEEDED); an error that wraps one of the reason errors (reasons.go) gets
// its reason and metadata; every other error is INTERNAL.
//
// Every message, a passed-through one's too, reaches the application as an exception's message, and an INTERNAL one
// the error log too, so it has no phone number, JID user part or URL query (sidecarlog.Redactor.Text): a slice may
// wrap whatsmeow's text, which names JIDs and prints URLs with their credential, or echo a JID it refused (ADR 0039).
// The debug line keeps err as it is.
func (s *Server) toStatus(method string, err error) error {
	if st, ok := status.FromError(err); ok {
		passed := st.Proto()
		passed.Message = s.redactor.Text(passed.GetMessage())

		return status.ErrorProto(passed) //nolint:wrapcheck // a gRPC status reaches grpc-go unwrapped
	}

	// The caller cancelled or its deadline passed: nothing failed here, so it is not INTERNAL.
	if errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
		s.log.Debug().Err(err).Str("method", method).Msg("call ended by its context")

		return status.FromContextError(err).Err() //nolint:wrapcheck // a gRPC status reaches grpc-go unwrapped
	}

	message := s.redactor.Text(err.Error())

	if code, reason, metadata, ok := reasonOf(err); ok {
		s.log.Debug().Err(err).Str("method", method).Msg("call failed with a reason")

		return errorInfoWith(code, reason, message, metadata)
	}

	s.log.Error().Str(zerolog.ErrorFieldName, message).Str("method", method).Msg("call failed")

	return errorInfo(codes.Internal, whatsmeowv1.ErrorReason_ERROR_REASON_INTERNAL, message)
}

// errorInfo is a status of code carrying one ErrorInfo of reason, sent without its ERROR_REASON_ prefix
// (error.proto).
func errorInfo(code codes.Code, reason whatsmeowv1.ErrorReason, message string) error {
	return errorInfoWith(code, reason, message, nil)
}

// errorInfoWith is errorInfo with ErrorInfo metadata.
func errorInfoWith(code codes.Code, reason whatsmeowv1.ErrorReason, message string, metadata map[string]string) error {
	st := status.New(code, message)

	detailed, err := st.WithDetails(&errdetails.ErrorInfo{
		Reason:   strings.TrimPrefix(reason.String(), "ERROR_REASON_"),
		Domain:   errorDomain,
		Metadata: metadata,
	})
	if err != nil {
		return st.Err() //nolint:wrapcheck // a gRPC status reaches grpc-go unwrapped
	}

	return detailed.Err() //nolint:wrapcheck // a gRPC status reaches grpc-go unwrapped
}
