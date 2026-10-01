#!/bin/bash
# kakao-bomb.app 빌드 (Apple Silicon + Intel 유니버설) → dist/kakao-bomb-<버전>.zip
# 사용: ./build.sh 1.0.0
set -euo pipefail
cd "$(dirname "$0")"

VERSION="${1:-1.0.0}"
APP=dist/kakao-bomb.app
BUILD=build

rm -rf "$BUILD" dist
mkdir -p "$BUILD" "$APP/Contents/MacOS" "$APP/Contents/Resources"

for arch in arm64 x86_64; do
    swiftc -O -swift-version 5 -target "$arch-apple-macos13.0" main.swift -o "$BUILD/kakao-bomb-$arch"
done
lipo -create "$BUILD"/kakao-bomb-arm64 "$BUILD"/kakao-bomb-x86_64 -output "$APP/Contents/MacOS/kakao-bomb"

swift scripts/make_icon.swift "$BUILD/AppIcon.iconset"
iconutil -c icns "$BUILD/AppIcon.iconset" -o "$APP/Contents/Resources/AppIcon.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key><string>com.tanso0126.kakao-bomb</string>
    <key>CFBundleName</key><string>kakao-bomb</string>
    <key>CFBundleDisplayName</key><string>kakao-bomb</string>
    <key>CFBundleExecutable</key><string>kakao-bomb</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>LSMinimumSystemVersion</key><string>13.0</string>
    <key>LSUIElement</key><true/>
    <key>NSHumanReadableCopyright</key><string>kakao-bomb</string>
</dict>
</plist>
PLIST

# Developer ID가 없어서 ad-hoc 서명 (받는 사람은 처음 한 번 "그래도 열기" 필요)
codesign --force --deep --sign - "$APP"
codesign --verify --strict "$APP"

ditto -c -k --keepParent "$APP" "dist/kakao-bomb-$VERSION.zip"
echo "완료: dist/kakao-bomb-$VERSION.zip"
