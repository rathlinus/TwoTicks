// A stand-in for WinWhatsApp.Bridge that serves made-up chats, for
// screenshots. It speaks the same line protocol as the real helper but never
// connects to WhatsApp. See ../README.md.
//
// WINWHATSAPP_DEMO is the folder with demo.json and its pictures.
// WINWHATSAPP_DEMO_QR=1 shows the linking screen instead of the chats.
// A file named demo-incoming in the app's data folder makes it send the
// incoming message of demo.json, as if it just arrived, and is then deleted.
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
	Now      int64                       `json:"now"`
	State    string                      `json:"state"`
	QR       string                      `json:"qr"`
	Me       map[string]string           `json:"me"`
	Chats    []map[string]any            `json:"chats"`
	Messages map[string][]map[string]any `json:"messages"`
	Avatars  map[string]string           `json:"avatars"`
	Profiles map[string]map[string]any   `json:"profiles"`
	Groups   map[string]map[string]any   `json:"groups"`
	Contacts []map[string]string         `json:"contacts"`
	Presence map[string]map[string]any   `json:"presence"`
	Typing   []map[string]any            `json:"typing"`
	Incoming map[string]any              `json:"incoming"`
}

var (
	mu  sync.Mutex
	out = bufio.NewWriter(os.Stdout)
	// Guards the demo's chats and messages, which the incoming message changes.
	dmu sync.Mutex
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
	data := flag.String("data", "", "the app's data folder, where demo-incoming is looked for")
	flag.Bool("debug", false, "ignored")
	flag.String("lang", "", "ignored")
	flag.Parse()

	dir, _ := filepath.Abs(os.Getenv("WINWHATSAPP_DEMO"))
	raw, err := os.ReadFile(filepath.Join(dir, "demo.json"))
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	var d demo
	if err := json.Unmarshal(raw, &d); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	d.State = "connected"
	if os.Getenv("WINWHATSAPP_DEMO_QR") == "1" {
		d.State = "qr"
	}
	fix(dir, time.Now().Unix()-d.Now, &d)

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
	if *data != "" && d.Incoming != nil {
		go watchIncoming(&d, filepath.Join(*data, "demo-incoming"))
	}

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
				dmu.Lock()
				result := handle(&d, state, req.Method, p)
				dmu.Unlock()
				write(map[string]any{"id": req.ID, "result": result})
			}
		}
		if err != nil {
			return
		}
	}
}

// watchIncoming sends the incoming message once the trigger file shows up,
// with the message event and the updated chat, as the real helper does.
func watchIncoming(d *demo, trigger string) {
	for {
		time.Sleep(200 * time.Millisecond)
		if os.Remove(trigger) != nil {
			continue
		}
		dmu.Lock()
		m := map[string]any{}
		for key, value := range d.Incoming {
			m[key] = value
		}
		chat := str(m, "chat")
		m["ts"] = float64(time.Now().Unix())
		m["notify"] = true
		d.Messages[chat] = append(d.Messages[chat], m)
		var updated map[string]any
		for _, c := range d.Chats {
			if c["jid"] == chat {
				unread, _ := c["unread"].(float64)
				c["unread"] = unread + 1
				c["ts"] = m["ts"]
				last := map[string]any{"id": m["id"], "fromMe": false, "kind": m["kind"], "status": 0, "text": m["text"]}
				if name := str(m, "senderName"); name != "" {
					last["senderName"] = strings.Fields(name)[0]
				}
				c["last"] = last
				updated = c
			}
		}
		event("message", m)
		if updated != nil {
			event("chat", updated)
		}
		dmu.Unlock()
	}
}

// fix moves every time forward to now and makes every picture's path absolute.
func fix(dir string, shift int64, d *demo) {
	var walk func(v any)
	walk = func(v any) {
		switch v := v.(type) {
		case map[string]any:
			for key, value := range v {
				switch value := value.(type) {
				case float64:
					// mutedUntil -1 means for ever.
					if (key == "ts" || key == "pinned" || key == "created" || key == "mutedUntil") && value > 0 {
						v[key] = value + float64(shift)
					}
				case string:
					if (key == "path" || key == "avatar") && value != "" && !filepath.IsAbs(value) {
						v[key] = filepath.Join(dir, value)
					}
				default:
					walk(value)
				}
			}
		case []any:
			for _, item := range v {
				walk(item)
			}
		}
	}
	for _, c := range d.Chats {
		walk(c)
	}
	for _, messages := range d.Messages {
		for _, m := range messages {
			walk(m)
		}
	}
	for _, g := range d.Groups {
		walk(g)
	}
	for jid, path := range d.Avatars {
		d.Avatars[jid] = filepath.Join(dir, path)
	}
}

// matches reports whether a message belongs in one of the lists of a chat's info.
func matches(m map[string]any, filter string) bool {
	text, _ := m["text"].(string)
	switch filter {
	case "media":
		return m["kind"] == "image" || m["kind"] == "video" || m["kind"] == "gif"
	case "docs":
		return m["kind"] == "document"
	case "links":
		return m["link"] != nil || strings.Contains(text, "http://") || strings.Contains(text, "https://") || strings.Contains(text, "www.")
	case "starred", "kept":
		on, _ := m[filter].(bool)
		return on
	}
	return false
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
		filter := str(p, "filter")
		if media, _ := p["media"].(bool); media {
			filter = "media"
		}
		list := []map[string]any{}
		for _, m := range d.Messages[str(p, "chat")] {
			if filter == "" || matches(m, filter) {
				list = append(list, m)
			}
		}
		return map[string]any{"messages": list, "hasOlder": false, "hasNewer": false}
	case "chatInfo":
		info := map[string]any{}
		recent := []map[string]any{}
		messages := d.Messages[str(p, "chat")]
		for _, filter := range []string{"media", "docs", "links", "starred", "kept"} {
			n := 0
			for _, m := range messages {
				if matches(m, filter) {
					n++
				}
			}
			info[filter] = n
		}
		for i := len(messages) - 1; i >= 0 && len(recent) < 6; i-- {
			if matches(messages[i], "media") {
				recent = append(recent, messages[i])
			}
		}
		info["recent"] = recent
		return info
	case "commonGroups":
		list := []map[string]any{}
		for jid, g := range d.Groups {
			members, _ := g["members"].([]any)
			found := false
			var names []string
			for _, member := range members {
				m, _ := member.(map[string]any)
				if m["jid"] == str(p, "jid") {
					found = true
				}
				if me, _ := m["me"].(bool); !me {
					names = append(names, str(m, "name"))
				}
			}
			if found {
				list = append(list, map[string]any{"jid": jid, "name": g["name"], "members": strings.Join(append(names, "You"), ", ")})
			}
		}
		return list
	case "search":
		query := strings.ToLower(str(p, "query"))
		list := []map[string]any{}
		for chat, messages := range d.Messages {
			if only := str(p, "chat"); only != "" && only != chat {
				continue
			}
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
	// A call in the demo rings and nobody answers: enough to show the call
	// window and to have the calling engine start.
	case "callIdentity":
		return map[string]string{"pn": "4915550100:1@c.us", "pnUser": "4915550100@c.us", "lid": "100000000000001:1@lid", "countryCode": "49"}
	case "callPrepare":
		return map[string]any{"peer": "100000000000002@lid", "peerPn": "4915550101@c.us", "devices": []string{"100000000000002@lid"}, "name": ""}
	case "callSend":
		return map[string]string{"node": "", "error": "0"}
	}
	return true
}
