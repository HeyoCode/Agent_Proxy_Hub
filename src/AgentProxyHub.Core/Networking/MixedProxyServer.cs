using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AgentProxyHub.Core.Models;

namespace AgentProxyHub.Core.Networking;

/// <summary>
/// 多端口混合入站代理引擎：
/// - 首字节自动识别 SOCKS5 / SOCKS4 / HTTP CONNECT / HTTP
/// - 实时记录上下行流量字节、并发连接、Fake-IP SNI 还原数与熔断拦截数
/// - 严格执行 Fail-Close 防串 IP 熔断锁
/// </summary>
public sealed class MixedProxyServer : IDisposable
{
    private sealed class SeatListenerState
    {
        public required AppSeat Seat { get; set; }
        public int BoundConfigPort { get; set; } = -1;
        public bool BoundIpv6 { get; set; }
        public TcpListener? Listener { get; set; }
        public TcpListener? ListenerV6 { get; set; }
        public CancellationTokenSource? Cts { get; set; }
        public long ActiveConnections;
        public long TotalConnections;
        public long BytesTx;
        public long BytesRx;
        public long LastSampleBytesTx;
        public long LastSampleBytesRx;
        public long LastSampleTimestamp = Stopwatch.GetTimestamp();
        public long BytesTxPerSec;
        public long BytesRxPerSec;
        public long SniRecoveredCount;
        public long KillSwitchBlockedCount;
        public long Ipv6BlockedCount;
    }

    private readonly UpstreamConnector _connector;
    private readonly ConcurrentDictionary<string, SeatListenerState> _seats = new();
    private readonly object _configLock = new();
    private readonly CancellationTokenSource _rateSamplerCts = new();
    private List<ProxyNode> _nodes = new();
    private IsolationSettings _isolation = new();
    private bool _disposed;

    public MixedProxyServer(UpstreamConnector connector)
    {
        _connector = connector;
        _ = RunRateSamplerLoopAsync(_rateSamplerCts.Token);
    }

    public void UpdateTopology(IReadOnlyList<AppSeat> seats, IReadOnlyList<ProxyNode> nodes, IsolationSettings isolation)
    {
        lock (_configLock)
        {
            bool ipv6Changed = _isolation.EnableIpv6 != isolation.EnableIpv6;
            _nodes = nodes.ToList();
            _isolation = isolation;

            var targetIds = seats.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var existingId in _seats.Keys.ToList())
            {
                if (!targetIds.Contains(existingId) && _seats.TryRemove(existingId, out var removed))
                {
                    StopListener(removed);
                }
            }

            foreach (var seat in seats)
            {
                var st = _seats.GetOrAdd(seat.Id, _ => new SeatListenerState { Seat = seat });
                bool portChanged = st.BoundConfigPort != seat.LocalPort;
                bool v6Mismatch = st.BoundIpv6 != isolation.EnableIpv6;
                st.Seat = seat;

                if (!seat.Enabled || seat.LocalPort < 0)
                {
                    StopListener(st);
                }
                else if (st.Listener == null || portChanged || v6Mismatch || ipv6Changed)
                {
                    StopListener(st);
                    StartListener(st);
                }
            }

            // 懒加载同步 L2 sing-box 扩展引擎：仅启动被活跃席位（主节点或前置中转）引用的 L2 节点
            if (_connector.Sidecar != null)
            {
                var neededL2Ids = seats
                    .Where(s => s.Enabled)
                    .SelectMany(s => new[] { s.BoundNodeId, s.RelayNodeId })
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet(StringComparer.Ordinal);
                var neededL2Nodes = _nodes
                    .Where(n => n.Enabled && n.RequiresSidecarEngine && neededL2Ids.Contains(n.Id))
                    .ToList();
                _ = _connector.Sidecar.EnsureNodesReady(
                    neededL2Nodes,
                    _connector.NicManager.GetLockedAdapter()?.Name,
                    isolation,
                    out _);
            }
        }
    }

    /// <summary>
    /// 获取某个已监听席位的实际绑定端口（传入 0 动态端口测试时很有用）
    /// </summary>
    public int? GetBoundPort(string seatId)
    {
        if (_seats.TryGetValue(seatId, out var st) && st.Listener?.LocalEndpoint is IPEndPoint ep)
            return ep.Port;
        return null;
    }

    public List<SeatTrafficSnapshot> GetTrafficSnapshots()
    {
        List<ProxyNode> nodesSnapshot;
        lock (_configLock) nodesSnapshot = _nodes.ToList();

        var result = new List<SeatTrafficSnapshot>();
        foreach (var st in _seats.Values.OrderBy(x => x.Seat.LocalPort))
        {
            var bound = nodesSnapshot.FirstOrDefault(n => n.Id == st.Seat.BoundNodeId);
            result.Add(new SeatTrafficSnapshot
            {
                SeatId = st.Seat.Id,
                AppName = st.Seat.Name,
                LocalPort = st.Listener?.LocalEndpoint is IPEndPoint ep ? ep.Port : st.Seat.LocalPort,
                BoundNodeName = bound?.Name ?? "未绑定线路",
                ExitIp = bound?.ExitIp ?? "—",
                ActiveConnections = Interlocked.Read(ref st.ActiveConnections),
                TotalConnections = Interlocked.Read(ref st.TotalConnections),
                BytesTx = Interlocked.Read(ref st.BytesTx),
                BytesRx = Interlocked.Read(ref st.BytesRx),
                BytesTxPerSec = Interlocked.Read(ref st.BytesTxPerSec),
                BytesRxPerSec = Interlocked.Read(ref st.BytesRxPerSec),
                SniRecoveredCount = Interlocked.Read(ref st.SniRecoveredCount),
                KillSwitchBlockedCount = Interlocked.Read(ref st.KillSwitchBlockedCount),
                Ipv6BlockedCount = Interlocked.Read(ref st.Ipv6BlockedCount)
            });
        }
        return result;
    }

    /// <summary>
    /// 计算各个席位的实时上下行速率（字节/秒），默认由每秒后台采样器自动触发，也可由单元测试显式驱动
    /// </summary>
    public void SampleThroughputRates()
    {
        long nowTs = Stopwatch.GetTimestamp();
        foreach (var st in _seats.Values)
        {
            long prevTs = Interlocked.Read(ref st.LastSampleTimestamp);
            double elapsedSec = (nowTs - prevTs) / (double)Stopwatch.Frequency;
            if (elapsedSec < 0.05) continue;

            long curTx = Interlocked.Read(ref st.BytesTx);
            long curRx = Interlocked.Read(ref st.BytesRx);
            long prevTx = Interlocked.Exchange(ref st.LastSampleBytesTx, curTx);
            long prevRx = Interlocked.Exchange(ref st.LastSampleBytesRx, curRx);
            Interlocked.Exchange(ref st.LastSampleTimestamp, nowTs);

            long deltaTx = Math.Max(0, curTx - prevTx);
            long deltaRx = Math.Max(0, curRx - prevRx);
            Interlocked.Exchange(ref st.BytesTxPerSec, (long)Math.Round(deltaTx / elapsedSec));
            Interlocked.Exchange(ref st.BytesRxPerSec, (long)Math.Round(deltaRx / elapsedSec));
        }
    }

    private async Task RunRateSamplerLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                SampleThroughputRates();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    private bool IsIpv6Enabled()
    {
        lock (_configLock) return _isolation.EnableIpv6;
    }

    private void StartListener(SeatListenerState st)
    {
        if (_disposed) return;
        var listener = new TcpListener(IPAddress.Loopback, st.Seat.LocalPort == 0 ? 0 : st.Seat.LocalPort);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();

        var cts = new CancellationTokenSource();
        st.Listener = listener;
        st.BoundConfigPort = st.Seat.LocalPort;
        st.BoundIpv6 = _isolation.EnableIpv6;
        st.Cts = cts;

        _ = Task.Run(() => AcceptLoopAsync(st, listener, cts.Token), cts.Token);

        // 开启 IPv6 开关时同步在 [::1] 环回启动双栈伴随监听（绝不暴露到局域网）
        if (_isolation.EnableIpv6 && Socket.OSSupportsIPv6)
        {
            try
            {
                int boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                var listenerV6 = new TcpListener(IPAddress.IPv6Loopback, boundPort);
                listenerV6.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listenerV6.Start();
                st.ListenerV6 = listenerV6;
                _ = Task.Run(() => AcceptLoopAsync(st, listenerV6, cts.Token), cts.Token);
            }
            catch
            {
                // 若系统禁用了 IPv6 环回，则优雅降级仅保留 127.0.0.1 监听
                st.ListenerV6 = null;
            }
        }
    }

    private static void StopListener(SeatListenerState st)
    {
        try { st.Cts?.Cancel(); } catch { }
        try { st.Listener?.Stop(); } catch { }
        try { st.ListenerV6?.Stop(); } catch { }
        st.Cts?.Dispose();
        st.Listener = null;
        st.ListenerV6 = null;
        st.BoundConfigPort = -1;
        st.Cts = null;
    }

    private async Task AcceptLoopAsync(SeatListenerState st, TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(st, client, ct), ct);
        }
    }

    private async Task HandleClientAsync(SeatListenerState st, TcpClient client, CancellationToken ct)
    {
        Interlocked.Increment(ref st.ActiveConnections);
        Interlocked.Increment(ref st.TotalConnections);
        using (client)
        {
            client.NoDelay = true;
            try
            {
                using var clientStream = client.GetStream();
                byte[] firstByte = new byte[1];
                int n;
                using (var firstByteCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    firstByteCts.CancelAfter(TimeSpan.FromSeconds(15));
                    n = await clientStream.ReadAsync(firstByte, firstByteCts.Token);
                }
                if (n <= 0) return;

                if (firstByte[0] == 0x05)
                {
                    await HandleSocks5ClientAsync(st, clientStream, ct);
                }
                else if (firstByte[0] == 0x04)
                {
                    await HandleSocks4ClientAsync(st, clientStream, ct);
                }
                else
                {
                    await HandleHttpClientAsync(st, clientStream, firstByte[0], ct);
                }
            }
            catch
            {
                // 客户端主动断开或连接超时
            }
            finally
            {
                Interlocked.Decrement(ref st.ActiveConnections);
            }
        }
    }

    private async Task HandleSocks5ClientAsync(SeatListenerState st, NetworkStream clientStream, CancellationToken ct)
    {
        byte[] nMethodsBuf = new byte[1];
        await UpstreamConnector.ReadExactAsync(clientStream, nMethodsBuf, ct);
        byte[] methods = new byte[nMethodsBuf[0]];
        await UpstreamConnector.ReadExactAsync(clientStream, methods, ct);

        // 本地端口免认证返回 0x00
        await clientStream.WriteAsync(new byte[] { 0x05, 0x00 }, ct);
        await clientStream.FlushAsync(ct);

        byte[] reqHdr = new byte[4];
        await UpstreamConnector.ReadExactAsync(clientStream, reqHdr, ct);
        if (reqHdr[0] != 0x05 || reqHdr[1] != 0x01)
        {
            await clientStream.WriteAsync(new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
            return;
        }

        string targetHost;
        switch (reqHdr[3])
        {
            case 0x01:
                byte[] ipv4 = new byte[4];
                await UpstreamConnector.ReadExactAsync(clientStream, ipv4, ct);
                targetHost = new IPAddress(ipv4).ToString();
                break;
            case 0x03:
                byte[] lenB = new byte[1];
                await UpstreamConnector.ReadExactAsync(clientStream, lenB, ct);
                byte[] dom = new byte[lenB[0]];
                await UpstreamConnector.ReadExactAsync(clientStream, dom, ct);
                targetHost = Encoding.ASCII.GetString(dom);
                break;
            case 0x04:
                byte[] ipv6 = new byte[16];
                await UpstreamConnector.ReadExactAsync(clientStream, ipv6, ct);
                targetHost = new IPAddress(ipv6).ToString();
                break;
            default:
                await clientStream.WriteAsync(new byte[] { 0x05, 0x08, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
                return;
        }

        byte[] portBuf = new byte[2];
        await UpstreamConnector.ReadExactAsync(clientStream, portBuf, ct);
        int targetPort = BinaryPrimitives.ReadUInt16BigEndian(portBuf);

        // IPv6 关闭时快速拒绝 IPv6 目标，避免 Happy Eyeballs 长时间等待或回退泄漏
        if (!IsIpv6Enabled() && (reqHdr[3] == 0x04 || UpstreamConnector.IsIpv6Literal(targetHost)))
        {
            Interlocked.Increment(ref st.Ipv6BlockedCount);
            await clientStream.WriteAsync(new byte[] { 0x05, 0x08, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
            await clientStream.FlushAsync(ct);
            return;
        }

        // 若命中 Fake-IP 段且开启 SNI 还原：先回复 SOCKS5 成功并预读首包以提取 SNI 真实域名
        byte[]? initialPayload = null;
        if (_isolation.TlsSniRecoveryEnabled && TlsSniInspector.IsFakeIp(targetHost))
        {
            await clientStream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
            await clientStream.FlushAsync(ct);

            (targetHost, initialPayload) = await TryRecoverSniFromInitialPacketAsync(st, clientStream, targetHost, ct);
            using var upstream = await DialUpstreamOrBlockAsync(st, targetHost, targetPort, ct);
            if (upstream == null) return;

            if (initialPayload is { Length: > 0 })
            {
                await upstream.WriteAsync(initialPayload, ct);
                await upstream.FlushAsync(ct);
                Interlocked.Add(ref st.BytesTx, initialPayload.Length);
            }
            await PumpBidirectionalAsync(st, clientStream, upstream, ct);
            return;
        }

        using var directUpstream = await DialUpstreamOrBlockAsync(st, targetHost, targetPort, ct);
        if (directUpstream == null)
        {
            await clientStream.WriteAsync(new byte[] { 0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
            return;
        }

        await clientStream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
        await clientStream.FlushAsync(ct);
        await PumpBidirectionalAsync(st, clientStream, directUpstream, ct);
    }

    private async Task HandleSocks4ClientAsync(SeatListenerState st, NetworkStream clientStream, CancellationToken ct)
    {
        byte[] hdr = new byte[7]; // cmd(1) + port(2) + ip(4)
        await UpstreamConnector.ReadExactAsync(clientStream, hdr, ct);
        if (hdr[0] != 0x01)
        {
            await clientStream.WriteAsync(new byte[] { 0x00, 0x5B, 0, 0, 0, 0, 0, 0 }, ct);
            return;
        }

        int port = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(1, 2));
        bool isSocks4a = hdr[3] == 0 && hdr[4] == 0 && hdr[5] == 0 && hdr[6] != 0;
        _ = await ReadNullTerminatedAsciiAsync(clientStream, ct); // userid

        string targetHost = isSocks4a
            ? await ReadNullTerminatedAsciiAsync(clientStream, ct)
            : new IPAddress(hdr.AsSpan(3, 4)).ToString();

        using var upstream = await DialUpstreamOrBlockAsync(st, targetHost, port, ct);
        if (upstream == null)
        {
            await clientStream.WriteAsync(new byte[] { 0x00, 0x5B, 0, 0, 0, 0, 0, 0 }, ct);
            return;
        }

        await clientStream.WriteAsync(new byte[] { 0x00, 0x5A, 0, 0, 0, 0, 0, 0 }, ct);
        await clientStream.FlushAsync(ct);
        await PumpBidirectionalAsync(st, clientStream, upstream, ct);
    }

    private async Task HandleHttpClientAsync(SeatListenerState st, NetworkStream clientStream, byte firstByte, CancellationToken ct)
    {
        var ms = new MemoryStream(512);
        ms.WriteByte(firstByte);
        var b = new byte[1];
        int match = 0;
        while (ms.Length < 32768)
        {
            int n = await clientStream.ReadAsync(b, ct);
            if (n <= 0) return;
            ms.WriteByte(b[0]);
            if ((match == 0 || match == 2) && b[0] == '\r') match++;
            else if ((match == 1 || match == 3) && b[0] == '\n') match++;
            else match = b[0] == '\r' ? 1 : 0;
            if (match == 4) break;
        }

        string rawHeaders = Encoding.ASCII.GetString(ms.ToArray());
        int firstCrLf = rawHeaders.IndexOf("\r\n", StringComparison.Ordinal);
        if (firstCrLf <= 0) return;

        string requestLine = rawHeaders[..firstCrLf];
        string[] parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return;

        string method = parts[0].ToUpperInvariant();
        string target = parts[1];

        if (method == "CONNECT")
        {
            (string host, int port) = ParseHostPort(target, 443);

            if (!IsIpv6Enabled() && UpstreamConnector.IsIpv6Literal(host))
            {
                Interlocked.Increment(ref st.Ipv6BlockedCount);
                await clientStream.WriteAsync("HTTP/1.1 502 Bad Gateway (IPv6 Disabled)\r\n\r\n"u8.ToArray(), ct);
                await clientStream.FlushAsync(ct);
                return;
            }

            if (_isolation.TlsSniRecoveryEnabled && TlsSniInspector.IsFakeIp(host))
            {
                await clientStream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct);
                await clientStream.FlushAsync(ct);

                (host, byte[]? initialPayload) = await TryRecoverSniFromInitialPacketAsync(st, clientStream, host, ct);
                using var upstreamSni = await DialUpstreamOrBlockAsync(st, host, port, ct);
                if (upstreamSni == null) return;

                if (initialPayload is { Length: > 0 })
                {
                    await upstreamSni.WriteAsync(initialPayload, ct);
                    await upstreamSni.FlushAsync(ct);
                    Interlocked.Add(ref st.BytesTx, initialPayload.Length);
                }
                await PumpBidirectionalAsync(st, clientStream, upstreamSni, ct);
                return;
            }

            using var upstream = await DialUpstreamOrBlockAsync(st, host, port, ct);
            if (upstream == null)
            {
                await clientStream.WriteAsync("HTTP/1.1 502 Bad Gateway (Kill Switch Blocked)\r\n\r\n"u8.ToArray(), ct);
                return;
            }

            await clientStream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct);
            await clientStream.FlushAsync(ct);
            await PumpBidirectionalAsync(st, clientStream, upstream, ct);
        }
        else
        {
            // 普通明文 HTTP 代理请求（如 http://example.com/path）
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
                return;

            string host = uri.Host.Trim('[', ']');
            int port = uri.Port > 0 ? uri.Port : 80;

            if (!IsIpv6Enabled() && UpstreamConnector.IsIpv6Literal(host))
            {
                Interlocked.Increment(ref st.Ipv6BlockedCount);
                await clientStream.WriteAsync("HTTP/1.1 502 Bad Gateway (IPv6 Disabled)\r\n\r\n"u8.ToArray(), ct);
                await clientStream.FlushAsync(ct);
                return;
            }

            using var upstream = await DialUpstreamOrBlockAsync(st, host, port, ct);
            if (upstream == null)
            {
                await clientStream.WriteAsync("HTTP/1.1 502 Bad Gateway (Kill Switch Blocked)\r\n\r\n"u8.ToArray(), ct);
                return;
            }

            string rewritten = RewriteHttpProxyHeader(rawHeaders, uri);
            byte[] headerBytes = Encoding.ASCII.GetBytes(rewritten);
            await upstream.WriteAsync(headerBytes, ct);
            await upstream.FlushAsync(ct);
            Interlocked.Add(ref st.BytesTx, headerBytes.Length);
            await PumpBidirectionalAsync(st, clientStream, upstream, ct);
        }
    }

    private async Task<(string Host, byte[]? InitialPayload)> TryRecoverSniFromInitialPacketAsync(
        SeatListenerState st,
        NetworkStream clientStream,
        string originalHost,
        CancellationToken ct)
    {
        var buf = new byte[4096];
        using var peekCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        peekCts.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            int read = await clientStream.ReadAsync(buf, peekCts.Token);
            if (read <= 0) return (originalHost, null);

            byte[] payload = buf.AsSpan(0, read).ToArray();
            if (TlsSniInspector.TryExtractSni(payload, out string? sni) && !string.IsNullOrWhiteSpace(sni))
            {
                Interlocked.Increment(ref st.SniRecoveredCount);
                return (sni!, payload);
            }
            return (originalHost, payload);
        }
        catch (OperationCanceledException)
        {
            return (originalHost, null);
        }
    }

    private async Task<Stream?> DialUpstreamOrBlockAsync(SeatListenerState st, string targetHost, int targetPort, CancellationToken ct)
    {
        List<ProxyNode> candidateNodes;
        ProxyNode? relayNode;
        IsolationSettings iso;
        AppSeat seat = st.Seat;
        bool killSwitch = seat.KillSwitchEnabled || _isolation.GlobalKillSwitch;

        lock (_configLock)
        {
            iso = _isolation;
            relayNode = string.IsNullOrWhiteSpace(seat.RelayNodeId)
                ? null
                : _nodes.FirstOrDefault(n => n.Id == seat.RelayNodeId && n.Enabled);
            var primary = _nodes.FirstOrDefault(n => n.Id == seat.BoundNodeId && n.Enabled);
            candidateNodes = new List<ProxyNode>();
            if (primary != null) candidateNodes.Add(primary);
            if (seat.AllowSameGroupFailover && primary != null)
            {
                candidateNodes.AddRange(_nodes.Where(n =>
                    n.Id != primary.Id &&
                    n.Enabled &&
                    n.Group == primary.Group &&
                    n.Status != "offline"));
            }
        }

        if (candidateNodes.Count == 0)
        {
            if (killSwitch)
            {
                Interlocked.Increment(ref st.KillSwitchBlockedCount);
                return null;
            }
            return await _connector.ConnectDirectPhysicalAsync(targetHost, targetPort, iso, ct);
        }

        foreach (var node in candidateNodes)
        {
            try
            {
                if (relayNode != null && relayNode.Id != node.Id)
                    return await _connector.ConnectChainAsync(relayNode, node, targetHost, targetPort, iso, ct);
                return await _connector.ConnectViaNodeAsync(node, targetHost, targetPort, iso, ct);
            }
            catch
            {
                // 尝试同组下一备用节点
            }
        }

        // 所有绑定专线失败：执行 Fail-Close 熔断锁，绝不回退直连或 Clash
        if (killSwitch)
        {
            Interlocked.Increment(ref st.KillSwitchBlockedCount);
            return null;
        }

        return await _connector.ConnectDirectPhysicalAsync(targetHost, targetPort, iso, ct);
    }

    private static async Task PumpBidirectionalAsync(SeatListenerState st, Stream client, Stream upstream, CancellationToken ct)
    {
        var tClientToUp = CopyWithCountAsync(client, upstream, bytes => Interlocked.Add(ref st.BytesTx, bytes), halfCloseWriteOnEof: true, ct);
        var tUpToClient = CopyWithCountAsync(upstream, client, bytes => Interlocked.Add(ref st.BytesRx, bytes), halfCloseWriteOnEof: true, ct);

        var firstDone = await Task.WhenAny(tClientToUp, tUpToClient);
        var remaining = firstDone == tClientToUp ? tUpToClient : tClientToUp;

        // 给另一方向保留短暂尾包排空窗口，避免客户端发送完请求半关闭后服务端最后一段响应被截断
        try
        {
            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await Task.WhenAny(remaining, Task.Delay(TimeSpan.FromSeconds(3), drainCts.Token));
            drainCts.Cancel();
        }
        catch { }
    }

    private static async Task CopyWithCountAsync(
        Stream src,
        Stream dst,
        Action<long> onBytes,
        bool halfCloseWriteOnEof,
        CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(32768);
        bool needExplicitFlush = dst is not NetworkStream;
        try
        {
            while (true)
            {
                int n = await src.ReadAsync(buffer, ct);
                if (n <= 0)
                {
                    if (halfCloseWriteOnEof && dst is NetworkStream dstNet)
                    {
                        try { dstNet.Socket.Shutdown(SocketShutdown.Send); } catch { }
                    }
                    break;
                }
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                if (needExplicitFlush)
                {
                    await dst.FlushAsync(ct);
                }
                onBytes(n);
            }
        }
        catch
        {
            // 对端关闭或取消
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string RewriteHttpProxyHeader(string rawHeaders, Uri uri)
    {
        var lines = rawHeaders.Split("\r\n", StringSplitOptions.None).ToList();
        if (lines.Count == 0) return rawHeaders;
        var firstParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (firstParts.Length >= 3)
        {
            lines[0] = $"{firstParts[0]} {uri.PathAndQuery} {firstParts[2]}";
        }
        lines = lines.Where(l => !l.StartsWith("Proxy-Connection:", StringComparison.OrdinalIgnoreCase)).ToList();
        return string.Join("\r\n", lines);
    }

    private static (string Host, int Port) ParseHostPort(string target, int defaultPort)
    {
        if (target.StartsWith('['))
        {
            int endBracket = target.IndexOf(']');
            if (endBracket > 1)
            {
                string ip6 = target[1..endBracket];
                if (endBracket + 2 <= target.Length && int.TryParse(target[(endBracket + 2)..], out int p6))
                    return (ip6, p6);
                return (ip6, defaultPort);
            }
        }
        int colon = target.LastIndexOf(':');
        if (colon > 0 && int.TryParse(target[(colon + 1)..], out int port))
        {
            return (target[..colon], port);
        }
        return (target, defaultPort);
    }

    private static async Task<string> ReadNullTerminatedAsciiAsync(Stream stream, CancellationToken ct)
    {
        var ms = new MemoryStream(64);
        var b = new byte[1];
        while (ms.Length < 512)
        {
            int n = await stream.ReadAsync(b, ct);
            if (n <= 0 || b[0] == 0x00) break;
            ms.WriteByte(b[0]);
        }
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _rateSamplerCts.Cancel(); } catch { }
        _rateSamplerCts.Dispose();
        foreach (var st in _seats.Values)
        {
            StopListener(st);
        }
        _seats.Clear();
    }
}
