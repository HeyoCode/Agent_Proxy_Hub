<div align="center">

<img src="assets/icon.svg" alt="Agent Proxy Hub Logo" width="84" height="84" />

# Agent Proxy Hub

**专为海外 AI 应用打造的跨平台独立出口代理网关、无损接管器与免解密用量统计中心**

*让 Claude、ChatGPT / Codex、Antigravity、Cursor 各自独占纯净静态出口 IP，与 Clash / Mihomo / Surge 平行共处、互不串线*

<p>
  <a href="https://github.com/HeyoCode/Agent_Proxy_Hub/releases">
    <img src="https://img.shields.io/github/v/release/HeyoCode/Agent_Proxy_Hub?include_prereleases&style=for-the-badge&color=4A56E2&label=Release" alt="Latest Release" />
  </a>
  <a href="https://github.com/HeyoCode/Agent_Proxy_Hub/stargazers">
    <img src="https://img.shields.io/github/stars/HeyoCode/Agent_Proxy_Hub?style=for-the-badge&color=F59E0B&label=Stars" alt="GitHub Stars" />
  </a>
  <a href="https://github.com/HeyoCode/Agent_Proxy_Hub/releases">
    <img src="https://img.shields.io/github/downloads/HeyoCode/Agent_Proxy_Hub/total?style=for-the-badge&color=10B981&label=Downloads" alt="Downloads" />
  </a>
</p>

<p>
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet&logoColor=white" alt=".NET 10" />
  <img src="https://img.shields.io/badge/React-19-149ECA?style=flat-square&logo=react&logoColor=white" alt="React 19" />
  <img src="https://img.shields.io/badge/TypeScript-5.7-3178C6?style=flat-square&logo=typescript&logoColor=white" alt="TypeScript" />
  <img src="https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-0078D6?style=flat-square&logo=windows&logoColor=white" alt="Windows" />
  <img src="https://img.shields.io/badge/macOS-Apple%20Silicon%20%2F%20Intel-18181B?style=flat-square&logo=apple&logoColor=white" alt="macOS" />
  <img src="https://img.shields.io/badge/Zero_MITM-TLS%201.3%20端到端加密-059669?style=flat-square" alt="Zero MITM" />
</p>

<p>
  <a href="#-核心特性">核心特性</a> •
  <a href="#-界面预览">界面预览</a> •
  <a href="#-物理硬件直出架构">架构原理</a> •
  <a href="#-默认席位与一键接管矩阵">应用接管</a> •
  <a href="#-快速安装与使用">快速上手</a> •
  <a href="#-本地-rest-api-与开发构建">API 与开发</a> •
  <a href="#-常见问题-faq">FAQ</a>
</p>

</div>

---

> [!IMPORTANT]
> **零改动系统全局代理 · 绝不与 Clash / Mihomo / Surge 冲突**
> Agent Proxy Hub 通过操作系统底层 Socket 接口直绑物理硬件网卡（Windows `IP_UNICAST_IF` / `IPV6_UNICAST_IF`，macOS `IP_BOUND_IF` / `IPV6_BOUND_IF`），自动绕过 `Wintun / Mihomo / Clash / utun / Tailscale / VMware` 虚拟隧道网卡。你的浏览器与日常软件继续走原有代理，AI 工具则拥有彼此隔离的独立静态住宅/专线出口。

---

## ✨ 核心特性

<table>
  <tr>
    <td width="50%" valign="top">
      <h3>🛡️ 物理硬件网卡直绑 · 彻底绕过 Clash</h3>
      <p>自动扫描并排除所有虚拟/隧道网卡，所有代理上游连接直接绑定物理以太网/Wi-Fi 出口；支持 <strong>TLS ClientHello SNI 域名还原</strong>，自动剥离 Clash Fake-IP (<code>198.18.0.0/15</code>)，上游域名走物理直连干净 DNS 解析。</p>
    </td>
    <td width="50%" valign="top">
      <h3>🔌 AI 应用一键无损接管 & 受控启动</h3>
      <p>内置 <code>AppProxyTakeoverService</code>，自动备份原配置并精准写入/还原代理入口，保留全部已有凭据与自定义项；Antigravity 启动器内置 <strong>150ms 端口探活守卫</strong>，网关开启时自动导流，关闭时自动直连绝不断网。</p>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <h3>📊 免解密双源 AI 用量统计（5s 自动同步）</h3>
      <p>零 MITM 拆包风险，后台每 <strong>5 秒</strong>增量巡检 <code>~/.claude</code>、<code>~/.codex</code>、<code>~/.gemini/antigravity</code> 结构化日志，聚合请求数、有效输入、输出、缓存命中率、Token 流速与自定义百万 Token 成本。</p>
    </td>
    <td width="50%" valign="top">
      <h3>⚡ 实时吞吐速率 & 累计流量自由切换</h3>
      <p>引擎内置 1 秒高精度滑动速率采样器，右侧午夜检视栏支持 <code>实时 (B/s · KB/s · MB/s)</code> 与 <code>累计 (B · KB · MB)</code> 一键切换，配合<strong>全链路实流验真</strong>实时观测端口活跃流与熔断拦截数。</p>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <h3>🧊 原生无边框三栏美学 & 真托盘快捷窗</h3>
      <p>Windows 11 DWM 原生圆角无外边框窗口 + 共享 WebView2 预热的<strong>系统托盘圆角悬浮面板</strong>（macOS 使用原生 <code>NSPopover</code>），彻底告别传统灰底右键菜单，单击托盘即可启停席位、一键接管与复制变量。</p>
    </td>
    <td width="50%" valign="top">
      <h3>🔒 防串 IP 熔断锁 & IPv6 双栈独立开关</h3>
      <p>开启 <strong>Fail-Close 熔断锁</strong>后，若专线节点掉线则直接阻断连接，绝不回退本地宽带或机场 IP；内置全局 <strong>IPv6 开关</strong>（默认关闭以防单栈节点卡顿与 IPv6 真实地址泄漏）。</p>
    </td>
  </tr>
</table>

---

## 🖼️ 界面预览

<table>
  <tr>
    <td width="50%" align="center">
      <img src="docs/screenshots/dashboard-light.png" alt="Nordic Mist 主控制台（浅色）" />
      <br />
      <sub><strong>主控制台（浅色 Nordic Mist）</strong> · 三栏拓扑总控、Token 趋势与右栏实时/累计吞吐切换</sub>
    </td>
    <td width="50%" align="center">
      <img src="docs/screenshots/usage-stats.png" alt="双源用量统计大盘" />
      <br />
      <sub><strong>双源用量统计</strong> · 5s 自动增量同步、缓存命中率分时柱图、模型与专线二维归属</sub>
    </td>
  </tr>
  <tr>
    <td width="50%" align="center">
      <img src="docs/screenshots/apps-takeover.png" alt="AI 应用一键无损接管工作台" />
      <br />
      <sub><strong>应用接管与 CLI 工作台</strong> · 实时进程识别、一键接管/无损还原、专线启动与终端变量</sub>
    </td>
    <td width="50%" align="center">
      <img src="docs/screenshots/tray-quick-panel.png" alt="系统托盘原生快捷面板" />
      <br />
      <sub><strong>系统托盘圆角快捷面板</strong> · 物理网卡与绕过状态、翠绿开关、一键接管与快速唤起</sub>
    </td>
  </tr>
</table>

<details>
<summary>🌙 <strong>点击展开：深色主题（Dark Mode）主控制台预览</strong></summary>
<br />
<p align="center">
  <img src="docs/screenshots/dashboard-dark.png" alt="深色主题主控制台" width="88%" />
</p>
</details>

---

## 🧭 物理硬件直出架构

```mermaid
flowchart LR
    subgraph APPS["独立 AI 应用层（互不串线）"]
        A1["Claude Code / Desktop<br/>:17891"]
        A2["ChatGPT / Codex CLI<br/>:17892"]
        A3["Antigravity IDE / Agent<br/>:17893"]
        A4["Cursor Editor<br/>:17894"]
    end

    subgraph CORE["Agent Proxy Hub Core (.NET 10)"]
        IN["多端口混合入站引擎<br/>SOCKS5 / SOCKS4 / HTTP(S)"]
        SNI["TLS SNI 还原<br/>剥离 198.18.x.x Fake-IP"]
        KS["Fail-Close 熔断锁<br/>+ IPv6 独立开关 + 1s 速率采样"]
        NIC["物理网卡 Socket 直绑<br/>IP_UNICAST_IF / IP_BOUND_IF"]
    end

    subgraph NET["物理硬件出口（自动绕过虚拟隧道）"]
        N1["🇺🇸 US 静态住宅专线"]
        N2["🇯🇵 JP 独立原生专线"]
        N3["🇸🇬 SG 独立代理线路"]
    end

    TUN["🚫 Clash / Mihomo / Wintun / utun<br/>已自动识别并彻底绕过"] -. 零字节经过 .-x NIC

    A1 & A2 & A3 & A4 --> IN --> SNI --> KS --> NIC
    NIC --> N1 & N2 & N3

    style TUN fill:#fff1f0,stroke:#f27973,stroke-dasharray: 5 5,color:#b42318
    style NIC fill:#eef2ff,stroke:#4a56e2,color:#1e293b
```

---

## 🎯 默认席位与一键接管矩阵

| AI 席位 | 本地混合端口 | 支持协议 | 一键无损接管机制 | 备份与还原策略 |
| :--- | :---: | :--- | :--- | :--- |
| **Claude** *(Code / Desktop)* | `127.0.0.1:17891` | HTTP / SOCKS5 / SOCKS4 | 原地合并 `~/.claude/settings.json` 的 `env.HTTP(S)_PROXY` 与 `NO_PROXY` | 保留全部 Token/模型配置，点击「还原默认」精准复原 |
| **ChatGPT / Codex** | `127.0.0.1:17892` | HTTP / SOCKS5 / SOCKS4 | 生成专属隔离启动脚本 + 一键拉起已注入代理环境的独立终端 | 一键清理托管脚本，零改动系统环境变量 |
| **Antigravity** *(IDE / Agent)* | `127.0.0.1:17893` | HTTP / SOCKS5 / SOCKS4 | 兼容自愈汉化启动器与独立启动脚本，内置 **150ms 端口探活守卫** | 网关关闭时自动跳过代理直连；一键剥离托管块还原 |
| **Cursor** *(AI 代码编辑器)* | `127.0.0.1:17894` | HTTP / SOCKS5 / SOCKS4 | 安全解析带注释的 `User/settings.json`，热写入 `http.proxy` | 保留字体/插件等全部用户配置，一键恢复原值 |

> [!TIP]
> **接管后何时生效？**
> - **Cursor**：写入 `User/settings.json` 后通常即时热生效；
> - **Claude Code / Codex**：新建对话或重开终端立即生效；
> - **Antigravity**：若接管前应用已在运行，请从系统托盘彻底退出 Antigravity 后重新打开，所有 `language_server` 与 Agent 流量即会 100% 切入 `:17893`。

---

## 🚀 快速安装与使用

### 方式一：直接下载安装程序（推荐）

前往 [**GitHub Releases**](https://github.com/HeyoCode/Agent_Proxy_Hub/releases) 下载对应平台最新安装包：
- **Windows x64**：`AgentProxyHub-Setup-v0.3.4-win-x64.exe`（默认用户级免管理员安装，支持 `/PORTABLE=1` 便携模式与 `--minimized` 开机静默托盘自启，覆盖升级自动平滑接管托盘旧实例）
- **macOS**：`AgentProxyHub-v0.3.4-osx-arm64.dmg` / `osx-x64.dmg`

### 方式二：3 步完成首次配置

1. **导入独立专线**：点击左侧「独立线路池」→「批量导入」，粘贴你的静态 SOCKS5 / HTTP(S) 代理线路并点击「全部验真」。
2. **绑定席位并接管**：在控制台为每个 AI 应用选择独立出口线路，点击右侧 **「一键接管」**（或点击 **「全链路验真」** 实流测试通道并刷新吞吐）。
3. **日常托盘操控**：关闭主窗口后自动常驻系统托盘；左键或右键单击托盘图标即可呼出圆角快捷面板，双击托盘图标直达主控制台。

<details>
<summary>📋 <strong>批量导入支持的代理格式示例（自动去重与 IPv6 支持）</strong></summary>

```text
# 标准 URI 格式（SOCKS5 / SOCKS4 / HTTP / HTTPS）
socks5://user1:pass1@38.154.227.14:1080#US-静态住宅-01
http://user2:pass2@104.221.88.19:8080#US-独享HTTP-02
https://user3:pass3@proxy.example.com:443#JP-TLS专线

# 四段冒号简写格式（默认按所选协议解析）
133.167.44.91:10808:agy:jp999#JP-NTT-03

# 方括号 IPv6 地址格式
socks5://u6:p6@[2001:db8::1]:1080#IPv6-双栈节点
```

</details>

---

## 🛠️ 本地 REST API 与开发构建

### 工程目录结构

```text
Agent_Proxy_Hub/
├── AgentProxyHub.sln                   # .NET 10 解决方案
├── src/
│   ├── AgentProxyHub.Core/             # 核心代理引擎、物理网卡直绑、无损接管、双源统计库
│   ├── AgentProxyHub.Host/             # 跨平台本地守护与 REST API 服务 (127.0.0.1:17880)
│   └── AgentProxyHub.Desktop.Win/      # Windows 无边框 WebView2 主窗与原生圆角托盘悬浮窗
├── macos/                              # macOS 原生菜单栏 NSPopover + WKWebView 宿主与脚本
├── web/                                # React 19 + TypeScript + Vite 前端（本地内嵌字体零外链）
├── installer/                          # Inno Setup 6 (Windows) 与 DMG (macOS) 打包脚本
├── tests/AgentProxyHub.Tests/          # xUnit 单元测试与端到端 SOCKS5 / IPv6 / 接管 / 速率测试
└── docs/screenshots/                   # README 展示截图资源
```

### 从源码编译与运行

```powershell
# 1. 构建前端资源（输出至 src/AgentProxyHub.Host/wwwroot）
pnpm --dir web install
pnpm --dir web build

# 2. 运行全套 xUnit 单元测试（10 项全绿）
dotnet test AgentProxyHub.sln -c Release

# 3. 启动 Windows 无边框托盘客户端
dotnet run --project src/AgentProxyHub.Desktop.Win/AgentProxyHub.Desktop.Win.csproj -c Release

# 或启动跨平台纯 Web 本地网关（Windows / macOS 通用，访问 http://127.0.0.1:17880/）
dotnet run --project src/AgentProxyHub.Host/AgentProxyHub.Host.csproj -c Release
```

<details>
<summary>📦 <strong>打包 Windows 安装程序 / macOS DMG 镜像</strong></summary>

```powershell
# Windows 轻量安装程序（需安装 Inno Setup 6）
powershell -ExecutionPolicy Bypass -File .\installer\windows\build-installer.ps1 -Version 0.3.4

# Windows 离线独立版（内置完整 .NET 10 运行时）
powershell -ExecutionPolicy Bypass -File .\installer\windows\build-installer.ps1 -Version 0.3.4 -SelfContained
```

```bash
# macOS Apple Silicon / Intel DMG 镜像
chmod +x installer/macos/build-dmg.sh
./installer/macos/build-dmg.sh osx-arm64 0.3.4
```

</details>

<details>
<summary>🔌 <strong>本地 REST API 端点速查（默认监听 <code>127.0.0.1:17880</code>）</strong></summary>

| 方法 | 路径 | 说明 |
| :---: | :--- | :--- |
| `GET` | `/api/overview` | 获取物理网卡、绕过隧道、席位配置、节点池、实时吞吐/速率与接管状态 |
| `POST` | `/api/nodes/batch-import` | 批量解析并去重导入多协议代理节点 |
| `POST` | `/api/nodes/{id}/verify` | 物理网卡直连并发验真单个节点（单次最快 RTT + 出口 IP） |
| `PUT` | `/api/seats/{id}` | 更新席位启停、绑定线路、本地端口与防串熔断锁 |
| `POST` | `/api/seats/{id}/takeover` | 对目标 AI 应用执行一键无损代理接管或还原默认配置 |
| `POST` | `/api/seats/{id}/launch` | 注入席位独立代理环境变量受控启动目标应用或终端 |
| `POST` | `/api/seats/{id}/verify-tunnel` | 经由 `127.0.0.1:{localPort}` 发起全链路实流验真并刷新吞吐 |
| `PUT` | `/api/isolation` | 切换锁定物理网卡、SNI 还原、全局熔断锁与 IPv6 双栈开关 |
| `GET` | `/api/stats/dashboard` | 查询双源 AI 用量统计（支持 `today / 24h / 7d / 30d / all` 与分页筛选） |
| `POST` | `/api/stats/sync` | 立即触发一次增量会话日志同步（后台默认每 5 秒自动执行） |

</details>

---

## ❓ 常见问题 FAQ

<details>
<summary><strong>1. 为什么「Token 消耗趋势」有数据，但右栏吞吐一开始是 0 B/s？</strong></summary>

- **Token 统计**直接增量扫描本地 `.claude / .codex / .gemini` 会话日志，不依赖代理流量；
- **吞吐计数**统计流经 `127.0.0.1:17891~17894` 端口的真实 TCP 流量。请先点击席位右侧的 **「一键接管」**（已运行中的桌面端应用需彻底退出后重开一次），或直接点击 **「全链路验真」** 立即观测实流穿透数据。

</details>

<details>
<summary><strong>2. 会不会把我的系统全局代理或 Clash / Mihomo 改坏？</strong></summary>

**绝对不会。** Agent Proxy Hub 永远不会写入 Windows WinINET 系统代理或 macOS 系统网络代理设置，也不会修改 Clash 配置文件。所有应用接管均在写入前自动备份原始键值至 `~/.agent-proxy-hub/takeover-backups/`，随时可一键无损还原。

</details>

<details>
<summary><strong>3. 为什么默认关闭 IPv6？什么时候需要开启？</strong></summary>

许多海外静态住宅代理或本地宽带环境仅提供纯 IPv4 出口，若放行 IPv6 既可能触发 Happy Eyeballs 连接卡顿，也可能导致部分直连目标暴露本机真实 IPv6 地址。当你明确使用支持 IPv6 的双栈代理节点时，在控制台总控区一键打开 **IPv6** 开关即可启用双栈解析与 `[::1]` 环回监听。

</details>

---

## 📈 Star History

<div align="center">
  <a href="https://star-history.com/#HeyoCode/Agent_Proxy_Hub&Date">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=HeyoCode/Agent_Proxy_Hub&type=Date&theme=dark" />
      <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=HeyoCode/Agent_Proxy_Hub&type=Date" />
      <img alt="Star History Chart" src="https://api.star-history.com/svg?repos=HeyoCode/Agent_Proxy_Hub&type=Date" width="760" />
    </picture>
  </a>
</div>

---

<div align="center">
  <sub>Crafted with precision by <a href="https://github.com/HeyoCode">@HeyoCode</a> · 如果这个项目帮你的 AI 账号告别了串线与风控，欢迎点一颗 ⭐ Star</sub>
</div>
