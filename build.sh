#!/bin/sh
# HugWatch.app 빌드 (Xcode 프로젝트 없이, Command Line Tools 만 있으면 됨)
#   ./build.sh          → 이 폴더에 HugWatch.app
#   ./build.sh install  → ~/Applications 에 설치하고 실행
set -e
cd "$(dirname "$0")"
APP=HugWatch.app
rm -rf $APP
mkdir -p $APP/Contents/MacOS $APP/Contents/Resources
cat > $APP/Contents/Info.plist <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleIdentifier</key><string>kr.bongchil.hugwatch</string>
  <key>CFBundleName</key><string>HugWatch</string>
  <key>CFBundleExecutable</key><string>HugWatch</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.1.0</string>
  <key>LSMinimumSystemVersion</key><string>15.0</string>
  <key>LSUIElement</key><true/>
  <key>CFBundleIconFile</key><string>AppIcon</string>
</dict></plist>
PLIST

# Apple Silicon + Intel 둘 다 (macOS 15 이상)
for arch in arm64 x86_64; do
  swiftc -parse-as-library -O -target $arch-apple-macos15.0 HugWatch.swift -o /tmp/hugwatch-$arch
done
lipo -create /tmp/hugwatch-arm64 /tmp/hugwatch-x86_64 -output $APP/Contents/MacOS/HugWatch
rm -f /tmp/hugwatch-arm64 /tmp/hugwatch-x86_64

# 앱 아이콘: 바이너리가 도트 인베이더를 직접 icns 로 그림
$APP/Contents/MacOS/HugWatch --icon $APP/Contents/Resources/AppIcon.icns
codesign --force --sign - $APP
$APP/Contents/MacOS/HugWatch --selftest
echo "built $APP"

if [ "$1" = "install" ]; then
  osascript -e 'quit app id "kr.bongchil.hugwatch"' 2>/dev/null || true
  mkdir -p ~/Applications
  rm -rf ~/Applications/$APP
  cp -R $APP ~/Applications/
  open ~/Applications/$APP
  echo "installed ~/Applications/$APP"
fi
