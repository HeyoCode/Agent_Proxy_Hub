using System.Diagnostics;
using System.Net.Security;
using System.Text;
using AgentProxyHub.Core.Models;

namespace AgentProxyHub.Core.Networking;

/// <summary>
/// 节点物理直连验真器：测试握手延迟、落地出口 IP 及 Claude / ChatGPT / Antigravity TLS 可达性
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
        var sw = Stopwatch.StartNew();
        try
        {
            // 1. 尝试通过物理网卡+代理隧道获取真实落地出口 IP (Cloudflare trace 轻量纯文本接口)
            string? exitIp = null;
            string? loc = null;
            try
            {
                (exitIp, loc) = await ProbeCloudflareTraceAsync(node, settings, ct);
            }
            catch
            {
                // 若 1.1.1.1 被节点侧屏蔽，降级只测 AI 域名 TLS 握手
            }

            // 2. 并行验证 Claude / ChatGPT / Google AI TLS 握手
            var aiChecks = new List<string>();
            bool claudeOk = await ProbeTlsEndpointAsync(node, "api.anthropic.com", 443, settings, ct);
            bool gptOk = await ProbeTlsEndpointAsync(node, "chatgpt.com", 443, settings, ct);
            bool agyOk = await ProbeTlsEndpointAsync(node, "generativelanguage.googleapis.com", 443, settings, ct);

            sw.Stop();
            aiChecks.Add(claudeOk ? "Claude ✓" : "Claude ✕");
            aiChecks.Add(gptOk ? "ChatGPT ✓" : "ChatGPT ✕");
            aiChecks.Add(agyOk ? "AGY ✓" : "AGY ✕");

            bool anyOk = claudeOk || gptOk || agyOk || exitIp != null;
            node.Status = anyOk ? "online" : "offline";
            node.LatencyMs = anyOk ? Math.Max(1, (int)sw.ElapsedMilliseconds) : null;
            node.ExitIp = exitIp ?? (anyOk ? node.Host : "—");
            node.IspSummary = !string.IsNullOrEmpty(loc) ? $"出口地区 {loc} · 物理直出已验证" : (anyOk ? "物理直出连通" : "握手超时");
            node.AiReachability = anyOk ? aiChecks : new List<string> { "超时" };
            node.LastCheckedAt = DateTimeOffset.UtcNow;
        }
        catch
        {
            sw.Stop();
            node.Status = "offline";
            node.LatencyMs = null;
            node.ExitIp = "—";
            node.IspSummary = "连接失败 / 超时";
            node.AiReachability = new List<string> { "超时" };
            node.LastCheckedAt = DateTimeOffset.UtcNow;
        }

        return node;
    }

    private async Task<(string? Ip, string? Loc)> ProbeCloudflareTraceAsync(ProxyNode node, IsolationSettings settings, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(6));

        using var stream = await _connector.ConnectViaNodeAsync(node, "1.1.1.1", 443, settings, cts.Token);
        using var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "cloudflare.com" }, cts.Token);

        byte[] req = "GET /cdn-cgi/trace HTTP/1.1\r\nHost: 1.1.1.1\r\nConnection: close\r\nUser-Agent: AgentProxyHub/1.0\r\n\r\n"u8.ToArray();
        await ssl.WriteAsync(req, cts.Token);
        await ssl.FlushAsync(cts.Token);

        using var reader = new StreamReader(ssl, Encoding.UTF8);
        string body = await reader.ReadToEndAsync(cts.Token);
        string? ip = null;
        string? loc = null;
        foreach (string line in body.Split('\n'))
        {
            if (line.StartsWith("ip=", StringComparison.Ordinal)) ip = line[3..].Trim();
            else if (line.StartsWith("loc=", StringComparison.Ordinal)) loc = line[4..].Trim();
        }
        return (ip, loc);
    }

    private async Task<bool> ProbeTlsEndpointAsync(ProxyNode node, string host, int port, IsolationSettings settings, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var stream = await _connector.ConnectViaNodeAsync(node, host, port, settings, cts.Token);
            using var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
