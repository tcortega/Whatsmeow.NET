package events

import (
	"testing"
)

func TestALiveMessageWithAnInvalidUTF8PushNameIsRecordedWithTheReplacementCharacter(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	m := messageEvent("M1")
	// ParseWebMessage and the node parser copy a push name unchecked (client.go:1039-1040); proto3 strings must be UTF-8.
	m.Info.PushName = "Ana \xff"

	h.mustDeliver("s1", m)

	event, err := h.open("s1", "c", 0).Recv()
	if err != nil {
		t.Fatalf("Recv: %v", err)
	}

	if got := event.GetMessage().GetInfo().GetPushName(); got != "Ana �" {
		t.Fatalf("push name %q, want %q", got, "Ana �")
	}
}
