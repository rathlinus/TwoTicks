package main

import (
	"context"
	"sort"
	"strings"
	"sync"
	"time"

	"go.mau.fi/whatsmeow/appstate"
	"go.mau.fi/whatsmeow/proto/waSyncAction"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
)

// What the contact and group info shows about a chat besides the profile.

// messageFilters pick the messages of a chat that the info lists: photos and
// videos, documents, links, starred and kept messages.
var messageFilters = map[string]string{
	"media":   `m.kind IN ('image', 'video', 'gif')`,
	"docs":    `m.kind = 'document'`,
	"links":   `m.kind != 'revoked' AND (m.link IS NOT NULL OR m.text LIKE '%http://%' OR m.text LIKE '%https://%' OR m.text LIKE '%www.%')`,
	"starred": `m.starred = 1`,
	"kept":    `m.kept = 1`,
}

type chatInfo struct {
	Media   int        `json:"media"`
	Docs    int        `json:"docs"`
	Links   int        `json:"links"`
	Starred int        `json:"starred"`
	Kept    int        `json:"kept"`
	Recent  []*Message `json:"recent"`
}

// chatInfo counts what the info's rows lead to and picks the newest photos
// and videos for the strip under "Media, links and docs".
func (b *Bridge) chatInfo(ctx context.Context, chat string) (*chatInfo, error) {
	info := &chatInfo{}
	err := b.r.QueryRowContext(ctx, `SELECT
			COALESCE(SUM(`+messageFilters["media"]+`), 0), COALESCE(SUM(`+messageFilters["docs"]+`), 0),
			COALESCE(SUM(`+messageFilters["links"]+`), 0), COALESCE(SUM(m.starred), 0), COALESCE(SUM(m.kept), 0)
		FROM messages m WHERE m.chat = ?`, chat).Scan(&info.Media, &info.Docs, &info.Links, &info.Starred, &info.Kept)
	if err != nil {
		return nil, err
	}
	info.Recent, err = b.queryMessages(ctx, `SELECT `+messageColumns+` FROM messages m
		WHERE m.chat = ? AND `+messageFilters["media"]+` ORDER BY m.ts DESC, m.rowid DESC LIMIT 6`, chat)
	if err != nil {
		return nil, err
	}
	if info.Recent == nil {
		info.Recent = []*Message{}
	}
	return info, nil
}

type commonGroup struct {
	JID     string `json:"jid"`
	Name    string `json:"name"`
	Members string `json:"members"`
}

// joinedGroups keeps the groups for a minute, so opening the info of one
// person after another does not ask the server each time.
type joinedGroups struct {
	sync.Mutex
	at     time.Time
	groups []*types.GroupInfo
}

func (b *Bridge) joinedGroupsCached(ctx context.Context) ([]*types.GroupInfo, error) {
	b.joined.Lock()
	defer b.joined.Unlock()
	if b.joined.groups != nil && time.Since(b.joined.at) < time.Minute {
		return b.joined.groups, nil
	}
	groups, err := b.cli.GetJoinedGroups(ctx)
	if err != nil {
		return nil, err
	}
	b.joined.groups, b.joined.at = groups, time.Now()
	return groups, nil
}

// commonGroups lists the groups that both you and the person are in, with the
// names of some members, as under "Groups in common".
func (b *Bridge) commonGroups(ctx context.Context, person types.JID) ([]commonGroup, error) {
	person = b.canonical(ctx, person)
	groups, err := b.joinedGroupsCached(ctx)
	if err != nil {
		return nil, err
	}
	list := []commonGroup{}
	for _, g := range groups {
		found := false
		var named, numbers []string
		for _, part := range g.Participants {
			jid := part.JID
			if !part.PhoneNumber.IsEmpty() {
				jid = part.PhoneNumber
			}
			jid = b.canonical(ctx, jid)
			if jid == person || b.canonical(ctx, part.LID) == person {
				found = true
			}
			if b.isOwn(part.JID) || b.isOwn(part.LID) || b.isOwn(part.PhoneNumber) {
				continue
			}
			if name := b.nameOf(ctx, jid); strings.HasPrefix(name, "+") {
				numbers = append(numbers, name)
			} else {
				named = append(named, name)
			}
		}
		if !found {
			continue
		}
		// Names first, then numbers, then you, as WhatsApp lists them.
		sort.Slice(named, func(i, j int) bool { return strings.ToLower(named[i]) < strings.ToLower(named[j]) })
		members := append(named, numbers...)
		if len(members) > 12 {
			members = members[:12]
		}
		members = append(members, "You")
		list = append(list, commonGroup{JID: g.JID.String(), Name: g.Name, Members: strings.Join(members, ", ")})
	}
	sort.Slice(list, func(i, j int) bool { return strings.ToLower(list[i].Name) < strings.ToLower(list[j].Name) })
	return list, nil
}

// isBlocked reports whether you blocked the person.
func (b *Bridge) isBlocked(ctx context.Context, person types.JID) bool {
	list, err := b.cli.GetBlocklist(ctx, "")
	if err != nil || list == nil {
		return false
	}
	person = b.canonical(ctx, person)
	for _, jid := range list.JIDs {
		if b.canonical(ctx, jid) == person {
			return true
		}
	}
	return false
}

func (b *Bridge) block(ctx context.Context, person types.JID, block bool) error {
	action := events.BlocklistChangeActionUnblock
	if block {
		action = events.BlocklistChangeActionBlock
	}
	_, err := b.cli.UpdateBlocklist(ctx, b.canonical(ctx, person), action)
	return err
}

// setDisappearing turns disappearing messages on for the given number of
// seconds, or off with 0. WhatsApp only accepts 24 hours, 7 days and 90 days.
func (b *Bridge) setDisappearing(ctx context.Context, jid types.JID, seconds int64) error {
	if err := b.cli.SetDisappearingTimer(ctx, jid, time.Duration(seconds)*time.Second, time.Now()); err != nil {
		return err
	}
	b.updateChat(jid, `ephemeral = ?`, seconds)
	return nil
}

// clearChat deletes the messages of a chat on every linked device but keeps
// the chat, and its starred messages.
func (b *Bridge) clearChat(ctx context.Context, chat string) error {
	jid, key, ts, _ := b.lastKey(ctx, chat)
	if jid.IsEmpty() {
		return userError("noSuchChat")
	}
	span := &waSyncAction.SyncActionMessageRange{LastMessageTimestamp: proto.Int64(ts.Unix())}
	if key != nil {
		span.Messages = []*waSyncAction.SyncActionMessage{{Key: key, Timestamp: proto.Int64(ts.Unix())}}
	}
	// The index ends with whether to delete starred messages and media too.
	patch := appstate.PatchInfo{
		Type: appstate.WAPatchRegularHigh,
		Mutations: []appstate.MutationInfo{{
			Index:   []string{appstate.IndexClearChat, jid.String(), "0", "0"},
			Version: 6,
			Value:   &waSyncAction.SyncActionValue{ClearChatAction: &waSyncAction.ClearChatAction{MessageRange: span}},
		}},
	}
	if err := b.cli.SendAppState(ctx, patch); err != nil {
		return err
	}
	b.deleteChat(jid, true, ts.Unix())
	return nil
}

// deleteChatEverywhere deletes a chat on every linked device.
func (b *Bridge) deleteChatEverywhere(ctx context.Context, chat string) error {
	jid, key, ts, _ := b.lastKey(ctx, chat)
	if jid.IsEmpty() {
		return userError("noSuchChat")
	}
	if err := b.cli.SendAppState(ctx, appstate.BuildDeleteChat(jid, ts, key, false)); err != nil {
		return err
	}
	b.deleteChat(jid, false, ts.Unix())
	return nil
}
