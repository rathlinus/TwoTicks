package main

import (
	"regexp"
	"slices"
	"testing"
)

func TestTextsHaveEveryLanguage(t *testing.T) {
	placeholder := regexp.MustCompile(`\{[a-z]+\}`)
	for key, byLang := range texts {
		want := placeholder.FindAllString(byLang["en"], -1)
		slices.Sort(want)
		for _, l := range []string{"en", "de"} {
			text, ok := byLang[l]
			if !ok {
				t.Errorf("%s has no %s text", key, l)
				continue
			}
			got := placeholder.FindAllString(text, -1)
			slices.Sort(got)
			if !slices.Equal(got, want) {
				t.Errorf("%s in %s has %v, English has %v", key, l, got, want)
			}
		}
	}
}

func TestNotices(t *testing.T) {
	defer func(old string) { lang = old }(lang)
	cases := []struct {
		lang, want string
		notice     func() string
	}{
		{"en", "You added Anna and Bob", func() string { return didToNotice("added", "", true, people{names: []string{"Anna", "Bob"}}) }},
		{"en", "Anna added you", func() string { return didToNotice("added", "Anna", false, people{me: true}) }},
		{"de", "Anna hat dich hinzugefügt", func() string { return didToNotice("added", "Anna", false, people{me: true}) }},
		{"de", "Du hast die Gruppe verlassen", func() string { return aboutNotice("left", people{me: true}) }},
		{"de", "Anna, Bob und du sind jetzt Admins", func() string { return aboutNotice("promoted", people{names: []string{"Anna", "Bob"}, me: true}) }},
		{"de", "Anna ist beigetreten", func() string { return aboutNotice("joined", people{names: []string{"Anna"}}) }},
		{"de", "nur Personen können angerufen werden", func() string { return userError("callPeopleOnly").Error() }},
	}
	for _, c := range cases {
		lang = c.lang
		if got := c.notice(); got != c.want {
			t.Errorf("got %q, want %q", got, c.want)
		}
	}
}
