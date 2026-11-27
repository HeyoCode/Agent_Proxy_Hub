<div align="center">

<img src="assets/icon.svg" alt="Agent Proxy Hub Logo" width="84" height="84" />

# Agent Proxy Hub

**专为海外 AI 应用打造的跨平台独立出口代理网关、无损接管器与免解密用量统计中心**

*让 Claude (Code / Desktop)、ChatGPT / Codex、Antigravity、Cursor、Windsurf、Kiro、VS Code Copilot、Zed、Trae、CodeBuddy、OpenClaw 等 **21 款 AI 应用**各自独占纯净静态出口 IP（支持账号多开隔离），与 Clash / Mihomo / Surge 平行共处、互不串线*

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
  <a href="LICENSE">
    <img src="https://img.shields.io/badge/License-PolyForm%20Noncommercial%201.0.0-E11D48?style=flat-square" alt="PolyForm Noncommercial 1.0.0" />
  </a>
</p>

<p>
  <a href="#-核心特性">核心特性</a> •
  <a href="#-界面预览">界面预览</a> •
  <a href="#-物理硬件直出架构">架构原理</a> •
  <a href="#-支持应用与无损接管矩阵">21 款支持应用</a> •
  <a href="#-快速安装与使用">快速上手</a> •
  <a href="#-本地-rest-api-与开发构建">API 与开发</a> •
  <a href="#-常见问题-faq">FAQ</a> •
  <a href="CHANGELOG.md">更新日志</a>
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
      <p>自动扫描并排除所有虚拟/隧道网卡，代理上游连接直绑物理以太网/Wi-Fi 出口；内置 <strong>TLS ClientHello SNI 域名还原</strong> 与<strong>物理网卡 UDP 53 + RFC 8484 HTTPS DoH 双层干净解析</strong>，全程过滤并剥离 Clash Fake-IP (<code>198.18.0.0/15</code>)。</p>
    </td>
    <td width="50%" valign="top">
      <h3>🔌 21 款 AI 应用无损接管 &amp; 多开账号隔离</h3>
      <p>内置支持 <strong>21 款主流 AI 桌面与 CLI 工具</strong>（含 Cursor / Windsurf / Kiro / VS Code Copilot / Zed / Trae / CodeBuddy），支持置顶排序隐藏、<strong>多开独立 User-Data 配置实例</strong>（<code>:17920+</code> 分端口隔离）；Antigravity 启动器内置 <strong>150ms 端口探活守卫</strong>并严禁误杀运行中会话。</p>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <h3>🔀 显式备用容灾 · 双跳链式加速与 5m 无感巡检</h3>
      <p>支持为每个席位独立配置<strong>「前置中转」</strong>与<strong>「备用容灾 (<code>FallbackNodeId</code>)」</strong>（主节点 → 显式备用 → 同组前 3 节点 6s 快速切换）；单节点/全链路/全量验真均具硬超时保护（<code>6s / 7.5s / 22s</code>）并支持<strong>每 5 分钟后台低并发无感巡检</strong>。</p>
    </td>
    <td width="50%" valign="top">
      <h3>⚡ 桌面秒开零黑屏 · WebSocket 实时推送</h3>
      <p>Windows 桌面采用主题同步原生遮罩与增量缓存印记实现<strong>启动零黑屏秒开</strong>，主控台与悬浮监控卡通过受守卫保护的 <strong><code>/ws/events</code></strong> 实时推送状态，副窗口隐藏态零后台轮询，吞吐转发基于 <code>ArrayPool</code> 零 GC 缓冲池。</p>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <h3>📊 免解密双源统计 · 工作区排行 &amp; 单会话审计</h3>
      <p>零 MITM 拆包，后台每 <strong>5 秒</strong>增量巡检 <code>.claude / .codex / .gemini</code> 日志并去重合并，提供<strong>项目工作区成本排行</strong>、<strong>365 天活跃热力图</strong>、<strong>Cache 节省额</strong>以及<strong>单会话上下文膨胀轨迹与工具调用审计</strong>。</p>
    </td>
    <td width="50%" valign="top">
      <h3>🌐 L1/L2 全协议 · SHA-256 离线包与快照防漂移</h3>
      <p><strong>L1 原生层</strong>支持 <code>SOCKS5 / SOCKS4 / HTTP(S) / Trojan / Shadowsocks AEAD</code>；<strong>L2 扩展层</strong>按需懒加载 <code>sing-box</code>（<code>VLESS / Hysteria2 / TUIC / WireGuard</code>），支持离线包 <strong>SHA-256 校验</strong>与订阅刷新<strong>快照防漂移锁定</strong>。</p>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <h3>🛠️ 独立 <code>aph</code> CLI · Remote-SSH 与存储维护</h3>
      <p>内置独立命令行工具 <strong><code>aph</code></strong>（<code>UseProxy=false</code> 直连本机网关），支持脚本查询、切换线路、接管与维护；内置 <strong>Remote-SSH 反向代理隧道生成器</strong>及配置目录失效备份一键清理。</p>
    </td>
    <td width="50%" valign="top">
      <h3>🔒 国家门禁 · 隐私护盾 · 日志回填与 WebDAV 同步</h3>
      <p>支持各席位<strong>出口国家硬门禁</strong>与 <strong>Fail-Close 熔断锁</strong>；本地凭据与 <strong>WebDAV 跨机同步</strong>均采用 <strong>AES-256-GCM 加密</strong>，诊断中心支持磁盘日志启动自动回填与全局隐私打码。</p>
    </td>
  </tr>
</table>

---

## 🖼️ 界面预览

<table>
  <tr>
    <td width="50%" align="center">
      <img src="docs/screenshots/dashboard-light.png" alt="主控制台（浅色主题）" />
      <br />
      <sub><strong>主控制台（浅色主题）</strong> · AI 应用矢量品牌标、拓扑翻页、前置中转/备用容灾绑定与出口雷达图</sub>
    </td>
    <td width="50%" align="center">
      <img src="docs/screenshots/usage-stats.png" alt="双源用量统计大盘" />
      <br />
      <sub><strong>双源用量统计</strong> · 5s 自动增量同步、首字延迟(估)与流速计算、缓存命中率分时柱图与 365 天热力图</sub>
    </td>
  </tr>
  <tr>
    <td width="50%" align="center">
      <img src="docs/screenshots/apps-takeover.png" alt="AI 应用一键无损接管工作台" />
      <br />
      <sub><strong>21 款应用接管、多开隔离与 CLI 工作台</strong> · 搜索置顶、多开账号目录隔离、Remote-SSH 与无损接管</sub>
    </td>
    <td width="50%" align="center">
      <img src="docs/screenshots/tray-quick-panel.png" alt="系统托盘原生快捷面板" />
      <br />
      <sub><strong>系统托盘圆角快捷面板</strong> · 物理网卡与绕过状态、快捷开关、一键接管与隐藏态零轮询控制</sub>
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
    subgraph APPS["21 款独立 AI 应用层 + 多开实例 (:17920+)"]
        A1["Claude Code (:17891)<br/>Claude Desktop (:17895)"]
        A2["ChatGPT / Codex<br/>:17892"]
        A3["Antigravity IDE<br/>:17893"]
        A4["Cursor / Windsurf / Kiro / VS Code /<br/>Zed / Trae / CodeBuddy / CLI (:17894~17911)"]
    end

    subgraph CORE["Agent Proxy Hub Core (.NET 10) · REST + /ws/events + aph CLI"]
        IN["多端口混合入站引擎<br/>SOCKS5 / SOCKS4 / HTTP(S)"]
        SNI["TLS SNI 还原 + 物理直连 DoH<br/>剥离并过滤 198.18.x.x Fake-IP"]
        KS["主备容灾 + Fail-Close 熔断 + 5m 静默巡检<br/>L1 原生 (Trojan/SS/SOCKS/HTTP) · L2 按需 sing-box"]
        NIC["物理网卡 Socket 直绑<br/>IP_UNICAST_IF / IP_BOUND_IF / bind_interface"]
    end

    subgraph NET["物理硬件出口（自动绕过虚拟隧道）"]
        R1["⚡ 可选前置高速中转<br/>港 / 日 CN2 / Trojan / Reality / Hy2"]
        N1["🇺🇸 US 静态住宅落地"]
        N2["🇯🇵 JP 独立原生落地"]
        N3["🇸🇬 SG 独立代理落地"]
    end

    TUN["🚫 Clash / Mihomo / Wintun / utun<br/>已自动识别并彻底绕过"] -. 零字节经过 .-x NIC

    A1 & A2 & A3 & A4 --> IN --> SNI --> KS --> NIC
    NIC --> R1 --> N1
    NIC --> N2 & N3

    style TUN fill:#fff1f0,stroke:#f27973,stroke-dasharray: 5 5,color:#b42318
    style NIC fill:#eef2ff,stroke:#4a56e2,color:#1e293b
```

---

## 🎯 支持应用与无损接管矩阵

> 所有席位均原生监听**单端口多协议混合入站**（同时兼容 `HTTP / SOCKS5 / SOCKS4`），写入配置前自动备份原文件，随时一键无损还原。

### 核心桌面与深度配置接管（11 款）

| 应用 | 席位 ID (`seatId`) | 默认端口 | 无损接管与还原方式 |
| :--- | :--- | :---: | :--- |
| **Claude** (Code + Desktop) | `claude` | `:17891` | 原地合并 `~/.claude/settings.json` 中的代理环境变量，保留全部 Token 与模型配置，一键精准还原 |
| **ChatGPT** (Desktop + Codex) | `chatgpt` | `:17892` | 生成专属隔离启动脚本，一键拉起已注入独立出口的受控终端，零改动系统环境变量 |
| **Antigravity** | `agy` | `:17893` | 兼容自愈启动器并内置 **150ms 端口探活守卫**（网关关闭时自动直连不断网），一键剥离还原 |
| **Cursor** | `cursor` | `:17894` | 安全解析带注释的 `User/settings.json` 热写入 `http.proxy`，保留全部字体与插件配置 |
| **Claude Desktop** | `claude-desktop` | `:17895` | 自动探测本机 `Claude.exe` / `Claude.app` 注入 `--proxy-server` 桌面路由参数，支持一键清理 |
| **Windsurf** | `windsurf` | `:17906` | 无损备份并写入 `Windsurf/User/settings.json` (`http.proxy`)，支持 `--user-data-dir` 账号多开 |
| **Kiro** | `kiro` | `:17907` | 无损备份并写入 `Kiro/User/settings.json` (`http.proxy`)，支持重启重连与独立配置多开 |
| **VS Code Copilot** | `vscode-copilot` | `:17908` | 无损备份并写入 VS Code `Code/User/settings.json` (`http.proxy`)，不干扰系统代理 |
| **Zed** | `zed` | `:17909` | 无损读写 `zed/settings.json` 原生 `"proxy"` 字段与进程环境变量注入 |
| **Trae** | `trae` | `:17910` | 无损备份并写入 `Trae/User/settings.json` (`http.proxy`)，支持多开目录隔离 |
| **CodeBuddy** | `codebuddy` | `:17911` | 无损读写 `CodeBuddy/User/settings.json` 与 CLI 隔离启动脚本双适配 |

### 扩展 CLI / Agent 支持矩阵（10 款，一键隔离启动器）

| 应用 | `seatId` | 默认端口 | 启动命令 | 应用 | `seatId` | 默认端口 | 启动命令 |
| :--- | :--- | :---: | :---: | :--- | :--- | :---: | :---: |
| **Gemini CLI** | `gemini-cli` | `:17896` | `gemini` | **Pi** | `pi` | `:17901` | `pi` |
| **Grok Build** | `grok-build` | `:17897` | `grok` | **MiniMax Code** | `minimax-code` | `:17902` | `minimax` |
| **OpenCode** | `opencode` | `:17898` | `opencode` | **Kimi Code** | `kimi-code` | `:17903` | `kimi` |
| **OpenClaw** | `openclaw` | `:17899` | `openclaw` | **DeepSeek Harness** | `deepseek-harness` | `:17904` | `deepseek` |
| **Hermes** | `hermes` | `:17900` | `hermes` | **Oh My Pi** | `oh-my-pi` | `:17905` | `omp` |

> [!TIP]
> **接管后何时生效？**
> - **Cursor / Windsurf / Kiro / VS Code**：写入 `User/settings.json` 后通常即时热生效；
> - **Claude Code / Codex / CLI 工具**：新建对话或重开终端立即生效；
> - **Antigravity**：若接管前应用已在运行，请从系统托盘彻底退出 Antigravity 后重新打开，所有 `language_server` 与 Agent 流量即会 100% 切入 `:17893`。

---

## 🚀 快速安装与使用

### 方式一：直接下载安装程序（推荐）

前往 [**GitHub Releases**](https://github.com/HeyoCode/Agent_Proxy_Hub/releases) 下载对应平台最新安装包：
- **Windows x64**：`AgentProxyHub-Setup-v0.7.0-win-x64.exe`（默认用户级免管理员安装，同步内置 `aph.exe` CLI，支持 `/PORTABLE=1` 便携模式与 `--minimized` 开机静默托盘自启，覆盖升级自动接管旧实例）
- **macOS**：`AgentProxyHub-v0.7.0-macos-arm64.dmg`

### 方式二：3 步完成首次配置

1. **导入独立专线或机场订阅**：点击左侧「独立线路池」→「批量导入线路」，可直接粘贴 **机场订阅 URL**（自动识别 Clash YAML 与 Base64）一键拉取预览并托管同步，或通过「手动添加」配置 L1/L2 全部 10 种协议（支持单条节点链接自动填表）。
2. **绑定主线 / 备用容灾并接管**：在控制台为每个 AI 应用选择落地出口线路，按需加配**「前置中转」**与**「备用容灾」**，点击右侧 **「一键接管」** 或 **「全链路验真」** 实流测试通道并刷新吞吐。
3. **日常托盘操控与自动化**：可在「网卡隔离」定制关窗最小化/直接退出与托盘左键动作（快捷面板 / 主窗 / 置顶悬浮监控卡）；远程开发可在「应用接管 & CLI → Remote-SSH」生成反向隧道脚本，本地自动化可使用内置 `aph` CLI。

<details>
<summary>⌨️ <strong>命令行工具 (<code>aph</code> CLI) 与 Remote-SSH 用法速查</strong></summary>

```powershell
# 查看网关状态、物理网卡与活跃席位（支持 --json / --port / --url）
aph status --json

# 列出全部 21 席位与端口绑定 / 容灾配置
aph seats

# 绑定主线路并同时配置显式备用容灾与前置中转
aph bind agy <primaryNodeId> --fallback <backupNodeId> --relay <relayNodeId>

# 单节点 / 全量线路直连验真，以及一键接管 / 隔离启动 / 存储清理
aph verify --all
aph takeover cursor on
aph launch cursor
aph cleanup --json
```

```bash
# Remote-SSH 反向隧道示例（让远端服务器 CLI 复用本机独立专线）
ssh -N -R 127.0.0.1:17891:127.0.0.1:17891 dev@your-remote-server
```

</details>

<details>
<summary>📋 <strong>批量导入支持的全协议格式示例（自动识别 L1/L2、去重与 IPv6）</strong></summary>

```text
# L1 原生内核直出协议（零外部依赖）
socks5://user1:pass1@38.154.227.14:1080#US-静态住宅-01
http://user2:pass2@104.221.88.19:8080#US-独享HTTP-02
https://user3:pass3@proxy.example.com:443#JP-TLS专线
trojan://your-password@hk.example.com:443?sni=hk.example.com#HK-CN2-Trojan中转
ss://YWVzLTI1Ni1nY206cGFzc3dvcmQ=@jp.example.com:8388#JP-SS2022中转

# L2 现代协议（按需自动拉起 sing-box Sidecar，物理网卡直绑）
vless://uuid@example.com:443?security=reality&sni=www.apple.com&pbk=xxx&sid=01#JP-Reality
hysteria2://auth@hy2.example.com:443/?sni=hy2.example.com#US-Hy2高速
tuic://uuid:pass@tuic.example.com:443?congestion_control=bbr#SG-TUIC
wireguard://privkey@wg.example.com:51820?publickey=pubkey&address=10.0.0.2/32#Home-WG

# 四段冒号简写格式 & 方括号 IPv6 地址格式
133.167.44.91:10808:agy:jp999#JP-NTT-03
socks5://u6:p6@[2001:db8::1]:1080#IPv6-双栈节点
```

</details>

---

## 🛠️ 本地 REST API 与开发构建

### 工程目录结构

```text
Agent_Proxy_Hub/
├── AgentProxyHub.sln                   # .NET 10 解决方案
├── CHANGELOG.md                        # 版本演进与 Release Notes 权威变更记录
├── LICENSE                             # PolyForm Noncommercial 1.0.0 非商用开源协议
├── src/
│   ├── AgentProxyHub.Core/             # 多协议代理内核、CredentialVault、国家门禁、订阅同步、日志与统计库
│   ├── AgentProxyHub.Host/             # 跨平台本地守护与环回受保护 REST API 服务 (127.0.0.1:17880)
│   ├── AgentProxyHub.Cli/              # 独立 CLI 工具 aph（支持 --json 脚本控制与自动化）
│   └── AgentProxyHub.Desktop.Win/      # Windows 无边框 WebView2 主窗（秒开零黑屏）与原生托盘/悬浮窗
├── macos/                              # macOS 原生菜单栏 NSPopover + WKWebView 宿主与脚本
├── web/                                # React 19 + TypeScript + Vite 前端（双主题自适应 + 隐私护盾 + 365天热力图）
├── installer/                          # Inno Setup 6 (Windows) 与 DMG (macOS) 打包脚本
├── tests/AgentProxyHub.Tests/          # 37 项 xUnit 测试（多协议 / 21 应用 / 备用容灾 / 超时巡检 / WebSocket / CLI）
└── docs/screenshots/                   # README 展示截图资源
```

### 从源码编译与运行

```powershell
# 1. 构建前端资源（输出至 src/AgentProxyHub.Host/wwwroot）
pnpm --dir web install --frozen-lockfile --config.node-linker=hoisted --config.package-import-method=copy
pnpm --dir web build

# 2. 运行全套 xUnit 单元测试（37 项全绿）
dotnet test AgentProxyHub.sln -c Release

# 3. 命令行脚本控制示例 (aph CLI)
dotnet run --project src/AgentProxyHub.Cli/AgentProxyHub.Cli.csproj -c Release -- status --json

# 4. 启动 Windows 无边框托盘客户端
dotnet run --project src/AgentProxyHub.Desktop.Win/AgentProxyHub.Desktop.Win.csproj -c Release

# 或启动跨平台纯 Web 本地网关（Windows / macOS 通用，访问 http://127.0.0.1:17880/）
dotnet run --project src/AgentProxyHub.Host/AgentProxyHub.Host.csproj -c Release
```

<details>
<summary>📦 <strong>打包 Windows 安装程序 / macOS DMG 镜像</strong></summary>

```powershell
# Windows 轻量安装程序（需安装 Inno Setup 6）
powershell -ExecutionPolicy Bypass -File .\installer\windows\build-installer.ps1 -Version 0.7.0

# Windows 离线独立版（内置完整 .NET 10 运行时）
powershell -ExecutionPolicy Bypass -File .\installer\windows\build-installer.ps1 -Version 0.7.0 -SelfContained
```

```bash
# macOS Apple Silicon / Intel DMG 镜像
chmod +x installer/macos/build-dmg.sh
./installer/macos/build-dmg.sh osx-arm64 0.7.0
```

</details>

<details>
<summary>🔌 <strong>本地 REST API 端点速查（默认监听 <code>127.0.0.1:17880</code>，受环回 Origin/Host 守卫保护）</strong></summary>

| 方法 | 路径 | 说明 |
| :---: | :--- | :--- |
| `GET` | `/api/health` | 零开销本地启动健康检查（绕过系统代理） |
| `WS` | `/ws/events` | 受环回 Origin/Host 校验的实时状态与速率推送通道 |
| `GET` | `/api/overview` | 获取物理网卡、绕过隧道、21 款席位配置、多开实例、桌面偏好、节点池、实时吞吐与接管状态 |
| `GET / POST` | `/api/maintenance/{stats,cleanup}` | 查询 `~/.agent-proxy-hub` 目录占用并安全清理失效接管备份与 `.tmp` 缓存 |
| `POST` | `/api/seats/reorder` | 保存自定义 AI 席位展示顺序 |
| `POST` | `/api/seats/{id}/restart` | 优雅关闭旧进程并携独立代理端口重连（严格保护 Antigravity） |
| `GET / POST / PUT / DELETE` | `/api/instances` | 多开隔离实例（独立 `ProfileDir` + `:17920+`）增删改查与 `/launch` 隔离启动 |
| `GET / PUT` | `/api/preferences` | 读写桌面关闭行为、托盘左键动作、悬浮监控窗位置与脱敏 WebDAV 配置 |
| `POST` | `/api/engine/singbox/import` | 离线导入 `.zip / .tar.gz / exe` 内核包（含 Zip-Slip 防护与 SHA-256 验真） |
| `POST` | `/api/webdav/{test,push,pull}` | WebDAV 连通检测、PBKDF2+AES-256-GCM 加密备份上传与云端拉取恢复 |
| `GET` | `/api/stats/conversations/{id}/audit` | 按需解析单会话上下文膨胀曲线与工具调用频次分布 |
| `POST` | `/api/nodes/manual` | 手动添加单条 L1/L2 全协议独立代理线路（含扩展参数 `extra`） |
| `POST` | `/api/nodes/batch-import` | 批量解析并去重导入多协议代理节点（支持 Clash YAML / Base64 / URI） |
| `POST` | `/api/nodes/fetch-subscription` | 拉取机场订阅链接（自动兼容 `clash.meta` / `v2rayN` 与可选代理）并实时预览 |
| `POST` | `/api/nodes/{id}/verify` | 物理网卡直连并发验真单个节点（6s 硬超时自动熔断停止 + 单次最快 RTT） |
| `POST` | `/api/nodes/verify-all` | 全量节点物理直连验真（L2 并集预热 + 8 路并发 + 22s 总超时上限 + 支持前端 Abort 取消） |
| `GET` | `/api/subscriptions` | 获取已托管订阅源脱敏列表（不含明文 Token/URL 路径） |
| `POST` | `/api/subscriptions` | 创建或更新托管订阅源并执行首次增量导入（支持 `profile-title` 自动命名） |
| `POST` | `/api/subscriptions/{id}/refresh` | 手动刷新指定订阅源，含席位出口**快照防漂移锁定**检查 |
| `POST` | `/api/nodes/{id}/approve-insecure` | 显式批准外部导入的 `allowInsecure` 节点跳过 TLS 证书校验 |
| `PUT` | `/api/seats/{id}` | 更新席位启停、绑定线路、前置中转、显式备用容灾 (`fallbackNodeId`)、预期国家门禁、自定义路径/`NO_PROXY` 与漂移确认 |
| `POST` | `/api/seats/{id}/takeover` | 对目标 AI 应用执行一键无损代理接管或还原默认配置 |
| `POST` | `/api/seats/{id}/launch` | 先校验预期出口国家硬门禁，通过后注入独立代理环境变量受控启动应用 |
| `POST` | `/api/seats/{id}/check-country` | 通过 Cloudflare Trace 立即核验当前席位出口国家与历史记录 |
| `GET` | `/api/seats/{id}/connections` | 查询当前席位近期最多 20 条穿透连接 SNI 审计摘要（零请求正文） |
| `POST` | `/api/seats/{id}/verify-tunnel` | 经由 `127.0.0.1:{localPort}` 发起全链路实流验真并刷新吞吐 |
| `GET` | `/api/engine/singbox` | 查询 L2 `sing-box` Sidecar 安装状态、版本与活跃桥接节点数 |
| `POST` | `/api/engine/singbox/install` | 一键从官方 Release 下载安装 `sing-box` 至本地隔离目录 |
| `POST` | `/api/engine/singbox/stop` | 手动停止 `sing-box` Sidecar 进程并释放临时桥接端口 |
| `PUT` | `/api/isolation` | 切换锁定物理网卡、SNI 还原、全局熔断锁、IPv6 双栈及每 5 分钟自动巡检开关 (`autoHealthCheckEnabled`) |
| `GET` | `/api/stats/dashboard` | 查询双源 AI 用量统计（含 TTFT 估算、Cache 节省额与 365 天活跃热力图） |
| `POST` | `/api/stats/sync` | 立即触发一次增量会话日志同步（后台默认每 5 秒自动执行） |
| `GET` | `/api/diagnostics/logs` | 查询自动脱敏的运行时事件日志（启动自动从 `~/.agent-proxy-hub/logs/` 回填最近 200 条记录） |
| `GET` | `/api/system/update-check` | 检查 GitHub Releases 最新版本与发布说明 |

</details>

---

## ❓ 常见问题 FAQ

<details>
<summary><strong>1. 为什么「Token 消耗趋势」有数据，但右栏吞吐一开始是 0 B/s？</strong></summary>

- **Token 统计**直接增量扫描本地 `.claude / .codex / .gemini` 会话日志，不依赖代理流量；
- **吞吐计数**统计流经 `127.0.0.1:17891~17911` 各席位（及 `:17920+` 多开实例）独立端口的真实 TCP 流量。请先点击席位右侧的 **「一键接管」**（已运行中的桌面端应用需彻底退出后重开一次），或直接点击 **「全链路验真」** 立即观测实流穿透数据。

</details>

<details>
<summary><strong>2. 会不会把我的系统全局代理或 Clash / Mihomo 改坏？</strong></summary>

**绝对不会。** Agent Proxy Hub 永远不会写入 Windows WinINET 系统代理或 macOS 系统网络代理设置，也不会修改 Clash 配置文件。所有应用接管均在写入前自动备份原始键值至 `~/.agent-proxy-hub/takeover-backups/`，随时可一键无损还原。

</details>

<details>
<summary><strong>3. 为什么默认关闭 IPv6？什么时候需要开启？</strong></summary>

许多海外静态住宅代理或本地宽带环境仅提供纯 IPv4 出口，若放行 IPv6 既可能触发 Happy Eyeballs 连接卡顿，也可能导致部分直连目标暴露本机真实 IPv6 地址。当你明确使用支持 IPv6 的双栈代理节点时，在控制台总控区一键打开 **IPv6** 开关即可启用双栈解析与 `[::1]` 环回监听。

</details>

<details>
<summary><strong>4. 验真遇到死节点会不会一直转圈？5 分钟自动测试会影响正常对话吗？</strong></summary>

- **硬超时止损**：单节点直连验真设 `6s`（全链路端口验真 `7.5s`、全量批量 `22s`）与前端 `AbortController` 双重超时上限，黑洞节点到期必定自动停止并标为离线超时，也可随时点击取消；
- **无感静默巡检**：后台每 5 分钟仅以并发 3 挑取活跃席位绑定及最久未测的优先节点（最多 16 条）做轻量探测，自动避让手动全量验真，绝不占满本地连接池或卡顿对话。

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

## 📜 开源许可协议（非商用）

本项目采用 [**PolyForm Noncommercial License 1.0.0**](LICENSE) 非商业用途开源协议发布：

- ✅ **允许**：个人学习、研究、测试、日常非商业自用，以及在保留本协议声明前提下的非商业开源二次修改与分享；
- ❌ **禁止**：未经作者（[@HeyoCode](https://github.com/HeyoCode)）书面授权，禁止用于任何形式的商业售卖、付费套壳分发、商业 SaaS 托管、打包进商业软件或企业内部商业用途。

---

<div align="center">
  <sub>Crafted with precision by <a href="https://github.com/HeyoCode">@HeyoCode</a> · 基于 PolyForm Noncommercial 1.0.0 协议开放 · 欢迎点一颗 ⭐ Star</sub>
</div>
