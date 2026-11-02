//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"path"
	"path/filepath"
	"strings"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/util/cbcutil"
	"go.mau.fi/whatsmeow/util/hkdfutil"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

// The fake's media server keeps each upload's ciphertext, as WhatsApp keeps the file the client POSTed, in a file of
// the media directory: a transfer of hundreds of MiB holds none of it in memory, and with a directory that outlives
// the process, a MediaRef stays downloadable across a sidecar restart. The crypto is whatsmeow's own (cbcutil,
// hkdfutil), so the hashes, the MAC and the media key are what whatsmeow would produce and check.

// macLength is the MAC whatsmeow appends to the ciphertext (download.go mediaHMACLength).
const macLength = 10

var (
	errNoMediaDir  = errors.New("the fake WhatsApp has no media directory")
	errUploadFails = errors.New("fake WhatsApp: FailNextUploads")
)

// Option configures a fake WhatsApp.
type Option func(*WhatsApp)

// WithMediaDir keeps the media server's files in dir.
func WithMediaDir(dir string) Option { return func(w *WhatsApp) { w.mediaDir = dir } }

// mediaKeys is whatsmeow's key expansion (download.go:322-325): iv, cipher key, mac key.
func mediaKeys(mediaKey []byte, appInfo whatsmeow.MediaType) (iv, cipherKey, macKey []byte) {
	expanded := hkdfutil.SHA256(mediaKey, nil, []byte(appInfo), 112)

	return expanded[:16], expanded[16:48], expanded[48:80]
}

// sidecarChunk is how much of the upload each streaming sidecar MAC covers (upload.go:156).
const sidecarChunk = 64 << 10

// streamingSidecar is the streaming sidecar whatsmeow makes for a video (upload.go:141-151, 158-181): for each 64 KiB
// of the uploaded file, ciphertext and MAC, the first 10 bytes of an HMAC-SHA256 over the 16 bytes before the chunk
// (the IV for the first) and the chunk.
func streamingSidecar(iv, macKey []byte, uploaded io.Reader) ([]byte, error) {
	var sidecar []byte

	before := iv
	chunk := make([]byte, sidecarChunk)

	for {
		n, err := io.ReadFull(uploaded, chunk)
		if n > 0 {
			mac := hmac.New(sha256.New, macKey)
			mac.Write(before)
			mac.Write(chunk[:n])
			sidecar = append(sidecar, mac.Sum(nil)[:macLength]...)
		}

		switch {
		case errors.Is(err, io.EOF), errors.Is(err, io.ErrUnexpectedEOF):
			return sidecar, nil
		case err != nil:
			return nil, fmt.Errorf("failed to read encrypted file for sidecar: %w", err)
		}

		// A whole chunk: the next MAC covers its last 16 bytes first.
		before = append(before[:0:0], chunk[sidecarChunk-aesBlock:]...)
	}
}

// aesBlock is the AES block size, the length of the IV and of what the next sidecar MAC covers before its chunk.
const aesBlock = 16

// blob is the file the media server keeps for directPath, which is /fake/<hex id>.
func (w *WhatsApp) blob(directPath string) (string, error) {
	if w.mediaDir == "" {
		return "", errNoMediaDir
	}

	id := path.Base(directPath)
	if !strings.HasPrefix(directPath, "/fake/") || strings.ContainsAny(id, `./\`) {
		return "", whatsmeow.ErrMediaDownloadFailedWith404
	}

	return filepath.Join(w.mediaDir, id), nil
}

// newBlob creates a new file on the media server, and returns it with its direct path.
func (w *WhatsApp) newBlob() (*os.File, string, error) {
	if w.mediaDir == "" {
		return nil, "", errNoMediaDir
	}

	if err := os.MkdirAll(w.mediaDir, 0o700); err != nil {
		return nil, "", fmt.Errorf("the media directory: %w", err)
	}

	id := make([]byte, 16)
	_, _ = rand.Read(id)

	f, err := os.OpenFile(filepath.Join(w.mediaDir, hex.EncodeToString(id)), os.O_RDWR|os.O_CREATE|os.O_EXCL, 0o600)
	if err != nil {
		return nil, "", fmt.Errorf("a media file: %w", err)
	}

	return f, "/fake/" + hex.EncodeToString(id), nil
}

// UploadReader encrypts the plaintext into tempFile with a new media key, as whatsmeow does (upload.go:115-154), then
// "POSTs" the temporary file: the media server keeps a copy. FailNextUploads makes the POST fail after the
// encryption, as a network failure does (upload.go:291-293). Each attempt shows in WaitForSent.
func (c *Client) UploadReader(_ context.Context, plaintext io.Reader, tempFile io.ReadWriteSeeker, appInfo whatsmeow.MediaType) (whatsmeow.UploadResponse, error) {
	var resp whatsmeow.UploadResponse

	if !c.IsConnected() {
		return resp, fmt.Errorf("failed to refresh media connections: %w", whatsmeow.ErrNotConnected)
	}

	resp.MediaKey = make([]byte, 32)
	_, _ = rand.Read(resp.MediaKey)
	iv, cipherKey, macKey := mediaKeys(resp.MediaKey, appInfo)

	var err error

	resp.FileSHA256, resp.FileEncSHA256, resp.FileLength, _, err = cbcutil.EncryptStream(cipherKey, iv, macKey, plaintext, tempFile)
	if err != nil {
		return whatsmeow.UploadResponse{}, fmt.Errorf("failed to encrypt file: %w", err)
	}

	if c.whatsApp.take(c.sessionID, func(s *script) bool { return s.failUploads > 0 }, func(s *script) { s.failUploads-- }) {
		c.whatsApp.append(&fakev1.Sent{SessionId: c.sessionID, Kind: &fakev1.Sent_MediaUpload{MediaUpload: &fakev1.MediaUpload{MediaType: string(appInfo)}}})

		return whatsmeow.UploadResponse{}, fmt.Errorf("failed to execute request: %w", &net.OpError{Op: "dial", Net: "tcp", Err: errUploadFails})
	}

	if _, err := tempFile.Seek(0, io.SeekStart); err != nil {
		return whatsmeow.UploadResponse{}, fmt.Errorf("failed to seek to start of temporary file: %w", err)
	}

	if appInfo == whatsmeow.MediaVideo {
		if resp.StreamingSidecar, err = streamingSidecar(iv, macKey, tempFile); err != nil {
			return whatsmeow.UploadResponse{}, err
		}

		if _, err := tempFile.Seek(0, io.SeekStart); err != nil {
			return whatsmeow.UploadResponse{}, fmt.Errorf("failed to seek to start of temporary file: %w", err)
		}
	}

	stored, directPath, err := c.whatsApp.newBlob()
	if err != nil {
		return whatsmeow.UploadResponse{}, err
	}
	defer func() { _ = stored.Close() }()

	if _, err := io.Copy(stored, tempFile); err != nil {
		return whatsmeow.UploadResponse{}, fmt.Errorf("failed to execute request: %w", err)
	}

	resp.DirectPath, resp.URL = directPath, "https://fake.whatsapp.net"+directPath
	c.whatsApp.append(&fakev1.Sent{SessionId: c.sessionID, Kind: &fakev1.Sent_MediaUpload{MediaUpload: &fakev1.MediaUpload{MediaType: string(appInfo), DirectPath: directPath}}})

	return resp, nil
}

// DownloadMediaWithPathToFile downloads the ciphertext into file and verifies and decrypts it there, with
// whatsmeow's checks in whatsmeow's order (download-to-file.go:121-161, 209-246), so a failed check leaves what
// whatsmeow leaves in the file. ExpireMedia's statuses and HoldNextDownload come first.
func (c *Client) DownloadMediaWithPathToFile(ctx context.Context, directPath string, encFileHash, fileHash, mediaKey []byte, mediaType whatsmeow.MediaType,
	_ string, _ bool, file whatsmeow.File,
) error {
	if !c.IsConnected() {
		return fmt.Errorf("failed to refresh media connections: %w", whatsmeow.ErrNotConnected)
	}

	c.whatsApp.append(&fakev1.Sent{SessionId: c.sessionID, Kind: &fakev1.Sent_MediaDownload{MediaDownload: &fakev1.MediaDownload{DirectPath: directPath}}})

	if err := c.whatsApp.expiredWith(directPath); err != nil {
		return err
	}

	name, err := c.whatsApp.blob(directPath)
	if err != nil {
		return err
	}

	stored, err := os.Open(name) //nolint:gosec // G304: a file of the fake's media directory
	if err != nil {
		return whatsmeow.ErrMediaDownloadFailedWith404
	}
	defer func() { _ = stored.Close() }()

	if c.whatsApp.take(c.sessionID, func(s *script) bool { return s.holdDownload }, func(s *script) { s.holdDownload = false }) {
		_, _ = io.CopyN(file, stored, macLength)
		<-ctx.Done()

		return ctx.Err() //nolint:wrapcheck // as whatsmeow's HTTP request reports its context
	}

	return verify(stored, file, encFileHash, fileHash, mediaKey, mediaType)
}

// verify copies the ciphertext into file, then checks and decrypts it as whatsmeow does.
func verify(stored io.Reader, file whatsmeow.File, encFileHash, fileHash, mediaKey []byte, mediaType whatsmeow.MediaType) error {
	encHash := sha256.New()

	size, err := io.Copy(file, io.TeeReader(stored, encHash))
	if err != nil {
		return fmt.Errorf("download: %w", err)
	}

	switch {
	case size <= macLength:
		return whatsmeow.ErrTooShortFile
	case !hmac.Equal(encFileHash, encHash.Sum(nil)):
		return whatsmeow.ErrInvalidMediaEncSHA256
	}

	mac := make([]byte, macLength)
	if _, err := file.ReadAt(mac, size-macLength); err != nil {
		return fmt.Errorf("failed to read MAC from file: %w", err)
	}

	if err := file.Truncate(size - macLength); err != nil {
		return fmt.Errorf("failed to truncate file to remove MAC: %w", err)
	}

	iv, cipherKey, macKey := mediaKeys(mediaKey, mediaType)
	h := hmac.New(sha256.New, macKey)
	h.Write(iv)

	if _, err := file.Seek(0, io.SeekStart); err != nil {
		return fmt.Errorf("failed to seek to start of file: %w", err)
	}

	if _, err := io.Copy(h, file); err != nil {
		return fmt.Errorf("failed to hash file: %w", err)
	}

	if !hmac.Equal(h.Sum(nil)[:macLength], mac) {
		return whatsmeow.ErrInvalidMediaHMAC
	}

	if _, err := file.Seek(0, io.SeekStart); err != nil {
		return fmt.Errorf("failed to seek to start of file after validating mac: %w", err)
	}

	if err := cbcutil.DecryptFile(cipherKey, iv, file); err != nil {
		return fmt.Errorf("failed to decrypt file: %w", err)
	}

	plain := sha256.New()

	if _, err := file.Seek(0, io.SeekStart); err != nil {
		return fmt.Errorf("failed to seek to start of file after decrypting: %w", err)
	}

	if _, err := io.Copy(plain, file); err != nil {
		return fmt.Errorf("failed to hash file: %w", err)
	}

	if !hmac.Equal(fileHash, plain.Sum(nil)) {
		return whatsmeow.ErrInvalidMediaSHA256
	}

	return nil
}

// expiredWith is the error ExpireMedia set for directPath, or nil.
func (w *WhatsApp) expiredWith(directPath string) error {
	w.mu.Lock()
	status := w.expired[directPath]
	w.mu.Unlock()

	switch status {
	case 0:
		return nil
	case 403:
		return whatsmeow.ErrMediaDownloadFailedWith403
	case 410:
		return whatsmeow.ErrMediaDownloadFailedWith410
	default:
		return whatsmeow.ErrMediaDownloadFailedWith404
	}
}

// retryRequest is a media retry receipt the phone has not answered: what it named, and the media key it carried.
type retryRequest struct {
	info types.MessageInfo
	key  []byte
}

// SendMediaRetryReceipt sends the server-error receipt that asks the phone to upload the media again
// (mediaretry.go:77-119). The phone will encrypt its answer with the media key the receipt carried.
func (c *Client) SendMediaRetryReceipt(_ context.Context, message *types.MessageInfo, mediaKey []byte) error {
	if !c.IsConnected() {
		return whatsmeow.ErrNotConnected
	}

	c.whatsApp.mu.Lock()
	c.whatsApp.retries[c.sessionID+"\x00"+message.ID] = retryRequest{info: *message, key: mediaKey}
	c.whatsApp.mu.Unlock()

	receipt := &fakev1.MediaRetryReceipt{MessageId: message.ID, Chat: message.Chat.String(), IsFromMe: message.IsFromMe}
	if message.IsGroup {
		receipt.Sender = message.Sender.String()
	}

	c.whatsApp.append(&fakev1.Sent{SessionId: c.sessionID, Kind: &fakev1.Sent_MediaRetryReceipt{MediaRetryReceipt: receipt}})

	return nil
}
