// The TwoTicks helper: a WhatsApp client without the web page.
//
// The app starts it as a child process and talks to it over standard input
// and output, one JSON object per line. Requests carry an id, a method and
// parameters; every request gets one response with the same id. Lines with an
// "event" field instead of an id are things that happened on WhatsApp's side,
// such as a new message.
//
// The helper keeps the WhatsApp session and its own database of chats and
// messages in the data folder it is given. The app never touches those files.
package main

import (
	"flag"
	"fmt"
	"os"
	"path/filepath"

	"go.mau.fi/whatsmeow/proto/waCompanionReg"
	"go.mau.fi/whatsmeow/store"
	"google.golang.org/protobuf/proto"
)

// Set at build time with -ldflags "-X main.version=1.2.3".
var version = "0.1.0"

func main() {
	dataDir := flag.String("data", "", "the folder for the session, the message database and downloaded media")
	debug := flag.Bool("debug", false, "write the protocol's debug messages to the log")
	flag.StringVar(&lang, "lang", lang, "the language of the text it writes itself, such as de")
	flag.Parse()

	if *dataDir == "" {
		fmt.Fprintln(os.Stderr, "usage: TwoTicks.Bridge --data <folder>")
		os.Exit(2)
	}
	if err := os.MkdirAll(*dataDir, 0o700); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}

	// The name the phone lists this device under in Linked devices.
	store.SetOSInfo("TwoTicks", [3]uint32{0, 1, 0})
	store.DeviceProps.PlatformType = waCompanionReg.DeviceProps_DESKTOP.Enum()
	store.DeviceProps.RequireFullSync = proto.Bool(false)

	log := newFileLogger(filepath.Join(*dataDir, "bridge.log"), *debug)
	log.Infof("Starting TwoTicks.Bridge %s", version)

	out := newOutput(os.Stdout)
	b, err := newBridge(*dataDir, log, out)
	if err != nil {
		log.Errorf("Failed to start: %v", err)
		out.event("fatal", map[string]string{"message": err.Error()})
		os.Exit(1)
	}

	go b.start()

	// Standard input closes when the app exits, for whatever reason.
	b.serve(os.Stdin)
	b.shutdown()
	log.Infof("Stopped")
}
