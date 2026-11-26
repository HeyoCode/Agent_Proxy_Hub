using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentProxyHub.Core.Apps;
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
        bool AllowInsecure = false);

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
        bool? AllowSameGroupFailover);

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
        bool? EnableIpv6);

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

        // 支持仅用于截图/UI验收的隔离端口模式，避免与已运行实例的端口冲突
        int? verifyPort = ParseVerifyPort(args);
        bool isVerifyOnly = verifyPort.HasValue;

        if (!isVerifyOnly)
        {
            // 首次启动即异步预扫本地会话日志并拉起全部已启用席位端口
            proxyServer.UpdateTopology(doc.Seats, doc.Nodes, doc.Isolation);
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

        var app = builder.Build();
        var stateLock = new object();

        // 后台每 5 秒自动增量同步一次本地 AI 会话日志，新产生对话 5 秒内自动呈现在大盘中
        var stoppingToken = app.Lifetime.ApplicationStopping;
        stoppingToken.Register(() =>
        {
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

        // 1. 总览与实时隔离状态
        app.MapGet("/api/overview", () =>
        {
            lock (stateLock)
            {
                var lockedNic = nicManager.GetLockedAdapter();
                var traffic = proxyServer.GetTrafficSnapshots();
                var adapters = nicManager.GetAdapters();
                var excludedVirtuals = adapters.Where(a => a.IsVirtualOrTunnel && a.Status == "Up").ToList();
                var cliSnippets = AppSeatManager.BuildCliSnippets(
                    doc.Seats.FirstOrDefault(s => s.Id == "claude"),
                    doc.Seats.FirstOrDefault(s => s.Id == "agy"));
                var seatTakeovers = takeoverService.InspectAll(doc.Seats);

                return Results.Ok(new
                {
                    project = ProjectMetadata.ToDto(),
                    os = OperatingSystem.IsMacOS() ? "mac" : "win",
                    arch = RuntimeInformation.OSArchitecture.ToString(),
                    memoryMb = Math.Round(Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024.0, 1),
                    lockedNic,
                    hasDetectedTunnel = nicManager.HasDetectedClashOrTunnel,
                    excludedVirtualNics = excludedVirtuals,
                    isolation = doc.Isolation,
                    seats = doc.Seats,
                    nodes = doc.Nodes,
                    trafficSnapshots = traffic,
                    seatTakeovers,
                    sidecarEngine = sidecar.GetStatus(),
                    cliSnippets
                });
            }
        });

        // 2. 线路池：查询、手动添加、批量预览、批量导入、删除、验真
        app.MapGet("/api/nodes", () =>
        {
            lock (stateLock) return Results.Ok(doc.Nodes);
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
                Status = "unverified",
                ExitIp = "—",
                IspSummary = "手动添加 · 待物理直连验真"
            };

            lock (stateLock)
            {
                doc.Nodes.Insert(0, node);
                store.Save(doc);
                if (!isVerifyOnly)
                    proxyServer.UpdateTopology(doc.Seats, doc.Nodes, doc.Isolation);
            }
            return Results.Ok(node);
        });

        app.MapPost("/api/nodes/batch-preview", (BatchPreviewRequest req) =>
        {
            lock (stateLock)
            {
                var parsed = ProxyNodeImporter.Parse(req.RawText ?? string.Empty, req.DefaultProtocol, req.DefaultGroup, doc.Nodes);
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
                        proxyServer.UpdateTopology(doc.Seats, doc.Nodes, doc.Isolation);
                }
            }
            return Results.Ok(new
            {
                addedCount = added.Count,
                duplicateCount = parsed.DuplicateCount,
                errorCount = parsed.ErrorCount,
                addedNodes = added
            });
        });

        app.MapDelete("/api/nodes/{id}", (string id) =>
        {
            lock (stateLock)
            {
                int removed = doc.Nodes.RemoveAll(n => n.Id == id);
                if (removed == 0) return Results.NotFound();
                foreach (var seat in doc.Seats.Where(s => s.BoundNodeId == id))
                {
                    seat.BoundNodeId = doc.Nodes.FirstOrDefault()?.Id;
                }
                store.Save(doc);
                if (!isVerifyOnly)
                    proxyServer.UpdateTopology(doc.Seats, doc.Nodes, doc.Isolation);
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
                return Results.Ok(target);
            }

            await healthChecker.VerifyNodeAsync(target, iso, ct);
            lock (stateLock) store.Save(doc);
            return Results.Ok(target);
        });

        app.MapPost("/api/nodes/verify-all", async (CancellationToken ct) =>
        {
            List<ProxyNode> targets;
            IsolationSettings iso;
            lock (stateLock)
            {
                targets = doc.Nodes.Where(n => n.Protocol != ProxyProtocol.DIRECT_PHYSICAL).ToList();
                iso = doc.Isolation;
            }
            await Task.WhenAll(targets.Select(n => healthChecker.VerifyNodeAsync(n, iso, ct)));
            lock (stateLock) store.Save(doc);
            return Results.Ok(doc.Nodes);
        });

        // 3. AI 软件席位管理与受控环境启动器
        app.MapPut("/api/seats/{id}", (string id, SeatUpdateRequest req) =>
        {
            lock (stateLock)
            {
                var seat = doc.Seats.FirstOrDefault(s => s.Id == id);
                if (seat == null) return Results.NotFound();

                bool portChanged = req.LocalPort is >= 1024 and <= 65535 && req.LocalPort.Value != seat.LocalPort;
                if (req.Enabled.HasValue) seat.Enabled = req.Enabled.Value;
                if (req.BoundNodeId != null) seat.BoundNodeId = req.BoundNodeId;
                if (req.RelayNodeId != null) seat.RelayNodeId = string.IsNullOrWhiteSpace(req.RelayNodeId) ? null : req.RelayNodeId;
                if (req.LocalPort is >= 1024 and <= 65535) seat.LocalPort = req.LocalPort.Value;
                if (req.KillSwitchEnabled.HasValue) seat.KillSwitchEnabled = req.KillSwitchEnabled.Value;
                if (req.AllowSameGroupFailover.HasValue) seat.AllowSameGroupFailover = req.AllowSameGroupFailover.Value;

                store.Save(doc);
                if (!isVerifyOnly)
                {
                    proxyServer.UpdateTopology(doc.Seats, doc.Nodes, doc.Isolation);
                    if (portChanged && takeoverService.InspectSeat(seat).IsTakenOver)
                    {
                        takeoverService.SetTakeover(seat, true);
                    }
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
                    proxyServer.UpdateTopology(doc.Seats, doc.Nodes, doc.Isolation);
                }

                var (ok, message, status) = takeoverService.SetTakeover(seat, req.Enabled);
                return Results.Ok(new
                {
                    ok,
                    message,
                    status,
                    seatTakeovers = takeoverService.InspectAll(doc.Seats)
                });
            }
        });

        app.MapPost("/api/seats/{id}/launch", (string id) =>
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

            lock (stateLock)
            {
                var seat = doc.Seats.FirstOrDefault(s => s.Id == id);
                if (seat == null) return Results.NotFound();

                if (!seat.Enabled)
                {
                    seat.Enabled = true;
                    store.Save(doc);
                    proxyServer.UpdateTopology(doc.Seats, doc.Nodes, doc.Isolation);
                }

                var (ok, message) = takeoverService.LaunchWithProxy(seat);
                return Results.Ok(new
                {
                    ok,
                    message,
                    seatTakeovers = takeoverService.InspectAll(doc.Seats)
                });
            }
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
                    proxyServer.UpdateTopology(doc.Seats, doc.Nodes, doc.Isolation);
                }
                boundNode = doc.Nodes.FirstOrDefault(n => n.Id == seat.BoundNodeId);
                iso = doc.Isolation;
            }

            if (isVerifyOnly)
            {
                if (boundNode != null && boundNode.Protocol != ProxyProtocol.DIRECT_PHYSICAL)
                    await healthChecker.VerifyNodeAsync(boundNode, iso, ct);
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

            var res = await healthChecker.VerifyViaLocalSeatAsync(seat.Id, seat.LocalPort, boundNode, iso, null, ct);
            var snap = proxyServer.GetTrafficSnapshots().FirstOrDefault(s => s.SeatId == seat.Id);
            res.BytesTx = snap?.BytesTx ?? 0;
            res.BytesRx = snap?.BytesRx ?? 0;

            lock (stateLock)
            {
                store.Save(doc);
            }

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
                if (!isVerifyOnly)
                    proxyServer.UpdateTopology(doc.Seats, doc.Nodes, doc.Isolation);
                return Results.Ok(doc.Seats);
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

                nicManager.Refresh(doc.Isolation.LockedPhysicalNicId);
                store.Save(doc);
                if (!isVerifyOnly)
                    proxyServer.UpdateTopology(doc.Seats, doc.Nodes, doc.Isolation);
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
                lock (stateLock) proxyServer.UpdateTopology(doc.Seats, doc.Nodes, doc.Isolation);
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
                    proxyServer.UpdateTopology(doc.Seats, doc.Nodes, doc.Isolation);
                return Results.Ok(new { ok = true, message = clean != null ? "已绑定自定义 sing-box 路径" : "已恢复默认路径探测", status = sidecar.GetStatus() });
            }
        });

        app.MapPost("/api/engine/singbox/stop", () =>
        {
            sidecar.Stop();
            return Results.Ok(new { ok = true, message = "已停止 sing-box 扩展引擎子进程", status = sidecar.GetStatus() });
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

        // 6. 在系统默认浏览器中安全打开固定 GitHub 项目仓库
        app.MapPost("/api/system/open-repo", () =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = ProjectMetadata.RepoUrl,
                    UseShellExecute = true
                });
                return Results.Ok(new { opened = true, url = ProjectMetadata.RepoUrl });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { opened = false, url = ProjectMetadata.RepoUrl, error = ex.Message });
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
