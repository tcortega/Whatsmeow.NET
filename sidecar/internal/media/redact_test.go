package media

import (
	"bytes"
	"errors"
	"fmt"
	"net/url"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"google.golang.org/grpc/status"
)

// TestATransferErrorNeverCarriesTheMediaURLsQuery: whatsmeow's request errors are *url.Errors that print the whole
// URL (upload.go:281-288, download.go:362-378), and its query is a credential: the media connection's auth on an
// upload, the CDN's signature on a download. Neither the log nor the status keeps it; the host and path stay.
func TestATransferErrorNeverCarriesTheMediaURLsQuery(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.svc.backoff = []time.Duration{time.Millisecond, time.Millisecond}

	media, err := h.uploadChunks(h.ctx, bytes.NewReader([]byte("a")), 64<<10)
	if err != nil {
		t.Fatalf("UploadMedia: %v", err)
	}

	h.stub.mu.Lock()
	h.stub.uploadErr = fmt.Errorf("failed to execute request: %w", &url.Error{
		Op: "Post", URL: "https://mmg.whatsapp.net/mms/document/TOKEN?auth=SECRET-AUTH&token=TOKEN", Err: errors.New("connection reset by peer"),
	})
	h.stub.downloadErr = fmt.Errorf("failed to download media from last host: %w", &url.Error{
		Op: "Get", URL: "https://mmg.whatsapp.net/v/t62.7118-24/1_2_3.enc?ccb=11-4&oh=SECRET-OH&oe=68&hash=abc&mms-type=image&__wa-mms=", Err: errors.New("connection reset by peer"),
	})
	h.stub.mu.Unlock()

	_, upload := h.uploadChunks(h.ctx, bytes.NewReader([]byte("a")), 64<<10)
	_, chunks := h.downloadChunks(h.ctx, media, &bytes.Buffer{})
	path := h.downloadPath(h.ctx, media, filepath.Join(h.root, "x.bin"))

	for name, c := range map[string]struct {
		err  error
		keep string
	}{
		"upload":             {upload, "https://mmg.whatsapp.net/mms/document/TOKEN"},
		"download in chunks": {chunks, "https://mmg.whatsapp.net/v/t62.7118-24/1_2_3.enc"},
		"download to a path": {path, "https://mmg.whatsapp.net/v/t62.7118-24/1_2_3.enc"},
	} {
		message := status.Convert(c.err).Message()
		if !strings.Contains(message, c.keep) || strings.Contains(message, "SECRET") {
			t.Errorf("%s: the status says %q; want %q and no query", name, message, c.keep)
		}
	}

	if logs := h.logs.String(); !strings.Contains(logs, "mmg.whatsapp.net") || strings.Contains(logs, "SECRET") {
		t.Errorf("the log has the query, or no failed request at all:\n%s", logs)
	}
}
