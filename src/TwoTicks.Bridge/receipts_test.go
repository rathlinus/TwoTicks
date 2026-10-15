package main

import (
	"context"
	"testing"
)

func TestRecordReceiptKeepsWhenEachStepFirstHappened(t *testing.T) {
	ctx := context.Background()
	w, r, err := openDatabases(ctx, t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	defer w.Close()
	defer r.Close()
	record := func(user string, status int, at int64) {
		t.Helper()
		if err := recordReceipt(ctx, w, "c", "m", user, status, at); err != nil {
			t.Fatal(err)
		}
	}
	times := func(user string) (status int, delivered, read, played int64) {
		t.Helper()
		if err := w.QueryRowContext(ctx, `SELECT status, delivered_at, read_at, played_at FROM receipts WHERE chat = 'c' AND msg_id = 'm' AND user = ?`, user).
			Scan(&status, &delivered, &read, &played); err != nil {
			t.Fatal(err)
		}
		return
	}

	// Delivered, read, then delivered again from another device: the first times stay.
	record("anna", statusDelivered, 100)
	record("anna", statusRead, 200)
	record("anna", statusDelivered, 300)
	if status, delivered, read, played := times("anna"); status != statusRead || delivered != 100 || read != 200 || played != 0 {
		t.Errorf("anna: status %d, delivered %d, read %d, played %d", status, delivered, read, played)
	}

	// Played without the receipts before it: it stands for them.
	record("ben", statusPlayed, 500)
	if status, delivered, read, played := times("ben"); status != statusPlayed || delivered != 500 || read != 500 || played != 500 {
		t.Errorf("ben: status %d, delivered %d, read %d, played %d", status, delivered, read, played)
	}
}
