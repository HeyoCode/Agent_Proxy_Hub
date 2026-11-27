using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AgentProxyHub.Core.Apps;
using AgentProxyHub.Core.Models;
using AgentProxyHub.Core.Networking;
using AgentProxyHub.Core.Storage;
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

    [Fact]
    public void ProxyNodeImporter_ShouldParseBracketedIpv6Nodes()
    {
        string raw = """
                     socks5://u6:p6@[2001:db8::1]:1080#v6-socks
                     [2001:db8::2]:8080:u2:p2#v6-colon
                     [2001:db8::3]:9090#v6-plain
                     """;

        var result = ProxyNodeImporter.Parse(raw, ProxyProtocol.HTTP, "IPv6池");
        Assert.Equal(3, result.TotalLines);
        Assert.Equal(3, result.ValidCount);
        Assert.Equal("2001:db8::1", result.Items[0].Node!.Host);
        Assert.Equal(ProxyProtocol.SOCKS5, result.Items[0].Node!.Protocol);
        Assert.Equal("2001:db8::2", result.Items[1].Node!.Host);
        Assert.Equal("u2", result.Items[1].Node!.Username);
        Assert.Equal("2001:db8::3", result.Items[2].Node!.Host);
        Assert.Equal(9090, result.Items[2].Node!.Port);
    }

    [Fact]
    public async Task MixedProxyServer_ShouldEnforceIpv6Toggle()
    {
        var nicMgr = new PhysicalNicManager();
        var dns = new CleanDnsResolver(nicMgr);
        var connector = new UpstreamConnector(nicMgr, dns);
        using var server = new MixedProxyServer(connector);

        var directNode = new ProxyNode
        {
            Id = "direct-phy-test",
            Name = "Direct",
            Protocol = ProxyProtocol.DIRECT_PHYSICAL,
            Host = "physical-nic",
            Port = 0,
            Enabled = true
        };

        int proxyPort = GetFreeTcpPort();
        var seat = new AppSeat
        {
            Id = "v6-seat",
            Name = "V6App",
            LocalPort = proxyPort,
            BoundNodeId = directNode.Id,
            Enabled = true,
            KillSwitchEnabled = true,
            AllowSameGroupFailover = false
        };

        // 1. IPv6 开关关闭时：HTTP CONNECT [::1] 与 SOCKS5 IPv6 均必须被立即拦截
        server.UpdateTopology([seat], [directNode], new IsolationSettings { EnableIpv6 = false, GlobalKillSwitch = true });

        using (var c1 = new TcpClient())
        {
            await c1.ConnectAsync(IPAddress.Loopback, proxyPort);
            using var s = c1.GetStream();
            byte[] req = Encoding.ASCII.GetBytes("CONNECT [::1]:443 HTTP/1.1\r\nHost: [::1]:443\r\n\r\n");
            await s.WriteAsync(req);
            var buf = new byte[256];
            int rn = await s.ReadAsync(buf);
            string resp = Encoding.ASCII.GetString(buf, 0, rn);
            Assert.Contains("502", resp);
        }

        using (var c2 = new TcpClient())
        {
            await c2.ConnectAsync(IPAddress.Loopback, proxyPort);
            using var s = c2.GetStream();
            await s.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
            var mResp = new byte[2];
            await UpstreamConnector.ReadExactAsync(s, mResp, CancellationToken.None);

            // SOCKS5 CONNECT ::1:443 (ATYP = 0x04)
            byte[] v6Req = new byte[4 + 16 + 2];
            v6Req[0] = 0x05;
            v6Req[1] = 0x01;
            v6Req[2] = 0x00;
            v6Req[3] = 0x04;
            IPAddress.IPv6Loopback.GetAddressBytes().CopyTo(v6Req, 4);
            BinaryPrimitives.WriteUInt16BigEndian(v6Req.AsSpan(20, 2), 443);
            await s.WriteAsync(v6Req);

            var s5Resp = new byte[10];
            await UpstreamConnector.ReadExactAsync(s, s5Resp, CancellationToken.None);
            Assert.Equal(0x08, s5Resp[1]);
        }

        var blockedSnap = server.GetTrafficSnapshots().First(x => x.SeatId == "v6-seat");
        Assert.True(blockedSnap.Ipv6BlockedCount >= 2);

        // 2. IPv6 开关开启后：在支持 IPv6 的宿主环境下应可通过代理连向 [::1] 回显服务
        if (Socket.OSSupportsIPv6)
        {
            using var echoV6 = new TcpListener(IPAddress.IPv6Loopback, 0);
            echoV6.Start();
            int echoV6Port = ((IPEndPoint)echoV6.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient c;
                    try { c = await echoV6.AcceptTcpClientAsync(); }
                    catch { break; }
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

            server.UpdateTopology([seat], [directNode], new IsolationSettings { EnableIpv6 = true, GlobalKillSwitch = true });

            using var c3 = new TcpClient();
            await c3.ConnectAsync(IPAddress.Loopback, proxyPort);
            using var s3 = c3.GetStream();
            byte[] connectReq = Encoding.ASCII.GetBytes($"CONNECT [::1]:{echoV6Port} HTTP/1.1\r\nHost: [::1]:{echoV6Port}\r\n\r\n");
            await s3.WriteAsync(connectReq);

            var respBuf = new byte[256];
            int rn = await s3.ReadAsync(respBuf);
            Assert.Contains("200 Connection Established", Encoding.ASCII.GetString(respBuf, 0, rn));

            byte[] msg = Encoding.UTF8.GetBytes("hello-ipv6-hub");
            await s3.WriteAsync(msg);
            var echoBuf = new byte[64];
            int en = await s3.ReadAsync(echoBuf);
            Assert.Equal("hello-ipv6-hub", Encoding.UTF8.GetString(echoBuf, 0, en));
        }
    }

    [Fact]
    public void AppProxyTakeoverService_ShouldTakeoverAndRestoreConfigsWithoutDataLoss()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "aph-takeover-" + Guid.NewGuid().ToString("N")[..8]);
        string fakeHome = Path.Combine(sandbox, "home");
        string fakeRoaming = Path.Combine(sandbox, "roaming");
        string fakeLocal = Path.Combine(sandbox, "local");
        Directory.CreateDirectory(Path.Combine(fakeHome, ".claude"));
        Directory.CreateDirectory(Path.Combine(fakeRoaming, "Cursor", "User"));
        Directory.CreateDirectory(Path.Combine(fakeLocal, "Antigravity-Zh-Auto"));

        try
        {
            string claudeJson = Path.Combine(fakeHome, ".claude", "settings.json");
            File.WriteAllText(claudeJson, """
                {
                  "language": "zh-CN",
                  "env": {
                    "ANTHROPIC_AUTH_TOKEN": "sk-preserve-me",
                    "ANTHROPIC_BASE_URL": "https://api.example.com"
                  }
                }
                """);

            string cursorJson = Path.Combine(fakeRoaming, "Cursor", "User", "settings.json");
            File.WriteAllText(cursorJson, """
                {
                  // 用户已有注释和字体配置
                  "editor.fontSize": 14,
                  "http.proxy": "",
                }
                """);

            string agyScript = Path.Combine(fakeLocal, "Antigravity-Zh-Auto", "run-launch.ps1");
            const string originalScript = "$ErrorActionPreference = 'Continue'\r\n& node.exe cli.js launch\r\n";
            File.WriteAllText(agyScript, originalScript, new UTF8Encoding(true));

            var claudeSeat = new AppSeat { Id = "claude", Name = "Claude", LocalPort = 17891, Enabled = true };
            var cursorSeat = new AppSeat { Id = "cursor", Name = "Cursor", LocalPort = 17894, Enabled = true };
            var agySeat = new AppSeat { Id = "agy", Name = "Antigravity", LocalPort = 17893, Enabled = true };
            var gptSeat = new AppSeat { Id = "chatgpt", Name = "ChatGPT", LocalPort = 17892, Enabled = true };

            var svc = new AppProxyTakeoverService(
                fakeHome,
                fakeRoaming,
                fakeLocal,
                names => names.Contains("Antigravity", StringComparer.OrdinalIgnoreCase),
                forceMacOs: false);

            var before = svc.InspectAll([claudeSeat, gptSeat, agySeat, cursorSeat]);
            Assert.False(before.First(s => s.SeatId == "claude").IsTakenOver);
            Assert.False(before.First(s => s.SeatId == "cursor").IsTakenOver);
            Assert.False(before.First(s => s.SeatId == "agy").IsTakenOver);
            Assert.True(before.First(s => s.SeatId == "agy").IsProcessRunning);

            // 1. 启用接管并校验写入准确性与原有字段完好
            Assert.True(svc.SetTakeover(claudeSeat, true).Ok);
            Assert.True(svc.SetTakeover(cursorSeat, true).Ok);
            Assert.True(svc.SetTakeover(agySeat, true).Ok);

            var afterEnable = svc.InspectAll([claudeSeat, gptSeat, agySeat, cursorSeat]);
            Assert.True(afterEnable.First(s => s.SeatId == "claude").IsTakenOver);
            Assert.True(afterEnable.First(s => s.SeatId == "cursor").IsTakenOver);
            Assert.True(afterEnable.First(s => s.SeatId == "agy").IsTakenOver);

            var claudeNode = JsonNode.Parse(File.ReadAllText(claudeJson))!.AsObject();
            Assert.Equal("zh-CN", claudeNode["language"]!.GetValue<string>());
            Assert.Equal("sk-preserve-me", claudeNode["env"]!["ANTHROPIC_AUTH_TOKEN"]!.GetValue<string>());
            Assert.Equal("http://127.0.0.1:17891", claudeNode["env"]!["HTTPS_PROXY"]!.GetValue<string>());

            var cursorNode = JsonNode.Parse(File.ReadAllText(cursorJson))!.AsObject();
            Assert.Equal(14, cursorNode["editor.fontSize"]!.GetValue<int>());
            Assert.Equal("http://127.0.0.1:17894", cursorNode["http.proxy"]!.GetValue<string>());

            string updatedScript = File.ReadAllText(agyScript);
            Assert.Contains("http://127.0.0.1:17893", updatedScript);
            Assert.Contains("& node.exe cli.js launch", updatedScript);

            // 2. 还原接管并校验原值 100% 恢复
            Assert.True(svc.SetTakeover(claudeSeat, false).Ok);
            Assert.True(svc.SetTakeover(cursorSeat, false).Ok);
            Assert.True(svc.SetTakeover(agySeat, false).Ok);

            var afterRestore = svc.InspectAll([claudeSeat, gptSeat, agySeat, cursorSeat]);
            Assert.False(afterRestore.First(s => s.SeatId == "claude").IsTakenOver);
            Assert.False(afterRestore.First(s => s.SeatId == "cursor").IsTakenOver);
            Assert.False(afterRestore.First(s => s.SeatId == "agy").IsTakenOver);

            var restoredClaude = JsonNode.Parse(File.ReadAllText(claudeJson))!.AsObject();
            Assert.Equal("sk-preserve-me", restoredClaude["env"]!["ANTHROPIC_AUTH_TOKEN"]!.GetValue<string>());
            Assert.Null(restoredClaude["env"]!["HTTPS_PROXY"]);

            var restoredCursor = JsonNode.Parse(File.ReadAllText(cursorJson))!.AsObject();
            Assert.Equal(14, restoredCursor["editor.fontSize"]!.GetValue<int>());
            Assert.Equal("", restoredCursor["http.proxy"]!.GetValue<string>());
            Assert.Null(restoredCursor["http.proxySupport"]);

            string restoredScript = File.ReadAllText(agyScript);
            Assert.DoesNotContain("AgentProxyHub", restoredScript);
            Assert.Contains("& node.exe cli.js launch", restoredScript);
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyViaLocalSeatAsync_ShouldSendRealBytesThroughMixedProxyServer()
    {
        using var mockTarget = new TcpListener(IPAddress.Loopback, 0);
        mockTarget.Start();
        int targetPort = ((IPEndPoint)mockTarget.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await mockTarget.AcceptTcpClientAsync(); }
                catch { break; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    using (var s = c.GetStream())
                    {
                        var buf = new byte[1024];
                        _ = await s.ReadAsync(buf);
                        const string body = "ip=198.51.100.42\nloc=US\n";
                        byte[] resp = Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
                        await s.WriteAsync(resp);
                        await s.FlushAsync();
                    }
                });
            }
        });

        using var upstreamSocks = new TcpListener(IPAddress.Loopback, 0);
        upstreamSocks.Start();
        int socksPort = ((IPEndPoint)upstreamSocks.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await upstreamSocks.AcceptTcpClientAsync(); }
                catch { break; }
                _ = Task.Run(() => HandleMockSocks5Async(c, "u2", "p2"));
            }
        });

        var nicMgr = new PhysicalNicManager();
        var dns = new CleanDnsResolver(nicMgr);
        var connector = new UpstreamConnector(nicMgr, dns);
        var checker = new NodeHealthChecker(connector);
        using var server = new MixedProxyServer(connector);

        var node = new ProxyNode
        {
            Id = "n-tunnel-test",
            Name = "Mock-SOCKS5-Tunnel",
            Protocol = ProxyProtocol.SOCKS5,
            Host = "127.0.0.1",
            Port = socksPort,
            Username = "u2",
            Password = "p2",
            Enabled = true
        };

        int seatPort = GetFreeTcpPort();
        var seat = new AppSeat
        {
            Id = "agy",
            Name = "Antigravity",
            LocalPort = seatPort,
            BoundNodeId = node.Id,
            Enabled = true,
            KillSwitchEnabled = true,
            AllowSameGroupFailover = false
        };

        server.UpdateTopology([seat], [node], new IsolationSettings { GlobalKillSwitch = true });

        var res = await checker.VerifyViaLocalSeatAsync(
            seat.Id,
            seatPort,
            node,
            new IsolationSettings { GlobalKillSwitch = true },
            [("Trace", $"http://127.0.0.1:{targetPort}/cdn-cgi/trace")]);

        Assert.True(res.Ok);
        Assert.Equal("198.51.100.42", res.ExitIp);
        Assert.Equal("online", node.Status);
        Assert.NotNull(node.LatencyMs);

        var snap = server.GetTrafficSnapshots().First(x => x.SeatId == "agy");
        Assert.True(snap.TotalConnections >= 1);
        Assert.True(snap.BytesTx > 0 && snap.BytesRx > 0);
    }

    [Fact]
    public async Task ThroughputSamplerAndIncrementalSync_ShouldComputeRatesAndPickUpNewLogs()
    {
        // 1. 验证实时吞吐速率采样与闲置归零
        using var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        int echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await echo.AcceptTcpClientAsync(); }
                catch { break; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    using (var s = c.GetStream())
                    {
                        var buf = new byte[512];
                        int n = await s.ReadAsync(buf);
                        if (n > 0) await s.WriteAsync(buf.AsMemory(0, n));
                    }
                });
            }
        });

        using var upstreamSocks = new TcpListener(IPAddress.Loopback, 0);
        upstreamSocks.Start();
        int socksPort = ((IPEndPoint)upstreamSocks.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await upstreamSocks.AcceptTcpClientAsync(); }
                catch { break; }
                _ = Task.Run(() => HandleMockSocks5Async(c, "u3", "p3"));
            }
        });

        var nicMgr = new PhysicalNicManager();
        var dns = new CleanDnsResolver(nicMgr);
        var connector = new UpstreamConnector(nicMgr, dns);
        using var server = new MixedProxyServer(connector);

        var node = new ProxyNode
        {
            Id = "n-rate",
            Name = "RateTestNode",
            Protocol = ProxyProtocol.SOCKS5,
            Host = "127.0.0.1",
            Port = socksPort,
            Username = "u3",
            Password = "p3",
            Enabled = true
        };

        int seatPort = GetFreeTcpPort();
        var seat = new AppSeat
        {
            Id = "rate-seat",
            Name = "RateApp",
            LocalPort = seatPort,
            BoundNodeId = node.Id,
            Enabled = true,
            KillSwitchEnabled = true,
            AllowSameGroupFailover = false
        };
        server.UpdateTopology([seat], [node], new IsolationSettings { GlobalKillSwitch = true });

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, seatPort);
            using var s = client.GetStream();
            byte[] connectReq = Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{echoPort} HTTP/1.1\r\nHost: 127.0.0.1:{echoPort}\r\n\r\n");
            await s.WriteAsync(connectReq);
            var respBuf = new byte[128];
            int rn = await s.ReadAsync(respBuf);
            Assert.Contains("200 Connection Established", Encoding.ASCII.GetString(respBuf, 0, rn));

            byte[] payload = Encoding.UTF8.GetBytes("throughput-sample-payload-1234567890");
            await s.WriteAsync(payload);
            var back = new byte[128];
            int bn = await s.ReadAsync(back);
            Assert.Equal(payload.Length, bn);
        }

        await Task.Delay(80);
        server.SampleThroughputRates();
        var activeSnap = server.GetTrafficSnapshots().First(x => x.SeatId == "rate-seat");
        Assert.True(activeSnap.BytesTxPerSec > 0 && activeSnap.BytesRxPerSec > 0);

        await Task.Delay(80);
        server.SampleThroughputRates();
        var idleSnap = server.GetTrafficSnapshots().First(x => x.SeatId == "rate-seat");
        Assert.Equal(0, idleSnap.BytesTxPerSec);
        Assert.Equal(0, idleSnap.BytesRxPerSec);

        // 2. 验证 5s 轮询底层增量解析：无变化命中缓存快路径，追加新行即时纳入统计
        string tempRoot = Path.Combine(Path.GetTempPath(), "aph-inc-" + Guid.NewGuid().ToString("N")[..8]);
        string agyDir = Path.Combine(tempRoot, ".gemini", "antigravity", "brain", "conv-r", ".system_generated", "logs");
        Directory.CreateDirectory(agyDir);
        string logFile = Path.Combine(agyDir, "transcript.jsonl");
        try
        {
            File.WriteAllText(logFile, "{\"step_index\":1,\"type\":\"PLANNER_RESPONSE\",\"created_at\":\"2026-11-26T10:00:00Z\",\"model\":\"gemini-2.5-pro\",\"input_tokens\":1000,\"cache_read_tokens\":0,\"output_tokens\":200}\n");

            var scanner = new SessionLogScanner(tempRoot);
            var stats = new UsageStatsService(scanner, new ModelPricingCatalog());

            var first = stats.SyncNow();
            Assert.Equal(1, first.ParsedEntries);

            var (_, _, unchanged) = scanner.ScanIncrementalWithChangeFlag();
            Assert.False(unchanged);

            File.AppendAllText(logFile, "{\"step_index\":2,\"type\":\"PLANNER_RESPONSE\",\"created_at\":\"2026-11-26T10:00:05Z\",\"model\":\"gemini-2.5-pro\",\"input_tokens\":500,\"cache_read_tokens\":0,\"output_tokens\":100}\n");
            var second = stats.SyncNow();
            Assert.Equal(2, second.ParsedEntries);

            var dash = stats.QueryDashboard("all", "all", "all", 1, 20, server.GetTrafficSnapshots());
            Assert.Equal(2, dash.TotalRequests);
            Assert.Equal(1800, dash.TotalTokens);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ProxyNodeImporter_ShouldParseAllL1AndL2ProtocolUris()
    {
        string ssCred = Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:my-ss-pass"));
        string raw = $"""
                     trojan://tr-pass-1@jp.example.com:443?sni=cdn.example.com&allowInsecure=1#JP-Trojan
                     ss://{ssCred}@us.example.com:8388#US-SS
                     vless://11111111-2222-3333-4444-555555555555@edge.example.com:443?security=reality&sni=www.microsoft.com&pbk=PUBKEY123&sid=0a1b&fp=chrome&flow=xtls-rprx-vision#Reality-Edge
                     hy2://hy2-secret@hk.example.com:8443?sni=hk.example.com&obfs=salamander&obfs-password=obfspw#HK-Hy2
                     tuic://aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee:tuicpass@sg.example.com:443?sni=sg.example.com&congestion_control=bbr&alpn=h3#SG-TUIC
                     wg://PRIVKEY123@de.example.com:51820?publickey=PEERPUB&address=10.0.0.2/32&mtu=1280#DE-WG
                     """;

        var parsed = ProxyNodeImporter.Parse(raw, ProxyProtocol.SOCKS5, "全协议测试池");
        Assert.Equal(6, parsed.TotalLines);
        Assert.Equal(6, parsed.ValidCount);
        Assert.Equal(0, parsed.ErrorCount);

        var tr = parsed.Items[0].Node!;
        Assert.Equal(ProxyProtocol.TROJAN, tr.Protocol);
        Assert.Equal("native", tr.EngineKind);
        Assert.Equal("cdn.example.com", tr.Sni);
        Assert.True(tr.AllowInsecure);

        var ss = parsed.Items[1].Node!;
        Assert.Equal(ProxyProtocol.SHADOWSOCKS, ss.Protocol);
        Assert.Equal("aes-256-gcm", ss.Method);
        Assert.Equal("my-ss-pass", ss.Password);
        Assert.Equal("native", ss.EngineKind);

        var vless = parsed.Items[2].Node!;
        Assert.Equal(ProxyProtocol.VLESS, vless.Protocol);
        Assert.True(vless.RequiresSidecarEngine);
        Assert.Equal("PUBKEY123", vless.Extra!["pbk"]);

        var hy2 = parsed.Items[3].Node!;
        Assert.Equal(ProxyProtocol.HYSTERIA2, hy2.Protocol);
        Assert.True(hy2.RequiresSidecarEngine);
        Assert.Equal("salamander", hy2.Extra!["obfs"]);

        var tuic = parsed.Items[4].Node!;
        Assert.Equal(ProxyProtocol.TUIC, tuic.Protocol);
        Assert.Equal("tuicpass", tuic.Password);

        var wg = parsed.Items[5].Node!;
        Assert.Equal(ProxyProtocol.WIREGUARD, wg.Protocol);
        Assert.Equal("PRIVKEY123", wg.Password);
    }

    [Fact]
    public async Task NativeCryptoProtocols_ShouldVerifySha224TrojanAndShadowsocksAeadRoundtrip()
    {
        // 1. 验证 FIPS 180-4 SHA-224 标准向量
        Assert.Equal("d14a028c2a3a2bc9476102bb288234c415a2b01f828ea62ac5b3e42f", NativeCryptoProtocols.ComputeSha224Hex(""));
        Assert.Equal("23097d223405d8228642a477bda255b32aadbce4bda0b3f7e36c9da7", NativeCryptoProtocols.ComputeSha224Hex("abc"));

        byte[] trojanHdr = NativeCryptoProtocols.BuildTrojanConnectHeader("abc", "api.anthropic.com", 443);
        Assert.StartsWith("23097d223405d8228642a477bda255b32aadbce4bda0b3f7e36c9da7\r\n", Encoding.ASCII.GetString(trojanHdr, 0, 58));

        // 2. 端到端验证 Shadowsocks AEAD (aes-256-gcm & chacha20-ietf-poly1305) 双工加密解密
        foreach (string method in new[] { "aes-256-gcm", "chacha20-ietf-poly1305" })
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Task serverTask = Task.Run(async () =>
            {
                using var srvClient = await listener.AcceptTcpClientAsync();
                using var ssServer = new ShadowsocksAeadStream(srvClient.GetStream(), method, "ss-secret-key");
                var buf = new byte[128];
                int n = await ssServer.ReadAsync(buf);
                await ssServer.WriteAsync(buf.AsMemory(0, n));
                await ssServer.FlushAsync();
            });

            using var cli = new TcpClient();
            await cli.ConnectAsync(IPAddress.Loopback, port);
            using var ssClient = new ShadowsocksAeadStream(cli.GetStream(), method, "ss-secret-key");
            byte[] plain = Encoding.UTF8.GetBytes($"hello-ss-{method}-payload");
            await ssClient.WriteAsync(plain);
            await ssClient.FlushAsync();

            var back = new byte[128];
            int rn = await ssClient.ReadAsync(back);
            Assert.Equal(Encoding.UTF8.GetString(plain), Encoding.UTF8.GetString(back, 0, rn));
            await serverTask;
        }
    }

    [Fact]
    public async Task DoubleHopRelayChain_And_SingBoxBridgeConfig_ShouldWorkEndToEnd()
    {
        // 1. 构造：本地目标回显 -> 落地 SOCKS5 -> 前置中转 SOCKS5 -> MixedProxyServer
        using var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        int echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await echo.AcceptTcpClientAsync(); }
                catch { break; }
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

        using var landingSocks = new TcpListener(IPAddress.Loopback, 0);
        landingSocks.Start();
        int landingPort = ((IPEndPoint)landingSocks.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await landingSocks.AcceptTcpClientAsync(); }
                catch { break; }
                _ = Task.Run(() => HandleMockSocks5Async(c, "landU", "landP"));
            }
        });

        using var relaySocks = new TcpListener(IPAddress.Loopback, 0);
        relaySocks.Start();
        int relayPort = ((IPEndPoint)relaySocks.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await relaySocks.AcceptTcpClientAsync(); }
                catch { break; }
                _ = Task.Run(() => HandleMockSocks5Async(c, "relayU", "relayP"));
            }
        });

        var nicMgr = new PhysicalNicManager();
        var dns = new CleanDnsResolver(nicMgr);
        var connector = new UpstreamConnector(nicMgr, dns);
        using var server = new MixedProxyServer(connector);

        var relayNode = new ProxyNode { Id = "n-relay", Name = "Relay", Protocol = ProxyProtocol.SOCKS5, Host = "127.0.0.1", Port = relayPort, Username = "relayU", Password = "relayP", Enabled = true };
        var landNode = new ProxyNode { Id = "n-land", Name = "Landing", Protocol = ProxyProtocol.SOCKS5, Host = "127.0.0.1", Port = landingPort, Username = "landU", Password = "landP", Enabled = true };
        int seatPort = GetFreeTcpPort();
        var seat = new AppSeat
        {
            Id = "chain-seat",
            Name = "ChainApp",
            LocalPort = seatPort,
            BoundNodeId = landNode.Id,
            RelayNodeId = relayNode.Id,
            Enabled = true,
            KillSwitchEnabled = true,
            AllowSameGroupFailover = false
        };
        server.UpdateTopology([seat], [relayNode, landNode], new IsolationSettings { GlobalKillSwitch = true });

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, seatPort);
            using var s = client.GetStream();
            byte[] req = Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{echoPort} HTTP/1.1\r\nHost: 127.0.0.1:{echoPort}\r\n\r\n");
            await s.WriteAsync(req);
            var respBuf = new byte[128];
            int rn = await s.ReadAsync(respBuf);
            Assert.Contains("200 Connection Established", Encoding.ASCII.GetString(respBuf, 0, rn));

            byte[] msg = Encoding.UTF8.GetBytes("double-hop-chain-ok");
            await s.WriteAsync(msg);
            var back = new byte[64];
            int bn = await s.ReadAsync(back);
            Assert.Equal("double-hop-chain-ok", Encoding.UTF8.GetString(back, 0, bn));
        }

        var snap = server.GetTrafficSnapshots().First(x => x.SeatId == "chain-seat");
        Assert.True(snap.BytesTx > 0 && snap.BytesRx > 0);

        // 2. 验证 sing-box 隔离桥接配置生成与物理网卡 bind_interface 绑定
        var parsedL2 = ProxyNodeImporter.Parse(
            "vless://11111111-2222-3333-4444-555555555555@edge.example.com:443?security=reality&sni=www.microsoft.com&pbk=PK1&sid=01&fp=chrome#R1\n" +
            "hy2://pw@hk.example.com:443?sni=hk.example.com#H1");
        var l2Nodes = parsedL2.Items.Select(x => x.Node!).ToList();

        var (json, portMap) = SingBoxSidecarManager.BuildBridgeConfigJson(l2Nodes, "以太网", new IsolationSettings { EnableIpv6 = false });
        Assert.Equal(2, portMap.Count);
        Assert.Contains("\"bind_interface\": \"\\u4EE5\\u592A\\u7F51\"", json);
        Assert.Contains("\"type\": \"vless\"", json);
        Assert.Contains("\"type\": \"hysteria2\"", json);
        Assert.Contains("\"public_key\": \"PK1\"", json);
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

    [Fact]
    public void ProxyNodeImporter_ShouldParseClashYamlInlineAndBlockSubscriptions()
    {
        const string clashYaml = """
            port: 7890
            proxies:
              - { name: "HK-Trojan-Inline", type: trojan, server: hk.example.com, port: 443, password: "pw1", sni: "cdn.example.com", skip-cert-verify: true }
              - { name: "JP-SS-Inline", type: ss, server: jp.example.com, port: 8388, cipher: aes-256-gcm, password: "pw2" }
              - name: "US-VLESS-Reality"
                type: vless
                server: us.example.com
                port: 443
                uuid: 11111111-2222-3333-4444-555555555555
                tls: true
                servername: www.apple.com
                flow: xtls-rprx-vision
                client-fingerprint: chrome
                reality-opts:
                  public-key: test-pbk-123
                  short-id: abcd
              - name: "SG-Hy2-Block"
                type: hysteria2
                server: sg.example.com
                port: 443
                password: hy2-secret
                sni: sg.example.com
                obfs: salamander
                obfs-password: obfs-pw
            proxy-groups:
              - name: Proxy
                type: select
            """;

        var res = ProxyNodeImporter.Parse(clashYaml, ProxyProtocol.SOCKS5, "机场订阅测试");
        Assert.Equal(4, res.ValidCount);
        Assert.Equal(0, res.ErrorCount);

        var nodes = res.Items.Where(i => i.Valid).Select(i => i.Node!).ToList();
        var trojan = Assert.Single(nodes, n => n.Protocol == ProxyProtocol.TROJAN);
        Assert.Equal("cdn.example.com", trojan.Sni);
        Assert.True(trojan.AllowInsecure);

        var ss = Assert.Single(nodes, n => n.Protocol == ProxyProtocol.SHADOWSOCKS);
        Assert.Equal("aes-256-gcm", ss.Method);

        var vless = Assert.Single(nodes, n => n.Protocol == ProxyProtocol.VLESS);
        Assert.Equal("test-pbk-123", vless.Extra?["pbk"]);
        Assert.Equal("abcd", vless.Extra?["sid"]);
        Assert.Equal("xtls-rprx-vision", vless.Extra?["flow"]);

        var hy2 = Assert.Single(nodes, n => n.Protocol == ProxyProtocol.HYSTERIA2);
        Assert.Equal("salamander", hy2.Extra?["obfs"]);
        Assert.Equal("obfs-pw", hy2.Extra?["obfs-password"]);
    }

    [Fact]
    public void SessionLogScanner_AntigravityShouldComputeForwardDurationAndEstimatedTtft()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"aph_agy_ttft_{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(tmp, [
                """{"step_index":1,"type":"GENERIC","created_at":"2026-11-26T17:05:54Z"}""",
                """{"step_index":2,"type":"PLANNER_RESPONSE","created_at":"2026-11-26T17:05:57Z","model":"gemini-4-neon-medium","input_tokens":3505,"cache_read_tokens":273763,"output_tokens":2800,"thinking":"analyzing request carefully"}""",
                """{"step_index":3,"type":"GENERIC","created_at":"2026-11-26T17:06:29Z"}"""
            ]);

            var entries = SessionLogScanner.ParseAntigravityTranscript(tmp);
            var e = Assert.Single(entries);
            Assert.Equal(32.0, e.DurationSeconds);
            Assert.Equal(87.5, e.TokensPerSecond);
            Assert.NotNull(e.FirstTokenSeconds);
            Assert.InRange(e.FirstTokenSeconds!.Value, 0.5, 31.9);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void HubStateStore_ShouldMergeAll15SupportedAppsWithoutOverwritingExistingSeats()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), $"aph_apps15_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            var store = new AgentProxyHub.Core.Storage.HubStateStore(tmpDir);
            var doc = store.LoadOrCreateDefault();
            Assert.Equal(21, doc.Seats.Count);
            string[] expectedIds =
            [
                "claude", "chatgpt", "agy", "cursor", "claude-desktop",
                "gemini-cli", "grok-build", "opencode", "openclaw", "hermes",
                "pi", "minimax-code", "kimi-code", "deepseek-harness", "oh-my-pi"
            ];
            foreach (string id in expectedIds)
            {
                Assert.Contains(doc.Seats, s => s.Id == id);
            }

            // 验证通用无损启动器接管与还原（以 gemini-cli 为例）
            var svc = new AppProxyTakeoverService(tmpDir, tmpDir, tmpDir, _ => false, forceMacOs: false);
            var gemini = doc.Seats.First(s => s.Id == "gemini-cli");
            var (okOn, _, stOn) = svc.SetTakeover(gemini, true);
            Assert.True(okOn);
            Assert.True(stOn.IsTakenOver);
            var (okOff, _, stOff) = svc.SetTakeover(gemini, false);
            Assert.True(okOff);
            Assert.False(stOff.IsTakenOver);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task MixedProxyServer_ShouldHotReloadPortWhenSameAppSeatReferenceMutated()
    {
        var nic = new PhysicalNicManager();
        var dns = new CleanDnsResolver(nic);
        var connector = new UpstreamConnector(nic, dns);
        using var server = new MixedProxyServer(connector);

        int p1 = GetFreeTcpPort();
        int p2 = GetFreeTcpPort();
        var seat = new AppSeat { Id = "claude", Name = "Claude", LocalPort = p1, BoundNodeId = "direct-phy", Enabled = true };
        var direct = new ProxyNode { Id = "direct-phy", Protocol = ProxyProtocol.DIRECT_PHYSICAL, Host = "physical-nic", Enabled = true };
        var iso = new IsolationSettings { EnableIpv6 = false };

        server.UpdateTopology([seat], [direct], iso);
        Assert.Equal(p1, server.GetBoundPort("claude"));

        // 模拟 HubHostFactory 原位修改同一个 AppSeat 引用对象的 LocalPort
        seat.LocalPort = p2;
        server.UpdateTopology([seat], [direct], iso);
        Assert.Equal(p2, server.GetBoundPort("claude"));

        using var probe = new TcpClient();
        await probe.ConnectAsync(IPAddress.Loopback, p2);
        Assert.True(probe.Connected);
    }

    [Fact]
    public void SingBoxSidecarManager_BuildBridgeConfigShouldPreserveExistingPortsOnUnion()
    {
        var n1 = new ProxyNode { Id = "n_active", Protocol = ProxyProtocol.VLESS, Host = "a.example.com", Port = 443, Username = "u1" };
        var n2 = new ProxyNode { Id = "n_verify", Protocol = ProxyProtocol.HYSTERIA2, Host = "b.example.com", Port = 443, Password = "p2" };
        var iso = new IsolationSettings();

        var (_, map1) = SingBoxSidecarManager.BuildBridgeConfigJson([n1], "Ethernet", iso, basePort: 18500);
        int originalPort = map1["n_active"];

        // 当新临时验真节点排在前面并入时，已有活跃节点端口必须保持原值不发生漂移
        var (_, map2) = SingBoxSidecarManager.BuildBridgeConfigJson([n2, n1], "Ethernet", iso, basePort: 18500, previousPorts: map1);
        Assert.Equal(originalPort, map2["n_active"]);
        Assert.NotEqual(originalPort, map2["n_verify"]);
    }

    [Fact]
    public void LoopbackSecurityGuard_ShouldBlockDnsRebindingAndCrossOriginWrites()
    {
        Assert.True(LoopbackSecurityGuard.IsAllowedRequest("127.0.0.1:17880", "POST", "http://127.0.0.1:17880", isApiRoute: true));
        Assert.True(LoopbackSecurityGuard.IsAllowedRequest("localhost:17880", "PUT", "http://localhost:5173", isApiRoute: true));
        Assert.True(LoopbackSecurityGuard.IsAllowedRequest("[::1]:17880", "POST", null, isApiRoute: true));

        // 拦截 DNS Rebinding 恶意域名 Host
        Assert.False(LoopbackSecurityGuard.IsAllowedRequest("evil-rebind.attacker.com:17880", "GET", null, isApiRoute: true));
        // 拦截外部网页跨站 POST (CSRF)
        Assert.False(LoopbackSecurityGuard.IsAllowedRequest("127.0.0.1:17880", "POST", "https://malicious.site", isApiRoute: true));
        // 拦截沙箱 iframe Origin: null 写请求
        Assert.False(LoopbackSecurityGuard.IsAllowedRequest("127.0.0.1:17880", "DELETE", "null", isApiRoute: true));
    }

    [Fact]
    public void CredentialVault_EncryptsAtRest_AndMigratesLegacyPlaintextState()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), $"aph_vault_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            string statePath = Path.Combine(tmpDir, "state.json");
            // 写入一个模拟 v0.4.4 明文密码与订阅 Token 的旧版配置文件
            File.WriteAllText(statePath, """
            {
              "nodes": [
                {
                  "id": "n-legacy",
                  "name": "Legacy-SS",
                  "protocol": "SHADOWSOCKS",
                  "host": "1.2.3.4",
                  "port": 8388,
                  "password": "super-secret-legacy-pw",
                  "method": "aes-256-gcm"
                }
              ],
              "subscriptions": [
                {
                  "id": "sub-1",
                  "name": "LegacySub",
                  "url": "https://sub.example.com/api/v1/client/subscribe?token=secret999"
                }
              ],
              "seats": []
            }
            """);

            var store = new AgentProxyHub.Core.Storage.HubStateStore(tmpDir);
            var doc = store.LoadOrCreateDefault();

            // 1. 内存中必须已透明解密并可供转发内核零开销读取
            Assert.Equal("super-secret-legacy-pw", doc.Nodes[0].Password);
            Assert.Equal("https://sub.example.com/api/v1/client/subscribe?token=secret999", doc.Subscriptions[0].Url);

            // 2. 磁盘上应已自动完成迁移加密，绝不包含明文密码与订阅 Token
            string rawOnDisk = File.ReadAllText(statePath);
            Assert.DoesNotContain("super-secret-legacy-pw", rawOnDisk, StringComparison.Ordinal);
            Assert.DoesNotContain("secret999", rawOnDisk, StringComparison.Ordinal);
            Assert.Contains("enc:v1:", rawOnDisk, StringComparison.Ordinal);

            // 3. 脱敏副本不得向前端/导出接口返回密码
            var redacted = doc.Nodes[0].ToRedactedCopy();
            Assert.Null(redacted.Password);
            Assert.True(redacted.HasCredential);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void Importer_SupportsHostPortAtUserPass_ProfileTitle_AndFlagsInsecureUnapproved()
    {
        string raw = "38.154.227.14:1080@alice:pass123#US-Home\ntrojan://pw@jp.example.com:443?allowInsecure=1#JP-Insecure";
        var parsed = ProxyNodeImporter.Parse(raw, ProxyProtocol.SOCKS5, "测试组");
        Assert.Equal(2, parsed.ValidCount);

        var homeNode = parsed.Items[0].Node!;
        Assert.Equal("38.154.227.14", homeNode.Host);
        Assert.Equal(1080, homeNode.Port);
        Assert.Equal("alice", homeNode.Username);
        Assert.Equal("pass123", homeNode.Password);
        Assert.True(homeNode.InsecureApproved);

        var insecureNode = parsed.Items[1].Node!;
        Assert.True(insecureNode.AllowInsecure);
        Assert.False(insecureNode.InsecureApproved);

        string titleB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("高速住宅专线池"));
        var headers = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["profile-title"] = [$"base64:{titleB64}"]
        };
        string title = ProxyNodeImporter.ExtractSubscriptionName(headers, new Uri("https://sub.example.com/sub?token=abc"));
        Assert.Equal("高速住宅专线池", title);
    }

    [Fact]
    public void SubscriptionSync_SnapshotDrift_PreservesBoundSeatNode()
    {
        var oldBoundNode = new ProxyNode
        {
            Id = "n-bound",
            Name = "US-01",
            Group = "机场池",
            Protocol = ProxyProtocol.SOCKS5,
            Host = "38.154.227.14",
            Port = 1080,
            Username = "u1",
            Password = "p1",
            SourceSubscriptionId = "sub_1"
        };
        var doc = new HubStateDocument
        {
            Nodes = [oldBoundNode],
            Seats =
            [
                new AppSeat { Id = "claude", Name = "Claude", LocalPort = 17891, BoundNodeId = "n-bound", Enabled = true }
            ],
            Subscriptions =
            [
                new SubscriptionSource { Id = "sub_1", Name = "机场池", Group = "机场池", Url = "https://sub.example.com/s" }
            ]
        };

        // 模拟订阅刷新：原节点密码改变，触发快照防漂移保护
        var changedIncoming = new ProxyNode
        {
            Id = "n-new",
            Name = "US-01",
            Group = "机场池",
            Protocol = ProxyProtocol.SOCKS5,
            Host = "38.154.227.14",
            Port = 1080,
            Username = "u1",
            Password = "p2-changed"
        };

        var diff = SubscriptionSyncEngine.MergeParsedNodes(doc, doc.Subscriptions[0], [changedIncoming]);
        Assert.Equal(1, diff.DriftCount);
        Assert.True(doc.Seats[0].HasNodeDrift);
        Assert.Equal("p1", oldBoundNode.Password);
        Assert.Contains("[快照锁定]", oldBoundNode.Name);
        Assert.Null(oldBoundNode.SourceSubscriptionId);
        Assert.Equal(2, doc.Nodes.Count);
    }

    [Fact]
    public void HubEventLogger_RedactsBearerTokensUrlsAndEncryptedBlobs()
    {
        string raw = "Fetch https://alice:secretPass99@sub.example.com/config?token=abc12345 failed with Authorization: Bearer sk-live-9876543210abcdef and blob enc:v1:AAAA1234/+==";
        string sanitized = AgentProxyHub.Core.Diagnostics.HubEventLogger.RedactSecrets(raw);

        Assert.DoesNotContain("secretPass99", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("abc12345", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-9876543210abcdef", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("AAAA1234", sanitized, StringComparison.Ordinal);
        Assert.Contains("://***:***@", sanitized, StringComparison.Ordinal);
        Assert.Contains("Bearer [REDACTED]", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionScanner_MergesAntigravityCliWithoutDoubleCounting_AndCalculatesCacheSavingsAndHeatmap()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"aph_cli_hm_{Guid.NewGuid():N}");
        string desktopDir = Path.Combine(tempRoot, ".gemini", "antigravity", "brain", "conv-shared", ".system_generated", "logs");
        string cliSharedDir = Path.Combine(tempRoot, ".gemini", "antigravity-cli", "brain", "conv-shared", ".system_generated", "logs");
        string cliOnlyDir = Path.Combine(tempRoot, ".gemini", "antigravity-cli", "brain", "conv-cli-only", ".system_generated", "logs");
        Directory.CreateDirectory(desktopDir);
        Directory.CreateDirectory(cliSharedDir);
        Directory.CreateDirectory(cliOnlyDir);

        try
        {
            string todayIso = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            string lineShared = $"{{\"step_index\":2,\"type\":\"PLANNER_RESPONSE\",\"created_at\":\"{todayIso}\",\"model\":\"gemini-2.5-pro\",\"input_tokens\":100000,\"cache_read_tokens\":1000000,\"output_tokens\":2000}}";
            string lineCliOnly = $"{{\"step_index\":1,\"type\":\"PLANNER_RESPONSE\",\"created_at\":\"{todayIso}\",\"model\":\"gemini-2.5-pro\",\"input_tokens\":50000,\"cache_read_tokens\":0,\"output_tokens\":1000}}";

            File.WriteAllLines(Path.Combine(desktopDir, "transcript.jsonl"), [lineShared]);
            File.WriteAllLines(Path.Combine(cliSharedDir, "transcript.jsonl"), [lineShared]);
            File.WriteAllLines(Path.Combine(cliOnlyDir, "transcript.jsonl"), [lineCliOnly]);

            var scanner = new SessionLogScanner(tempRoot);
            var pricing = new ModelPricingCatalog();
            var stats = new UsageStatsService(scanner, pricing);

            var sync = stats.SyncNow();
            Assert.Equal(2, sync.ParsedEntries); // 共享会话不双计 + CLI 独占会话纳入

            var dash = stats.QueryDashboard("today", "all", "all", 1, 20, []);
            Assert.Equal(2, dash.TotalRequests);
            Assert.True(dash.TotalCacheSavedUsd > 0.9); // gemini-2.5-pro: (1.25 - 0.31) * 1M / 1M = 0.94
            Assert.Equal(365, dash.ActivityHeatmap.Count);
            Assert.True(dash.ActiveDaysLastYear >= 1);
            Assert.Contains(dash.ActivityHeatmap, c => c.Requests == 2 && c.Level > 0);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void DefaultSeats_Contains21BuiltInApps_And_TakeoverSupportsNewIdes()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"aph_21apps_{Guid.NewGuid():N}");
        string hubDir = Path.Combine(tempRoot, ".agent-proxy-hub");
        string roaming = Path.Combine(tempRoot, "AppData", "Roaming");
        string local = Path.Combine(tempRoot, "AppData", "Local");
        Directory.CreateDirectory(hubDir);
        Directory.CreateDirectory(roaming);
        Directory.CreateDirectory(local);

        try
        {
            var store = new HubStateStore(hubDir);
            var doc = store.LoadOrCreateDefault();
            Assert.Equal(21, doc.Seats.Count);

            string[] addedIds = ["windsurf", "kiro", "vscode-copilot", "zed", "trae", "codebuddy"];
            foreach (string id in addedIds)
            {
                Assert.Contains(doc.Seats, s => s.Id == id);
            }

            // 预置 Windsurf 与 Zed 已有设置，验证无损接管保留原有配置并可精准还原
            string windsurfSettings = Path.Combine(roaming, "Windsurf", "User", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(windsurfSettings)!);
            File.WriteAllText(windsurfSettings, "{\n  \"editor.fontSize\": 15\n}");

            string zedSettings = Path.Combine(tempRoot, ".config", "zed", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(zedSettings)!);
            File.WriteAllText(zedSettings, "{\n  \"theme\": \"One Dark\"\n}");

            var takeover = new AppProxyTakeoverService(tempRoot, roaming, local, _ => false, forceMacOs: false);

            var wsSeat = doc.Seats.First(s => s.Id == "windsurf");
            var (wsOk, _, wsStatus) = takeover.SetTakeover(wsSeat, true);
            Assert.True(wsOk);
            Assert.True(wsStatus.IsTakenOver);
            string wsWritten = File.ReadAllText(windsurfSettings);
            Assert.Contains($"http://127.0.0.1:{wsSeat.LocalPort}", wsWritten);
            Assert.Contains("15", wsWritten);

            var (wsRestoreOk, _, wsRestoredStatus) = takeover.SetTakeover(wsSeat, false);
            Assert.True(wsRestoreOk);
            Assert.False(wsRestoredStatus.IsTakenOver);
            string wsRestored = File.ReadAllText(windsurfSettings);
            Assert.DoesNotContain("http.proxy", wsRestored);
            Assert.Contains("15", wsRestored);

            var zedSeat = doc.Seats.First(s => s.Id == "zed");
            var (zedOk, _, zedStatus) = takeover.SetTakeover(zedSeat, true);
            Assert.True(zedOk);
            Assert.True(zedStatus.IsTakenOver);
            string zedWritten = File.ReadAllText(zedSettings);
            Assert.Contains($"http://127.0.0.1:{zedSeat.LocalPort}", zedWritten);
            Assert.Contains("One Dark", zedWritten);

            var (zedRestoreOk, _, _) = takeover.SetTakeover(zedSeat, false);
            Assert.True(zedRestoreOk);
            Assert.DoesNotContain($"127.0.0.1:{zedSeat.LocalPort}", File.ReadAllText(zedSettings));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task AppInstanceManager_AllocatesUniquePorts_And_AntigravityRestartIsHardBlocked()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"aph_inst_{Guid.NewGuid():N}");
        string hubDir = Path.Combine(tempRoot, ".agent-proxy-hub");
        Directory.CreateDirectory(hubDir);

        try
        {
            var store = new HubStateStore(hubDir);
            var doc = store.LoadOrCreateDefault();
            var takeover = new AppProxyTakeoverService(tempRoot, tempRoot, tempRoot, _ => false, forceMacOs: false);
            var mgr = new AppInstanceManager(store, takeover);

            string p1Dir = Path.Combine(tempRoot, "profiles", "c1");
            string p2Dir = Path.Combine(tempRoot, "profiles", "c2");
            var inst1 = mgr.CreateInstance(doc, "cursor", "工作账号", p1Dir, "direct-phy");
            var inst2 = mgr.CreateInstance(doc, "cursor", "个人账号", p2Dir, "direct-phy");

            Assert.NotEqual(inst1.LocalPort, inst2.LocalPort);
            Assert.True(Directory.Exists(p1Dir));
            Assert.True(Directory.Exists(p2Dir));

            var baseCursor = doc.Seats.First(s => s.Id == "cursor");
            var vSeat = inst1.ToVirtualSeat(baseCursor);
            Assert.Equal($"cursor:inst:{inst1.Id}", vSeat.Id);
            Assert.True(vSeat.IsInstance);
            Assert.Equal(inst1.LocalPort, vSeat.LocalPort);

            // 验证 Antigravity 席位严禁强制重启（防误杀当前宿主 IDE 会话）
            var agySeat = doc.Seats.First(s => s.Id == "agy");
            var (restartOk, restartMsg) = await takeover.GracefulRestartWithProxyAsync(agySeat);
            Assert.False(restartOk);
            Assert.Contains("Antigravity", restartMsg);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task SingBoxSidecarManager_ImportOfflinePackage_VerifiesSha256_And_BlocksZipSlip()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"aph_sb_imp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            using var sidecar = new SingBoxSidecarManager(tempRoot);

            // 1. 构造含 Zip-Slip 恶意目录穿越条目的压缩包并验证拦截
            string evilZip = Path.Combine(tempRoot, "evil.zip");
            using (var zip = System.IO.Compression.ZipFile.Open(evilZip, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("../../evil.exe");
                using var sw = new StreamWriter(entry.Open());
                sw.Write("evil");
            }
            var (evilOk, evilMsg, _) = await sidecar.ImportOfflinePackageAsync(evilZip);
            Assert.False(evilOk);
            Assert.Contains("Zip-Slip", evilMsg);

            // 2. 构造合法 sing-box 压缩包并检验 SHA-256 严格比对
            string binName = OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box";
            string goodZip = Path.Combine(tempRoot, "good-singbox.zip");
            using (var zip = System.IO.Compression.ZipFile.Open(goodZip, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry($"sing-box-1.11.0/{binName}");
                using var sw = new StreamWriter(entry.Open());
                sw.Write("MZ-fake-sing-box-payload-for-sha256-test");
            }

            string wrongSha = new('0', 64);
            var (badShaOk, badShaMsg, _) = await sidecar.ImportOfflinePackageAsync(goodZip, wrongSha);
            Assert.False(badShaOk);
            Assert.Contains("SHA-256", badShaMsg);

            string actualZipSha = SingBoxSidecarManager.ComputeFileSha256Safe(goodZip)!;
            var (goodOk, _, goodStatus) = await sidecar.ImportOfflinePackageAsync(goodZip, actualZipSha);
            Assert.True(goodOk);
            Assert.True(goodStatus.Installed);
            Assert.False(string.IsNullOrWhiteSpace(goodStatus.BinarySha256));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void WebDavSyncService_EncryptAndDecryptDocument_RoundTripsCrossMachine_And_RejectsWrongPassphrase()
    {
        var doc = new HubStateDocument
        {
            Nodes =
            [
                new ProxyNode
                {
                    Id = "n-dav-1",
                    Name = "Tokyo-Static",
                    Protocol = ProxyProtocol.SOCKS5,
                    Host = "203.0.113.88",
                    Port = 1080,
                    Username = "alice",
                    Password = "node-s3cr3t-password"
                }
            ],
            Instances =
            [
                new ManagedAppInstance
                {
                    Id = "inst-work1",
                    BaseSeatId = "cursor",
                    Name = "工作账号",
                    ProfileDir = @"C:\Profiles\Work",
                    LocalPort = 17920,
                    BoundNodeId = "n-dav-1"
                }
            ],
            Preferences = new HubUiPreferences
            {
                CloseAction = "minimize_to_tray",
                WebDavUrl = "https://dav.example.com/dav/",
                WebDavPassword = "master-webdav-password",
                WebDavSyncPassphrase = "sync-passphrase-2026"
            }
        };

        string encrypted = WebDavSyncService.EncryptDocument(doc, "sync-passphrase-2026");
        Assert.DoesNotContain("node-s3cr3t-password", encrypted, StringComparison.Ordinal);
        Assert.DoesNotContain("master-webdav-password", encrypted, StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() =>
            WebDavSyncService.DecryptDocument(encrypted, "wrong-passphrase"));

        var restored = WebDavSyncService.DecryptDocument(encrypted, "sync-passphrase-2026");
        Assert.Single(restored.Nodes);
        Assert.Equal("node-s3cr3t-password", restored.Nodes[0].Password);
        Assert.Single(restored.Instances);
        Assert.Equal("工作账号", restored.Instances[0].Name);
        Assert.Null(restored.Preferences.WebDavPassword);
    }

    [Fact]
    public void SessionScanner_ExtractsWorkspaceAndBuildsConversationAudit_ForClaudeAndCodex()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"aph_audit_{Guid.NewGuid():N}");
        string claudeProj = Path.Combine(tempRoot, ".claude", "projects", "proj-demo");
        Directory.CreateDirectory(claudeProj);

        try
        {
            string t1 = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("yyyy-MM-ddTHH:mm:ssZ");
            string t2 = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("yyyy-MM-ddTHH:mm:ssZ");
            string sessionFile = Path.Combine(claudeProj, "conv-audit-99.jsonl");
            File.WriteAllLines(sessionFile,
            [
                $"{{\"timestamp\":\"{t1}\",\"cwd\":\"F:\\\\source\\\\Agent_Proxy_Hub\",\"message\":{{\"id\":\"m1\",\"model\":\"claude-sonnet-4-5\",\"content\":[{{\"type\":\"tool_use\",\"name\":\"Read\"}}],\"usage\":{{\"input_tokens\":1000,\"cache_read_input_tokens\":2000,\"output_tokens\":200}}}}}}",
                $"{{\"timestamp\":\"{t2}\",\"cwd\":\"F:\\\\source\\\\Agent_Proxy_Hub\",\"message\":{{\"id\":\"m2\",\"model\":\"claude-sonnet-4-5\",\"content\":[{{\"type\":\"tool_use\",\"name\":\"Edit\"}},{{\"type\":\"tool_use\",\"name\":\"Bash\"}}],\"usage\":{{\"input_tokens\":2500,\"cache_read_input_tokens\":15000,\"output_tokens\":500}}}}}}"
            ]);

            var scanner = new SessionLogScanner(tempRoot);
            var stats = new UsageStatsService(scanner, new ModelPricingCatalog());
            stats.SyncNow();

            var dash = stats.QueryDashboard("all", "all", "all", 1, 20, []);
            Assert.NotEmpty(dash.WorkspaceBreakdown);
            Assert.Contains(dash.WorkspaceBreakdown, w => w.Workspace.Contains("Agent_Proxy_Hub", StringComparison.OrdinalIgnoreCase) && w.Requests == 2);

            var audit = stats.GetConversationAudit("claude:conv-audit-99");
            Assert.NotNull(audit);
            Assert.Equal(2, audit!.TotalTurns);
            Assert.Equal(17500, audit.PeakContextTokens);
            Assert.True(audit.ContextGrowthRatio > 4.0);
            Assert.Contains(audit.ToolBreakdown, t => t.ToolName == "Read" && t.Calls == 1);
            Assert.Contains(audit.ToolBreakdown, t => t.ToolName == "Edit" && t.Calls == 1);
            Assert.Contains(audit.ToolBreakdown, t => t.ToolName == "Bash" && t.Calls == 1);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }
}
