using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AgentProxyHub.Core.Models;
using AgentProxyHub.Core.Networking;
using AgentProxyHub.Core.Telemetry;
using Xunit;

namespace AgentProxyHub.Tests;

public class ProxyAndTelemetryTests
{
    [Fact]
    public void BatchImporter_ShouldParseMixedFormatsAndDeduplicate()
    {
        string raw = """
                     socks5://user1:pass1@38.154.227.14:1080#US-静态-01
                     http://user2:pass2@104.221.88.19:8080#US-HTTP-02
                     133.167.44.91:10808:agy:jp999#JP-NTT-03
                     socks5://user1:pass1@38.154.227.14:1080#重复节点
                     invalid-host-without-port
                     """;

        var result = ProxyNodeImporter.Parse(raw, ProxyProtocol.SOCKS5, "测试组");
        Assert.Equal(5, result.TotalLines);
        Assert.Equal(3, result.ValidCount);
        Assert.Equal(1, result.DuplicateCount);
        Assert.Equal(1, result.ErrorCount);
        Assert.Equal(ProxyProtocol.SOCKS5, result.Items[0].Node!.Protocol);
        Assert.Equal(ProxyProtocol.HTTP, result.Items[1].Node!.Protocol);
        Assert.Equal("agy", result.Items[2].Node!.Username);
    }

    [Fact]
    public void TlsSniInspector_ShouldIdentifyFakeIpAndExtractSni()
    {
        Assert.True(TlsSniInspector.IsFakeIp("198.18.0.12"));
        Assert.True(TlsSniInspector.IsFakeIp("198.19.255.1"));
        Assert.False(TlsSniInspector.IsFakeIp("38.154.227.14"));

        byte[] clientHello = BuildMinimalTlsClientHello("api.anthropic.com");
        bool ok = TlsSniInspector.TryExtractSni(clientHello, out string? sni);
        Assert.True(ok);
        Assert.Equal("api.anthropic.com", sni);
    }

    [Fact]
    public void PhysicalNicManager_ShouldEnumerateNicsAndFilterLoopback()
    {
        var mgr = new PhysicalNicManager();
        var adapters = mgr.GetAdapters();
        Assert.NotNull(adapters);
        Assert.DoesNotContain(adapters, a => a.Description.Contains("Loopback", StringComparison.OrdinalIgnoreCase) && !a.IsVirtualOrTunnel);
    }

    [Fact]
    public void SessionParsers_ShouldParseAntigravityClaudeAndCodexAndCalculateStats()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "aph-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);
        try
        {
            // 1. 构造 Antigravity 样本
            string agyDir = Path.Combine(tempRoot, ".gemini", "antigravity", "brain", "conv-1", ".system_generated", "logs");
            Directory.CreateDirectory(agyDir);
            File.WriteAllText(Path.Combine(agyDir, "transcript.jsonl"), """
                {"step_index":1,"type":"USER_INPUT","created_at":"2026-11-25T10:00:00Z"}
                {"step_index":2,"type":"PLANNER_RESPONSE","created_at":"2026-11-25T10:00:10Z","model":"gemini-2.5-pro","input_tokens":2000,"cache_read_tokens":8000,"output_tokens":500}
                """);

            // 2. 构造 Claude Code 流式去重样本（同 message.id 出现两条，应取终态最大值）
            string claudeDir = Path.Combine(tempRoot, ".claude", "projects", "demo");
            Directory.CreateDirectory(claudeDir);
            File.WriteAllText(Path.Combine(claudeDir, "sess1.jsonl"), """
                {"timestamp":"2026-11-25T10:01:00Z","message":{"id":"msg_01","model":"claude-sonnet-4-20250514","usage":{"input_tokens":100,"cache_creation_input_tokens":200,"cache_read_input_tokens":700,"output_tokens":10}}}
                {"timestamp":"2026-11-25T10:01:05Z","message":{"id":"msg_01","model":"claude-sonnet-4-20250514","usage":{"input_tokens":100,"cache_creation_input_tokens":200,"cache_read_input_tokens":700,"output_tokens":300}}}
                """);

            // 3. 构造 Codex 样本（注意 input_tokens 包含 cached_input_tokens，有效输入应扣减缓存；重复 total_tokens 应去重）
            string codexDir = Path.Combine(tempRoot, ".codex", "sessions", "2026");
            Directory.CreateDirectory(codexDir);
            File.WriteAllText(Path.Combine(codexDir, "rollout.jsonl"), """
                {"timestamp":"2026-11-25T10:02:00Z","type":"turn_context","payload":{"model":"gpt-5"}}
                {"timestamp":"2026-11-25T10:02:04Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":1500},"last_token_usage":{"input_tokens":1000,"cached_input_tokens":600,"cache_write_input_tokens":0,"output_tokens":500}}}}
                {"timestamp":"2026-11-25T10:02:04Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":1500},"last_token_usage":{"input_tokens":1000,"cached_input_tokens":600,"cache_write_input_tokens":0,"output_tokens":500}}}}
                """);

            var scanner = new SessionLogScanner(tempRoot);
            var pricing = new ModelPricingCatalog();
            var stats = new UsageStatsService(scanner, pricing);
            var sync = stats.SyncNow();

            Assert.Equal(3, sync.ScannedFiles);
            Assert.Equal(3, sync.ParsedEntries);

            var dash = stats.QueryDashboard("all", "all", "all", 1, 20, []);
            Assert.Equal(3, dash.TotalRequests);
            // 有效输入：2000 + 100 + (1000-600=400) = 2500
            Assert.Equal(2500, dash.EffectiveInputTokens);
            // 输出：500 + 300 + 500 = 1300
            Assert.Equal(1300, dash.OutputTokens);
            // 缓存读：8000 + 700 + 600 = 9300
            Assert.Equal(9300, dash.CacheReadTokens);
            // 缓存写：200
            Assert.Equal(200, dash.CacheWriteTokens);
            Assert.True(dash.TotalCostUsd > 0);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task MixedProxyServer_ShouldForwardViaSocks5AndBlockOnKillSwitch()
    {
        // 1. 启动本地模拟目标回显服务器
        using var echoListener = new TcpListener(IPAddress.Loopback, 0);
        echoListener.Start();
        int echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var c = await echoListener.AcceptTcpClientAsync();
                _ = Task.Run(async () =>
                {
                    using (c)
                    using (var s = c.GetStream())
                    {
                        var buf = new byte[128];
                        int n = await s.ReadAsync(buf);
                        if (n > 0) await s.WriteAsync(buf.AsMemory(0, n));
                    }
                });
            }
        });

        // 2. 启动本地带账密认证的模拟上游 SOCKS5 服务器
        using var upstreamSocks = new TcpListener(IPAddress.Loopback, 0);
        upstreamSocks.Start();
        int socksPort = ((IPEndPoint)upstreamSocks.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var c = await upstreamSocks.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleMockSocks5Async(c, "u1", "p1"));
            }
        });

        var nicMgr = new PhysicalNicManager();
        var dns = new CleanDnsResolver(nicMgr);
        var connector = new UpstreamConnector(nicMgr, dns);
        using var server = new MixedProxyServer(connector);

        var node = new ProxyNode
        {
            Id = "test-socks",
            Name = "Mock-SOCKS5",
            Protocol = ProxyProtocol.SOCKS5,
            Host = "127.0.0.1",
            Port = socksPort,
            Username = "u1",
            Password = "p1",
            Enabled = true
        };

        var seat = new AppSeat
        {
            Id = "test-seat",
            Name = "TestApp",
            LocalPort = 0, // 自动分配空闲测试端口
            BoundNodeId = node.Id,
            Enabled = true,
            KillSwitchEnabled = true,
            AllowSameGroupFailover = false
        };

        var seatDirect = new AppSeat
        {
            Id = "broken-seat",
            Name = "BrokenApp",
            LocalPort = 0,
            BoundNodeId = "non-existent-node",
            Enabled = true,
            KillSwitchEnabled = true
        };

        // 分别启动两个端口测试
        int freePort1 = GetFreeTcpPort();
        int freePort2 = GetFreeTcpPort();
        seat.LocalPort = freePort1;
        seatDirect.LocalPort = freePort2;
        server.UpdateTopology([seat, seatDirect], [node], new IsolationSettings { GlobalKillSwitch = true });

        // 3. 客户端走 HTTP CONNECT 连入混合端口 -> 自动转入上游账密 SOCKS5 -> 连向回显服务器
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, freePort1);
            using var s = client.GetStream();
            byte[] connectReq = Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{echoPort} HTTP/1.1\r\nHost: 127.0.0.1:{echoPort}\r\n\r\n");
            await s.WriteAsync(connectReq);

            var respBuf = new byte[256];
            int rn = await s.ReadAsync(respBuf);
            string respStr = Encoding.ASCII.GetString(respBuf, 0, rn);
            Assert.Contains("200 Connection Established", respStr);

            byte[] msg = Encoding.UTF8.GetBytes("hello-agent-proxy-hub");
            await s.WriteAsync(msg);
            var echoBuf = new byte[64];
            int en = await s.ReadAsync(echoBuf);
            Assert.Equal("hello-agent-proxy-hub", Encoding.UTF8.GetString(echoBuf, 0, en));
        }

        // 4. 验证熔断锁：未绑定可用节点时必须直接返回 502 拦截，绝不回退直连
        using (var blockedClient = new TcpClient())
        {
            await blockedClient.ConnectAsync(IPAddress.Loopback, freePort2);
            using var s = blockedClient.GetStream();
            byte[] connectReq = Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{echoPort} HTTP/1.1\r\nHost: 127.0.0.1:{echoPort}\r\n\r\n");
            await s.WriteAsync(connectReq);
            var respBuf = new byte[256];
            int rn = await s.ReadAsync(respBuf);
            string respStr = Encoding.ASCII.GetString(respBuf, 0, rn);
            Assert.Contains("502", respStr);
        }

        var snaps = server.GetTrafficSnapshots();
        var s1 = snaps.First(x => x.SeatId == "test-seat");
        var s2 = snaps.First(x => x.SeatId == "broken-seat");
        Assert.True(s1.BytesTx > 0 && s1.BytesRx > 0);
        Assert.True(s2.KillSwitchBlockedCount >= 1);
    }

    private static async Task HandleMockSocks5Async(TcpClient client, string expectedUser, string expectedPass)
    {
        using (client)
        using (var s = client.GetStream())
        {
            var hdr = new byte[2];
            await UpstreamConnector.ReadExactAsync(s, hdr, CancellationToken.None);
            var methods = new byte[hdr[1]];
            await UpstreamConnector.ReadExactAsync(s, methods, CancellationToken.None);
            await s.WriteAsync(new byte[] { 0x05, 0x02 });

            var authHdr = new byte[2];
            await UpstreamConnector.ReadExactAsync(s, authHdr, CancellationToken.None);
            var u = new byte[authHdr[1]];
            await UpstreamConnector.ReadExactAsync(s, u, CancellationToken.None);
            var pLen = new byte[1];
            await UpstreamConnector.ReadExactAsync(s, pLen, CancellationToken.None);
            var p = new byte[pLen[0]];
            await UpstreamConnector.ReadExactAsync(s, p, CancellationToken.None);

            if (Encoding.UTF8.GetString(u) != expectedUser || Encoding.UTF8.GetString(p) != expectedPass)
            {
                await s.WriteAsync(new byte[] { 0x01, 0x01 });
                return;
            }
            await s.WriteAsync(new byte[] { 0x01, 0x00 });

            var req = new byte[4];
            await UpstreamConnector.ReadExactAsync(s, req, CancellationToken.None);
            if (req[3] != 0x01) return;
            var ipBytes = new byte[4];
            await UpstreamConnector.ReadExactAsync(s, ipBytes, CancellationToken.None);
            var portBytes = new byte[2];
            await UpstreamConnector.ReadExactAsync(s, portBytes, CancellationToken.None);
            int port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);

            using var target = new TcpClient();
            await target.ConnectAsync(new IPAddress(ipBytes), port);
            await s.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
            using var ts = target.GetStream();
            await Task.WhenAny(s.CopyToAsync(ts), ts.CopyToAsync(s));
        }
    }

    private static int GetFreeTcpPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static byte[] BuildMinimalTlsClientHello(string serverName)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(serverName);
        using var ext = new MemoryStream();
        // server_name extension (0x0000)
        ext.Write(new byte[] { 0x00, 0x00 });
        ushort extLen = (ushort)(nameBytes.Length + 5);
        ext.Write(new byte[] { (byte)(extLen >> 8), (byte)(extLen & 0xFF) });
        ushort listLen = (ushort)(nameBytes.Length + 3);
        ext.Write(new byte[] { (byte)(listLen >> 8), (byte)(listLen & 0xFF) });
        ext.WriteByte(0x00); // host_name
        ext.Write(new byte[] { (byte)(nameBytes.Length >> 8), (byte)(nameBytes.Length & 0xFF) });
        ext.Write(nameBytes);
        byte[] extBytes = ext.ToArray();

        using var hello = new MemoryStream();
        hello.Write(new byte[] { 0x03, 0x03 }); // TLS 1.2
        hello.Write(new byte[32]);              // Random
        hello.WriteByte(0x00);                  // SessionID len
        hello.Write(new byte[] { 0x00, 0x02, 0x13, 0x01 }); // CipherSuites
        hello.Write(new byte[] { 0x01, 0x00 }); // Compression
        hello.Write(new byte[] { (byte)(extBytes.Length >> 8), (byte)(extBytes.Length & 0xFF) });
        hello.Write(extBytes);
        byte[] helloBody = hello.ToArray();

        using var handshake = new MemoryStream();
        handshake.WriteByte(0x01); // ClientHello
        handshake.WriteByte(0x00);
        handshake.WriteByte((byte)(helloBody.Length >> 8));
        handshake.WriteByte((byte)(helloBody.Length & 0xFF));
        handshake.Write(helloBody);
        byte[] hsBytes = handshake.ToArray();

        using var record = new MemoryStream();
        record.WriteByte(0x16); // Handshake
        record.Write(new byte[] { 0x03, 0x01 });
        record.Write(new byte[] { (byte)(hsBytes.Length >> 8), (byte)(hsBytes.Length & 0xFF) });
        record.Write(hsBytes);
        return record.ToArray();
    }
}
