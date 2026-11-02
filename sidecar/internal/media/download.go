package media

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"

	"go.mau.fi/whatsmeow"
	"google.golang.org/grpc"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	sidecarlog "github.com/tcortega/Whatsmeow.NET/sidecar/internal/log"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// DownloadMedia downloads to a path under the media root or in chunks (media.proto). whatsmeow writes into a file,
// checks the ciphertext's hash and MAC, decrypts it in place and checks the plaintext's hash (download-to-file.go:
// 121-161); a failed check leaves the ciphertext, or unchecked plaintext, in that file. So the file is always one of
// the sidecar's: a temporary file in the destination's directory, renamed over the destination once verified, or
// one in its own directory, streamed once verified. Nothing reaches the client before the whole file verified.
func (svc *Service) DownloadMedia(req *whatsmeowv1.DownloadMediaRequest, stream grpc.ServerStreamingServer[whatsmeowv1.DownloadMediaResponse]) error {
	ctx := stream.Context()

	kind, err := checkRef("media", req.GetMedia())
	if err != nil {
		return err
	}

	client, err := svc.client(req.GetSessionId())
	if err != nil {
		return err
	}

	if req.GetPath() != "" {
		return svc.downloadTo(ctx, client, kind, req.GetMedia(), req.GetPath())
	}

	file, err := os.CreateTemp(svc.temp, "download-*")
	if err != nil {
		return fmt.Errorf("download: create the temporary file: %w", err)
	}
	defer svc.removeTemp(file)

	if err := svc.download(ctx, client, kind, req.GetMedia(), file); err != nil {
		return err
	}

	if _, err := file.Seek(0, io.SeekStart); err != nil {
		return fmt.Errorf("download: %w", err)
	}

	for {
		// A new buffer for each chunk: grpc-go may still read a message after Send returned (SendMsg's doc).
		chunk := make([]byte, chunkSize)

		n, err := io.ReadFull(file, chunk)
		if n > 0 {
			if serr := stream.Send(&whatsmeowv1.DownloadMediaResponse{Chunk: chunk[:n]}); serr != nil {
				return fmt.Errorf("download: %w", serr)
			}
		}

		if errors.Is(err, io.EOF) || errors.Is(err, io.ErrUnexpectedEOF) {
			return nil
		}

		if err != nil {
			return fmt.Errorf("download: %w", err)
		}
	}
}

// downloadTo writes the verified file to path: into a temporary file next to it, created through the media root, then
// renamed over it, so the path holds the verified file or what it held before.
func (svc *Service) downloadTo(ctx context.Context, client Client, kind whatsmeow.MediaType, media *whatsmeowv1.MediaRef, path string) error {
	rel, err := svc.inRoot("path", path)
	if err != nil {
		return err
	}

	dir, name := filepath.Split(rel)
	if dir == "" {
		dir = "."
	}

	if info, err := svc.root.Stat(dir); err != nil || !info.IsDir() {
		return invalid("path", "the directory of %q does not exist inside the media root", path)
	}

	if info, err := svc.root.Lstat(rel); err == nil && !info.Mode().IsRegular() {
		return invalid("path", "%q is not a regular file", path)
	}

	suffix := make([]byte, 8)
	_, _ = rand.Read(suffix)
	partial := filepath.Join(dir, "."+name+"."+hex.EncodeToString(suffix)+".part")

	file, err := svc.root.OpenFile(partial, os.O_RDWR|os.O_CREATE|os.O_EXCL, 0o600)
	if err != nil {
		return invalid("path", "%v", err)
	}

	renamed := false

	defer func() {
		_ = file.Close()

		if !renamed {
			_ = svc.root.Remove(partial)
		}
	}()

	if err := svc.download(ctx, client, kind, media, file); err != nil {
		return err
	}

	if err := file.Close(); err != nil {
		return fmt.Errorf("download: %w", err)
	}

	if err := svc.root.Rename(partial, rel); err != nil {
		return fmt.Errorf("download: %w", err)
	}

	renamed = true

	return nil
}

// download fills file with the verified plaintext. A plaintext hash that differs after a valid MAC is tolerated with
// a warning, as mautrix does: the MAC covers all of the ciphertext.
func (svc *Service) download(ctx context.Context, client Client, kind whatsmeow.MediaType, media *whatsmeowv1.MediaRef, file *os.File) error {
	// A *url.Error prints the URL, whose query has the CDN's signature (download-to-file.go:101, download.go:375).
	err := sidecarlog.Error(client.DownloadMediaWithPathToFile(ctx, media.GetDirectPath(), media.GetFileEncSha256(), media.GetFileSha256(), media.GetMediaKey(), kind, "", false, file))

	switch {
	case err == nil:
		return nil
	case errors.Is(err, whatsmeow.ErrInvalidMediaSHA256):
		svc.log.Warn().Err(err).Msg("a downloaded file's plaintext hash differs after a valid MAC; delivering it, as mautrix does")

		return nil
	case errors.Is(err, whatsmeow.ErrMediaDownloadFailedWith403), errors.Is(err, whatsmeow.ErrMediaDownloadFailedWith404),
		errors.Is(err, whatsmeow.ErrMediaDownloadFailedWith410):
		return fmt.Errorf("download: %w: %w", server.ErrMediaExpired, err)
	default:
		return fmt.Errorf("download: %w", sessionError(err))
	}
}

// checkRef checks that media has what a download needs (download-to-file.go:80-98), and returns its key type.
func checkRef(field string, media *whatsmeowv1.MediaRef) (whatsmeow.MediaType, error) {
	kind, err := mediaType(field, media.GetMediaType())

	switch {
	case media == nil:
		return "", invalid(field, "no media")
	case err != nil:
		return "", err
	case !strings.HasPrefix(media.GetDirectPath(), "/"):
		return "", invalid(field, "the direct path %q does not start with /", media.GetDirectPath())
	case len(media.GetMediaKey()) != 32:
		return "", invalid(field, "a media key of %d bytes; want 32", len(media.GetMediaKey()))
	case len(media.GetFileSha256()) != 32, len(media.GetFileEncSha256()) != 32:
		return "", invalid(field, "the file hashes must be 32 bytes each")
	default:
		return kind, nil
	}
}
