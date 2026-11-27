# Changelog

All notable changes to **Agent Proxy Hub** are documented in this file.

## [v0.7.1] - 2026-11-28

### 🔒 关键安全与稳定性修复
- **彻底修复 `sing-box 1.14+` 新配置规范兼容与 VLESS / 前置中转异常**：`SingBoxSidecarManager` 自动识别内核版本号，针对 `sing-box 1.12+/1.14+` 切换至新版 `dns.servers` (`type: "udp"` + `server` + `bind_interface`)、`domain_resolver`、`route.default_domain_resolver` 及顶层 `endpoints` WireGuard 架构（同时向下兼容 `1.11` 及更早版本），彻底消除 `legacy DNS server formats ... removed in sing-box 1.14.0` 致命退出现象；VLESS 新增 `packet_encoding`、`alpn`、`ws ?ed=2048` Early Data 自动拆分、`httpupgrade` 与 `http/h2` 传输层映射；`MixedProxyServer` / `NodeHealthChecker` 对带前置中转的双跳连接放宽握手与验真超时（`10s~12s`）、在熔断日志打印具体拨号失败原因，且中转异常时不再将正常的落地节点误标为 `offline`。
- **端口段物理隔离与单席位故障隔离**：将 `SingBoxSidecarManager.BridgePortBase` 迁移至 `:18020+` 并增加系统环回端口占用探测，与多开实例 `:17920+` 彻底错峰；`MixedProxyServer.StartListener` 新增 `SocketException` 隔离容错，单个席位端口冲突不再阻断其余席位监听与 L2 同步。
- **修复跨站 WebSocket 劫持 (CSWSH)**：`LoopbackSecurityGuard` 对包含 `/ws/events` 与 `/api/*` 在内的所有受保护路由统一校验 `Origin` 头必须为环回来源，阻止外部网页静默读取本机网卡与节点元数据。
- **主密钥 `.vault.key` 防覆盖与瞬态锁重试**：`CredentialVault` 拆分只读解密与轮换路径，`Unprotect` 绝不覆盖现有密钥；读取遭占用时自动退避重试 3 次，损坏或不可解封密钥轮换前自动备份为 `.vault.key.corrupt-*.bak`。
- **补全备份清理与节点引用完整性**：`StorageMaintenanceService` 同步识别清理失效的 `*.backup.json` 与 `*.bak`；`HubStateDocument.GetReferencedNodeIds()` 与节点删除/订阅同步逻辑完整纳入 `FallbackNodeId` 与多开实例 `Instances[].BoundNodeId`。
- **JSONC 保注释原位补丁与安全进程重启**：新增词法感知 `JsoncPatcher`，接管与还原 VS Code / Cursor / Windsurf / Kiro / Zed / Trae / CodeBuddy / Claude `settings.json` 时完整保留所有 `//` / `/* */` 注释与原有格式；`claude-desktop` 仅匹配桌面进程不误杀 `Claude Code CLI`，多工作区编辑器仅执行礼貌关闭不强杀树。

### 🌐 跨端对齐与热路径优化
- **macOS 宿主与双端动态端口对齐**：`MacTrayHost.swift` 与 Windows `Program.cs` 均从 `state.json` 读取 `hostApiPort`；macOS 托盘弹窗接入 `NSPopoverDelegate` 派发 `tray:shown`/`tray:hidden` 与 `/api/health` 探活，并打包 `aph` CLI；`MainForm.cs` 访问本地偏好强制 `UseProxy = false` 并保留仓库子路径跳转。
- **协议解析、隐私脱敏与性能增强**：`ProxyNodeImporter` 升级 YAML 缩进栈支持多层嵌套（如 `ws-opts.headers.Host`）；`WebDavSyncService` 支持同主机安全重定向；`MixedProxyServer` 为入站 SOCKS/HTTP 全握手阶段施加 15s 超时、`ArrayPool` 块读请求头（完整透传粘包正文）并注入 `Connection: close`；`ShadowsocksAeadStream` 复用 AEAD 密码器实例并接入 `ArrayPool`；`PhysicalNicManager` 增加网卡变更自动感知，`CleanDnsResolver` 复用物理绑定的 DoH 客户端并设置缓存上限；补全 `RightInspector` Toast 隐私脱敏与检测接口零持久化写副作用。

## [v0.7.0] - 2026-11-28

### ⚡ 桌面启动秒开重构（彻底根治长时间黑屏）
- **后台并行构建 + 原生启动过渡遮罩**：桌面宿主 [Program.cs](src/AgentProxyHub.Desktop.Win/Program.cs) 立即创窗进入消息循环并后台初始化代理内核，[MainForm.cs](src/AgentProxyHub.Desktop.Win/MainForm.cs) 跟随 Windows 深浅色注册表设置窗口背景色、`DWMWA_USE_IMMERSIVE_DARK_MODE` 与双缓冲启动过渡层，[index.html](web/index.html) 同步预注入主题底色。
- **WebView2 增量版本印记缓存**：改为仅在版本号或构建时间戳变更时清理旧磁盘缓存，日常启动零销毁 V8 字节码与字体缓存。
- **零开销 `/api/health` 探活 + 进程快照缓存**：启动探活改为 `UseProxy = false` 直连 `/api/health`（35ms 间隔），`AppProxyTakeoverService` 采用 3s 单次 `Process.GetProcesses()` 快照与 2.5s `InspectAll` 结果缓存，消除 21 席位重复 OS 内核枚举与启动超时阻塞；并将托盘面板与悬浮窗延后至主首屏渲染完成后再预热。

### 🔀 显式备用容灾与实时推送 (`FallbackNodeId` + WebSocket)
- **每席位显式备用容灾线路**：`AppSeat` 新增 `FallbackNodeId`，`MixedProxyServer` 按 `绑定主线路 → 显式备用线路 → 同组自动容灾` 顺序切换并在 `SeatTrafficSnapshot.EffectiveNodeName` 与右侧检视栏实时标注当前实际容灾节点。
- **受环回安全守卫保护的 WebSocket `/ws/events`**：前后端（主控制台与悬浮监控窗）优先通过本地 WebSocket 实时推送刷新概览与吞吐速率，状态变更即时推送，断线自动无感回退轮询。

### 🛠️ 生态与跨端运维 (`aph` CLI + Remote-SSH + 存储维护)
- **独立无界面命令行工具 `aph`（`AgentProxyHub.Cli`）**：内置 `SocketsHttpHandler { UseProxy = false }`，支持 `status / seats / nodes / verify / bind / takeover / launch / cleanup` 及 `--json` 结构化输出，同步集成至 Windows / macOS 安装打包脚本。
- **Remote-SSH 反向代理隧道生成器**：「应用接管 & CLI」新增 **Remote-SSH** 页签，针对当前选中席位与目标远端主机一键生成 `ssh -R`、`~/.ssh/config` `RemoteForward` 及远端环境变量导出脚本。
- **本地存储维护 (`StorageMaintenanceService`)**：「网卡隔离」页新增配置目录容量审计与一键安全清理失效备份/临时文件功能。

### 🛡️ 并发连接池防堵死、验真硬超时与运行日志回填
- **彻底解决同源 6 连接堵死导致的统计空白、日志空白与操作卡顿**：将 `BuildOverviewPayload()` 锁内采集拆至 `stateLock` 外部执行；隐藏态托盘快捷面板与悬浮监控窗自动停止后台轮询与 WebSocket 占用；前端请求统一引入 `AbortController` 超时、防重入锁与可见性门控，用量大盘加载过渡态显示清晰、当日零数据时支持一键切换历史视图。
- **单节点/全链路/全量验真硬超时停止与每 5 分钟后台无感自动测试**：`NodeHealthChecker` 单节点 `6s` / 全链路 `7.5s` / 全量 `22s` 与前端 `9.5s~25s` 双保险超时止损；后端每 5 分钟自动低并发（并发 3、至多 16 条优先节点）静默巡检并可在线路池一键开关；同组多节点故障回退收敛为前 3 候选且单节点 `6s` 快速切走。
- **运行日志磁盘启动回填与全链路埋点**：`HubEventLogger` 启动时从 `~/.agent-proxy-hub/logs/hub-*.log` 回填最近 200 条脱敏历史事件，补齐单节点验真、全链路验真、后台巡检与席位接管切换事件记录；全量 **37 项 xUnit 单元测试**通过回归验证。

---

## [v0.6.0] - 2026-11-28

### 🧩 21 款 AI 应用、多开账号隔离与排序置顶
- **内置支持矩阵扩充至 21 款主流 AI 应用**：新增 **Windsurf (`:17906`)**、**Kiro (`:17907`)**、**VS Code Copilot (`:17908`)**、**Zed (`:17909`)**、**Trae (`:17910`)** 与 **CodeBuddy (`:17911`)** 原生 SVG 品牌标识与无损 `settings.json` 代理接管/备份还原。
- **多开账号隔离实例（Profile Isolation, `:17920+`）**：新增 `ManagedAppInstance` 与 `AppInstanceManager`，支持为任意 IDE / CLI 创建多个独立 `--user-data-dir` / `CLAUDE_CONFIG_DIR` / `CODEX_HOME` 配置目录实例，分配独立本地混合代理端口并发多开、互不串号且各自独占出口 IP。
- **席位排序 / 置顶 / 隐藏与安全重启重连**：支持自由上下调序（`/api/seats/reorder`）、置顶与主屏隐藏；非 Antigravity 席位支持「重启重连」（`/api/seats/{id}/restart`），并对正在运行的 `Antigravity` / `language_server` 与本网关进程施加硬保护，严禁强制结束。

### 📊 工作区成本排行与单会话上下文/工具审计
- **单会话上下文膨胀与工具调用审计 (`/api/stats/conversations/{conversationId}/audit`)**：免解密按需解析 Antigravity / Claude Code / Codex 会话流的工具调用分布（`Read` / `Edit` / `Bash` / `function_call` 等）与逐轮上下文膨胀曲线（峰值上下文、膨胀倍率与优化提示），绝不读取用户提示词或源码正文。
- **项目工作区成本排行 (`WorkspaceBreakdown`)**：自动提取会话 `cwd / workspace` 并按工作区聚合算力耗时与估算花费，支持隐私护盾目录脱敏。

### 🪟 置顶悬浮监控窗、SHA-256 离线校验与 WebDAV AES-GCM 同步
- **Always-on-Top 极简置顶悬浮监控窗 (`FloatingMiniForm`)**：共享 WebView2 运行环境与深浅主题状态，支持透明无边框拖拽定位、实时收发速率展示、前 3 个活跃席位一键启停，以及桌面关窗行为/托盘左键点击偏好定制（`/api/preferences`）。
- **`sing-box` 离线包 SHA-256 验证与 Zip-Slip 防护**：支持直接导入本地 `.zip` / `.tar.gz` / `.exe`，强制拦截压缩包 `..` 目录穿越攻击并校验可选 SHA-256 指纹。
- **WebDAV 跨机器加密同步 (`WebDavSyncService`)**：基于 **PBKDF2-SHA256 + AES-256-GCM** 在本地将节点、席位、订阅源与多开实例全量加密后同步至坚果云 / Nextcloud / Alist 等 WebDAV 服务（自动剥离 WebDAV 主密码，凭据库 `CredentialVault` 静态护密），并在全量 **29 项 xUnit 单元测试**中通过回归验证。

---

## [v0.5.0] - 2026-11-28

### 🛡️ 防风控门禁、凭据加密与安全护盾
- **AES-256-GCM + DPAPI 本地凭据静态加密库 (`CredentialVault`)**：首次启动自动无损将旧版 `state.json` 明文节点密码、WireGuard 私钥及订阅 URL 加密迁移为 `enc:v1:...` 密文；`/api/overview`、`/api/nodes` 与批量预览对敏感字段全量脱敏，只返回 `hasCredential` 状态标记。
- **启动前出口国家硬门禁 (Country Guard)**：每个 AI 席位均可指定预期出口国家/地区（如 `US / JP / SG`）；执行受控启动 (`POST /api/seats/{id}/launch`) 前自动经由席位独立端口做零凭据 Cloudflare Trace 国家核验，若实测国家不符、节点离线或跳过证书未批准则立即硬阻断启动，保留最近 12 条核验记录。
- **`skip-cert-verify` 显式风险审批与防订阅静默漂移快照**：外部订阅或批量导入的 `allowInsecure` 节点默认进入「待批准跳过证书」状态，未经人工批准禁止验真或绑定；当多订阅源刷新导致已绑定节点的落地主机/密码发生变化时，保留原节点加注 `[快照锁定]` 并提醒用户确认，绝不静默切换出口。
- **全局一键「隐私护盾 (Privacy Mode)」**：左下角工具栏一键切换打码模式，自动遮蔽真实出口 IP、代理主机地址、用户名与订阅域名，截屏分享零泄密。

### 🌐 订阅源后台托管、连接实流审计与诊断中心
- **多订阅源后台加密托管与 6h 自动同步**：支持同时管理多个订阅源，`ExtractSubscriptionName` 自动解析 `profile-title`（含 Base64）与 `Content-Disposition` 命名，支持单源一键刷新、`AbortController` 取消全量验真、以及 `host:port@user:pass` 简写解析。
- **每席位近期 20 条 SNI 连接实流审计**：`MixedProxyServer` 为每个席位记录最近 20 条穿透连接摘要（时间、SNI 还原域名/端口、路由节点、字节数与耗时，零请求正文），在右侧检视栏可随时展开核验流量是否真实经过专线。
- **脱敏运行日志中心 & 应用内检查更新**：新增 `HubEventLogger`（自动脱敏 `enc:v1:`、`Bearer`、URL 密码/Token），支持应用内分级别检索与复制诊断摘要；GitHub 项目浮窗支持后台静默检查最新版本并直接渲染发布说明。

### 📊 用量统计扩展与终端/路径增强
- **合并 `antigravity-cli` 日志（零双计）与缓存节省统计**：`SessionLogScanner` 自动扫描并合并 `~/.gemini/antigravity-cli/brain` 会话记录并按 `ID` 去重；用量大盘新增 **「Prompt Cache 累计节省金额」** 与 **365 天 AI 编码活跃热力图**（内置 Gemini 4 / Claude 4.5 等新定价规则）。
- **按应用自定义路径与 `NO_PROXY`**：支持为 15 款 AI 应用单独配置 `customExecutablePath` 与附加内网绕过规则 `customNoProxy`，同步增强 Windows `wt.exe` / `pwsh.exe` 交互式终端探测。
- **24 项 xUnit 单元测试全绿**：新增静态加密迁密、订阅防漂移快照、风险审批、`HubEventLogger` 脱敏与 365 天热力图回归测试。

---

## [v0.4.4] - 2026-11-27

### 🛡️ 核心稳定性与并发缺陷清零
- **根除 `sing-box` 长跑管道死锁**：子进程启动后立即开启 `BeginOutputReadLine / BeginErrorReadLine` 异步排空并写入 24 行环形诊断缓冲，彻底消除日志写满 OS 管道缓冲区引发的 L2 流量挂起死锁。
- **L2 节点「常驻拓扑 ∪ 临时验真」并集桥接**：拆分活跃席位节点与单次/批量验真临时节点，扩容验真时保持既有节点桥接端口零漂移，验真未绑定 L2 节点绝不再中断在用席位连接。
- **修复席位 `LocalPort` 在线热切换失效**：`SeatListenerState` 改用值类型端口/IPv6 快照比对，解决同一 `AppSeat` 引用自比导致改端口不重绑的问题。
- **流完整性与零 GC 热路径优化**：补充 15s 首字节空闲连接超时（防预连接池泄露活跃连接数）、双工半关闭 `Shutdown(Send)` + 3s 尾包排空窗口、`ArrayPool<byte>.Shared` 32KB 缓冲池，以及 `ShadowsocksAeadStream` 对称读端互斥锁。

### 🔒 网络隔离、安全防护与并发限流
- **物理网卡直连 HTTPS DoH (RFC 8484) 兜底与 Fake-IP 拦截**：当内网拦截外网 UDP 53 时自动通过物理网卡直连 TCP 443 发起加密 DoH 查询，并全程过滤 `198.18.0.0/15` Fake-IP 污染结果。
- **本地管理 API 环回安全守卫 (`LoopbackSecurityGuard`)**：严格校验 `Host` 与跨站写请求 `Origin` 头，阻断 DNS Rebinding 与外部网页 CSRF 调用。
- **全量验真并发限流与悬空引用清理**：`/api/nodes/verify-all` 升级为 L2 批量单次预热 + `SemaphoreSlim(8)` 限流，消除家用路由 NAT 拥塞导致的假超时；删除节点同步清理席位 `RelayNodeId`。

### ✨ 交互体验增强与回归测试
- **CLI 隔离命令随所选应用实时联动**：「应用接管与 CLI」页点击左侧任意一款应用（15 款任选），右侧 PowerShell / macOS Zsh 脚本自动切换为对应端口与启动命令。
- **机场订阅本地记忆与一键刷新**：「独立线路池」自动记住最近订阅配置并在顶栏提供 **「刷新订阅」** 一键增量去重导入按钮。
- **自动化测试扩充至 19 项全绿**：新增端口原位热切换、L2 并集端口零漂移、环回安全守卫专项回归测试。

---

## [v0.4.3] - 2026-11-27

### 🚀 扩展支持 15 款主流 AI 应用
- **新增 11 款内置应用席位**（默认未启用不占端口，随用随开）：`Claude Desktop`（`:17895`）、`Gemini CLI`（`:17896`）、`Grok Build`（`:17897`）、`OpenCode`（`:17898`）、`OpenClaw`（`:17899`）、`Hermes`（`:17900`）、`Pi`（`:17901`）、`MiniMax Code`（`:17902`）、`Kimi Code`（`:17903`）、`DeepSeek Harness`（`:17904`）、`Oh My Pi`（`:17905`），连同原有 `Claude Code`、`ChatGPT`、`Antigravity`、`Cursor` 共支持 **15 款应用**。
- 升级后自动保留已有席位全部配置，仅无损增量补齐缺失的新应用；全部补充离线矢量品牌图标、进程检测与隔离启动器。

### 🎛️ 独立分流总控动态翻页 & 用量统计修正
- 六角拓扑总控卡升级为 **已启用优先排序 + 标题栏 `‹ 1/4 ›` 翻页控制**，保持对称美观的同时可直接总控全部 15 款应用。
- 修正 `SessionLogScanner` 中 Antigravity `PLANNER_RESPONSE` 耗时计算方向，并在请求日志新增 **「首字(估)」** 列。

---

## [v0.4.2] - 2026-11-27

### 🎨 重新设计「导入独立代理线路」双模式工作台
- 顶部将「**订阅链接拉取**」与「**文本 / 链接粘贴**」分离为清晰的双模式视图，解决窄列挤压变形。
- 新增**格式识别与协议分布胶囊卡片**取代大段 Base64 乱码展示，右侧预览列表重构为三列防溢出布局并增加关键词即时筛选。

---

## [v0.4.1] - 2026-11-27

### ✨ 全协议手动表单、机场订阅拉取与非商用许可
- 手动添加弹窗覆盖全部 **10 种 L1 + L2 协议**动态专属字段，新增单条节点链接一键解析回填。
- 新增 `POST /api/nodes/fetch-subscription` 与零依赖 **Clash / Mihomo YAML 订阅解析器**，支持 Clash YAML 与 Base64 订阅一键拉取预览。
- 正式采用 **PolyForm Noncommercial License 1.0.0** 非商业开源许可协议。

---

## [v0.4.0] - 2026-11-27

### 🌐 L1/L2 双层多协议架构、双跳中转与统一主题
- **L1 原生扩展**：纯 .NET 零依赖支持 `Trojan (TLS 1.3)` 与 `Shadowsocks AEAD`，支持每席位独立配置**「前置中转 → 落地专线」双跳链式加速**。
- **L2 按需 `sing-box` Sidecar**：支持 `VLESS (Reality) / Hysteria2 / TUIC / WireGuard`，空闲零进程占用，物理网卡 `bind_interface` 直绑。
- **官方品牌图标与主题升级**：内置官方原版 AI 品牌矢量标，新增**跟随系统 / 浅色 / 深色**三态切换与浅色全窗统一视觉风格。
