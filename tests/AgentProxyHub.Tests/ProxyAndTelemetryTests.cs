using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AgentProxyHub.Core.Apps;
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
}
