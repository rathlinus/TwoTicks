package main

import (
	"regexp"
	"strings"

	"go.mau.fi/whatsmeow/proto/waE2E"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// describe tells what a message shows: its kind, its text or caption, its
// media and the context (reply, mentions) it was sent in. An empty kind means
// the message has nothing to show of its own.
func describe(msg *waE2E.Message) (kind, text string, media *Media, ci *waE2E.ContextInfo) {
	if msg == nil {
		return "", "", nil, nil
	}
	msg = albumItem(msg)
	switch {
	case msg.Conversation != nil:
		return "text", msg.GetConversation(), nil, nil

	case msg.ExtendedTextMessage != nil:
		m := msg.ExtendedTextMessage
		return "text", m.GetText(), nil, m.GetContextInfo()

	case msg.ImageMessage != nil:
		m := msg.ImageMessage
		return "image", m.GetCaption(), &Media{
			Mime: m.GetMimetype(), Size: int64(m.GetFileLength()), Width: int(m.GetWidth()), Height: int(m.GetHeight()),
			Thumb: m.GetJPEGThumbnail(),
		}, m.GetContextInfo()

	case msg.VideoMessage != nil || msg.PtvMessage != nil:
		m := msg.VideoMessage
		if m == nil {
			m = msg.PtvMessage
		}
		kind := "video"
		if m.GetGifPlayback() {
			kind = "gif"
		}
		return kind, m.GetCaption(), &Media{
			Mime: m.GetMimetype(), Size: int64(m.GetFileLength()), Width: int(m.GetWidth()), Height: int(m.GetHeight()),
			Seconds: int(m.GetSeconds()), Thumb: m.GetJPEGThumbnail(),
		}, m.GetContextInfo()

	case msg.AudioMessage != nil:
		m := msg.AudioMessage
		kind := "audio"
		if m.GetPTT() {
			kind = "voice"
		}
		return kind, "", &Media{
			Mime: m.GetMimetype(), Size: int64(m.GetFileLength()), Seconds: int(m.GetSeconds()), Waveform: m.GetWaveform(),
		}, m.GetContextInfo()

	case msg.DocumentMessage != nil:
		m := msg.DocumentMessage
		name := m.GetFileName()
		if name == "" {
			name = m.GetTitle()
		}
		return "document", m.GetCaption(), &Media{
			Mime: m.GetMimetype(), Size: int64(m.GetFileLength()), Name: name, Pages: int(m.GetPageCount()),
			Thumb: m.GetJPEGThumbnail(),
		}, m.GetContextInfo()

	case msg.StickerMessage != nil:
		m := msg.StickerMessage
		return "sticker", "", &Media{
			Mime: m.GetMimetype(), Size: int64(m.GetFileLength()), Width: int(m.GetWidth()), Height: int(m.GetHeight()),
			Animated: m.GetIsAnimated(), Thumb: m.GetPngThumbnail(),
		}, m.GetContextInfo()

	case msg.LocationMessage != nil:
		m := msg.LocationMessage
		text := strings.TrimSpace(m.GetName() + "\n" + m.GetAddress())
		return "location", text, &Media{Lat: m.GetDegreesLatitude(), Lng: m.GetDegreesLongitude(), Thumb: m.GetJPEGThumbnail()}, m.GetContextInfo()

	case msg.LiveLocationMessage != nil:
		m := msg.LiveLocationMessage
		return "location", strings.TrimSpace(tr("liveLocation") + "\n" + m.GetCaption()), &Media{
			Lat: m.GetDegreesLatitude(), Lng: m.GetDegreesLongitude(), Thumb: m.GetJPEGThumbnail(),
		}, m.GetContextInfo()

	case msg.ContactMessage != nil:
		m := msg.ContactMessage
		return "contact", contactText(m.GetDisplayName(), m.GetVcard()), nil, m.GetContextInfo()

	case msg.ContactsArrayMessage != nil:
		m := msg.ContactsArrayMessage
		var lines []string
		for _, c := range m.GetContacts() {
			lines = append(lines, contactText(c.GetDisplayName(), c.GetVcard()))
		}
		return "contact", strings.Join(lines, "\n"), nil, m.GetContextInfo()

	case msg.PollCreationMessage != nil || msg.PollCreationMessageV2 != nil || msg.PollCreationMessageV3 != nil:
		m := msg.PollCreationMessage
		if m == nil {
			m = msg.PollCreationMessageV2
		}
		if m == nil {
			m = msg.PollCreationMessageV3
		}
		lines := []string{m.GetName()}
		for _, o := range m.GetOptions() {
			lines = append(lines, "• "+o.GetOptionName())
		}
		return "poll", strings.Join(lines, "\n"), nil, m.GetContextInfo()

	case msg.EventMessage != nil:
		m := msg.EventMessage
		return "text", strings.TrimSpace("📅 " + m.GetName() + "\n" + m.GetDescription()), nil, m.GetContextInfo()

	case msg.GroupInviteMessage != nil:
		m := msg.GroupInviteMessage
		return "text", strings.TrimSpace(tr("groupInvite", "name", m.GetGroupName()) + "\n" + m.GetCaption()), nil, m.GetContextInfo()

	case msg.ButtonsMessage != nil:
		m := msg.ButtonsMessage
		return "text", strings.TrimSpace(m.GetContentText() + "\n" + m.GetFooterText()), nil, m.GetContextInfo()

	case msg.TemplateMessage != nil:
		t := msg.TemplateMessage.GetHydratedTemplate()
		return "text", strings.TrimSpace(t.GetHydratedTitleText() + "\n" + t.GetHydratedContentText() + "\n" + t.GetHydratedFooterText()), nil, msg.TemplateMessage.GetContextInfo()

	case msg.ListMessage != nil:
		m := msg.ListMessage
		return "text", strings.TrimSpace(m.GetTitle() + "\n" + m.GetDescription()), nil, m.GetContextInfo()

	case msg.InteractiveMessage != nil:
		m := msg.InteractiveMessage
		return "text", strings.TrimSpace(m.GetHeader().GetTitle() + "\n" + m.GetBody().GetText() + "\n" + m.GetFooter().GetText()), nil, m.GetContextInfo()

	case msg.ButtonsResponseMessage != nil:
		m := msg.ButtonsResponseMessage
		return "text", m.GetSelectedDisplayText(), nil, m.GetContextInfo()

	case msg.ListResponseMessage != nil:
		m := msg.ListResponseMessage
		return "text", m.GetTitle(), nil, m.GetContextInfo()

	case msg.TemplateButtonReplyMessage != nil:
		m := msg.TemplateButtonReplyMessage
		return "text", m.GetSelectedDisplayText(), nil, m.GetContextInfo()
	}

	// Messages that carry no content of their own.
	if msg.ProtocolMessage != nil || msg.ReactionMessage != nil || msg.EncReactionMessage != nil ||
		msg.PollUpdateMessage != nil || msg.SenderKeyDistributionMessage != nil || msg.KeepInChatMessage != nil ||
		msg.PinInChatMessage != nil || msg.AlbumMessage != nil || msg.MessageHistoryBundle != nil ||
		msg.EncEventResponseMessage != nil || msg.SecretEncryptedMessage != nil || msg.Call != nil {
		return "", "", nil, nil
	}
	if !hasContent(msg) {
		return "", "", nil, nil
	}
	return "unsupported", "", nil, nil
}

// albumItem unwraps a photo or video sent as part of an album. WhatsApp wraps
// each item in associatedChildMessage, which whatsmeow leaves in place.
func albumItem(msg *waE2E.Message) *waE2E.Message {
	if inner := msg.GetAssociatedChildMessage().GetMessage(); inner != nil {
		return inner
	}
	return msg
}

// hasContent reports whether a message has any field set apart from the
// bookkeeping every message may carry.
func hasContent(msg *waE2E.Message) bool {
	found := false
	msg.ProtoReflect().Range(func(fd protoreflect.FieldDescriptor, _ protoreflect.Value) bool {
		switch fd.Name() {
		case "messageContextInfo", "senderKeyDistributionMessage", "fastRatchetKeySenderKeyDistributionMessage":
			return true
		}
		found = true
		return false
	})
	return found
}

// fieldNames lists the fields set in a message, for logging what is not understood yet.
func fieldNames(msg *waE2E.Message) []string {
	var names []string
	msg.ProtoReflect().Range(func(fd protoreflect.FieldDescriptor, _ protoreflect.Value) bool {
		names = append(names, string(fd.Name()))
		return true
	})
	return names
}

var vcardPhone = regexp.MustCompile(`(?m)^TEL[^:]*:(.+)$`)

func contactText(name, vcard string) string {
	if m := vcardPhone.FindStringSubmatch(vcard); m != nil {
		return strings.TrimSpace(name + "\n" + strings.TrimSpace(m[1]))
	}
	return name
}
