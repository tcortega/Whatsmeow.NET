//go:build fakewhatsapp

package fakewhatsapp

import (
	"cmp"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"slices"
	"time"

	"go.mau.fi/whatsmeow/appstate"
	"go.mau.fi/whatsmeow/types"
	"google.golang.org/protobuf/types/known/timestamppb"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

// SyncChatState makes the phone change a chat's state, and the session's client sync it (syncFromPhone).
func (c *Control) SyncChatState(_ context.Context, req *fakev1.SyncChatStateRequest) (*fakev1.SyncChatStateResponse, error) {
	client, err := c.whatsApp.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	chat, err := types.ParseJID(req.GetChat())
	if err != nil {
		return nil, fmt.Errorf("sync chat state: chat %q: %w", req.GetChat(), err)
	}

	patch, err := phonePatch(chat, req.GetChange())
	if err != nil {
		return nil, err
	}

	patch.Timestamp = timeOf(req.GetChangeTime())
	client.syncFromPhone(patch, req.GetFullSync())

	return &fakev1.SyncChatStateResponse{}, nil
}

// phonePatch is the patch the phone writes for a change, as whatsmeow's builders make it.
func phonePatch(chat types.JID, change *fakev1.PhoneChatChange) (appstate.PatchInfo, error) {
	switch kind := change.GetKind().(type) {
	case *fakev1.PhoneChatChange_Mute_:
		if kind.Mute.GetUntil() == nil {
			return appstate.BuildMuteAbs(chat, kind.Mute.GetMuted(), nil), nil
		}

		end := kind.Mute.GetUntil().AsTime().UnixMilli()

		return appstate.BuildMuteAbs(chat, kind.Mute.GetMuted(), &end), nil
	case *fakev1.PhoneChatChange_Pinned:
		return appstate.BuildPin(chat, kind.Pinned), nil
	case *fakev1.PhoneChatChange_Archived:
		return appstate.BuildArchive(chat, kind.Archived, time.Time{}, nil), nil
	case *fakev1.PhoneChatChange_Read:
		return appstate.BuildMarkChatAsRead(chat, kind.Read, time.Time{}, nil), nil
	case *fakev1.PhoneChatChange_Star_:
		sender := chat
		if kind.Star.GetSender() != "" {
			var err error
			if sender, err = types.ParseJID(kind.Star.GetSender()); err != nil {
				return appstate.PatchInfo{}, fmt.Errorf("sync chat state: sender %q: %w", kind.Star.GetSender(), err)
			}
		}

		return appstate.BuildStar(chat, sender, kind.Star.GetMessageId(), kind.Star.GetIsFromMe(), kind.Star.GetStarred()), nil
	default:
		return appstate.PatchInfo{}, errors.New("sync chat state: no change")
	}
}

// GetPhoneChatState reads the chat's entries of the session's app state.
func (c *Control) GetPhoneChatState(_ context.Context, req *fakev1.GetPhoneChatStateRequest) (*fakev1.GetPhoneChatStateResponse, error) {
	chat, err := types.ParseJID(req.GetChat())
	if err != nil {
		return nil, fmt.Errorf("get the phone's chat state: chat %q: %w", req.GetChat(), err)
	}

	c.whatsApp.mu.Lock()
	defer c.whatsApp.mu.Unlock()

	resp := &fakev1.GetPhoneChatStateResponse{}

	for _, collection := range c.whatsApp.appStateOf(req.GetSessionId()).collections {
		for _, m := range collection {
			if len(m.index) < 2 || m.index[1] != chat.String() {
				continue
			}

			switch m.index[0] {
			case appstate.IndexMute:
				resp.Muted = m.value.GetMuteAction().GetMuted()
				if end := m.value.GetMuteAction().GetMuteEndTimestamp(); resp.GetMuted() && end > 0 {
					resp.MuteUntil = timestamppb.New(time.UnixMilli(end))
				}
			case appstate.IndexPin:
				resp.Pinned = m.value.GetPinAction().GetPinned()
			case appstate.IndexArchive:
				resp.Archived = m.value.GetArchiveChatAction().GetArchived()
			case appstate.IndexMarkChatAsRead:
				resp.Unread = !m.value.GetMarkChatAsReadAction().GetRead()
			case appstate.IndexStar:
				if len(m.index) == 5 && m.value.GetStarAction().GetStarred() {
					resp.Starred = append(resp.Starred, &fakev1.GetPhoneChatStateResponse_StarredMessage{
						MessageId: m.index[2], IsFromMe: m.index[3] == "1", Sender: m.index[4],
					})
				}
			}
		}
	}

	slices.SortFunc(resp.Starred, func(a, b *fakev1.GetPhoneChatStateResponse_StarredMessage) int {
		return cmp.Compare(a.GetMessageId(), b.GetMessageId())
	})

	return resp, nil
}

// CorruptAppState makes the session's copy of the collection out of step.
func (c *Control) CorruptAppState(_ context.Context, req *fakev1.CorruptAppStateRequest) (*fakev1.CorruptAppStateResponse, error) {
	name := appstate.WAPatchName(req.GetCollection())
	if !slices.Contains(appstate.AllPatchNames[:], name) {
		return nil, fmt.Errorf("corrupt app state: %q is not a collection", req.GetCollection())
	}

	c.whatsApp.mu.Lock()
	c.whatsApp.appStateOf(req.GetSessionId()).corrupted[name] = req.GetUnrecoverable()
	c.whatsApp.mu.Unlock()

	return &fakev1.CorruptAppStateResponse{}, nil
}

// HoldFullSync holds the session's full syncs, or lets them go on.
func (c *Control) HoldFullSync(_ context.Context, req *fakev1.HoldFullSyncRequest) (*fakev1.HoldFullSyncResponse, error) {
	c.whatsApp.mu.Lock()
	defer c.whatsApp.mu.Unlock()

	s := c.whatsApp.appStateOf(req.GetSessionId())
	if s.hold && !req.GetHold() {
		close(s.released)
		s.released = make(chan struct{})
	}

	s.hold = req.GetHold()

	return &fakev1.HoldFullSyncResponse{}, nil
}

// indexKey is a mutation's index as one map key.
func indexKey(index []string) string {
	key, _ := json.Marshal(index)

	return string(key)
}
