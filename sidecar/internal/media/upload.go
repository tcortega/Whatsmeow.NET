package media

import (
	"context"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"time"

	"go.mau.fi/whatsmeow"
	"google.golang.org/grpc"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	sidecarlog "github.com/tcortega/Whatsmeow.NET/sidecar/internal/log"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// UploadMedia uploads a file by path or in chunks (media.proto). whatsmeow does not retry an upload: rawUpload makes
// one POST to the first host (upload.go:243-302). So the slice retries, and each attempt is a whole UploadReader,
// which reads the plaintext again and encrypts it with a new media key (upload.go:115-117): the plaintext is a file
// that can be opened again, the user's under the root or the spooled chunks.
func (svc *Service) UploadMedia(stream grpc.ClientStreamingServer[whatsmeowv1.UploadMediaRequest, whatsmeowv1.UploadMediaResponse]) error {
	ctx := stream.Context()

	first, err := stream.Recv()
	if errors.Is(err, io.EOF) {
		return invalid("header", "the upload sent no header")
	}

	if err != nil {
		return fmt.Errorf("upload: %w", err)
	}

	header := first.GetHeader()
	if header == nil {
		return invalid("header", "the first message is not the header")
	}

	kind, err := mediaType("header.media_type", header.GetMediaType())
	if err != nil {
		return err
	}

	client, err := svc.client(header.GetSessionId())
	if err != nil {
		return err
	}

	var open func() (*os.File, error)

	if path := header.GetPath(); path != "" {
		open, err = svc.source(path)
		if err != nil {
			return err
		}

		if err := noMoreParts(stream); err != nil {
			return err
		}
	} else {
		spool, err := svc.spool(stream)
		if err != nil {
			return err
		}
		defer svc.removeTemp(spool)

		open = func() (*os.File, error) { return os.Open(spool.Name()) }
	}

	resp, err := svc.upload(ctx, client, kind, open)
	if err != nil {
		return err
	}

	err = stream.SendAndClose(&whatsmeowv1.UploadMediaResponse{Media: &whatsmeowv1.MediaRef{
		MediaType: header.GetMediaType(), DirectPath: resp.DirectPath, Url: resp.URL, MediaKey: resp.MediaKey,
		FileSha256: resp.FileSHA256, FileEncSha256: resp.FileEncSHA256, FileLength: resp.FileLength, StreamingSidecar: resp.StreamingSidecar,
	}})
	if err != nil {
		return fmt.Errorf("upload: %w", err)
	}

	return nil
}

// source checks that path is inside the media root, and returns how each attempt opens it: through the root, and
// checked as opened, so a file swapped after an earlier check is refused as what it is now.
func (svc *Service) source(path string) (func() (*os.File, error), error) {
	rel, err := svc.inRoot("header.path", path)
	if err != nil {
		return nil, err
	}

	return func() (*os.File, error) { return svc.openRegular(path, rel) }, nil
}

// openRegular opens rel under the root without blocking: a FIFO with no writer would block the open past the call's
// context (open(2) O_NONBLOCK) [observed: TestAPathSwappedForAFIFOIsRefusedAtOnce without it]. A read of a regular
// file ignores the flag. Anything but a regular file is INVALID_ARGUMENT.
func (svc *Service) openRegular(path, rel string) (*os.File, error) {
	f, err := svc.root.OpenFile(rel, os.O_RDONLY|openNonBlocking, 0)
	if err != nil {
		return nil, invalid("header.path", "%v", err)
	}

	info, err := f.Stat()
	if err == nil && !info.Mode().IsRegular() {
		err = invalid("header.path", "%q is not a regular file", path)
	}

	if err != nil {
		_ = f.Close()

		return nil, fmt.Errorf("upload: %w", err)
	}

	return f, nil
}

// noMoreParts checks that the client ended the stream after a header with a path.
func noMoreParts(stream grpc.ClientStreamingServer[whatsmeowv1.UploadMediaRequest, whatsmeowv1.UploadMediaResponse]) error {
	_, err := stream.Recv()

	switch {
	case errors.Is(err, io.EOF):
		return nil
	case err != nil:
		return fmt.Errorf("upload: %w", err)
	default:
		return invalid("chunk", "an upload by path sends no chunks")
	}
}

// spool writes the chunks to a temporary file of the sidecar's own, one at a time, until the client ends the stream.
func (svc *Service) spool(stream grpc.ClientStreamingServer[whatsmeowv1.UploadMediaRequest, whatsmeowv1.UploadMediaResponse]) (*os.File, error) {
	spool, err := os.CreateTemp(svc.temp, "chunks-*")
	if err != nil {
		return nil, fmt.Errorf("upload: spool the chunks: %w", err)
	}

	for {
		part, err := stream.Recv()
		if errors.Is(err, io.EOF) {
			return spool, nil
		}

		if err == nil {
			err = chunkOf(part)
		}

		if err == nil {
			_, err = spool.Write(part.GetChunk())
		}

		if err != nil {
			svc.removeTemp(spool)

			return nil, fmt.Errorf("upload: %w", err)
		}
	}
}

// chunkOf checks that part is a chunk of at most chunkSize bytes.
func chunkOf(part *whatsmeowv1.UploadMediaRequest) error {
	switch {
	case part.GetHeader() != nil:
		return invalid("header", "a second header")
	case len(part.GetChunk()) > chunkSize:
		return invalid("chunk", "a chunk of %d bytes; at most %d", len(part.GetChunk()), chunkSize)
	default:
		return nil
	}
}

// upload runs the attempts, waiting the backoff before each retry, within the call: only its context ends the wait.
func (svc *Service) upload(ctx context.Context, client Client, kind whatsmeow.MediaType, open func() (*os.File, error)) (whatsmeow.UploadResponse, error) {
	for attempt := 0; ; attempt++ {
		resp, err := svc.attempt(ctx, client, kind, open)
		if err == nil {
			return resp, nil
		}

		if !retryable(ctx, err) {
			return whatsmeow.UploadResponse{}, fmt.Errorf("upload: %w", sessionError(err))
		}

		if attempt == len(svc.backoff) {
			return whatsmeow.UploadResponse{}, fmt.Errorf("upload: %d attempts failed, the last with: %w", attempt+1, err)
		}

		svc.log.Warn().Err(err).Int("attempt", attempt+1).Dur("retry_in", svc.backoff[attempt]).Msg("an upload failed; retrying")

		timer := time.NewTimer(svc.backoff[attempt])

		select {
		case <-ctx.Done():
			timer.Stop()

			return whatsmeow.UploadResponse{}, fmt.Errorf("upload: %w", ctx.Err())
		case <-timer.C:
		}
	}
}

// attempt is one UploadReader: the plaintext opened again, and a new temporary file for the ciphertext. That file is
// never the user's: whatsmeow overwrites it (upload.go:114).
func (svc *Service) attempt(ctx context.Context, client Client, kind whatsmeow.MediaType, open func() (*os.File, error)) (whatsmeow.UploadResponse, error) {
	plaintext, err := open()
	if err != nil {
		return whatsmeow.UploadResponse{}, err
	}
	defer func() { _ = plaintext.Close() }()

	ciphertext, err := os.CreateTemp(svc.temp, "upload-*")
	if err != nil {
		return whatsmeow.UploadResponse{}, fmt.Errorf("create the temporary file: %w", err)
	}
	defer svc.removeTemp(ciphertext)

	resp, err := client.UploadReader(ctx, plaintext, ciphertext, kind)
	if err != nil {
		// Its *url.Error prints the URL, whose query has the media connection's auth (upload.go:251-288).
		return whatsmeow.UploadResponse{}, fmt.Errorf("upload: %w", sidecarlog.Error(err))
	}

	return resp, nil
}

// retryable is a failure another attempt may not have: not the call ending, not the session going offline, not a
// file of ours failing to read or write (a *fs.PathError), and not a path that is no regular file under the root,
// which another attempt would only repeat.
func retryable(ctx context.Context, err error) bool {
	var pathErr *fs.PathError

	switch {
	case ctx.Err() != nil, errors.Is(err, context.Canceled), errors.Is(err, context.DeadlineExceeded):
		return false
	case errors.Is(err, whatsmeow.ErrNotConnected), errors.Is(err, whatsmeow.ErrNotLoggedIn), errors.Is(err, whatsmeow.ErrClientIsNil):
		return false
	case errors.As(err, &pathErr), errors.Is(err, server.ErrInvalidArgument):
		return false
	default:
		return true
	}
}
