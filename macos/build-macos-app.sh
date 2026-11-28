#!/usr/bin/env bash
set -euo pipefail

# 在 macOS 上构建独立轻量级 Agent Proxy Hub.app（Apple Silicon 或 Intel）
ARCH="${1:-osx-arm64}"
ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
APP_DIR="$ROOT_DIR/dist/AgentProxyHub.app"
MACOS_DIR="$APP_DIR/Contents/MacOS"

echo "[1/4] 构建 React Cult UI 前端..."
pnpm --dir "$ROOT_DIR/web" install
pnpm --dir "$ROOT_DIR/web" build

echo "[2/4] 发布 .NET 10 核心代理服务与 aph CLI ($ARCH)..."
dotnet publish "$ROOT_DIR/src/AgentProxyHub.Host/AgentProxyHub.Host.csproj" \
  -c Release -r "$ARCH" --self-contained false \
  -o "$MACOS_DIR"
dotnet publish "$ROOT_DIR/src/AgentProxyHub.Cli/AgentProxyHub.Cli.csproj" \
  -c Release -r "$ARCH" --self-contained false \
  -o "$MACOS_DIR"

echo "[3/4] 编译原生 Swift 菜单栏 + WKWebView 宿主..."
mkdir -p "$MACOS_DIR"
swiftc "$ROOT_DIR/macos/MacTrayHost.swift" \
  -framework Cocoa -framework WebKit \
  -O -o "$MACOS_DIR/AgentProxyHub"

cat > "$APP_DIR/Contents/Info.plist" << 'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key>
  <string>AgentProxyHub</string>
  <key>CFBundleIdentifier</key>
  <string>dev.heyocode.agentproxyhub</string>
  <key>CFBundleName</key>
  <string>Agent Proxy Hub</string>
  <key>CFBundleShortVersionString</key>
  <string>0.8.0</string>
  <key>CFBundleVersion</key>
  <string>0.8.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
EOF

echo "[4/4] 打包完成: $APP_DIR"
