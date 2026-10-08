package main

import (
	"context"
	"testing"

	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"google.golang.org/protobuf/proto"
)

func associated(kind waE2E.MessageAssociation_AssociationType, parent string) *waE2E.MessageContextInfo {
	return &waE2E.MessageContextInfo{MessageAssociation: &waE2E.MessageAssociation{
		AssociationType:  kind.Enum(),
		ParentMessageKey: &waCommon.MessageKey{ID: proto.String(parent)},
	}}
}

func paired(kind waE2E.ContextInfo_PairedMediaType) *waE2E.ContextInfo {
	return &waE2E.ContextInfo{PairedMediaType: kind.Enum()}
}

func TestUnwrapTellsAlbumsFromWhatAddsToAPhoto(t *testing.T) {
	wrapped := func(inner *waE2E.Message, outer *waE2E.MessageContextInfo) *waE2E.Message {
		return &waE2E.Message{MessageContextInfo: outer, AssociatedChildMessage: &waE2E.FutureProofMessage{Message: inner}}
	}
	cases := []struct {
		name string
		msg  *waE2E.Message
		want relation
	}{
		{"a photo alone", &waE2E.Message{ImageMessage: &waE2E.ImageMessage{}}, relation{}},
		{"a photo of an album", &waE2E.Message{
			ImageMessage:       &waE2E.ImageMessage{},
			MessageContextInfo: associated(waE2E.MessageAssociation_MEDIA_ALBUM, "A"),
		}, relation{album: "A", parent: "A"}},
		{"a wrapped video of an album", wrapped(&waE2E.Message{
			VideoMessage:       &waE2E.VideoMessage{},
			MessageContextInfo: associated(waE2E.MessageAssociation_MEDIA_ALBUM, "A"),
		}, nil), relation{album: "A", parent: "A"}},
		{"the moving part of a motion photo", wrapped(&waE2E.Message{VideoMessage: &waE2E.VideoMessage{}},
			associated(waE2E.MessageAssociation_MOTION_PHOTO, "P")), relation{child: childMotion, parent: "P"}},
		{"a photo in high quality", wrapped(&waE2E.Message{
			ImageMessage:       &waE2E.ImageMessage{},
			MessageContextInfo: associated(waE2E.MessageAssociation_HD_IMAGE_DUAL_UPLOAD, "P"),
		}, nil), relation{child: childHD, parent: "P"}},
		{"marked only on the video", wrapped(&waE2E.Message{
			VideoMessage: &waE2E.VideoMessage{ContextInfo: paired(waE2E.ContextInfo_MOTION_PHOTO_CHILD)},
		}, associated(waE2E.MessageAssociation_UNKNOWN, "P")), relation{child: childMotion, parent: "P"}},
		{"nothing to add it to", wrapped(&waE2E.Message{
			ImageMessage: &waE2E.ImageMessage{ContextInfo: paired(waE2E.ContextInfo_HD_IMAGE_CHILD)},
		}, nil), relation{}},
	}
	for _, c := range cases {
		content, got := unwrap(c.msg)
		if got != c.want {
			t.Errorf("%s: got %+v, want %+v", c.name, got, c.want)
		}
		if content.ImageMessage == nil && content.VideoMessage == nil {
			t.Errorf("%s: the content was not unwrapped", c.name)
		}
	}
}

func TestRepairAlbumsOnce(t *testing.T) {
	ctx := context.Background()
	w, r, err := openDatabases(ctx, t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	defer w.Close()
	defer r.Close()

	photo := func(id string, ts int64, sender string, msg *waE2E.Message) {
		t.Helper()
		raw, _ := proto.Marshal(msg)
		_, err := w.ExecContext(ctx, `INSERT INTO messages (chat, id, sender, from_me, ts, kind, raw) VALUES ('c', ?, ?, 0, ?, 'image', ?)`, id, sender, ts, raw)
		if err != nil {
			t.Fatal(err)
		}
	}
	notice := func(id string, ts int64, sender string) {
		t.Helper()
		_, err := w.ExecContext(ctx, `INSERT INTO messages (chat, id, sender, from_me, ts, kind) VALUES ('c', ?, ?, 0, ?, 'unsupported')`, id, sender, ts)
		if err != nil {
			t.Fatal(err)
		}
	}
	inAlbum := associated(waE2E.MessageAssociation_MEDIA_ALBUM, "A")
	photo("1", 100, "s", &waE2E.Message{ImageMessage: &waE2E.ImageMessage{}, MessageContextInfo: inAlbum})
	photo("2", 101, "s", &waE2E.Message{
		ImageMessage:       &waE2E.ImageMessage{ContextInfo: paired(waE2E.ContextInfo_MOTION_PHOTO_PARENT)},
		MessageContextInfo: inAlbum,
	})
	photo("3", 102, "s", &waE2E.Message{ImageMessage: &waE2E.ImageMessage{}})
	// The moving part of photo 2, as an earlier version stored it.
	notice("4", 104, "s")
	// Not understood for other reasons: by someone else, a second one, and long after.
	notice("5", 105, "other")
	notice("6", 106, "s")
	notice("7", 900, "s")
	if _, err := w.ExecContext(ctx, `INSERT INTO chats (jid, last_ts, last_id) VALUES ('c', 104, '4')`); err != nil {
		t.Fatal(err)
	}

	for range 2 {
		if err := repairAlbums(ctx, w); err != nil {
			t.Fatal(err)
		}
	}

	var albums, left string
	err = w.QueryRowContext(ctx, `SELECT group_concat(id || ':' || album, ' ') FROM (SELECT id, album FROM messages WHERE kind = 'image' ORDER BY id)`).Scan(&albums)
	if err != nil {
		t.Fatal(err)
	}
	if albums != "1:A 2:A 3:" {
		t.Errorf("albums: %s", albums)
	}
	err = w.QueryRowContext(ctx, `SELECT group_concat(id, ' ') FROM (SELECT id FROM messages WHERE kind = 'unsupported' ORDER BY id)`).Scan(&left)
	if err != nil {
		t.Fatal(err)
	}
	if left != "5 6 7" {
		t.Errorf("rows left: %s", left)
	}
	var last string
	if err := w.QueryRowContext(ctx, `SELECT last_id FROM chats WHERE jid = 'c'`).Scan(&last); err != nil || last != "7" {
		t.Errorf("the chat's last message: %s, %v", last, err)
	}
}
