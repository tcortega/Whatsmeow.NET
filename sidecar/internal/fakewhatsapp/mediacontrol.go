//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"crypto/rand"
	"errors"
	"fmt"
	"io"
	"os"

	"go.mau.fi/whatsmeow/proto/waMmsRetry"
	"go.mau.fi/whatsmeow/types/events"
	"go.mau.fi/whatsmeow/util/gcmutil"
	"go.mau.fi/whatsmeow/util/hkdfutil"
	"google.golang.org/protobuf/proto"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

var errNoRetryRequest = errors.New("no media retry request for the message")

// FailNextUploads makes the session's next uploads fail after they encrypted the file.
func (c *Control) FailNextUploads(_ context.Context, req *fakev1.FailNextUploadsRequest) (*fakev1.FailNextUploadsResponse, error) {
	c.whatsApp.setScript(req.GetSessionId(), func(s *script) { s.failUploads = req.GetCount() })

	return &fakev1.FailNextUploadsResponse{}, nil
}

// ExpireMedia makes WhatsApp answer the downloads of a direct path with a status.
func (c *Control) ExpireMedia(_ context.Context, req *fakev1.ExpireMediaRequest) (*fakev1.ExpireMediaResponse, error) {
	switch req.GetStatus() {
	case 403, 404, 410:
	default:
		return nil, fmt.Errorf("expire %s: status %d is not 403, 404 or 410", req.GetDirectPath(), req.GetStatus())
	}

	c.whatsApp.mu.Lock()
	c.whatsApp.expired[req.GetDirectPath()] = req.GetStatus()
	c.whatsApp.mu.Unlock()

	return &fakev1.ExpireMediaResponse{}, nil
}

// CorruptMedia flips one byte of the stored ciphertext.
func (c *Control) CorruptMedia(_ context.Context, req *fakev1.CorruptMediaRequest) (*fakev1.CorruptMediaResponse, error) {
	name, err := c.whatsApp.blob(req.GetDirectPath())
	if err != nil {
		return nil, fmt.Errorf("corrupt %s: %w", req.GetDirectPath(), err)
	}

	f, err := os.OpenFile(name, os.O_RDWR, 0) //nolint:gosec // G304: a file of the fake's media directory
	if err != nil {
		return nil, fmt.Errorf("corrupt %s: %w", req.GetDirectPath(), err)
	}
	defer func() { _ = f.Close() }()

	b := make([]byte, 1)
	if _, err := f.ReadAt(b, 0); err != nil {
		return nil, fmt.Errorf("corrupt %s: %w", req.GetDirectPath(), err)
	}

	b[0] ^= 0xff

	if _, err := f.WriteAt(b, 0); err != nil {
		return nil, fmt.Errorf("corrupt %s: %w", req.GetDirectPath(), err)
	}

	return &fakev1.CorruptMediaResponse{}, nil
}

// HoldNextDownload holds the session's next download until its call ends.
func (c *Control) HoldNextDownload(_ context.Context, req *fakev1.HoldNextDownloadRequest) (*fakev1.HoldNextDownloadResponse, error) {
	c.whatsApp.setScript(req.GetSessionId(), func(s *script) { s.holdDownload = true })

	return &fakev1.HoldNextDownloadResponse{}, nil
}

// AnswerMediaRetry makes the phone answer a media retry request, as whatsmeow parses the answer
// (mediaretry.go:139-176), encrypted under the request's media key (mediaretry.go:24-41), and dispatches it on the
// node queue (notification.go:510-511).
func (c *Control) AnswerMediaRetry(_ context.Context, req *fakev1.AnswerMediaRetryRequest) (*fakev1.AnswerMediaRetryResponse, error) {
	client, err := c.whatsApp.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	c.whatsApp.mu.Lock()
	request, ok := c.whatsApp.retries[req.GetSessionId()+"\x00"+req.GetMessageId()]
	c.whatsApp.mu.Unlock()

	if !ok {
		return nil, fmt.Errorf("answer %q of session %q: %w", req.GetMessageId(), req.GetSessionId(), errNoRetryRequest)
	}

	evt := &events.MediaRetry{
		Timestamp: timeOf(req.GetAnswerTime()), MessageID: request.info.ID, ChatID: request.info.Chat, FromMe: request.info.IsFromMe,
	}
	if request.info.IsGroup {
		evt.SenderID = request.info.Sender
	}

	notif := &waMmsRetry.MediaRetryNotification{StanzaID: proto.String(request.info.ID)}

	switch answer := req.GetAnswer().(type) {
	case *fakev1.AnswerMediaRetryRequest_ReuploadFrom:
		directPath, err := c.whatsApp.reupload(answer.ReuploadFrom)
		if err != nil {
			return nil, err
		}

		notif.Result, notif.DirectPath = waMmsRetry.MediaRetryNotification_SUCCESS.Enum(), proto.String(directPath)
	case *fakev1.AnswerMediaRetryRequest_Result:
		notif.Result = waMmsRetry.MediaRetryNotification_ResultType(answer.Result).Enum()
	case *fakev1.AnswerMediaRetryRequest_ErrorCode:
		evt.Error = &events.MediaRetryError{Code: int(answer.ErrorCode)}
	case *fakev1.AnswerMediaRetryRequest_Encrypted:
		evt.Ciphertext, evt.IV = answer.Encrypted.GetCiphertext(), answer.Encrypted.GetIv()
	default:
		return nil, fmt.Errorf("answer %q: no answer", req.GetMessageId())
	}

	if evt.Error == nil && req.GetEncrypted() == nil {
		if evt.Ciphertext, evt.IV, err = encryptAnswer(request.key, request.info.ID, notif); err != nil {
			return nil, err
		}
	}

	// whatsmeow acknowledges the notification whatever the handlers return, so nothing is recorded.
	client.dispatch(evt)

	return &fakev1.AnswerMediaRetryResponse{}, nil
}

// reupload copies the ciphertext at directPath to a new path, as the phone uploads the same encrypted file again.
func (w *WhatsApp) reupload(directPath string) (string, error) {
	name, err := w.blob(directPath)
	if err != nil {
		return "", fmt.Errorf("reupload %s: %w", directPath, err)
	}

	from, err := os.Open(name) //nolint:gosec // G304: a file of the fake's media directory
	if err != nil {
		return "", fmt.Errorf("reupload %s: %w", directPath, err)
	}
	defer func() { _ = from.Close() }()

	to, newPath, err := w.newBlob()
	if err != nil {
		return "", err
	}
	defer func() { _ = to.Close() }()

	if _, err := io.Copy(to, from); err != nil {
		return "", fmt.Errorf("reupload %s: %w", directPath, err)
	}

	return newPath, nil
}

// encryptAnswer encrypts the phone's answer as the phone does: AES-GCM under the media key's retry key, with the
// message id as additional data (mediaretry.go:24-26, 130).
func encryptAnswer(mediaKey []byte, messageID string, notif *waMmsRetry.MediaRetryNotification) (ciphertext, iv []byte, err error) {
	plaintext, err := proto.Marshal(notif)
	if err != nil {
		return nil, nil, fmt.Errorf("the answer: %w", err)
	}

	iv = make([]byte, 12)
	_, _ = rand.Read(iv)

	ciphertext, err = gcmutil.Encrypt(hkdfutil.SHA256(mediaKey, nil, []byte("WhatsApp Media Retry Notification"), 32), iv, plaintext, []byte(messageID))
	if err != nil {
		return nil, nil, fmt.Errorf("encrypt the answer: %w", err)
	}

	return ciphertext, iv, nil
}
