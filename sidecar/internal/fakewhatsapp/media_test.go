//go:build fakewhatsapp

package fakewhatsapp

import (
	"bytes"
	"context"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"errors"
	"os"
	"path/filepath"
	"testing"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waMmsRetry"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

// mediaOnline is a connected client of session s1 on a fake with a media directory.
func mediaOnline(t *testing.T) (*WhatsApp, *Client, fakev1.FakeWhatsAppServiceClient, context.Context) {
	t.Helper()

	whatsApp, control, ctx := start(t)
	whatsApp.mediaDir = t.TempDir()
	client := whatsApp.NewClient("s1")
	client.connected = true

	return whatsApp, client, control, ctx
}

// upload uploads content as an image, with a temporary file of the test's.
func upload(t *testing.T, client *Client, content []byte) (whatsmeow.UploadResponse, string, error) {
	t.Helper()

	temp, err := os.CreateTemp(t.TempDir(), "upload-*")
	if err != nil {
		t.Fatalf("temp: %v", err)
	}
	defer func() { _ = temp.Close() }()

	resp, err := client.UploadReader(context.Background(), bytes.NewReader(content), temp, whatsmeow.MediaImage)

	return resp, temp.Name(), err
}

// download downloads resp into a new file and returns what the file holds afterwards.
func download(ctx context.Context, t *testing.T, client *Client, resp whatsmeow.UploadResponse) ([]byte, error) {
	t.Helper()

	f, err := os.Create(filepath.Join(t.TempDir(), "download"))
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	defer func() { _ = f.Close() }()

	err = client.DownloadMediaWithPathToFile(ctx, resp.DirectPath, resp.FileEncSHA256, resp.FileSHA256, resp.MediaKey, whatsmeow.MediaImage, "", false, f)
	got, _ := os.ReadFile(f.Name())

	return got, err
}

func content(n int) []byte {
	b := make([]byte, n)
	_, _ = rand.Read(b)

	return b
}

func TestAnUploadIsEncryptedAsWhatsmeowEncryptsAndDownloadsBack(t *testing.T) {
	t.Parallel()

	_, client, control, ctx := mediaOnline(t)
	plain := content(100_000)

	resp, temp, err := upload(t, client, plain)
	if err != nil {
		t.Fatalf("UploadReader: %v", err)
	}

	sum := sha256.Sum256(plain)
	ciphertext, _ := os.ReadFile(temp) //nolint:gosec // G304: the test's own file
	encSum := sha256.Sum256(ciphertext)

	// whatsmeow's layout: PKCS#7-padded AES-CBC, then a 10-byte MAC, all in the temporary file (upload.go:131).
	if !bytes.Equal(resp.FileSHA256, sum[:]) || !bytes.Equal(resp.FileEncSHA256, encSum[:]) || resp.FileLength != uint64(len(plain)) ||
		len(ciphertext) != (len(plain)/16+1)*16+macLength || len(resp.MediaKey) != 32 || resp.URL != "https://fake.whatsapp.net"+resp.DirectPath {
		t.Fatalf("UploadReader = %+v with %d bytes of ciphertext", resp, len(ciphertext))
	}

	wantSent(t, waitForSent(ctx, t, control, "s1", 0), &fakev1.Sent{Index: 1, SessionId: "s1", Kind: &fakev1.Sent_MediaUpload{
		MediaUpload: &fakev1.MediaUpload{MediaType: string(whatsmeow.MediaImage), DirectPath: resp.DirectPath},
	}})

	got, err := download(ctx, t, client, resp)
	if err != nil || !bytes.Equal(got, plain) {
		t.Fatalf("DownloadMediaWithPathToFile = %d bytes, %v; want the upload", len(got), err)
	}

	wantSent(t, waitForSent(ctx, t, control, "s1", 1), &fakev1.Sent{Index: 2, SessionId: "s1", Kind: &fakev1.Sent_MediaDownload{
		MediaDownload: &fakev1.MediaDownload{DirectPath: resp.DirectPath},
	}})
}

func TestFailNextUploadsFailsThatManyAttempts(t *testing.T) {
	t.Parallel()

	_, client, control, ctx := mediaOnline(t)

	if _, err := control.FailNextUploads(ctx, &fakev1.FailNextUploadsRequest{SessionId: "s1", Count: 2}); err != nil {
		t.Fatalf("FailNextUploads: %v", err)
	}

	for range 2 {
		if _, _, err := upload(t, client, content(10)); err == nil {
			t.Fatal("a scripted upload failure succeeded")
		}
	}

	if _, _, err := upload(t, client, content(10)); err != nil {
		t.Fatalf("the third upload: %v", err)
	}

	for i, path := range []bool{false, false, true} {
		sent := waitForSent(ctx, t, control, "s1", uint64(i)).GetMediaUpload()
		if (sent.GetDirectPath() != "") != path {
			t.Fatalf("attempt %d = %v; want a path only for the one that succeeded", i+1, sent)
		}
	}
}

func TestExpireMediaAnswersEveryDownloadWithItsStatus(t *testing.T) {
	t.Parallel()

	_, client, control, ctx := mediaOnline(t)

	resp, _, err := upload(t, client, content(10))
	if err != nil {
		t.Fatalf("UploadReader: %v", err)
	}

	for status, want := range map[int32]error{403: whatsmeow.ErrMediaDownloadFailedWith403, 404: whatsmeow.ErrMediaDownloadFailedWith404, 410: whatsmeow.ErrMediaDownloadFailedWith410} {
		if _, err := control.ExpireMedia(ctx, &fakev1.ExpireMediaRequest{DirectPath: resp.DirectPath, Status: status}); err != nil {
			t.Fatalf("ExpireMedia: %v", err)
		}

		if _, err := download(ctx, t, client, resp); !errors.Is(err, want) {
			t.Fatalf("download after ExpireMedia(%d) = %v, want %v", status, err, want)
		}
	}
}

func TestACorruptOrWrongKeyedDownloadFailsAndLeavesNoPlaintext(t *testing.T) {
	t.Parallel()

	_, client, control, ctx := mediaOnline(t)
	plain := content(1000)

	resp, _, err := upload(t, client, plain)
	if err != nil {
		t.Fatalf("UploadReader: %v", err)
	}

	wrongKey := resp
	wrongKey.MediaKey = content(32)

	if got, err := download(ctx, t, client, wrongKey); !errors.Is(err, whatsmeow.ErrInvalidMediaHMAC) || bytes.Contains(got, plain[:100]) {
		t.Fatalf("a wrong key = %v; want ErrInvalidMediaHMAC with no plaintext in the file", err)
	}

	if _, err := control.CorruptMedia(ctx, &fakev1.CorruptMediaRequest{DirectPath: resp.DirectPath}); err != nil {
		t.Fatalf("CorruptMedia: %v", err)
	}

	if got, err := download(ctx, t, client, resp); !errors.Is(err, whatsmeow.ErrInvalidMediaEncSHA256) || bytes.Contains(got, plain[:100]) {
		t.Fatalf("a corrupt copy = %v; want ErrInvalidMediaEncSHA256 with no plaintext in the file", err)
	}
}

func TestHoldNextDownloadWaitsForTheCallToEnd(t *testing.T) {
	t.Parallel()

	_, client, control, ctx := mediaOnline(t)

	resp, _, err := upload(t, client, content(1000))
	if err != nil {
		t.Fatalf("UploadReader: %v", err)
	}

	if _, err := control.HoldNextDownload(ctx, &fakev1.HoldNextDownloadRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("HoldNextDownload: %v", err)
	}

	held, cancel := context.WithCancel(ctx)
	ended := make(chan error, 1)

	go func() {
		_, err := download(held, t, client, resp)
		ended <- err
	}()

	// The download started, then waits until its call ends.
	_ = waitForSent(ctx, t, control, "s1", 1)
	cancel()

	if err := <-ended; !errors.Is(err, context.Canceled) {
		t.Fatalf("a held download ended with %v, want its context's error", err)
	}

	if _, err := download(ctx, t, client, resp); err != nil {
		t.Fatalf("the next download: %v", err)
	}
}

func TestADownloadTakesTheHoldBeforeItShowsInWaitForSent(t *testing.T) {
	t.Parallel()

	whatsApp, client, control, ctx := mediaOnline(t)

	resp, _, err := upload(t, client, content(10))
	if err != nil {
		t.Fatalf("UploadReader: %v", err)
	}

	if _, err := control.HoldNextDownload(ctx, &fakev1.HoldNextDownloadRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("HoldNextDownload: %v", err)
	}

	f, err := os.Create(filepath.Join(t.TempDir(), "download"))
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	defer func() { _ = f.Close() }()

	held, cancel := context.WithCancel(ctx)
	defer cancel()

	ended := make(chan error, 1)

	// The goroutine only reports what the download returned; the test goroutine fails on it.
	go func() {
		ended <- client.DownloadMediaWithPathToFile(held, resp.DirectPath, resp.FileEncSHA256, resp.FileSHA256, resp.MediaKey, whatsmeow.MediaImage, "", false, f)
	}()

	// Each look reads the log and the hold under one lock, so the first look that sees the download sees the hold
	// as it was when the download showed (#182). The test's context bounds the looks.
	for {
		shown, holdLeft := downloadShown(whatsApp, "s1")
		if !shown {
			if ctx.Err() != nil {
				t.Fatalf("the download never showed in WaitForSent: %v", ctx.Err())
			}

			continue
		}

		if holdLeft {
			t.Fatal("the download showed in WaitForSent before it took the hold")
		}

		break
	}

	cancel()

	if err := <-ended; !errors.Is(err, context.Canceled) {
		t.Fatalf("the held download ended with %v, want its context's error", err)
	}
}

// downloadShown reports whether the session's log has a download, and whether its hold is still left.
func downloadShown(w *WhatsApp, sessionID string) (shown, holdLeft bool) {
	w.mu.Lock()
	defer w.mu.Unlock()

	for _, sent := range w.sent {
		if sent.GetSessionId() == sessionID && sent.GetMediaDownload() != nil {
			shown = true
		}
	}

	s, ok := w.scripts[sessionID]

	return shown, ok && s.holdDownload
}

func TestHoldNextDownloadIsTakenByTheNextDownloadEvenOneThatFails(t *testing.T) {
	t.Parallel()

	_, client, control, ctx := mediaOnline(t)

	expired, _, err := upload(t, client, content(10))
	if err != nil {
		t.Fatalf("UploadReader: %v", err)
	}

	resp, _, err := upload(t, client, content(10))
	if err != nil {
		t.Fatalf("UploadReader: %v", err)
	}

	if _, err := control.ExpireMedia(ctx, &fakev1.ExpireMediaRequest{DirectPath: expired.DirectPath, Status: 410}); err != nil {
		t.Fatalf("ExpireMedia: %v", err)
	}

	if _, err := control.HoldNextDownload(ctx, &fakev1.HoldNextDownloadRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("HoldNextDownload: %v", err)
	}

	// The next download shows in WaitForSent with the hold already taken, even one that then fails: a test that saw
	// it there and scripts the next hold never has a late download take that one instead (#182).
	if _, err := download(ctx, t, client, expired); !errors.Is(err, whatsmeow.ErrMediaDownloadFailedWith410) {
		t.Fatalf("the expired download = %v, want ErrMediaDownloadFailedWith410", err)
	}

	cancelled, cancel := context.WithCancel(ctx)
	cancel()

	// A held download would end with its context's error; this one is not held.
	if _, err := download(cancelled, t, client, resp); err != nil {
		t.Fatalf("the download after the held one = %v, want no hold left", err)
	}
}

func TestAnsweredMediaRetriesDecryptAsWhatsmeowDecryptsThem(t *testing.T) {
	t.Parallel()

	_, client, control, ctx := mediaOnline(t)

	resp, _, err := upload(t, client, content(1000))
	if err != nil {
		t.Fatalf("UploadReader: %v", err)
	}

	answers := make(chan *events.MediaRetry, 4)
	client.AddEventHandlerWithSuccessStatus(func(evt any) bool {
		if retry, ok := evt.(*events.MediaRetry); ok {
			answers <- retry
		}

		return false // acknowledged anyway: the fake records nothing for it
	})

	info := &types.MessageInfo{ID: "M1", MessageSource: types.MessageSource{
		Chat: types.NewJID("120363000000000000", types.GroupServer), Sender: types.NewJID("111", types.HiddenUserServer), IsGroup: true, IsFromMe: true,
	}}

	if err := client.SendMediaRetryReceipt(ctx, info, resp.MediaKey); err != nil {
		t.Fatalf("SendMediaRetryReceipt: %v", err)
	}

	wantSent(t, waitForSent(ctx, t, control, "s1", 1), &fakev1.Sent{Index: 2, SessionId: "s1", Kind: &fakev1.Sent_MediaRetryReceipt{
		MediaRetryReceipt: &fakev1.MediaRetryReceipt{MessageId: "M1", Chat: "120363000000000000@g.us", Sender: "111@lid", IsFromMe: true},
	}})

	if _, err := control.AnswerMediaRetry(ctx, &fakev1.AnswerMediaRetryRequest{
		SessionId: "s1", MessageId: "M1",
		Answer: &fakev1.AnswerMediaRetryRequest_ReuploadFrom{ReuploadFrom: resp.DirectPath},
	}); err != nil {
		t.Fatalf("AnswerMediaRetry: %v", err)
	}

	// AnswerMediaRetry returns once the handlers ran.
	answer := <-answers
	if answer.MessageID != "M1" || answer.ChatID != info.Chat || answer.SenderID != info.Sender || !answer.FromMe {
		t.Fatalf("the answer names %+v", answer)
	}

	notif, err := whatsmeow.DecryptMediaRetryNotification(answer, resp.MediaKey)
	if err != nil || notif.GetResult() != waMmsRetry.MediaRetryNotification_SUCCESS || notif.GetDirectPath() == resp.DirectPath {
		t.Fatalf("decrypted %v, %v; want SUCCESS with a new path", notif, err)
	}

	moved := resp
	moved.DirectPath = notif.GetDirectPath()

	if _, err := download(ctx, t, client, moved); err != nil {
		t.Fatalf("the new path: %v", err)
	}

	if _, err := control.AnswerMediaRetry(ctx, &fakev1.AnswerMediaRetryRequest{
		SessionId: "s1", MessageId: "M1",
		Answer: &fakev1.AnswerMediaRetryRequest_ErrorCode{ErrorCode: 2},
	}); err != nil {
		t.Fatalf("AnswerMediaRetry: %v", err)
	}

	if _, err := whatsmeow.DecryptMediaRetryNotification(<-answers, resp.MediaKey); !errors.Is(err, whatsmeow.ErrMediaNotAvailableOnPhone) {
		t.Fatalf("an error code 2 decrypted to %v, want ErrMediaNotAvailableOnPhone", err)
	}

	if _, err := control.AnswerMediaRetry(ctx, &fakev1.AnswerMediaRetryRequest{
		SessionId: "s1", MessageId: "M9",
		Answer: &fakev1.AnswerMediaRetryRequest_ErrorCode{ErrorCode: 2},
	}); err == nil {
		t.Fatal("an answer to no request succeeded")
	}
}

func TestAVideoUploadHasAStreamingSidecarAsWhatsmeowMakesIt(t *testing.T) {
	t.Parallel()

	_, client, _, _ := mediaOnline(t)

	// What is uploaded is the padded ciphertext and a 10-byte MAC, so it is never a whole number of 64 KiB chunks; at
	// 131_056 bytes the ciphertext is two whole chunks, and the last MAC covers only the upload's MAC.
	for _, tc := range []struct {
		plain, uploaded, macs int
	}{{150_000, 150_026, 3}, {131_056, 131_082, 3}} {
		plain := content(tc.plain)

		temp, err := os.CreateTemp(t.TempDir(), "upload-*")
		if err != nil {
			t.Fatalf("temp: %v", err)
		}

		resp, err := client.UploadReader(context.Background(), bytes.NewReader(plain), temp, whatsmeow.MediaVideo)
		_ = temp.Close()

		if err != nil {
			t.Fatalf("UploadReader(%d bytes): %v", tc.plain, err)
		}

		// whatsmeow's layout (upload.go:156-181): for each 64 KiB of what was uploaded, the first 10 bytes of an
		// HMAC-SHA256 over the 16 bytes before it (the IV for the first) and the chunk.
		uploaded, _ := os.ReadFile(temp.Name())
		if len(uploaded) != tc.uploaded {
			t.Fatalf("uploaded %d bytes for %d; want %d", len(uploaded), tc.plain, tc.uploaded)
		}

		iv, _, macKey := mediaKeys(resp.MediaKey, whatsmeow.MediaVideo)

		var want []byte

		for start := 0; start < len(uploaded); start += 64 << 10 {
			before := iv
			if start > 0 {
				before = uploaded[start-16 : start]
			}

			mac := hmac.New(sha256.New, macKey)
			mac.Write(before)
			mac.Write(uploaded[start:min(start+64<<10, len(uploaded))])
			want = append(want, mac.Sum(nil)[:10]...)
		}

		if len(want) != tc.macs*10 || !bytes.Equal(resp.StreamingSidecar, want) {
			t.Fatalf("StreamingSidecar of %d bytes = %x; want %x", tc.plain, resp.StreamingSidecar, want)
		}
	}

	image, _, err := upload(t, client, content(150_000))
	if err != nil || image.StreamingSidecar != nil {
		t.Fatalf("an image upload = %+v, %v; want no streaming sidecar", image, err)
	}
}
