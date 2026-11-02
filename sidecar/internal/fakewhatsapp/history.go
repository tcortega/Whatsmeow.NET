//go:build fakewhatsapp

package fakewhatsapp

import (
	"bytes"
	"compress/zlib"
	"context"
	"errors"
	"fmt"
	"os"
	"strings"
	"time"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/proto/waHistorySync"
	"go.mau.fi/whatsmeow/proto/waWeb"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

// The phone's history sync: DeliverHistorySync keeps a blob on the media server as WhatsApp keeps one, zlib-compressed
// protobuf (message.go:791-798), in a file that outlives the process, so a blob half done when the sidecar is killed
// is downloaded again after the restart; the client reads it back as whatsmeow's DownloadHistorySync does, and deletes
// it as DeleteMedia does.

var errNotPaired = errors.New("the session was never paired")

// DownloadHistorySync reads the blob as whatsmeow does (message.go:783-825): the inline payload, or a download from
// the media server, which needs the connection (mediaconn.go:44-58) and fails for a blob ExpireMedia expired. The rest
// is whatsmeow's own, on a client that never connects: it decompresses and parses the blob, and stores what it
// carries, the messages' secrets among it (message.go:799-825, 988-1050).
func (c *Client) DownloadHistorySync(ctx context.Context, notif *waE2E.HistorySyncNotification, synchronousStorage bool) (*waHistorySync.HistorySync, error) {
	data := notif.GetInitialHistBootstrapInlinePayload()
	if data == nil {
		downloaded, err := c.downloadBlob(notif.GetDirectPath())
		if err != nil {
			return nil, fmt.Errorf("failed to download: %w", err)
		}

		data = downloaded
	}

	return whatsmeow.NewClient(c.Store, nil).DownloadHistorySync(ctx, &waE2E.HistorySyncNotification{InitialHistBootstrapInlinePayload: data}, synchronousStorage) //nolint:wrapcheck // whatsmeow's own
}

func (c *Client) downloadBlob(directPath string) ([]byte, error) {
	if !c.IsConnected() {
		return nil, fmt.Errorf("failed to refresh media connections: %w", whatsmeow.ErrNotConnected)
	}

	c.whatsApp.append(&fakev1.Sent{SessionId: c.sessionID, Kind: &fakev1.Sent_MediaDownload{MediaDownload: &fakev1.MediaDownload{DirectPath: directPath}}})

	if err := c.whatsApp.expiredWith(directPath); err != nil {
		return nil, err
	}

	name, err := c.whatsApp.blob(directPath)
	if err != nil {
		return nil, err
	}

	data, err := os.ReadFile(name) //nolint:gosec // G304: a file of the fake's media directory
	if err != nil {
		return nil, whatsmeow.ErrMediaDownloadFailedWith404
	}

	return data, nil
}

// DeleteMedia deletes the file from the media server as whatsmeow's DELETE does (upload.go:305-359): nothing for an
// empty path, and a connection is needed for the media hosts.
func (c *Client) DeleteMedia(_ context.Context, _ whatsmeow.MediaType, directPath string, _ []byte, _ string) error {
	if directPath == "" {
		return nil
	}

	if !c.IsConnected() {
		return fmt.Errorf("failed to refresh media connections: %w", whatsmeow.ErrNotConnected)
	}

	c.whatsApp.append(&fakev1.Sent{SessionId: c.sessionID, Kind: &fakev1.Sent_MediaDelete{MediaDelete: &fakev1.MediaDelete{DirectPath: directPath}}})

	name, err := c.whatsApp.blob(directPath)
	if err != nil {
		return err
	}

	if err := os.Remove(name); err != nil {
		return errors.New("media delete failed with status code 404") //nolint:err113 // whatsmeow's own untyped error (upload.go:353)
	}

	return nil
}

// ParseWebMessage is whatsmeow's own (client.go:1025-1083), on a client that never connects: it reads only the
// device's JIDs.
func (c *Client) ParseWebMessage(chat types.JID, webMsg *waWeb.WebMessageInfo) (*events.Message, error) {
	return whatsmeow.NewClient(c.Store, nil).ParseWebMessage(chat, webMsg) //nolint:wrapcheck // whatsmeow's own
}

// BuildHistorySyncRequest is whatsmeow's own (send.go:588-605).
func (c *Client) BuildHistorySyncRequest(last *types.MessageInfo, count int) *waE2E.Message {
	return whatsmeow.NewClient(c.Store, nil).BuildHistorySyncRequest(last, count)
}

// CompleteOfflineSync dispatches OfflineSyncCompleted on the session's queue (connectionevents.go:72-90).
func (c *Control) CompleteOfflineSync(_ context.Context, req *fakev1.CompleteOfflineSyncRequest) (*fakev1.CompleteOfflineSyncResponse, error) {
	client, err := c.whatsApp.client(req.GetSessionId())
	if err != nil {
		return nil, fmt.Errorf("complete the offline sync: %w", err)
	}

	client.dispatch(&events.OfflineSyncCompleted{})

	c.whatsApp.mu.Lock()
	defer c.whatsApp.mu.Unlock()

	return &fakev1.CompleteOfflineSyncResponse{Index: uint64(len(c.whatsApp.sent))}, nil
}

// DeliverHistorySync keeps the blob and has the account's phone send its notification.
func (c *Control) DeliverHistorySync(ctx context.Context, req *fakev1.DeliverHistorySyncRequest) (*fakev1.DeliverHistorySyncResponse, error) {
	client, err := c.whatsApp.client(req.GetSessionId())
	if err != nil {
		return nil, fmt.Errorf("deliver history sync: %w", err)
	}

	if client.Store.ID == nil {
		return nil, fmt.Errorf("deliver history sync to session %q: %w", req.GetSessionId(), errNotPaired)
	}

	data, err := compressed(historySync(req))
	if err != nil {
		return nil, fmt.Errorf("deliver history sync: %w", err)
	}

	notif := &waE2E.HistorySyncNotification{
		SyncType: waE2E.HistorySyncType(req.GetSyncType()).Enum(), ChunkOrder: proto.Uint32(req.GetChunkOrder()), Progress: proto.Uint32(req.GetProgress()),
	}

	if !req.GetBlobless() {
		notif.FileEncSHA256, notif.MediaKey, notif.EncHandle, notif.FileLength = []byte{1}, []byte{2}, proto.String("handle"), proto.Uint64(uint64(len(data)))
	}

	if req.GetInline() && !req.GetBlobless() {
		notif.InitialHistBootstrapInlinePayload = data
	}

	if !req.GetInline() && !req.GetBlobless() {
		f, directPath, err := c.whatsApp.newBlob()
		if err != nil {
			return nil, fmt.Errorf("deliver history sync: %w", err)
		}

		_, writeErr := f.Write(data)
		if err := errors.Join(writeErr, f.Close()); err != nil {
			return nil, fmt.Errorf("deliver history sync: %w", err)
		}

		notif.DirectPath = proto.String(directPath)
	}

	// From the account's primary device, to the account (message.go:884-907).
	own := client.Store.ID.ToNonAD()
	evt := &events.Message{
		Info: types.MessageInfo{
			MessageSource: types.MessageSource{Chat: own, Sender: own, IsFromMe: true},
			ID:            req.GetMessageId(), Category: "peer", Timestamp: time.Now().Truncate(time.Second),
		},
		Message: &waE2E.Message{ProtocolMessage: &waE2E.ProtocolMessage{Type: waE2E.ProtocolMessage_HISTORY_SYNC_NOTIFICATION.Enum(), HistorySyncNotification: notif}},
	}

	if err := c.whatsApp.deliverMessage(ctx, req.GetSessionId(), evt); err != nil {
		return nil, err
	}

	return &fakev1.DeliverHistorySyncResponse{DirectPath: notif.GetDirectPath()}, nil
}

// historySync is the request's blob, its messages as a WebMessageInfo carries them.
func historySync(req *fakev1.DeliverHistorySyncRequest) *waHistorySync.HistorySync {
	blob := &waHistorySync.HistorySync{
		SyncType:   waHistorySync.HistorySync_HistorySyncType(req.GetSyncType()).Enum(),
		ChunkOrder: proto.Uint32(req.GetChunkOrder()), Progress: proto.Uint32(req.GetProgress()),
	}

	for _, conv := range req.GetConversations() {
		out := &waHistorySync.Conversation{ID: proto.String(conv.GetChat())}

		for _, m := range conv.GetMessages() {
			web := &waWeb.WebMessageInfo{
				Key:              &waCommon.MessageKey{RemoteJID: proto.String(conv.GetChat()), FromMe: proto.Bool(m.GetFromMe()), ID: proto.String(m.GetId())},
				Message:          &waE2E.Message{Conversation: proto.String(m.GetText() + strings.Repeat("x", int(req.GetPadText())))},
				MessageTimestamp: proto.Uint64(uint64(max(m.GetMessageTime().GetSeconds(), 0))),
			}

			if m.GetMessage() != nil {
				web.Message = m.GetMessage()
			}

			if m.GetParticipant() != "" {
				web.Key.Participant = proto.String(m.GetParticipant())
			}

			if m.GetPushName() != "" {
				web.PushName = proto.String(m.GetPushName())
			}

			if m.GetMessageSecret() != nil {
				web.MessageSecret = m.GetMessageSecret()
			}

			web.PollUpdates = m.GetPollUpdates()

			out.Messages = append(out.Messages, &waHistorySync.HistorySyncMsg{Message: web})
		}

		blob.Conversations = append(blob.Conversations, out)
	}

	return blob
}

func compressed(blob *waHistorySync.HistorySync) ([]byte, error) {
	raw, err := proto.Marshal(blob)
	if err != nil {
		return nil, fmt.Errorf("marshal the blob: %w", err)
	}

	var buf bytes.Buffer

	w := zlib.NewWriter(&buf)
	_, writeErr := w.Write(raw)

	if err := errors.Join(writeErr, w.Close()); err != nil {
		return nil, fmt.Errorf("compress the blob: %w", err)
	}

	return buf.Bytes(), nil
}

// GetPairingDeviceProps reads the history fields of the props the session's latest pairing sent.
func (c *Control) GetPairingDeviceProps(_ context.Context, req *fakev1.GetPairingDevicePropsRequest) (*fakev1.GetPairingDevicePropsResponse, error) {
	c.whatsApp.mu.Lock()
	props, ok := c.whatsApp.pairingProps[req.GetSessionId()]
	c.whatsApp.mu.Unlock()

	if !ok {
		return nil, fmt.Errorf("the pairing props of session %q: %w", req.GetSessionId(), errNotPaired)
	}

	config := props.GetHistorySyncConfig()

	return &fakev1.GetPairingDevicePropsResponse{
		RequireFullSync: props.GetRequireFullSync(), FullSyncDaysLimit: config.FullSyncDaysLimit,
		FullSyncSizeMbLimit: config.FullSyncSizeMbLimit, StorageQuotaMb: config.StorageQuotaMb,
	}, nil
}
