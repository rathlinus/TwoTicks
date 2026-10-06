package main

import (
	"fmt"
	"io"
	"os"
	"sync"
	"time"

	waLog "go.mau.fi/whatsmeow/util/log"
)

// The log grows until it reaches this size; the next start moves it to
// bridge.log.old and begins a new one.
const maxLogSize = 4 << 20

// fileLogger writes the helper's log to a file. Standard output belongs to the
// protocol with the app, so nothing else may write there.
type fileLogger struct {
	mu     *sync.Mutex
	w      io.Writer
	module string
	debug  bool
}

var _ waLog.Logger = (*fileLogger)(nil)

func newFileLogger(path string, debug bool) *fileLogger {
	if info, err := os.Stat(path); err == nil && info.Size() > maxLogSize {
		_ = os.Rename(path, path+".old")
	}
	var w io.Writer = io.Discard
	if f, err := os.OpenFile(path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o600); err == nil {
		w = f
	}
	return &fileLogger{mu: &sync.Mutex{}, w: w, module: "Bridge", debug: debug}
}

func (l *fileLogger) write(level, msg string, args []any) {
	line := fmt.Sprintf("%s [%s %s] %s\n", time.Now().Format("2006-01-02 15:04:05.000"), l.module, level, fmt.Sprintf(msg, args...))
	l.mu.Lock()
	defer l.mu.Unlock()
	_, _ = io.WriteString(l.w, line)
}

func (l *fileLogger) Errorf(msg string, args ...any) { l.write("ERROR", msg, args) }
func (l *fileLogger) Warnf(msg string, args ...any)  { l.write("WARN", msg, args) }
func (l *fileLogger) Infof(msg string, args ...any)  { l.write("INFO", msg, args) }

func (l *fileLogger) Debugf(msg string, args ...any) {
	if l.debug {
		l.write("DEBUG", msg, args)
	}
}

func (l *fileLogger) Sub(module string) waLog.Logger {
	return &fileLogger{mu: l.mu, w: l.w, module: l.module + "/" + module, debug: l.debug}
}
