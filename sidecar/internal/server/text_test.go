package server

import (
	"slices"
	"testing"

	"google.golang.org/protobuf/proto"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

func TestTextMakesAStringAProto3FieldCanCarry(t *testing.T) {
	t.Parallel()

	if _, err := proto.Marshal(&whatsmeowv1.MessageInfo{PushName: "Ana \xff"}); err == nil {
		t.Fatal("an invalid UTF-8 proto3 string marshalled; this test proves nothing")
	}

	if got := Text("Ana \xff\xfe ok"); got != "Ana � ok" {
		t.Fatalf("Text = %q", got)
	}

	if _, err := proto.Marshal(&whatsmeowv1.MessageInfo{PushName: Text("Ana \xff")}); err != nil {
		t.Fatalf("marshal: %v", err)
	}

	if got := Texts([]string{"a\xff", "b"}); !slices.Equal(got, []string{"a�", "b"}) {
		t.Fatalf("Texts = %q", got)
	}
}

// TestReadableJIDIsWhatTheClientParses: the client's Jid.TryParse (dotnet/src/Whatsmeow.Client/Jid.cs), on the forms
// whatsmeow's JID.String gives, the ones its ParseJID keeps when it fails (#122), and the client's own refusals.
func TestReadableJIDIsWhatTheClientParses(t *testing.T) {
	t.Parallel()

	for s, want := range map[string]bool{
		"5511900000001@s.whatsapp.net":     true,
		"111:2@lid":                        true,
		"5511900000001.1:2@s.whatsapp.net": true,
		"5511900000001.1@s.whatsapp.net":   true,
		"120363000000000000@g.us":          true,
		"status@broadcast":                 true,
		"s.whatsapp.net":                   true,
		// The empty JID's form, and a JID without a server.
		"":      false,
		"@":     false,
		"x@":    false,
		"a@b@c": false,
		// What ParseJID keeps when it fails, which JID.String writes back as is.
		"a.b.c@s.whatsapp.net":   false,
		"a:b:c@s.whatsapp.net":   false,
		"a:1.2:0@s.whatsapp.net": false,
		// The client's own refusals: an agent past a byte, a device past a ushort, a device that is not a number.
		"5511900000001.256@s.whatsapp.net":   false,
		"5511900000001:65536@s.whatsapp.net": false,
		"5511900000001:x@s.whatsapp.net":     false,
		"5511900000001.1:x@s.whatsapp.net":   false,
		"5511900000001.@s.whatsapp.net":      false,
		"5511900000001:-1@s.whatsapp.net":    false,
	} {
		if got := ReadableJID(s); got != want {
			t.Errorf("ReadableJID(%q) = %v, want %v", s, got, want)
		}
	}
}
