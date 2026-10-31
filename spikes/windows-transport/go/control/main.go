// Control: Go client and Go server in one process over AF_UNIX, bidi streaming
// ~500-byte messages back to back, the grpc-go #7039 reproducer shape.
package main

import (
	"context"
	"flag"
	"fmt"
	"io"
	"net"
	"os"
	"path/filepath"
	"runtime"
	"time"

	spikev1 "spike/gen"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
)

type svc struct{ spikev1.UnimplementedEventServiceServer }

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
	dur := flag.Duration("duration", 90*time.Second, "how long to run")
	flag.Parse()
	dir, _ := os.MkdirTemp("", "ctl")
	sock := filepath.Join(dir, "s")
	lis, err := net.Listen("unix", sock)
	if err != nil {
		fmt.Println("FAIL listen", err)
		os.Exit(1)
	}
	srv := grpc.NewServer()
	spikev1.RegisterEventServiceServer(srv, svc{})
	go srv.Serve(lis)
	conn, err := grpc.NewClient("unix://"+filepath.ToSlash(sock), grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		fmt.Println("FAIL dial", err)
		os.Exit(1)
	}
	ctx, cancel := context.WithTimeout(context.Background(), *dur)
	defer cancel()
	stream, err := spikev1.NewEventServiceClient(conn).Chat(ctx)
	if err != nil {
		fmt.Println("FAIL open", err)
		os.Exit(1)
	}
	payload := make([]byte, 500)
	var sent, recv uint64
	errc := make(chan error, 2)
	go func() {
		for {
			if err := stream.Send(&spikev1.Event{Seq: sent, Payload: payload}); err != nil {
				errc <- fmt.Errorf("send: %w", err)
				return
			}
			sent++
		}
	}()
	go func() {
		for {
			e, err := stream.Recv()
			if err != nil {
				errc <- fmt.Errorf("recv: %w", err)
				return
			}
			if e.Seq != recv {
				errc <- fmt.Errorf("out of order: got %d want %d", e.Seq, recv)
				return
			}
			recv++
		}
	}()
	err = <-errc
	ok := ctx.Err() != nil // ended by our deadline, not by a transport error
	status := "PASS"
	if !ok {
		status = "FAIL"
	}
	fmt.Printf("%s go-control %s/%s %s: %d msgs echoed in order over %s (end: %v)\n", status, runtime.GOOS, runtime.Version(), "uds-bidi-500B", recv, *dur, err)
	if !ok {
		os.Exit(1)
	}
}
