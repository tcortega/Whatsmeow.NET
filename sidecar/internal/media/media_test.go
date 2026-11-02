package media

import (
	"bytes"
	"context"
	"crypto/sha256"
	"os"
	"path/filepath"
	"runtime"
	"slices"
	"strings"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"google.golang.org/grpc/codes"
	"google.golang.org/protobuf/proto"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// wantRef fails unless media is what uploading content as mediaType gives: the plaintext's hash and length, and the
// fields only WhatsApp knows as it answered.
func wantRef(t *testing.T, media *whatsmeowv1.MediaRef, mediaType whatsmeowv1.MediaType, content []byte) {
	t.Helper()

	sum := sha256.Sum256(content)
	want := &whatsmeowv1.MediaRef{
		MediaType: mediaType, DirectPath: media.GetDirectPath(), Url: "https://stub" + media.GetDirectPath(), MediaKey: media.GetMediaKey(),
		FileSha256: sum[:], FileEncSha256: media.GetFileEncSha256(), FileLength: uint64(len(content)), StreamingSidecar: media.GetStreamingSidecar(),
	}

	if !proto.Equal(media, want) || len(media.GetMediaKey()) != 32 || len(media.GetDirectPath()) == 0 {
		t.Fatalf("media = %v, want %v with a 32-byte key and a path", media, want)
	}
}

func TestAPathUnderTheRootIsUploadedWithoutTouchingTheFile(t *testing.T) {
	t.Parallel()

	h := start(t)
	content := random(200 << 10)
	path := file(t, h.root, "photo.jpg", content)

	media, err := h.uploadPath(h.ctx, path, whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE)
	if err != nil {
		t.Fatalf("UploadMedia: %v", err)
	}

	wantRef(t, media, whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE, content)

	// whatsmeow overwrites its temporary file with ciphertext: the user's file is never that file.
	if got, _ := os.ReadFile(path); !bytes.Equal(got, content) { //nolint:gosec // G304: the test's own file
		t.Fatal("the uploaded file changed")
	}

	if slices.Contains(h.stub.temps, path) {
		t.Fatalf("the user's file was whatsmeow's temporary file: %v", h.stub.temps)
	}

	if got := entries(t, h.temp); len(got) != 0 {
		t.Fatalf("the sidecar's temporary directory has %v after the upload", got)
	}

	var plain bytes.Buffer
	if _, err := h.downloadChunks(h.ctx, media, &plain); err != nil || !bytes.Equal(plain.Bytes(), content) {
		t.Fatalf("the download gave %d bytes, %v; want the upload", plain.Len(), err)
	}
}

func TestAVideoKeepsItsStreamingSidecar(t *testing.T) {
	t.Parallel()

	h := start(t)

	media, err := h.uploadPath(h.ctx, file(t, h.root, "clip.mp4", random(10)), whatsmeowv1.MediaType_MEDIA_TYPE_VIDEO)
	if err != nil || string(media.GetStreamingSidecar()) != "sidecar" {
		t.Fatalf("UploadMedia = %v, %v; want the streaming sidecar whatsmeow made", media, err)
	}
}

func TestChunksRoundTripByteForByte(t *testing.T) {
	t.Parallel()

	h := start(t)
	content := random(1<<20 + 123)

	media, err := h.uploadChunks(h.ctx, bytes.NewReader(content), 64<<10)
	if err != nil {
		t.Fatalf("UploadMedia: %v", err)
	}

	wantRef(t, media, whatsmeowv1.MediaType_MEDIA_TYPE_DOCUMENT, content)

	var plain bytes.Buffer
	if _, err := h.downloadChunks(h.ctx, media, &plain); err != nil || !bytes.Equal(plain.Bytes(), content) {
		t.Fatalf("the download gave %d bytes, %v; want the upload", plain.Len(), err)
	}

	dest := filepath.Join(h.root, "copy.bin")
	if err := h.downloadPath(h.ctx, media, dest); err != nil {
		t.Fatalf("DownloadMedia to a path: %v", err)
	}

	if got, _ := os.ReadFile(dest); !bytes.Equal(got, content) { //nolint:gosec // G304: the test's own file
		t.Fatal("the downloaded file differs from the upload")
	}

	if got := entries(t, h.root); !slices.Equal(got, []string{"copy.bin"}) {
		t.Fatalf("the destination directory has %v, want only the file", got)
	}

	if got := entries(t, h.temp); len(got) != 0 {
		t.Fatalf("the sidecar's temporary directory has %v", got)
	}
}

func TestADownloadByPathReplacesAnExistingFile(t *testing.T) {
	t.Parallel()

	h := start(t)
	content := random(1000)

	media, err := h.uploadChunks(h.ctx, bytes.NewReader(content), 64<<10)
	if err != nil {
		t.Fatalf("UploadMedia: %v", err)
	}

	dest := file(t, h.root, "old.bin", []byte("old"))
	if err := h.downloadPath(h.ctx, media, dest); err != nil {
		t.Fatalf("DownloadMedia: %v", err)
	}

	if got, _ := os.ReadFile(dest); !bytes.Equal(got, content) { //nolint:gosec // G304: the test's own file
		t.Fatal("the file was not replaced")
	}
}

func TestAChunkOver64KiBIsInvalid(t *testing.T) {
	t.Parallel()

	h := start(t)

	_, err := h.uploadChunks(h.ctx, bytes.NewReader(random(64<<10+1)), 64<<10+1)
	wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", "chunk")
}

func TestAnUploadNeedsOneHeaderFirst(t *testing.T) {
	t.Parallel()

	h := start(t)

	cases := map[string][]*whatsmeowv1.UploadMediaRequest{
		"no header":     {{Part: &whatsmeowv1.UploadMediaRequest_Chunk{Chunk: []byte{1}}}},
		"two headers":   {header("s1", whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE, ""), header("s1", whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE, "")},
		"no media type": {header("s1", whatsmeowv1.MediaType_MEDIA_TYPE_UNSPECIFIED, "")},
		"nothing":       {},
	}

	for name, parts := range cases {
		t.Run(name, func(t *testing.T) {
			t.Parallel()

			stream, err := h.client.UploadMedia(h.ctx)
			if err != nil {
				t.Fatalf("UploadMedia: %v", err)
			}

			for _, p := range parts {
				_ = stream.Send(p)
			}

			_, err = stream.CloseAndRecv()
			wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", "")
		})
	}
}

func TestChunksAfterAPathAreInvalid(t *testing.T) {
	t.Parallel()

	h := start(t)
	path := file(t, h.root, "a.bin", []byte("a"))

	stream, err := h.client.UploadMedia(h.ctx)
	if err != nil {
		t.Fatalf("UploadMedia: %v", err)
	}

	_ = stream.Send(header("s1", whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE, path))
	_ = stream.Send(&whatsmeowv1.UploadMediaRequest{Part: &whatsmeowv1.UploadMediaRequest_Chunk{Chunk: []byte{1}}})

	_, err = stream.CloseAndRecv()
	wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", "chunk")
}

func TestPathsOutsideTheRootAreRefusedAndNothingOutsideIsTouched(t *testing.T) {
	t.Parallel()

	h := start(t)
	outside := t.TempDir()
	secret := file(t, outside, "secret.bin", []byte("secret"))

	// A directory symlink out of the root, and an absolute symlink to a file inside it (os.Root refuses both).
	if err := os.Symlink(outside, filepath.Join(h.root, "out")); err != nil {
		t.Fatalf("symlink: %v", err)
	}

	inside := file(t, h.root, "inside.bin", []byte("inside"))
	if err := os.Symlink(inside, filepath.Join(h.root, "absolute")); err != nil {
		t.Fatalf("symlink: %v", err)
	}

	cases := map[string]string{
		"dot dot out":       h.root + string(filepath.Separator) + ".." + string(filepath.Separator) + "secret.bin",
		"outside":           secret,
		"symlink out":       filepath.Join(h.root, "out", "secret.bin"),
		"absolute symlink":  filepath.Join(h.root, "absolute"),
		"relative":          "inside.bin",
		"the root itself":   h.root,
		"a directory":       filepath.Join(h.root, "out"),
		"missing":           filepath.Join(h.root, "missing.bin"),
		"missing directory": filepath.Join(h.root, "missing", "x.bin"),
	}

	media, err := h.uploadChunks(h.ctx, bytes.NewReader([]byte("new")), 64<<10)
	if err != nil {
		t.Fatalf("UploadMedia: %v", err)
	}

	for name, path := range cases {
		t.Run(name, func(t *testing.T) {
			t.Parallel()

			if name != "missing" && name != "missing directory" {
				_, err := h.uploadPath(h.ctx, path, whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE)
				wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", "header.path")
			}

			if name == "missing" {
				return // a download may create a missing file
			}

			err := h.downloadPath(h.ctx, media, path)
			wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", "path")
		})
	}

	t.Cleanup(func() {
		if got, _ := os.ReadFile(secret); string(got) != "secret" { //nolint:gosec // G304: the test's own file
			t.Error("a file outside the root changed")
		}

		if got := entries(t, outside); !slices.Equal(got, []string{"secret.bin"}) {
			t.Errorf("the directory outside the root has %v", got)
		}
	})
}

func TestWithNoMediaRootEveryPathIsRefused(t *testing.T) {
	t.Parallel()

	h := startWith(t, newStub(t), false)
	path := file(t, t.TempDir(), "a.bin", []byte("a"))

	_, err := h.uploadPath(h.ctx, path, whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE)
	wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", "header.path")

	if _, err := h.uploadChunks(h.ctx, bytes.NewReader([]byte("a")), 64<<10); err != nil {
		t.Fatalf("chunks with no root: %v", err)
	}
}

func TestATransientUploadFailureIsRetriedWithANewAttempt(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.svc.backoff = []time.Duration{time.Millisecond, time.Millisecond}
	h.stub.failNext = 2
	content := random(100 << 10)

	media, err := h.uploadChunks(h.ctx, bytes.NewReader(content), 64<<10)
	if err != nil {
		t.Fatalf("UploadMedia: %v", err)
	}

	// Each attempt read the whole file again: the third one's hashes are the file's.
	wantRef(t, media, whatsmeowv1.MediaType_MEDIA_TYPE_DOCUMENT, content)

	if h.stub.count() != 3 {
		t.Fatalf("attempts = %d, want 3", h.stub.count())
	}

	if got := entries(t, h.temp); len(got) != 0 {
		t.Fatalf("the sidecar's temporary directory has %v", got)
	}
}

func TestAnUploadThatFailsEveryAttemptIsOneError(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.svc.backoff = []time.Duration{time.Millisecond, time.Millisecond}
	h.stub.failNext = 10

	_, err := h.uploadPath(h.ctx, file(t, h.root, "a.bin", []byte("a")), whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE)
	wantReason(t, err, codes.Internal, "INTERNAL", "")

	if h.stub.count() != 3 {
		t.Fatalf("attempts = %d, want 3", h.stub.count())
	}
}

func TestACancelDuringTheBackoffEndsTheUpload(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.svc.backoff = []time.Duration{time.Hour}
	h.stub.failNext = 1

	// The slice logs the retry before it waits: cancel once it waits.
	waiting := make(chan struct{})
	h.svc.log = zerolog.New(lineWriter(func(line string) {
		if strings.Contains(line, "retrying") {
			close(waiting)
		}
	}))

	ctx, cancel := context.WithCancel(h.ctx)
	go func() {
		<-waiting
		cancel()
	}()

	_, err := h.uploadChunks(ctx, bytes.NewReader([]byte("a")), 64<<10)
	if err == nil {
		t.Fatal("the upload succeeded after its call was cancelled")
	}

	// The handler stopped waiting and cleaned up: nothing is left behind.
	h.eventuallyEmpty(h.temp)
}

// lineWriter calls f with each log line.
type lineWriter func(line string)

func (w lineWriter) Write(p []byte) (int, error) {
	w(string(p))

	return len(p), nil
}

func TestExpiredMediaIsMediaExpired(t *testing.T) {
	t.Parallel()

	h := start(t)

	media, err := h.uploadChunks(h.ctx, bytes.NewReader([]byte("a")), 64<<10)
	if err != nil {
		t.Fatalf("UploadMedia: %v", err)
	}

	for _, expired := range []error{whatsmeow.ErrMediaDownloadFailedWith403, whatsmeow.ErrMediaDownloadFailedWith404, whatsmeow.ErrMediaDownloadFailedWith410} {
		h.stub.mu.Lock()
		h.stub.expired[media.GetDirectPath()] = expired
		h.stub.mu.Unlock()

		_, err := h.downloadChunks(h.ctx, media, &bytes.Buffer{})
		wantReason(t, err, codes.NotFound, "MEDIA_EXPIRED", "")

		err = h.downloadPath(h.ctx, media, filepath.Join(h.root, "x.bin"))
		wantReason(t, err, codes.NotFound, "MEDIA_EXPIRED", "")
	}
}

func TestAFailedMacDeliversNothing(t *testing.T) {
	t.Parallel()

	h := start(t)

	media, err := h.uploadChunks(h.ctx, bytes.NewReader(random(300<<10)), 64<<10)
	if err != nil {
		t.Fatalf("UploadMedia: %v", err)
	}

	h.stub.corrupt[media.GetDirectPath()] = true

	chunks, err := h.downloadChunks(h.ctx, media, &bytes.Buffer{})
	wantReason(t, err, codes.Internal, "INTERNAL", "")

	if chunks != 0 {
		t.Fatalf("%d chunks were sent before the MAC check failed", chunks)
	}

	err = h.downloadPath(h.ctx, media, filepath.Join(h.root, "x.bin"))
	wantReason(t, err, codes.Internal, "INTERNAL", "")

	if got := entries(t, h.root); len(got) != 0 {
		t.Fatalf("the destination directory has %v after a failed MAC check", got)
	}

	if got := entries(t, h.temp); len(got) != 0 {
		t.Fatalf("the sidecar's temporary directory has %v", got)
	}
}

func TestAWrongMediaKeyFailsTheMacCheck(t *testing.T) {
	t.Parallel()

	h := start(t)

	media, err := h.uploadChunks(h.ctx, bytes.NewReader([]byte("a")), 64<<10)
	if err != nil {
		t.Fatalf("UploadMedia: %v", err)
	}

	media.MediaKey = random(32)

	_, err = h.downloadChunks(h.ctx, media, &bytes.Buffer{})
	wantReason(t, err, codes.Internal, "INTERNAL", "")
}

func TestAPlaintextHashMismatchAfterAValidMacIsDelivered(t *testing.T) {
	t.Parallel()

	h := start(t)
	content := random(1000)

	media, err := h.uploadChunks(h.ctx, bytes.NewReader(content), 64<<10)
	if err != nil {
		t.Fatalf("UploadMedia: %v", err)
	}

	h.stub.badSHA[media.GetDirectPath()] = true

	var plain bytes.Buffer
	if _, err := h.downloadChunks(h.ctx, media, &plain); err != nil || !bytes.Equal(plain.Bytes(), content) {
		t.Fatalf("download = %d bytes, %v; want the plaintext, as mautrix tolerates ErrInvalidMediaSHA256", plain.Len(), err)
	}
}

func TestAnIncompleteMediaIsInvalid(t *testing.T) {
	t.Parallel()

	h := start(t)

	media, err := h.uploadChunks(h.ctx, bytes.NewReader([]byte("a")), 64<<10)
	if err != nil {
		t.Fatalf("UploadMedia: %v", err)
	}

	for name, broken := range map[string]func(m *whatsmeowv1.MediaRef){
		"no type":          func(m *whatsmeowv1.MediaRef) { m.MediaType = whatsmeowv1.MediaType_MEDIA_TYPE_UNSPECIFIED },
		"a relative path":  func(m *whatsmeowv1.MediaRef) { m.DirectPath = "stub/x" },
		"a short key":      func(m *whatsmeowv1.MediaRef) { m.MediaKey = m.MediaKey[:31] },
		"no plaintext sha": func(m *whatsmeowv1.MediaRef) { m.FileSha256 = nil },
		"no enc sha":       func(m *whatsmeowv1.MediaRef) { m.FileEncSha256 = nil },
	} {
		m := proto.CloneOf(media)
		broken(m)

		_, err := h.downloadChunks(h.ctx, m, &bytes.Buffer{})
		if st := err; st == nil {
			t.Fatalf("%s: the download succeeded", name)
		}

		wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", "media")
	}
}

func TestACancelledDownloadLeavesNoPartialFile(t *testing.T) {
	t.Parallel()

	h := start(t)

	media, err := h.uploadChunks(h.ctx, bytes.NewReader(random(1000)), 64<<10)
	if err != nil {
		t.Fatalf("UploadMedia: %v", err)
	}

	for _, byPath := range []bool{true, false} {
		h.stub.mu.Lock()
		h.stub.hold, h.stub.held = true, make(chan struct{})
		held := h.stub.held
		h.stub.mu.Unlock()

		ctx, cancel := context.WithCancel(h.ctx)
		go func() { <-held; cancel() }()

		if byPath {
			err = h.downloadPath(ctx, media, filepath.Join(h.root, "x.bin"))
		} else {
			_, err = h.downloadChunks(ctx, media, &bytes.Buffer{})
		}

		if err == nil {
			t.Fatalf("by path %v: the download succeeded after its call was cancelled", byPath)
		}

		// The handler cleans up as it returns, which can be after the client saw the cancel.
		h.eventuallyEmpty(h.root, h.temp)

		cancel()
	}
}

func TestOfflineSessionsFailEveryTransfer(t *testing.T) {
	t.Parallel()

	h := start(t)

	stream, err := h.client.UploadMedia(h.ctx)
	if err != nil {
		t.Fatalf("UploadMedia: %v", err)
	}

	_ = stream.Send(header("offline", whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE, ""))
	_, err = stream.CloseAndRecv()
	wantReason(t, err, codes.Unavailable, "NOT_CONNECTED", "")

	media, err := h.uploadChunks(h.ctx, bytes.NewReader([]byte("a")), 64<<10)
	if err != nil {
		t.Fatalf("UploadMedia: %v", err)
	}

	down, err := h.client.DownloadMedia(h.ctx, &whatsmeowv1.DownloadMediaRequest{SessionId: "offline", Media: media})
	if err == nil {
		_, err = down.Recv()
	}

	wantReason(t, err, codes.Unavailable, "NOT_CONNECTED", "")

	_, err = h.client.RequestMediaRetry(h.ctx, &whatsmeowv1.RequestMediaRetryRequest{SessionId: "offline", MessageId: "M1", Chat: "1@s.whatsapp.net", Media: media})
	wantReason(t, err, codes.Unavailable, "NOT_CONNECTED", "")
}

func TestAClientWithoutMediaIsInternal(t *testing.T) {
	t.Parallel()

	// The session's client is not a media client: a bug in the wiring, not the caller's.
	h := startWith(t, plain{}, true)

	_, err := h.client.RequestMediaRetry(h.ctx, &whatsmeowv1.RequestMediaRetryRequest{
		SessionId: "s1", MessageId: "M1", Chat: "1@s.whatsapp.net",
		Media: &whatsmeowv1.MediaRef{MediaType: whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE, DirectPath: "/x", MediaKey: random(32), FileSha256: random(32), FileEncSha256: random(32)},
	})
	wantReason(t, err, codes.Internal, "INTERNAL", "")
}

// TestTransfersOfHundredsOfMiBKeepTheHeapFlat sends 256 MiB up in chunks and back down in chunks, and samples the
// heap meanwhile: the slice holds a chunk at a time, never the file (ARCHITECTURE invariant 8).
//
// It is not parallel: the package's other tests would share the heap it samples. Go runs it before any parallel one.
func TestTransfersOfHundredsOfMiBKeepTheHeapFlat(t *testing.T) {
	h := start(t)

	const size = 256 << 20

	source := filepath.Join(t.TempDir(), "big.bin")
	writeRandomFile(t, source, size)

	runtime.GC()

	baseline := heapInUse()
	peak := baseline
	done := make(chan struct{})
	sampled := make(chan struct{})

	go func() {
		defer close(sampled)

		for {
			select {
			case <-done:
				return
			case <-time.After(5 * time.Millisecond):
				peak = max(peak, heapInUse())
			}
		}
	}()

	in, err := os.Open(source) //nolint:gosec // G304: the test's own file
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	defer func() { _ = in.Close() }()

	media, err := h.uploadChunks(h.ctx, in, 64<<10)
	if err != nil {
		t.Fatalf("UploadMedia: %v", err)
	}

	hash := sha256.New()
	if _, err := h.downloadChunks(h.ctx, media, hash); err != nil {
		t.Fatalf("DownloadMedia: %v", err)
	}

	close(done)
	<-sampled

	if !bytes.Equal(hash.Sum(nil), media.GetFileSha256()) {
		t.Fatal("the download differs from the upload")
	}

	// The test's own client, the stub's server and gRPC's windows share this heap; a file's worth would be 256 MiB.
	if growth := peak - baseline; growth > 64<<20 {
		t.Fatalf("the heap grew by %d MiB during a 256 MiB transfer", growth>>20)
	}

	t.Logf("heap growth during the 256 MiB transfer: %d MiB", (peak-baseline)>>20)
}

func writeRandomFile(t *testing.T, path string, size int) {
	t.Helper()

	f, err := os.Create(path) //nolint:gosec // G304: the test's own file
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	defer func() { _ = f.Close() }()

	block := random(1 << 20)
	for range size / len(block) {
		block[0]++

		if _, err := f.Write(block); err != nil {
			t.Fatalf("write: %v", err)
		}
	}
}

func heapInUse() uint64 {
	var stats runtime.MemStats

	runtime.ReadMemStats(&stats)

	return stats.HeapInuse
}
