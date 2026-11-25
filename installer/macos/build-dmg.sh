#!/usr/bin/env bash
set -euo pipefail

# 构建 macOS 原生 .app 与拖拽式 .dmg 安装镜像
# 用法: ./installer/macos/build-dmg.sh [osx-arm64|osx-x64] [版本号]
ARCH="${1:-osx-arm64}"
VERSION="${2:-0.2.0}"
ROOT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
DIST_DIR="$ROOT_DIR/dist"
STAGE_DIR="$DIST_DIR/dmg-stage-$ARCH"
APP_DIR="$DIST_DIR/AgentProxyHub.app"
MACOS_DIR="$APP_DIR/Contents/MacOS"
RES_DIR="$APP_DIR/Contents/Resources"
DMG_NAME="AgentProxyHub-v${VERSION}-macos-${ARCH#osx-}.dmg"
DMG_PATH="$DIST_DIR/$DMG_NAME"

rm -rf "$APP_DIR" "$STAGE_DIR" "$DMG_PATH"
mkdir -p "$MACOS_DIR" "$RES_DIR" "$STAGE_DIR"

echo "[1/5] 构建 Cult UI React 前端..."
pnpm --dir "$ROOT_DIR/web" install --frozen-lockfile --network-concurrency=2
pnpm --dir "$ROOT_DIR/web" build

echo "[2/5] 发布 .NET 10 跨平台后端服务 ($ARCH)..."
dotnet publish "$ROOT_DIR/src/AgentProxyHub.Host/AgentProxyHub.Host.csproj" \
  -c Release -r "$ARCH" --self-contained true \
  -o "$MACOS_DIR"

echo "[3/5] 生成 macOS AppIcon.icns 图标..."
ICON_SRC="$ROOT_DIR/assets/icon-512.png"
if [[ -f "$ICON_SRC" ]] && command -v iconutil >/dev/null 2>&1; then
  ICONSET="$STAGE_DIR/AppIcon.iconset"
  mkdir -p "$ICONSET"
  for sz in 16 32 128 256 512; do
    sips -z "$sz" "$sz" "$ICON_SRC" --out "$ICONSET/icon_${sz}x${sz}.png" >/dev/null
    sz2=$((sz * 2))
    if [[ $sz2 -le 512 ]]; then
      sips -z "$sz2" "$sz2" "$ICON_SRC" --out "$ICONSET/icon_${sz}x${sz}@2x.png" >/dev/null
    fi
  done
  iconutil -c icns "$ICONSET" -o "$RES_DIR/AppIcon.icns"
  rm -rf "$ICONSET"
fi

echo "[4/5] 编译 Swift 原生菜单栏 + WKWebView 桌面宿主..."
SWIFT_TARGET="arm64-apple-macos13.0"
if [[ "$ARCH" == "osx-x64" ]]; then
  SWIFT_TARGET="x86_64-apple-macos13.0"
fi
swiftc "$ROOT_DIR/macos/MacTrayHost.swift" \
  -target "$SWIFT_TARGET" \
  -framework Cocoa -framework WebKit \
  -O -o "$MACOS_DIR/AgentProxyHub"

cat > "$APP_DIR/Contents/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key>
  <string>AgentProxyHub</string>
  <key>CFBundleIconFile</key>
  <string>AppIcon</string>
  <key>CFBundleIdentifier</key>
  <string>dev.heyocode.agentproxyhub</string>
  <key>CFBundleName</key>
  <string>Agent Proxy Hub</string>
  <key>CFBundleShortVersionString</key>
  <string>${VERSION}</string>
  <key>CFBundleVersion</key>
  <string>${VERSION}</string>
  <key>LSMinimumSystemVersion</key>
  <string>13.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
EOF

echo "[5/5] 封装拖拽式 DMG 安装镜像..."
cp -R "$APP_DIR" "$STAGE_DIR/Agent Proxy Hub.app"
ln -s /Applications "$STAGE_DIR/Applications"
hdiutil create -volname "Agent Proxy Hub v${VERSION}" \
  -srcfolder "$STAGE_DIR" \
  -ov -format UDZO "$DMG_PATH"

rm -rf "$STAGE_DIR"
shasum -a 256 "$DMG_PATH" > "$DMG_PATH.sha256"
echo "DMG 构建完成: $DMG_PATH"
