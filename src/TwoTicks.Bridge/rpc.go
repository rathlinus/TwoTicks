package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"sync"
	"time"
)

type request struct {
	ID     int64           `json:"id"`
	Method string          `json:"method"`
	Params json.RawMessage `json:"params"`
}

type response struct {
	ID     int64  `json:"id"`
	Result any    `json:"result"`
	Error  string `json:"error,omitempty"`
}

type eventLine struct {
	Event string `json:"event"`
	Data  any    `json:"data,omitempty"`
}

type handler func(ctx context.Context, params json.RawMessage) (any, error)

// output writes whole lines to the app. Responses and events come from many
// goroutines, so every line is written under a lock.
type output struct {
	mu sync.Mutex
	w  *bufio.Writer
}

func newOutput(w io.Writer) *output {
	return &output{w: bufio.NewWriterSize(w, 64<<10)}
}

func (o *output) write(v any) {
	data, err := json.Marshal(v)
	if err != nil {
		data, _ = json.Marshal(eventLine{Event: "error", Data: map[string]string{"message": err.Error()}})
	}
	o.mu.Lock()
	defer o.mu.Unlock()
	_, _ = o.w.Write(data)
	_ = o.w.WriteByte('\n')
	_ = o.w.Flush()
}

func (o *output) event(name string, data any) {
	o.write(eventLine{Event: name, Data: data})
}

// serve reads requests until the input closes. Each request runs on its own
// goroutine, so a slow download does not hold up sending a message.
func (b *Bridge) serve(r io.Reader) {
	reader := bufio.NewReaderSize(r, 1<<20)
	for {
		line, err := reader.ReadBytes('\n')
		if line = bytes.TrimSpace(line); len(line) > 0 {
			var req request
			if jsonErr := json.Unmarshal(line, &req); jsonErr != nil {
				b.log.Warnf("Ignoring a line that is not a request: %v", jsonErr)
			} else {
				go b.handle(req)
			}
		}
		if err != nil {
			return
		}
	}
}

func (b *Bridge) handle(req request) {
	resp := response{ID: req.ID}
	defer func() {
		if p := recover(); p != nil {
			b.log.Errorf("Panic in %s: %v", req.Method, p)
			resp.Result = nil
			resp.Error = fmt.Sprintf("internal error: %v", p)
		}
		b.out.write(resp)
	}()

	h, ok := b.handlers[req.Method]
	if !ok {
		resp.Error = "unknown method " + req.Method
		return
	}
	started := time.Now()
	result, err := h(b.ctx, req.Params)
	if elapsed := time.Since(started); elapsed > 300*time.Millisecond {
		b.log.Infof("Slow request: %s took %s", req.Method, elapsed.Round(time.Millisecond))
	}
	if err != nil {
		resp.Error = err.Error()
		return
	}
	resp.Result = result
}

// params decodes the parameters of a request into T.
func params[T any](raw json.RawMessage) (T, error) {
	var p T
	if len(raw) == 0 || string(raw) == "null" {
		return p, nil
	}
	if err := json.Unmarshal(raw, &p); err != nil {
		return p, fmt.Errorf("bad parameters: %w", err)
	}
	return p, nil
}
