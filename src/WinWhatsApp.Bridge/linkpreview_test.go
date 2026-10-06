package main

import (
	"strings"
	"testing"
)

func TestPageMetaReadsTheHead(t *testing.T) {
	page := `<!doctype html><html><head>
		<title> Clean-room design -
		Wikipedia </title>
		<meta property="og:title" content="Clean-room design">
		<meta property="og:title" content="A second one">
		<META NAME="Description" CONTENT="Copying &amp; reverse engineering">
		<meta property="og:image" content="/static/picture.png" />
		</head><body><meta property="og:description" content="not in the head"></body></html>`
	meta := pageMeta(strings.NewReader(page))

	if got := meta["og:title"]; got != "Clean-room design" {
		t.Errorf("og:title = %q", got)
	}
	if got := meta["description"]; got != "Copying & reverse engineering" {
		t.Errorf("description = %q", got)
	}
	if got := meta["og:image"]; got != "/static/picture.png" {
		t.Errorf("og:image = %q", got)
	}
	if got := meta["title"]; got != "Clean-room design - Wikipedia" {
		t.Errorf("title = %q", got)
	}
	if _, ok := meta["og:description"]; ok {
		t.Error("read a meta tag from the body")
	}
}
