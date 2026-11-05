//go:build fakewhatsapp

package fakewhatsapp

import (
	"bytes"
	"testing"

	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

func TestAnEncryptedRetryAnswerReachesTheHandlerUnchanged(t *testing.T) {
	t.Parallel()

	_, client, control, ctx := mediaOnline(t)
	info := &types.MessageInfo{ID: "M1", MessageSource: types.MessageSource{Chat: types.NewJID("111", types.HiddenUserServer)}}
	if err := client.SendMediaRetryReceipt(ctx, info, content(32)); err != nil {
		t.Fatalf("SendMediaRetryReceipt: %v", err)
	}

	answers := make(chan *events.MediaRetry, 1)
	client.AddEventHandlerWithSuccessStatus(func(evt any) bool {
		if retry, ok := evt.(*events.MediaRetry); ok {
			answers <- retry
		}

		return false
	})

	for _, encrypted := range []*fakev1.EncryptedMediaRetryAnswer{
		{Ciphertext: []byte{1, 2, 3}, Iv: []byte{4}},
		{Iv: make([]byte, 12)},
		{},
	} {
		if _, err := control.AnswerMediaRetry(ctx, &fakev1.AnswerMediaRetryRequest{
			SessionId: "s1", MessageId: "M1", Answer: &fakev1.AnswerMediaRetryRequest_Encrypted{Encrypted: encrypted},
		}); err != nil {
			t.Fatalf("AnswerMediaRetry: %v", err)
		}

		select {
		case answer := <-answers:
			if answer.MessageID != info.ID || answer.ChatID != info.Chat || answer.Error != nil ||
				!bytes.Equal(answer.Ciphertext, encrypted.GetCiphertext()) || !bytes.Equal(answer.IV, encrypted.GetIv()) {
				t.Fatalf("answer = %+v; want the supplied bytes %v for %v", answer, encrypted, info)
			}
		case <-ctx.Done():
			t.Fatal("the raw answer never reached the handler")
		}
	}
}
