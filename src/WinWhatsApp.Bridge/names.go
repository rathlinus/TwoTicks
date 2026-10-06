package main

import (
	"context"
	"strings"

	"go.mau.fi/whatsmeow/types"
)

// nameOf gives the name to show for a person or group: the name saved in the
// phone's contacts first, then the name they gave themselves, then the number.
func (b *Bridge) nameOf(ctx context.Context, jid types.JID) string {
	jid = jid.ToNonAD()
	if jid.Server == types.GroupServer {
		var name string
		_ = b.r.QueryRowContext(ctx, `SELECT name FROM chats WHERE jid = ?`, jid.String()).Scan(&name)
		if name != "" {
			return name
		}
		return "Group"
	}

	candidates := []types.JID{jid}
	var pn types.JID
	switch jid.Server {
	case types.HiddenUserServer:
		if p, err := b.cli.Store.LIDs.GetPNForLID(ctx, jid); err == nil && !p.IsEmpty() {
			pn = p.ToNonAD()
			candidates = append(candidates, pn)
		}
	case types.DefaultUserServer:
		pn = jid
		if lid, err := b.cli.Store.LIDs.GetLIDForPN(ctx, jid); err == nil && !lid.IsEmpty() {
			candidates = append(candidates, lid.ToNonAD())
		}
	}

	var first, business, push, redacted string
	for _, c := range candidates {
		info, err := b.cli.Store.Contacts.GetContact(ctx, c)
		if err != nil || !info.Found {
			continue
		}
		if info.FullName != "" {
			return info.FullName
		}
		first = firstNonEmpty(first, info.FirstName)
		business = firstNonEmpty(business, info.BusinessName)
		push = firstNonEmpty(push, info.PushName)
		redacted = firstNonEmpty(redacted, info.RedactedPhone)
	}
	switch {
	case first != "":
		return first
	case business != "":
		return business
	case push != "":
		return push
	case !pn.IsEmpty():
		return formatPhone(pn.User)
	case redacted != "":
		return redacted
	}
	return jid.User
}

// isSavedContact reports whether the phone has a name saved for the person.
func (b *Bridge) isSavedContact(ctx context.Context, jid types.JID) bool {
	info, err := b.cli.Store.Contacts.GetContact(ctx, jid.ToNonAD())
	return err == nil && info.Found && (info.FullName != "" || info.FirstName != "")
}

func firstNonEmpty(a, b string) string {
	if a != "" {
		return a
	}
	return b
}

// formatPhone writes the number of a JID with the plus in front.
func formatPhone(user string) string {
	if user == "" || strings.ContainsFunc(user, func(r rune) bool { return r < '0' || r > '9' }) {
		return user
	}
	return "+" + user
}

func (b *Bridge) senderName(ctx context.Context, sender, pushName string) string {
	jid, err := types.ParseJID(sender)
	if err != nil {
		return pushName
	}
	if b.isOwn(jid) {
		return "You"
	}
	name := b.nameOf(ctx, jid)
	if name == formatPhone(jid.User) || name == jid.User {
		if pushName != "" {
			return "~" + pushName
		}
	}
	return name
}

// decorateMessages fills in the names the app shows: who sent a group
// message, who is quoted, who reacted, and who is mentioned.
func (b *Bridge) decorateMessages(ctx context.Context, list []*Message) {
	names := map[string]string{}
	lookup := func(sender, pushName string) string {
		if n, ok := names[sender]; ok {
			return n
		}
		n := b.senderName(ctx, sender, pushName)
		names[sender] = n
		return n
	}
	for _, m := range list {
		isGroup := strings.HasSuffix(m.Chat, "@"+types.GroupServer)
		if isGroup && !m.FromMe && m.Sender != "" {
			m.SenderName = lookup(m.Sender, m.pushName)
		}
		if q := m.Quote; q != nil {
			if jid, err := types.ParseJID(q.Sender); err == nil && b.isOwn(jid) {
				q.FromMe = true
				q.SenderName = "You"
			} else {
				q.SenderName = lookup(q.Sender, "")
			}
		}
		for i := range m.Reactions {
			r := &m.Reactions[i]
			if jid, err := types.ParseJID(r.Sender); err == nil && b.isOwn(jid) {
				r.FromMe = true
				r.Name = "You"
			} else {
				r.Name = lookup(r.Sender, "")
			}
		}
		if len(m.mentionJIDs) > 0 {
			m.Mentions = make(map[string]string, len(m.mentionJIDs))
			for _, s := range m.mentionJIDs {
				jid, err := types.ParseJID(s)
				if err != nil {
					continue
				}
				name := lookup(b.canonical(ctx, jid).String(), "")
				m.Mentions[jid.User] = strings.TrimPrefix(name, "~")
			}
		}
	}
}

// decorateChats fills in the names in the chat list.
func (b *Bridge) decorateChats(ctx context.Context, list []*Chat) {
	for _, c := range list {
		jid, err := types.ParseJID(c.JID)
		switch {
		case err != nil:
			c.Name = c.storedName
		case c.Group:
			c.Name = firstNonEmpty(c.storedName, "Group")
		case b.isOwn(jid):
			c.Name = b.nameOf(ctx, jid) + " (You)"
		default:
			name := b.nameOf(ctx, jid)
			if (name == formatPhone(jid.User) || name == jid.User) && c.storedName != "" {
				name = c.storedName
			}
			c.Name = name
		}
		if c.Last != nil && c.Last.sender != "" {
			c.Last.SenderName = strings.TrimPrefix(b.senderName(ctx, c.Last.sender, c.Last.pushName), "~")
		}
	}
}
