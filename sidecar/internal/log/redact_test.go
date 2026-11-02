package log

import (
	"bytes"
	"errors"
	"fmt"
	"net/url"
	"strings"
	"testing"

	"github.com/rs/zerolog"
)

func TestErrorCutsTheQueryOfTheURLItWraps(t *testing.T) {
	t.Parallel()

	cause := errors.New("connection reset")
	err := fmt.Errorf("failed to execute request: %w", &url.Error{Op: "Post", URL: "https://mmg.whatsapp.net/mms/image/T?auth=SECRET&token=T", Err: cause})

	got := Error(err)
	if got.Error() != `failed to execute request: Post "https://mmg.whatsapp.net/mms/image/T": connection reset` || !errors.Is(got, cause) {
		t.Fatalf("Error = %q (is the cause: %v)", got, errors.Is(got, cause))
	}

	plain := errors.New("no url")
	if got := Error(plain); got.Error() != plain.Error() || !errors.Is(got, plain) {
		t.Fatal("an error without a URL changed")
	}
}

func TestWhatsmeowsLogLinesHaveNoURLQuery(t *testing.T) {
	t.Parallel()

	var buf bytes.Buffer

	log := Whatsmeow(zerolog.New(&buf)).Sub("Client")
	log.Warnf("Failed to download media: %s, trying with next host...", `Get "https://mmg.whatsapp.net/v/t62/1.enc?ccb=1&oh=SECRET&oe=6&hash=h": EOF`)
	log.Debugf("plain %d", 1)

	got := buf.String()
	if strings.Contains(got, "SECRET") || !strings.Contains(got, `Get \"https://mmg.whatsapp.net/v/t62/1.enc\": EOF`) || !strings.Contains(got, `"sublogger":"Client"`) || !strings.Contains(got, "plain 1") {
		t.Fatalf("logged %s", got)
	}
}
