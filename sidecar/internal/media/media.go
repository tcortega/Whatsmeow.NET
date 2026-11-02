// Package media is the Media slice: uploads and downloads by path under the media root or in 64 KiB chunks, and the
// media retry that asks the phone to upload expired media again (media.proto, ARCHITECTURE §7.6).
//
// No file travels as one message (ARCHITECTURE invariant 8), and none is held in memory: a path is read or written
// in place, chunks are spooled to a file of the sidecar's own, and whatsmeow encrypts and decrypts through files
// (upload.go:115-154, download-to-file.go:121-161).
package media

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/types"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/session"
)

// chunkSize is the most bytes one chunk carries, both ways (media.proto).
const chunkSize = 64 << 10

// Client is what the slice needs of a whatsmeow client. *whatsmeow.Client and the fake WhatsApp's client both
// satisfy it (ADR 0019).
type Client interface {
	UploadReader(ctx context.Context, plaintext io.Reader, tempFile io.ReadWriteSeeker, appInfo whatsmeow.MediaType) (whatsmeow.UploadResponse, error)
	DownloadMediaWithPathToFile(ctx context.Context, directPath string, encFileHash, fileHash, mediaKey []byte, mediaType whatsmeow.MediaType,
		mmsType string, allowNoHash bool, file whatsmeow.File) error
	SendMediaRetryReceipt(ctx context.Context, message *types.MessageInfo, mediaKey []byte) error
}

var _ Client = (*whatsmeow.Client)(nil)

// Sessions is what the slice needs of the Sessions slice: the client of a session that is online.
type Sessions interface {
	Online(id string) (session.Client, <-chan struct{}, error)
}

// Service implements whatsmeowv1.MediaServiceServer.
type Service struct {
	whatsmeowv1.UnimplementedMediaServiceServer

	sessions Sessions
	db       *sql.DB
	// root confines every path the sidecar reads or writes for a client; nil when no media root is configured.
	root     *os.Root
	rootPath string
	// temp is the sidecar's own directory for spooled chunks, ciphertext and downloads to stream.
	temp string
	log  zerolog.Logger
	// backoff is the wait before each upload retry; its length plus one is the number of attempts.
	backoff []time.Duration
}

// New returns the slice. mediaRoot is the absolute directory paths are confined to, "" for none. temp is emptied and
// made owner-only: one sidecar owns the database, and so the directory next to it; what a crash left there goes.
func New(sessions Sessions, db *sql.DB, mediaRoot, temp string, log zerolog.Logger) (*Service, error) {
	svc := &Service{sessions: sessions, db: db, temp: temp, log: log, backoff: []time.Duration{time.Second, 2 * time.Second}}

	if err := os.RemoveAll(temp); err != nil {
		return nil, fmt.Errorf("empty the temporary directory %s: %w", temp, err)
	}

	if err := os.MkdirAll(temp, 0o700); err != nil {
		return nil, fmt.Errorf("create the temporary directory %s: %w", temp, err)
	}

	if mediaRoot == "" {
		return svc, nil
	}

	if !filepath.IsAbs(mediaRoot) {
		return nil, fmt.Errorf("the media root %q is not an absolute path", mediaRoot)
	}

	root, err := os.OpenRoot(mediaRoot)
	if err != nil {
		return nil, fmt.Errorf("open the media root: %w", err)
	}

	svc.root, svc.rootPath = root, filepath.Clean(mediaRoot)

	return svc, nil
}

// Close releases the media root.
func (svc *Service) Close() error {
	if svc.root == nil {
		return nil
	}

	if err := svc.root.Close(); err != nil {
		return fmt.Errorf("close the media root: %w", err)
	}

	return nil
}

// client is the media client of a session that is online.
func (svc *Service) client(id string) (Client, error) {
	c, _, err := svc.sessions.Online(id)
	if err != nil {
		return nil, fmt.Errorf("media: %w", err)
	}

	media, ok := c.(Client)
	if !ok {
		return nil, fmt.Errorf("session %q: its client (%T) cannot transfer media", id, c)
	}

	return media, nil
}

// inRoot is path relative to the media root, or INVALID_ARGUMENT for field when there is no root, or path is not
// absolute or lexically outside it. The os.Root the caller then opens it with refuses what escapes by a symlink.
func (svc *Service) inRoot(field, path string) (string, error) {
	if svc.root == nil {
		return "", invalid(field, "no media root is configured; send the file in chunks")
	}

	if !filepath.IsAbs(path) {
		return "", invalid(field, "%q is not an absolute path", path)
	}

	rel, err := filepath.Rel(svc.rootPath, path)
	if err != nil || rel == "." || rel == ".." || strings.HasPrefix(rel, ".."+string(filepath.Separator)) {
		return "", invalid(field, "%q is not inside the media root", path)
	}

	return rel, nil
}

// invalid is INVALID_ARGUMENT for the request field field.
func invalid(field, format string, args ...any) error {
	return fmt.Errorf("media: %w", server.InvalidArgument(field, format, args...))
}

// mediaType is whatsmeow's key type for t (download.go:42-52).
func mediaType(field string, t whatsmeowv1.MediaType) (whatsmeow.MediaType, error) {
	switch t {
	case whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE:
		return whatsmeow.MediaImage, nil
	case whatsmeowv1.MediaType_MEDIA_TYPE_VIDEO:
		return whatsmeow.MediaVideo, nil
	case whatsmeowv1.MediaType_MEDIA_TYPE_AUDIO:
		return whatsmeow.MediaAudio, nil
	case whatsmeowv1.MediaType_MEDIA_TYPE_DOCUMENT:
		return whatsmeow.MediaDocument, nil
	case whatsmeowv1.MediaType_MEDIA_TYPE_UNSPECIFIED:
		return "", invalid(field, "no media type")
	default:
		return "", invalid(field, "media type %d is not known", t)
	}
}

// sessionError maps whatsmeow's errors for a session that went offline meanwhile; the rest pass through.
func sessionError(err error) error {
	switch {
	case errors.Is(err, whatsmeow.ErrNotConnected):
		return fmt.Errorf("%w: %w", server.ErrNotConnected, err)
	case errors.Is(err, whatsmeow.ErrNotLoggedIn):
		return fmt.Errorf("%w: %w", server.ErrNotLoggedIn, err)
	default:
		return err
	}
}

// removeTemp closes and deletes one of the sidecar's temporary files.
func (svc *Service) removeTemp(f *os.File) {
	_ = f.Close()

	if err := os.Remove(f.Name()); err != nil && !errors.Is(err, fs.ErrNotExist) {
		svc.log.Warn().Err(err).Str("file", f.Name()).Msg("removing a temporary media file failed")
	}
}
