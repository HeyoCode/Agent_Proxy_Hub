# Agent Proxy Hub

专为 **Claude**（Desktop / Claude Code）、**ChatGPT**（Desktop / Codex CLI）、**Antigravity**（IDE / Agent）等海外 AI 软件打造的跨平台（Windows / macOS）轻量级独立网络代理与双源用量统计网关。

## 核心能力

- **彻底不受 Clash / Surge / VPN 干扰（物理网卡硬件级直绑）**
  - **永不篡改系统全局代理**：保留浏览器的 Clash/系统代理设置，完全互不干扰。
  - **Socket 级物理出口直绑**：自动识别并排斥 `Wintun / Mihomo / Clash / utun / Tailscale / VMware / TAP` 等虚拟隧道接口；出站连接直绑物理硬件网卡（Windows 使用 `IP_UNICAST_IF`，macOS 使用 `IP_BOUND_IF`）。
  - **防 Clash Fake-IP (`198.18.0.0/15`)**：支持 `SOCKS5h` 远端解析、TLS `ClientHello SNI` 首包域名嗅探还原，以及上游节点域名物理直连 DNS。
  - **防串 IP 熔断锁（Fail-Close Kill Switch）**：专线异常断开时直接阻断对应 AI 连接，严禁静默回退到本地宽带或机场节点。
- **全协议与多席位独立分流**
  - 上游支持 `SOCKS5 / SOCKS5h`（含账密认证）、`SOCKS4 / 4a`、`HTTP CONNECT`、`HTTPS TLS 代理` 与物理直连通道。
  - 本地多端口混合入站（`:17891 ~ :17894` 分应用独立端口，首包自动兼容 HTTP / SOCKS5 / SOCKS4），支持手动添加与混合格式批量导入及去重。
- **免解密双源 AI 用量统计（CC Switch 级）**
  - 保持代理隧道端到端 TLS 1.3 原生加密不拆包，通过增量扫描本机 `~/.claude`、`~/.codex`、`~/.gemini/antigravity` 结构化日志元数据汇总请求次数、有效输入、输出、缓存命中率与输出流速；
  - 结合代理层上下行字节流量与自定义模型百万 Token 定价表，实现应用、模型与专线流量二维联动统计。

## 工程结构

```text
Agent_Proxy_Hub/
├── AgentProxyHub.sln                 # .NET 10 解决方案
├── src/
│   ├── AgentProxyHub.Core/           # 跨平台核心代理引擎、物理网卡直绑、批量解析与双源统计库
│   ├── AgentProxyHub.Host/           # 跨平台本地守护进程与 REST API 服务 (127.0.0.1:17880)
│   └── AgentProxyHub.Desktop.Win/    # Windows WebView2 + 系统托盘原生宿主
├── macos/                            # macOS 原生菜单栏 (NSStatusItem) + WKWebView 宿主与打包脚本
├── web/                              # React 19 + TypeScript + Vite 前端 (Cult UI 风格)
├── tests/AgentProxyHub.Tests/        # xUnit 单元测试与端到端代理测试
└── preview/index.html                # 纯静态零依赖交互设计预览稿
```

## 快速构建与运行

### 1. 构建前端界面
```bash
pnpm --dir web install
pnpm --dir web build
```

### 2. 编译与测试后端（.NET 10 SDK）
```bash
dotnet build AgentProxyHub.sln -c Release
dotnet test tests/AgentProxyHub.Tests/AgentProxyHub.Tests.csproj -c Release
```

### 3. 启动桌面端或服务端
- **Windows 桌面托盘版**：
  ```powershell
  dotnet run --project src/AgentProxyHub.Desktop.Win/AgentProxyHub.Desktop.Win.csproj -c Release
  ```
- **跨平台本地服务（Windows / macOS 通用）**：
  ```bash
  dotnet run --project src/AgentProxyHub.Host/AgentProxyHub.Host.csproj -c Release
  # 浏览器访问 http://127.0.0.1:17880/
  ```
- **macOS 原生菜单栏版打包**：
  ```bash
  chmod +x macos/build-macos-app.sh
  ./macos/build-macos-app.sh osx-arm64
  ```
