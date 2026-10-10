#!/usr/bin/env bash
#
# Builds what a release consists of on macOS or Linux, as release.ps1 does for
# Windows, and collects the files to publish in artifacts/release:
#
#   Linux   TwoTicks-<version>-linux-<arch>.tar.gz   the app folder, which runs after unpacking
#           twoticks_<version>_<arch>.deb            a package for Debian, Ubuntu and their relatives
#   macOS   TwoTicks-<version>-macos-<arch>.dmg      a disk image with TwoTicks.app
#
#   scripts/release.sh --version 1.2.0 [--arch x64|arm64]
#
# The .deb needs dpkg-deb, which Debian and Ubuntu have. The disk image needs
# the tools that come with macOS.

set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version=""
arch=""

while [ $# -gt 0 ]; do
    case "$1" in
        --version) version="$2"; shift 2 ;;
        --arch) arch="$2"; shift 2 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done
if ! [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo 'Give the release version, such as --version 1.2.0.' >&2
    exit 2
fi
if [ -z "$arch" ]; then
    case "$(uname -m)" in
        x86_64 | amd64) arch=x64 ;;
        *) arch=arm64 ;;
    esac
fi

app="$repo/artifacts/app"
release="$repo/artifacts/release"
packaging="$repo/packaging"
mkdir -p "$release"

"$repo/scripts/build.sh" --version "$version" --arch "$arch" --skip-tests

# Debug symbols, which nobody running the program needs.
find "$app" -name '*.pdb' -delete

if [ "$(uname -s)" = Darwin ]; then
    echo 'Making the app bundle...'
    # Uno makes TwoTicks.app from a publish of its own; the folder built
    # above is what the checks run on.
    bundles="$repo/artifacts/bundle"
    rm -rf "$bundles"
    dotnet publish "$repo/src/TwoTicks.Desktop/TwoTicks.Desktop.csproj" \
        -c Release -r "osx-$arch" -p:PackageFormat=app -p:Version="$version" -o "$bundles/publish" --nologo -v:q
    bundle="$(find "$repo/artifacts/bundle" "$repo/src/TwoTicks.Desktop/bin" -maxdepth 6 -name 'TwoTicks.app' -type d 2> /dev/null | head -n 1)"
    if [ -z "$bundle" ]; then
        echo 'The app bundle was not made.' >&2
        exit 1
    fi

    plist="$bundle/Contents/Info.plist"
    # Calls ask for the microphone and video calls for the camera; macOS shows these texts when it asks.
    /usr/libexec/PlistBuddy -c 'Add :NSMicrophoneUsageDescription string "TwoTicks uses the microphone for calls."' "$plist" 2> /dev/null || true
    /usr/libexec/PlistBuddy -c 'Add :NSCameraUsageDescription string "TwoTicks uses the camera for video calls."' "$plist" 2> /dev/null || true
    /usr/libexec/PlistBuddy -c 'Add :LSApplicationCategoryType string "public.app-category.social-networking"' "$plist" 2> /dev/null || true
    find "$bundle" -name '*.pdb' -delete

    # Signed for this machine only: without a signature macOS on Apple silicon
    # does not start a program at all. People still have to allow the app once.
    codesign --force --deep --sign - "$bundle"

    echo 'Making the disk image...'
    staging="$(mktemp -d)"
    cp -R "$bundle" "$staging/TwoTicks.app"
    ln -s /Applications "$staging/Applications"
    image="$release/TwoTicks-$version-macos-$arch.dmg"
    rm -f "$image"
    hdiutil create -volname TwoTicks -srcfolder "$staging" -ov -format UDZO "$image" > /dev/null
    rm -rf "$staging"
else
    echo 'Packing the archive...'
    bundle="$release/TwoTicks"
    rm -rf "$bundle"
    cp -R "$app" "$bundle"
    chmod +x "$bundle/TwoTicks" "$bundle/TwoTicks.Bridge"
    tar -C "$release" -czf "$release/TwoTicks-$version-linux-$arch.tar.gz" TwoTicks
    rm -rf "$bundle"

    if command -v dpkg-deb > /dev/null; then
        echo 'Building the .deb...'
        case "$arch" in
            x64) deb_arch=amd64 ;;
            *) deb_arch=arm64 ;;
        esac
        root="$(mktemp -d)"
        mkdir -p "$root/DEBIAN" "$root/opt" "$root/usr/bin" "$root/usr/share/applications"
        cp -R "$app" "$root/opt/twoticks"
        chmod 755 "$root/opt/twoticks/TwoTicks" "$root/opt/twoticks/TwoTicks.Bridge"
        ln -s /opt/twoticks/TwoTicks "$root/usr/bin/twoticks"
        sed "s|@EXEC@|/opt/twoticks/TwoTicks|; s|@ICON@|io.github.rathlinus.TwoTicks|" \
            "$packaging/linux/twoticks.desktop" > "$root/usr/share/applications/io.github.rathlinus.TwoTicks.desktop"
        python3 "$packaging/linux/icons.py" "$repo/src/TwoTicks.App/Assets/AppIcon.png" "$root/usr/share/icons/hicolor" io.github.rathlinus.TwoTicks
        size="$(du -sk "$root" | cut -f1)"
        sed "s|@VERSION@|$version|; s|@ARCH@|$deb_arch|; s|@SIZE@|$size|" "$packaging/linux/control" > "$root/DEBIAN/control"
        cp "$packaging/linux/postinst" "$root/DEBIAN/postinst"
        cp "$packaging/linux/postinst" "$root/DEBIAN/postrm"
        chmod 755 "$root/DEBIAN/postinst" "$root/DEBIAN/postrm"
        dpkg-deb --root-owner-group --build "$root" "$release/twoticks_${version}_${deb_arch}.deb" > /dev/null
        rm -rf "$root"
    else
        echo 'dpkg-deb was not found: no .deb is built.'
    fi
fi

echo 'Release files:'
ls -1 "$release" | sed 's/^/  /'
