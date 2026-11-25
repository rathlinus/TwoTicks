package main

import (
	"slices"
	"testing"
)

func TestMentionedKeepsThePeopleInTheText(t *testing.T) {
	jids := []string{
		"447700900111@s.whatsapp.net",
		"100000000000002:3@lid",
		"447700900222@s.whatsapp.net",
		"447700900111@s.whatsapp.net",
		"44770090@s.whatsapp.net",
		"not a jid@@",
	}
	got := mentioned("hi @447700900111, and @100000000000002!", jids)
	want := []string{"447700900111@s.whatsapp.net", "100000000000002@lid"}
	if !slices.Equal(got, want) {
		t.Errorf("mentioned = %v, want %v", got, want)
	}
	if got := mentioned("no one here", jids); len(got) != 0 {
		t.Errorf("mentioned without any = %v", got)
	}
	// A number that only starts another one is found where it stands alone.
	if got := mentioned("@447700900111 @44770090", []string{"44770090@s.whatsapp.net"}); len(got) != 1 {
		t.Errorf("mentioned behind a longer number = %v", got)
	}
}

func TestWithMentions(t *testing.T) {
	if withMentions(nil, nil) != nil {
		t.Error("a message without mentions got a context")
	}
	ci := withMentions(nil, []string{"1@lid"})
	if ci == nil || !slices.Equal(ci.GetMentionedJID(), []string{"1@lid"}) {
		t.Errorf("context = %v", ci)
	}
}

func TestNamesForMentions(t *testing.T) {
	names := map[string]string{"447700900111": "Alice", "100000000000002": "Bob"}
	got := namesForMentions("@447700900111 and @100000000000002, not @4477 or a@b.c @", names)
	want := "@Alice and @Bob, not @4477 or a@b.c @"
	if got != want {
		t.Errorf("namesForMentions = %q, want %q", got, want)
	}
	if got := namesForMentions("@447700900111", nil); got != "@447700900111" {
		t.Errorf("without names = %q", got)
	}
}
