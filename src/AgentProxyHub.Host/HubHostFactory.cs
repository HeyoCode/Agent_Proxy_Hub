using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentProxyHub.Core.Apps;
using AgentProxyHub.Core.Diagnostics;
using AgentProxyHub.Core.Models;
using AgentProxyHub.Core.Networking;
using AgentProxyHub.Core.Storage;
using AgentProxyHub.Core.Telemetry;
using Microsoft.Extensions.FileProviders;

namespace AgentProxyHub.Host;

/// <summary>
/// 跨平台核心运行时控制器与本地 REST API 工厂（同时服务 CLI/守护进程、Windows 托盘壳与 macOS 菜单栏壳）
/// </summary>
public static class HubHostFactory
{
    public sealed record ManualNodeRequest(
        ProxyProtocol Protocol,
        string Name,
        string Group,
        string Host,
        int Port,
        string? Username,
        string? Password,
        string? Method = null,
        string? Sni = null,
        bool AllowInsecure = false,
        Dictionary<string, string>? Extra = null);

    public sealed record SubscriptionFetchRequest(
        string Url,
        string? DefaultGroup = null,
        string? ProxyUrl = null);

    public sealed record SubscriptionUpsertRequest(
        string? Id,
        string? Name,
        string Url,
        string? Group = null,
        string? ProxyUrl = null,
        int AutoRefreshHours = 0);

    public sealed record BatchPreviewRequest(
        string RawText,
        ProxyProtocol DefaultProtocol,
        string DefaultGroup);

    public sealed record SeatUpdateRequest(
        bool? Enabled,
        string? BoundNodeId,
        string? RelayNodeId,
        int? LocalPort,
        bool? KillSwitchEnabled,
        bool? AllowSameGroupFailover,
        string? ExpectedCountryCode = null,
        string? CustomNoProxy = null,
        string? CustomExecutablePath = null,
        bool? AcknowledgeDrift = null,
        bool? IsHidden = null,
        bool? IsPinned = null,
        int? SortOrder = null,
        string? FallbackNodeId = null);

    public sealed record ReorderSeatsRequest(List<string> OrderedSeatIds);

    public sealed record CreateInstanceRequest(
        string BaseSeatId,
        string Name,
        string? ProfileDir = null,
        string? BoundNodeId = null,
        string? ExtraArgs = null,
        string? WorkingDirectory = null);

    public sealed record UpdateInstanceRequest(
        bool? Enabled = null,
        string? BoundNodeId = null,
        bool? KillSwitchEnabled = null,
        string? Name = null,
        string? ExtraArgs = null,
        string? WorkingDirectory = null);

    public sealed record PreferencesUpdateRequest(
        string? CloseAction = null,
        string? TrayLeftClickAction = null,
        bool? MiniCardEnabled = null,
        int? MiniCardX = null,
        int? MiniCardY = null,
        string? WebDavUrl = null,
        string? WebDavUsername = null,
        string? WebDavPassword = null,
        string? WebDavSyncPassphrase = null);

    public sealed record SingBoxImportRequest(string LocalPath, string? ExpectedSha256 = null);

    public sealed record WebDavActionRequest(
        string? WebDavUrl = null,
        string? WebDavUsername = null,
        string? WebDavPassword = null,
        string? Passphrase = null);

    public sealed record ToggleAllSeatsRequest(bool Enabled);

    public sealed record SeatTakeoverRequest(bool Enabled);

    public sealed record SingBoxInstallRequest(string? ProxyUrl);

    public sealed record SingBoxPathRequest(string? BinaryPath);

    public sealed record IsolationUpdateRequest(
        string? LockedPhysicalNicId,
        bool? AutoBypassVirtualNics,
        bool? TlsSniRecoveryEnabled,
        bool? ForceRemoteDns,
        bool? DirectPhysicalDohEnabled,
        string? DohServerIp,
        bool? GlobalKillSwitch,
        bool? EnableIpv6,
        bool? AutoHealthCheckEnabled = null,
        int? AutoHealthCheckMinutes = null);

    public sealed record PricingUpsertRequest(
        string Pattern,
        string DisplayName,
        double InputPerMillion,
        double OutputPerMillion,
        double CacheReadPerMillion,
        double CacheWritePerMillion);

    public static WebApplication BuildApp(string[] args, string? customWwwRoot = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.ConfigureHttpJsonOptions(opts =>
        {
            opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        var store = new HubStateStore();
        var doc = store.LoadOrCreateDefault();

        var nicManager = new PhysicalNicManager(doc.Isolation.LockedPhysicalNicId);
        var dnsResolver = new CleanDnsResolver(nicManager);
        var sidecar = new SingBoxSidecarManager();
        sidecar.SetCustomBinaryPath(doc.Isolation.CustomSingBoxPath);
        var connector = new UpstreamConnector(nicManager, dnsResolver, sidecar);
        var proxyServer = new MixedProxyServer(connector);
        var healthChecker = new NodeHealthChecker(connector);
        var pricingCatalog = new ModelPricingCatalog();
        pricingCatalog.SetCustomRules(doc.CustomPricings);
        var sessionScanner = new SessionLogScanner();
        var usageService = new UsageStatsService(sessionScanner, pricingCatalog);
        var takeoverService = new AppProxyTakeoverService();
        var instanceManager = new AppInstanceManager(store, takeoverService);
        var webDavSync = new WebDavSyncService();
        var maintenanceService = new StorageMaintenanceService();

        // 支持仅用于截图/UI验收的隔离端口模式，避免与已运行实例的端口冲突
        int? verifyPort = ParseVerifyPort(args);
        bool isVerifyOnly = verifyPort.HasValue;

        static List<AppSeat> BuildEffectiveSeats(HubStateDocument d)
        {
            var ordered = d.Seats
                .OrderBy(s => s.IsPinned ? 0 : 1)
                .ThenBy(s => s.SortOrder)
                .ToList();
            foreach (var inst in d.Instances)
            {
                var baseSeat = d.Seats.FirstOrDefault(s => s.Id.Equals(inst.BaseSeatId, StringComparison.OrdinalIgnoreCase));
                if (baseSeat != null) ordered.Add(inst.ToVirtualSeat(baseSeat));
            }
            return ordered;
        }

        if (!isVerifyOnly)
        {
            // 首次启动即异步预扫本地会话日志并拉起全部已启用席位与多开实例端口
            proxyServer.UpdateTopology(BuildEffectiveSeats(doc), doc.Nodes, doc.Isolation);
        }
        _ = Task.Run(() => usageService.SyncNow());

        int apiPort = verifyPort ?? (doc.HostApiPort > 0 ? doc.HostApiPort : 17880);
        builder.WebHost.UseUrls($"http://127.0.0.1:{apiPort}");

        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(doc);
        builder.Services.AddSingleton(nicManager);
        builder.Services.AddSingleton(sidecar);
        builder.Services.AddSingleton(proxyServer);
        builder.Services.AddSingleton(healthChecker);
        builder.Services.AddSingleton(pricingCatalog);
        builder.Services.AddSingleton(usageService);
        builder.Services.AddSingleton(takeoverService);
        builder.Services.AddSingleton(instanceManager);
        builder.Services.AddSingleton(webDavSync);
        builder.Services.AddSingleton(maintenanceService);

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        var stateLock = new object();
        var subRefreshGate = new SemaphoreSlim(1, 1);
        long stateRevision = 0;
        int manualVerifyRunning = 0;
        var wsJsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
        HubEventLogger.Default.Info(
            "SYSTEM",
            $"Agent Proxy Hub v{ProjectMetadata.Version} 核心初始化完成（VerifyOnly={isVerifyOnly} · 网卡={nicManager.GetLockedAdapter()?.Name ?? "未锁定"} · 启用席位={doc.Seats.Count(s => s.Enabled)}/{doc.Seats.Count}）");

        // 本地环回安全守卫：拦截 DNS Rebinding 非法 Host 头与外部网页跨站写请求 (CSRF)
        app.Use(async (ctx, next) =>
        {
            string? hostHdr = ctx.Request.Headers.Host.FirstOrDefault();
            string? originHdr = ctx.Request.Headers.Origin.FirstOrDefault();
            string path = ctx.Request.Path.Value ?? "/";
            bool isProtectedRoute = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/ws/", StringComparison.OrdinalIgnoreCase);
            if (!LoopbackSecurityGuard.IsAllowedRequest(hostHdr, ctx.Request.Method, originHdr, isProtectedRoute))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { message = "Forbidden: loopback origin/host validation failed" });
                return;
            }
            await next();
        });

        object BuildOverviewPayload()
        {
            IsolationSettings isoSnap;
            List<AppSeat> orderedSeats;
            List<ManagedAppInstance> instSnap;
            HubUiPreferences prefSnap;
            List<ProxyNode> redactedNodes;
            List<SubscriptionPublicDto> subDtos;

            lock (stateLock)
            {
                isoSnap = doc.Isolation;
                orderedSeats = doc.Seats
                    .OrderBy(s => s.IsPinned ? 0 : 1)
                    .ThenBy(s => s.SortOrder)
                    .ToList();
                instSnap = doc.Instances.ToList();
                prefSnap = doc.Preferences.ToRedactedCopy();
                redactedNodes = doc.Nodes.Select(n => n.ToRedactedCopy()).ToList();
                subDtos = (doc.Subscriptions ?? []).Select(s => s.ToPublicDto()).ToList();
            }

            // 锁外采集网卡、进程接管和流量快照，防止 IO/OS 枚举阻塞写操作或状态锁排队
            var lockedNic = nicManager.GetLockedAdapter();
            var traffic = proxyServer.GetTrafficSnapshots();
            var adapters = nicManager.GetAdapters();
            var excludedVirtuals = adapters.Where(a => a.IsVirtualOrTunnel && a.Status == "Up").ToList();
            var cliSnippets = AppSeatManager.BuildCliSnippets(
                orderedSeats.FirstOrDefault(s => s.Id == "claude"),
                orderedSeats.FirstOrDefault(s => s.Id == "agy"));
            var seatTakeovers = takeoverService.InspectAll(orderedSeats);

            return new
            {
                project = ProjectMetadata.ToDto(),
                os = OperatingSystem.IsMacOS() ? "mac" : "win",
                arch = RuntimeInformation.OSArchitecture.ToString(),
                memoryMb = Math.Round(Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024.0, 1),
                lockedNic,
                hasDetectedTunnel = nicManager.HasDetectedClashOrTunnel,
                excludedVirtualNics = excludedVirtuals,
                isolation = isoSnap,
                seats = orderedSeats,
                instances = instSnap,
                preferences = prefSnap,
                nodes = redactedNodes,
                subscriptions = subDtos,
                trafficSnapshots = traffic,
                seatTakeovers,
                sidecarEngine = sidecar.GetStatus(),
                cliSnippets
            };
        }

        // 后台每 5 秒自动增量同步一次本地 AI 会话日志，新产生对话 5 秒内自动呈现在大盘中
        var stoppingToken = app.Lifetime.ApplicationStopping;
        stoppingToken.Register(() =>
        {
            try { proxyServer.Dispose(); } catch { }
            try { sidecar.Dispose(); } catch { }
        });
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    try
                    {
                        usageService.SyncNow();
                    }
                    catch
                    {
                        // 单轮读取冲突安全跳过，等待下一轮 5 秒采样
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常关闭
            }
        }, stoppingToken);

        // 解析静态资源目录（支持独立 Host 发布及桌面壳内嵌目录）
        string wwwroot = ResolveWwwRoot(customWwwRoot);
        if (Directory.Exists(wwwroot))
        {
            // 对根路径与 HTML 文档强制禁用 HTTP 缓存，防止桌面端 WebView2 覆盖升级后命中旧版 index.html
            app.Use(async (ctx, next) =>
            {
                string reqPath = ctx.Request.Path.Value ?? "/";
                if (reqPath == "/" || reqPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
                    ctx.Response.Headers["Pragma"] = "no-cache";
                    ctx.Response.Headers["Expires"] = "0";
                }
                await next();
            });

            var fp = new PhysicalFileProvider(wwwroot);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fp });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = fp,
                OnPrepareResponse = ctx =>
                {
                    if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                    {
                        ctx.Context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
                        ctx.Context.Response.Headers["Pragma"] = "no-cache";
                        ctx.Context.Response.Headers["Expires"] = "0";
                    }
                }
            });
        }

        // 0. 零开销启动探活 + 本地 WebSocket 实时状态推送 (/ws/events)
        app.MapGet("/api/health", () => Results.Ok(new { ok = true, version = ProjectMetadata.Version }));

        app.MapGet("/ws/events", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, stoppingToken);
            var ct = linkedCts.Token;

            // 持续读取对端控制帧以响应客户端主动 Close 握手，防止断连挂起
            var recvTask = Task.Run(async () =>
            {
                var recvBuf = new byte[1024];
                try
                {
                    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var recv = await ws.ReceiveAsync(recvBuf, ct);
                        if (recv.MessageType == WebSocketMessageType.Close)
                        {
                            linkedCts.Cancel();
                            break;
                        }
                    }
                }
                catch { }
            }, CancellationToken.None);

            try
            {
                long lastSentRev = -1;
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    long curRev = Interlocked.Read(ref stateRevision);
                    var msg = new { type = "overview", revision = curRev, data = BuildOverviewPayload() };
                    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(msg, wsJsonOpts);
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                    lastSentRev = curRev;

                    // 1.5s 周期推送实时速率；若期间配置变更（revision 跃升）则提前唤醒推送
                    for (int step = 0; step < 6 && !ct.IsCancellationRequested; step++)
                    {
                        await Task.Delay(250, ct);
                        if (Interlocked.Read(ref stateRevision) != lastSentRev) break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally
            {
                if (ws.State == WebSocketState.CloseReceived)
                {
                    try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", CancellationToken.None); } catch { }
                }
                else if (ws.State == WebSocketState.Open)
                {
                    try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); } catch { }
                }
                try { await recvTask; } catch { }
            }
        });

        // 1. 总览与实时隔离状态
        app.MapGet("/api/overview", () => Results.Ok(BuildOverviewPayload()));

        // 2. 线路池：查询、手动添加、批量预览、批量导入、删除、风险审批、验真
        app.MapGet("/api/nodes", () =>
        {
            lock (stateLock) return Results.Ok(doc.Nodes.Select(n => n.ToRedactedCopy()).ToList());
        });

        app.MapPost("/api/nodes/{id}/approve-insecure", (string id) =>
        {
            lock (stateLock)
            {
                var node = doc.Nodes.FirstOrDefault(n => n.Id == id);
                if (node == null) return Results.NotFound();
                node.InsecureApproved = true;
                node.IspSummary = "已人工批准跳过证书校验 · 待验真";
                store.Save(doc);
                HubEventLogger.Default.Warn("GUARD", $"用户已批准节点「{node.Name}」跳过 TLS 证书校验");
                return Results.Ok(node.ToRedactedCopy());
            }
        });

        app.MapPost("/api/nodes/manual", (ManualNodeRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Host) || req.Port is < 1 or > 65535)
                return Results.BadRequest(new { message = "主机地址或端口无效 (1~65535)" });

            string cleanHost = req.Host.Trim().Trim('[', ']');
            string displayHost = cleanHost.Contains(':') ? $"[{cleanHost}]" : cleanHost;
            var node = new ProxyNode
            {
                Id = "n_" + Guid.NewGuid().ToString("N")[..8],
                Protocol = req.Protocol,
                Name = string.IsNullOrWhiteSpace(req.Name) ? $"{req.Protocol}-{displayHost}:{req.Port}" : req.Name.Trim(),
                Group = string.IsNullOrWhiteSpace(req.Group) ? "手动添加专线" : req.Group.Trim(),
                Host = cleanHost,
                Port = req.Port,
                Username = string.IsNullOrWhiteSpace(req.Username) ? null : req.Username.Trim(),
                Password = string.IsNullOrWhiteSpace(req.Password) ? null : req.Password.Trim(),
                Method = string.IsNullOrWhiteSpace(req.Method) ? null : req.Method.Trim().ToLowerInvariant(),
                Sni = string.IsNullOrWhiteSpace(req.Sni) ? null : req.Sni.Trim(),
                AllowInsecure = req.AllowInsecure,
                Extra = req.Extra is { Count: > 0 } ? new Dictionary<string, string>(req.Extra, StringComparer.OrdinalIgnoreCase) : null,
                Status = "unverified",
                ExitIp = "—",
                IspSummary = req.Protocol is ProxyProtocol.VLESS or ProxyProtocol.HYSTERIA2 or ProxyProtocol.TUIC or ProxyProtocol.WIREGUARD
                    ? $"{req.Protocol} · sing-box 手动添加"
                    : "手动添加 · 待物理直连验真"
            };

            lock (stateLock)
            {
                doc.Nodes.Insert(0, node);
                store.Save(doc);
                if (!isVerifyOnly)
                    proxyServer.UpdateTopology(BuildEffectiveSeats(doc), doc.Nodes, doc.Isolation);
            }
            return Results.Ok(node.ToRedactedCopy());
        });

        async Task<(string RawText, string SuggestedTitle, string DetectedFormat)> FetchSubscriptionRawAsync(
            Uri subUri,
            string? proxyUrl,
            string fallbackGroup,
            CancellationToken ct)
        {
            async Task<(string Content, Dictionary<string, IEnumerable<string>> Headers)> DownloadAsync(string userAgent)
            {
                using var handler = new SocketsHttpHandler
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                    AllowAutoRedirect = true,
                    MaxAutomaticRedirections = 5,
                    ConnectTimeout = TimeSpan.FromSeconds(10)
                };
                if (!string.IsNullOrWhiteSpace(proxyUrl))
                {
                    handler.Proxy = new System.Net.WebProxy(proxyUrl.Trim());
                    handler.UseProxy = true;
                }
                else
                {
                    handler.UseProxy = false;
                }

                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(18) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
                using var resp = await http.GetAsync(subUri, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                var hdrs = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in resp.Headers) hdrs[h.Key] = h.Value;
                foreach (var h in resp.Content.Headers) hdrs[h.Key] = h.Value;

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var ms = new MemoryStream();
                var buf = new byte[16384];
                int total = 0;
                const int maxBytes = 4 * 1024 * 1024;
                int read;
                while ((read = await stream.ReadAsync(buf, ct)) > 0)
                {
                    total += read;
                    if (total > maxBytes) throw new InvalidOperationException("订阅内容超过 4MB 安全上限");
                    ms.Write(buf, 0, read);
                }
                return (System.Text.Encoding.UTF8.GetString(ms.ToArray()), hdrs);
            }

            var (raw, headers) = await DownloadAsync("clash.meta/1.18");
            BatchParseResult parsed;
            lock (stateLock)
            {
                parsed = ProxyNodeImporter.Parse(raw, ProxyProtocol.SOCKS5, fallbackGroup, doc.Nodes);
            }

            if (parsed.ValidCount == 0 && parsed.DuplicateCount == 0)
            {
                var (fallbackRaw, fallbackHeaders) = await DownloadAsync("v2rayN/6.45");
                BatchParseResult fallbackParsed;
                lock (stateLock)
                {
                    fallbackParsed = ProxyNodeImporter.Parse(fallbackRaw, ProxyProtocol.SOCKS5, fallbackGroup, doc.Nodes);
                }
                if (fallbackParsed.ValidCount > 0 || fallbackParsed.DuplicateCount > 0)
                {
                    raw = fallbackRaw;
                    headers = fallbackHeaders;
                }
            }

            string title = ProxyNodeImporter.ExtractSubscriptionName(headers, subUri);
            string format = ProxyNodeImporter.LooksLikeClashYaml(raw) ? "Clash YAML" : "URI / Base64";
            return (raw, title, format);
        }

        async Task<SubscriptionSyncEngine.SyncDiffResult> RefreshSubscriptionInternalAsync(
            SubscriptionSource sub,
            CancellationToken ct)
        {
            if (!Uri.TryCreate(sub.Url.Trim(), UriKind.Absolute, out var subUri))
                throw new InvalidOperationException("订阅 URL 格式无效");

            await subRefreshGate.WaitAsync(ct);
            try
            {
                var (raw, suggestedTitle, _) = await FetchSubscriptionRawAsync(subUri, sub.ProxyUrl, sub.Group, ct);
                var parsed = ProxyNodeImporter.Parse(raw, ProxyProtocol.SOCKS5, sub.Group, []);
                var validNodes = parsed.Items.Where(i => i.Node != null).Select(i => i.Node!).ToList();

                lock (stateLock)
                {
                    if (string.IsNullOrWhiteSpace(sub.Name))
                        sub.Name = suggestedTitle;
                    var diff = SubscriptionSyncEngine.MergeParsedNodes(doc, sub, validNodes);
                    store.Save(doc);
                    if (!isVerifyOnly)
                        proxyServer.UpdateTopology(BuildEffectiveSeats(doc), doc.Nodes, doc.Isolation);
                    return diff;
                }
            }
            catch (Exception ex)
            {
                lock (stateLock)
                {
                    sub.LastError = ex.Message;
                    store.Save(doc);
                }
                HubEventLogger.Default.Error("SUB", $"订阅源「{sub.Name}」刷新失败：{ex.Message}");
                throw;
            }
            finally
            {
                subRefreshGate.Release();
            }
        }

        // 后台周期巡检：每 15 分钟检查开启了自动同步的订阅源（间隔默认 6 小时）与日志合并
        _ = Task.Run(async () =>
        {
            using var subTimer = new PeriodicTimer(TimeSpan.FromMinutes(15));
            try
            {
                while (await subTimer.WaitForNextTickAsync(stoppingToken))
                {
                    List<SubscriptionSource> dueSubs;
                    lock (stateLock)
                    {
                        var now = DateTimeOffset.UtcNow;
                        dueSubs = (doc.Subscriptions ?? [])
                            .Where(s => s.AutoRefreshHours > 0 &&
                                        (!s.LastSyncedAt.HasValue || (now - s.LastSyncedAt.Value).TotalHours >= s.AutoRefreshHours))
                            .ToList();
                    }
                    foreach (var sub in dueSubs)
                    {
                        if (stoppingToken.IsCancellationRequested) break;
                        try { await RefreshSubscriptionInternalAsync(sub, stoppingToken); }
                        catch { }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, stoppingToken);

        // 每 N 分钟（默认 5 分钟）后台无感自动线路健康巡检：避开首屏峰值与用户手动验真，最多探测 16 条优先级最高节点
        if (!isVerifyOnly)
        {
            _ = Task.Run(async () =>
            {
                DateTimeOffset lastPatrolUtc = DateTimeOffset.MinValue;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
                    using var patrolTicker = new PeriodicTimer(TimeSpan.FromSeconds(30));
                    while (true)
                    {
                        bool enabled;
                        int intervalMin;
                        List<ProxyNode> targets = [];
                        IsolationSettings iso;
                        string? lockedNicName;

                        lock (stateLock)
                        {
                            enabled = doc.Isolation.AutoHealthCheckEnabled;
                            intervalMin = Math.Clamp(doc.Isolation.AutoHealthCheckMinutes, 1, 60);
                            iso = doc.Isolation;
                        }

                        var nowUtc = DateTimeOffset.UtcNow;
                        if (enabled
                            && Volatile.Read(ref manualVerifyRunning) == 0
                            && (lastPatrolUtc == DateTimeOffset.MinValue || (nowUtc - lastPatrolUtc).TotalMinutes >= intervalMin))
                        {
                            lock (stateLock)
                            {
                                var priorityIds = doc.Seats
                                    .Where(s => s.Enabled)
                                    .SelectMany(s => new[] { s.BoundNodeId, s.FallbackNodeId, s.RelayNodeId })
                                    .Where(id => !string.IsNullOrWhiteSpace(id))
                                    .ToHashSet(StringComparer.Ordinal);

                                var priorityNodes = doc.Nodes
                                    .Where(n => priorityIds.Contains(n.Id) && n.Protocol != ProxyProtocol.DIRECT_PHYSICAL && n.Enabled)
                                    .ToList();
                                var fillNodes = doc.Nodes
                                    .Where(n => !priorityIds.Contains(n.Id) && n.Protocol != ProxyProtocol.DIRECT_PHYSICAL && n.Enabled)
                                    .OrderBy(n => n.LastCheckedAt ?? DateTimeOffset.MinValue)
                                    .Take(Math.Max(0, 16 - priorityNodes.Count))
                                    .ToList();

                                targets = priorityNodes.Concat(fillNodes).Take(16).ToList();
                            }

                            lockedNicName = nicManager.GetLockedAdapter()?.Name;
                            lastPatrolUtc = DateTimeOffset.UtcNow;

                            if (targets.Count > 0)
                            {
                                var (checkedCount, onlineCount, offlineCount) =
                                    await healthChecker.RunSilentPatrolAsync(targets, iso, lockedNicName, stoppingToken);

                                if (checkedCount > 0)
                                {
                                    lock (stateLock) store.Save(doc);
                                    Interlocked.Increment(ref stateRevision);
                                    HubEventLogger.Default.Info(
                                        "VERIFY",
                                        $"后台每 {intervalMin} 分钟无感自动验真完成：共检测 {checkedCount} 条（在线 {onlineCount} / 离线 {offlineCount}）");
                                }
                            }
                        }

                        if (!await patrolTicker.WaitForNextTickAsync(stoppingToken))
                            break;
                    }
                }
                catch (OperationCanceledException) { }
                catch
                {
                    // 自动巡检绝不影响核心代理守护循环
                }
            }, stoppingToken);
        }

        app.MapPost("/api/nodes/fetch-subscription", async (SubscriptionFetchRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Url) ||
                !Uri.TryCreate(req.Url.Trim(), UriKind.Absolute, out var subUri) ||
                (subUri.Scheme != Uri.UriSchemeHttp && subUri.Scheme != Uri.UriSchemeHttps))
            {
                return Results.BadRequest(new { message = "请输入有效的 http:// 或 https:// 订阅链接" });
            }

            string group = string.IsNullOrWhiteSpace(req.DefaultGroup) ? "订阅导入池" : req.DefaultGroup.Trim();
            try
            {
                var (raw, suggestedTitle, format) = await FetchSubscriptionRawAsync(subUri, req.ProxyUrl, group, ct);
                BatchParseResult parsed;
                lock (stateLock)
                {
                    parsed = ProxyNodeImporter.Parse(raw, ProxyProtocol.SOCKS5, group, doc.Nodes);
                }

                // 预览列表中的节点统一脱敏，避免密码回传到浏览器
                foreach (var item in parsed.Items)
                {
                    if (item.Node != null) item.Node = item.Node.ToRedactedCopy();
                }

                return Results.Ok(new
                {
                    rawText = raw,
                    preview = parsed,
                    detectedFormat = format,
                    suggestedTitle
                });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { message = $"拉取订阅失败：{ex.Message}（若订阅域名被拦截，可在右侧填写本机代理如 http://127.0.0.1:7897）" });
            }
        });

        app.MapGet("/api/subscriptions", () =>
        {
            lock (stateLock)
                return Results.Ok((doc.Subscriptions ?? []).Select(s => s.ToPublicDto()).ToList());
        });

        app.MapPost("/api/subscriptions", async (SubscriptionUpsertRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Url) ||
                !Uri.TryCreate(req.Url.Trim(), UriKind.Absolute, out var subUri) ||
                (subUri.Scheme != Uri.UriSchemeHttp && subUri.Scheme != Uri.UriSchemeHttps))
            {
                return Results.BadRequest(new { message = "请输入有效的 http:// 或 https:// 订阅链接" });
            }

            SubscriptionSource sub;
            lock (stateLock)
            {
                doc.Subscriptions ??= new();
                sub = !string.IsNullOrWhiteSpace(req.Id)
                    ? (doc.Subscriptions.FirstOrDefault(s => s.Id == req.Id) ?? new SubscriptionSource())
                    : (doc.Subscriptions.FirstOrDefault(s => string.Equals(s.Url, req.Url.Trim(), StringComparison.Ordinal)) ?? new SubscriptionSource());

                if (!doc.Subscriptions.Contains(sub))
                    doc.Subscriptions.Insert(0, sub);

                sub.Url = req.Url.Trim();
                sub.Group = string.IsNullOrWhiteSpace(req.Group) ? (string.IsNullOrWhiteSpace(sub.Group) ? "订阅导入池" : sub.Group) : req.Group.Trim();
                sub.ProxyUrl = string.IsNullOrWhiteSpace(req.ProxyUrl) ? null : req.ProxyUrl.Trim();
                sub.AutoRefreshHours = req.AutoRefreshHours > 0 ? Math.Clamp(req.AutoRefreshHours, 1, 168) : 0;
                if (!string.IsNullOrWhiteSpace(req.Name)) sub.Name = req.Name.Trim();
                store.Save(doc);
            }

            try
            {
                var diff = await RefreshSubscriptionInternalAsync(sub, ct);
                return Results.Ok(new
                {
                    subscription = sub.ToPublicDto(),
                    diff
                });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new
                {
                    message = $"订阅源已保存但首次拉取失败：{ex.Message}",
                    subscription = sub.ToPublicDto()
                });
            }
        });

        app.MapPost("/api/subscriptions/{id}/refresh", async (string id, CancellationToken ct) =>
        {
            SubscriptionSource? sub;
            lock (stateLock)
            {
                sub = (doc.Subscriptions ?? []).FirstOrDefault(s => s.Id == id);
            }
            if (sub == null) return Results.NotFound(new { message = "未找到对应订阅源" });

            try
            {
                var diff = await RefreshSubscriptionInternalAsync(sub, ct);
                return Results.Ok(new
                {
                    subscription = sub.ToPublicDto(),
                    diff
                });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { message = $"刷新订阅失败：{ex.Message}", subscription = sub.ToPublicDto() });
            }
        });

        app.MapDelete("/api/subscriptions/{id}", (string id, bool? removeNodes) =>
        {
            lock (stateLock)
            {
                int removedSub = (doc.Subscriptions ?? []).RemoveAll(s => s.Id == id);
                if (removedSub == 0) return Results.NotFound();

                var boundIds = doc.Seats
                    .SelectMany(s => new[] { s.BoundNodeId, s.RelayNodeId })
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToHashSet(StringComparer.Ordinal);

                if (removeNodes == true)
                {
                    doc.Nodes.RemoveAll(n => string.Equals(n.SourceSubscriptionId, id, StringComparison.Ordinal) && !boundIds.Contains(n.Id));
                }
                foreach (var n in doc.Nodes.Where(n => string.Equals(n.SourceSubscriptionId, id, StringComparison.Ordinal)))
                {
                    n.SourceSubscriptionId = null;
                }
                store.Save(doc);
                if (!isVerifyOnly)
                    proxyServer.UpdateTopology(BuildEffectiveSeats(doc), doc.Nodes, doc.Isolation);
                HubEventLogger.Default.Info("SUB", $"已移除订阅源 {id}（保留已绑定席位快照）");
                return Results.Ok(new { removed = removedSub });
            }
        });

        app.MapPost("/api/nodes/batch-preview", (BatchPreviewRequest req) =>
        {
            lock (stateLock)
            {
                var parsed = ProxyNodeImporter.Parse(req.RawText ?? string.Empty, req.DefaultProtocol, req.DefaultGroup, doc.Nodes);
                foreach (var item in parsed.Items)
                {
                    if (item.Node != null) item.Node = item.Node.ToRedactedCopy();
                }
                return Results.Ok(parsed);
            }
        });

        app.MapPost("/api/nodes/batch-import", (BatchPreviewRequest req) =>
        {
            List<ProxyNode> added = new();
            BatchParseResult parsed;
            lock (stateLock)
            {
                parsed = ProxyNodeImporter.Parse(req.RawText ?? string.Empty, req.DefaultProtocol, req.DefaultGroup, doc.Nodes);
                foreach (var item in parsed.Items.Where(i => i.Valid && i.Node != null))
                {
                    doc.Nodes.Add(item.Node!);
                    added.Add(item.Node!);
                }
                if (added.Count > 0)
                {
                    store.Save(doc);
                    if (!isVerifyOnly)
                        proxyServer.UpdateTopology(BuildEffectiveSeats(doc), doc.Nodes, doc.Isolation);
                }
            }
            return Results.Ok(new
            {
                addedCount = added.Count,
                duplicateCount = parsed.DuplicateCount,
                errorCount = parsed.ErrorCount,
                addedNodes = added.Select(n => n.ToRedactedCopy()).ToList()
            });
        });

        app.MapDelete("/api/nodes/{id}", (string id) =>
        {
            lock (stateLock)
            {
                int removed = doc.Nodes.RemoveAll(n => n.Id == id);
                if (removed == 0) return Results.NotFound();
                foreach (var seat in doc.Seats)
                {
                    if (seat.BoundNodeId == id)
                        seat.BoundNodeId = doc.Nodes.FirstOrDefault()?.Id;
                    if (seat.RelayNodeId == id)
                        seat.RelayNodeId = null;
                    if (seat.FallbackNodeId == id)
                        seat.FallbackNodeId = null;
                }
                store.Save(doc);
                Interlocked.Increment(ref stateRevision);
                if (!isVerifyOnly)
                    proxyServer.UpdateTopology(BuildEffectiveSeats(doc), doc.Nodes, doc.Isolation);
                return Results.Ok(new { removed });
            }
        });

        app.MapPost("/api/nodes/{id}/verify", async (string id, CancellationToken ct) =>
        {
            ProxyNode? target;
            IsolationSettings iso;
            lock (stateLock)
            {
                target = doc.Nodes.FirstOrDefault(n => n.Id == id);
                iso = doc.Isolation;
            }
            if (target == null) return Results.NotFound();

            if (target.Protocol == ProxyProtocol.DIRECT_PHYSICAL)
            {
                target.Status = "online";
                target.LatencyMs = 1;
                target.ExitIp = nicManager.GetLockedAdapter()?.IPv4Address ?? "物理网卡直出";
                target.IspSummary = "本机物理出口直绑（未经代理节点）";
                target.AiReachability = ["物理直出通道"];
                target.LastCheckedAt = DateTimeOffset.UtcNow;
                lock (stateLock) store.Save(doc);
                Interlocked.Increment(ref stateRevision);
                HubEventLogger.Default.Info("VERIFY", $"节点「{target.Name}」直出验真完成：{target.ExitIp} (1 ms)");
                return Results.Ok(target.ToRedactedCopy());
            }

            using var verifyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            verifyCts.CancelAfter(TimeSpan.FromSeconds(8));
            await healthChecker.VerifyNodeAsync(target, iso, verifyCts.Token);
            lock (stateLock) store.Save(doc);
            Interlocked.Increment(ref stateRevision);
            if (target.Status == "online")
                HubEventLogger.Default.Info("VERIFY", $"节点「{target.Name}」验真通过：{target.ExitIp} ({target.LatencyMs ?? 1} ms)");
            else
                HubEventLogger.Default.Warn("VERIFY", $"节点「{target.Name}」验真离线：{target.IspSummary}");
            return Results.Ok(target.ToRedactedCopy());
        });

        app.MapPost("/api/nodes/verify-all", async (CancellationToken ct) =>
        {
            List<ProxyNode> targets;
            IsolationSettings iso;
            string? lockedNicName;
            lock (stateLock)
            {
                targets = doc.Nodes.Where(n => n.Protocol != ProxyProtocol.DIRECT_PHYSICAL).ToList();
                iso = doc.Isolation;
                lockedNicName = nicManager.GetLockedAdapter()?.Name;
            }

            Interlocked.Increment(ref manualVerifyRunning);
            var verifiedSet = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            // 批量验真前一次性预热全部待测 L2 节点至 sing-box 并集桥，避免并发连环重启
            var l2Targets = targets.Where(n => n.RequiresSidecarEngine && (!n.AllowInsecure || n.InsecureApproved)).ToList();
            if (l2Targets.Count > 0 && sidecar.GetStatus().Installed)
            {
                _ = sidecar.EnsureEphemeralNodesReady(l2Targets, lockedNicName, iso, out _);
            }

            try
            {
                using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                batchCts.CancelAfter(TimeSpan.FromSeconds(22));
                using var gate = new SemaphoreSlim(8, 8);
                try
                {
                    await Task.WhenAll(targets.Select(async n =>
                    {
                        await gate.WaitAsync(batchCts.Token);
                        try
                        {
                            await healthChecker.VerifyNodeAsync(n, iso, batchCts.Token);
                            verifiedSet.TryAdd(n.Id, 1);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }));
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 全量 22s 硬超时触发时，将尚未测完的节点置为连接超时，确保接口必有响应且不无限挂起
                    foreach (var pendingNode in targets.Where(t => !verifiedSet.ContainsKey(t.Id)))
                    {
                        pendingNode.Status = "offline";
                        pendingNode.LatencyMs = null;
                        pendingNode.ExitIp = "—";
                        pendingNode.IspSummary = "批量验真超时";
                        pendingNode.AiReachability = new List<string> { "连接超时" };
                        pendingNode.LastCheckedAt = DateTimeOffset.UtcNow;
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref manualVerifyRunning);
                if (l2Targets.Count > 0)
                {
                    sidecar.ReleaseEphemeralNodes(l2Targets.Select(n => n.Id));
                }
            }

            lock (stateLock) store.Save(doc);
            Interlocked.Increment(ref stateRevision);
            int onlineCount = targets.Count(n => n.Status == "online");
            HubEventLogger.Default.Info("VERIFY", $"全量线路验真完成：共 {targets.Count} 条，在线 {onlineCount} 条 / 离线 {targets.Count - onlineCount} 条");
            return Results.Ok(doc.Nodes.Select(n => n.ToRedactedCopy()).ToList());
        });

        // 3. AI 软件席位管理与受控环境启动器
        app.MapPut("/api/seats/{id}", (string id, SeatUpdateRequest req) =>
        {
            lock (stateLock)
            {
                var seat = doc.Seats.FirstOrDefault(s => s.Id == id);
                if (seat == null) return Results.NotFound();

                if (req.BoundNodeId != null)
                {
                    var cand = doc.Nodes.FirstOrDefault(n => n.Id == req.BoundNodeId);
                    if (cand != null && cand.AllowInsecure && !cand.InsecureApproved)
                    {
                        return Results.BadRequest(new { message = $"节点「{cand.Name}」声明跳过 TLS 证书校验且尚未批准，请先在线路池点击「批准风险」后再绑定。" });
                    }
                }
                if (!string.IsNullOrWhiteSpace(req.RelayNodeId))
                {
                    var relayCand = doc.Nodes.FirstOrDefault(n => n.Id == req.RelayNodeId);
                    if (relayCand != null && relayCand.AllowInsecure && !relayCand.InsecureApproved)
                    {
                        return Results.BadRequest(new { message = $"前置中转「{relayCand.Name}」跳过 TLS 证书校验尚未批准，请先批准风险。" });
                    }
                }
                if (!string.IsNullOrWhiteSpace(req.FallbackNodeId))
                {
                    var fbCand = doc.Nodes.FirstOrDefault(n => n.Id == req.FallbackNodeId);
                    if (fbCand != null && fbCand.AllowInsecure && !fbCand.InsecureApproved)
                    {
                        return Results.BadRequest(new { message = $"备用容灾节点「{fbCand.Name}」跳过 TLS 证书校验尚未批准，请先批准风险。" });
                    }
                }

                bool portChanged = req.LocalPort is >= 1024 and <= 65535 && req.LocalPort.Value != seat.LocalPort;
                if (req.Enabled.HasValue) seat.Enabled = req.Enabled.Value;
                if (req.BoundNodeId != null)
                {
                    seat.BoundNodeId = req.BoundNodeId;
                    var boundNode = doc.Nodes.FirstOrDefault(n => n.Id == req.BoundNodeId);
                    seat.BoundNodeSnapshotHash = boundNode?.ConfigSnapshotHash;
                    seat.HasNodeDrift = false;
                }
                if (req.RelayNodeId != null) seat.RelayNodeId = string.IsNullOrWhiteSpace(req.RelayNodeId) ? null : req.RelayNodeId;
                if (req.FallbackNodeId != null) seat.FallbackNodeId = string.IsNullOrWhiteSpace(req.FallbackNodeId) ? null : req.FallbackNodeId;
                if (req.LocalPort is >= 1024 and <= 65535) seat.LocalPort = req.LocalPort.Value;
                if (req.KillSwitchEnabled.HasValue) seat.KillSwitchEnabled = req.KillSwitchEnabled.Value;
                if (req.AllowSameGroupFailover.HasValue) seat.AllowSameGroupFailover = req.AllowSameGroupFailover.Value;
                if (req.ExpectedCountryCode != null)
                {
                    string trimmedCountry = req.ExpectedCountryCode.Trim().ToUpperInvariant();
                    seat.ExpectedCountryCode = string.IsNullOrEmpty(trimmedCountry) ? null : trimmedCountry;
                }
                if (req.CustomNoProxy != null)
                    seat.CustomNoProxy = string.IsNullOrWhiteSpace(req.CustomNoProxy) ? null : req.CustomNoProxy.Trim();
                if (req.CustomExecutablePath != null)
                    seat.CustomExecutablePath = string.IsNullOrWhiteSpace(req.CustomExecutablePath) ? null : req.CustomExecutablePath.Trim().Trim('"');
                if (req.AcknowledgeDrift == true)
                {
                    var curNode = doc.Nodes.FirstOrDefault(n => n.Id == seat.BoundNodeId);
                    seat.BoundNodeSnapshotHash = curNode?.ConfigSnapshotHash;
                    seat.HasNodeDrift = false;
                }
                if (req.IsHidden.HasValue) seat.IsHidden = req.IsHidden.Value;
                if (req.IsPinned.HasValue) seat.IsPinned = req.IsPinned.Value;
                if (req.SortOrder.HasValue) seat.SortOrder = req.SortOrder.Value;

                takeoverService.InvalidateCache();
                store.Save(doc);
                Interlocked.Increment(ref stateRevision);
                if (!isVerifyOnly)
                {
                    proxyServer.UpdateTopology(BuildEffectiveSeats(doc), doc.Nodes, doc.Isolation);
                    if (portChanged && takeoverService.InspectSeat(seat).IsTakenOver)
                    {
                        takeoverService.SetTakeover(seat, true);
                    }
                }
                if (req.Enabled.HasValue || req.BoundNodeId != null || req.FallbackNodeId != null || req.RelayNodeId != null)
                {
                    string boundName = doc.Nodes.FirstOrDefault(n => n.Id == seat.BoundNodeId)?.Name ?? "未绑定";
                    HubEventLogger.Default.Info("PROXY", $"更新席位「{seat.Name}」(:{seat.LocalPort})：状态={(seat.Enabled ? "启用" : "暂停")} · 主线={boundName}");
                }
                return Results.Ok(seat);
            }
        });

        app.MapPost("/api/seats/{id}/takeover", (string id, SeatTakeoverRequest req) =>
        {
            if (isVerifyOnly)
            {
                return Results.Ok(new
                {
                    ok = false,
                    message = "验证预览模式禁止修改本机应用配置文件",
                    seatTakeovers = takeoverService.InspectAll(doc.Seats)
                });
            }

            lock (stateLock)
            {
                var seat = doc.Seats.FirstOrDefault(s => s.Id == id);
                if (seat == null) return Results.NotFound();

                if (req.Enabled && !seat.Enabled)
                {
                    seat.Enabled = true;
                    store.Save(doc);
                    proxyServer.UpdateTopology(BuildEffectiveSeats(doc), doc.Nodes, doc.Isolation);
                }

                var (ok, message, status) = takeoverService.SetTakeover(seat, req.Enabled);
                Interlocked.Increment(ref stateRevision);
                HubEventLogger.Default.Info("PROXY", $"席位「{seat.Name}」{(req.Enabled ? "接管" : "还原")} -> {message}");
                return Results.Ok(new
                {
                    ok,
                    message,
                    status,
                    seatTakeovers = takeoverService.InspectAll(doc.Seats)
                });
            }
        });

        app.MapPost("/api/seats/{id}/launch", async (string id, CancellationToken ct) =>
        {
            if (isVerifyOnly)
            {
                return Results.Ok(new
                {
                    ok = false,
                    message = "验证预览模式不拉起外部应用进程",
                    seatTakeovers = takeoverService.InspectAll(doc.Seats)
                });
            }

            AppSeat? seat;
            ProxyNode? boundNode;
            IsolationSettings iso;
            lock (stateLock)
            {
                seat = doc.Seats.FirstOrDefault(s => s.Id == id);
                if (seat == null) return Results.NotFound();

                if (!seat.Enabled)
                {
                    seat.Enabled = true;
                    store.Save(doc);
                    proxyServer.UpdateTopology(BuildEffectiveSeats(doc), doc.Nodes, doc.Isolation);
                }
                boundNode = doc.Nodes.FirstOrDefault(n => n.Id == seat.BoundNodeId);
                iso = doc.Isolation;
            }

            // 若配置了「出口国家硬门禁」，启动客户端前强制先走独立端口验明出口国家
            if (!string.IsNullOrWhiteSpace(seat.ExpectedCountryCode))
            {
                var check = await healthChecker.CheckSeatCountryGuardAsync(seat, boundNode, iso, ct);
                if (!check.Passed)
                {
                    return Results.Ok(new
                    {
                        ok = false,
                        blockedByCountryGuard = true,
                        countryCheck = check,
                        message = check.Reason,
                        seatTakeovers = takeoverService.InspectAll(doc.Seats)
                    });
                }
            }

            var (ok, message) = takeoverService.LaunchWithProxy(seat);
            HubEventLogger.Default.Info("PROXY", $"受控启动 {seat.Name} (:{seat.LocalPort}) -> {message}");
            return Results.Ok(new
            {
                ok,
                message,
                seatTakeovers = takeoverService.InspectAll(doc.Seats)
            });
        });

        app.MapPost("/api/seats/{id}/check-country", async (string id, CancellationToken ct) =>
        {
            AppSeat? seat;
            ProxyNode? boundNode;
            IsolationSettings iso;
            lock (stateLock)
            {
                seat = doc.Seats.FirstOrDefault(s => s.Id == id);
                if (seat == null) return Results.NotFound();
                if (!seat.Enabled && !isVerifyOnly)
                {
                    seat.Enabled = true;
                    store.Save(doc);
                    proxyServer.UpdateTopology(BuildEffectiveSeats(doc), doc.Nodes, doc.Isolation);
                }
                boundNode = doc.Nodes.FirstOrDefault(n => n.Id == seat.BoundNodeId);
                iso = doc.Isolation;
            }

            var res = await healthChecker.CheckSeatCountryGuardAsync(seat, boundNode, iso, ct);
            return Results.Ok(new
            {
                check = res,
                history = healthChecker.GetCountryCheckHistory(id)
            });
        });

        app.MapGet("/api/seats/{id}/connections", (string id, int? limit) =>
        {
            var items = proxyServer.GetRecentConnections(id, limit ?? 20);
            var countryHistory = healthChecker.GetCountryCheckHistory(id);
            return Results.Ok(new { connections = items, countryHistory });
        });

        app.MapPost("/api/seats/{id}/verify-tunnel", async (string id, CancellationToken ct) =>
        {
            AppSeat? seat;
            ProxyNode? boundNode;
            IsolationSettings iso;
            lock (stateLock)
            {
                seat = doc.Seats.FirstOrDefault(s => s.Id == id);
                if (seat == null) return Results.NotFound();
                if (!seat.Enabled && !isVerifyOnly)
                {
                    seat.Enabled = true;
                    store.Save(doc);
                    proxyServer.UpdateTopology(BuildEffectiveSeats(doc), doc.Nodes, doc.Isolation);
                }
                boundNode = doc.Nodes.FirstOrDefault(n => n.Id == seat.BoundNodeId);
                iso = doc.Isolation;
            }

            if (isVerifyOnly)
            {
                if (boundNode != null && boundNode.Protocol != ProxyProtocol.DIRECT_PHYSICAL)
                {
                    using var verifyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    verifyCts.CancelAfter(TimeSpan.FromSeconds(8));
                    await healthChecker.VerifyNodeAsync(boundNode, iso, verifyCts.Token);
                }
                Interlocked.Increment(ref stateRevision);
                HubEventLogger.Default.Info("VERIFY", $"席位「{seat.Name}」预览直连验真完成：{boundNode?.ExitIp ?? "—"}");
                return Results.Ok(new TunnelVerificationResult
                {
                    Ok = boundNode?.Status == "online",
                    SeatId = seat.Id,
                    LocalPort = seat.LocalPort,
                    ExitIp = boundNode?.ExitIp ?? "—",
                    LatencyMs = boundNode?.LatencyMs,
                    Node = boundNode,
                    Message = "预览模式完成节点直连检测"
                });
            }

            using var tunnelCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            tunnelCts.CancelAfter(TimeSpan.FromSeconds(9));
            var res = await healthChecker.VerifyViaLocalSeatAsync(seat.Id, seat.LocalPort, boundNode, iso, null, tunnelCts.Token);
            var snap = proxyServer.GetTrafficSnapshots().FirstOrDefault(s => s.SeatId == seat.Id);
            res.BytesTx = snap?.BytesTx ?? 0;
            res.BytesRx = snap?.BytesRx ?? 0;

            lock (stateLock)
            {
                store.Save(doc);
            }
            Interlocked.Increment(ref stateRevision);
            if (res.Ok)
                HubEventLogger.Default.Info("VERIFY", $"{seat.Name} :{seat.LocalPort} {res.Message}");
            else
                HubEventLogger.Default.Warn("VERIFY", $"{seat.Name} :{seat.LocalPort} {res.Message}");

            return Results.Ok(res);
        });

        app.MapPost("/api/seats/toggle-all", (ToggleAllSeatsRequest req) =>
        {
            lock (stateLock)
            {
                foreach (var seat in doc.Seats)
                {
                    seat.Enabled = req.Enabled;
                }

                store.Save(doc);
                Interlocked.Increment(ref stateRevision);
                if (!isVerifyOnly)
                    proxyServer.UpdateTopology(BuildEffectiveSeats(doc), doc.Nodes, doc.Isolation);
                HubEventLogger.Default.Info("PROXY", req.Enabled ? "已一键开启全部 AI 席位独立端口" : "已一键暂停全部 AI 席位监听");
                return Results.Ok(doc.Seats);
            }
        });

        app.MapPost("/api/seats/reorder", (ReorderSeatsRequest req) =>
        {
            lock (stateLock)
            {
                int idx = 1;
                var map = doc.Seats.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
                foreach (string seatId in req.OrderedSeatIds ?? [])
                {
                    if (map.TryGetValue(seatId, out var s))
                    {
                        s.SortOrder = idx++;
                        map.Remove(seatId);
                    }
                }
                foreach (var rem in map.Values.OrderBy(s => s.SortOrder))
                {
                    rem.SortOrder = idx++;
                }

                store.Save(doc);
                return Results.Ok(doc.Seats.OrderBy(s => s.IsPinned ? 0 : 1).ThenBy(s => s.SortOrder).ToList());
            }
        });

        app.MapPost("/api/seats/{id}/restart", async (string id, CancellationToken ct) =>
        {
            if (isVerifyOnly)
            {
                return Results.Ok(new
                {
                    ok = false,
                    message = "验证预览模式不终止或重启外部应用进程"
                });
            }

            AppSeat? seat;
            lock (stateLock)
            {
                seat = doc.Seats.FirstOrDefault(s => s.Id == id);
                if (seat == null) return Results.NotFound();
            }

            var (ok, message) = await takeoverService.GracefulRestartWithProxyAsync(seat, ct);
            HubEventLogger.Default.Info("PROXY", $"席位重启重连 {seat.Name} (:{seat.LocalPort}) -> {message}");
            return Results.Ok(new
            {
                ok,
                message,
                seatTakeovers = takeoverService.InspectAll(doc.Seats)
            });
        });

        // 3.5 多开隔离实例 (ManagedAppInstance) CRUD 与启动
        app.MapGet("/api/instances", () =>
        {
            lock (stateLock) return Results.Ok(doc.Instances);
        });

        app.MapPost("/api/instances", (CreateInstanceRequest req) =>
        {
            lock (stateLock)
            {
                try
                {
                    var inst = instanceManager.CreateInstance(
                        doc,
                        req.BaseSeatId,
                        req.Name,
                        req.ProfileDir,
                        req.BoundNodeId,
                        req.ExtraArgs,
                        req.WorkingDirectory);

                    if (!isVerifyOnly)
                        proxyServer.UpdateTopology(BuildEffectiveSeats(doc), doc.Nodes, doc.Isolation);

                    HubEventLogger.Default.Info("INSTANCE", $"新建多开隔离实例「{inst.Name}」(:{inst.LocalPort})");
                    return Results.Ok(inst);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            }
        });

        app.MapPut("/api/instances/{id}", (string id, UpdateInstanceRequest req) =>
        {
            lock (stateLock)
            {
                var inst = doc.Instances.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (inst == null) return Results.NotFound();
                if (req.Enabled.HasValue) inst.Enabled = req.Enabled.Value;
                if (req.BoundNodeId != null) inst.BoundNodeId = req.BoundNodeId;
                if (req.KillSwitchEnabled.HasValue) inst.KillSwitchEnabled = req.KillSwitchEnabled.Value;
                if (!string.IsNullOrWhiteSpace(req.Name)) inst.Name = req.Name.Trim();
                if (req.ExtraArgs != null) inst.ExtraArgs = string.IsNullOrWhiteSpace(req.ExtraArgs) ? null : req.ExtraArgs.Trim();
                if (req.WorkingDirectory != null) inst.WorkingDirectory = string.IsNullOrWhiteSpace(req.WorkingDirectory) ? null : req.WorkingDirectory.Trim();

                store.Save(doc);
                if (!isVerifyOnly)
                    proxyServer.UpdateTopology(BuildEffectiveSeats(doc), doc.Nodes, doc.Isolation);
                return Results.Ok(inst);
            }
        });

        app.MapDelete("/api/instances/{id}", (string id) =>
        {
            lock (stateLock)
            {
                if (!instanceManager.DeleteInstance(doc, id, out string message))
                    return Results.NotFound(new { message });
                if (!isVerifyOnly)
                    proxyServer.UpdateTopology(BuildEffectiveSeats(doc), doc.Nodes, doc.Isolation);
                return Results.Ok(new { ok = true, message });
            }
        });

        app.MapPost("/api/instances/{id}/launch", (string id) =>
        {
            if (isVerifyOnly)
                return Results.Ok(new { ok = false, message = "验证预览模式不拉起多开实例进程" });

            lock (stateLock)
            {
                var inst = doc.Instances.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (inst == null) return Results.NotFound();
                if (!inst.Enabled)
                {
                    inst.Enabled = true;
                    store.Save(doc);
                    proxyServer.UpdateTopology(BuildEffectiveSeats(doc), doc.Nodes, doc.Isolation);
                }
                var (ok, message) = instanceManager.LaunchInstance(doc, id);
                HubEventLogger.Default.Info("INSTANCE", $"启动多开实例 {inst.Name} (:{inst.LocalPort}) -> {message}");
                return Results.Ok(new { ok, message, instance = inst });
            }
        });

        // 4. 物理网卡与防干扰设置
        app.MapGet("/api/isolation", () =>
        {
            lock (stateLock)
            {
                return Results.Ok(new
                {
                    settings = doc.Isolation,
                    adapters = nicManager.GetAdapters(),
                    lockedAdapter = nicManager.GetLockedAdapter(),
                    sidecarEngine = sidecar.GetStatus()
                });
            }
        });

        app.MapPost("/api/isolation/rescan", () =>
        {
            lock (stateLock)
            {
                var adapters = nicManager.Refresh(doc.Isolation.LockedPhysicalNicId);
                return Results.Ok(new
                {
                    adapters,
                    lockedAdapter = nicManager.GetLockedAdapter()
                });
            }
        });

        app.MapPut("/api/isolation", (IsolationUpdateRequest req) =>
        {
            lock (stateLock)
            {
                if (req.LockedPhysicalNicId != null) doc.Isolation.LockedPhysicalNicId = req.LockedPhysicalNicId;
                if (req.AutoBypassVirtualNics.HasValue) doc.Isolation.AutoBypassVirtualNics = req.AutoBypassVirtualNics.Value;
                if (req.TlsSniRecoveryEnabled.HasValue) doc.Isolation.TlsSniRecoveryEnabled = req.TlsSniRecoveryEnabled.Value;
                if (req.ForceRemoteDns.HasValue) doc.Isolation.ForceRemoteDns = req.ForceRemoteDns.Value;
                if (req.DirectPhysicalDohEnabled.HasValue) doc.Isolation.DirectPhysicalDohEnabled = req.DirectPhysicalDohEnabled.Value;
                if (!string.IsNullOrWhiteSpace(req.DohServerIp)) doc.Isolation.DohServerIp = req.DohServerIp.Trim();
                if (req.GlobalKillSwitch.HasValue) doc.Isolation.GlobalKillSwitch = req.GlobalKillSwitch.Value;
                if (req.EnableIpv6.HasValue) doc.Isolation.EnableIpv6 = req.EnableIpv6.Value;
                if (req.AutoHealthCheckEnabled.HasValue) doc.Isolation.AutoHealthCheckEnabled = req.AutoHealthCheckEnabled.Value;
                if (req.AutoHealthCheckMinutes.HasValue) doc.Isolation.AutoHealthCheckMinutes = Math.Clamp(req.AutoHealthCheckMinutes.Value, 1, 60);

                nicManager.Refresh(doc.Isolation.LockedPhysicalNicId);
                store.Save(doc);
                Interlocked.Increment(ref stateRevision);
                if (!isVerifyOnly)
                    proxyServer.UpdateTopology(BuildEffectiveSeats(doc), doc.Nodes, doc.Isolation);
                HubEventLogger.Default.Info("NIC", $"更新隔离策略（网卡={nicManager.GetLockedAdapter()?.Name ?? "未锁定"} · 自动巡检={(doc.Isolation.AutoHealthCheckEnabled ? $"{doc.Isolation.AutoHealthCheckMinutes}m" : "关闭")}）");
                return Results.Ok(new
                {
                    settings = doc.Isolation,
                    adapters = nicManager.GetAdapters(),
                    lockedAdapter = nicManager.GetLockedAdapter(),
                    sidecarEngine = sidecar.GetStatus()
                });
            }
        });

        // 4.5 L2 sing-box 懒加载扩展引擎管理 API
        app.MapGet("/api/engine/singbox", () => Results.Ok(sidecar.GetStatus()));

        app.MapPost("/api/engine/singbox/install", async (SingBoxInstallRequest? req, CancellationToken ct) =>
        {
            var (ok, message, status) = await sidecar.InstallLatestAsync(req?.ProxyUrl, ct);
            if (ok && !isVerifyOnly)
            {
                lock (stateLock) proxyServer.UpdateTopology(BuildEffectiveSeats(doc), doc.Nodes, doc.Isolation);
            }
            return Results.Ok(new { ok, message, status = sidecar.GetStatus() });
        });

        app.MapPut("/api/engine/singbox/path", (SingBoxPathRequest req) =>
        {
            lock (stateLock)
            {
                string? clean = string.IsNullOrWhiteSpace(req.BinaryPath) ? null : req.BinaryPath.Trim().Trim('"');
                if (clean != null && !File.Exists(clean))
                    return Results.BadRequest(new { message = $"文件不存在：{clean}" });

                doc.Isolation.CustomSingBoxPath = clean;
                sidecar.SetCustomBinaryPath(clean);
                store.Save(doc);
                if (!isVerifyOnly)
                    proxyServer.UpdateTopology(BuildEffectiveSeats(doc), doc.Nodes, doc.Isolation);
                return Results.Ok(new { ok = true, message = clean != null ? "已绑定自定义 sing-box 路径" : "已恢复默认路径探测", status = sidecar.GetStatus() });
            }
        });

        app.MapPost("/api/engine/singbox/stop", () =>
        {
            sidecar.Stop();
            return Results.Ok(new { ok = true, message = "已停止 sing-box 扩展引擎子进程", status = sidecar.GetStatus() });
        });

        app.MapPost("/api/engine/singbox/import", async (SingBoxImportRequest req, CancellationToken ct) =>
        {
            var (ok, message, status) = await sidecar.ImportOfflinePackageAsync(req.LocalPath, req.ExpectedSha256, ct);
            if (ok)
            {
                lock (stateLock)
                {
                    doc.Isolation.CustomSingBoxPath = null;
                    store.Save(doc);
                    if (!isVerifyOnly)
                        proxyServer.UpdateTopology(BuildEffectiveSeats(doc), doc.Nodes, doc.Isolation);
                }
            }
            return Results.Ok(new { ok, message, status = sidecar.GetStatus() });
        });

        // 4.8 桌面交互偏好与跨机器 WebDAV 加密同步
        app.MapGet("/api/preferences", () =>
        {
            lock (stateLock) return Results.Ok(doc.Preferences.ToRedactedCopy());
        });

        app.MapPut("/api/preferences", (PreferencesUpdateRequest req) =>
        {
            lock (stateLock)
            {
                var p = doc.Preferences;
                if (req.CloseAction is "minimize_to_tray" or "exit") p.CloseAction = req.CloseAction;
                if (req.TrayLeftClickAction is "toggle_flyout" or "show_main" or "toggle_mini") p.TrayLeftClickAction = req.TrayLeftClickAction;
                if (req.MiniCardEnabled.HasValue) p.MiniCardEnabled = req.MiniCardEnabled.Value;
                if (req.MiniCardX.HasValue) p.MiniCardX = req.MiniCardX.Value;
                if (req.MiniCardY.HasValue) p.MiniCardY = req.MiniCardY.Value;
                if (req.WebDavUrl != null) p.WebDavUrl = string.IsNullOrWhiteSpace(req.WebDavUrl) ? null : req.WebDavUrl.Trim();
                if (req.WebDavUsername != null) p.WebDavUsername = string.IsNullOrWhiteSpace(req.WebDavUsername) ? null : req.WebDavUsername.Trim();
                if (req.WebDavPassword != null && req.WebDavPassword != "••••••••")
                {
                    p.WebDavPassword = string.IsNullOrEmpty(req.WebDavPassword) ? null : req.WebDavPassword;
                    p.HasWebDavPassword = !string.IsNullOrEmpty(p.WebDavPassword);
                }
                if (req.WebDavSyncPassphrase != null && req.WebDavSyncPassphrase != "••••••••")
                {
                    p.WebDavSyncPassphrase = string.IsNullOrEmpty(req.WebDavSyncPassphrase) ? null : req.WebDavSyncPassphrase;
                    p.HasSyncPassphrase = !string.IsNullOrEmpty(p.WebDavSyncPassphrase);
                }
                store.Save(doc);
                return Results.Ok(p.ToRedactedCopy());
            }
        });

        app.MapPost("/api/webdav/test", async (WebDavActionRequest req, CancellationToken ct) =>
        {
            string? url;
            string? user;
            string? pass;
            lock (stateLock)
            {
                var p = doc.Preferences;
                url = !string.IsNullOrWhiteSpace(req.WebDavUrl) ? req.WebDavUrl.Trim() : p.WebDavUrl;
                user = req.WebDavUsername != null ? req.WebDavUsername : p.WebDavUsername;
                pass = req.WebDavPassword != null && req.WebDavPassword != "••••••••" ? req.WebDavPassword : p.WebDavPassword;
            }
            if (string.IsNullOrWhiteSpace(url))
                return Results.Ok(new { ok = false, message = "请先填写 WebDAV 地址" });

            var (ok, message) = await webDavSync.TestConnectionAsync(url, user, pass, ct);
            return Results.Ok(new { ok, message });
        });

        app.MapPost("/api/webdav/push", async (WebDavActionRequest req, CancellationToken ct) =>
        {
            HubStateDocument snapshot;
            string? url;
            string? user;
            string? pass;
            string? passphrase;
            lock (stateLock)
            {
                var p = doc.Preferences;
                url = !string.IsNullOrWhiteSpace(req.WebDavUrl) ? req.WebDavUrl.Trim() : p.WebDavUrl;
                user = req.WebDavUsername != null ? req.WebDavUsername : p.WebDavUsername;
                pass = req.WebDavPassword != null && req.WebDavPassword != "••••••••" ? req.WebDavPassword : p.WebDavPassword;
                passphrase = req.Passphrase != null && req.Passphrase != "••••••••" ? req.Passphrase : p.WebDavSyncPassphrase;
                snapshot = doc;
            }
            if (string.IsNullOrWhiteSpace(url))
                return Results.Ok(new { ok = false, message = "请先填写 WebDAV 地址" });
            if (string.IsNullOrWhiteSpace(passphrase))
                return Results.Ok(new { ok = false, message = "请设置加密同步口令（至少 4 位）" });

            var (ok, message, syncedAt) = await webDavSync.PushBackupAsync(snapshot, url, user, pass, passphrase, ct);
            if (ok && syncedAt.HasValue)
            {
                lock (stateLock)
                {
                    doc.Preferences.LastWebDavSyncAt = syncedAt;
                    store.Save(doc);
                }
            }
            return Results.Ok(new { ok, message, lastWebDavSyncAt = syncedAt });
        });

        app.MapPost("/api/webdav/pull", async (WebDavActionRequest req, CancellationToken ct) =>
        {
            string? url;
            string? user;
            string? pass;
            string? passphrase;
            lock (stateLock)
            {
                var p = doc.Preferences;
                url = !string.IsNullOrWhiteSpace(req.WebDavUrl) ? req.WebDavUrl.Trim() : p.WebDavUrl;
                user = req.WebDavUsername != null ? req.WebDavUsername : p.WebDavUsername;
                pass = req.WebDavPassword != null && req.WebDavPassword != "••••••••" ? req.WebDavPassword : p.WebDavPassword;
                passphrase = req.Passphrase != null && req.Passphrase != "••••••••" ? req.Passphrase : p.WebDavSyncPassphrase;
            }
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(passphrase))
                return Results.Ok(new { ok = false, message = "请先填写 WebDAV 地址与同步解密口令" });

            var (ok, message, restored) = await webDavSync.PullBackupAsync(url, user, pass, passphrase, ct);
            if (!ok || restored == null)
                return Results.Ok(new { ok = false, message });

            lock (stateLock)
            {
                doc.Isolation = restored.Isolation ?? doc.Isolation;
                doc.Nodes = restored.Nodes ?? doc.Nodes;
                doc.Seats = restored.Seats ?? doc.Seats;
                doc.Instances = restored.Instances ?? new();
                doc.Subscriptions = restored.Subscriptions ?? doc.Subscriptions;
                doc.CustomPricings = restored.CustomPricings ?? doc.CustomPricings;
                doc.Preferences.LastWebDavSyncAt = DateTimeOffset.UtcNow;
                pricingCatalog.SetCustomRules(doc.CustomPricings);
                usageService.RecalculatePricing();
                store.Save(doc);
                if (!isVerifyOnly)
                    proxyServer.UpdateTopology(BuildEffectiveSeats(doc), doc.Nodes, doc.Isolation);
            }
            return Results.Ok(new { ok = true, message, lastWebDavSyncAt = doc.Preferences.LastWebDavSyncAt });
        });

        // 5. CC Switch 级双源统计大盘、按需同步与模型定价 API
        app.MapGet("/api/stats/dashboard", (string? range, string? appFilter, string? modelFilter, int? page, int? pageSize) =>
        {
            var traffic = proxyServer.GetTrafficSnapshots();
            var data = usageService.QueryDashboard(
                string.IsNullOrWhiteSpace(range) ? "today" : range,
                appFilter,
                modelFilter,
                page ?? 1,
                pageSize ?? 20,
                traffic);
            return Results.Ok(data);
        });

        app.MapGet("/api/stats/conversations/{conversationId}/audit", (string conversationId) =>
        {
            string decoded = Uri.UnescapeDataString(conversationId);
            var report = usageService.GetConversationAudit(decoded);
            return report != null
                ? Results.Ok(report)
                : Results.NotFound(new { message = $"未找到会话审计记录: {decoded}" });
        });

        app.MapPost("/api/stats/sync", () =>
        {
            var syncResult = usageService.SyncNow();
            return Results.Ok(syncResult);
        });

        app.MapGet("/api/stats/pricing", () => Results.Ok(pricingCatalog.GetAllRules()));

        app.MapPut("/api/stats/pricing", (PricingUpsertRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Pattern))
                return Results.BadRequest(new { message = "模型匹配前缀不能为空" });

            lock (stateLock)
            {
                doc.CustomPricings.RemoveAll(r => r.Pattern.Equals(req.Pattern.Trim(), StringComparison.OrdinalIgnoreCase));
                doc.CustomPricings.Insert(0, new ModelPricingRule
                {
                    Pattern = req.Pattern.Trim(),
                    DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? req.Pattern.Trim() : req.DisplayName.Trim(),
                    InputPerMillion = Math.Max(0, req.InputPerMillion),
                    OutputPerMillion = Math.Max(0, req.OutputPerMillion),
                    CacheReadPerMillion = Math.Max(0, req.CacheReadPerMillion),
                    CacheWritePerMillion = Math.Max(0, req.CacheWritePerMillion),
                    IsCustom = true
                });
                pricingCatalog.SetCustomRules(doc.CustomPricings);
                usageService.RecalculatePricing();
                store.Save(doc);
            }

            return Results.Ok(pricingCatalog.GetAllRules());
        });

        // 6. 脱敏运行日志、版本更新检查与安全打开固定 GitHub 项目仓库
        app.MapGet("/api/diagnostics/logs", (string? level, string? keyword, int? limit) =>
        {
            return Results.Ok(HubEventLogger.Default.Query(level, keyword, limit ?? 200));
        });

        app.MapDelete("/api/diagnostics/logs", () =>
        {
            HubEventLogger.Default.ClearMemory();
            return Results.Ok(new { cleared = true });
        });

        DateTimeOffset updateCacheAt = DateTimeOffset.MinValue;
        object? cachedUpdatePayload = null;
        var updateCacheLock = new object();

        app.MapGet("/api/system/update-check", async (string? proxyUrl, bool? force, CancellationToken ct) =>
        {
            lock (updateCacheLock)
            {
                if (force != true && cachedUpdatePayload != null && (DateTimeOffset.UtcNow - updateCacheAt).TotalMinutes < 15)
                {
                    return Results.Ok(cachedUpdatePayload);
                }
            }

            string current = ProjectMetadata.Version;
            try
            {
                using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) };
                if (!string.IsNullOrWhiteSpace(proxyUrl))
                {
                    handler.Proxy = new System.Net.WebProxy(proxyUrl.Trim());
                    handler.UseProxy = true;
                }
                else
                {
                    handler.UseProxy = false;
                }

                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(7) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"AgentProxyHub/{current}");
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

                using var resp = await http.GetAsync($"https://api.github.com/repos/{ProjectMetadata.RepoSlug}/releases/latest", ct);
                resp.EnsureSuccessStatusCode();
                string json = await resp.Content.ReadAsStringAsync(ct);
                using var jdoc = JsonDocument.Parse(json);
                var root = jdoc.RootElement;
                string tag = root.TryGetProperty("tag_name", out var tEl) ? (tEl.GetString() ?? current) : current;
                string cleanLatest = tag.TrimStart('v', 'V');
                string? htmlUrl = root.TryGetProperty("html_url", out var uEl) ? uEl.GetString() : ProjectMetadata.RepoUrl + "/releases";
                string? publishedAt = root.TryGetProperty("published_at", out var pEl) ? pEl.GetString() : null;
                string? body = root.TryGetProperty("body", out var bEl) ? bEl.GetString() : string.Empty;

                bool hasUpdate = Version.TryParse(cleanLatest, out var latestVer) &&
                                 Version.TryParse(current, out var curVer) &&
                                 latestVer > curVer;

                var payload = new
                {
                    ok = true,
                    currentVersion = current,
                    latestVersion = cleanLatest,
                    hasUpdate,
                    publishedAt,
                    htmlUrl,
                    releaseNotes = body
                };
                lock (updateCacheLock)
                {
                    cachedUpdatePayload = payload;
                    updateCacheAt = DateTimeOffset.UtcNow;
                }
                return Results.Ok(payload);
            }
            catch (Exception ex)
            {
                return Results.Ok(new
                {
                    ok = false,
                    currentVersion = current,
                    latestVersion = current,
                    hasUpdate = false,
                    htmlUrl = ProjectMetadata.RepoUrl + "/releases",
                    releaseNotes = string.Empty,
                    error = ex.Message
                });
            }
        });

        app.MapPost("/api/system/open-repo", (string? targetUrl) =>
        {
            string urlToOpen = ProjectMetadata.RepoUrl;
            if (!string.IsNullOrWhiteSpace(targetUrl) &&
                targetUrl.StartsWith(ProjectMetadata.RepoUrl, StringComparison.OrdinalIgnoreCase))
            {
                urlToOpen = targetUrl;
            }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = urlToOpen,
                    UseShellExecute = true
                });
                return Results.Ok(new { opened = true, url = urlToOpen });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { opened = false, url = urlToOpen, error = ex.Message });
            }
        });

        // 7. 本地配置存储体积统计与失效接管备份/临时文件安全清理
        app.MapGet("/api/maintenance/stats", () => Results.Ok(maintenanceService.GetStorageStats()));

        app.MapPost("/api/maintenance/cleanup", () =>
        {
            lock (stateLock)
            {
                takeoverService.InvalidateCache();
                var takeovers = takeoverService.InspectAll(doc.Seats);
                var activeIds = takeovers.Where(t => t.IsTakenOver).Select(t => t.SeatId).ToList();
                var res = maintenanceService.CleanupRedundantFiles(activeIds);
                HubEventLogger.Default.Info("MAINT", res.Message);
                return Results.Ok(res);
            }
        });

        return app;
    }

    private static int? ParseVerifyPort(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("--verify-port=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(a["--verify-port=".Length..], out int eqPort) &&
                eqPort is >= 1024 and <= 65535)
            {
                return eqPort;
            }

            if (a.Equals("--verify-port", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length &&
                int.TryParse(args[i + 1], out int nextPort) &&
                nextPort is >= 1024 and <= 65535)
            {
                return nextPort;
            }
        }
        return null;
    }

    private static string ResolveWwwRoot(string? customWwwRoot)
    {
        if (!string.IsNullOrWhiteSpace(customWwwRoot) && Directory.Exists(customWwwRoot))
            return customWwwRoot;

        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AgentProxyHub.Host", "wwwroot")),
            @"f:\source\Agent_Proxy_Hub\src\AgentProxyHub.Host\wwwroot"
        ];

        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }
}
