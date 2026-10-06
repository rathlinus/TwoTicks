package main

import (
	"context"
	"crypto/sha1"
	"encoding/hex"
	"fmt"
	"sort"
	"strings"
	"time"

	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/proto/waHistorySync"
	"go.mau.fi/whatsmeow/proto/waWeb"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
)

// Only messages newer than this raise a notification. Older ones arrive when
// the PC comes back online; the unread counts tell about those.
const notifyWindow = 3 * time.Minute

// skipChat leaves out what the app does not show: status updates, channels and
// broadcast lists.
func skipChat(jid types.JID) bool {
	return jid.IsEmpty() || jid.Server == types.BroadcastServer || jid.Server == types.NewsletterServer ||
		jid.Server == types.BotServer
}

func (b *Bridge) ownJID() types.JID {
	if id := b.cli.Store.ID; id != nil {
		return id.ToNonAD()
	}
	return types.EmptyJID
}

func (b *Bridge) isOwn(jid types.JID) bool {
	jid = jid.ToNonAD()
	if id := b.cli.Store.ID; id != nil && id.User == jid.User && jid.Server == types.DefaultUserServer {
		return true
	}
	lid := b.cli.Store.GetLID()
	return !lid.IsEmpty() && lid.User == jid.User && jid.Server == types.HiddenUserServer
}

// convert turns a decrypted message into a stored one. It returns nil for
// messages that only change other messages (reactions, edits, deletions) or
// that carry nothing to show.
func (b *Bridge) convert(ctx context.Context, evt *events.Message) *Message {
	msg := evt.Message
	if msg == nil {
		return nil
	}
	info := evt.Info
	chat := b.canonical(ctx, info.Chat)
	sender := b.canonical(ctx, info.Sender)
	if info.IsFromMe {
		sender = b.ownJID()
	}

	kind, text, media, ci := describe(msg)
	if kind == "" {
		return nil
	}
	if kind == "unsupported" {
		b.log.Infof("Unsupported message %s with fields %v", evt.Info.ID, fieldNames(msg))
	}
	if evt.IsViewOnce {
		kind, text, media = "viewonce", "", nil
	}

	m := &Message{
		Chat:      chat.String(),
		ID:        info.ID,
		Sender:    sender.String(),
		FromMe:    info.IsFromMe,
		TS:        info.Timestamp.Unix(),
		Kind:      kind,
		Text:      text,
		Media:     media,
		Status:    statusSent,
		pushName:  info.PushName,
		rawChat:   info.Chat.ToNonAD().String(),
		rawSender: info.Sender.ToNonAD().String(),
	}
	if !info.IsFromMe {
		m.Status = 0
	}
	if ext := msg.GetExtendedTextMessage(); ext != nil && ext.GetMatchedText() != "" && (ext.GetTitle() != "" || ext.GetDescription() != "") {
		m.Link = &Link{URL: ext.GetMatchedText(), Title: ext.GetTitle(), Description: ext.GetDescription(), Thumb: ext.GetJPEGThumbnail()}
	}
	if ci != nil {
		m.mentionJIDs = ci.GetMentionedJID()
		if id := ci.GetStanzaID(); id != "" && ci.GetQuotedMessage() != nil {
			qKind, qText, qMedia, _ := describe(ci.GetQuotedMessage())
			if qText == "" && qMedia != nil {
				qText = qMedia.Name
			}
			quoteSender := chat
			if p := ci.GetParticipant(); p != "" {
				if jid, err := types.ParseJID(p); err == nil {
					quoteSender = b.canonical(ctx, jid)
				}
			}
			m.Quote = &Quote{ID: id, Sender: quoteSender.String(), Kind: qKind, Text: truncate(qText, 300)}
		}
	}
	if kind != "viewonce" && kind != "unsupported" {
		m.raw, _ = proto.Marshal(msg)
	}
	return m
}

func (b *Bridge) onMessage(evt *events.Message, live bool) {
	ctx := b.ctx
	info := evt.Info
	if skipChat(info.Chat) || evt.Message == nil {
		return
	}
	msg := evt.Message
	chat := b.canonical(ctx, info.Chat)

	if pm := msg.GetProtocolMessage(); pm != nil {
		sender := b.canonical(ctx, info.Sender)
		if info.IsFromMe {
			sender = b.ownJID()
		}
		target := pm.GetKey().GetID()
		switch pm.GetType() {
		case waE2E.ProtocolMessage_REVOKE:
			if b.mayRevoke(ctx, chat, sender, info.IsFromMe, target) {
				b.revoke(chat.String(), target)
			}
		case waE2E.ProtocolMessage_MESSAGE_EDIT:
			b.receiveEdit(ctx, chat, sender, info.IsFromMe, target, pm.GetEditedMessage())
		case waE2E.ProtocolMessage_EPHEMERAL_SETTING:
			b.updateChat(info.Chat, `ephemeral = ?`, pm.GetEphemeralExpiration())
		}
		return
	}
	// Newer WhatsApp versions send some edits encrypted with the secret of the
	// message they change, instead of as a protocol message.
	if enc := msg.GetSecretEncryptedMessage(); enc != nil {
		if enc.GetSecretEncType() != waE2E.SecretEncryptedMessage_MESSAGE_EDIT {
			return
		}
		edited, err := b.decryptEdit(ctx, evt)
		if err != nil {
			return
		}
		sender := b.canonical(ctx, info.Sender)
		if info.IsFromMe {
			sender = b.ownJID()
		}
		b.receiveEdit(ctx, chat, sender, info.IsFromMe, enc.GetTargetMessageKey().GetID(), edited)
		return
	}
	if r := msg.GetReactionMessage(); r != nil {
		sender := b.canonical(ctx, info.Sender)
		if info.IsFromMe {
			sender = b.ownJID()
		}
		b.applyReaction(chat.String(), sender.String(), r.GetKey().GetID(), r.GetText(), info.Timestamp.Unix())
		return
	}

	m := b.convert(ctx, evt)
	if m == nil {
		return
	}
	isNew := live && !m.FromMe
	if isNew {
		m.unread = true
	}

	tx, err := b.w.BeginTx(ctx, nil)
	if err != nil {
		b.log.Errorf("Failed to store message %s: %v", m.ID, err)
		return
	}
	defer tx.Rollback()
	written, err := saveMessage(ctx, tx, m)
	if err != nil {
		b.log.Errorf("Failed to store message %s: %v", m.ID, err)
		return
	}
	if !written {
		return
	}
	addUnread := 0
	if isNew {
		addUnread = 1
	}
	if err := bumpChat(ctx, tx, m.Chat, info.IsGroup, m.TS, m.ID, addUnread); err != nil {
		b.log.Errorf("Failed to update chat %s: %v", m.Chat, err)
		return
	}
	if exp := contextInfoOf(msg).GetExpiration(); exp > 0 {
		_, _ = tx.ExecContext(ctx, `UPDATE chats SET ephemeral = ? WHERE jid = ?`, exp, m.Chat)
	}
	if err := tx.Commit(); err != nil {
		b.log.Errorf("Failed to store message %s: %v", m.ID, err)
		return
	}
	if chat.Server == types.HiddenUserServer {
		b.lidChats.Store(m.Chat, true)
	}

	stored, _ := b.loadMessage(ctx, m.Chat, m.ID)
	if stored == nil {
		return
	}
	_ = b.attachReactions(ctx, m.Chat, []*Message{stored})
	if isNew && time.Since(info.Timestamp) < notifyWindow && !b.isMuted(m.Chat) {
		stored.Notify = true
	}
	// The chat first: a notification for a new chat needs its name.
	b.emitChat(m.Chat)
	b.emitMessage(stored)

	if info.IsGroup {
		b.ensureGroupName(chat)
	}
}

func contextInfoOf(msg *waE2E.Message) *waE2E.ContextInfo {
	_, _, _, ci := describe(msg)
	return ci
}

func (b *Bridge) isMuted(chat string) bool {
	var until int64
	_ = b.r.QueryRowContext(b.ctx, `SELECT muted_until FROM chats WHERE jid = ?`, chat).Scan(&until)
	return until < 0 || until > time.Now().Unix()
}

// onUndecryptable stores a placeholder. The phone is asked to send the message
// again, and when it arrives it replaces the placeholder.
func (b *Bridge) onUndecryptable(evt *events.UndecryptableMessage) {
	info := evt.Info
	if skipChat(info.Chat) || evt.DecryptFailMode == events.DecryptFailHide {
		return
	}
	kind := "pending"
	if evt.UnavailableType == events.UnavailableTypeViewOnce {
		kind = "viewonce"
	}
	chat := b.canonical(b.ctx, info.Chat)
	sender := b.canonical(b.ctx, info.Sender)
	m := &Message{
		Chat: chat.String(), ID: info.ID, Sender: sender.String(), FromMe: info.IsFromMe, TS: info.Timestamp.Unix(),
		Kind: kind, pushName: info.PushName, rawChat: info.Chat.ToNonAD().String(), rawSender: info.Sender.ToNonAD().String(),
	}
	written, err := saveMessage(b.ctx, b.w, m)
	if err != nil || !written {
		return
	}
	_ = bumpChat(b.ctx, b.w, m.Chat, info.IsGroup, m.TS, m.ID, 0)
	b.reloadAndEmit(m.Chat, m.ID)
	b.emitChat(m.Chat)
}

// isSenderOf reports whether a message was sent by the given person. Edits and
// deletions travel inside the encrypted message, where WhatsApp's servers
// cannot check them, so a changed client could otherwise rewrite anyone's
// messages.
func (b *Bridge) isSenderOf(ctx context.Context, chat, id string, sender types.JID, fromMe bool) bool {
	target, err := b.loadMessage(ctx, chat, id)
	if err != nil || target == nil {
		return false
	}
	if target.FromMe || fromMe {
		return target.FromMe && fromMe
	}
	return target.Sender == sender.String()
}

// mayRevoke reports whether someone may delete a message for everyone: its
// sender may, and in a group so may an admin.
func (b *Bridge) mayRevoke(ctx context.Context, chat, sender types.JID, fromMe bool, id string) bool {
	if b.isSenderOf(ctx, chat.String(), id, sender, fromMe) {
		return true
	}
	if chat.Server != types.GroupServer {
		return false
	}
	info, err := b.cli.GetGroupInfo(ctx, chat)
	if err != nil {
		b.log.Warnf("Could not check who may delete in %s: %v", chat, err)
		return false
	}
	for _, p := range info.Participants {
		if !(p.IsAdmin || p.IsSuperAdmin) {
			continue
		}
		for _, jid := range []types.JID{p.JID, p.LID, p.PhoneNumber} {
			if !jid.IsEmpty() && b.canonical(ctx, jid) == sender {
				return true
			}
		}
	}
	return false
}

func (b *Bridge) revoke(chat, id string) {
	_, err := b.w.ExecContext(b.ctx,
		`UPDATE messages SET kind = 'revoked', text = '', media = NULL, quote = NULL, link = NULL, raw = NULL WHERE chat = ? AND id = ?`, chat, id)
	if err != nil {
		b.log.Errorf("Failed to delete message %s: %v", id, err)
		return
	}
	_, _ = b.w.ExecContext(b.ctx, `DELETE FROM reactions WHERE chat = ? AND msg_id = ?`, chat, id)
	b.reloadAndEmit(chat, id)
	b.emitChat(chat)
}

// decryptEdit returns the new content of an edit sent encrypted with the
// secret of the message it changes.
func (b *Bridge) decryptEdit(ctx context.Context, evt *events.Message) (*waE2E.Message, error) {
	enc := evt.Message.GetSecretEncryptedMessage()
	edited, err := b.cli.DecryptSecretEncryptedMessage(ctx, evt)
	if err != nil {
		b.log.Warnf("Failed to decrypt the edit %s of %s: %v", evt.Info.ID, enc.GetTargetMessageKey().GetID(), err)
		return nil, err
	}
	// The content may come wrapped as the edit it is.
	if inner := edited.GetEditedMessage().GetMessage(); inner != nil {
		edited = inner
	}
	if pm := edited.GetProtocolMessage(); pm != nil && pm.GetEditedMessage() != nil {
		edited = pm.GetEditedMessage()
	}
	return edited, nil
}

// receiveEdit changes the text of a message, if the edit comes from who sent it.
func (b *Bridge) receiveEdit(ctx context.Context, chat, sender types.JID, fromMe bool, id string, edited *waE2E.Message) {
	if !b.isSenderOf(ctx, chat.String(), id, sender, fromMe) {
		b.log.Warnf("Ignoring an edit of %s in %s by %s, who did not send it or it is not stored", id, chat, sender)
		return
	}
	b.applyEdit(chat.String(), id, edited)
}

func (b *Bridge) applyEdit(chat, id string, edited *waE2E.Message) {
	if edited == nil {
		return
	}
	_, text, _, _ := describe(edited)
	raw, err := b.loadRaw(b.ctx, chat, id)
	if err == nil && len(raw) > 0 {
		// Keep the media of an edited caption; only the text changes.
		var original waE2E.Message
		if proto.Unmarshal(raw, &original) == nil {
			setText(&original, text)
			raw, _ = proto.Marshal(&original)
		}
	}
	_, err = b.w.ExecContext(b.ctx, `UPDATE messages SET text = ?, edited = 1, raw = COALESCE(?, raw) WHERE chat = ? AND id = ?`, text, raw, chat, id)
	if err != nil {
		b.log.Errorf("Failed to edit message %s: %v", id, err)
		return
	}
	b.reloadAndEmit(chat, id)
	b.emitChat(chat)
}

// setText replaces the text or caption of a message.
func setText(msg *waE2E.Message, text string) {
	switch {
	case msg.Conversation != nil:
		msg.Conversation = proto.String(text)
	case msg.ExtendedTextMessage != nil:
		msg.ExtendedTextMessage.Text = proto.String(text)
	case msg.ImageMessage != nil:
		msg.ImageMessage.Caption = proto.String(text)
	case msg.VideoMessage != nil:
		msg.VideoMessage.Caption = proto.String(text)
	case msg.DocumentMessage != nil:
		msg.DocumentMessage.Caption = proto.String(text)
	}
}

func (b *Bridge) applyReaction(chat, sender, id, emoji string, ts int64) {
	var err error
	if emoji == "" {
		_, err = b.w.ExecContext(b.ctx, `DELETE FROM reactions WHERE chat = ? AND msg_id = ? AND sender = ?`, chat, id, sender)
	} else {
		_, err = b.w.ExecContext(b.ctx, `
			INSERT INTO reactions (chat, msg_id, sender, emoji, ts) VALUES (?, ?, ?, ?, ?)
			ON CONFLICT (chat, msg_id, sender) DO UPDATE SET emoji = excluded.emoji, ts = excluded.ts
			WHERE excluded.ts >= reactions.ts`, chat, id, sender, emoji, ts)
	}
	if err != nil {
		b.log.Errorf("Failed to store reaction: %v", err)
		return
	}
	b.reloadAndEmit(chat, id)
}

func (b *Bridge) onReceipt(evt *events.Receipt) {
	ctx := b.ctx
	chat := b.canonical(ctx, evt.Chat).String()

	if evt.Type == types.ReceiptTypeReadSelf || (evt.IsFromMe && evt.Type == types.ReceiptTypeRead) {
		// Read on another of our devices.
		args := []any{chat}
		for _, id := range evt.MessageIDs {
			args = append(args, id)
		}
		placeholders := strings.TrimSuffix(strings.Repeat("?,", len(evt.MessageIDs)), ",")
		_, _ = b.w.ExecContext(ctx, `UPDATE messages SET unread = 0 WHERE chat = ? AND id IN (`+placeholders+`)`, args...)
		_, _ = b.w.ExecContext(ctx, `
			UPDATE chats SET unread = (SELECT COUNT(*) FROM messages WHERE chat = ?1 AND unread = 1), marked_unread = 0
			WHERE jid = ?1`, chat)
		b.emitChat(chat)
		return
	}
	if evt.IsFromMe {
		return
	}

	var status int
	switch evt.Type {
	case types.ReceiptTypeDelivered, "inactive":
		status = statusDelivered
	case types.ReceiptTypeRead:
		status = statusRead
	case types.ReceiptTypePlayed:
		status = statusPlayed
	default:
		return
	}

	var changed []string
	if evt.IsGroup {
		user := b.canonical(ctx, evt.Sender).String()
		var members int
		_ = b.r.QueryRowContext(ctx, `SELECT members FROM chats WHERE jid = ?`, chat).Scan(&members)
		for _, id := range evt.MessageIDs {
			_, _ = b.w.ExecContext(ctx, `
				INSERT INTO receipts (chat, msg_id, user, status) VALUES (?, ?, ?, ?)
				ON CONFLICT (chat, msg_id, user) DO UPDATE SET status = MAX(receipts.status, excluded.status)`,
				chat, id, user, status)
			// Delivered as soon as one member has it; read once all others read it.
			reached := statusDelivered
			if members > 1 {
				var count int
				_ = b.w.QueryRowContext(ctx, `SELECT COUNT(*) FROM receipts WHERE chat = ? AND msg_id = ? AND status >= ?`,
					chat, id, statusRead).Scan(&count)
				if count >= members-1 {
					reached = statusRead
				}
			}
			if b.raiseStatus(chat, id, reached) {
				changed = append(changed, id)
			}
		}
	} else {
		for _, id := range evt.MessageIDs {
			if b.raiseStatus(chat, id, status) {
				changed = append(changed, id)
			}
		}
	}
	if len(changed) > 0 {
		// In a group the messages of one receipt can end up with different
		// ticks: some read by everyone, others not yet.
		byStatus := map[int][]string{}
		for _, id := range changed {
			var s int
			_ = b.r.QueryRowContext(ctx, `SELECT status FROM messages WHERE chat = ? AND id = ?`, chat, id).Scan(&s)
			byStatus[s] = append(byStatus[s], id)
		}
		for s, ids := range byStatus {
			b.out.event("status", map[string]any{"chat": chat, "ids": ids, "status": s})
		}
		b.emitChatIfLast(chat, changed)
	}
}

func (b *Bridge) raiseStatus(chat, id string, status int) bool {
	res, err := b.w.ExecContext(b.ctx,
		`UPDATE messages SET status = ? WHERE chat = ? AND id = ? AND from_me = 1 AND status >= 0 AND status < ?`, status, chat, id, status)
	if err != nil {
		return false
	}
	n, _ := res.RowsAffected()
	return n > 0
}

// emitChatIfLast refreshes the chat list entry when one of the messages is the
// one it shows, so its ticks change too.
func (b *Bridge) emitChatIfLast(chat string, ids []string) {
	var last string
	_ = b.r.QueryRowContext(b.ctx, `SELECT last_id FROM chats WHERE jid = ?`, chat).Scan(&last)
	for _, id := range ids {
		if id == last {
			b.emitChat(chat)
			return
		}
	}
}

func (b *Bridge) onHistorySync(evt *events.HistorySync) {
	ctx := b.ctx
	data := evt.Data
	syncType := data.GetSyncType()
	b.log.Infof("History sync %s: %d conversations, progress %d", syncType, len(data.GetConversations()), data.GetProgress())

	// Whether the chat state in this sync describes the present. On-demand
	// syncs of older messages carry stale unread counts.
	current := syncType == waHistorySync.HistorySync_INITIAL_BOOTSTRAP ||
		syncType == waHistorySync.HistorySync_RECENT || syncType == waHistorySync.HistorySync_FULL

	type parsed struct {
		chat     types.JID
		rawChat  types.JID
		conv     *waHistorySync.Conversation
		messages []*Message
		react    [][4]any
	}
	var convs []parsed

	// Parse everything before writing: resolving LIDs reads the session
	// database, which should not happen inside the write transaction.
	for _, conv := range data.GetConversations() {
		rawJID, err := types.ParseJID(conv.GetID())
		if err != nil || skipChat(rawJID) {
			continue
		}
		if rawJID.Server == types.HiddenUserServer && conv.GetPnJID() != "" {
			if pn, err := types.ParseJID(conv.GetPnJID()); err == nil {
				b.cli.StoreLIDPNMapping(ctx, rawJID, pn)
			}
		}
		p := parsed{chat: b.canonical(ctx, rawJID), rawChat: rawJID, conv: conv}

		type ordered struct {
			m     *Message
			order uint64
		}
		var list []ordered
		for _, hm := range conv.GetMessages() {
			web := hm.GetMessage()
			if web == nil {
				continue
			}
			m := b.convertHistory(ctx, rawJID, web)
			if m != nil {
				list = append(list, ordered{m, hm.GetMsgOrderID()})
			}
			for _, r := range web.GetReactions() {
				reactor := b.ownJID()
				if !r.GetKey().GetFromMe() {
					if jid, err := types.ParseJID(r.GetKey().GetParticipant()); err == nil && !jid.IsEmpty() {
						reactor = b.canonical(ctx, jid)
					} else {
						reactor = p.chat
					}
				}
				if r.GetText() != "" {
					p.react = append(p.react, [4]any{web.GetKey().GetID(), reactor.String(), r.GetText(), normalizeTimestamp(r.GetSenderTimestampMS())})
				}
			}
		}
		sort.SliceStable(list, func(i, j int) bool {
			if list[i].m.TS != list[j].m.TS {
				return list[i].m.TS < list[j].m.TS
			}
			return list[i].order < list[j].order
		})
		for _, o := range list {
			p.messages = append(p.messages, o.m)
		}
		if current {
			// The newest unread messages are the ones to send read receipts
			// for once the chat is opened here.
			left := int(conv.GetUnreadCount())
			for i := len(p.messages) - 1; i >= 0 && left > 0; i-- {
				if !p.messages[i].FromMe && p.messages[i].Kind != "system" {
					p.messages[i].unread = true
					left--
				}
			}
		}
		convs = append(convs, p)
	}

	tx, err := b.w.BeginTx(ctx, nil)
	if err != nil {
		b.log.Errorf("Failed to store history: %v", err)
		return
	}
	defer tx.Rollback()

	for _, p := range convs {
		conv := p.conv
		chat := p.chat.String()
		isGroup := p.chat.Server == types.GroupServer
		name := conv.GetName()
		if name == "" {
			name = conv.GetDisplayName()
		}
		ts := int64(conv.GetConversationTimestamp())
		if lm := int64(conv.GetLastMsgTimestamp()); lm > ts {
			ts = lm
		}

		_, err := tx.ExecContext(ctx, `
			INSERT INTO chats (jid, name, is_group, last_ts) VALUES (?, ?, ?, ?)
			ON CONFLICT (jid) DO UPDATE SET
				name = CASE WHEN excluded.name != '' THEN excluded.name ELSE chats.name END,
				last_ts = MAX(chats.last_ts, excluded.last_ts)`,
			chat, name, isGroup, ts)
		if err != nil {
			b.log.Errorf("Failed to store chat %s: %v", chat, err)
			continue
		}
		if current {
			muted := normalizeTimestamp(int64(conv.GetMuteEndTime()))
			_, err = tx.ExecContext(ctx, `
				UPDATE chats SET unread = ?, marked_unread = ?, archived = ?, pinned = ?, muted_until = ?, read_only = ?, ephemeral = ?
				WHERE jid = ?`,
				conv.GetUnreadCount(), conv.GetMarkedAsUnread(), conv.GetArchived(), normalizeTimestamp(int64(conv.GetPinned())),
				muted, conv.GetReadOnly(), conv.GetEphemeralExpiration(), chat)
			if err != nil {
				b.log.Errorf("Failed to store chat %s: %v", chat, err)
			}
		}
		for _, m := range p.messages {
			written, err := saveMessage(ctx, tx, m)
			if err != nil {
				b.log.Errorf("Failed to store message %s: %v", m.ID, err)
				continue
			}
			if m.isEdit && !written {
				_, _ = tx.ExecContext(ctx, `UPDATE messages SET text = ?, edited = 1 WHERE chat = ? AND id = ?`, m.Text, chat, m.ID)
				continue
			}
			if err := bumpChat(ctx, tx, chat, isGroup, m.TS, m.ID, 0); err != nil {
				b.log.Errorf("Failed to update chat %s: %v", chat, err)
			}
		}
		// The conversation's time can be later than its newest stored message,
		// which bumpChat then does not count as the last one.
		_, _ = tx.ExecContext(ctx, `
			UPDATE chats SET last_id = COALESCE((SELECT id FROM messages WHERE chat = ?1 ORDER BY ts DESC, rowid DESC LIMIT 1), last_id)
			WHERE jid = ?1`, chat)
		for _, r := range p.react {
			_, _ = tx.ExecContext(ctx, `INSERT OR IGNORE INTO reactions (chat, msg_id, sender, emoji, ts) VALUES (?, ?, ?, ?, ?)`,
				chat, r[0], r[1], r[2], r[3])
		}
		if p.chat.Server == types.HiddenUserServer {
			b.lidChats.Store(chat, true)
		}
		b.historyChats.Store(chat, true)
	}
	if err := tx.Commit(); err != nil {
		b.log.Errorf("Failed to store history: %v", err)
		return
	}

	b.out.event("sync", map[string]any{"type": syncType.String(), "progress": data.GetProgress()})
	b.chatsChanged.trigger()
}

// convertHistory turns a message from history sync into a stored one.
func (b *Bridge) convertHistory(ctx context.Context, chat types.JID, web *waWeb.WebMessageInfo) *Message {
	evt, err := b.cli.ParseWebMessage(chat, web)
	if err != nil {
		return nil
	}
	if stub := web.GetMessageStubType(); stub != waWeb.WebMessageInfo_UNKNOWN {
		return b.stubMessage(ctx, evt, web)
	}
	if evt.Message == nil {
		return nil
	}
	if evt.Message.GetProtocolMessage() != nil || evt.Message.GetReactionMessage() != nil {
		return nil
	}
	if enc := evt.Message.GetSecretEncryptedMessage(); enc != nil {
		if enc.GetSecretEncType() != waE2E.SecretEncryptedMessage_MESSAGE_EDIT {
			return nil
		}
		edited, err := b.decryptEdit(ctx, evt)
		if err != nil {
			return nil
		}
		// As ParseWebMessage does with a plain edit: the new content under the original ID.
		evt.Info.ID = enc.GetTargetMessageKey().GetID()
		evt.Message = edited
	}
	m := b.convert(ctx, evt)
	if m == nil {
		return nil
	}
	if evt.Info.ID != web.GetKey().GetID() {
		// ParseWebMessage turns an edit into the edited message under the
		// original ID; it replaces the text of the original when that is stored.
		m.Edited = true
		m.isEdit = true
	}
	if m.FromMe {
		// The ticks of WebMessageInfo count from ERROR = 0.
		switch web.GetStatus() {
		case waWeb.WebMessageInfo_ERROR:
			m.Status = statusFailed
		case waWeb.WebMessageInfo_PENDING:
			m.Status = statusPending
		case waWeb.WebMessageInfo_SERVER_ACK:
			m.Status = statusSent
		case waWeb.WebMessageInfo_DELIVERY_ACK:
			m.Status = statusDelivered
		case waWeb.WebMessageInfo_READ:
			m.Status = statusRead
		case waWeb.WebMessageInfo_PLAYED:
			m.Status = statusPlayed
		}
	}
	return m
}

// stubMessage turns the notices WhatsApp keeps in a chat's history (someone
// joined, the subject changed, a call was missed) into system messages.
func (b *Bridge) stubMessage(ctx context.Context, evt *events.Message, web *waWeb.WebMessageInfo) *Message {
	info := evt.Info
	actor := "You"
	if !info.IsFromMe {
		actor = b.nameOf(ctx, b.canonical(ctx, info.Sender))
	}
	var names []string
	for _, p := range web.GetMessageStubParameters() {
		if jid, err := types.ParseJID(p); err == nil && jid.User != "" && (jid.Server == types.DefaultUserServer || jid.Server == types.HiddenUserServer) {
			if b.isOwn(jid) {
				names = append(names, "you")
			} else {
				names = append(names, b.nameOf(ctx, b.canonical(ctx, jid)))
			}
		}
	}
	params := web.GetMessageStubParameters()
	first := ""
	if len(params) > 0 {
		first = params[0]
	}

	var text string
	switch web.GetMessageStubType() {
	case waWeb.WebMessageInfo_REVOKE:
		m := b.convertRevokedStub(ctx, evt)
		return m
	case waWeb.WebMessageInfo_GROUP_CREATE:
		text = fmt.Sprintf("%s created group \"%s\"", actor, first)
	case waWeb.WebMessageInfo_GROUP_CHANGE_SUBJECT:
		text = fmt.Sprintf("%s changed the group name to \"%s\"", actor, first)
	case waWeb.WebMessageInfo_GROUP_CHANGE_ICON:
		text = actor + " changed this group's icon"
	case waWeb.WebMessageInfo_GROUP_CHANGE_DESCRIPTION:
		text = actor + " changed the group description"
	case waWeb.WebMessageInfo_GROUP_PARTICIPANT_ADD:
		text = fmt.Sprintf("%s added %s", actor, joinNames(names))
	case waWeb.WebMessageInfo_GROUP_PARTICIPANT_REMOVE:
		text = fmt.Sprintf("%s removed %s", actor, joinNames(names))
	case waWeb.WebMessageInfo_GROUP_PARTICIPANT_LEAVE:
		text = joinNames(names) + " left"
	case waWeb.WebMessageInfo_GROUP_PARTICIPANT_INVITE:
		text = joinNames(names) + " joined using an invite link"
	case waWeb.WebMessageInfo_GROUP_PARTICIPANT_PROMOTE:
		text = joinNames(names) + " is now an admin"
	case waWeb.WebMessageInfo_CALL_MISSED_VOICE:
		text = "Missed voice call"
	case waWeb.WebMessageInfo_CALL_MISSED_VIDEO:
		text = "Missed video call"
	case waWeb.WebMessageInfo_CALL_MISSED_GROUP_VOICE:
		text = "Missed group voice call"
	case waWeb.WebMessageInfo_CALL_MISSED_GROUP_VIDEO:
		text = "Missed group video call"
	case waWeb.WebMessageInfo_CHANGE_EPHEMERAL_SETTING:
		text = actor + " changed the disappearing messages setting"
	default:
		return nil
	}
	return &Message{
		Chat:      b.canonical(ctx, info.Chat).String(),
		ID:        info.ID,
		Sender:    b.canonical(ctx, info.Sender).String(),
		FromMe:    info.IsFromMe,
		TS:        info.Timestamp.Unix(),
		Kind:      "system",
		Text:      text,
		rawChat:   info.Chat.ToNonAD().String(),
		rawSender: info.Sender.ToNonAD().String(),
	}
}

func (b *Bridge) convertRevokedStub(ctx context.Context, evt *events.Message) *Message {
	info := evt.Info
	sender := b.canonical(ctx, info.Sender)
	if info.IsFromMe {
		sender = b.ownJID()
	}
	return &Message{
		Chat: b.canonical(ctx, info.Chat).String(), ID: info.ID, Sender: sender.String(), FromMe: info.IsFromMe,
		TS: info.Timestamp.Unix(), Kind: "revoked", pushName: info.PushName,
		rawChat: info.Chat.ToNonAD().String(), rawSender: info.Sender.ToNonAD().String(),
	}
}

func joinNames(names []string) string {
	switch len(names) {
	case 0:
		return "someone"
	case 1:
		return names[0]
	default:
		return strings.Join(names[:len(names)-1], ", ") + " and " + names[len(names)-1]
	}
}

// systemMessage stores a notice that happened live, such as someone joining a
// group, under an ID derived from its content so it is stored once.
func (b *Bridge) systemMessage(chat types.JID, sender types.JID, ts time.Time, text string) {
	sum := sha1.Sum([]byte(fmt.Sprintf("%s|%d|%s", chat, ts.Unix(), text)))
	m := &Message{
		Chat: chat.String(), ID: "sys-" + hex.EncodeToString(sum[:8]), Sender: sender.String(), TS: ts.Unix(),
		Kind: "system", Text: text, FromMe: b.isOwn(sender),
	}
	written, err := saveMessage(b.ctx, b.w, m)
	if err != nil || !written {
		return
	}
	_ = bumpChat(b.ctx, b.w, m.Chat, chat.Server == types.GroupServer, m.TS, m.ID, 0)
	b.reloadAndEmit(m.Chat, m.ID)
	b.emitChat(m.Chat)
}

func (b *Bridge) onGroupInfo(evt *events.GroupInfo) {
	ctx := b.ctx
	chat := evt.JID
	actor := "Someone"
	var actorJID types.JID
	if evt.Sender != nil {
		actorJID = b.canonical(ctx, *evt.Sender)
		if b.isOwn(actorJID) {
			actor = "You"
		} else {
			actor = b.nameOf(ctx, actorJID)
		}
	}
	names := func(list []types.JID) string {
		var out []string
		for _, jid := range list {
			if b.isOwn(jid) {
				out = append(out, "you")
			} else {
				out = append(out, b.nameOf(ctx, b.canonical(ctx, jid)))
			}
		}
		return joinNames(out)
	}

	if evt.Name != nil {
		_, _ = b.w.ExecContext(ctx, `UPDATE chats SET name = ? WHERE jid = ?`, evt.Name.Name, chat.String())
		b.systemMessage(chat, actorJID, evt.Timestamp, fmt.Sprintf("%s changed the group name to \"%s\"", actor, evt.Name.Name))
	}
	if len(evt.Join) > 0 {
		_, _ = b.w.ExecContext(ctx, `UPDATE chats SET members = members + ? WHERE jid = ? AND members > 0`, len(evt.Join), chat.String())
		if evt.Sender != nil && !(len(evt.Join) == 1 && evt.Join[0].User == evt.Sender.User) {
			b.systemMessage(chat, actorJID, evt.Timestamp, fmt.Sprintf("%s added %s", actor, names(evt.Join)))
		} else {
			b.systemMessage(chat, actorJID, evt.Timestamp, names(evt.Join)+" joined")
		}
	}
	if len(evt.Leave) > 0 {
		_, _ = b.w.ExecContext(ctx, `UPDATE chats SET members = MAX(members - ?, 0) WHERE jid = ?`, len(evt.Leave), chat.String())
		if evt.Sender != nil && !(len(evt.Leave) == 1 && evt.Leave[0].User == evt.Sender.User) {
			b.systemMessage(chat, actorJID, evt.Timestamp, fmt.Sprintf("%s removed %s", actor, names(evt.Leave)))
		} else {
			b.systemMessage(chat, actorJID, evt.Timestamp, names(evt.Leave)+" left")
		}
	}
	if evt.Topic != nil {
		b.systemMessage(chat, actorJID, evt.Timestamp, actor+" changed the group description")
	}
	if evt.Announce != nil {
		if !evt.Announce.IsAnnounce {
			_, _ = b.w.ExecContext(ctx, `UPDATE chats SET read_only = 0 WHERE jid = ?`, chat.String())
		} else {
			// Only admins may write now; whether that includes us needs the member list.
			go func() {
				info, err := b.cli.GetGroupInfo(b.ctx, chat)
				if err != nil {
					b.log.Warnf("Failed to fetch group %s: %v", chat, err)
					return
				}
				_, _ = b.w.ExecContext(b.ctx, `UPDATE chats SET read_only = ? WHERE jid = ?`, !isAdmin(b, info), chat.String())
				b.emitChat(chat.String())
			}()
		}
	}
	b.emitChat(chat.String())
}

func (b *Bridge) onJoinedGroup(evt *events.JoinedGroup) {
	info := evt.GroupInfo
	_, err := b.w.ExecContext(b.ctx, `
		INSERT INTO chats (jid, name, is_group, last_ts, members) VALUES (?, ?, 1, ?, ?)
		ON CONFLICT (jid) DO UPDATE SET name = excluded.name, members = excluded.members`,
		info.JID.String(), info.Name, joinedAt(info), len(info.Participants))
	if err != nil {
		b.log.Errorf("Failed to store group %s: %v", info.JID, err)
		return
	}
	b.emitChat(info.JID.String())
}

// joinedAt is the time a newly joined group sorts by until its first message.
func joinedAt(info types.GroupInfo) int64 {
	if !info.GroupCreated.IsZero() && time.Since(info.GroupCreated) < 24*time.Hour {
		return info.GroupCreated.Unix()
	}
	return time.Now().Unix()
}

// refreshGroups updates the names and member counts of all groups.
func (b *Bridge) refreshGroups(ctx context.Context) {
	groups, err := b.cli.GetJoinedGroups(ctx)
	if err != nil {
		b.log.Warnf("Failed to fetch groups: %v", err)
		return
	}
	for _, g := range groups {
		_, err := b.w.ExecContext(ctx, `UPDATE chats SET name = ?, members = ?, read_only = ? WHERE jid = ?`,
			g.Name, len(g.Participants), g.IsAnnounce && !isAdmin(b, g), g.JID.String())
		if err != nil {
			b.log.Errorf("Failed to update group %s: %v", g.JID, err)
		}
	}
	b.chatsChanged.trigger()
}

func isAdmin(b *Bridge, g *types.GroupInfo) bool {
	for _, p := range g.Participants {
		if (b.isOwn(p.JID) || b.isOwn(p.LID)) && (p.IsAdmin || p.IsSuperAdmin) {
			return true
		}
	}
	return false
}

// ensureGroupName fetches the name of a group that has none stored yet, which
// happens for a group that is new to this device.
func (b *Bridge) ensureGroupName(jid types.JID) {
	var name string
	_ = b.r.QueryRowContext(b.ctx, `SELECT name FROM chats WHERE jid = ?`, jid.String()).Scan(&name)
	if name != "" {
		return
	}
	if _, loaded := b.groupFetches.LoadOrStore(jid.String(), true); loaded {
		return
	}
	go func() {
		info, err := b.cli.GetGroupInfo(b.ctx, jid)
		if err != nil {
			b.groupFetches.Delete(jid.String())
			b.log.Warnf("Failed to fetch group %s: %v", jid, err)
			return
		}
		_, _ = b.w.ExecContext(b.ctx, `UPDATE chats SET name = ?, members = ? WHERE jid = ?`, info.Name, len(info.Participants), jid.String())
		b.emitChat(jid.String())
	}()
}
