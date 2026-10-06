package main

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"math"
	"os"
	"path/filepath"
	"sync"
	"sync/atomic"
	"time"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/store/sqlstore"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	waLog "go.mau.fi/whatsmeow/util/log"
)

// Connection states the app shows.
const (
	stateStarting   = "starting"
	stateQR         = "qr"
	stateConnecting = "connecting"
	stateSyncing    = "syncing"
	stateConnected  = "connected"
	stateLoggedOut  = "loggedOut"
	stateReplaced   = "replaced"
	stateBanned     = "banned"
	stateOutdated   = "outdated"
	stateError      = "error"
)

type Bridge struct {
	ctx     context.Context
	cancel  context.CancelFunc
	dataDir string
	log     waLog.Logger
	out     *output

	// w writes, r reads. See openDatabases.
	w *sql.DB
	r *sql.DB

	container *sqlstore.Container
	cli       *whatsmeow.Client
	handlers  map[string]handler

	stateMu  sync.Mutex
	state    string
	stateMsg string

	// Whether the app's window is in the foreground. WhatsApp sends typing
	// notifications only to clients that report themselves online, and the
	// phone stops showing notifications while a linked device is online.
	online atomic.Bool

	// LID chats that still need to move to the phone number chat once the
	// number is known. See canonical.
	lidChats sync.Map
	merges   chan [2]string

	chatsChanged  *debouncer
	historyChats  sync.Map
	groupsFetched sync.Once
	groupFetches  sync.Map

	joined joinedGroups

	mediaRetries sync.Map // message ID -> chan *events.MediaRetry
	downloads    sync.Map // chat/id -> *download
	avatarSlots  chan struct{}
}

func newBridge(dataDir string, log *fileLogger, out *output) (*Bridge, error) {
	ctx, cancel := context.WithCancel(context.Background())
	b := &Bridge{
		ctx:         ctx,
		cancel:      cancel,
		dataDir:     dataDir,
		log:         log,
		out:         out,
		state:       stateStarting,
		merges:      make(chan [2]string, 256),
		avatarSlots: make(chan struct{}, 3),
	}

	var err error
	b.w, b.r, err = openDatabases(ctx, dataDir)
	if err != nil {
		cancel()
		return nil, err
	}

	sessionPath := filepath.ToSlash(filepath.Join(dataDir, "session.db"))
	b.container, err = sqlstore.New(ctx, "sqlite",
		"file:"+sessionPath+"?_pragma=foreign_keys(1)&_pragma=busy_timeout(10000)&_pragma=journal_mode(WAL)", log.Sub("Store"))
	if err != nil {
		cancel()
		return nil, err
	}
	device, err := b.container.GetFirstDevice(ctx)
	if err != nil {
		cancel()
		return nil, err
	}

	clientLog := log.Sub("Client")
	if !log.debug {
		clientLog = &levelFilter{Logger: clientLog}
	}
	b.cli = whatsmeow.NewClient(device, clientLog)
	b.cli.EnableAutoReconnect = true
	b.cli.AutomaticMessageRerequestFromPhone = true
	// Mute, pin and archive state arrives as app state. Without this, the
	// first full sync after linking would apply it without telling us.
	b.cli.EmitAppStateEventsOnFullSync = true
	b.cli.AddEventHandler(b.onEvent)
	if device.ID != nil {
		// Names in the chat list come from the contacts. Loading them in one
		// query fills whatsmeow's cache, instead of one query per chat later.
		if _, err := device.Contacts.GetAllContacts(ctx); err != nil {
			log.Warnf("Failed to load contacts: %v", err)
		}
	}

	b.chatsChanged = newDebouncer(400*time.Millisecond, func() {
		var chats []string
		b.historyChats.Range(func(key, _ any) bool {
			chats = append(chats, key.(string))
			b.historyChats.Delete(key)
			return true
		})
		b.out.event("chats", map[string]any{"history": chats})
	})

	b.handlers = b.methods()
	b.loadLIDChats()
	go b.runMerges()
	return b, nil
}

func (b *Bridge) start() {
	b.emitState()
	b.connect()
}

// connect connects to WhatsApp. Without a session it starts linking: the QR
// codes to scan come as events.
func (b *Bridge) connect() {
	if b.cli.IsConnected() {
		return
	}
	if b.cli.Store.ID == nil {
		qrChan, err := b.cli.GetQRChannel(b.ctx)
		if err != nil {
			b.setState(stateError, err.Error())
			return
		}
		if err := b.cli.Connect(); err != nil {
			b.setState(stateError, err.Error())
			return
		}
		go func() {
			for item := range qrChan {
				switch item.Event {
				case whatsmeow.QRChannelEventCode:
					b.setState(stateQR, "")
					b.out.event("qr", map[string]any{"code": item.Code, "timeout": int(item.Timeout.Seconds())})
				case "success":
					b.setState(stateSyncing, "")
				case "timeout":
					b.setState(stateQR, "expired")
				case whatsmeow.QRChannelEventError:
					b.setState(stateQR, item.Error.Error())
				default:
					b.log.Infof("QR channel: %s", item.Event)
				}
			}
		}()
		return
	}
	b.setState(stateConnecting, "")
	if err := b.cli.Connect(); err != nil {
		b.log.Warnf("Failed to connect: %v", err)
		b.setState(stateConnecting, err.Error())
	}
}

func (b *Bridge) shutdown() {
	b.cli.Disconnect()
	b.cancel()
	b.chatsChanged.flush()
	_ = b.container.Close()
	_ = b.w.Close()
	_ = b.r.Close()
}

func (b *Bridge) setState(state, msg string) {
	b.stateMu.Lock()
	changed := b.state != state || b.stateMsg != msg
	b.state, b.stateMsg = state, msg
	b.stateMu.Unlock()
	if changed {
		b.emitState()
	}
}

func (b *Bridge) stateInfo() map[string]any {
	b.stateMu.Lock()
	info := map[string]any{"state": b.state}
	if b.stateMsg != "" {
		info["message"] = b.stateMsg
	}
	b.stateMu.Unlock()
	if id := b.cli.Store.ID; id != nil {
		info["me"] = map[string]string{"jid": id.ToNonAD().String(), "name": b.cli.Store.PushName}
	}
	return info
}

func (b *Bridge) emitState() {
	b.out.event("state", b.stateInfo())
}

func (b *Bridge) onEvent(rawEvt any) {
	defer func() {
		if p := recover(); p != nil {
			b.log.Errorf("Panic handling %T: %v", rawEvt, p)
		}
	}()

	switch evt := rawEvt.(type) {
	case *events.Connected:
		b.setState(stateConnected, "")
		go b.afterConnect()
	case *events.Disconnected:
		b.setState(stateConnecting, "")
	case *events.KeepAliveTimeout:
		b.setState(stateConnecting, "")
	case *events.KeepAliveRestored:
		b.setState(stateConnected, "")
	case *events.PairSuccess:
		b.log.Infof("Linked as %s (%s)", evt.ID, evt.Platform)
		b.setState(stateSyncing, "")
	case *events.LoggedOut:
		b.log.Warnf("Logged out: %s", evt.Reason.String())
		b.wipe()
		b.setState(stateLoggedOut, evt.Reason.String())
	case *events.StreamReplaced:
		b.setState(stateReplaced, "")
	case *events.TemporaryBan:
		b.setState(stateBanned, evt.String())
	case *events.ConnectFailure:
		b.setState(stateError, evt.Reason.String()+" "+evt.Message)
	case *events.ClientOutdated:
		b.setState(stateOutdated, "")

	case *events.Message:
		b.onMessage(evt, true)
	case *events.UndecryptableMessage:
		b.onUndecryptable(evt)
	case *events.Receipt:
		b.onReceipt(evt)
	case *events.HistorySync:
		b.onHistorySync(evt)
	case *events.OfflineSyncCompleted:
		b.chatsChanged.trigger()

	case *events.ChatPresence:
		b.out.event("typing", map[string]any{
			"chat":   b.canonical(b.ctx, evt.Chat).String(),
			"sender": b.canonical(b.ctx, evt.Sender).String(),
			"typing": evt.State == types.ChatPresenceComposing,
			"audio":  evt.Media == types.ChatPresenceMediaAudio,
		})
	case *events.Presence:
		data := map[string]any{"jid": b.canonical(b.ctx, evt.From).String(), "online": !evt.Unavailable}
		if !evt.LastSeen.IsZero() {
			data["lastSeen"] = evt.LastSeen.Unix()
		}
		b.out.event("presence", data)

	case *events.Mute:
		until := int64(0)
		if evt.Action.GetMuted() {
			until = normalizeTimestamp(evt.Action.GetMuteEndTimestamp())
			if until == 0 {
				until = -1
			}
		}
		b.updateChat(evt.JID, `muted_until = ?`, until)
	case *events.Pin:
		pinned := int64(0)
		if evt.Action.GetPinned() {
			pinned = evt.Timestamp.Unix()
		}
		b.updateChat(evt.JID, `pinned = ?`, pinned)
	case *events.Archive:
		b.updateChat(evt.JID, `archived = ?`, evt.Action.GetArchived())
	case *events.MarkChatAsRead:
		if evt.Action.GetRead() {
			b.markChatReadLocally(evt.JID)
		} else {
			b.updateChat(evt.JID, `marked_unread = 1`)
		}
	case *events.DeleteChat:
		b.deleteChat(evt.JID, false, evt.Action.GetMessageRange().GetLastMessageTimestamp())
	case *events.ClearChat:
		b.deleteChat(evt.JID, true, evt.Action.GetMessageRange().GetLastMessageTimestamp())
	case *events.DeleteForMe:
		b.deleteMessage(evt.ChatJID, evt.MessageID)
	case *events.Star:
		b.applyStar(b.canonical(b.ctx, evt.ChatJID).String(), evt.MessageID, evt.Action.GetStarred())

	case *events.Contact, *events.PushName, *events.BusinessName:
		b.chatsChanged.trigger()
	case *events.PushNameSetting:
		b.emitState()
	case *events.AppStateSyncComplete:
		b.chatsChanged.trigger()

	case *events.GroupInfo:
		b.onGroupInfo(evt)
	case *events.JoinedGroup:
		b.onJoinedGroup(evt)
	case *events.Picture:
		b.onPictureChanged(evt)
	case *events.MediaRetry:
		if ch, ok := b.mediaRetries.Load(evt.MessageID); ok {
			select {
			case ch.(chan *events.MediaRetry) <- evt:
			default:
			}
		}
	case *events.CallOffer:
		b.onCallOffer(evt)
	}
}

// afterConnect runs after every connection. The first time, it fetches the
// groups, whose names and member counts history sync does not always carry.
func (b *Bridge) afterConnect() {
	if b.online.Load() {
		_ = b.cli.SendPresence(b.ctx, types.PresenceAvailable)
	}
	b.groupsFetched.Do(func() {
		time.Sleep(2 * time.Second)
		b.refreshGroups(b.ctx)
	})
}

// wipe deletes everything that belongs to the account after it was logged
// out, as unlinking a device on the phone should.
func (b *Bridge) wipe() {
	for _, table := range []string{"chats", "messages", "reactions", "receipts", "avatars"} {
		if _, err := b.w.ExecContext(b.ctx, "DELETE FROM "+table); err != nil {
			b.log.Errorf("Failed to clear %s: %v", table, err)
		}
	}
	for _, dir := range []string{"media", "avatars", "tmp"} {
		_ = os.RemoveAll(filepath.Join(b.dataDir, dir))
	}
}

// updateChat changes columns of a chat that app state refers to.
func (b *Bridge) updateChat(jid types.JID, set string, args ...any) {
	chat := b.canonical(b.ctx, jid).String()
	_, err := b.w.ExecContext(b.ctx, `UPDATE chats SET `+set+` WHERE jid = ?`, append(args, chat)...)
	if err != nil {
		b.log.Errorf("Failed to update chat %s: %v", chat, err)
		return
	}
	b.emitChat(chat)
}

func (b *Bridge) emitChat(jid string) {
	c, err := b.loadChat(b.ctx, jid)
	if err != nil {
		b.log.Errorf("Failed to load chat %s: %v", jid, err)
		return
	}
	if c == nil {
		return
	}
	b.decorateChats(b.ctx, []*Chat{c})
	b.out.event("chat", c)
}

func (b *Bridge) emitMessage(m *Message) {
	b.decorateMessages(b.ctx, []*Message{m})
	b.out.event("message", m)
}

// reloadAndEmit sends the stored version of a message after it changed.
func (b *Bridge) reloadAndEmit(chat, id string) {
	m, err := b.loadMessage(b.ctx, chat, id)
	if err != nil || m == nil {
		return
	}
	_ = b.attachReactions(b.ctx, chat, []*Message{m})
	b.emitMessage(m)
}

// deleteChat removes the messages of a chat up to the last one the phone had
// when it was cleared or deleted there; messages that came after stay. A
// cleared chat keeps its starred messages, as the phone does by default. A
// deleted chat goes from the list when nothing is left in it.
func (b *Bridge) deleteChat(jid types.JID, keepChat bool, lastTimestamp int64) {
	chat := b.canonical(b.ctx, jid).String()
	until := normalizeTimestamp(lastTimestamp)
	if until <= 0 {
		until = math.MaxInt64
	}
	deleteMessages := `DELETE FROM messages WHERE chat = ?1 AND ts <= ?2`
	if keepChat {
		deleteMessages += ` AND starred = 0`
	}
	stmts := []string{
		deleteMessages,
		`DELETE FROM reactions WHERE chat = ?1 AND msg_id NOT IN (SELECT id FROM messages WHERE chat = ?1)`,
		`DELETE FROM receipts WHERE chat = ?1 AND msg_id NOT IN (SELECT id FROM messages WHERE chat = ?1)`,
		`DELETE FROM pins WHERE chat = ?1 AND msg_id NOT IN (SELECT id FROM messages WHERE chat = ?1)`,
		`UPDATE chats SET unread = (SELECT COUNT(*) FROM messages WHERE chat = ?1 AND unread = 1),
			last_id = COALESCE((SELECT id FROM messages WHERE chat = ?1 ORDER BY ts DESC, rowid DESC LIMIT 1), '')
			WHERE jid = ?1`,
	}
	if !keepChat {
		stmts = append(stmts, `DELETE FROM chats WHERE jid = ?1 AND NOT EXISTS (SELECT 1 FROM messages WHERE chat = ?1)`)
	}
	for _, stmt := range stmts {
		if _, err := b.w.ExecContext(b.ctx, stmt, chat, until); err != nil {
			b.log.Errorf("Failed to delete chat %s: %v", chat, err)
		}
	}
	b.out.event("cleared", map[string]string{"chat": chat})
	b.chatsChanged.trigger()
}

func (b *Bridge) deleteMessage(jid types.JID, id string) {
	chat := b.canonical(b.ctx, jid).String()
	if _, err := b.w.ExecContext(b.ctx, `DELETE FROM messages WHERE chat = ? AND id = ?`, chat, id); err != nil {
		b.log.Errorf("Failed to delete message: %v", err)
		return
	}
	if res, err := b.w.ExecContext(b.ctx, `DELETE FROM pins WHERE chat = ? AND msg_id = ?`, chat, id); err == nil {
		if n, _ := res.RowsAffected(); n > 0 {
			b.out.event("pins", map[string]string{"chat": chat})
		}
	}
	b.refreshLastMessage(chat)
	b.out.event("deleted", map[string]string{"chat": chat, "id": id})
	b.emitChat(chat)
}

// refreshLastMessage points the chat at its newest remaining message.
func (b *Bridge) refreshLastMessage(chat string) {
	_, err := b.w.ExecContext(b.ctx, `
		UPDATE chats SET last_id = COALESCE((SELECT id FROM messages WHERE chat = ?1 ORDER BY ts DESC, rowid DESC LIMIT 1), '')
		WHERE jid = ?1`, chat)
	if err != nil {
		b.log.Errorf("Failed to update the last message of %s: %v", chat, err)
	}
}

func (b *Bridge) markChatReadLocally(jid types.JID) {
	chat := b.canonical(b.ctx, jid).String()
	_, _ = b.w.ExecContext(b.ctx, `UPDATE messages SET unread = 0 WHERE chat = ? AND unread = 1`, chat)
	b.updateChat(jid, `unread = 0, marked_unread = 0`)
}

func (b *Bridge) onCallOffer(evt *events.CallOffer) {
	from := b.canonical(b.ctx, evt.CallCreator)
	if from.IsEmpty() {
		from = b.canonical(b.ctx, evt.From)
	}
	video := false
	if evt.Data != nil {
		_, video = evt.Data.GetOptionalChildByTag("video")
	}
	b.out.event("call", map[string]any{
		"from":  from.String(),
		"name":  b.nameOf(b.ctx, from),
		"video": video,
		"group": !evt.GroupJID.IsEmpty(),
	})
}

// canonical gives the JID a chat or person is stored under. WhatsApp is moving
// people from phone numbers to hidden IDs (LIDs); messages from one person
// can arrive with either. Chats are kept under the phone number whenever it
// is known, so both kinds end up in the same chat.
func (b *Bridge) canonical(ctx context.Context, jid types.JID) types.JID {
	jid = jid.ToNonAD()
	if jid.Server != types.HiddenUserServer {
		return jid
	}
	pn, err := b.cli.Store.LIDs.GetPNForLID(ctx, jid)
	if err != nil || pn.IsEmpty() {
		return jid
	}
	pn = pn.ToNonAD()
	if _, ok := b.lidChats.LoadAndDelete(jid.String()); ok {
		b.merges <- [2]string{jid.String(), pn.String()}
	}
	return pn
}

func (b *Bridge) loadLIDChats() {
	rows, err := b.r.QueryContext(b.ctx, `SELECT jid FROM chats WHERE jid LIKE '%@lid'`)
	if err != nil {
		return
	}
	defer rows.Close()
	for rows.Next() {
		var jid string
		if rows.Scan(&jid) == nil {
			b.lidChats.Store(jid, true)
		}
	}
}

// runMerges moves chats stored under a LID to the phone number chat, outside
// whatever transaction found out the number.
func (b *Bridge) runMerges() {
	for {
		select {
		case <-b.ctx.Done():
			return
		case pair := <-b.merges:
			b.mergeChat(pair[0], pair[1])
		}
	}
}

func (b *Bridge) mergeChat(from, to string) {
	ctx := b.ctx
	tx, err := b.w.BeginTx(ctx, nil)
	if err != nil {
		b.log.Errorf("Failed to merge %s into %s: %v", from, to, err)
		return
	}
	defer tx.Rollback()

	stmts := []string{
		`UPDATE OR IGNORE messages SET chat = ?2 WHERE chat = ?1`,
		`DELETE FROM messages WHERE chat = ?1`,
		`UPDATE OR IGNORE reactions SET chat = ?2 WHERE chat = ?1`,
		`DELETE FROM reactions WHERE chat = ?1`,
		`UPDATE OR IGNORE receipts SET chat = ?2 WHERE chat = ?1`,
		`DELETE FROM receipts WHERE chat = ?1`,
		`UPDATE OR IGNORE pins SET chat = ?2 WHERE chat = ?1`,
		`DELETE FROM pins WHERE chat = ?1`,
		`INSERT INTO chats (jid, name, is_group, last_ts, last_id, unread, marked_unread, muted_until, pinned, archived, read_only, members, ephemeral)
			SELECT ?2, name, is_group, last_ts, last_id, unread, marked_unread, muted_until, pinned, archived, read_only, members, ephemeral FROM chats WHERE jid = ?1
			ON CONFLICT (jid) DO UPDATE SET
				last_id = CASE WHEN excluded.last_ts > chats.last_ts THEN excluded.last_id ELSE chats.last_id END,
				last_ts = MAX(chats.last_ts, excluded.last_ts),
				unread = chats.unread + excluded.unread,
				name = CASE WHEN chats.name = '' THEN excluded.name ELSE chats.name END`,
		`DELETE FROM chats WHERE jid = ?1`,
	}
	for _, stmt := range stmts {
		if _, err := tx.ExecContext(ctx, stmt, from, to); err != nil {
			b.log.Errorf("Failed to merge %s into %s: %v", from, to, err)
			return
		}
	}
	if err := tx.Commit(); err != nil {
		b.log.Errorf("Failed to merge %s into %s: %v", from, to, err)
		return
	}
	b.log.Infof("Merged chat %s into %s", from, to)
	b.historyChats.Store(to, true)
	b.out.event("merged", map[string]string{"from": from, "to": to})
	b.chatsChanged.trigger()
}

// normalizeTimestamp turns a WhatsApp timestamp, which is in seconds in some
// places and milliseconds in others, into seconds. Negative values stay as
// they are: -1 means forever.
func normalizeTimestamp(ts int64) int64 {
	if ts > 1e12 {
		return ts / 1000
	}
	return ts
}

// debouncer runs fn once after a burst of triggers has calmed down.
type debouncer struct {
	mu    sync.Mutex
	delay time.Duration
	timer *time.Timer
	fn    func()
}

func newDebouncer(delay time.Duration, fn func()) *debouncer {
	return &debouncer{delay: delay, fn: fn}
}

func (d *debouncer) trigger() {
	d.mu.Lock()
	defer d.mu.Unlock()
	if d.timer == nil {
		d.timer = time.AfterFunc(d.delay, func() {
			d.mu.Lock()
			d.timer = nil
			d.mu.Unlock()
			d.fn()
		})
	}
}

func (d *debouncer) flush() {
	d.mu.Lock()
	t := d.timer
	d.timer = nil
	d.mu.Unlock()
	if t != nil && t.Stop() {
		d.fn()
	}
}

// levelFilter keeps the protocol library's informational chatter out of the
// log unless debugging was asked for.
type levelFilter struct {
	waLog.Logger
}

func (l *levelFilter) Infof(string, ...any)  {}
func (l *levelFilter) Debugf(string, ...any) {}
func (l *levelFilter) Sub(module string) waLog.Logger {
	return &levelFilter{Logger: l.Logger.Sub(module)}
}

var errNotLoggedIn = errors.New("not linked to a phone yet")

func mustJSON(v any) json.RawMessage {
	data, _ := json.Marshal(v)
	return data
}
