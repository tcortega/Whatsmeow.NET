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
