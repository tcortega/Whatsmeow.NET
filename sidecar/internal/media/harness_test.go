package media

import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"path/filepath"
	"runtime"
	"sync"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/types"
	"google.golang.org/genproto/googleapis/rpc/errdetails"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"
	"google.golang.org/grpc/test/bufconn"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	sidecarlog "github.com/tcortega/Whatsmeow.NET/sidecar/internal/log"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/session"
	sidecarstore "github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

const token = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"

// stub stands in for whatsmeow's media side behind the slice's interface. Its "encryption" is a XOR, so what it
// writes to the temporary file is never the plaintext; its server keeps each upload's ciphertext in a file, so a
// transfer of hundreds of MiB holds none of it in memory.
type stub struct {
	session.Client // never called: the slice uses only the media methods

	dir string

	mu sync.Mutex
	// keys is each uploaded path's media key: a download with another key fails the MAC check, as whatsmeow's does.
	keys      map[string][]byte
	attempts  int
	failNext  int
	expired   map[string]error
	corrupt   map[string]bool
	badSHA    map[string]bool
	held      chan struct{} // closed once a held download wrote part of the file
	hold      bool
	temps     []string // the temporary files UploadReader got
	receipts  []receipt
	onReceipt func()
	// uploadErr and downloadErr, when set, are what every upload attempt and download returns.
	uploadErr, downloadErr error
}

type receipt struct {
	info types.MessageInfo
	key  []byte
}

func newStub(t *testing.T) *stub {
	t.Helper()

	return &stub{dir: t.TempDir(), keys: map[string][]byte{}, expired: map[string]error{}, corrupt: map[string]bool{}, badSHA: map[string]bool{}}
}

const xor = 0x5a

// xorWriter writes p XORed, so the ciphertext never equals the plaintext.
type xorWriter struct{ w io.Writer }

func (x xorWriter) Write(p []byte) (int, error) {
	buf := make([]byte, len(p))
	for i, b := range p {
		buf[i] = b ^ xor
	}

	return x.w.Write(buf) //nolint:wrapcheck // the stub's own writer
}

func (s *stub) UploadReader(_ context.Context, plaintext io.Reader, tempFile io.ReadWriteSeeker, appInfo whatsmeow.MediaType) (whatsmeow.UploadResponse, error) {
	if f, ok := tempFile.(*os.File); ok {
		s.mu.Lock()
		s.temps = append(s.temps, f.Name())
		s.mu.Unlock()
	}

	plain, enc := sha256.New(), sha256.New()

	n, err := io.Copy(io.MultiWriter(plain, xorWriter{io.MultiWriter(tempFile, enc)}), plaintext)
	if err != nil {
		return whatsmeow.UploadResponse{}, fmt.Errorf("failed to encrypt file: %w", err)
	}

	s.mu.Lock()
	s.attempts++
	fail := s.failNext > 0
	if fail {
		s.failNext--
	}
	uploadErr := s.uploadErr
	s.mu.Unlock()

	if uploadErr != nil {
		return whatsmeow.UploadResponse{}, uploadErr
	}

	if fail {
		return whatsmeow.UploadResponse{}, fmt.Errorf("failed to execute request: %w", &net.OpError{Op: "dial", Net: "tcp", Err: errors.New("stub: down")})
	}

	if _, err := tempFile.Seek(0, io.SeekStart); err != nil {
		return whatsmeow.UploadResponse{}, fmt.Errorf("seek: %w", err)
	}

	id := make([]byte, 8)
	_, _ = rand.Read(id)
	path := "/stub/" + hex.EncodeToString(id)

	blob, err := os.Create(filepath.Join(s.dir, hex.EncodeToString(id)))
	if err != nil {
		return whatsmeow.UploadResponse{}, fmt.Errorf("store: %w", err)
	}
	defer func() { _ = blob.Close() }()

	if _, err := io.Copy(blob, tempFile); err != nil {
		return whatsmeow.UploadResponse{}, fmt.Errorf("store: %w", err)
	}

	key := make([]byte, 32)
	_, _ = rand.Read(key)

	s.mu.Lock()
	s.keys[path] = key
	s.mu.Unlock()

	resp := whatsmeow.UploadResponse{
		URL: "https://stub" + path, DirectPath: path, MediaKey: key, FileSHA256: plain.Sum(nil), FileEncSHA256: enc.Sum(nil), FileLength: uint64(n), //nolint:gosec // G115: a length
	}
	if appInfo == whatsmeow.MediaVideo {
		resp.StreamingSidecar = []byte("sidecar")
	}

	return resp, nil
}

func (s *stub) DownloadMediaWithPathToFile(ctx context.Context, directPath string, encFileHash, fileHash, mediaKey []byte, _ whatsmeow.MediaType,
	_ string, _ bool, file whatsmeow.File,
) error {
	s.mu.Lock()
	key, expired, corrupt, badSHA, hold, held, downloadErr := s.keys[directPath], s.expired[directPath], s.corrupt[directPath], s.badSHA[directPath], s.hold, s.held, s.downloadErr
	s.hold = false
	s.mu.Unlock()

	if expired != nil {
		return expired
	}

	if downloadErr != nil {
		return downloadErr
	}

	blob, err := os.Open(filepath.Join(s.dir, filepath.Base(directPath)))
	if err != nil {
		return whatsmeow.ErrMediaDownloadFailedWith404
	}
	defer func() { _ = blob.Close() }()

	if hold {
		_, _ = io.CopyN(file, blob, 10)
		close(held)
		<-ctx.Done()

		return ctx.Err() //nolint:wrapcheck // as whatsmeow returns it
	}

	enc := sha256.New()
	if _, err := io.Copy(io.MultiWriter(file, enc), blob); err != nil {
		return fmt.Errorf("download: %w", err)
	}

	// As whatsmeow leaves it after a failed check: the ciphertext in the file.
	if corrupt || !bytes.Equal(key, mediaKey) {
		return whatsmeow.ErrInvalidMediaHMAC
	}

	if !bytes.Equal(enc.Sum(nil), encFileHash) {
		return whatsmeow.ErrInvalidMediaEncSHA256
	}

	plain := sha256.New()
	if err := decryptInPlace(file, plain); err != nil {
		return err
	}

	if badSHA || !bytes.Equal(plain.Sum(nil), fileHash) {
		return whatsmeow.ErrInvalidMediaSHA256
	}

	return nil
}

// decryptInPlace XORs the file back to plaintext, as whatsmeow decrypts it in place (download-to-file.go:151).
func decryptInPlace(file whatsmeow.File, plain io.Writer) error {
	buf := make([]byte, 32<<10)

	for offset := int64(0); ; {
		n, err := file.ReadAt(buf, offset)
		for i := range buf[:n] {
			buf[i] ^= xor
		}

		_, _ = plain.Write(buf[:n])

		if _, werr := file.WriteAt(buf[:n], offset); werr != nil {
			return fmt.Errorf("decrypt: %w", werr)
		}

		offset += int64(n)

		if errors.Is(err, io.EOF) {
			return nil
		}

		if err != nil {
			return fmt.Errorf("decrypt: %w", err)
		}
	}
}

func (s *stub) SendMediaRetryReceipt(_ context.Context, message *types.MessageInfo, mediaKey []byte) error {
	s.mu.Lock()
	on := s.onReceipt
	s.receipts = append(s.receipts, receipt{info: *message, key: mediaKey})
	s.mu.Unlock()

	if on != nil {
		on()
	}

	return nil
}

// sessions is the Sessions slice for the tests: every session is online with the one stub, unless offline names it.
type sessions struct {
	client  session.Client
	offline map[string]error
}

func (s sessions) Online(id string) (session.Client, <-chan struct{}, error) {
	if err := s.offline[id]; err != nil {
		return nil, nil, err
	}

	return s.client, make(chan struct{}), nil
}

// harness is the slice on a real store, served by the real server over an in-memory listener.
type harness struct {
	t      *testing.T
	root   string
	temp   string
	dbFile string
	db     *sidecarstore.Store
	stub   *stub
	svc    *Service
	// logs is everything the slice and the server logged.
	logs   *lockedBuffer
	client whatsmeowv1.MediaServiceClient
	ctx    context.Context //nolint:containedctx // the test's call context
}

func start(t *testing.T) *harness {
	t.Helper()

	return startWith(t, newStub(t), true)
}

// startWith starts the slice with client as every session's client; withRoot configures a media root.
func startWith(t *testing.T, client session.Client, withRoot bool) *harness {
	t.Helper()

	dir := t.TempDir()
	dbFile := filepath.Join(dir, "whatsmeow-net.db")

	db, err := sidecarstore.Open(context.Background(), dbFile, zerolog.Nop())
	if err != nil {
		t.Fatalf("open the store: %v", err)
	}

	t.Cleanup(func() { _ = db.Close() })

	if _, err := db.DB.ExecContext(context.Background(), "INSERT INTO session (id) VALUES ('s1')"); err != nil {
		t.Fatalf("create the session: %v", err)
	}

	h := &harness{t: t, temp: filepath.Join(dir, "whatsmeow-net.db.tmp"), dbFile: dbFile, db: db, logs: &lockedBuffer{}}
	log := zerolog.New(h.logs).Level(zerolog.DebugLevel)
	h.stub, _ = client.(*stub)

	root := ""
	if withRoot {
		h.root = filepath.Join(dir, "media")
		if err := os.Mkdir(h.root, 0o700); err != nil {
			t.Fatalf("the media root: %v", err)
		}

		root = h.root
	}

	h.svc, err = New(sessions{client: client, offline: map[string]error{"offline": fmt.Errorf("offline: %w", server.ErrNotConnected)}}, db.DB, root, h.temp, log)
	if err != nil {
		t.Fatalf("New: %v", err)
	}

	t.Cleanup(func() { _ = h.svc.Close() })

	srv := server.New(token, log, sidecarlog.NewRedactor())
	whatsmeowv1.RegisterMediaServiceServer(srv, h.svc)

	lis := bufconn.Listen(1 << 20)
	served := make(chan error, 1)

	go func() { served <- srv.Serve(lis) }()

	conn, err := grpc.NewClient("passthrough:///bufnet",
		grpc.WithContextDialer(func(ctx context.Context, _ string) (net.Conn, error) { return lis.DialContext(ctx) }),
		grpc.WithTransportCredentials(insecure.NewCredentials()),
		grpc.WithDefaultCallOptions(grpc.MaxCallRecvMsgSize(16<<20), grpc.MaxCallSendMsgSize(16<<20)))
	if err != nil {
		t.Fatalf("dial: %v", err)
	}

	t.Cleanup(func() {
		_ = conn.Close()

		srv.Shutdown()
		srv.Stop()

		if err := <-served; err != nil {
			t.Errorf("serve: %v", err)
		}
	})

	h.client = whatsmeowv1.NewMediaServiceClient(conn)

	// A bound for a broken slice, never a wait: every wait below ends on a condition.
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	t.Cleanup(cancel)

	h.ctx = metadata.AppendToOutgoingContext(ctx, "authorization", "Bearer "+token)

	return h
}

// file writes content to name under dir and returns its path.
func file(t *testing.T, dir, name string, content []byte) string {
	t.Helper()

	path := filepath.Join(dir, name)
	if err := os.WriteFile(path, content, 0o600); err != nil {
		t.Fatalf("write %s: %v", path, err)
	}

	return path
}

// random is n random bytes.
func random(n int) []byte {
	b := make([]byte, n)
	_, _ = rand.Read(b)

	return b
}

// uploadPath uploads path by handoff.
func (h *harness) uploadPath(ctx context.Context, path string, mediaType whatsmeowv1.MediaType) (*whatsmeowv1.MediaRef, error) {
	stream, err := h.client.UploadMedia(ctx)
	if err != nil {
		return nil, err //nolint:wrapcheck // the test reads the status
	}

	if err := stream.Send(header("s1", mediaType, path)); err != nil {
		return nil, err //nolint:wrapcheck // the test reads the status
	}

	resp, err := stream.CloseAndRecv()

	return resp.GetMedia(), err
}

// uploadChunks uploads content in chunks of size.
func (h *harness) uploadChunks(ctx context.Context, content io.Reader, size int) (*whatsmeowv1.MediaRef, error) {
	stream, err := h.client.UploadMedia(ctx)
	if err != nil {
		return nil, err //nolint:wrapcheck // the test reads the status
	}

	if err := stream.Send(header("s1", whatsmeowv1.MediaType_MEDIA_TYPE_DOCUMENT, "")); err != nil {
		return nil, err //nolint:wrapcheck // the test reads the status
	}

	for {
		buf := make([]byte, size)

		n, err := io.ReadFull(content, buf)
		if n > 0 {
			if serr := stream.Send(&whatsmeowv1.UploadMediaRequest{Part: &whatsmeowv1.UploadMediaRequest_Chunk{Chunk: buf[:n]}}); serr != nil {
				break // the server ended the call; CloseAndRecv has its status
			}
		}

		if err != nil {
			break
		}
	}

	resp, err := stream.CloseAndRecv()

	return resp.GetMedia(), err
}

func header(sessionID string, mediaType whatsmeowv1.MediaType, path string) *whatsmeowv1.UploadMediaRequest {
	return &whatsmeowv1.UploadMediaRequest{Part: &whatsmeowv1.UploadMediaRequest_Header_{
		Header: &whatsmeowv1.UploadMediaRequest_Header{SessionId: sessionID, MediaType: mediaType, Path: path},
	}}
}

// downloadChunks downloads media into w, and returns the number of chunks.
func (h *harness) downloadChunks(ctx context.Context, media *whatsmeowv1.MediaRef, w io.Writer) (int, error) {
	stream, err := h.client.DownloadMedia(ctx, &whatsmeowv1.DownloadMediaRequest{SessionId: "s1", Media: media})
	if err != nil {
		return 0, err //nolint:wrapcheck // the test reads the status
	}

	chunks := 0

	for {
		resp, err := stream.Recv()
		if errors.Is(err, io.EOF) {
			return chunks, nil
		}

		if err != nil {
			return chunks, err //nolint:wrapcheck // the test reads the status
		}

		if len(resp.GetChunk()) > 64<<10 {
			h.t.Errorf("a chunk of %d bytes, want at most 65536", len(resp.GetChunk()))
		}

		chunks++

		if _, err := w.Write(resp.GetChunk()); err != nil {
			return chunks, fmt.Errorf("write: %w", err)
		}
	}
}

// downloadPath downloads media to path by handoff.
func (h *harness) downloadPath(ctx context.Context, media *whatsmeowv1.MediaRef, path string) error {
	stream, err := h.client.DownloadMedia(ctx, &whatsmeowv1.DownloadMediaRequest{SessionId: "s1", Media: media, Path: path})
	if err != nil {
		return err //nolint:wrapcheck // the test reads the status
	}

	resp, err := stream.Recv()
	if errors.Is(err, io.EOF) {
		return nil
	}

	if err == nil {
		h.t.Fatalf("a download by path sent %v", resp)
	}

	return err //nolint:wrapcheck // the test reads the status
}

// wantReason fails unless err is a status of code with one ErrorInfo of reason, and of field when it is not "".
func wantReason(t *testing.T, err error, code codes.Code, reason, field string) {
	t.Helper()

	st, _ := status.FromError(err)
	if st.Code() != code || len(st.Details()) != 1 {
		t.Fatalf("error = %v, want %v with one ErrorInfo", err, code)
	}

	info, _ := st.Details()[0].(*errdetails.ErrorInfo)
	if info.GetReason() != reason || (field != "" && info.GetMetadata()["field"] != field) {
		t.Fatalf("ErrorInfo = %v, want %s with field %q", info, reason, field)
	}
}

// entries is the names in dir.
func entries(t *testing.T, dir string) []string {
	t.Helper()

	list, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("read %s: %v", dir, err)
	}

	names := make([]string, 0, len(list))
	for _, e := range list {
		names = append(names, e.Name())
	}

	return names
}

// plain is a session client that cannot transfer media.
type plain struct{ session.Client }

// count is the number of upload attempts.
func (s *stub) count() int {
	s.mu.Lock()
	defer s.mu.Unlock()

	return s.attempts
}

// eventuallyEmpty waits until every dir is empty: a handler cleans up as it returns, which can be after its client saw
// the call end. Only the test's bound ends the wait.
func (h *harness) eventuallyEmpty(dirs ...string) {
	h.t.Helper()

	for {
		left := 0
		for _, dir := range dirs {
			left += len(entries(h.t, dir))
		}

		if left == 0 {
			return
		}

		if h.ctx.Err() != nil {
			h.t.Fatalf("files are left in %v", dirs)
		}

		runtime.Gosched()
	}
}

// lockedBuffer is a log destination the slice and the server write from their own goroutines.
type lockedBuffer struct {
	mu  sync.Mutex
	buf bytes.Buffer
}

func (b *lockedBuffer) Write(p []byte) (int, error) {
	b.mu.Lock()
	defer b.mu.Unlock()

	return b.buf.Write(p) //nolint:wrapcheck // a bytes.Buffer
}

func (b *lockedBuffer) String() string {
	b.mu.Lock()
	defer b.mu.Unlock()

	return b.buf.String()
}
