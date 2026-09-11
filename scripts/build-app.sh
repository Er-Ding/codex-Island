#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
cd "$project_dir"
export CLANG_MODULE_CACHE_PATH="$project_dir/.build/clang-module-cache"
export SWIFTPM_MODULECACHE_OVERRIDE="$project_dir/.build/swift-module-cache"
swift build --disable-sandbox -c release --scratch-path "$project_dir/.build"
binary_dir="$(swift build --disable-sandbox -c release --scratch-path "$project_dir/.build" --show-bin-path)"
app_dir="$project_dir/dist/Codex Island.app"
mkdir -p "$app_dir/Contents/MacOS" "$app_dir/Contents/Resources"
cp "$binary_dir/CodexIsland" "$app_dir/Contents/MacOS/CodexIsland"
cat > "$app_dir/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleIdentifier</key><string>local.stephen.codexisland</string>
  <key>CFBundleName</key><string>Codex Island</string>
  <key>CFBundleDisplayName</key><string>Codex Island</string>
  <key>CFBundleExecutable</key><string>CodexIsland</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.2.0</string>
  <key>CFBundleVersion</key><string>2</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSAppleEventsUsageDescription</key><string>点击终端任务时，选择该会话已经打开的终端页签。</string>
</dict></plist>
PLIST
codesign --force --sign - "$app_dir"
codesign --verify --strict "$app_dir"
printf '\n已生成：%s\n' "$app_dir"
