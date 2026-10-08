package main

import (
	"context"
	"database/sql"
	"errors"

	"go.mau.fi/whatsmeow/proto/waE2E"
	"google.golang.org/protobuf/proto"
)

// What a message can add to a photo or video sent before it.
const (
	childHD     = "hd"
	childMotion = "motion"
)

// relation is how a message belongs to another one: as a photo or video of an
// album, or as something that only adds to its parent and is not shown by itself.
type relation struct {
	// The ID of the message that announced the album.
	album string
	// childHD or childMotion, with the message it adds to.
	child  string
	parent string
}

// unwrap returns the content of a message and how it belongs to another one.
//
// Photos sent together are an album: a message that only says how many are
// coming, then the photos, each naming that message as its parent. A photo
// can also bring a message along: the same picture in high quality, sent
// after a small one, or the moving part of a motion photo. WhatsApp wraps
// those in associatedChildMessage, which whatsmeow leaves in place.
func unwrap(msg *waE2E.Message) (*waE2E.Message, relation) {
	var rel relation
	if msg == nil {
		return nil, rel
	}
	content := msg
	if inner := msg.GetAssociatedChildMessage().GetMessage(); inner != nil {
		content = inner
	}
	assoc := content.GetMessageContextInfo().GetMessageAssociation()
	if assoc == nil {
		assoc = msg.GetMessageContextInfo().GetMessageAssociation()
	}
	parent := assoc.GetParentMessageKey().GetID()

	switch assoc.GetAssociationType() {
	case waE2E.MessageAssociation_MEDIA_ALBUM:
		if content.ImageMessage != nil || content.VideoMessage != nil {
			rel.album = parent
		}
	case waE2E.MessageAssociation_HD_IMAGE_DUAL_UPLOAD, waE2E.MessageAssociation_HD_VIDEO_DUAL_UPLOAD,
		waE2E.MessageAssociation_HEVC_VIDEO_DUAL_UPLOAD:
		rel.child = childHD
	case waE2E.MessageAssociation_MOTION_PHOTO:
		rel.child = childMotion
	default:
		// Older versions only mark the photo or video itself.
		switch pairedMedia(content) {
		case waE2E.ContextInfo_HD_IMAGE_CHILD, waE2E.ContextInfo_HD_VIDEO_CHILD, waE2E.ContextInfo_HEVC_VIDEO_CHILD:
			rel.child = childHD
		case waE2E.ContextInfo_MOTION_PHOTO_CHILD:
			rel.child = childMotion
		}
	}
	if rel.child != "" && parent == "" {
		// Nothing to add it to; it shows as the photo or video it is.
		rel.child = ""
	}
	rel.parent = parent
	return content, rel
}

// pairedMedia tells whether a photo or video is one of a pair: the one shown,
// which another message adds to, or the one that adds to it.
func pairedMedia(msg *waE2E.Message) waE2E.ContextInfo_PairedMediaType {
	switch {
	case msg.GetImageMessage() != nil:
		return msg.GetImageMessage().GetContextInfo().GetPairedMediaType()
	case msg.GetVideoMessage() != nil:
		return msg.GetVideoMessage().GetContextInfo().GetPairedMediaType()
	}
	return waE2E.ContextInfo_NOT_PAIRED_MEDIA
}

// hasChild reports whether a photo or video says another message adds to it.
func hasChild(msg *waE2E.Message) bool {
	switch pairedMedia(msg) {
	case waE2E.ContextInfo_SD_IMAGE_PARENT, waE2E.ContextInfo_SD_VIDEO_PARENT, waE2E.ContextInfo_HEVC_VIDEO_PARENT,
		waE2E.ContextInfo_MOTION_PHOTO_PARENT:
		return true
	}
	return false
}

// saveChild keeps what a message adds to its parent. The high quality version
// of a photo takes the place of the small one the next time it is opened.
func (b *Bridge) saveChild(ctx context.Context, chat, id string, rel relation, content *waE2E.Message) {
	raw, err := proto.Marshal(content)
	if err != nil {
		return
	}
	_, err = b.w.ExecContext(ctx, `INSERT OR REPLACE INTO children (chat, id, parent, kind, raw) VALUES (?, ?, ?, ?, ?)`,
		chat, id, rel.parent, rel.child, raw)
	if err != nil {
		b.log.Errorf("Failed to store %s, which adds to %s: %v", id, rel.parent, err)
		return
	}
	if rel.child != childHD {
		return
	}
	// Only a file that was downloaded: one sent from here is the original.
	res, err := b.w.ExecContext(ctx, `UPDATE messages SET local_path = '' WHERE chat = ? AND id = ? AND local_path != '' AND from_me = 0`, chat, rel.parent)
	if err == nil {
		if n, _ := res.RowsAffected(); n > 0 {
			b.reloadAndEmit(chat, rel.parent)
		}
	}
}

// loadChild returns what a message of the given kind added to another one, or nil.
func (b *Bridge) loadChild(ctx context.Context, chat, parent, kind string) []byte {
	var raw []byte
	err := b.r.QueryRowContext(ctx, `SELECT raw FROM children WHERE chat = ? AND parent = ? AND kind = ? ORDER BY rowid DESC LIMIT 1`,
		chat, parent, kind).Scan(&raw)
	if err != nil {
		return nil
	}
	return raw
}

// repairAlbums brings a database from before albums were understood up to
// date, once. Photos that were stored without their album get it from the
// message they came in. And the rows saying "not supported" that an earlier
// version stored for the moving part of a motion photo, or for the high
// quality version of a photo, go: they are not messages of their own. Those
// rows kept nothing to tell them by, so they are found by the photo they
// followed: one row per such photo, from the same sender, within two minutes.
func repairAlbums(ctx context.Context, db *sql.DB) error {
	var done string
	err := db.QueryRowContext(ctx, `SELECT value FROM meta WHERE key = 'albums'`).Scan(&done)
	if err == nil {
		return nil
	}
	if !errors.Is(err, sql.ErrNoRows) {
		return err
	}

	type photo struct {
		rowid        int64
		chat, sender string
		ts           int64
		album        string
		parent       bool
	}
	var photos []photo
	rows, err := db.QueryContext(ctx, `SELECT rowid, chat, sender, ts, raw FROM messages
		WHERE raw IS NOT NULL AND kind IN ('image', 'video', 'gif') ORDER BY ts, rowid`)
	if err != nil {
		return err
	}
	for rows.Next() {
		var p photo
		var raw []byte
		if err := rows.Scan(&p.rowid, &p.chat, &p.sender, &p.ts, &raw); err != nil {
			rows.Close()
			return err
		}
		var msg waE2E.Message
		if proto.Unmarshal(raw, &msg) != nil {
			continue
		}
		content, rel := unwrap(&msg)
		p.album, p.parent = rel.album, hasChild(content)
		if p.album != "" || p.parent {
			photos = append(photos, p)
		}
	}
	rows.Close()
	if err := rows.Err(); err != nil {
		return err
	}

	tx, err := db.BeginTx(ctx, nil)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	chats := map[string]bool{}
	for _, p := range photos {
		if p.album != "" {
			if _, err := tx.ExecContext(ctx, `UPDATE messages SET album = ? WHERE rowid = ? AND album = ''`, p.album, p.rowid); err != nil {
				return err
			}
		}
		if !p.parent {
			continue
		}
		res, err := tx.ExecContext(ctx, `DELETE FROM messages WHERE rowid = (
			SELECT rowid FROM messages WHERE chat = ?1 AND sender = ?2 AND kind = 'unsupported' AND raw IS NULL
				AND ts BETWEEN ?3 AND ?3 + 120 ORDER BY ts, rowid LIMIT 1)`, p.chat, p.sender, p.ts)
		if err != nil {
			return err
		}
		if n, _ := res.RowsAffected(); n > 0 {
			chats[p.chat] = true
		}
	}
	for chat := range chats {
		_, err := tx.ExecContext(ctx, `
			UPDATE chats SET last_id = COALESCE((SELECT id FROM messages WHERE chat = ?1 ORDER BY ts DESC, rowid DESC LIMIT 1), '')
			WHERE jid = ?1`, chat)
		if err != nil {
			return err
		}
	}
	if _, err := tx.ExecContext(ctx, `INSERT INTO meta (key, value) VALUES ('albums', '1')`); err != nil {
		return err
	}
	return tx.Commit()
}
