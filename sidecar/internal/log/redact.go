package log

import (
	"errors"
	"fmt"
	"net/url"
	"regexp"
	"strings"

	"github.com/rs/zerolog"
	waLog "go.mau.fi/whatsmeow/util/log"
)

// URL is raw without its query and fragment: whatsmeow's media URLs carry a credential there, the media connection's
// auth on an upload (upload.go:251-273) and the CDN's signature on a download, whose direct path is joined to more
// query with "&" (download-to-file.go:101). Scheme, host and path stay, for the log.
func URL(raw string) string {
	if i := strings.IndexAny(raw, "?&#"); i >= 0 {
		return raw[:i]
	}

	return raw
}

// Error is err with the query cut from the URL of the *url.Error it wraps, if any: that error prints its whole URL,
// and whatsmeow's wrappers already hold that text. The cut error still unwraps to err.
func Error(err error) error {
	var urlErr *url.Error
	if !errors.As(err, &urlErr) {
		return err
	}

	cut := URL(urlErr.URL)
	if cut == urlErr.URL {
		return err
	}

	return &redacted{text: strings.ReplaceAll(err.Error(), urlErr.URL, cut), err: err}
}

// redacted is an error whose text no longer has a URL's query.
type redacted struct {
	text string
	err  error
}

func (r *redacted) Error() string { return r.text }

func (r *redacted) Unwrap() error { return r.err }

// urls matches the URLs in a log line, up to a quote, space or bracket.
var urls = regexp.MustCompile(`https?://[^\s"'<>]+`)

// Whatsmeow is whatsmeow's logger over log, with the query cut from every URL in a line: whatsmeow logs a failed
// media download with its URL (download-to-file.go:117, 180).
func Whatsmeow(log zerolog.Logger) waLog.Logger { return &whatsmeowLog{log: log} }

type whatsmeowLog struct {
	log zerolog.Logger
	mod string
}

func (w *whatsmeowLog) Warnf(msg string, args ...any)  { w.write(w.log.Warn(), msg, args) }
func (w *whatsmeowLog) Errorf(msg string, args ...any) { w.write(w.log.Error(), msg, args) }
func (w *whatsmeowLog) Infof(msg string, args ...any)  { w.write(w.log.Info(), msg, args) }
func (w *whatsmeowLog) Debugf(msg string, args ...any) { w.write(w.log.Debug(), msg, args) }

// Sub is the sublogger whatsmeow names module, as waLog.Zerolog makes it (util/log/zerolog.go:31-36).
func (w *whatsmeowLog) Sub(module string) waLog.Logger {
	if w.mod != "" {
		module = w.mod + "/" + module
	}

	return &whatsmeowLog{log: w.log.With().Str("sublogger", module).Logger(), mod: module}
}

func (*whatsmeowLog) write(e *zerolog.Event, msg string, args []any) {
	if !e.Enabled() {
		return
	}

	e.Msg(urls.ReplaceAllStringFunc(fmt.Sprintf(msg, args...), URL))
}
