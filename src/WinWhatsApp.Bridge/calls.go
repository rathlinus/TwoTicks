package main

// Calls. The app runs WhatsApp Web's calling engine; the helper carries the
// engine's signaling between it and WhatsApp's servers. Signaling is a set of
// stanzas inside <call>: offer, accept, transport, terminate and so on. The
// engine reads and writes them in WhatsApp's binary XML, the same encoding
// whatsmeow uses, so they pass through as they are, base64 encoded, apart
// from the call key in an offer, which is end-to-end encrypted with Signal:
// the helper decrypts it on the way in and encrypts it on the way out.

import (
	"context"
	"encoding/base64"
	"errors"
	"fmt"
	"strings"
	"time"

	waBinary "go.mau.fi/whatsmeow/binary"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/types"
	waLog "go.mau.fi/whatsmeow/util/log"
	"google.golang.org/protobuf/proto"
)

// Stanzas the receiving side confirms with a receipt instead of only an ack.
var receiptedCallStanzas = map[string]bool{"offer": true, "accept": true, "reject": true, "enc_rekey": true}

// recvTap hands the nodes whatsmeow receives to the helper before whatsmeow
// handles them. whatsmeow turns call stanzas into events without the stanza's
// id and attributes the engine needs, and has no way to see raw nodes; its
// receive log gets every node, so that is where they are taken from.
type recvTap struct {
	waLog.Logger
	tap func(*waBinary.Node)
}

func (l *recvTap) Sub(module string) waLog.Logger {
	sub := l.Logger.Sub(module)
	if module == "Recv" {
		return &nodeLogger{Logger: sub, tap: l.tap}
	}
	return sub
}

type nodeLogger struct {
	waLog.Logger
	tap func(*waBinary.Node)
}

func (l *nodeLogger) Debugf(msg string, args ...any) {
	if len(args) == 1 {
		if node, ok := args[0].(*waBinary.Node); ok {
			l.tap(node)
		}
	}
	l.Logger.Debugf(msg, args...)
}

// tapNode takes call stanzas and receipts for them off the receive loop, in
// the order they came.
func (b *Bridge) tapNode(node *waBinary.Node) {
	switch node.Tag {
	case "call":
	case "receipt":
		if _, ok := node.GetOptionalChildByTag("offer"); !ok {
			if _, ok := node.GetOptionalChildByTag("accept"); !ok {
				if _, ok := node.GetOptionalChildByTag("reject"); !ok {
					return
				}
			}
		}
	default:
		return
	}
	// whatsmeow goes on to handle the node too, so the helper works on a copy.
	data, err := waBinary.Marshal(*node)
	if err != nil {
		return
	}
	if node, err = waBinary.Unmarshal(data[1:]); err != nil {
		return
	}
	select {
	case b.callNodes <- node:
	default:
		b.log.Warnf("Dropped a call stanza, too many waiting")
	}
}

func (b *Bridge) runCallNodes() {
	for {
		select {
		case <-b.ctx.Done():
			return
		case node := <-b.callNodes:
			func() {
				defer func() {
					if p := recover(); p != nil {
						b.log.Errorf("Panic handling a call stanza: %v", p)
					}
				}()
				if node.Tag == "call" {
					b.onCallStanza(node)
				} else {
					b.onCallReceipt(node)
				}
			}()
		}
	}
}

// callSignal is what the app hands to the engine.
type callSignal struct {
	// offer, message or receipt.
	Kind     string `json:"kind"`
	Node     string `json:"node"`
	Peer     string `json:"peer"`
	Platform string `json:"platform,omitempty"`
	Version  string `json:"version,omitempty"`
	T        int64  `json:"t,omitempty"`
	E        int64  `json:"e,omitempty"`
	Offline  bool   `json:"offline,omitempty"`
	TCToken  string `json:"tcToken,omitempty"`
	CallID   string `json:"callId"`

	// For an offer: who calls, as the app knows the chat.
	Chat       string `json:"chat,omitempty"`
	Name       string `json:"name,omitempty"`
	NotContact bool   `json:"notContact,omitempty"`
}

func (b *Bridge) onCallStanza(node *waBinary.Node) {
	children := node.GetChildren()
	if len(children) != 1 {
		return
	}
	ctx := b.ctx
	ag := node.AttrGetter()
	from := ag.JID("from")
	id := ag.String("id")
	child := &children[0]
	cag := child.AttrGetter()
	callID := cag.OptionalString("call-id")
	creator := cag.OptionalJIDOrEmpty("call-creator")

	_, group := child.Attrs["group-jid"]
	_, video := child.GetOptionalChildByTag("video")
	if child.Tag == "offer" && (group || video) {
		// The engine is set up for one-to-one voice calls; these ring on the phone.
		b.notifyCall(from, creator, video, group)
		return
	}

	if child.Tag == "offer" || child.Tag == "enc_rekey" {
		if err := b.decryptCallKey(ctx, child, from, ag.UnixTime("t")); err != nil {
			b.log.Warnf("Failed to decrypt the key of call %s from %s: %v", callID, from, err)
			if child.Tag == "offer" {
				b.notifyCall(from, creator, video, group)
			}
			return
		}
	}
	if receiptedCallStanzas[child.Tag] {
		b.sendCallReceipt(ctx, from, id, child.Tag, callID, creator)
	}

	data, err := marshalForEngine(*child)
	if err != nil {
		b.log.Warnf("Failed to encode a call stanza: %v", err)
		return
	}
	signal := callSignal{
		Kind:     "message",
		Node:     base64.StdEncoding.EncodeToString(data),
		Peer:     legacyJID(from),
		Platform: ag.OptionalString("platform"),
		Version:  ag.OptionalString("version"),
		T:        ag.OptionalUnixTime("t").Unix(),
		E:        ag.OptionalUnixTime("e").Unix(),
		TCToken:  b.tcToken(ctx, from),
		CallID:   callID,
	}
	_, signal.Offline = node.Attrs["offline"]
	if signal.T < 0 {
		signal.T = 0
	}
	if signal.E < 0 {
		signal.E = 0
	}
	if child.Tag == "offer" {
		signal.Kind = "offer"
		caller := creator
		if caller.IsEmpty() {
			caller = from
		}
		chat := b.canonical(ctx, caller)
		signal.Chat = chat.String()
		signal.Name = b.nameOf(ctx, chat)
		contact, err := b.cli.Store.Contacts.GetContact(ctx, chat)
		signal.NotContact = err == nil && !contact.Found
	}
	b.out.event("callSignal", signal)
}

// notifyCall tells the app about a call it cannot answer.
func (b *Bridge) notifyCall(from, creator types.JID, video, group bool) {
	caller := b.canonical(b.ctx, creator)
	if caller.IsEmpty() {
		caller = b.canonical(b.ctx, from)
	}
	b.out.event("call", map[string]any{
		"from":  caller.String(),
		"name":  b.nameOf(b.ctx, caller),
		"video": video,
		"group": group,
	})
}

func (b *Bridge) onCallReceipt(node *waBinary.Node) {
	from := node.AttrGetter().JID("from")
	callID := ""
	if children := node.GetChildren(); len(children) > 0 {
		callID = children[0].AttrGetter().OptionalString("call-id")
	}
	data, err := marshalForEngine(*node)
	if err != nil {
		return
	}
	b.out.event("callSignal", callSignal{
		Kind:    "receipt",
		Node:    base64.StdEncoding.EncodeToString(data),
		Peer:    legacyJID(from),
		TCToken: b.tcToken(b.ctx, from),
		CallID:  callID,
	})
}

// sendCallReceipt confirms an offer, accept, reject or new key, as WhatsApp
// Web does. The caller's phone shows "Ringing" once the offer is confirmed.
func (b *Bridge) sendCallReceipt(ctx context.Context, to types.JID, id, tag, callID string, creator types.JID) {
	attrs := waBinary.Attrs{"call-id": callID}
	if !creator.IsEmpty() {
		attrs["call-creator"] = creator
	}
	err := b.cli.DangerousInternals().SendNode(ctx, waBinary.Node{
		Tag:     "receipt",
		Attrs:   waBinary.Attrs{"to": to, "id": id},
		Content: []waBinary.Node{{Tag: tag, Attrs: attrs}},
	})
	if err != nil {
		b.log.Warnf("Failed to confirm a call %s: %v", tag, err)
	}
}

// decryptCallKey replaces the encrypted call key in an offer with the key itself.
func (b *Bridge) decryptCallKey(ctx context.Context, stanza *waBinary.Node, from types.JID, ts time.Time) error {
	children, _ := stanza.Content.([]waBinary.Node)
	for i := range children {
		enc := &children[i]
		if enc.Tag != "enc" {
			continue
		}
		encType := enc.AttrGetter().String("type")
		if encType != "msg" && encType != "pkmsg" {
			return fmt.Errorf("unexpected key type %q", encType)
		}
		plaintext, _, err := b.cli.DangerousInternals().DecryptDM(ctx, enc, from, encType == "pkmsg", ts)
		if err != nil {
			return err
		}
		var msg waE2E.Message
		if err := proto.Unmarshal(plaintext, &msg); err != nil {
			return err
		}
		key := msg.GetCall().GetCallKey()
		if len(key) == 0 {
			return errors.New("no call key in the message")
		}
		enc.Content = key
	}
	return nil
}

// encryptCallKeys encrypts the call keys the engine put in an offer, for the
// one device it goes to or for each device under <destination>. It reports
// whether the offer needs this device's identity, as a first message does.
func (b *Bridge) encryptCallKeys(ctx context.Context, stanza *waBinary.Node, peer types.JID) (bool, error) {
	children, _ := stanza.Content.([]waBinary.Node)
	needIdentity := false
	encrypt := func(enc *waBinary.Node, device types.JID) error {
		key, _ := enc.Content.([]byte)
		plaintext, err := proto.Marshal(&waE2E.Message{Call: &waE2E.Call{CallKey: key}})
		if err != nil {
			return err
		}
		attrs := waBinary.Attrs{"count": enc.AttrGetter().OptionalString("count")}
		if attrs["count"] == "" {
			attrs["count"] = "0"
		}
		in := b.cli.DangerousInternals()
		node, identity, err := in.EncryptMessageForDevice(ctx, plaintext, device, nil, attrs, nil)
		if err != nil {
			// No session with the device yet: start one from its prekeys.
			bundles := in.FetchPreKeysNoError(ctx, []types.JID{device})
			bundle := bundles[device]
			if bundle == nil {
				return fmt.Errorf("no prekeys for %s: %w", device, err)
			}
			node, identity, err = in.EncryptMessageForDevice(ctx, plaintext, device, bundle, attrs, nil)
			if err != nil {
				return err
			}
		}
		*enc = *node
		needIdentity = needIdentity || identity
		return nil
	}

	for i := range children {
		switch children[i].Tag {
		case "enc":
			if err := encrypt(&children[i], peer); err != nil {
				return false, err
			}
		case "destination":
			targets, _ := children[i].Content.([]waBinary.Node)
			for j := range targets {
				device, ok := targets[j].Attrs["jid"].(types.JID)
				if !ok {
					continue
				}
				inner, _ := targets[j].Content.([]waBinary.Node)
				for k := range inner {
					if inner[k].Tag == "enc" {
						if err := encrypt(&inner[k], device); err != nil {
							// Like WhatsApp Web: the offer still goes to the other devices.
							b.log.Warnf("Failed to encrypt the call key for %s: %v", device, err)
							inner[k] = waBinary.Node{Tag: "removed"}
						}
					}
				}
				targets[j].Content = dropRemoved(inner)
			}
		}
	}
	return needIdentity, nil
}

func dropRemoved(nodes []waBinary.Node) []waBinary.Node {
	kept := nodes[:0]
	for _, n := range nodes {
		if n.Tag != "removed" {
			kept = append(kept, n)
		}
	}
	return kept
}

// sendCall sends a stanza of the engine to a peer and returns the server's ack.
func (b *Bridge) sendCall(ctx context.Context, peerText, payload string) (map[string]any, error) {
	raw, err := base64.StdEncoding.DecodeString(payload)
	if err != nil || len(raw) == 0 {
		return nil, errors.New("bad call stanza")
	}
	unpacked, err := waBinary.Unpack(raw)
	if err != nil {
		return nil, err
	}
	stanza, err := waBinary.Unmarshal(unpacked)
	if err != nil {
		return nil, err
	}
	peer, err := parseLegacyJID(peerText)
	if err != nil {
		return nil, err
	}

	to := peer
	if stanza.Tag == "offer" || stanza.Tag == "enc_rekey" {
		if _, ok := stanza.GetOptionalChildByTag("destination"); ok {
			to = peer.ToNonAD()
		}
		needIdentity, err := b.encryptCallKeys(ctx, stanza, peer)
		if err != nil {
			if stanza.Tag != "offer" {
				return nil, err
			}
			b.log.Warnf("Sending call offer without a key: %v", err)
			children, _ := stanza.Content.([]waBinary.Node)
			kept := children[:0]
			for _, c := range children {
				if c.Tag != "enc" {
					kept = append(kept, c)
				}
			}
			stanza.Content = kept
		}
		if needIdentity {
			children, _ := stanza.Content.([]waBinary.Node)
			stanza.Content = append(children, b.cli.DangerousInternals().MakeDeviceIdentityNode())
		}
	}

	in := b.cli.DangerousInternals()
	id := b.cli.GenerateMessageID()
	wait := in.WaitResponse(id)
	err = in.SendNode(ctx, waBinary.Node{
		Tag:     "call",
		Attrs:   waBinary.Attrs{"to": to, "id": id},
		Content: []waBinary.Node{*stanza},
	})
	if err != nil {
		in.CancelResponse(id, wait)
		return nil, err
	}
	select {
	case ack := <-wait:
		data, err := marshalForEngine(*ack)
		if err != nil {
			return nil, err
		}
		ag := ack.AttrGetter()
		ackErr := ag.OptionalString("error")
		if ackErr == "" {
			ackErr = "0"
		}
		return map[string]any{
			"node":    base64.StdEncoding.EncodeToString(data),
			"error":   ackErr,
			"type":    ag.OptionalString("type"),
			"tcToken": b.tcToken(ctx, peer),
		}, nil
	case <-time.After(20 * time.Second):
		in.CancelResponse(id, wait)
		return nil, errors.New("no answer from WhatsApp")
	case <-ctx.Done():
		in.CancelResponse(id, wait)
		return nil, ctx.Err()
	}
}

// prepareCall finds what the engine needs to call someone: their hidden ID,
// which calls go to, their devices and their privacy token.
func (b *Bridge) prepareCall(ctx context.Context, chat string) (map[string]any, error) {
	pn, err := types.ParseJID(chat)
	if err != nil {
		return nil, err
	}
	pn = pn.ToNonAD()
	if pn.Server != types.DefaultUserServer && pn.Server != types.HiddenUserServer {
		return nil, errors.New("only people can be called")
	}
	peer := pn
	if pn.Server == types.DefaultUserServer {
		if lid, err := b.cli.Store.LIDs.GetLIDForPN(ctx, pn); err == nil && !lid.IsEmpty() {
			peer = lid
		} else if devices, err := b.cli.GetUserDevices(ctx, []types.JID{pn}); err == nil && len(devices) > 0 {
			// Looking up the devices teaches whatsmeow the hidden ID.
			if lid, err := b.cli.Store.LIDs.GetLIDForPN(ctx, pn); err == nil && !lid.IsEmpty() {
				peer = lid
			}
		}
	} else if found, err := b.cli.Store.LIDs.GetPNForLID(ctx, pn); err == nil && !found.IsEmpty() {
		pn = found.ToNonAD()
	}

	devices, err := b.cli.GetUserDevices(ctx, []types.JID{peer})
	if err != nil {
		return nil, err
	}
	var list []string
	for _, d := range devices {
		// Device 99 is a business's hosted device, which does not take calls.
		if d.Device == 99 || d.Server == types.HostedServer || d.Server == types.HostedLIDServer {
			continue
		}
		list = append(list, legacyJID(d))
	}
	if len(list) == 0 {
		return nil, errors.New("this person has no devices that take calls")
	}
	return map[string]any{
		"peer":    legacyJID(peer),
		"peerPn":  legacyJID(pn),
		"devices": list,
		"tcToken": b.tcToken(ctx, peer),
		"name":    b.nameOf(ctx, b.canonical(ctx, pn)),
	}, nil
}

// callIdentity is this device as the engine knows itself.
func (b *Bridge) callIdentity() (map[string]string, error) {
	id := b.cli.Store.ID
	if id == nil {
		return nil, errNotLoggedIn
	}
	lid := b.cli.Store.GetLID()
	return map[string]string{
		"pn":     legacyJID(*id),
		"pnUser": legacyJID(id.ToNonAD()),
		"lid":    legacyJID(lid),
	}, nil
}

// tcToken is the privacy token the person gave this account, which shows
// WhatsApp that they know each other. Empty when there is none.
func (b *Bridge) tcToken(ctx context.Context, jid types.JID) string {
	user := jid.ToNonAD()
	candidates := []types.JID{user}
	if user.Server == types.DefaultUserServer {
		if lid, err := b.cli.Store.LIDs.GetLIDForPN(ctx, user); err == nil && !lid.IsEmpty() {
			candidates = append([]types.JID{lid}, candidates...)
		}
	}
	for _, c := range candidates {
		if token, err := b.cli.Store.PrivacyTokens.GetPrivacyToken(ctx, c); err == nil && token != nil && len(token.Token) > 0 {
			return base64.StdEncoding.EncodeToString(token.Token)
		}
	}
	return ""
}

// marshalForEngine encodes a stanza from WhatsApp again for the engine.
//
// whatsmeow reads the device ID of a person's main device, device 0, as the
// person's ID, and writes it back that way. The engine needs it written as a
// device where WhatsApp sent a device, as in the <device> list of an offer's
// ack, or it cannot read the stanza. whatsmeow writes the device form for any
// device above 0 and keeps only the low byte of the number, so device 256
// comes out as device 0 in the device form.
func marshalForEngine(node waBinary.Node) ([]byte, error) {
	markDevices(&node)
	return waBinary.Marshal(node)
}

func markDevices(node *waBinary.Node) {
	if node.Tag == "device" {
		if jid, ok := node.Attrs["jid"].(types.JID); ok && jid.Device == 0 &&
			(jid.Server == types.DefaultUserServer || jid.Server == types.HiddenUserServer) {
			jid.Device = 256
			node.Attrs["jid"] = jid
		}
	}
	if children, ok := node.Content.([]waBinary.Node); ok {
		for i := range children {
			markDevices(&children[i])
		}
	}
}

// legacyJID writes a JID as WhatsApp Web's calling engine does, with c.us for
// phone numbers.
func legacyJID(jid types.JID) string {
	return strings.Replace(jid.String(), "@"+types.DefaultUserServer, "@"+types.LegacyUserServer, 1)
}

func parseLegacyJID(text string) (types.JID, error) {
	jid, err := types.ParseJID(text)
	if err != nil {
		return jid, err
	}
	if jid.Server == types.LegacyUserServer {
		jid.Server = types.DefaultUserServer
	}
	return jid, nil
}
