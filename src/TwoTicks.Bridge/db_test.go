package main

import (
	"context"
	"path/filepath"
	"testing"
)

func TestRebasePathsFollowsTheDataFolder(t *testing.T) {
	ctx := context.Background()
	former := filepath.Join(t.TempDir(), "WinWhatsApp")
	current := filepath.Join(t.TempDir(), "TwoTicks")
	sent := filepath.Join(t.TempDir(), "media", "holiday.jpg")

	w, r, err := openDatabases(ctx, t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	defer w.Close()
	defer r.Close()
	exec := func(query string, args ...any) {
		t.Helper()
		if _, err := w.ExecContext(ctx, query, args...); err != nil {
			t.Fatal(err)
		}
	}
	paths := func() (picture, downloaded, own string) {
		t.Helper()
		for query, target := range map[string]*string{
			`SELECT path FROM avatars WHERE jid = 'a'`:       &picture,
			`SELECT local_path FROM messages WHERE id = '1'`: &downloaded,
			`SELECT local_path FROM messages WHERE id = '2'`: &own,
		} {
			if err := w.QueryRowContext(ctx, query).Scan(target); err != nil {
				t.Fatal(err)
			}
		}
		return
	}
	message := `INSERT INTO messages (chat, id, sender, from_me, ts, kind, local_path) VALUES ('c', ?, 's', 0, 1, 'image', ?)`
	exec(`INSERT INTO avatars (jid, path) VALUES ('a', ?)`, filepath.Join(former, "avatars", "a.jpg"))
	exec(`INSERT INTO avatars (jid, path) VALUES ('none', '')`)
	exec(message, "1", filepath.Join(former, "media", "1.jpg"))
	exec(message, "2", sent)

	// A database from before the folder was written down, in a folder that was renamed.
	if err := rebasePaths(ctx, w, current); err != nil {
		t.Fatal(err)
	}
	picture, downloaded, own := paths()
	if picture != filepath.Join(current, "avatars", "a.jpg") || downloaded != filepath.Join(current, "media", "1.jpg") {
		t.Errorf("the paths did not follow: %s, %s", picture, downloaded)
	}
	if own != sent {
		t.Errorf("a file sent from elsewhere moved: %s", own)
	}

	// Nothing to do the next time, and a later move is known by what was written down.
	if err := rebasePaths(ctx, w, current); err != nil {
		t.Fatal(err)
	}
	later := filepath.Join(t.TempDir(), "Später")
	if err := rebasePaths(ctx, w, later); err != nil {
		t.Fatal(err)
	}
	picture, downloaded, own = paths()
	if picture != filepath.Join(later, "avatars", "a.jpg") || downloaded != filepath.Join(later, "media", "1.jpg") || own != sent {
		t.Errorf("the paths did not follow a second move: %s, %s, %s", picture, downloaded, own)
	}
}
