package main

import (
	"slices"
	"strings"
)

// lang is the language of the text the helper writes itself, set with --lang
// to the app's language.
var lang = "en"

// texts is the helper's own text by key and language: the notices it stores
// in chats, such as who joined a group, and the errors the app shows as they
// are. Text missing in a language comes from English. Values go in braces.
//
// Notices about what someone did have a ".you" form for when that was the
// user, because German says "Du hast" but "Anna hat". Notices about a list of
// people have ".one" and ".other" forms for how many they are.
var texts = map[string]map[string]string{
	"someone":      {"en": "someone", "de": "jemand"},
	"someoneActor": {"en": "Someone", "de": "Jemand"},
	"youSubject":   {"en": "you", "de": "du"},
	"youObject":    {"en": "you", "de": "dich"},
	"list":         {"en": "{rest} and {last}", "de": "{rest} und {last}"},

	"groupCreated":         {"en": `{actor} created group "{name}"`, "de": "{actor} hat die Gruppe „{name}“ erstellt"},
	"groupCreated.you":     {"en": `You created group "{name}"`, "de": "Du hast die Gruppe „{name}“ erstellt"},
	"groupRenamed":         {"en": `{actor} changed the group name to "{name}"`, "de": "{actor} hat den Gruppennamen in „{name}“ geändert"},
	"groupRenamed.you":     {"en": `You changed the group name to "{name}"`, "de": "Du hast den Gruppennamen in „{name}“ geändert"},
	"groupIcon":            {"en": "{actor} changed this group's icon", "de": "{actor} hat das Gruppenbild geändert"},
	"groupIcon.you":        {"en": "You changed this group's icon", "de": "Du hast das Gruppenbild geändert"},
	"groupDescription":     {"en": "{actor} changed the group description", "de": "{actor} hat die Gruppenbeschreibung geändert"},
	"groupDescription.you": {"en": "You changed the group description", "de": "Du hast die Gruppenbeschreibung geändert"},
	"disappearing":         {"en": "{actor} changed the disappearing messages setting", "de": "{actor} hat die Einstellung für selbstlöschende Nachrichten geändert"},
	"disappearing.you":     {"en": "You changed the disappearing messages setting", "de": "Du hast die Einstellung für selbstlöschende Nachrichten geändert"},
	"added":                {"en": "{actor} added {names}", "de": "{actor} hat {names} hinzugefügt"},
	"added.you":            {"en": "You added {names}", "de": "Du hast {names} hinzugefügt"},
	"removed":              {"en": "{actor} removed {names}", "de": "{actor} hat {names} entfernt"},
	"removed.you":          {"en": "You removed {names}", "de": "Du hast {names} entfernt"},

	"left.you":           {"en": "You left", "de": "Du hast die Gruppe verlassen"},
	"left.one":           {"en": "{names} left", "de": "{names} hat die Gruppe verlassen"},
	"left.other":         {"en": "{names} left", "de": "{names} haben die Gruppe verlassen"},
	"joined.you":         {"en": "You joined", "de": "Du bist beigetreten"},
	"joined.one":         {"en": "{names} joined", "de": "{names} ist beigetreten"},
	"joined.other":       {"en": "{names} joined", "de": "{names} sind beigetreten"},
	"joinedByLink.you":   {"en": "You joined using an invite link", "de": "Du bist über einen Einladungslink beigetreten"},
	"joinedByLink.one":   {"en": "{names} joined using an invite link", "de": "{names} ist über einen Einladungslink beigetreten"},
	"joinedByLink.other": {"en": "{names} joined using an invite link", "de": "{names} sind über einen Einladungslink beigetreten"},
	"promoted.you":       {"en": "You're now an admin", "de": "Du bist jetzt Admin"},
	"promoted.one":       {"en": "{names} is now an admin", "de": "{names} ist jetzt Admin"},
	"promoted.other":     {"en": "{names} are now admins", "de": "{names} sind jetzt Admins"},

	"missedVoiceCall":      {"en": "Missed voice call", "de": "Verpasster Sprachanruf"},
	"missedVideoCall":      {"en": "Missed video call", "de": "Verpasster Videoanruf"},
	"missedGroupVoiceCall": {"en": "Missed group voice call", "de": "Verpasster Gruppen-Sprachanruf"},
	"missedGroupVideoCall": {"en": "Missed group video call", "de": "Verpasster Gruppen-Videoanruf"},
	"liveLocation":         {"en": "Live location", "de": "Live-Standort"},
	"groupInvite":          {"en": `Invitation to join the group "{name}"`, "de": "Einladung zur Gruppe „{name}“"},

	"notLinked":            {"en": "not linked to a phone yet", "de": "noch nicht mit einem Telefon verknüpft"},
	"alreadyLinked":        {"en": "already linked", "de": "bereits verknüpft"},
	"notConnected":         {"en": "not connected to WhatsApp", "de": "nicht mit WhatsApp verbunden"},
	"notConnectedYet":      {"en": "not connected to WhatsApp yet, try again in a moment", "de": "noch nicht mit WhatsApp verbunden, versuch es gleich noch einmal"},
	"notOnWhatsApp":        {"en": "this number is not on WhatsApp", "de": "diese Nummer ist nicht bei WhatsApp"},
	"noSuchChat":           {"en": "no such chat", "de": "diesen Chat gibt es nicht"},
	"messageNotFound":      {"en": "message not found", "de": "Nachricht nicht gefunden"},
	"noFile":               {"en": "this message has no file to download", "de": "diese Nachricht hat keine Datei zum Herunterladen"},
	"noPreview":            {"en": "this message has no preview", "de": "diese Nachricht hat keine Vorschau"},
	"fileGone":             {"en": "this file is no longer on your phone", "de": "diese Datei ist nicht mehr auf deinem Telefon"},
	"phoneNoAnswer":        {"en": "your phone did not answer. Make sure it is online and try again", "de": "dein Telefon hat nicht geantwortet. Stell sicher, dass es online ist, und versuch es noch einmal"},
	"nothingToSend":        {"en": "nothing to send", "de": "nichts zu senden"},
	"cannotSendFolder":     {"en": "folders cannot be sent", "de": "Ordner können nicht gesendet werden"},
	"storeFailed":          {"en": "failed to store the message", "de": "die Nachricht konnte nicht gespeichert werden"},
	"cannotResend":         {"en": "this message can't be sent again; send it anew", "de": "diese Nachricht kann nicht erneut gesendet werden; sende sie neu"},
	"editOwnTextOnly":      {"en": "only your own text messages can be edited", "de": "nur deine eigenen Textnachrichten können bearbeitet werden"},
	"editTooLate":          {"en": "messages can only be edited for 20 minutes after sending", "de": "Nachrichten können nur bis 20 Minuten nach dem Senden bearbeitet werden"},
	"revokeOwnOnly":        {"en": "only your own messages can be deleted for everyone", "de": "nur deine eigenen Nachrichten können für alle gelöscht werden"},
	"cannotPin":            {"en": "this message can't be pinned", "de": "diese Nachricht kann nicht angeheftet werden"},
	"cannotKeep":           {"en": "this message can't be kept", "de": "diese Nachricht kann nicht behalten werden"},
	"unkeepSenderOnly":     {"en": "only who sent a message can stop keeping it", "de": "nur wer eine Nachricht gesendet hat, kann sie nicht mehr behalten"},
	"cannotForward":        {"en": "this message can't be forwarded", "de": "diese Nachricht kann nicht weitergeleitet werden"},
	"callPeopleOnly":       {"en": "only people can be called", "de": "nur Personen können angerufen werden"},
	"noCallDevices":        {"en": "this person has no devices that take calls", "de": "diese Person hat keine Geräte, die Anrufe annehmen"},
	"noAnswerFromServer":   {"en": "no answer from WhatsApp", "de": "keine Antwort von WhatsApp"},
	"noMessagesToGoOnFrom": {"en": "no messages to continue from", "de": "keine Nachrichten, an die angeknüpft werden kann"},
}

// tr is the text for a key with its values filled in, given as name and
// value in turn: tr("added", "actor", "Anna", "names", "Bob").
func tr(key string, values ...string) string {
	text, ok := texts[key][lang]
	if !ok {
		text, ok = texts[key]["en"]
	}
	if !ok {
		return key
	}
	pairs := make([]string, 0, len(values))
	for i := 0; i+1 < len(values); i += 2 {
		pairs = append(pairs, "{"+values[i]+"}", values[i+1])
	}
	return strings.NewReplacer(pairs...).Replace(text)
}

// userError is an error the app shows to the user as it is, so it is in the
// app's language.
type userError string

func (e userError) Error() string { return tr(string(e)) }

// people is a list of people a notice is about: the names of others, and
// whether the user is among them.
type people struct {
	names []string
	me    bool
}

// join lists the people as "Anna, Bob and you", with you as the word for the user.
func (p people) join(you string) string {
	names := p.names
	if p.me {
		names = append(slices.Clone(names), you)
	}
	switch len(names) {
	case 0:
		return tr("someone")
	case 1:
		return names[0]
	default:
		return tr("list", "rest", strings.Join(names[:len(names)-1], ", "), "last", names[len(names)-1])
	}
}

func (p people) count() int {
	if p.me {
		return len(p.names) + 1
	}
	return len(p.names)
}

// didNotice is a notice of what someone did, such as changing the group name.
func didNotice(key, actor string, actorIsMe bool, values ...string) string {
	if actorIsMe {
		return tr(key+".you", values...)
	}
	return tr(key, append([]string{"actor", actor}, values...)...)
}

// didToNotice is a notice of what someone did to people, such as adding them.
func didToNotice(key, actor string, actorIsMe bool, p people) string {
	return didNotice(key, actor, actorIsMe, "names", p.join(tr("youObject")))
}

// aboutNotice is a notice about people, such as who left.
func aboutNotice(key string, p people) string {
	switch {
	case p.me && len(p.names) == 0:
		return tr(key + ".you")
	case p.count() == 1:
		return tr(key+".one", "names", p.join(tr("youSubject")))
	default:
		return tr(key+".other", "names", p.join(tr("youSubject")))
	}
}
