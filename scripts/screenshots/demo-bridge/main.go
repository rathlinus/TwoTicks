// A stand-in for WinWhatsApp.Bridge that serves made-up chats from demo.json,
// for screenshots. It speaks the same line protocol as the real helper but
// never connects to WhatsApp. See ../README.md.
package main

import (
	"bufio"
	"bytes"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"
)

type demo struct {
	State    string                       `json:"state"`
	QR       string                       `json:"qr"`
	Me       map[string]string            `json:"me"`
	Chats    []map[string]any             `json:"chats"`
	Messages map[string][]map[string]any  `json:"messages"`
	Avatars  map[string]string            `json:"avatars"`
	Profiles map[string]map[string]any    `json:"profiles"`
	Groups   map[string]map[string]any    `json:"groups"`
	Contacts []map[string]string          `json:"contacts"`
	Presence map[string]map[string]any    `json:"presence"`
	Typing   []map[string]any             `json:"typing"`
}

var (
	mu  sync.Mutex
	out = bufio.NewWriter(os.Stdout)
)

func write(v any) {
	data, _ := json.Marshal(v)
	mu.Lock()
	defer mu.Unlock()
	out.Write(data)
	out.WriteByte('\n')
	out.Flush()
}

func event(name string, data any) { write(map[string]any{"event": name, "data": data}) }

func main() {
	dataDir := flag.String("data", "", "the folder with demo.json")
	flag.Bool("debug", false, "ignored")
	flag.Parse()

	raw, err := os.ReadFile(filepath.Join(*dataDir, "demo.json"))
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	var d demo
	if err := json.Unmarshal(raw, &d); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	if d.State == "" {
		d.State = "connected"
	}

	state := map[string]any{"state": d.State}
	if d.State != "qr" {
		state["me"] = d.Me
	}
	event("state", state)
	if d.State == "qr" {
		event("qr", map[string]any{"code": d.QR, "timeout": 600})
	}
	// Typing stops showing after a while unless it is repeated.
	go func() {
		for {
			for _, t := range d.Typing {
				event("typing", t)
			}
			time.Sleep(3 * time.Second)
		}
	}()

	reader := bufio.NewReaderSize(os.Stdin, 1<<20)
	for {
		line, err := reader.ReadBytes('\n')
		if line = bytes.TrimSpace(line); len(line) > 0 {
			var req struct {
				ID     int64           `json:"id"`
				Method string          `json:"method"`
				Params json.RawMessage `json:"params"`
			}
			if json.Unmarshal(line, &req) == nil {
				var p map[string]any
				_ = json.Unmarshal(req.Params, &p)
				write(map[string]any{"id": req.ID, "result": handle(&d, state, req.Method, p)})
			}
		}
		if err != nil {
			return
		}
	}
}

func str(p map[string]any, key string) string {
	s, _ := p[key].(string)
	return s
}

func handle(d *demo, state map[string]any, method string, p map[string]any) any {
	switch method {
	case "status":
		return state
	case "chats":
		return d.Chats
	case "chat":
		for _, c := range d.Chats {
			if c["jid"] == str(p, "chat") {
				return c
			}
		}
		return map[string]any{"jid": str(p, "chat")}
	case "messages":
		list := []map[string]any{}
		for _, m := range d.Messages[str(p, "chat")] {
			if media, _ := p["media"].(bool); media && m["kind"] != "image" && m["kind"] != "video" {
				continue
			}
			list = append(list, m)
		}
		return map[string]any{"messages": list, "hasOlder": false, "hasNewer": false}
	case "search":
		query := strings.ToLower(str(p, "query"))
		list := []map[string]any{}
		for _, messages := range d.Messages {
			for _, m := range messages {
				if text, _ := m["text"].(string); query != "" && strings.Contains(strings.ToLower(text), query) {
					list = append(list, m)
				}
			}
		}
		sort.Slice(list, func(i, j int) bool { return list[i]["ts"].(float64) > list[j]["ts"].(float64) })
		return list
	case "avatar", "picture":
		return map[string]string{"path": d.Avatars[str(p, "jid")]}
	case "profile":
		if profile, ok := d.Profiles[str(p, "jid")]; ok {
			return profile
		}
		return map[string]any{"jid": str(p, "jid"), "name": ""}
	case "groupInfo":
		return d.Groups[str(p, "chat")]
	case "contacts":
		return d.Contacts
	case "subscribe":
		if presence, ok := d.Presence[str(p, "chat")]; ok {
			go func() {
				time.Sleep(200 * time.Millisecond)
				event("presence", presence)
			}()
		}
		return true
	case "download":
		for _, m := range d.Messages[str(p, "chat")] {
			if m["id"] == str(p, "id") {
				if media, ok := m["media"].(map[string]any); ok {
					return map[string]any{"path": media["path"]}
				}
			}
		}
		return map[string]string{"path": ""}
	}
	return true
}
