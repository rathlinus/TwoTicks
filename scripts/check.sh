#!/usr/bin/env bash
#
# Starts the built app on macOS or Linux and lets it check itself: it runs on
# the made-up chats of scripts/screenshots, never on a real account, opens a
# chat, and writes a picture of its window and a report to artifacts/check.
# Fails when the chats did not show.
#
#   scripts/check.sh [app folder]     the folder scripts/build.sh made; artifacts/app when left out
#
# On macOS the app bundle scripts/release.sh made is checked as well, when
# there is one, with its results in artifacts/check/bundle: the icon in the
# menu bar and notifications only exist for the bundle.
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
# The stand-in for the WhatsApp helper; see scripts/screenshots/README.md.
(cd "$repo/scripts/screenshots/demo-bridge" && go build -o "$work/WinWhatsApp.Bridge" .)

# Runs one copy of the app and waits for it to finish its check.
#   check <program> <folder for the results>
check() {
    local program="$1" results="$2" data status=0 pid
    data="$(mktemp -d "$work/data.XXXXXX")"
    cat > "$data/settings.json" << 'EOF'
{ "Notifications": true, "CloseToTray": false, "Theme": "Light", "ChatListWidth": 400,
  "Window": { "X": 40, "Y": 40, "Width": 1400, "Height": 900, "Maximized": false } }
EOF
    mkdir -p "$results"

    local run=("$program")
    if [ "$(uname -s)" = Linux ] && [ -z "${DISPLAY:-}" ]; then
        run=(xvfb-run -a -s '-screen 0 1480x980x24' "${run[@]}")
    fi

    WINWHATSAPP_DATA="$data" WINWHATSAPP_DEMO="$repo/scripts/screenshots/demo" WINWHATSAPP_CHECK="$results" \
        "${run[@]}" > "$results/output.txt" 2>&1 &
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

    cp "$data/app.log" "$results/app.log" 2> /dev/null || true
    echo "--- report: $program ---"
    cat "$results/report.txt" 2> /dev/null || echo '(none)'
    echo '--- output ---'
    tail -n 40 "$results/output.txt" 2> /dev/null || true
    echo '--- log ---'
    tail -n 40 "$results/app.log" 2> /dev/null || true
    return "$status"
}

rm -rf "$out"
status=0

cp -R "$app" "$work/app"
cp "$work/WinWhatsApp.Bridge" "$work/app/WinWhatsApp.Bridge"
check "$work/app/WinWhatsApp" "$out" || status=$?

if [ "$(uname -s)" = Darwin ]; then
    bundle="$(find "$repo/artifacts/bundle" -maxdepth 4 -name 'WinWhatsApp.app' -type d 2> /dev/null | head -n 1)"
    if [ -n "$bundle" ]; then
        cp -R "$bundle" "$work/WinWhatsApp.app"
        cp "$work/WinWhatsApp.Bridge" "$work/WinWhatsApp.app/Contents/Resources/WinWhatsApp.Bridge"
        # The helper changed, so the signature has to be made again.
        codesign --force --deep --sign - "$work/WinWhatsApp.app" 2> /dev/null || true
        check "$work/WinWhatsApp.app/Contents/MacOS/WinWhatsApp" "$out/bundle" || status=$?
    fi
fi

exit "$status"
