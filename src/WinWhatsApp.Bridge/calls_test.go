package main

import (
	"testing"

	waBinary "go.mau.fi/whatsmeow/binary"
	"go.mau.fi/whatsmeow/binary/token"
	"go.mau.fi/whatsmeow/types"
)

// The engine cannot read an offer's ack whose <device> list has a main
// device written as a person, as whatsmeow writes it back.
func TestMarshalForEngineWritesDevices(t *testing.T) {
	lid := types.JID{User: "133358801658027", Server: types.HiddenUserServer}
	node := waBinary.Node{Tag: "ack", Content: []waBinary.Node{{Tag: "user", Attrs: waBinary.Attrs{"jid": lid},
		Content: []waBinary.Node{{Tag: "device", Attrs: waBinary.Attrs{"jid": lid}}}}}}
	data, err := marshalForEngine(node)
	if err != nil {
		t.Fatal(err)
	}
	devices := 0
	for i := 0; i+2 < len(data); i++ {
		if data[i] == token.ADJID && data[i+1] == types.LIDDomain && data[i+2] == 0 {
			devices++
		}
	}
	if devices != 1 {
		t.Fatalf("want the device and only it in the device form, got %d", devices)
	}
	back, err := waBinary.Unmarshal(data[1:])
	if err != nil {
		t.Fatal(err)
	}
	user := back.GetChildByTag("user")
	device := user.GetChildByTag("device").Attrs["jid"].(types.JID)
	if device.User != lid.User || device.Device != 0 || device.Server != types.HiddenUserServer {
		t.Fatalf("read back as %v", device)
	}
}
