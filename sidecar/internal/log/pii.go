package log

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"

	"github.com/rs/zerolog"
)

// minDigits is the shortest run of digits redacted: whatsmeow refuses a phone number of six digits or fewer
// (pair-code.go:98), and a JID's user part, a LID or a group's id, is longer.
const minDigits = 7

// Redactor replaces every run of seven or more digits, a phone number or the user part of a JID, with a token: "~"
// and 8 hex digits of a SHA-256 of a per-process random key and the digits (ARCHITECTURE §8.8, ADR 0039). The rest
// stays, so "5511900000001:3@s.whatsapp.net" becomes "~1a2b3c4d:3@s.whatsapp.net", and one user has one token in
// every form, JID, signal address or "+" number, for the life of the process. A run that touches a letter or a digit,
// or follows a "." or a "~", is not a number on its own (an id, a version, a token) and stays.
type Redactor struct {
	key [32]byte
}

// NewRedactor returns a redactor with a new random key.
func NewRedactor() *Redactor {
	r := &Redactor{}
	_, _ = rand.Read(r.key[:]) // crypto/rand.Read never returns an error (go doc crypto/rand.Read)

	return r
}

// Text is s without a phone number, a JID's user part, or a URL's query (URL), for a gRPC status message.
func (r *Redactor) Text(s string) string {
	return string(r.redact([]byte(urls.ReplaceAllStringFunc(s, URL)), false))
}

// Writer is w with every line of info level and above redacted, inside its JSON strings only, so a line stays JSON
// and a number field stays a number. A debug line is written as it is: ARCHITECTURE §8.8 allows a JID at debug, and
// the sidecar logs none (newLogger).
func Writer(w io.Writer, r *Redactor) zerolog.LevelWriter { return &writer{w: w, r: r} }

type writer struct {
	w io.Writer
	r *Redactor
}

// Write is a line without a level, redacted.
func (w *writer) Write(p []byte) (int, error) { return w.WriteLevel(zerolog.NoLevel, p) }

func (w *writer) WriteLevel(level zerolog.Level, p []byte) (int, error) {
	line := p
	if level > zerolog.DebugLevel {
		line = w.r.redact(p, true)
	}

	if _, err := w.w.Write(line); err != nil {
		return 0, fmt.Errorf("write a log line: %w", err)
	}

	return len(p), nil
}

// redact is p with each run of digits redacted, in JSON strings only when inJSON; p itself when there is none. In
// JSON an escape sequence ("\n", "é") separates, as the character it stands for would.
func (r *Redactor) redact(p []byte, inJSON bool) []byte {
	var out []byte

	last := 0
	inString := !inJSON
	// prev is the byte before i as the run's guard sees it: an escape sequence counts as a space.
	prev := byte(' ')

	for i := 0; i < len(p); {
		c := p[i]

		switch {
		case inJSON && c == '"':
			inString = !inString
			prev = c
			i++

			continue
		case !inString:
			prev = c
			i++

			continue
		case inJSON && c == '\\':
			i += escapeLen(p[i:])
			prev = ' '

			continue
		case !isDigit(c):
			prev = c
			i++

			continue
		}

		j := i
		for j < len(p) && isDigit(p[j]) {
			j++
		}

		if j-i >= minDigits && !isWord(prev) && prev != '.' && prev != '~' && (j == len(p) || !isWord(p[j])) {
			if out == nil {
				out = make([]byte, 0, len(p))
			}

			out = append(out, p[last:i]...)
			out = r.appendToken(out, p[i:j])
			last = j
		}

		prev = p[j-1]
		i = j
	}

	if out == nil {
		return p
	}

	return append(out, p[last:]...)
}

// appendToken appends the token of digits to out.
func (r *Redactor) appendToken(out, digits []byte) []byte {
	h := sha256.New()
	h.Write(r.key[:])
	h.Write(digits)

	var sum [sha256.Size]byte

	out = append(out, '~')

	return hex.AppendEncode(out, h.Sum(sum[:0])[:4])
}

// escapeLen is the length of the JSON escape sequence p starts with: \uXXXX or a backslash and one character.
func escapeLen(p []byte) int {
	if len(p) >= 6 && p[1] == 'u' {
		return 6
	}

	return min(2, len(p))
}

func isDigit(c byte) bool { return c >= '0' && c <= '9' }

func isWord(c byte) bool { return isDigit(c) || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') }
