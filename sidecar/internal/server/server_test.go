package server

import (
	"context"
	"errors"
	"fmt"
	"net"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"google.golang.org/genproto/googleapis/rpc/errdetails"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials/insecure"
	healthpb "google.golang.org/grpc/health/grpc_health_v1"
	"google.golang.org/grpc/mem"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"
	"google.golang.org/grpc/test/bufconn"
	"google.golang.org/protobuf/proto"

	"go.mau.fi/whatsmeow/proto/waE2E"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

const token = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"

// messages records what reached the handler and answers with err.
type messages struct {
	whatsmeowv1.UnimplementedMessageServiceServer

	received chan *whatsmeowv1.SendMessageRequest
	err      error
}

func (m *messages) SendMessage(_ context.Context, req *whatsmeowv1.SendMessageRequest) (*whatsmeowv1.SendMessageResponse, error) {
	m.received <- req

	return &whatsmeowv1.SendMessageResponse{}, m.err
}

// clientCodec marshals partial messages, as the .NET client does: Google.Protobuf is lenient. It is not the
// server's codec, so a test of the server's codec cannot pass through the client's.
type clientCodec struct{}

func (clientCodec) Marshal(v any) (mem.BufferSlice, error) {
	b, err := proto.MarshalOptions{AllowPartial: true}.Marshal(v.(proto.Message))
	if err != nil {
		return nil, fmt.Errorf("marshal: %w", err)
	}

	return mem.BufferSlice{mem.SliceBuffer(b)}, nil
}

func (clientCodec) Unmarshal(data mem.BufferSlice, v any) error {
	if err := (proto.UnmarshalOptions{AllowPartial: true}).Unmarshal(data.Materialize(), v.(proto.Message)); err != nil {
		return fmt.Errorf("unmarshal: %w", err)
	}

	return nil
}

func (clientCodec) Name() string { return "proto" }

// start serves a Server with a test MessageService over an in-memory listener and returns a client connection.
func start(t *testing.T, handlerErr error) (*Server, *grpc.ClientConn, *messages) {
	t.Helper()

	srv := New(token, zerolog.Nop())
	msgs := &messages{received: make(chan *whatsmeowv1.SendMessageRequest, 1), err: handlerErr}
	whatsmeowv1.RegisterMessageServiceServer(srv, msgs)
	whatsmeowv1.RegisterSidecarServiceServer(srv, whatsmeowv1.UnimplementedSidecarServiceServer{})

	lis := bufconn.Listen(1 << 20)
	served := make(chan error, 1)

	go func() { served <- srv.Serve(lis) }()

	t.Cleanup(func() {
		srv.Stop()

		if err := <-served; err != nil {
			t.Errorf("serve: %v", err)
		}
	})

	conn, err := grpc.NewClient("passthrough:///bufnet",
		grpc.WithContextDialer(func(ctx context.Context, _ string) (net.Conn, error) { return lis.DialContext(ctx) }),
		grpc.WithTransportCredentials(insecure.NewCredentials()),
		grpc.WithDefaultCallOptions(grpc.ForceCodecV2(clientCodec{})))
	if err != nil {
		t.Fatalf("dial: %v", err)
	}

	t.Cleanup(func() { _ = conn.Close() })

	return srv, conn, msgs
}

func withToken(value string) context.Context {
	return metadata.AppendToOutgoingContext(context.Background(), "authorization", value)
}

// wantErrorInfo fails unless err is a status with code and one ErrorInfo of reason in domain whatsmeow.net.
func wantErrorInfo(t *testing.T, err error, code codes.Code, reason string) {
	t.Helper()

	st, ok := status.FromError(err)
	if !ok || st.Code() != code {
		t.Fatalf("error = %v, want code %v", err, code)
	}

	details := st.Details()
	if len(details) != 1 {
		t.Fatalf("details = %v, want one ErrorInfo", details)
	}

	info, ok := details[0].(*errdetails.ErrorInfo)
	if !ok || info.GetReason() != reason || info.GetDomain() != "whatsmeow.net" {
		t.Fatalf("detail = %v, want ErrorInfo %s in whatsmeow.net", details[0], reason)
	}
}

func TestCallsWithoutTheTokenAreRejected(t *testing.T) {
	t.Parallel()

	_, conn, _ := start(t, nil)
	health := healthpb.NewHealthClient(conn)

	for name, ctx := range map[string]context.Context{
		"no token":         context.Background(),
		"wrong token":      withToken("Bearer " + token[:63] + "0"),
		"token, no scheme": withToken(token),
	} {
		_, err := health.Check(ctx, &healthpb.HealthCheckRequest{})
		wantErrorInfo(t, err, codes.Unauthenticated, "UNAUTHENTICATED")

		stream, err := health.Watch(ctx, &healthpb.HealthCheckRequest{})
		if err != nil {
			t.Fatalf("%s: watch: %v", name, err)
		}

		_, err = stream.Recv()
		wantErrorInfo(t, err, codes.Unauthenticated, "UNAUTHENTICATED")
	}

	got, err := health.Check(withToken("Bearer "+token), &healthpb.HealthCheckRequest{})
	if err != nil || got.GetStatus() != healthpb.HealthCheckResponse_SERVING {
		t.Fatalf("check with the token = %v, %v, want SERVING", got, err)
	}
}

func TestAMessageMissingAProto2RequiredFieldReachesTheHandler(t *testing.T) {
	t.Parallel()

	_, conn, msgs := start(t, nil)
	partial := &waE2E.Message{ExtendedTextMessage: &waE2E.ExtendedTextMessage{
		Text:         proto.String("hi"),
		EndCardTiles: []*waE2E.VideoEndCard{{Username: proto.String("only one of four required fields")}},
	}}

	_, err := whatsmeowv1.NewMessageServiceClient(conn).SendMessage(withToken("Bearer "+token), &whatsmeowv1.SendMessageRequest{Message: partial})
	if err != nil {
		t.Fatalf("send: %v", err)
	}

	got := <-msgs.received
	if !proto.Equal(got.GetMessage(), partial) {
		t.Fatalf("handler got %v, want %v", got.GetMessage(), partial)
	}
}

func TestAnUnmappedErrorIsInternalWithErrorInfo(t *testing.T) {
	t.Parallel()

	_, conn, _ := start(t, errors.New("disk on fire"))

	_, err := whatsmeowv1.NewMessageServiceClient(conn).SendMessage(withToken("Bearer "+token), &whatsmeowv1.SendMessageRequest{})
	wantErrorInfo(t, err, codes.Internal, "INTERNAL")
}

func TestAfterShutdownCallsAreRejectedExceptShutdown(t *testing.T) {
	t.Parallel()

	srv, conn, _ := start(t, nil)

	select {
	case <-srv.ShuttingDown():
		t.Fatal("shutting down before Shutdown")
	default:
	}

	srv.Shutdown()
	srv.Shutdown()
	<-srv.ShuttingDown()

	_, err := healthpb.NewHealthClient(conn).Check(withToken("Bearer "+token), &healthpb.HealthCheckRequest{})
	wantErrorInfo(t, err, codes.Unavailable, "SIDECAR_SHUTTING_DOWN")

	// Shutdown itself passes the gate and reaches its handler; the unimplemented one answers Unimplemented.
	_, err = whatsmeowv1.NewSidecarServiceClient(conn).Shutdown(withToken("Bearer "+token), &whatsmeowv1.ShutdownRequest{})
	if status.Code(err) != codes.Unimplemented {
		t.Fatalf("shutdown while shutting down = %v, want it to reach its handler", err)
	}
}

func TestShutdownEndsAnOpenStreamWithSidecarShuttingDown(t *testing.T) {
	t.Parallel()

	srv, conn, _ := start(t, nil)

	stream, err := healthpb.NewHealthClient(conn).Watch(withToken("Bearer "+token), &healthpb.HealthCheckRequest{})
	if err != nil {
		t.Fatalf("watch: %v", err)
	}

	if _, err := stream.Recv(); err != nil {
		t.Fatalf("first status: %v", err)
	}

	srv.Shutdown()
	srv.Stop()

	_, err = stream.Recv()
	wantErrorInfo(t, err, codes.Unavailable, "SIDECAR_SHUTTING_DOWN")
}

// stuck is a stream handler that ignores its context, the worst case for a graceful stop.
type stuck struct {
	whatsmeowv1.UnimplementedEventServiceServer

	opened  chan struct{}
	release chan struct{}
}

func (s *stuck) StreamEvents(*whatsmeowv1.StreamEventsRequest, grpc.ServerStreamingServer[whatsmeowv1.StreamEventsResponse]) error {
	close(s.opened)
	<-s.release

	return nil
}

func TestStopEndsAStreamThatIgnoresShutdownAfterTheGrace(t *testing.T) {
	t.Parallel()

	srv := New(token, zerolog.Nop())
	srv.stopGrace = 50 * time.Millisecond
	handler := &stuck{opened: make(chan struct{}), release: make(chan struct{})}
	whatsmeowv1.RegisterEventServiceServer(srv, handler)

	lis := bufconn.Listen(1 << 20)
	served := make(chan error, 1)

	go func() { served <- srv.Serve(lis) }()

	t.Cleanup(func() { close(handler.release) })

	conn, err := grpc.NewClient("passthrough:///bufnet",
		grpc.WithContextDialer(func(ctx context.Context, _ string) (net.Conn, error) { return lis.DialContext(ctx) }),
		grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		t.Fatalf("dial: %v", err)
	}

	t.Cleanup(func() { _ = conn.Close() })

	stream, err := whatsmeowv1.NewEventServiceClient(conn).StreamEvents(withToken("Bearer "+token), &whatsmeowv1.StreamEventsRequest{})
	if err != nil {
		t.Fatalf("stream: %v", err)
	}

	go func() { _, _ = stream.Recv() }()

	<-handler.opened
	srv.Shutdown()
	srv.Stop()

	if err := <-served; err != nil {
		t.Fatalf("serve: %v", err)
	}
}

// rawCodec sends the bytes it is given as they are, so a test can send a body that is not a valid message.
type rawCodec struct{}

func (rawCodec) Marshal(v any) (mem.BufferSlice, error) {
	return mem.BufferSlice{mem.SliceBuffer(*v.(*[]byte))}, nil
}

func (rawCodec) Unmarshal(data mem.BufferSlice, v any) error {
	*v.(*[]byte) = data.Materialize()

	return nil
}

func (rawCodec) Name() string { return "proto" }

func TestACallWithoutTheTokenIsRejectedBeforeItsBodyIsDecoded(t *testing.T) {
	t.Parallel()

	_, conn, msgs := start(t, nil)
	garbage := []byte{0xff, 0xff, 0xff, 0xff}
	var reply []byte

	err := conn.Invoke(context.Background(), whatsmeowv1.MessageService_SendMessage_FullMethodName, &garbage, &reply, grpc.ForceCodecV2(rawCodec{}))
	wantErrorInfo(t, err, codes.Unauthenticated, "UNAUTHENTICATED")

	select {
	case <-msgs.received:
		t.Fatal("the handler ran")
	default:
	}
}
