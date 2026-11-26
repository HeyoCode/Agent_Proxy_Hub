using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Text;
using AgentProxyHub.Core.Models;

namespace AgentProxyHub.Core.Networking;

/// <summary>
/// 节点物理直连验真器：
/// - 并行测试落地出口 IP 与 Claude / ChatGPT / Antigravity TLS 单次 RTT（消除串行超时叠加虚高）
/// - 支持经由本地席位监听端口 (127.0.0.1:localPort) 发起真实流量全链路验真
/// </summary>
public sealed class NodeHealthChecker
{
    private readonly UpstreamConnector _connector;

    public NodeHealthChecker(UpstreamConnector connector)
    {
        _connector = connector;
    }

    public async Task<ProxyNode> VerifyNodeAsync(ProxyNode node, IsolationSettings settings, CancellationToken ct = default)
    {
        if (!settings.EnableIpv6 && UpstreamConnector.IsIpv6Literal(node.Host))
        {
            node.Status = "offline";
            node.LatencyMs = null;
            node.ExitIp = "—";
            node.IspSummary = "IPv6 开关已关闭，请开启后验真";
            node.AiReachability = new List<string> { "IPv6 已禁用" };
            node.LastCheckedAt = DateTimeOffset.UtcNow;
            return node;
        }

        if (node.RequiresSidecarEngine && _connector.Sidecar != null)
        {
            var status = _connector.Sidecar.GetStatus();
            if (!status.Installed)
            {
                node.Status = "offline";
                node.LatencyMs = null;
                node.ExitIp = "—";
                node.IspSummary = "需先在「网卡隔离」页一键安装 sing-box 扩展内核";
                node.AiReachability = new List<string> { "缺少扩展内核" };
                node.LastCheckedAt = DateTimeOffset.UtcNow;
                return node;
            }
        }

        try
        {
            var exitTask = ProbeExitIpViaNodeAsync(node, settings, ct);
            var claudeTask = ProbeTlsEndpointAsync(node, "api.anthropic.com", 443, settings, ct);
            var gptTask = ProbeTlsEndpointAsync(node, "chatgpt.com", 443, settings, ct);
            var agyTask = ProbeTlsEndpointAsync(node, "generativelanguage.googleapis.com", 443, settings, ct);

            await Task.WhenAll(exitTask, claudeTask, gptTask, agyTask);

            var exitRes = exitTask.Result;
            var claudeRes = claudeTask.Result;
            var gptRes = gptTask.Result;
            var agyRes = agyTask.Result;

            var aiChecks = new List<string>
            {
                claudeRes.Ok ? $"Claude {claudeRes.ElapsedMs}ms" : "Claude ✕",
                gptRes.Ok ? $"ChatGPT {gptRes.ElapsedMs}ms" : "ChatGPT ✕",
                agyRes.Ok ? $"AGY {agyRes.ElapsedMs}ms" : "AGY ✕"
            };

            List<int> latencies = new();
            if (claudeRes.Ok) latencies.Add(claudeRes.ElapsedMs);
            if (gptRes.Ok) latencies.Add(gptRes.ElapsedMs);
            if (agyRes.Ok) latencies.Add(agyRes.ElapsedMs);
            if (exitRes.Ok && exitRes.ElapsedMs > 0) latencies.Add(exitRes.ElapsedMs);

            bool anyOk = claudeRes.Ok || gptRes.Ok || agyRes.Ok || !string.IsNullOrEmpty(exitRes.Ip);
            node.Status = anyOk ? "online" : "offline";
            node.LatencyMs = latencies.Count > 0 ? Math.Max(1, latencies.Min()) : null;
            node.ExitIp = exitRes.Ip ?? (anyOk ? node.Host : "—");
            node.IspSummary = BuildSummary(anyOk, exitRes.Loc, node);
            node.AiReachability = anyOk ? aiChecks : new List<string> { "连接超时" };
            node.LastCheckedAt = DateTimeOffset.UtcNow;
        }
        catch
        {
            node.Status = "offline";
            node.LatencyMs = null;
            node.ExitIp = "—";
            node.IspSummary = BuildSummary(false, null, node);
            node.AiReachability = new List<string> { "连接超时" };
            node.LastCheckedAt = DateTimeOffset.UtcNow;
        }

        return node;
    }

    /// <summary>
    /// 通过本机席位监听端口 (127.0.0.1:localPort) 发起真实 HTTP/HTTPS 代理链路探测，
    /// 真实验证「应用 -> 本地席位端口 -> MixedProxyServer -> 上游节点/物理网卡 -> AI 目标」完整通道并刷新吞吐字节。
    /// </summary>
    public async Task<TunnelVerificationResult> VerifyViaLocalSeatAsync(
        string seatId,
        int localPort,
        ProxyNode? boundNode,
        IsolationSettings settings,
        IReadOnlyList<(string Label, string Url)>? customProbeUrls = null,
        CancellationToken ct = default)
    {
        if (boundNode != null && !settings.EnableIpv6 && UpstreamConnector.IsIpv6Literal(boundNode.Host))
        {
            return new TunnelVerificationResult
            {
                Ok = false,
                SeatId = seatId,
                LocalPort = localPort,
                Message = "IPv6 开关已关闭，当前席位绑定的 IPv6 节点无法建立隧道"
            };
        }

        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{localPort}"),
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.Zero,
            AutomaticDecompression = DecompressionMethods.None
        };

        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(6)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentProxyHub/0.3.4");

        string? exitIp = null;
        string? loc = null;
        int? exitLatency = null;
        var aiChecks = new List<string>();
        var latencies = new List<int>();

        if (customProbeUrls is { Count: > 0 })
        {
            foreach (var item in customProbeUrls)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    using var resp = await http.GetAsync(item.Url, ct);
                    string body = await resp.Content.ReadAsStringAsync(ct);
                    sw.Stop();
                    int ms = Math.Max(1, (int)sw.ElapsedMilliseconds);
                    if (resp.IsSuccessStatusCode)
                    {
                        latencies.Add(ms);
                        aiChecks.Add($"{item.Label} {ms}ms");
                        if (string.IsNullOrEmpty(exitIp) && body.Contains("ip=", StringComparison.Ordinal))
                        {
                            foreach (string line in body.Split('\n'))
                            {
                                if (line.StartsWith("ip=", StringComparison.Ordinal)) exitIp = line[3..].Trim();
                                else if (line.StartsWith("loc=", StringComparison.Ordinal)) loc = line[4..].Trim();
                            }
                        }
                    }
                    else
                    {
                        aiChecks.Add($"{item.Label} HTTP {(int)resp.StatusCode}");
                    }
                }
                catch
                {
                    aiChecks.Add($"{item.Label} ✕");
                }
            }
        }
        else
        {
            var exitTask = ProbeExitIpViaLocalProxyAsync(http, ct);
            var claudeTask = ProbeHttpHeadViaLocalProxyAsync(http, "Claude", "https://api.anthropic.com/", ct);
            var gptTask = ProbeHttpHeadViaLocalProxyAsync(http, "ChatGPT", "https://chatgpt.com/", ct);
            var agyTask = ProbeHttpHeadViaLocalProxyAsync(http, "AGY", "https://generativelanguage.googleapis.com/", ct);

            await Task.WhenAll(exitTask, claudeTask, gptTask, agyTask);

            (exitIp, loc, exitLatency) = exitTask.Result;
            if (exitLatency.HasValue) latencies.Add(exitLatency.Value);

            foreach (var res in new[] { claudeTask.Result, gptTask.Result, agyTask.Result })
            {
                if (res.Ok)
                {
                    latencies.Add(res.ElapsedMs);
                    aiChecks.Add($"{res.Label} {res.ElapsedMs}ms");
                }
                else
                {
                    aiChecks.Add($"{res.Label} ✕");
                }
            }
        }

        bool anyOk = latencies.Count > 0 || !string.IsNullOrEmpty(exitIp);
        int? bestMs = latencies.Count > 0 ? Math.Max(1, latencies.Min()) : exitLatency;
        string finalExitIp = exitIp
            ?? (boundNode?.Protocol == ProxyProtocol.DIRECT_PHYSICAL ? boundNode.ExitIp : (anyOk && boundNode != null ? boundNode.Host : "—"));

        if (boundNode != null)
        {
            boundNode.Status = anyOk ? "online" : "offline";
            boundNode.LatencyMs = bestMs;
            if (boundNode.Protocol != ProxyProtocol.DIRECT_PHYSICAL)
            {
                boundNode.ExitIp = finalExitIp;
                boundNode.IspSummary = BuildSummary(anyOk, loc, boundNode);
            }
            else if (!string.IsNullOrEmpty(exitIp))
            {
                boundNode.ExitIp = exitIp;
                boundNode.IspSummary = !string.IsNullOrEmpty(loc) ? $"物理直出 · {loc}" : "本机物理出口直绑（未经代理节点）";
            }
            boundNode.AiReachability = anyOk ? aiChecks : new List<string> { "隧道拦截或超时" };
            boundNode.LastCheckedAt = DateTimeOffset.UtcNow;
        }

        return new TunnelVerificationResult
        {
            Ok = anyOk,
            SeatId = seatId,
            LocalPort = localPort,
            ExitIp = finalExitIp,
            LatencyMs = bestMs,
            AiReachability = aiChecks,
            Node = boundNode,
            Message = anyOk
                ? $"全链路验真通过：{finalExitIp} ({bestMs ?? 1} ms · 流量已真实穿过 :{localPort})"
                : $"端口 :{localPort} 全链路验真失败，{BuildSummary(false, null, boundNode)}"
        };
    }

    private async Task<(bool Ok, string? Ip, string? Loc, int ElapsedMs)> ProbeExitIpViaNodeAsync(
        ProxyNode node,
        IsolationSettings settings,
        CancellationToken ct)
    {
        // 主探测：Cloudflare trace (3.5s 超时)
        var cf = await ProbeCloudflareTraceAsync(node, settings, ct);
        if (!string.IsNullOrEmpty(cf.Ip))
            return (true, cf.Ip, cf.Loc, cf.ElapsedMs);

        // 兜底：部分海外出口或 CDN 会屏蔽 1.1.1.1，回退轻量明文 IP 查询域名
        var ipify = await ProbeIpifyAsync(node, settings, ct);
        if (!string.IsNullOrEmpty(ipify.Ip))
            return (true, ipify.Ip, null, ipify.ElapsedMs);

        return (false, null, null, 0);
    }

    private async Task<(string? Ip, string? Loc, int ElapsedMs)> ProbeCloudflareTraceAsync(
        ProxyNode node,
        IsolationSettings settings,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(3500));
        try
        {
            using var stream = await _connector.ConnectViaNodeAsync(node, "1.1.1.1", 443, settings, cts.Token);
            using var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "cloudflare.com" }, cts.Token);

            byte[] req = "GET /cdn-cgi/trace HTTP/1.1\r\nHost: 1.1.1.1\r\nConnection: close\r\nUser-Agent: AgentProxyHub/0.3.4\r\n\r\n"u8.ToArray();
            await ssl.WriteAsync(req, cts.Token);
            await ssl.FlushAsync(cts.Token);

            using var reader = new StreamReader(ssl, Encoding.UTF8);
            string body = await reader.ReadToEndAsync(cts.Token);
            sw.Stop();

            string? ip = null;
            string? loc = null;
            foreach (string line in body.Split('\n'))
            {
                if (line.StartsWith("ip=", StringComparison.Ordinal)) ip = line[3..].Trim();
                else if (line.StartsWith("loc=", StringComparison.Ordinal)) loc = line[4..].Trim();
            }
            return (ip, loc, Math.Max(1, (int)sw.ElapsedMilliseconds));
        }
        catch
        {
            return (null, null, 0);
        }
    }

    private async Task<(string? Ip, int ElapsedMs)> ProbeIpifyAsync(
        ProxyNode node,
        IsolationSettings settings,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(3500));
        try
        {
            using var stream = await _connector.ConnectViaNodeAsync(node, "api.ipify.org", 443, settings, cts.Token);
            using var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "api.ipify.org" }, cts.Token);

            byte[] req = "GET / HTTP/1.1\r\nHost: api.ipify.org\r\nConnection: close\r\nUser-Agent: AgentProxyHub/0.3.4\r\n\r\n"u8.ToArray();
            await ssl.WriteAsync(req, cts.Token);
            await ssl.FlushAsync(cts.Token);

            using var reader = new StreamReader(ssl, Encoding.UTF8);
            string body = await reader.ReadToEndAsync(cts.Token);
            sw.Stop();

            int split = body.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            string? ip = split >= 0 ? body[(split + 4)..].Trim() : null;
            if (!IPAddress.TryParse(ip, out _)) ip = null;
            return (ip, Math.Max(1, (int)sw.ElapsedMilliseconds));
        }
        catch
        {
            return (null, 0);
        }
    }

    private async Task<(bool Ok, int ElapsedMs)> ProbeTlsEndpointAsync(
        ProxyNode node,
        string host,
        int port,
        IsolationSettings settings,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(4500));
        try
        {
            using var stream = await _connector.ConnectViaNodeAsync(node, host, port, settings, cts.Token);
            using var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cts.Token);
            sw.Stop();
            return (true, Math.Max(1, (int)sw.ElapsedMilliseconds));
        }
        catch
        {
            return (false, 0);
        }
    }

    private static async Task<(string? Ip, string? Loc, int? LatencyMs)> ProbeExitIpViaLocalProxyAsync(HttpClient http, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(4000));
            using var resp = await http.GetAsync("https://cloudflare.com/cdn-cgi/trace", cts.Token);
            string body = await resp.Content.ReadAsStringAsync(cts.Token);
            sw.Stop();

            string? ip = null;
            string? loc = null;
            foreach (string line in body.Split('\n'))
            {
                if (line.StartsWith("ip=", StringComparison.Ordinal)) ip = line[3..].Trim();
                else if (line.StartsWith("loc=", StringComparison.Ordinal)) loc = line[4..].Trim();
            }
            if (!string.IsNullOrEmpty(ip))
                return (ip, loc, Math.Max(1, (int)sw.ElapsedMilliseconds));
        }
        catch { }

        var sw2 = Stopwatch.StartNew();
        try
        {
            using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(TimeSpan.FromMilliseconds(4000));
            string raw = (await http.GetStringAsync("https://api.ipify.org", cts2.Token)).Trim();
            sw2.Stop();
            if (IPAddress.TryParse(raw, out _))
                return (raw, null, Math.Max(1, (int)sw2.ElapsedMilliseconds));
        }
        catch { }

        return (null, null, null);
    }

    private static async Task<(string Label, bool Ok, int ElapsedMs)> ProbeHttpHeadViaLocalProxyAsync(
        HttpClient http,
        string label,
        string url,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(4500));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();
            return (label, true, Math.Max(1, (int)sw.ElapsedMilliseconds));
        }
        catch
        {
            return (label, false, 0);
        }
    }

    private static string BuildSummary(bool anyOk, string? loc, ProxyNode? node)
    {
        if (anyOk)
        {
            return !string.IsNullOrEmpty(loc)
                ? $"出口地区 {loc} · 物理直出已验证"
                : "物理直出连通";
        }

        if (node != null && node.Protocol == ProxyProtocol.SOCKS5 && node.Port is 80 or 8080 or 3128)
        {
            return $"连接失败（端口 :{node.Port} 若为 HTTP 代理请改选 HTTP 协议）";
        }

        return "连接失败 / 超时";
    }
}
