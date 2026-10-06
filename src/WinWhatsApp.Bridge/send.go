package main

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"time"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/appstate"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/types"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

type sendParams struct {
	Chat    string `json:"chat"`
	Text    string `json:"text"`
	ReplyTo string `json:"replyTo"`
	// The preview of a link in the text, as linkPreview made it.
	Link *Link `json:"link"`
}

type sendMediaParams struct {
	Chat       string `json:"chat"`
	Path       string `json:"path"`
	Caption    string `json:"caption"`
	ReplyTo    string `json:"replyTo"`
	AsDocument bool   `json:"asDocument"`
	// Filled in by the app for videos, which Go cannot decode: a preview
	// frame, the size and the length.
	Thumb   []byte `json:"thumb"`
	Width   int    `json:"width"`
	Height  int    `json:"height"`
	Seconds int    `json:"seconds"`
}

func (b *Bridge) newOutgoing(chat types.JID, kind, text string) *Message {
	own := b.ownJID().String()
	return &Message{
		Chat:      chat.String(),
		ID:        string(b.cli.GenerateMessageID()),
		Sender:    own,
		FromMe:    true,
		TS:        time.Now().Unix(),
		Kind:      kind,
		Text:      text,
		Status:    statusPending,
		rawChat:   chat.String(),
		rawSender: own,
	}
}

// replyContext builds what a new message carries about the chat: the message
// it replies to and the disappearing messages timer.
func (b *Bridge) replyContext(ctx context.Context, chat types.JID, replyTo string) (*waE2E.ContextInfo, *Quote) {
	ci := &waE2E.ContextInfo{}
	used := false

	var ephemeral uint32
	_ = b.r.QueryRowContext(ctx, `SELECT ephemeral FROM chats WHERE jid = ?`, chat.String()).Scan(&ephemeral)
	if ephemeral > 0 {
		ci.Expiration = proto.Uint32(ephemeral)
		used = true
	}

	var quote *Quote
	if replyTo != "" {
		if target, err := b.loadMessage(ctx, chat.String(), replyTo); err == nil && target != nil {
			quoted := &waE2E.Message{Conversation: proto.String(target.Text)}
			if raw, err := b.loadRaw(ctx, chat.String(), replyTo); err == nil && len(raw) > 0 {
				var original waE2E.Message
				if proto.Unmarshal(raw, &original) == nil {
					quoted = withoutContext(&original)
				}
			}
			participant := target.rawSender
			if participant == "" {
				participant = target.Sender
			}
			ci.StanzaID = proto.String(target.ID)
			ci.Participant = proto.String(participant)
			ci.QuotedMessage = quoted
			used = true

			qText := target.Text
			if qText == "" && target.Media != nil {
				qText = target.Media.Name
			}
			quote = &Quote{ID: target.ID, Sender: target.Sender, Kind: target.Kind, Text: truncate(qText, 300)}
		}
	}
	if !used {
		return nil, nil
	}
	return ci, quote
}

// withoutContext copies a message for quoting, without the message it
// replied to itself, so replies to replies do not nest.
func withoutContext(msg *waE2E.Message) *waE2E.Message {
	clone := proto.Clone(msg).(*waE2E.Message)
	clone.MessageContextInfo = nil
	switch {
	case clone.ExtendedTextMessage != nil:
		clone.ExtendedTextMessage.ContextInfo = nil
	case clone.ImageMessage != nil:
		clone.ImageMessage.ContextInfo = nil
	case clone.VideoMessage != nil:
		clone.VideoMessage.ContextInfo = nil
	case clone.AudioMessage != nil:
		clone.AudioMessage.ContextInfo = nil
	case clone.DocumentMessage != nil:
		clone.DocumentMessage.ContextInfo = nil
	case clone.StickerMessage != nil:
		clone.StickerMessage.ContextInfo = nil
	}
	return clone
}

func (b *Bridge) sendText(ctx context.Context, p sendParams) (*Message, error) {
	chat, err := types.ParseJID(p.Chat)
	if err != nil {
		return nil, err
	}
	if strings.TrimSpace(p.Text) == "" {
		return nil, errors.New("nothing to send")
	}
	m := b.newOutgoing(chat, "text", p.Text)
	ci, quote := b.replyContext(ctx, chat, p.ReplyTo)
	m.Quote = quote
	if l := p.Link; l != nil && l.URL != "" && strings.Contains(p.Text, l.URL) && (l.Title != "" || l.Description != "") {
		m.Link = l
	}

	msg := &waE2E.Message{Conversation: proto.String(p.Text)}
	if ci != nil || m.Link != nil {
		ext := &waE2E.ExtendedTextMessage{Text: proto.String(p.Text), ContextInfo: ci}
		if m.Link != nil {
			// The receiving apps find the link in the text by the matched text.
			ext.MatchedText = proto.String(m.Link.URL)
			ext.Title = optional(m.Link.Title)
			ext.Description = optional(m.Link.Description)
			ext.JPEGThumbnail = m.Link.Thumb
			ext.PreviewType = waE2E.ExtendedTextMessage_NONE.Enum()
		}
		msg = &waE2E.Message{ExtendedTextMessage: ext}
	}
	return b.dispatch(ctx, chat, m, func(context.Context, *Message) (*waE2E.Message, error) { return msg, nil })
}

func (b *Bridge) sendMedia(ctx context.Context, p sendMediaParams) (*Message, error) {
	chat, err := types.ParseJID(p.Chat)
	if err != nil {
		return nil, err
	}
	info, err := os.Stat(p.Path)
	if err != nil {
		return nil, err
	}
	if info.IsDir() {
		return nil, errors.New("folders cannot be sent")
	}
	mimeType := mimeFor(p.Path)
	name := filepath.Base(p.Path)

	kind := "document"
	var img preparedImage
	if !p.AsDocument {
		switch {
		case strings.HasPrefix(mimeType, "image/") && mimeType != "image/gif" && mimeType != "image/svg+xml":
			var ok bool
			if img, ok = prepareImage(p.Path); ok {
				kind = "image"
				mimeType = "image/jpeg"
			}
		case mimeType == "video/mp4" && info.Size() < 100<<20:
			kind = "video"
		case isPlayableAudio(mimeType):
			kind = "audio"
		}
	}

	caption := p.Caption
	if kind == "audio" {
		caption = ""
	}
	m := b.newOutgoing(chat, kind, caption)
	m.localPath = p.Path
	m.Media = &Media{Mime: mimeType, Size: info.Size(), Width: p.Width, Height: p.Height, Seconds: p.Seconds, Thumb: p.Thumb}
	switch kind {
	case "image":
		m.Media.Width, m.Media.Height, m.Media.Thumb = img.width, img.height, img.thumb
		m.Media.Size = int64(len(img.data))
	case "document":
		m.Media.Name = name
	}
	ci, quote := b.replyContext(ctx, chat, p.ReplyTo)
	m.Quote = quote

	build := func(ctx context.Context, m *Message) (*waE2E.Message, error) {
		var upload whatsmeow.UploadResponse
		var err error
		switch kind {
		case "image":
			upload, err = b.cli.Upload(ctx, img.data, whatsmeow.MediaImage)
		default:
			mediaType := whatsmeow.MediaDocument
			switch kind {
			case "video":
				mediaType = whatsmeow.MediaVideo
			case "audio":
				mediaType = whatsmeow.MediaAudio
			}
			var f *os.File
			if f, err = os.Open(p.Path); err != nil {
				return nil, err
			}
			defer f.Close()
			upload, err = b.cli.UploadReader(ctx, f, nil, mediaType)
		}
		if err != nil {
			return nil, err
		}

		now := proto.Int64(time.Now().Unix())
		switch kind {
		case "image":
			return &waE2E.Message{ImageMessage: &waE2E.ImageMessage{
				URL: &upload.URL, DirectPath: &upload.DirectPath, MediaKey: upload.MediaKey,
				FileEncSHA256: upload.FileEncSHA256, FileSHA256: upload.FileSHA256, FileLength: &upload.FileLength,
				Mimetype: proto.String("image/jpeg"), Width: proto.Uint32(uint32(img.width)), Height: proto.Uint32(uint32(img.height)),
				JPEGThumbnail: img.thumb, Caption: optional(caption), ContextInfo: ci, MediaKeyTimestamp: now,
			}}, nil
		case "video":
			return &waE2E.Message{VideoMessage: &waE2E.VideoMessage{
				URL: &upload.URL, DirectPath: &upload.DirectPath, MediaKey: upload.MediaKey,
				FileEncSHA256: upload.FileEncSHA256, FileSHA256: upload.FileSHA256, FileLength: &upload.FileLength,
				Mimetype: proto.String("video/mp4"), Seconds: proto.Uint32(uint32(p.Seconds)),
				Width: proto.Uint32(uint32(p.Width)), Height: proto.Uint32(uint32(p.Height)),
				JPEGThumbnail: p.Thumb, Caption: optional(caption), ContextInfo: ci, MediaKeyTimestamp: now,
			}}, nil
		case "audio":
			return &waE2E.Message{AudioMessage: &waE2E.AudioMessage{
				URL: &upload.URL, DirectPath: &upload.DirectPath, MediaKey: upload.MediaKey,
				FileEncSHA256: upload.FileEncSHA256, FileSHA256: upload.FileSHA256, FileLength: &upload.FileLength,
				Mimetype: proto.String(mimeType), Seconds: proto.Uint32(uint32(p.Seconds)), PTT: proto.Bool(false),
				ContextInfo: ci, MediaKeyTimestamp: now,
			}}, nil
		}
		doc := &waE2E.DocumentMessage{
			URL: &upload.URL, DirectPath: &upload.DirectPath, MediaKey: upload.MediaKey,
			FileEncSHA256: upload.FileEncSHA256, FileSHA256: upload.FileSHA256, FileLength: &upload.FileLength,
			Mimetype: proto.String(mimeType), FileName: proto.String(name), Title: proto.String(name),
			Caption: optional(caption), ContextInfo: ci, MediaKeyTimestamp: now,
		}
		if caption != "" {
			return &waE2E.Message{DocumentWithCaptionMessage: &waE2E.FutureProofMessage{
				Message: &waE2E.Message{DocumentMessage: doc},
			}}, nil
		}
		return &waE2E.Message{DocumentMessage: doc}, nil
	}
	return b.dispatch(ctx, chat, m, build)
}

// isPlayableAudio reports whether WhatsApp's apps play a file of this type in
// the chat. Other audio files, such as WAV, go as documents.
func isPlayableAudio(mimeType string) bool {
	base := strings.TrimSpace(strings.SplitN(mimeType, ";", 2)[0])
	switch base {
	case "audio/mpeg", "audio/mp4", "audio/aac", "audio/ogg", "audio/amr":
		return true
	}
	return false
}

func optional(s string) *string {
	if s == "" {
		return nil
	}
	return &s
}

// dispatch stores an outgoing message right away, so it shows with a clock,
// and sends it in the background. build makes the message to send; for media
// that includes the upload.
func (b *Bridge) dispatch(ctx context.Context, chat types.JID, m *Message, build func(context.Context, *Message) (*waE2E.Message, error)) (*Message, error) {
	if b.cli.Store.ID == nil {
		return nil, errNotLoggedIn
	}
	if _, err := saveMessage(ctx, b.w, m); err != nil {
		return nil, err
	}
	if err := bumpChat(ctx, b.w, m.Chat, chat.Server == types.GroupServer, m.TS, m.ID, 0); err != nil {
		return nil, err
	}
	stored, err := b.loadMessage(ctx, m.Chat, m.ID)
	if err != nil || stored == nil {
		return nil, errors.New("failed to store the message")
	}
	b.decorateMessages(ctx, []*Message{stored})
	b.emitChat(m.Chat)

	go b.deliver(chat, m, build)
	return stored, nil
}

func (b *Bridge) deliver(chat types.JID, m *Message, build func(context.Context, *Message) (*waE2E.Message, error)) {
	ctx := b.ctx
	msg, err := build(ctx, m)
	if err == nil {
		if raw, mErr := proto.Marshal(msg); mErr == nil {
			_, _ = b.w.ExecContext(ctx, `UPDATE messages SET raw = ? WHERE chat = ? AND id = ?`, raw, m.Chat, m.ID)
		}
		_, err = b.cli.SendMessage(ctx, chat, msg, whatsmeow.SendRequestExtra{ID: types.MessageID(m.ID)})
	}
	if err != nil {
		b.log.Errorf("Failed to send %s to %s: %v", m.ID, m.Chat, err)
		_, _ = b.w.ExecContext(ctx, `UPDATE messages SET status = ? WHERE chat = ? AND id = ? AND status <= 0`, statusFailed, m.Chat, m.ID)
		b.out.event("sendFailed", map[string]string{"chat": m.Chat, "id": m.ID, "message": err.Error()})
	} else {
		_, _ = b.w.ExecContext(ctx, `UPDATE messages SET status = MAX(status, ?) WHERE chat = ? AND id = ?`, statusSent, m.Chat, m.ID)
	}
	b.reloadAndEmit(m.Chat, m.ID)
	b.emitChatIfLast(m.Chat, []string{m.ID})
}

// retry sends a message that failed again, with the same ID.
func (b *Bridge) retry(ctx context.Context, chatText, id string) error {
	chat, err := types.ParseJID(chatText)
	if err != nil {
		return err
	}
	m, err := b.loadMessage(ctx, chatText, id)
	if err != nil || m == nil || !m.FromMe {
		return errors.New("message not found")
	}
	raw, err := b.loadRaw(ctx, chatText, id)
	if err != nil || len(raw) == 0 {
		return errors.New("this message can't be sent again; send it anew")
	}
	var msg waE2E.Message
	if err := proto.Unmarshal(raw, &msg); err != nil {
		return err
	}
	_, _ = b.w.ExecContext(ctx, `UPDATE messages SET status = ? WHERE chat = ? AND id = ?`, statusPending, chatText, id)
	b.reloadAndEmit(chatText, id)
	go b.deliver(chat, m, func(context.Context, *Message) (*waE2E.Message, error) { return &msg, nil })
	return nil
}

// target resolves the chat and sender a reaction, edit or deletion refers to,
// as the message was addressed when it arrived.
func (b *Bridge) target(ctx context.Context, chatText, id string) (types.JID, types.JID, types.JID, *Message, error) {
	chat, err := types.ParseJID(chatText)
	if err != nil {
		return chat, chat, chat, nil, err
	}
	m, err := b.loadMessage(ctx, chatText, id)
	if err != nil || m == nil {
		return chat, chat, chat, nil, errors.New("message not found")
	}
	keyChat := chat
	if parsed, err := types.ParseJID(m.rawChat); err == nil && !parsed.IsEmpty() {
		keyChat = parsed
	}
	sender := types.EmptyJID
	if !m.FromMe {
		if parsed, err := types.ParseJID(m.rawSender); err == nil {
			sender = parsed
		}
	}
	return chat, keyChat, sender, m, nil
}

func (b *Bridge) react(ctx context.Context, chatText, id, emoji string) error {
	chat, keyChat, sender, _, err := b.target(ctx, chatText, id)
	if err != nil {
		return err
	}
	msg := b.cli.BuildReaction(keyChat, sender, types.MessageID(id), emoji)
	if _, err := b.cli.SendMessage(ctx, chat, msg); err != nil {
		return err
	}
	b.applyReaction(chatText, b.ownJID().String(), id, emoji, time.Now().Unix())
	return nil
}

func (b *Bridge) edit(ctx context.Context, chatText, id, text string) error {
	chat, keyChat, _, m, err := b.target(ctx, chatText, id)
	if err != nil {
		return err
	}
	if !m.FromMe || m.Kind != "text" {
		return errors.New("only your own text messages can be edited")
	}
	if time.Since(time.Unix(m.TS, 0)) > whatsmeow.EditWindow {
		return errors.New("messages can only be edited for 20 minutes after sending")
	}
	content := &waE2E.Message{Conversation: proto.String(text)}
	if _, err := b.cli.SendMessage(ctx, chat, b.cli.BuildEdit(keyChat, types.MessageID(id), content)); err != nil {
		return err
	}
	b.applyEdit(chatText, id, content)
	return nil
}

func (b *Bridge) revokeOwn(ctx context.Context, chatText, id string) error {
	chat, keyChat, sender, m, err := b.target(ctx, chatText, id)
	if err != nil {
		return err
	}
	if !m.FromMe && chat.Server != types.GroupServer {
		return errors.New("only your own messages can be deleted for everyone")
	}
	if _, err := b.cli.SendMessage(ctx, chat, b.cli.BuildRevoke(keyChat, sender, types.MessageID(id))); err != nil {
		return err
	}
	b.revoke(chatText, id)
	return nil
}

// pin pins a message at the top of its chat for everyone, for the given
// number of seconds, or unpins it.
func (b *Bridge) pin(ctx context.Context, chatText, id string, pinned bool, seconds int64) error {
	chat, keyChat, sender, m, err := b.target(ctx, chatText, id)
	if err != nil {
		return err
	}
	if !canAddOn(m) {
		return errors.New("this message can't be pinned")
	}
	pinType := waE2E.PinInChatMessage_UNPIN_FOR_ALL
	if pinned {
		pinType = waE2E.PinInChatMessage_PIN_FOR_ALL
		if seconds <= 0 {
			seconds = defaultPinSeconds
		}
	}
	now := time.Now()
	msg := &waE2E.Message{PinInChatMessage: &waE2E.PinInChatMessage{
		Key:               b.cli.BuildMessageKey(keyChat, sender, types.MessageID(id)),
		Type:              pinType.Enum(),
		SenderTimestampMS: proto.Int64(now.UnixMilli()),
	}}
	if pinned {
		msg.MessageContextInfo = &waE2E.MessageContextInfo{MessageAddOnDurationInSecs: proto.Uint32(uint32(seconds))}
	}
	if _, err := b.cli.SendMessage(ctx, chat, msg); err != nil {
		return err
	}
	b.applyPin(chatText, id, b.ownJID().String(), pinned, now.Unix(), seconds)
	return nil
}

// keep keeps a disappearing message in the chat for everyone, or lets it go
// again, which only its sender may.
func (b *Bridge) keep(ctx context.Context, chatText, id string, kept bool) error {
	chat, keyChat, sender, m, err := b.target(ctx, chatText, id)
	if err != nil {
		return err
	}
	if !canAddOn(m) {
		return errors.New("this message can't be kept")
	}
	if !kept && !m.FromMe {
		return errors.New("only who sent a message can stop keeping it")
	}
	keepType := waE2E.KeepType_UNDO_KEEP_FOR_ALL
	if kept {
		keepType = waE2E.KeepType_KEEP_FOR_ALL
	}
	msg := &waE2E.Message{KeepInChatMessage: &waE2E.KeepInChatMessage{
		Key:         b.cli.BuildMessageKey(keyChat, sender, types.MessageID(id)),
		KeepType:    keepType.Enum(),
		TimestampMS: proto.Int64(time.Now().UnixMilli()),
	}}
	if _, err := b.cli.SendMessage(ctx, chat, msg); err != nil {
		return err
	}
	b.applyKeep(chatText, id, kept)
	return nil
}

// star stars a message or takes the star away, on every linked device.
func (b *Bridge) star(ctx context.Context, chatText, id string, starred bool) error {
	_, keyChat, sender, m, err := b.target(ctx, chatText, id)
	if err != nil {
		return err
	}
	if m.FromMe {
		// BuildStar writes "0" for the sender when it is the chat itself,
		// which is what WhatsApp expects for one's own messages.
		sender = keyChat
	}
	if err := b.cli.SendAppState(ctx, appstate.BuildStar(keyChat, sender, types.MessageID(id), m.FromMe, starred)); err != nil {
		return err
	}
	b.applyStar(chatText, id, starred)
	return nil
}

// canAddOn reports whether a message can be pinned or kept: one that is there
// to see, and sent.
func canAddOn(m *Message) bool {
	switch m.Kind {
	case "revoked", "system", "pending", "viewonce", "unsupported":
		return false
	}
	return !m.FromMe || m.Status > statusPending
}

// forward sends a copy of a message to other chats, marked as forwarded.
// Media goes along without uploading it again.
func (b *Bridge) forward(ctx context.Context, chatText, id string, to []string) ([]*Message, error) {
	source, err := b.loadMessage(ctx, chatText, id)
	if err != nil || source == nil {
		return nil, errors.New("message not found")
	}
	switch source.Kind {
	case "revoked", "system", "pending", "viewonce", "unsupported", "poll":
		return nil, errors.New("this message can't be forwarded")
	}
	raw, err := b.loadRaw(ctx, chatText, id)
	if err != nil || len(raw) == 0 {
		return nil, errors.New("this message can't be forwarded")
	}
	var original waE2E.Message
	if err := proto.Unmarshal(raw, &original); err != nil {
		return nil, err
	}
	// WhatsApp counts how often a message travelled, to mark ones forwarded many times.
	score := uint32(0)
	if source.Forwarded > 0 {
		score = uint32(source.Forwarded)
	}
	if !source.FromMe || source.Forwarded > 0 {
		score++
	}

	var sent []*Message
	for _, target := range to {
		chat, err := types.ParseJID(target)
		if err != nil {
			return sent, err
		}
		ci, _ := b.replyContext(ctx, chat, "")
		if ci == nil {
			ci = &waE2E.ContextInfo{}
		}
		if score > 0 {
			ci.IsForwarded = proto.Bool(true)
			ci.ForwardingScore = proto.Uint32(score)
		}
		msg := forwardCopy(&original, ci)

		m := b.newOutgoing(chat, source.Kind, source.Text)
		m.Media = source.Media
		if m.Media != nil {
			m.Media.Path = ""
		}
		m.localPath = source.localPath
		m.Link = source.Link
		m.Forwarded = int(score)
		stored, err := b.dispatch(ctx, chat, m, func(context.Context, *Message) (*waE2E.Message, error) { return msg, nil })
		if err != nil {
			return sent, err
		}
		sent = append(sent, stored)
	}
	return sent, nil
}

// forwardCopy copies a message to send it on: without the message it replied
// to, the people it mentioned and its secret, and with the given context.
func forwardCopy(original *waE2E.Message, ci *waE2E.ContextInfo) *waE2E.Message {
	msg := proto.Clone(original).(*waE2E.Message)
	msg.MessageContextInfo = nil
	if msg.Conversation != nil {
		// Plain text has no room for the context.
		msg = &waE2E.Message{ExtendedTextMessage: &waE2E.ExtendedTextMessage{Text: msg.Conversation}}
	}
	setContext(msg.ProtoReflect(), ci)
	return msg
}

// setContext replaces the context of the content of a message, also of
// content wrapped in another message, as a document with a caption is.
func setContext(msg protoreflect.Message, ci *waE2E.ContextInfo) bool {
	done := false
	msg.Range(func(fd protoreflect.FieldDescriptor, v protoreflect.Value) bool {
		if fd.Kind() != protoreflect.MessageKind || fd.IsList() || fd.IsMap() {
			return true
		}
		inner := v.Message()
		if field := inner.Descriptor().Fields().ByName("contextInfo"); field != nil {
			inner.Set(field, protoreflect.ValueOfMessage(proto.Clone(ci).ProtoReflect()))
			done = true
			return false
		}
		if field := inner.Descriptor().Fields().ByName("message"); field != nil && inner.Has(field) {
			done = setContext(inner.Get(field).Message(), ci)
			return !done
		}
		return true
	})
	return done
}
