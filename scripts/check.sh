#!/usr/bin/env bash
#
# Starts the built app on macOS or Linux and lets it check itself: it runs on
# the made-up chats of scripts/screenshots, never on a real account, opens a
# chat, and writes a picture of its window and a report to artifacts/check.
# Fails when the chats did not show.
#
#   scripts/check.sh [app folder]     the folder scripts/build.sh made; artifacts/app when left out
#
# Needs Go for the stand-in helper. On Linux without a screen it needs Xvfb.

set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
app="${1:-$repo/artifacts/app}"
out="$repo/artifacts/check"

if [ ! -x "$app/WinWhatsApp" ]; then
    echo "No app in $app. Build it first: scripts/build.sh" >&2
    exit 1
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cp -R "$app" "$work/app"
# The stand-in for the WhatsApp helper; see scripts/screenshots/README.md.
(cd "$repo/scripts/screenshots/demo-bridge" && go build -o "$work/app/WinWhatsApp.Bridge" .)

mkdir -p "$work/data"
cat > "$work/data/settings.json" << 'EOF'
{ "Notifications": true, "CloseToTray": false, "Theme": "Light", "ChatListWidth": 400,
  "Window": { "X": 40, "Y": 40, "Width": 1400, "Height": 900, "Maximized": false } }
EOF

rm -rf "$out"
mkdir -p "$out"
export WINWHATSAPP_DATA="$work/data"
export WINWHATSAPP_DEMO="$repo/scripts/screenshots/demo"
export WINWHATSAPP_CHECK="$out"

run=("$work/app/WinWhatsApp")
if [ "$(uname -s)" = Linux ] && [ -z "${DISPLAY:-}" ]; then
    run=(xvfb-run -a -s '-screen 0 1480x980x24' "${run[@]}")
fi

status=0
"${run[@]}" > "$out/output.txt" 2>&1 &
pid=$!
# It quits by itself; a hung start must not hold up the build.
for _ in $(seq 1 120); do
    if ! kill -0 "$pid" 2> /dev/null; then
        break
    fi
    sleep 1
done
if kill -0 "$pid" 2> /dev/null; then
    echo 'The app did not finish its check in two minutes.' >&2
    kill -9 "$pid" 2> /dev/null || true
    status=1
else
    wait "$pid" || status=$?
fi

cp "$work/data/app.log" "$out/app.log" 2> /dev/null || true
echo '--- report ---'
cat "$out/report.txt" 2> /dev/null || echo '(none)'
echo '--- output ---'
tail -n 40 "$out/output.txt" 2> /dev/null || true
echo '--- log ---'
tail -n 40 "$out/app.log" 2> /dev/null || true
exit "$status"
