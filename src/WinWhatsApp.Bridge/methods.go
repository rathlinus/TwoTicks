package main

import (
	"context"
	"encoding/json"
	"errors"
	"sort"
	"strings"
	"time"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/appstate"
	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/types"
)

type chatParams struct {
	Chat string `json:"chat"`
}

type messageParams struct {
	Chat string `json:"chat"`
	ID   string `json:"id"`
}

type cursor struct {
	TS  int64 `json:"ts"`
	Seq int64 `json:"seq"`
}

type messagesParams struct {
	Chat   string  `json:"chat"`
	Before *cursor `json:"before"`
	After  *cursor `json:"after"`
	Around string  `json:"around"`
	Limit  int     `json:"limit"`
	// Only photos and videos, for stepping through them in the viewer.
	Media bool `json:"media"`
}

type messagesResult struct {
	Messages []*Message `json:"messages"`
	HasOlder bool       `json:"hasOlder"`
	HasNewer bool       `json:"hasNewer"`
}

type setChatParams struct {
	Chat       string `json:"chat"`
	Mute       *int64 `json:"mute"` // seconds; -1 for always, 0 to unmute
	Pin        *bool  `json:"pin"`
	Archive    *bool  `json:"archive"`
	MarkUnread *bool  `json:"markUnread"`
}

type contact struct {
	JID   string `json:"jid"`
	Name  string `json:"name"`
	Phone string `json:"phone"`
}

func (b *Bridge) methods() map[string]handler {
	return map[string]handler{
		"status": func(ctx context.Context, _ json.RawMessage) (any, error) {
			return b.stateInfo(), nil
		},

		// Starts linking again after the QR codes ran out.
		"login": func(ctx context.Context, _ json.RawMessage) (any, error) {
			if b.cli.Store.ID != nil {
				return nil, errors.New("already linked")
			}
			b.cli.Disconnect()
			go b.connect()
			return true, nil
		},

		// Links with a code typed on the phone instead of scanning.
		"pairPhone": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[struct {
				Phone string `json:"phone"`
			}](raw)
			if err != nil {
				return nil, err
			}
			phone := strings.Map(func(r rune) rune {
				if r >= '0' && r <= '9' {
					return r
				}
				return -1
			}, p.Phone)
			if !b.cli.IsConnected() {
				return nil, errors.New("not connected to WhatsApp yet, try again in a moment")
			}
			code, err := b.cli.PairPhone(ctx, phone, true, whatsmeow.PairClientChrome, "Chrome (Windows)")
			if err != nil {
				return nil, err
			}
			return map[string]string{"code": code}, nil
		},

		"logout": func(ctx context.Context, _ json.RawMessage) (any, error) {
			if b.cli.Store.ID != nil {
				if err := b.cli.Logout(ctx); err != nil {
					b.log.Warnf("Logout failed, removing the session anyway: %v", err)
					_ = b.cli.Store.Delete(ctx)
				}
			}
			b.wipe()
			b.setState(stateLoggedOut, "")
			return true, nil
		},

		"chats": func(ctx context.Context, _ json.RawMessage) (any, error) {
			list, err := b.queryChats(ctx, `WHERE c.last_ts > 0 OR c.pinned > 0`)
			if err != nil {
				return nil, err
			}
			b.decorateChats(ctx, list)
			if list == nil {
				list = []*Chat{}
			}
			return list, nil
		},

		// One chat, also one that has no messages yet, as when starting a new chat.
		"chat": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[chatParams](raw)
			if err != nil {
				return nil, err
			}
			jid, err := types.ParseJID(p.Chat)
			if err != nil {
				return nil, err
			}
			jid = b.canonical(ctx, jid)
			c, err := b.loadChat(ctx, jid.String())
			if err != nil {
				return nil, err
			}
			if c == nil {
				c = &Chat{JID: jid.String(), Group: jid.Server == types.GroupServer}
			}
			b.decorateChats(ctx, []*Chat{c})
			return c, nil
		},

		"messages": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[messagesParams](raw)
			if err != nil {
				return nil, err
			}
			return b.messages(ctx, p)
		},

		"search": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[struct {
				Query string `json:"query"`
				Limit int    `json:"limit"`
			}](raw)
			if err != nil {
				return nil, err
			}
			if strings.TrimSpace(p.Query) == "" {
				return []*Message{}, nil
			}
			if p.Limit <= 0 || p.Limit > 200 {
				p.Limit = 50
			}
			pattern := "%" + strings.NewReplacer(`\`, `\\`, `%`, `\%`, `_`, `\_`).Replace(p.Query) + "%"
			list, err := b.queryMessages(ctx, `SELECT `+messageColumns+` FROM messages m
				WHERE m.text LIKE ? ESCAPE '\' AND m.kind NOT IN ('revoked', 'system')
				ORDER BY m.ts DESC LIMIT ?`, pattern, p.Limit)
			if err != nil {
				return nil, err
			}
			b.decorateMessages(ctx, list)
			if list == nil {
				list = []*Message{}
			}
			return list, nil
		},

		"send": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[sendParams](raw)
			if err != nil {
				return nil, err
			}
			return b.sendText(ctx, p)
		},

		"linkPreview": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[struct {
				URL string `json:"url"`
			}](raw)
			if err != nil {
				return nil, err
			}
			return linkPreview(ctx, p.URL)
		},

		"sendMedia": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[sendMediaParams](raw)
			if err != nil {
				return nil, err
			}
			return b.sendMedia(ctx, p)
		},

		"retry": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[messageParams](raw)
			if err != nil {
				return nil, err
			}
			return true, b.retry(ctx, p.Chat, p.ID)
		},

		"react": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[struct {
				Chat  string `json:"chat"`
				ID    string `json:"id"`
				Emoji string `json:"emoji"`
			}](raw)
			if err != nil {
				return nil, err
			}
			return true, b.react(ctx, p.Chat, p.ID, p.Emoji)
		},

		"edit": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[struct {
				Chat string `json:"chat"`
				ID   string `json:"id"`
				Text string `json:"text"`
			}](raw)
			if err != nil {
				return nil, err
			}
			return true, b.edit(ctx, p.Chat, p.ID, p.Text)
		},

		"pin": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[struct {
				Chat    string `json:"chat"`
				ID      string `json:"id"`
				Pin     bool   `json:"pin"`
				Seconds int64  `json:"seconds"`
			}](raw)
			if err != nil {
				return nil, err
			}
			return true, b.pin(ctx, p.Chat, p.ID, p.Pin, p.Seconds)
		},

		// The pinned messages of a chat, the newest pin first.
		"pins": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[chatParams](raw)
			if err != nil {
				return nil, err
			}
			list, err := b.queryMessages(ctx, `SELECT `+messageColumns+` FROM pins p JOIN messages m ON m.chat = p.chat AND m.id = p.msg_id
				WHERE p.chat = ? AND p.expires > unixepoch() ORDER BY p.ts DESC`, p.Chat)
			if err != nil {
				return nil, err
			}
			b.decorateMessages(ctx, list)
			if list == nil {
				list = []*Message{}
			}
			return list, nil
		},

		"keep": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[struct {
				Chat string `json:"chat"`
				ID   string `json:"id"`
				Keep bool   `json:"keep"`
			}](raw)
			if err != nil {
				return nil, err
			}
			return true, b.keep(ctx, p.Chat, p.ID, p.Keep)
		},

		"forward": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[struct {
				Chat string   `json:"chat"`
				ID   string   `json:"id"`
				To   []string `json:"to"`
			}](raw)
			if err != nil {
				return nil, err
			}
			return b.forward(ctx, p.Chat, p.ID, p.To)
		},

		"revoke": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[messageParams](raw)
			if err != nil {
				return nil, err
			}
			return true, b.revokeOwn(ctx, p.Chat, p.ID)
		},

		"deleteForMe": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[messageParams](raw)
			if err != nil {
				return nil, err
			}
			jid, err := types.ParseJID(p.Chat)
			if err != nil {
				return nil, err
			}
			b.deleteMessage(jid, p.ID)
			return true, nil
		},

		"markRead": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[chatParams](raw)
			if err != nil {
				return nil, err
			}
			return true, b.markRead(ctx, p.Chat)
		},

		// Tells the sender a voice message was listened to.
		"markPlayed": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[messageParams](raw)
			if err != nil {
				return nil, err
			}
			m, err := b.loadMessage(ctx, p.Chat, p.ID)
			if err != nil || m == nil || m.FromMe {
				return false, err
			}
			rawChat, err := types.ParseJID(m.rawChat)
			if err != nil {
				return false, err
			}
			rawSender, _ := types.ParseJID(m.rawSender)
			return true, b.cli.MarkRead(ctx, []types.MessageID{types.MessageID(m.ID)}, time.Now(), rawChat, rawSender, types.ReceiptTypePlayed)
		},

		"download": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[messageParams](raw)
			if err != nil {
				return nil, err
			}
			// Not tied to the request: a download the app stops waiting for
			// still finishes and is there the next time.
			path, err := b.download(b.ctx, p.Chat, p.ID)
			if err != nil {
				return nil, err
			}
			return map[string]string{"path": path}, nil
		},

		"thumbnail": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[messageParams](raw)
			if err != nil {
				return nil, err
			}
			return true, b.thumbnail(ctx, p.Chat, p.ID)
		},

		"avatar": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[struct {
				JID   string `json:"jid"`
				Force bool   `json:"force"`
			}](raw)
			if err != nil {
				return nil, err
			}
			path, err := b.avatar(ctx, p.JID, p.Force)
			if err != nil {
				b.log.Debugf("Avatar of %s: %v", p.JID, err)
			}
			return map[string]string{"path": path}, nil
		},

		// The profile picture at full size, to look at.
		"picture": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[struct {
				JID string `json:"jid"`
			}](raw)
			if err != nil {
				return nil, err
			}
			path, err := b.picture(ctx, p.JID)
			if err != nil {
				return nil, err
			}
			return map[string]string{"path": path}, nil
		},

		"profile": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[struct {
				JID string `json:"jid"`
			}](raw)
			if err != nil {
				return nil, err
			}
			jid, err := types.ParseJID(p.JID)
			if err != nil {
				return nil, err
			}
			return b.profileOf(ctx, jid), nil
		},

		"typing": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[struct {
				Chat   string `json:"chat"`
				Typing bool   `json:"typing"`
			}](raw)
			if err != nil {
				return nil, err
			}
			jid, err := types.ParseJID(p.Chat)
			if err != nil {
				return nil, err
			}
			state := types.ChatPresencePaused
			if p.Typing {
				state = types.ChatPresenceComposing
			}
			return true, b.cli.SendChatPresence(ctx, jid, state, types.ChatPresenceMediaText)
		},

		// The app reports whether its window is in the foreground.
		"online": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[struct {
				Online bool `json:"online"`
			}](raw)
			if err != nil {
				return nil, err
			}
			b.online.Store(p.Online)
			if !b.cli.IsConnected() || b.cli.Store.PushName == "" {
				return false, nil
			}
			presence := types.PresenceUnavailable
			if p.Online {
				presence = types.PresenceAvailable
			}
			return true, b.cli.SendPresence(ctx, presence)
		},

		// Asks for online and typing updates of a person while their chat is open.
		"subscribe": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[chatParams](raw)
			if err != nil {
				return nil, err
			}
			jid, err := types.ParseJID(p.Chat)
			if err != nil || jid.Server == types.GroupServer {
				return false, err
			}
			return true, b.cli.SubscribePresence(ctx, jid)
		},

		"contacts": func(ctx context.Context, _ json.RawMessage) (any, error) {
			all, err := b.cli.Store.Contacts.GetAllContacts(ctx)
			if err != nil {
				return nil, err
			}
			list := []contact{}
			for jid, info := range all {
				if jid.Server != types.DefaultUserServer {
					continue
				}
				name := firstNonEmpty(info.FullName, info.FirstName)
				if name == "" {
					continue
				}
				list = append(list, contact{JID: jid.String(), Name: name, Phone: formatPhone(jid.User)})
			}
			sort.Slice(list, func(i, j int) bool { return strings.ToLower(list[i].Name) < strings.ToLower(list[j].Name) })
			return list, nil
		},

		// Finds the WhatsApp account of a phone number.
		"checkNumber": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[struct {
				Phone string `json:"phone"`
			}](raw)
			if err != nil {
				return nil, err
			}
			phone := "+" + strings.Map(func(r rune) rune {
				if r >= '0' && r <= '9' {
					return r
				}
				return -1
			}, p.Phone)
			results, err := b.cli.IsOnWhatsApp(ctx, []string{phone})
			if err != nil {
				return nil, err
			}
			for _, r := range results {
				if r.IsIn {
					return map[string]string{"jid": r.JID.String()}, nil
				}
			}
			return nil, errors.New("this number is not on WhatsApp")
		},

		"setChat": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[setChatParams](raw)
			if err != nil {
				return nil, err
			}
			return true, b.setChat(ctx, p)
		},

		// Asks the phone for messages older than the oldest one stored.
		"requestOlder": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[chatParams](raw)
			if err != nil {
				return nil, err
			}
			return true, b.requestOlder(ctx, p.Chat)
		},

		"groupInfo": func(ctx context.Context, raw json.RawMessage) (any, error) {
			p, err := params[chatParams](raw)
			if err != nil {
				return nil, err
			}
			jid, err := types.ParseJID(p.Chat)
			if err != nil {
				return nil, err
			}
			info, err := b.cli.GetGroupInfo(ctx, jid)
			if err != nil {
				return nil, err
			}
			type member struct {
				JID   string `json:"jid"`
				Name  string `json:"name"`
				Admin bool   `json:"admin,omitempty"`
				Me    bool   `json:"me,omitempty"`
			}
			members := make([]member, 0, len(info.Participants))
			for _, part := range info.Participants {
				person := part.JID
				if !part.PhoneNumber.IsEmpty() {
					person = part.PhoneNumber
				}
				person = b.canonical(ctx, person)
				me := b.isOwn(part.JID) || b.isOwn(part.LID) || b.isOwn(part.PhoneNumber)
				name := b.nameOf(ctx, person)
				if me {
					name = "You"
				}
				members = append(members, member{JID: person.String(), Name: name, Admin: part.IsAdmin || part.IsSuperAdmin, Me: me})
			}
			_, _ = b.w.ExecContext(ctx, `UPDATE chats SET name = ?, members = ? WHERE jid = ?`, info.Name, len(members), jid.String())
			result := map[string]any{"name": info.Name, "topic": info.Topic, "members": members}
			if !info.GroupCreated.IsZero() {
				result["created"] = info.GroupCreated.Unix()
			}
			owner := info.OwnerPN
			if owner.IsEmpty() {
				owner = info.OwnerJID
			}
			if !owner.IsEmpty() {
				result["createdBy"] = strings.TrimPrefix(b.senderName(ctx, b.canonical(ctx, owner).String(), ""), "~")
			}
			return result, nil
		},
	}
}

func (b *Bridge) messages(ctx context.Context, p messagesParams) (*messagesResult, error) {
	limit := p.Limit
	if limit <= 0 || limit > 500 {
		limit = 60
	}
	sel := `SELECT ` + messageColumns + ` FROM messages m WHERE m.chat = ? `
	if p.Media {
		sel += `AND m.kind IN ('image', 'video', 'gif') `
	}
	var list []*Message
	var err error
	result := &messagesResult{}

	switch {
	case p.Around != "":
		var ts, seq int64
		err = b.r.QueryRowContext(ctx, `SELECT ts, rowid FROM messages WHERE chat = ? AND id = ?`, p.Chat, p.Around).Scan(&ts, &seq)
		if err != nil {
			return nil, errors.New("message not found")
		}
		older, err := b.queryMessages(ctx, sel+`AND (m.ts < ? OR (m.ts = ? AND m.rowid <= ?)) ORDER BY m.ts DESC, m.rowid DESC LIMIT ?`,
			p.Chat, ts, ts, seq, limit/2+1)
		if err != nil {
			return nil, err
		}
		newer, err := b.queryMessages(ctx, sel+`AND (m.ts > ? OR (m.ts = ? AND m.rowid > ?)) ORDER BY m.ts, m.rowid LIMIT ?`,
			p.Chat, ts, ts, seq, limit/2)
		if err != nil {
			return nil, err
		}
		result.HasOlder = len(older) == limit/2+1
		result.HasNewer = len(newer) == limit/2
		reverse(older)
		list = append(older, newer...)
	case p.After != nil:
		list, err = b.queryMessages(ctx, sel+`AND (m.ts > ? OR (m.ts = ? AND m.rowid > ?)) ORDER BY m.ts, m.rowid LIMIT ?`,
			p.Chat, p.After.TS, p.After.TS, p.After.Seq, limit)
		result.HasNewer = len(list) == limit
		result.HasOlder = true
	case p.Before != nil:
		list, err = b.queryMessages(ctx, sel+`AND (m.ts < ? OR (m.ts = ? AND m.rowid < ?)) ORDER BY m.ts DESC, m.rowid DESC LIMIT ?`,
			p.Chat, p.Before.TS, p.Before.TS, p.Before.Seq, limit)
		result.HasOlder = len(list) == limit
		reverse(list)
	default:
		list, err = b.queryMessages(ctx, sel+`ORDER BY m.ts DESC, m.rowid DESC LIMIT ?`, p.Chat, limit)
		result.HasOlder = len(list) == limit
		reverse(list)
	}
	if err != nil {
		return nil, err
	}
	if err := b.attachReactions(ctx, p.Chat, list); err != nil {
		return nil, err
	}
	b.decorateMessages(ctx, list)
	if list == nil {
		list = []*Message{}
	}
	result.Messages = list
	return result, nil
}

func reverse[T any](list []T) {
	for i, j := 0, len(list)-1; i < j; i, j = i+1, j-1 {
		list[i], list[j] = list[j], list[i]
	}
}

// markRead sends read receipts for the unread messages of a chat, so the
// phone and the senders see them as read.
func (b *Bridge) markRead(ctx context.Context, chat string) error {
	rows, err := b.r.QueryContext(ctx, `SELECT id, raw_chat, raw_sender FROM messages WHERE chat = ? AND unread = 1 ORDER BY ts`, chat)
	if err != nil {
		return err
	}
	type group struct {
		chat, sender string
		ids          []types.MessageID
	}
	groups := map[string]*group{}
	var order []string
	for rows.Next() {
		var id, rawChat, rawSender string
		if err := rows.Scan(&id, &rawChat, &rawSender); err != nil {
			rows.Close()
			return err
		}
		key := rawChat + "|" + rawSender
		g := groups[key]
		if g == nil {
			g = &group{chat: rawChat, sender: rawSender}
			groups[key] = g
			order = append(order, key)
		}
		g.ids = append(g.ids, types.MessageID(id))
	}
	rows.Close()

	var markedUnread bool
	_ = b.r.QueryRowContext(ctx, `SELECT marked_unread FROM chats WHERE jid = ?`, chat).Scan(&markedUnread)

	// Only the messages read above: one that arrived since then is still unread.
	for _, key := range order {
		ids := groups[key].ids
		for start := 0; start < len(ids); start += 200 {
			end := min(start+200, len(ids))
			args := []any{chat}
			for _, id := range ids[start:end] {
				args = append(args, string(id))
			}
			placeholders := strings.TrimSuffix(strings.Repeat("?,", end-start), ",")
			_, _ = b.w.ExecContext(ctx, `UPDATE messages SET unread = 0 WHERE chat = ? AND id IN (`+placeholders+`)`, args...)
		}
	}
	_, _ = b.w.ExecContext(ctx, `
		UPDATE chats SET unread = (SELECT COUNT(*) FROM messages WHERE chat = ?1 AND unread = 1), marked_unread = 0
		WHERE jid = ?1`, chat)
	b.emitChat(chat)

	if b.cli.Store.ID == nil {
		return nil
	}
	for _, key := range order {
		g := groups[key]
		rawChat, err := types.ParseJID(g.chat)
		if err != nil {
			continue
		}
		rawSender, _ := types.ParseJID(g.sender)
		for start := 0; start < len(g.ids); start += 100 {
			end := min(start+100, len(g.ids))
			if err := b.cli.MarkRead(ctx, g.ids[start:end], time.Now(), rawChat, rawSender); err != nil {
				b.log.Warnf("Failed to send read receipts in %s: %v", chat, err)
			}
		}
	}
	if markedUnread {
		if jid, key, ts, ok := b.lastKey(ctx, chat); ok {
			if err := b.cli.SendAppState(ctx, appstate.BuildMarkChatAsRead(jid, true, ts, key)); err != nil {
				b.log.Warnf("Failed to mark %s as read: %v", chat, err)
			}
		}
	}
	return nil
}

func (b *Bridge) setChat(ctx context.Context, p setChatParams) error {
	jid, err := types.ParseJID(p.Chat)
	if err != nil {
		return err
	}
	if p.Mute != nil {
		mute := *p.Mute != 0
		var duration time.Duration
		if *p.Mute > 0 {
			duration = time.Duration(*p.Mute) * time.Second
		}
		if err := b.cli.SendAppState(ctx, appstate.BuildMute(jid, mute, duration)); err != nil {
			return err
		}
		until := int64(0)
		if mute {
			until = -1
			if duration > 0 {
				until = time.Now().Add(duration).Unix()
			}
		}
		b.updateChat(jid, `muted_until = ?`, until)
	}
	if p.Pin != nil {
		if err := b.cli.SendAppState(ctx, appstate.BuildPin(jid, *p.Pin)); err != nil {
			return err
		}
		pinned := int64(0)
		if *p.Pin {
			pinned = time.Now().Unix()
		}
		b.updateChat(jid, `pinned = ?`, pinned)
	}
	if p.Archive != nil {
		_, key, ts, _ := b.lastKey(ctx, p.Chat)
		if err := b.cli.SendAppState(ctx, appstate.BuildArchive(jid, *p.Archive, ts, key)); err != nil {
			return err
		}
		b.updateChat(jid, `archived = ?`, *p.Archive)
	}
	if p.MarkUnread != nil && *p.MarkUnread {
		_, key, ts, _ := b.lastKey(ctx, p.Chat)
		if err := b.cli.SendAppState(ctx, appstate.BuildMarkChatAsRead(jid, false, ts, key)); err != nil {
			return err
		}
		b.updateChat(jid, `marked_unread = 1`)
	}
	return nil
}

// lastKey identifies the newest message of a chat, which app state changes
// like archiving refer to.
func (b *Bridge) lastKey(ctx context.Context, chat string) (types.JID, *waCommon.MessageKey, time.Time, bool) {
	jid, err := types.ParseJID(chat)
	if err != nil {
		return jid, nil, time.Time{}, false
	}
	var id, rawChat, rawSender string
	var fromMe bool
	var ts int64
	err = b.r.QueryRowContext(ctx, `SELECT id, raw_chat, raw_sender, from_me, ts FROM messages WHERE chat = ? AND kind != 'system'
		ORDER BY ts DESC, rowid DESC LIMIT 1`, chat).Scan(&id, &rawChat, &rawSender, &fromMe, &ts)
	if err != nil {
		return jid, nil, time.Now(), false
	}
	keyChat, err := types.ParseJID(rawChat)
	if err != nil || keyChat.IsEmpty() {
		keyChat = jid
	}
	sender := types.EmptyJID
	if !fromMe {
		sender, _ = types.ParseJID(rawSender)
	}
	return jid, b.cli.BuildMessageKey(keyChat, sender, types.MessageID(id)), time.Unix(ts, 0), true
}

func (b *Bridge) requestOlder(ctx context.Context, chat string) error {
	var id, rawChat, rawSender string
	var fromMe bool
	var ts int64
	err := b.r.QueryRowContext(ctx, `SELECT id, raw_chat, raw_sender, from_me, ts FROM messages WHERE chat = ? AND kind != 'system'
		ORDER BY ts, rowid LIMIT 1`, chat).Scan(&id, &rawChat, &rawSender, &fromMe, &ts)
	if err != nil {
		return errors.New("no messages to continue from")
	}
	keyChat, err := types.ParseJID(rawChat)
	if err != nil {
		return err
	}
	sender, _ := types.ParseJID(rawSender)
	info := &types.MessageInfo{
		MessageSource: types.MessageSource{Chat: keyChat, Sender: sender, IsFromMe: fromMe, IsGroup: keyChat.Server == types.GroupServer},
		ID:            types.MessageID(id),
		Timestamp:     time.Unix(ts, 0),
	}
	_, err = b.cli.SendPeerMessage(ctx, b.cli.BuildHistorySyncRequest(info, 50))
	return err
}
