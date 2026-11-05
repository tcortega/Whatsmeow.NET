package server

import (
	"encoding/json"
	"os"
	"path/filepath"
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

// TestReadableJIDIsWhatTheClientParses: one table, testdata/jids.json, which the client's own test reads too
// (JidParseTests.TryParse_AgreesWithTheSidecarsTable, over Jid.TryParse): the forms whatsmeow's JID.String gives, the
// ones its ParseJID keeps when it fails (#122), and the client's own rules, quirks included.
func TestReadableJIDIsWhatTheClientParses(t *testing.T) {
	t.Parallel()

	raw, err := os.ReadFile(filepath.Join("testdata", "jids.json"))
	if err != nil {
		t.Fatalf("read the table: %v", err)
	}

	var table []struct {
		JID      string `json:"jid"`
		Readable bool   `json:"readable"`
	}
	if err := json.Unmarshal(raw, &table); err != nil || len(table) == 0 {
		t.Fatalf("parse the table: %d rows, %v", len(table), err)
	}

	for _, row := range table {
		if got := ReadableJID(row.JID); got != row.Readable {
			t.Errorf("ReadableJID(%q) = %v, want %v", row.JID, got, row.Readable)
		}
	}
}
