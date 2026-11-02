package log

import (
	"bytes"
	"encoding/json"
	"io"
	"regexp"
	"strings"
	"testing"

	"github.com/rs/zerolog"
	signallogger "go.mau.fi/libsignal/logger"
)

// token is what a redacted number becomes.
var token = regexp.MustCompile(`~[0-9a-f]{8}`)

func TestTextRedactsEveryPhoneNumberAndJIDUserPart(t *testing.T) {
	t.Parallel()

	r := NewRedactor()

	for _, tc := range []struct {
		name, text string
		// gone must not survive; kept must.
		gone []string
		kept []string
	}{
		{"a phone number JID", "No LID found for 5511900000001@s.whatsapp.net", []string{"5511900000001"}, []string{"No LID found for ~", "@s.whatsapp.net"}},
		{"a device", "from 5511900000001:3@s.whatsapp.net", []string{"5511900000001"}, []string{":3@s.whatsapp.net"}},
		{"an agent and a device", "from 5511900000001.1:3@s.whatsapp.net", []string{"5511900000001"}, []string{".1:3@s.whatsapp.net"}},
		{"a LID", "Failed to save privacy token from 111111111111111@lid: locked", []string{"111111111111111"}, []string{"@lid: locked"}},
		{"a group", "Error parsing group 120363000000000000@g.us: bad", []string{"120363000000000000"}, []string{"@g.us"}},
		{"an old group with its creator's number", "group 5511900000001-1600000000@g.us", []string{"5511900000001", "1600000000"}, []string{"-~", "@g.us"}},
		{"a signal address", "no session for user 5511900000001:0", []string{"5511900000001"}, []string{"for user ~", ":0"}},
		{"a LID signal user", "from 5511900000001 to 111111111111111_1", []string{"5511900000001", "111111111111111"}, []string{"_1"}},
		{"an international number", `"+5511900000001" is not a JID`, []string{"5511900000001"}, []string{`"+~`, `" is not a JID`}},
		{"seven digits", "+6834002 typed", []string{"6834002"}, []string{"+~"}},
		{"in a node", `<message from="5511900000001@s.whatsapp.net" t="1730000000">`, []string{"5511900000001"}, []string{`from="~`}},
	} {
		got := r.Text(tc.text)
		for _, gone := range tc.gone {
			if strings.Contains(got, gone) {
				t.Errorf("%s: Text(%q) = %q, still has %q", tc.name, tc.text, got, gone)
			}
		}

		for _, kept := range append(tc.kept, "~") {
			if !strings.Contains(got, kept) {
				t.Errorf("%s: Text(%q) = %q, lost %q", tc.name, tc.text, got, kept)
			}
		}
	}
}

func TestTextKeepsWhatIsNotAPhoneNumber(t *testing.T) {
	t.Parallel()

	r := NewRedactor()

	for _, text := range []string{
		"Client outdated (405) connect failure (client version: 2.3000.1027954720)",
		"Unavailable message 3EB0123456789ABCDEF0 from status@broadcast",
		"Uploading 812 new prekeys to server",
		"Node handling took 1.234567891s at 2026-11-02T16:08:08-03:00",
		"dial tcp 157.240.12.35:443: refused",
		"Got 515 code from s.whatsapp.net, reconnecting",
		"user 123456 is six digits",
	} {
		if got := r.Text(text); got != text {
			t.Errorf("Text(%q) = %q, want it unchanged", text, got)
		}
	}
}

func TestTextGivesAUserOneTokenInEveryForm(t *testing.T) {
	t.Parallel()

	r := NewRedactor()
	got := r.Text("5511900000001@s.whatsapp.net 5511900000001:3@s.whatsapp.net 5511900000001 +5511900000001 5511900000002")

	tokens := token.FindAllString(got, -1)
	if len(tokens) != 5 || tokens[0] != tokens[1] || tokens[0] != tokens[2] || tokens[0] != tokens[3] || tokens[0] == tokens[4] {
		t.Fatalf("Text = %q, want the first four with one token and the last with another", got)
	}

	if other := NewRedactor().Text("5511900000001"); other == tokens[0] {
		t.Fatalf("two redactors gave %q the same token %q: the key is not per process", "5511900000001", other)
	}
}

func TestTextIsTheSameRedactedTwice(t *testing.T) {
	t.Parallel()

	r := NewRedactor()

	once := r.Text("from 5511900000001:3@s.whatsapp.net in 120363000000000000@g.us")
	if twice := r.Text(once); twice != once {
		t.Fatalf("Text(Text) = %q, want %q", twice, once)
	}

	// A token can be all digits; toStatus's error line goes through the writer too.
	if got := r.Text("from ~12345678:3@s.whatsapp.net"); got != "from ~12345678:3@s.whatsapp.net" {
		t.Fatalf("Text redacted a token: %q", got)
	}
}

func TestTextCutsTheQueryOfEveryURL(t *testing.T) {
	t.Parallel()

	got := NewRedactor().Text(`failed to upload: Post "https://mmg.whatsapp.net/mms/image/T?auth=SECRET&token=T": EOF`)
	if got != `failed to upload: Post "https://mmg.whatsapp.net/mms/image/T": EOF` {
		t.Fatalf("Text = %q", got)
	}
}

// line logs one line through the writer and returns it, checked to be JSON.
func line(t *testing.T, write func(zerolog.Logger)) string {
	t.Helper()

	var buf bytes.Buffer

	write(zerolog.New(Writer(&buf, NewRedactor())).Level(zerolog.DebugLevel).With().Timestamp().Logger())

	got := buf.String()
	if !json.Valid(buf.Bytes()) {
		t.Fatalf("not one JSON line: %s", got)
	}

	return got
}

func TestTheWriterRedactsEveryLineAtInfoAndAbove(t *testing.T) {
	t.Parallel()

	for _, level := range []zerolog.Level{zerolog.InfoLevel, zerolog.WarnLevel, zerolog.ErrorLevel} {
		got := line(t, func(log zerolog.Logger) {
			log.WithLevel(level).Str("session_id", "s1").AnErr("error", errStr("no session for user 5511900000001:0")).
				Msgf("Error decrypting message 3EB0 from %s", "5511900000001:3@s.whatsapp.net")
		})
		if strings.Contains(got, "5511900000001") || !token.MatchString(got) || !strings.Contains(got, `"session_id":"s1"`) {
			t.Errorf("%v: logged %s", level, got)
		}
	}
}

func TestTheWriterKeepsADebugLine(t *testing.T) {
	t.Parallel()

	got := line(t, func(log zerolog.Logger) { log.Debug().Msg("sending to 5511900000001@s.whatsapp.net") })
	if !strings.Contains(got, "sending to 5511900000001@s.whatsapp.net") {
		t.Fatalf("logged %s", got)
	}
}

func TestTheWriterRedactsOnlyInsideStrings(t *testing.T) {
	t.Parallel()

	got := line(t, func(log zerolog.Logger) {
		// A process id can have seven digits (Linux pid_max 4194304); a number field stays JSON.
		log.Info().Int("parent_pid", 4194304).Uint64("seq", 12345678901).Msg("stopping")
	})
	if !strings.Contains(got, `"parent_pid":4194304`) || !strings.Contains(got, `"seq":12345678901`) || !strings.Contains(got, `"time":"`) {
		t.Fatalf("logged %s", got)
	}
}

func TestTheWriterRedactsAfterAnEscape(t *testing.T) {
	t.Parallel()

	// whatsmeow logs a panic's stack and a node's children on their own lines (message.go:320, client.go:1005).
	got := line(t, func(log zerolog.Logger) {
		log.Warn().Msg("a\n5511900000001\t5511900000002\"5511900000003\\5511900000004 é5511900000005")
	})
	if strings.Contains(got, "551190000000") || len(token.FindAllString(got, -1)) != 5 {
		t.Fatalf("logged %s", got)
	}
}

func TestWhatsmeowsAndLibsignalsLinesAreRedactedThroughTheWriter(t *testing.T) {
	var buf bytes.Buffer

	log := zerolog.New(Writer(&buf, NewRedactor())).Level(zerolog.InfoLevel)

	SetupSignal(log)
	signallogger.Warning("Unable to decrypt for 5511900000001:0")
	Whatsmeow(log).Sub("Client").Warnf("No LID found for %s", "5511900000001@s.whatsapp.net")

	if got := buf.String(); strings.Contains(got, "5511900000001") || len(token.FindAllString(got, -1)) != 2 {
		t.Fatalf("logged %s", got)
	}
}

// errStr is an error with a fixed text.
type errStr string

func (e errStr) Error() string { return string(e) }

// BenchmarkWriter is the cost the writer adds to a line: the scan, and a hash per number.
func BenchmarkWriter(b *testing.B) {
	w := Writer(io.Discard, NewRedactor())

	for name, msg := range map[string]string{
		"no number": "Failed to send keepalive: websocket: close sent",
		"three":     "Error decrypting message 3EB0A1B2C3D4E5F6 from 5511900000001:3@s.whatsapp.net in 120363000000000000@g.us: no session for user 5511900000001:3",
		"node":      `Node handling took 6s for <message from=\"120363000000000000@g.us\" id=\"3EB0A1B2C3D4E5F6\" participant=\"111111111111111@lid\" t=\"1730000000\">` + strings.Repeat("ab", 900) + "</message>",
	} {
		line := []byte(`{"level":"warn","session_id":"s1","component":"whatsmeow","sublogger":"Client","time":"2026-11-02T16:08:08-03:00","message":"` + msg + "\"}\n")

		b.Run(name, func(b *testing.B) {
			b.ReportAllocs()

			for b.Loop() {
				_, _ = w.WriteLevel(zerolog.WarnLevel, line)
			}
		})
	}
}
