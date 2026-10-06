package main

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"path/filepath"
	"strings"

	_ "modernc.org/sqlite"
)

// The chats database. WhatsApp's servers keep no history for linked devices,
// so everything shown in the app comes from here: what the phone sent during
// history sync, and every message since.
const schema = `
CREATE TABLE IF NOT EXISTS chats (
	jid           TEXT PRIMARY KEY,
	name          TEXT NOT NULL DEFAULT '',
	is_group      INTEGER NOT NULL DEFAULT 0,
	last_ts       INTEGER NOT NULL DEFAULT 0,
	last_id       TEXT NOT NULL DEFAULT '',
	unread        INTEGER NOT NULL DEFAULT 0,
	marked_unread INTEGER NOT NULL DEFAULT 0,
	muted_until   INTEGER NOT NULL DEFAULT 0,
	pinned        INTEGER NOT NULL DEFAULT 0,
	archived      INTEGER NOT NULL DEFAULT 0,
	read_only     INTEGER NOT NULL DEFAULT 0,
	members       INTEGER NOT NULL DEFAULT 0,
	ephemeral     INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS messages (
	chat       TEXT NOT NULL,
	id         TEXT NOT NULL,
	sender     TEXT NOT NULL,
	from_me    INTEGER NOT NULL,
	ts         INTEGER NOT NULL,
	kind       TEXT NOT NULL,
	text       TEXT NOT NULL DEFAULT '',
	media      TEXT,
	quote      TEXT,
	mentions   TEXT,
	link       TEXT,
	status     INTEGER NOT NULL DEFAULT 0,
	edited     INTEGER NOT NULL DEFAULT 0,
	unread     INTEGER NOT NULL DEFAULT 0,
	push_name  TEXT NOT NULL DEFAULT '',
	raw_chat   TEXT NOT NULL DEFAULT '',
	raw_sender TEXT NOT NULL DEFAULT '',
	raw        BLOB,
	local_path TEXT NOT NULL DEFAULT '',
	forwarded  INTEGER NOT NULL DEFAULT 0,
	kept       INTEGER NOT NULL DEFAULT 0,
	starred    INTEGER NOT NULL DEFAULT 0,
	PRIMARY KEY (chat, id)
);
CREATE INDEX IF NOT EXISTS messages_by_time ON messages (chat, ts);
CREATE INDEX IF NOT EXISTS messages_unread ON messages (chat) WHERE unread = 1;

CREATE TABLE IF NOT EXISTS reactions (
	chat   TEXT NOT NULL,
	msg_id TEXT NOT NULL,
	sender TEXT NOT NULL,
	emoji  TEXT NOT NULL,
	ts     INTEGER NOT NULL,
	PRIMARY KEY (chat, msg_id, sender)
);

-- Who has received and read each of our messages in a group. A group message
-- counts as read once every other member has read it.
CREATE TABLE IF NOT EXISTS receipts (
	chat   TEXT NOT NULL,
	msg_id TEXT NOT NULL,
	user   TEXT NOT NULL,
	status INTEGER NOT NULL,
	PRIMARY KEY (chat, msg_id, user)
);

-- Messages pinned at the top of their chat, until the pin runs out.
CREATE TABLE IF NOT EXISTS pins (
	chat    TEXT NOT NULL,
	msg_id  TEXT NOT NULL,
	sender  TEXT NOT NULL,
	ts      INTEGER NOT NULL,
	expires INTEGER NOT NULL,
	PRIMARY KEY (chat, msg_id)
);

CREATE TABLE IF NOT EXISTS avatars (
	jid        TEXT PRIMARY KEY,
	picture_id TEXT NOT NULL DEFAULT '',
	path       TEXT NOT NULL DEFAULT '',
	checked_at INTEGER NOT NULL DEFAULT 0
);
`

// Columns added since the first version, for databases made before them.
var migrations = []string{
	`ALTER TABLE messages ADD COLUMN forwarded INTEGER NOT NULL DEFAULT 0`,
	`ALTER TABLE messages ADD COLUMN kept INTEGER NOT NULL DEFAULT 0`,
	`ALTER TABLE messages ADD COLUMN starred INTEGER NOT NULL DEFAULT 0`,
}

// Message status, as the ticks show it.
const (
	statusFailed    = -1
	statusPending   = 0
	statusSent      = 1
	statusDelivered = 2
	statusRead      = 3
	statusPlayed    = 4
)

// Message is one message as the app sees it.
type Message struct {
	Chat       string            `json:"chat"`
	ID         string            `json:"id"`
	Seq        int64             `json:"seq"`
	Sender     string            `json:"sender"`
	SenderName string            `json:"senderName,omitempty"`
	FromMe     bool              `json:"fromMe"`
	TS         int64             `json:"ts"`
	Kind       string            `json:"kind"`
	Text       string            `json:"text,omitempty"`
	Media      *Media            `json:"media,omitempty"`
	Quote      *Quote            `json:"quote,omitempty"`
	Link       *Link             `json:"link,omitempty"`
	Mentions   map[string]string `json:"mentions,omitempty"`
	Reactions  []Reaction        `json:"reactions,omitempty"`
	Status     int               `json:"status"`
	Edited     bool              `json:"edited,omitempty"`
	// How often the message was forwarded before it got here; 0 when it was not.
	Forwarded int  `json:"forwarded,omitempty"`
	Kept      bool `json:"kept,omitempty"`
	Starred   bool `json:"starred,omitempty"`
	Pinned    bool `json:"pinned,omitempty"`
	Notify    bool `json:"notify,omitempty"`

	mentionJIDs []string
	unread      bool
	isEdit      bool
	pushName    string
	rawChat     string
	rawSender   string
	raw         []byte
	localPath   string
}

type Media struct {
	Mime     string  `json:"mime,omitempty"`
	Size     int64   `json:"size,omitempty"`
	Name     string  `json:"name,omitempty"`
	Width    int     `json:"w,omitempty"`
	Height   int     `json:"h,omitempty"`
	Seconds  int     `json:"secs,omitempty"`
	Pages    int     `json:"pages,omitempty"`
	Thumb    []byte  `json:"thumb,omitempty"`
	Waveform []byte  `json:"wave,omitempty"`
	Animated bool    `json:"animated,omitempty"`
	Lat      float64 `json:"lat,omitempty"`
	Lng      float64 `json:"lng,omitempty"`
	Path     string  `json:"path,omitempty"`
}

type Quote struct {
	ID         string `json:"id"`
	Sender     string `json:"sender"`
	SenderName string `json:"senderName,omitempty"`
	FromMe     bool   `json:"fromMe,omitempty"`
	Kind       string `json:"kind"`
	Text       string `json:"text,omitempty"`
}

type Link struct {
	URL         string `json:"url"`
	Title       string `json:"title,omitempty"`
	Description string `json:"description,omitempty"`
	Thumb       []byte `json:"thumb,omitempty"`
}

type Reaction struct {
	Sender string `json:"sender"`
	Name   string `json:"name,omitempty"`
	Emoji  string `json:"emoji"`
	FromMe bool   `json:"fromMe,omitempty"`
}

// Chat is one row of the chat list.
type Chat struct {
	JID          string       `json:"jid"`
	Name         string       `json:"name"`
	Group        bool         `json:"group,omitempty"`
	TS           int64        `json:"ts"`
	Unread       int          `json:"unread,omitempty"`
	MarkedUnread bool         `json:"markedUnread,omitempty"`
	MutedUntil   int64        `json:"mutedUntil,omitempty"`
	Pinned       int64        `json:"pinned,omitempty"`
	Archived     bool         `json:"archived,omitempty"`
	ReadOnly     bool         `json:"readOnly,omitempty"`
	Members      int          `json:"members,omitempty"`
	Ephemeral    int          `json:"ephemeral,omitempty"`
	Avatar       string       `json:"avatar,omitempty"`
	Last         *LastMessage `json:"last,omitempty"`

	storedName string
}

// LastMessage is the line under a chat's name in the list.
type LastMessage struct {
	ID         string `json:"id"`
	FromMe     bool   `json:"fromMe,omitempty"`
	SenderName string `json:"senderName,omitempty"`
	Kind       string `json:"kind"`
	Text       string `json:"text,omitempty"`
	Status     int    `json:"status"`
	Name       string `json:"name,omitempty"`
	Seconds    int    `json:"secs,omitempty"`

	sender   string
	pushName string
}

// openDatabases opens the chats database twice: one connection that does all
// writing, and a few read-only ones, so the app can read while a long history
// sync is being written.
func openDatabases(ctx context.Context, dataDir string) (writer, reader *sql.DB, err error) {
	path := filepath.ToSlash(filepath.Join(dataDir, "chats.db"))
	dsn := "file:" + path + "?_pragma=busy_timeout(10000)&_pragma=journal_mode(WAL)&_pragma=synchronous(NORMAL)&_txlock=immediate"

	writer, err = sql.Open("sqlite", dsn)
	if err != nil {
		return nil, nil, err
	}
	writer.SetMaxOpenConns(1)
	if _, err = writer.ExecContext(ctx, schema); err != nil {
		writer.Close()
		return nil, nil, fmt.Errorf("failed to create the chats database: %w", err)
	}
	for _, stmt := range migrations {
		if _, err = writer.ExecContext(ctx, stmt); err != nil && !strings.Contains(err.Error(), "duplicate column") {
			writer.Close()
			return nil, nil, fmt.Errorf("failed to update the chats database: %w", err)
		}
	}

	reader, err = sql.Open("sqlite", dsn+"&_pragma=query_only(1)")
	if err != nil {
		writer.Close()
		return nil, nil, err
	}
	reader.SetMaxOpenConns(4)
	return writer, reader, nil
}

type execer interface {
	ExecContext(ctx context.Context, query string, args ...any) (sql.Result, error)
	QueryRowContext(ctx context.Context, query string, args ...any) *sql.Row
}

func marshalOrNil(v any, isNil bool) any {
	if isNil {
		return nil
	}
	data, err := json.Marshal(v)
	if err != nil {
		return nil
	}
	return string(data)
}

// saveMessage stores a message. A message that is already stored is left as
// it is, unless the stored one was only a placeholder for a message that could
// not be decrypted yet. Reports whether anything was written.
func saveMessage(ctx context.Context, db execer, m *Message) (bool, error) {
	var mentions any
	if len(m.mentionJIDs) > 0 {
		mentions = marshalOrNil(m.mentionJIDs, false)
	}
	res, err := db.ExecContext(ctx, `
		INSERT INTO messages (chat, id, sender, from_me, ts, kind, text, media, quote, mentions, link, status, edited, unread, push_name, raw_chat, raw_sender, raw, local_path, forwarded, kept, starred)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
		ON CONFLICT (chat, id) DO UPDATE SET
			kind = excluded.kind, text = excluded.text, media = excluded.media, quote = excluded.quote,
			mentions = excluded.mentions, link = excluded.link, raw = excluded.raw, sender = excluded.sender, forwarded = excluded.forwarded,
			push_name = excluded.push_name, unread = excluded.unread, status = MAX(messages.status, excluded.status)
		WHERE messages.kind = 'pending'`,
		m.Chat, m.ID, m.Sender, m.FromMe, m.TS, m.Kind, m.Text,
		marshalOrNil(m.Media, m.Media == nil), marshalOrNil(m.Quote, m.Quote == nil), mentions, marshalOrNil(m.Link, m.Link == nil),
		m.Status, m.Edited, m.unread, m.pushName, m.rawChat, m.rawSender, m.raw, m.localPath, m.Forwarded, m.Kept, m.Starred)
	if err != nil {
		return false, err
	}
	n, _ := res.RowsAffected()
	return n > 0, nil
}

// bumpChat creates the chat if needed and makes the message its last one when
// it is the newest.
func bumpChat(ctx context.Context, db execer, chat string, isGroup bool, ts int64, id string, addUnread int) error {
	_, err := db.ExecContext(ctx, `
		INSERT INTO chats (jid, is_group, last_ts, last_id, unread) VALUES (?, ?, ?, ?, ?)
		ON CONFLICT (jid) DO UPDATE SET
			last_id = CASE WHEN excluded.last_ts >= chats.last_ts OR chats.last_id = '' THEN excluded.last_id ELSE chats.last_id END,
			last_ts = MAX(chats.last_ts, excluded.last_ts),
			unread = chats.unread + excluded.unread`,
		chat, isGroup, ts, id, addUnread)
	return err
}

const messageColumns = `m.rowid, m.chat, m.id, m.sender, m.from_me, m.ts, m.kind, m.text, m.media, m.quote, m.mentions, m.link, m.status, m.edited, m.unread, m.push_name, m.raw_chat, m.raw_sender, m.local_path,
	m.forwarded, m.kept, m.starred, EXISTS (SELECT 1 FROM pins p WHERE p.chat = m.chat AND p.msg_id = m.id AND p.expires > unixepoch())`

type scanner interface {
	Scan(dest ...any) error
}

func scanMessage(row scanner) (*Message, error) {
	var m Message
	var media, quote, mentions, link sql.NullString
	err := row.Scan(&m.Seq, &m.Chat, &m.ID, &m.Sender, &m.FromMe, &m.TS, &m.Kind, &m.Text, &media, &quote, &mentions, &link,
		&m.Status, &m.Edited, &m.unread, &m.pushName, &m.rawChat, &m.rawSender, &m.localPath, &m.Forwarded, &m.Kept, &m.Starred, &m.Pinned)
	if err != nil {
		return nil, err
	}
	if media.Valid {
		m.Media = &Media{}
		_ = json.Unmarshal([]byte(media.String), m.Media)
	}
	if quote.Valid {
		m.Quote = &Quote{}
		_ = json.Unmarshal([]byte(quote.String), m.Quote)
	}
	if mentions.Valid {
		_ = json.Unmarshal([]byte(mentions.String), &m.mentionJIDs)
	}
	if link.Valid {
		m.Link = &Link{}
		_ = json.Unmarshal([]byte(link.String), m.Link)
	}
	if m.localPath != "" {
		if m.Media == nil {
			m.Media = &Media{}
		}
		m.Media.Path = m.localPath
	}
	return &m, nil
}

func (b *Bridge) loadMessage(ctx context.Context, chat, id string) (*Message, error) {
	row := b.r.QueryRowContext(ctx, `SELECT `+messageColumns+` FROM messages m WHERE m.chat = ? AND m.id = ?`, chat, id)
	m, err := scanMessage(row)
	if errors.Is(err, sql.ErrNoRows) {
		return nil, nil
	}
	return m, err
}

func (b *Bridge) loadRaw(ctx context.Context, chat, id string) ([]byte, error) {
	var raw []byte
	err := b.r.QueryRowContext(ctx, `SELECT raw FROM messages WHERE chat = ? AND id = ?`, chat, id).Scan(&raw)
	return raw, err
}

func (b *Bridge) queryMessages(ctx context.Context, query string, args ...any) ([]*Message, error) {
	rows, err := b.r.QueryContext(ctx, query, args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []*Message
	for rows.Next() {
		m, err := scanMessage(rows)
		if err != nil {
			return nil, err
		}
		list = append(list, m)
	}
	return list, rows.Err()
}

// attachReactions fills in the reactions of the given messages of one chat.
func (b *Bridge) attachReactions(ctx context.Context, chat string, list []*Message) error {
	if len(list) == 0 {
		return nil
	}
	byID := make(map[string]*Message, len(list))
	args := make([]any, 0, len(list)+1)
	args = append(args, chat)
	for _, m := range list {
		byID[m.ID] = m
		args = append(args, m.ID)
	}
	placeholders := strings.TrimSuffix(strings.Repeat("?,", len(list)), ",")
	rows, err := b.r.QueryContext(ctx,
		`SELECT msg_id, sender, emoji FROM reactions WHERE chat = ? AND msg_id IN (`+placeholders+`) ORDER BY ts`, args...)
	if err != nil {
		return err
	}
	defer rows.Close()
	for rows.Next() {
		var id string
		var r Reaction
		if err := rows.Scan(&id, &r.Sender, &r.Emoji); err != nil {
			return err
		}
		if m := byID[id]; m != nil {
			m.Reactions = append(m.Reactions, r)
		}
	}
	return rows.Err()
}

func (b *Bridge) loadChat(ctx context.Context, jid string) (*Chat, error) {
	list, err := b.queryChats(ctx, `WHERE c.jid = ?`, jid)
	if err != nil || len(list) == 0 {
		return nil, err
	}
	return list[0], nil
}

func (b *Bridge) queryChats(ctx context.Context, where string, args ...any) ([]*Chat, error) {
	rows, err := b.r.QueryContext(ctx, `
		SELECT c.jid, c.name, c.is_group, c.last_ts, c.unread, c.marked_unread, c.muted_until, c.pinned, c.archived,
			c.read_only, c.members, c.ephemeral, COALESCE(a.path, ''),
			m.id, m.sender, m.from_me, m.kind, m.text, m.status, m.media, m.push_name
		FROM chats c
		LEFT JOIN messages m ON m.chat = c.jid AND m.id = c.last_id
		LEFT JOIN avatars a ON a.jid = c.jid
		`+where+`
		ORDER BY c.pinned DESC, c.last_ts DESC`, args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var list []*Chat
	for rows.Next() {
		var c Chat
		var lastID, lastSender, lastKind, lastText, lastMedia, lastPush sql.NullString
		var lastFromMe sql.NullBool
		var lastStatus sql.NullInt64
		err := rows.Scan(&c.JID, &c.storedName, &c.Group, &c.TS, &c.Unread, &c.MarkedUnread, &c.MutedUntil, &c.Pinned, &c.Archived,
			&c.ReadOnly, &c.Members, &c.Ephemeral, &c.Avatar,
			&lastID, &lastSender, &lastFromMe, &lastKind, &lastText, &lastStatus, &lastMedia, &lastPush)
		if err != nil {
			return nil, err
		}
		if lastID.Valid {
			last := &LastMessage{
				ID:     lastID.String,
				FromMe: lastFromMe.Bool,
				Kind:   lastKind.String,
				Text:   truncate(lastText.String, 160),
				Status: int(lastStatus.Int64),
			}
			if lastMedia.Valid {
				var media Media
				if json.Unmarshal([]byte(lastMedia.String), &media) == nil {
					last.Name = media.Name
					last.Seconds = media.Seconds
				}
			}
			if c.Group && !last.FromMe {
				last.sender = lastSender.String
				last.pushName = lastPush.String
			}
			c.Last = last
		}
		list = append(list, &c)
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}
	return list, nil
}

// truncate shortens s to at most n runes.
func truncate(s string, n int) string {
	if len(s) <= n {
		return s
	}
	i := 0
	for pos := range s {
		if i == n {
			return s[:pos]
		}
		i++
	}
	return s
}
