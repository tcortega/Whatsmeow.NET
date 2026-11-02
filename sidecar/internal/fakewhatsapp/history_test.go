//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"errors"
	"slices"
	"testing"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/proto/waHistorySync"
	wastore "go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

// historyRequest is a blob of one direct chat with a message from the other user and one from the account.
func historyRequest(inline bool) *fakev1.DeliverHistorySyncRequest {
	return &fakev1.DeliverHistorySyncRequest{
		SessionId: "s1", MessageId: "N1", SyncType: int32(waHistorySync.HistorySync_RECENT), ChunkOrder: 2, Progress: 40, Inline: inline,
		Conversations: []*fakev1.DeliverHistorySyncRequest_Conversation{{
			Chat: "5511987654321@s.whatsapp.net",
			Messages: []*fakev1.DeliverHistorySyncRequest_Message{
				{Id: "H1", Text: "old", MessageTime: timestamppb.New(timestamppb.Now().AsTime().Truncate(1e9)), PushName: "Ana"},
				{Id: "H2", FromMe: true, Text: "older"},
			},
		}},
	}
}

func TestDeliverHistorySyncSendsTheNotificationAndTheClientDownloadsAndDeletesTheBlob(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx, client, got, _ := linked(t)
	whatsApp.mediaDir = t.TempDir()
	<-got // PairSuccess

	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("connect: %v", err)
	}

	resp, err := control.DeliverHistorySync(ctx, historyRequest(false))
	if err != nil {
		t.Fatalf("DeliverHistorySync: %v", err)
	}

	var notification *waE2E.HistorySyncNotification

	for notification == nil {
		if m, ok := (<-got).(*events.Message); ok {
			if !m.Info.IsFromMe || m.Info.ID != "N1" || m.Info.Sender.User != "1555" {
				t.Fatalf("notification info %+v, want N1 from the account's own phone", m.Info)
			}

			notification = m.Message.GetProtocolMessage().GetHistorySyncNotification()
		}
	}

	if notification.GetDirectPath() != resp.GetDirectPath() || notification.GetChunkOrder() != 2 || notification.GetProgress() != 40 ||
		notification.GetSyncType() != waE2E.HistorySyncType_RECENT || notification.InitialHistBootstrapInlinePayload != nil {
		t.Fatalf("notification %v, want the blob at %s", notification, resp.GetDirectPath())
	}

	blob, err := client.DownloadHistorySync(ctx, notification, true)
	if err != nil {
		t.Fatalf("DownloadHistorySync: %v", err)
	}

	conv := blob.GetConversations()[0]
	if blob.GetSyncType() != waHistorySync.HistorySync_RECENT || conv.GetID() != "5511987654321@s.whatsapp.net" || len(conv.GetMessages()) != 2 {
		t.Fatalf("blob %v, want the conversation", blob)
	}

	parsed, err := client.ParseWebMessage(types.EmptyJID, conv.GetMessages()[1].GetMessage())
	if err != nil || parsed.Info.ID != "H2" || !parsed.Info.IsFromMe || parsed.Info.Sender.String() != "1555@s.whatsapp.net" || parsed.Message.GetConversation() != "older" {
		t.Fatalf("parsed %v, %v; want H2 from the account, as whatsmeow parses it", parsed, err)
	}

	if err := client.DeleteMedia(ctx, whatsmeow.MediaHistory, notification.GetDirectPath(), notification.GetFileEncSHA256(), notification.GetEncHandle()); err != nil {
		t.Fatalf("DeleteMedia: %v", err)
	}

	// The notification's receipt, the download, then the delete.
	var kinds []string

	for after := uint64(0); len(kinds) < 3; {
		w, err := control.WaitForSent(ctx, &fakev1.WaitForSentRequest{SessionId: "s1", After: after})
		if err != nil {
			t.Fatalf("WaitForSent: %v", err)
		}

		after = w.GetSent().GetIndex()

		switch k := w.GetSent().GetKind().(type) {
		case *fakev1.Sent_Receipt:
			kinds = append(kinds, "receipt "+k.Receipt.GetId())
		case *fakev1.Sent_MediaDownload:
			kinds = append(kinds, "download "+k.MediaDownload.GetDirectPath())
		case *fakev1.Sent_MediaDelete:
			kinds = append(kinds, "delete "+k.MediaDelete.GetDirectPath())
		}
	}

	if want := []string{"receipt N1", "download " + resp.GetDirectPath(), "delete " + resp.GetDirectPath()}; !slices.Equal(kinds, want) {
		t.Fatalf("sent %v, want %v", kinds, want)
	}

	// Deleted: the media server no longer has it.
	if _, err := client.DownloadHistorySync(ctx, notification, true); !errors.Is(err, whatsmeow.ErrMediaDownloadFailedWith404) {
		t.Fatalf("download after the delete: %v, want a 404", err)
	}
}

func TestAnInlineBlobNeedsNoDownload(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx, client, got, _ := linked(t)
	whatsApp.mediaDir = t.TempDir()
	<-got

	resp, err := control.DeliverHistorySync(ctx, historyRequest(true))
	if err != nil || resp.GetDirectPath() != "" {
		t.Fatalf("DeliverHistorySync: %v, %v; want no direct path", resp, err)
	}

	m := (<-got).(*events.Message)
	notification := m.Message.GetProtocolMessage().GetHistorySyncNotification()

	// Inline, the payload is read without a connection, as whatsmeow does (message.go:786-788).
	blob, err := client.DownloadHistorySync(ctx, notification, true)
	if err != nil || len(blob.GetConversations()) != 1 {
		t.Fatalf("DownloadHistorySync: %v, %v; want the blob", blob, err)
	}

	if err := client.DeleteMedia(ctx, whatsmeow.MediaHistory, "", nil, ""); err != nil {
		t.Fatalf("DeleteMedia of nothing: %v", err)
	}
}

func TestABloblessNotificationCarriesNothingToDownload(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx, _, got, _ := linked(t)
	whatsApp.mediaDir = t.TempDir()
	<-got

	req := historyRequest(false)
	req.SyncType, req.Blobless = int32(waE2E.HistorySyncType_NO_HISTORY), true

	if _, err := control.DeliverHistorySync(ctx, req); err != nil {
		t.Fatalf("DeliverHistorySync: %v", err)
	}

	notif := (<-got).(*events.Message).Message.GetProtocolMessage().GetHistorySyncNotification()
	if notif.GetSyncType() != waE2E.HistorySyncType_NO_HISTORY || notif.GetDirectPath() != "" || notif.InitialHistBootstrapInlinePayload != nil ||
		notif.GetMediaKey() != nil || notif.GetFileEncSHA256() != nil {
		t.Fatalf("notification %v, want NO_HISTORY with no blob", notif)
	}
}

func TestAnExpiredBlobFailsItsDownload(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx, client, got, _ := linked(t)
	whatsApp.mediaDir = t.TempDir()
	<-got

	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("connect: %v", err)
	}

	resp, err := control.DeliverHistorySync(ctx, historyRequest(false))
	if err != nil {
		t.Fatalf("DeliverHistorySync: %v", err)
	}

	if _, err := control.ExpireMedia(ctx, &fakev1.ExpireMediaRequest{DirectPath: resp.GetDirectPath(), Status: 410}); err != nil {
		t.Fatalf("ExpireMedia: %v", err)
	}

	notification := &waE2E.HistorySyncNotification{DirectPath: proto.String(resp.GetDirectPath())}
	if _, err := client.DownloadHistorySync(ctx, notification, true); !errors.Is(err, whatsmeow.ErrMediaDownloadFailedWith410) {
		t.Fatalf("download: %v, want a 410", err)
	}

	client.Disconnect()

	if _, err := client.DownloadHistorySync(context.Background(), notification, true); !errors.Is(err, whatsmeow.ErrNotConnected) {
		t.Fatalf("download offline: %v, want not connected", err)
	}
}

func TestCompleteOfflineSyncDispatchesItOnTheQueue(t *testing.T) {
	t.Parallel()

	_, control, ctx, _, got, _ := linked(t)
	<-got

	resp, err := control.CompleteOfflineSync(ctx, &fakev1.CompleteOfflineSyncRequest{SessionId: "s1"})
	if err != nil {
		t.Fatalf("CompleteOfflineSync: %v", err)
	}

	if _, ok := (<-got).(*events.OfflineSyncCompleted); !ok {
		t.Fatal("no OfflineSyncCompleted")
	}

	if resp.GetIndex() != 0 {
		t.Fatalf("index %d, want 0: nothing was sent", resp.GetIndex())
	}
}

// Not parallel: it changes the process-wide store.DeviceProps, as cmd/whatsmeow-net does once at startup.
func TestLinkDeviceKeepsTheDevicePropsThePairingSent(t *testing.T) { //nolint:paralleltest // see above
	saved := proto.CloneOf(wastore.DeviceProps)
	t.Cleanup(func() { wastore.DeviceProps = saved })

	wastore.DeviceProps = proto.CloneOf(saved)
	wastore.DeviceProps.RequireFullSync = proto.Bool(true)
	wastore.DeviceProps.HistorySyncConfig.FullSyncDaysLimit = proto.Uint32(30)

	_, control, ctx, _, got, _ := linked(t)
	<-got

	wastore.DeviceProps.HistorySyncConfig.FullSyncDaysLimit = proto.Uint32(1)

	resp, err := control.GetPairingDeviceProps(ctx, &fakev1.GetPairingDevicePropsRequest{SessionId: "s1"})
	if err != nil {
		t.Fatalf("GetPairingDeviceProps: %v", err)
	}

	want := &fakev1.GetPairingDevicePropsResponse{
		RequireFullSync: true, FullSyncDaysLimit: proto.Uint32(30), StorageQuotaMb: saved.GetHistorySyncConfig().StorageQuotaMb,
	}
	if !proto.Equal(resp, want) {
		t.Fatalf("props %v, want %v", resp, want)
	}

	if _, err := control.GetPairingDeviceProps(ctx, &fakev1.GetPairingDevicePropsRequest{SessionId: "s2"}); err == nil {
		t.Fatal("props of a session never paired, want an error")
	}
}

func TestAPeerSendGoesToTheAccountWithoutALIDLookup(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, got, _ := linked(t)
	<-got

	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("connect: %v", err)
	}

	request := client.BuildHistorySyncRequest(&types.MessageInfo{
		MessageSource: types.MessageSource{Chat: types.NewJID("5511987654321", types.DefaultUserServer)}, ID: "OLD1",
	}, 50)
	if request.GetProtocolMessage().GetPeerDataOperationRequestMessage().GetHistorySyncOnDemandRequest().GetOnDemandMsgCount() != 50 {
		t.Fatalf("request %v, want whatsmeow's", request)
	}

	own := types.NewJID("1555", types.DefaultUserServer)
	if _, err := client.SendMessage(ctx, own, request, whatsmeow.SendRequestExtra{ID: "3EB0000000000000000001", Peer: true}); err != nil {
		t.Fatalf("SendMessage: %v", err)
	}

	w, err := control.WaitForSent(ctx, &fakev1.WaitForSentRequest{SessionId: "s1"})
	if err != nil {
		t.Fatalf("WaitForSent: %v", err)
	}

	if m := w.GetSent().GetMessage(); m.GetChat() != own.String() || !proto.Equal(m.GetMessage(), request) {
		t.Fatalf("sent %v, want the request to %s", w.GetSent(), own)
	}
}
