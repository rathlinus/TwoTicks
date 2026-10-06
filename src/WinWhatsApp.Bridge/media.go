package main

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"image"
	"image/jpeg"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	_ "image/gif"
	_ "image/png"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/proto/waMmsRetry"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"golang.org/x/image/draw"
	_ "golang.org/x/image/webp"
	"google.golang.org/protobuf/proto"
)

// How long a profile picture is used before asking WhatsApp whether it changed.
const avatarMaxAge = 24 * time.Hour

type download struct {
	done chan struct{}
	path string
	err  error
}

// download fetches the media of a message into the media folder and returns
// the file. Asking twice for the same message waits for the first download.
func (b *Bridge) download(ctx context.Context, chat, id string) (string, error) {
	key := chat + "/" + id
	d := &download{done: make(chan struct{})}
	if existing, loaded := b.downloads.LoadOrStore(key, d); loaded {
		e := existing.(*download)
		<-e.done
		return e.path, e.err
	}
	d.path, d.err = b.doDownload(ctx, chat, id)
	b.downloads.Delete(key)
	close(d.done)
	return d.path, d.err
}

func (b *Bridge) doDownload(ctx context.Context, chat, id string) (string, error) {
	m, err := b.loadMessage(ctx, chat, id)
	if err != nil {
		return "", err
	}
	if m == nil {
		return "", errors.New("message not found")
	}
	if m.localPath != "" {
		if _, err := os.Stat(m.localPath); err == nil {
			return m.localPath, nil
		}
	}
	raw, err := b.loadRaw(ctx, chat, id)
	if err != nil || len(raw) == 0 {
		return "", errors.New("this message has no file to download")
	}
	var msg waE2E.Message
	if err := proto.Unmarshal(raw, &msg); err != nil {
		return "", err
	}
	part, name, mimeType := downloadablePart(&msg)
	if part == nil || len(part.GetMediaKey()) == 0 {
		return "", errors.New("this message has no file to download")
	}

	path := b.mediaPath(id, name, mimeType)
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return "", err
	}
	partial := path + ".part"
	f, err := os.Create(partial)
	if err != nil {
		return "", err
	}
	err = b.cli.DownloadToFile(ctx, part, f)
	if errors.Is(err, whatsmeow.ErrMediaDownloadFailedWith403) || errors.Is(err, whatsmeow.ErrMediaDownloadFailedWith404) ||
		errors.Is(err, whatsmeow.ErrMediaDownloadFailedWith410) {
		// WhatsApp's servers delete files after a while. The phone still has
		// them and can upload them again when asked.
		b.log.Infof("Media of %s expired, asking the phone to upload it again", id)
		var directPath string
		directPath, err = b.requestMediaRetry(ctx, m, part.GetMediaKey())
		if err == nil {
			setDirectPath(&msg, directPath)
			if updated, mErr := proto.Marshal(&msg); mErr == nil {
				_, _ = b.w.ExecContext(ctx, `UPDATE messages SET raw = ? WHERE chat = ? AND id = ?`, updated, chat, id)
			}
			part, _, _ = downloadablePart(&msg)
			_ = f.Truncate(0)
			_, _ = f.Seek(0, io.SeekStart)
			err = b.cli.DownloadToFile(ctx, part, f)
		}
	}
	closeErr := f.Close()
	if err == nil {
		err = closeErr
	}
	if err != nil {
		_ = os.Remove(partial)
		b.log.Warnf("Failed to download media of %s: %v", id, err)
		return "", friendlyDownloadError(err)
	}
	_ = os.Remove(path)
	if err := os.Rename(partial, path); err != nil {
		return "", err
	}
	if _, err := b.w.ExecContext(ctx, `UPDATE messages SET local_path = ? WHERE chat = ? AND id = ?`, path, chat, id); err != nil {
		return "", err
	}
	b.reloadAndEmit(chat, id)
	return path, nil
}

func friendlyDownloadError(err error) error {
	switch {
	case errors.Is(err, whatsmeow.ErrMediaNotAvailableOnPhone):
		return errors.New("this file is no longer on your phone")
	case errors.Is(err, context.DeadlineExceeded):
		return errors.New("your phone did not answer. Make sure it is online and try again")
	}
	return err
}

// requestMediaRetry asks the phone to upload the media of a message again and
// returns where it put it.
func (b *Bridge) requestMediaRetry(ctx context.Context, m *Message, mediaKey []byte) (string, error) {
	rawChat, err := types.ParseJID(m.rawChat)
	if err != nil {
		return "", err
	}
	rawSender, _ := types.ParseJID(m.rawSender)
	info := &types.MessageInfo{
		MessageSource: types.MessageSource{Chat: rawChat, Sender: rawSender, IsFromMe: m.FromMe, IsGroup: rawChat.Server == types.GroupServer},
		ID:            m.ID,
	}
	ch := make(chan *events.MediaRetry, 1)
	b.mediaRetries.Store(m.ID, ch)
	defer b.mediaRetries.Delete(m.ID)

	if err := b.cli.SendMediaRetryReceipt(ctx, info, mediaKey); err != nil {
		return "", err
	}
	select {
	case evt := <-ch:
		notif, err := whatsmeow.DecryptMediaRetryNotification(evt, mediaKey)
		if err != nil {
			return "", err
		}
		if notif.GetResult() != waMmsRetry.MediaRetryNotification_SUCCESS || notif.GetDirectPath() == "" {
			return "", whatsmeow.ErrMediaNotAvailableOnPhone
		}
		return notif.GetDirectPath(), nil
	case <-time.After(60 * time.Second):
		return "", context.DeadlineExceeded
	case <-ctx.Done():
		return "", ctx.Err()
	}
}

// downloadablePart finds the part of a message that has a file.
func downloadablePart(msg *waE2E.Message) (whatsmeow.DownloadableMessage, string, string) {
	if inner := msg.GetDocumentWithCaptionMessage().GetMessage(); inner != nil {
		msg = inner
	}
	switch {
	case msg.ImageMessage != nil:
		return msg.ImageMessage, "", msg.ImageMessage.GetMimetype()
	case msg.VideoMessage != nil:
		return msg.VideoMessage, "", msg.VideoMessage.GetMimetype()
	case msg.PtvMessage != nil:
		return msg.PtvMessage, "", msg.PtvMessage.GetMimetype()
	case msg.AudioMessage != nil:
		return msg.AudioMessage, "", msg.AudioMessage.GetMimetype()
	case msg.DocumentMessage != nil:
		name := msg.DocumentMessage.GetFileName()
		if name == "" {
			name = msg.DocumentMessage.GetTitle()
		}
		return msg.DocumentMessage, name, msg.DocumentMessage.GetMimetype()
	case msg.StickerMessage != nil:
		return msg.StickerMessage, "", msg.StickerMessage.GetMimetype()
	}
	return nil, "", ""
}

func setDirectPath(msg *waE2E.Message, path string) {
	if inner := msg.GetDocumentWithCaptionMessage().GetMessage(); inner != nil {
		msg = inner
	}
	p := proto.String(path)
	switch {
	case msg.ImageMessage != nil:
		msg.ImageMessage.DirectPath = p
	case msg.VideoMessage != nil:
		msg.VideoMessage.DirectPath = p
	case msg.PtvMessage != nil:
		msg.PtvMessage.DirectPath = p
	case msg.AudioMessage != nil:
		msg.AudioMessage.DirectPath = p
	case msg.DocumentMessage != nil:
		msg.DocumentMessage.DirectPath = p
	case msg.StickerMessage != nil:
		msg.StickerMessage.DirectPath = p
	}
}

// mediaPath is where the file of a message is kept. Documents keep their
// name, in a folder of their own; everything else is named after the message.
func (b *Bridge) mediaPath(id, name, mimeType string) string {
	dir := filepath.Join(b.dataDir, "media")
	safeID := safeFileName(id)
	if name = safeFileName(name); name != "" {
		return filepath.Join(dir, safeID, name)
	}
	return filepath.Join(dir, safeID+extensionFor(mimeType))
}

var extensions = map[string]string{
	"image/jpeg": ".jpg", "image/png": ".png", "image/webp": ".webp", "image/gif": ".gif",
	"video/mp4": ".mp4", "video/3gpp": ".3gp", "video/quicktime": ".mov", "video/webm": ".webm",
	"audio/ogg": ".ogg", "audio/mpeg": ".mp3", "audio/mp4": ".m4a", "audio/aac": ".aac", "audio/amr": ".amr",
	"audio/wav": ".wav", "audio/x-wav": ".wav", "application/pdf": ".pdf", "text/plain": ".txt",
	"application/zip": ".zip", "application/vnd.android.package-archive": ".apk",
}

func extensionFor(mimeType string) string {
	base := strings.TrimSpace(strings.SplitN(mimeType, ";", 2)[0])
	if ext, ok := extensions[strings.ToLower(base)]; ok {
		return ext
	}
	return ".bin"
}

func mimeFor(path string) string {
	ext := strings.ToLower(filepath.Ext(path))
	for mimeType, e := range extensions {
		if e == ext && !strings.HasPrefix(mimeType, "audio/x-") {
			return mimeType
		}
	}
	switch ext {
	case ".jpeg", ".jfif":
		return "image/jpeg"
	case ".doc":
		return "application/msword"
	case ".docx":
		return "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
	case ".xls":
		return "application/vnd.ms-excel"
	case ".xlsx":
		return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
	case ".ppt":
		return "application/vnd.ms-powerpoint"
	case ".pptx":
		return "application/vnd.openxmlformats-officedocument.presentationml.presentation"
	case ".opus":
		return "audio/ogg; codecs=opus"
	}
	f, err := os.Open(path)
	if err != nil {
		return "application/octet-stream"
	}
	defer f.Close()
	head := make([]byte, 512)
	n, _ := f.Read(head)
	return http.DetectContentType(head[:n])
}

// safeFileName makes a name usable as a Windows file name.
func safeFileName(name string) string {
	name = strings.Map(func(r rune) rune {
		if r < 32 || strings.ContainsRune(`<>:"/\|?*`, r) {
			return '_'
		}
		return r
	}, name)
	name = strings.TrimRight(strings.TrimSpace(name), ". ")
	if len(name) > 150 {
		ext := filepath.Ext(name)
		if len(ext) > 20 {
			ext = ""
		}
		name = truncate(name, 150-len(ext)) + ext
	}
	base := strings.ToUpper(strings.TrimSuffix(name, filepath.Ext(name)))
	switch base {
	case "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3":
		name = "_" + name
	}
	return name
}

// thumbnail fetches the preview picture of a video that came without one, as
// messages from history sync do, and stores it with the message.
func (b *Bridge) thumbnail(ctx context.Context, chat, id string) error {
	raw, err := b.loadRaw(ctx, chat, id)
	if err != nil || len(raw) == 0 {
		return errors.New("message not found")
	}
	var msg waE2E.Message
	if err := proto.Unmarshal(raw, &msg); err != nil {
		return err
	}
	part, _, _ := downloadablePart(&msg)
	withThumb, ok := part.(whatsmeow.DownloadableThumbnail)
	if !ok || withThumb.GetThumbnailDirectPath() == "" {
		return errors.New("this message has no preview")
	}
	data, err := b.cli.DownloadThumbnail(ctx, withThumb)
	if err != nil {
		return err
	}
	m, err := b.loadMessage(ctx, chat, id)
	if err != nil || m == nil {
		return errors.New("message not found")
	}
	if m.Media == nil {
		m.Media = &Media{}
	}
	m.Media.Path = ""
	m.Media.Thumb = data
	if _, err := b.w.ExecContext(ctx, `UPDATE messages SET media = ? WHERE chat = ? AND id = ?`, marshalOrNil(m.Media, false), chat, id); err != nil {
		return err
	}
	b.reloadAndEmit(chat, id)
	return nil
}

// avatar returns the profile picture of a person or group as a local file, or
// "" when there is none. Pictures are checked for changes once a day.
func (b *Bridge) avatar(ctx context.Context, jidText string, force bool) (string, error) {
	jid, err := types.ParseJID(jidText)
	if err != nil {
		return "", err
	}
	var pictureID, path string
	var checked int64
	_ = b.r.QueryRowContext(ctx, `SELECT picture_id, path, checked_at FROM avatars WHERE jid = ?`, jidText).Scan(&pictureID, &path, &checked)
	if path != "" {
		if _, err := os.Stat(path); err != nil {
			path, pictureID = "", ""
		}
	}
	if !force && time.Since(time.Unix(checked, 0)) < avatarMaxAge {
		return path, nil
	}
	if b.cli.Store.ID == nil || !b.cli.IsConnected() {
		return path, nil
	}

	select {
	case b.avatarSlots <- struct{}{}:
		defer func() { <-b.avatarSlots }()
	case <-ctx.Done():
		return path, ctx.Err()
	}

	info, err := b.cli.GetProfilePictureInfo(ctx, jid, &whatsmeow.GetProfilePictureParams{Preview: true, ExistingID: pictureID})
	newPath, newID := path, pictureID
	switch {
	case errors.Is(err, whatsmeow.ErrProfilePictureNotSet) || errors.Is(err, whatsmeow.ErrProfilePictureUnauthorized):
		newPath, newID = "", ""
	case err != nil:
		// Try again in an hour rather than on every request.
		_, _ = b.w.ExecContext(ctx, `
			INSERT INTO avatars (jid, picture_id, path, checked_at) VALUES (?, ?, ?, ?)
			ON CONFLICT (jid) DO UPDATE SET checked_at = excluded.checked_at`,
			jidText, pictureID, path, time.Now().Add(time.Hour-avatarMaxAge).Unix())
		return path, err
	case info != nil:
		newID = info.ID
		newPath = filepath.Join(b.dataDir, "avatars", safeFileName(jid.User+"-"+jid.Server)+"-"+safeFileName(info.ID)+".jpg")
		if err := fetchToFile(ctx, info.URL, newPath); err != nil {
			return path, err
		}
	}
	if path != "" && path != newPath {
		_ = os.Remove(path)
	}
	_, err = b.w.ExecContext(ctx, `
		INSERT INTO avatars (jid, picture_id, path, checked_at) VALUES (?, ?, ?, ?)
		ON CONFLICT (jid) DO UPDATE SET picture_id = excluded.picture_id, path = excluded.path, checked_at = excluded.checked_at`,
		jidText, newID, newPath, time.Now().Unix())
	if newPath != path {
		b.out.event("avatar", map[string]string{"jid": jidText, "path": newPath})
	}
	return newPath, err
}

func (b *Bridge) onPictureChanged(evt *events.Picture) {
	jid := b.canonical(b.ctx, evt.JID).String()
	_, _ = b.w.ExecContext(b.ctx, `UPDATE avatars SET checked_at = 0 WHERE jid = ?`, jid)
	go func() { _, _ = b.avatar(b.ctx, jid, true) }()
}

var httpClient = &http.Client{Timeout: 30 * time.Second}

func fetchToFile(ctx context.Context, url, path string) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return err
	}
	resp, err := httpClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("download failed with status %d", resp.StatusCode)
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return err
	}
	f, err := os.Create(path + ".part")
	if err != nil {
		return err
	}
	_, err = io.Copy(f, resp.Body)
	if closeErr := f.Close(); err == nil {
		err = closeErr
	}
	if err != nil {
		_ = os.Remove(path + ".part")
		return err
	}
	return os.Rename(path+".part", path)
}

// preparedImage is an image ready to send: JPEG data, its size and a small
// preview for the chat bubble before the image is downloaded.
type preparedImage struct {
	data          []byte
	width, height int
	thumb         []byte
}

// prepareImage reads an image to send. JPEG files are sent as they are;
// other formats are converted to JPEG, as WhatsApp's apps do. ok is false for
// files Go cannot decode, such as HEIC, which then go as documents.
func prepareImage(path string) (img preparedImage, ok bool) {
	data, err := os.ReadFile(path)
	if err != nil {
		return img, false
	}
	decoded, format, err := image.Decode(bytes.NewReader(data))
	if err != nil {
		return img, false
	}
	bounds := decoded.Bounds()
	img.width, img.height = bounds.Dx(), bounds.Dy()
	if format == "jpeg" {
		img.data = data
	} else {
		var buf bytes.Buffer
		if err := jpeg.Encode(&buf, flatten(decoded), &jpeg.Options{Quality: 92}); err != nil {
			return img, false
		}
		img.data = buf.Bytes()
	}
	img.thumb = thumbnail(decoded, 160)
	return img, true
}

// flatten puts an image with transparency on white, which is what a JPEG of
// it should look like.
func flatten(src image.Image) image.Image {
	bounds := src.Bounds()
	dst := image.NewRGBA(bounds)
	draw.Draw(dst, bounds, image.White, image.Point{}, draw.Src)
	draw.Draw(dst, bounds, src, bounds.Min, draw.Over)
	return dst
}

// thumbnail scales an image so its longer side is at most size pixels and
// encodes it as a small JPEG.
func thumbnail(src image.Image, size int) []byte {
	bounds := src.Bounds()
	w, h := bounds.Dx(), bounds.Dy()
	if w == 0 || h == 0 {
		return nil
	}
	if w > h {
		h = max(1, h*size/w)
		w = size
	} else {
		w = max(1, w*size/h)
		h = size
	}
	dst := image.NewRGBA(image.Rect(0, 0, w, h))
	draw.Draw(dst, dst.Bounds(), image.White, image.Point{}, draw.Src)
	draw.ApproxBiLinear.Scale(dst, dst.Bounds(), src, bounds, draw.Over, nil)
	var buf bytes.Buffer
	if err := jpeg.Encode(&buf, dst, &jpeg.Options{Quality: 70}); err != nil {
		return nil
	}
	return buf.Bytes()
}
