package main

import (
	"bufio"
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"net"
	"os"
	"path/filepath"
	"runtime"

	spikev1 "spike/gen"
	"google.golang.org/grpc"
)

type svc struct {
	spikev1.UnimplementedEventServiceServer
}

func (svc) Ping(context.Context, *spikev1.PingRequest) (*spikev1.PingResponse, error) {
	return &spikev1.PingResponse{Nonce: "n0nce"}, nil
}

func (svc) StreamEvents(req *spikev1.StreamEventsRequest, stream grpc.ServerStreamingServer[spikev1.Event]) error {
	payload := make([]byte, req.PayloadSize)
	for i := uint64(1); i <= uint64(req.Count); i++ {
		if err := stream.Send(&spikev1.Event{Seq: req.AfterSeq + i, Payload: payload}); err != nil {
			return err
		}
	}
	return nil
}

func (svc) Upload(stream grpc.ClientStreamingServer[spikev1.Chunk, spikev1.UploadResult]) error {
	var n, b uint64
	inOrder := true
	for {
		c, err := stream.Recv()
		if err == io.EOF {
			return stream.SendAndClose(&spikev1.UploadResult{Chunks: n, Bytes: b, InOrder: inOrder})
		}
		if err != nil {
			return err
		}
		if c.Index != n {
			inOrder = false
		}
		n++
		b += uint64(len(c.Data))
	}
}

func (svc) Chat(stream grpc.BidiStreamingServer[spikev1.Event, spikev1.Event]) error {
	for {
		e, err := stream.Recv()
		if err == io.EOF {
			return nil
		}
		if err != nil {
			return err
		}
		if err := stream.Send(e); err != nil {
			return err
		}
	}
}

func main() {
	transport := flag.String("transport", "uds", "uds or pipe")
	flag.Parse()
	in := bufio.NewReader(os.Stdin)
	_, _ = in.ReadString('\n') // token line
	var lis net.Listener
	var addr string
	var err error
	var cleanup func()
	switch *transport {
	case "uds":
		dir, _ := os.MkdirTemp("", "wn")
		addr = filepath.Join(dir, "s")
		lis, err = net.Listen("unix", addr)
		cleanup = func() { os.RemoveAll(dir) }
	case "pipe":
		lis, addr, err = listenPipe()
		cleanup = func() {}
	default:
		err = fmt.Errorf("unknown transport %q", *transport)
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, "listen:", err)
		os.Exit(1)
	}
	srv := grpc.NewServer(grpc.MaxRecvMsgSize(16<<20), grpc.MaxSendMsgSize(16<<20))
	spikev1.RegisterEventServiceServer(srv, svc{})
	go srv.Serve(lis)
	b, _ := json.Marshal(map[string]string{"event": "ready", "transport": *transport, "listen": addr, "go": runtime.Version()})
	fmt.Println(string(b))
	for {
		if _, err := in.ReadByte(); err != nil {
			break
		}
	}
	srv.GracefulStop()
	cleanup()
}
