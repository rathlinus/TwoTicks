#!/usr/bin/env bash
#
# Builds TwoTicks for macOS or Linux: the app and the WhatsApp helper next
# to it. The same as build.ps1 does for Windows.
#
# Runs the tests, builds the Go helper and the fonts, and publishes the app to
# artifacts/app. The folder runs as it is, without .NET or anything else
# installed.
#
# Needs the .NET 10 SDK, Go, and Python 3 with fonttools and pillow.
#
#   scripts/build.sh [--version 1.2.0] [--arch x64|arm64] [--skip-tests] [--run]
#
#   --version     The version to stamp into the programs. Left out, a
#                 development build gets the one in Directory.Build.props.
#   --arch        The processor to build for. Left out, the one of this machine.
#   --skip-tests  Publish without running the tests first.
#   --run         Start the freshly built TwoTicks afterwards.

set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version=""
arch=""
skip_tests=false
run=false

while [ $# -gt 0 ]; do
    case "$1" in
        --version) version="$2"; shift 2 ;;
        --arch) arch="$2"; shift 2 ;;
        --skip-tests) skip_tests=true; shift ;;
        --run) run=true; shift ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

case "$(uname -s)" in
    Darwin) os=osx ;;
    Linux) os=linux ;;
    *) echo "This script builds on macOS and Linux. On Windows, use scripts\\build.ps1." >&2; exit 1 ;;
esac
if [ -z "$arch" ]; then
    case "$(uname -m)" in
        x86_64 | amd64) arch=x64 ;;
        arm64 | aarch64) arch=arm64 ;;
        *) echo "Unknown processor: $(uname -m)" >&2; exit 1 ;;
    esac
fi
rid="$os-$arch"

for tool in dotnet go python3; do
    if ! command -v "$tool" > /dev/null; then
        echo "$tool was not found. See docs/development.md for what to install." >&2
        exit 1
    fi
done
if ! python3 -c 'import fontTools, PIL' 2> /dev/null; then
    echo 'Python needs fonttools and pillow: pip install fonttools pillow' >&2
    exit 1
fi

if [ "$skip_tests" = false ]; then
    echo 'Running the tests...'
    dotnet test "$repo/tests/TwoTicks.Core.Tests" --nologo -v:q
    (cd "$repo/src/TwoTicks.Bridge" && go vet ./...)
fi

output="$repo/artifacts/app"
# Publishing does not remove files an earlier build left there.
rm -rf "$output"
# The project builds the helper before it compiles; see BuildBridge in
# TwoTicks.Desktop.csproj. Removing it keeps a release from reusing a
# helper built with another version.
rm -rf "$repo/artifacts/bridge"

version_arguments=()
if [ -n "$version" ]; then
    version_arguments=("-p:Version=$version")
fi

echo "Publishing TwoTicks for $rid..."
dotnet publish "$repo/src/TwoTicks.Desktop/TwoTicks.Desktop.csproj" \
    -c Release -r "$rid" --self-contained -o "$output" --nologo -v:q "${version_arguments[@]}"

echo "Built: $output/TwoTicks"

if [ "$run" = true ]; then
    "$output/TwoTicks" &
fi
