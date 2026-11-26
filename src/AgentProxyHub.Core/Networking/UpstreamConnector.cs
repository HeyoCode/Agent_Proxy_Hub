using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using AgentProxyHub.Core.Models;

namespace AgentProxyHub.Core.Networking;

/// <summary>
/// 物理网卡直绑上游拨号器：支持 SOCKS5/5h、SOCKS4/4a、HTTP CONNECT、HTTPS TLS 及物理直连
/// </summary>
public sealed class UpstreamConnector
{
    private readonly PhysicalNicManager _nicManager;
    private readonly CleanDnsResolver _dnsResolver;
    private readonly SingBoxSidecarManager? _sidecar;

    public UpstreamConnector(PhysicalNicManager nicManager, CleanDnsResolver dnsResolver, SingBoxSidecarManager? sidecar = null)
    {
        _nicManager = nicManager;
        _dnsResolver = dnsResolver;
        _sidecar = sidecar;
    }

    public PhysicalNicManager NicManager => _nicManager;
    public SingBoxSidecarManager? Sidecar => _sidecar;

    public static bool IsIpv6Literal(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        string clean = host.Trim().Trim('[', ']');
        return IPAddress.TryParse(clean, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetworkV6;
    }

    /// <summary>
    /// 双跳链式代理拨号：前置加速中转节点 (L1/L2) -> 落地干净代理节点 (L1) -> 目标 AI 服务器
    /// </summary>
    public async Task<Stream> ConnectChainAsync(
        ProxyNode? relayNode,
        ProxyNode landingNode,
        string targetHost,
        int targetPort,
        IsolationSettings settings,
        CancellationToken ct = default)
    {
        if (relayNode == null ||
            relayNode.Id == landingNode.Id ||
            relayNode.Protocol == ProxyProtocol.DIRECT_PHYSICAL ||
            landingNode.Protocol == ProxyProtocol.DIRECT_PHYSICAL)
        {
            return await ConnectViaNodeAsync(landingNode, targetHost, targetPort, settings, ct);
        }

        if (landingNode.RequiresSidecarEngine)
        {
            throw new NotSupportedException("VLESS / Hysteria2 / TUIC / WireGuard 请作为「前置中转」或直连出口使用，二级落地请搭配 SOCKS5 / HTTP / Trojan / Shadowsocks");
        }

        Stream relayStream = await ConnectViaNodeAsync(relayNode, landingNode.Host, landingNode.Port, settings, ct);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            return await HandshakeOverTransportAsync(relayStream, landingNode, targetHost, targetPort, timeoutCts.Token);
        }
        catch
        {
            relayStream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 通过物理网卡连接指定代理节点，并让该节点代为连向 targetHost:targetPort
    /// </summary>
    public async Task<Stream> ConnectViaNodeAsync(
        ProxyNode node,
        string targetHost,
        int targetPort,
        IsolationSettings settings,
        CancellationToken ct = default)
    {
        if (!settings.EnableIpv6 && IsIpv6Literal(targetHost))
            throw new InvalidOperationException("IPv6 开关已关闭，已阻断 IPv6 目标连接");

        if (node.Protocol == ProxyProtocol.DIRECT_PHYSICAL)
        {
            return await ConnectDirectPhysicalAsync(targetHost, targetPort, settings, ct);
        }

        // L2 现代高抗干扰协议（VLESS / Hysteria2 / TUIC / WireGuard）自动路由至受控 sing-box 本地环回桥
        if (node.RequiresSidecarEngine)
        {
            return await ConnectViaSidecarBridgeAsync(node, targetHost, targetPort, settings, ct);
        }

        string cleanProxyHost = node.Host.Trim().Trim('[', ']');
        IPAddress? proxyIp = IPAddress.TryParse(cleanProxyHost, out var ip) ? ip : null;
        if (proxyIp != null)
        {
            if (proxyIp.AddressFamily == AddressFamily.InterNetworkV6 && !settings.EnableIpv6)
                throw new InvalidOperationException("IPv6 开关已关闭，无法连接 IPv6 代理节点");
        }
        else
        {
            if (settings.DirectPhysicalDohEnabled)
            {
                proxyIp = await _dnsResolver.ResolveHostAsync(cleanProxyHost, settings.DohServerIp, settings.EnableIpv6, ct);
            }
            else if (!settings.EnableIpv6)
            {
                proxyIp = (await Dns.GetHostAddressesAsync(cleanProxyHost, AddressFamily.InterNetwork, ct)).FirstOrDefault()
                          ?? throw new InvalidOperationException($"无法解析代理节点域名: {cleanProxyHost}");
            }
            else
            {
                var all = await Dns.GetHostAddressesAsync(cleanProxyHost, ct);
                proxyIp = all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                          ?? all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6)
                          ?? throw new InvalidOperationException($"无法解析代理节点域名: {cleanProxyHost}");
            }
        }

        var socket = _nicManager.CreateBoundTcpSocket(proxyIp.AddressFamily, proxyIp);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(new IPEndPoint(proxyIp, node.Port), timeoutCts.Token);

            Stream transport = new NetworkStream(socket, ownsSocket: true);
            return await HandshakeOverTransportAsync(transport, node, targetHost, targetPort, timeoutCts.Token);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task<Stream> ConnectViaSidecarBridgeAsync(
        ProxyNode node,
        string targetHost,
        int targetPort,
        IsolationSettings settings,
        CancellationToken ct)
    {
        if (_sidecar == null)
            throw new InvalidOperationException("sing-box 扩展引擎未初始化");

        int? bridgePort = _sidecar.GetBridgePort(node.Id);
        if (!bridgePort.HasValue)
        {
            string? boundNic = _nicManager.GetLockedAdapter()?.Name;
            if (!_sidecar.EnsureEphemeralNodesReady([node], boundNic, settings, out string? err))
                throw new InvalidOperationException(err ?? "请先在「网卡隔离」页一键安装 sing-box 扩展内核");
            bridgePort = _sidecar.GetBridgePort(node.Id)
                ?? throw new InvalidOperationException("sing-box 本地桥接端口尚未就绪");
        }

        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(IPAddress.Loopback, bridgePort.Value, timeoutCts.Token);
            Stream stream = client.GetStream();
            var bridgeNode = new ProxyNode { Protocol = ProxyProtocol.SOCKS5, Host = "127.0.0.1", Port = bridgePort.Value };
            await HandshakeSocks5Async(stream, bridgeNode, targetHost, targetPort, timeoutCts.Token);
            return stream;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<Stream> HandshakeOverTransportAsync(
        Stream transport,
        ProxyNode node,
        string targetHost,
        int targetPort,
        CancellationToken ct)
    {
        switch (node.Protocol)
        {
            case ProxyProtocol.DIRECT_PHYSICAL:
                return transport;

            case ProxyProtocol.SOCKS5:
                await HandshakeSocks5Async(transport, node, targetHost, targetPort, ct);
                return transport;

            case ProxyProtocol.SOCKS4:
                await HandshakeSocks4Async(transport, targetHost, targetPort, ct);
                return transport;

            case ProxyProtocol.HTTP:
                await HandshakeHttpConnectAsync(transport, node, targetHost, targetPort, ct);
                return transport;

            case ProxyProtocol.HTTPS:
            {
                var ssl = CreateClientSslStream(transport, node);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = string.IsNullOrWhiteSpace(node.Sni) ? node.Host : node.Sni
                }, ct);
                await HandshakeHttpConnectAsync(ssl, node, targetHost, targetPort, ct);
                return ssl;
            }

            case ProxyProtocol.TROJAN:
            {
                var ssl = CreateClientSslStream(transport, node);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = string.IsNullOrWhiteSpace(node.Sni) ? node.Host : node.Sni
                }, ct);
                byte[] header = NativeCryptoProtocols.BuildTrojanConnectHeader(node.Password ?? string.Empty, targetHost, targetPort);
                await ssl.WriteAsync(header, ct);
                await ssl.FlushAsync(ct);
                return ssl;
            }

            case ProxyProtocol.SHADOWSOCKS:
            {
                var ssStream = new ShadowsocksAeadStream(
                    transport,
                    string.IsNullOrWhiteSpace(node.Method) ? "aes-256-gcm" : node.Method,
                    node.Password ?? string.Empty);
                byte[] addrHdr = NativeCryptoProtocols.EncodeSocksAddress(targetHost, targetPort);
                await ssStream.WriteAsync(addrHdr, ct);
                await ssStream.FlushAsync(ct);
                return ssStream;
            }

            default:
                throw new NotSupportedException($"不支持的代理协议: {node.Protocol}");
        }
    }

    private static SslStream CreateClientSslStream(Stream transport, ProxyNode node)
    {
        if (node.AllowInsecure)
        {
            return new SslStream(transport, leaveInnerStreamOpen: false, (_, _, _, _) => true);
        }
        return new SslStream(transport, leaveInnerStreamOpen: false);
    }

    /// <summary>
    /// 物理网卡直接连接远端目标（不使用上游代理，仅用于显式选择的 DIRECT_PHYSICAL 场景或节点测试）
    /// </summary>
    public async Task<Stream> ConnectDirectPhysicalAsync(string host, int port, IsolationSettings settings, CancellationToken ct = default)
    {
        string cleanHost = host.Trim().Trim('[', ']');
        if (!settings.EnableIpv6 && IsIpv6Literal(cleanHost))
            throw new InvalidOperationException("IPv6 开关已关闭，已阻断 IPv6 直连");

        IPAddress ip = IPAddress.TryParse(cleanHost, out var parsed)
            ? parsed
            : await _dnsResolver.ResolveHostAsync(cleanHost, settings.DohServerIp, settings.EnableIpv6, ct);

        var socket = _nicManager.CreateBoundTcpSocket(ip.AddressFamily, ip);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(new IPEndPoint(ip, port), timeoutCts.Token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task HandshakeSocks5Async(Stream stream, ProxyNode node, string targetHost, int targetPort, CancellationToken ct)
    {
        bool hasAuth = !string.IsNullOrEmpty(node.Username);
        byte[] greeting = hasAuth ? [0x05, 0x02, 0x00, 0x02] : [0x05, 0x01, 0x00];
        await stream.WriteAsync(greeting, ct);
        await stream.FlushAsync(ct);

        byte[] methodResp = new byte[2];
        await ReadExactAsync(stream, methodResp, ct);
        if (methodResp[0] != 0x05 || methodResp[1] == 0xFF)
            throw new IOException("上游 SOCKS5 代理拒绝认证协商");

        if (methodResp[1] == 0x02)
        {
            byte[] uBytes = Encoding.UTF8.GetBytes(node.Username ?? string.Empty);
            byte[] pBytes = Encoding.UTF8.GetBytes(node.Password ?? string.Empty);
            byte[] authReq = new byte[3 + uBytes.Length + pBytes.Length];
            authReq[0] = 0x01;
            authReq[1] = (byte)uBytes.Length;
            uBytes.CopyTo(authReq, 2);
            authReq[2 + uBytes.Length] = (byte)pBytes.Length;
            pBytes.CopyTo(authReq, 3 + uBytes.Length);

            await stream.WriteAsync(authReq, ct);
            await stream.FlushAsync(ct);

            byte[] authResp = new byte[2];
            await ReadExactAsync(stream, authResp, ct);
            if (authResp[1] != 0x00)
                throw new UnauthorizedAccessException("上游 SOCKS5 用户名或密码认证失败");
        }

        // 组装 CONNECT 请求（SOCKS5h：域名采用 ATYP=0x03 由远端解析）
        byte[] addrPayload;
        if (IPAddress.TryParse(targetHost, out var targetIp))
        {
            byte[] ipBytes = targetIp.GetAddressBytes();
            byte atyp = targetIp.AddressFamily == AddressFamily.InterNetwork ? (byte)0x01 : (byte)0x04;
            addrPayload = new byte[4 + ipBytes.Length + 2];
            addrPayload[0] = 0x05;
            addrPayload[1] = 0x01; // CONNECT
            addrPayload[2] = 0x00;
            addrPayload[3] = atyp;
            ipBytes.CopyTo(addrPayload, 4);
            BinaryPrimitives.WriteUInt16BigEndian(addrPayload.AsSpan(4 + ipBytes.Length, 2), (ushort)targetPort);
        }
        else
        {
            byte[] hostBytes = Encoding.ASCII.GetBytes(targetHost);
            if (hostBytes.Length > 255) throw new ArgumentException("域名过长");
            addrPayload = new byte[4 + 1 + hostBytes.Length + 2];
            addrPayload[0] = 0x05;
            addrPayload[1] = 0x01;
            addrPayload[2] = 0x00;
            addrPayload[3] = 0x03; // DOMAINNAME (SOCKS5h)
            addrPayload[4] = (byte)hostBytes.Length;
            hostBytes.CopyTo(addrPayload, 5);
            BinaryPrimitives.WriteUInt16BigEndian(addrPayload.AsSpan(5 + hostBytes.Length, 2), (ushort)targetPort);
        }

        await stream.WriteAsync(addrPayload, ct);
        await stream.FlushAsync(ct);

        byte[] hdr = new byte[4];
        await ReadExactAsync(stream, hdr, ct);
        if (hdr[1] != 0x00)
            throw new IOException($"上游 SOCKS5 连接目标失败，返回码: 0x{hdr[1]:X2}");

        int skipLen = hdr[3] switch
        {
            0x01 => 4 + 2,
            0x04 => 16 + 2,
            0x03 => 1,
            _ => throw new IOException("上游 SOCKS5 返回未知地址类型")
        };

        if (hdr[3] == 0x03)
        {
            byte[] lenB = new byte[1];
            await ReadExactAsync(stream, lenB, ct);
            skipLen = lenB[0] + 2;
        }
        byte[] discard = new byte[skipLen];
        await ReadExactAsync(stream, discard, ct);
    }

    private static async Task HandshakeSocks4Async(Stream stream, string targetHost, int targetPort, CancellationToken ct)
    {
        bool isIp = IPAddress.TryParse(targetHost, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork;
        using var ms = new MemoryStream(128);
        ms.WriteByte(0x04);
        ms.WriteByte(0x01); // CONNECT
        Span<byte> portBuf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBuf, (ushort)targetPort);
        ms.Write(portBuf);

        if (isIp && ip != null)
        {
            ms.Write(ip.GetAddressBytes());
            ms.WriteByte(0x00);
        }
        else
        {
            // SOCKS4a: 0.0.0.1 + 域名
            ms.Write(new byte[] { 0, 0, 0, 1 });
            ms.WriteByte(0x00);
            ms.Write(Encoding.ASCII.GetBytes(targetHost));
            ms.WriteByte(0x00);
        }

        await stream.WriteAsync(ms.ToArray(), ct);
        await stream.FlushAsync(ct);

        byte[] resp = new byte[8];
        await ReadExactAsync(stream, resp, ct);
        if (resp[1] != 0x5A)
            throw new IOException($"上游 SOCKS4 连接失败，状态码: 0x{resp[1]:X2}");
    }

    private static async Task HandshakeHttpConnectAsync(Stream stream, ProxyNode node, string targetHost, int targetPort, CancellationToken ct)
    {
        string cleanTarget = targetHost.Trim().Trim('[', ']');
        string formattedHost = IsIpv6Literal(cleanTarget) ? $"[{cleanTarget}]" : cleanTarget;
        var sb = new StringBuilder();
        sb.Append($"CONNECT {formattedHost}:{targetPort} HTTP/1.1\r\n");
        sb.Append($"Host: {formattedHost}:{targetPort}\r\n");
        sb.Append("Proxy-Connection: Keep-Alive\r\n");
        if (!string.IsNullOrEmpty(node.Username))
        {
            string token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{node.Username}:{node.Password ?? string.Empty}"));
            sb.Append($"Proxy-Authorization: Basic {token}\r\n");
        }
        sb.Append("\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
        await stream.FlushAsync(ct);

        string headerText = await ReadHttpHeaderAsync(stream, ct);
        int lineEnd = headerText.IndexOf('\n');
        string statusLine = lineEnd > 0 ? headerText[..lineEnd].Trim() : headerText.Trim();
        if (!statusLine.Contains(" 200 ", StringComparison.Ordinal))
            throw new IOException($"上游 HTTP CONNECT 代理拒绝请求: {statusLine}");
    }

    private static async Task<string> ReadHttpHeaderAsync(Stream stream, CancellationToken ct)
    {
        var ms = new MemoryStream(512);
        var buf = new byte[1];
        int match = 0;
        while (ms.Length < 16384)
        {
            int n = await stream.ReadAsync(buf, ct);
            if (n <= 0) throw new EndOfStreamException("上游代理响应中断");
            ms.WriteByte(buf[0]);
            if ((match == 0 || match == 2) && buf[0] == '\r') match++;
            else if ((match == 1 || match == 3) && buf[0] == '\n') match++;
            else match = buf[0] == '\r' ? 1 : 0;

            if (match == 4) break;
        }
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    public static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (n <= 0) throw new EndOfStreamException("代理握手流意外断开");
            offset += n;
        }
    }
}
