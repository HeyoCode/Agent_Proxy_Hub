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

    public UpstreamConnector(PhysicalNicManager nicManager, CleanDnsResolver dnsResolver)
    {
        _nicManager = nicManager;
        _dnsResolver = dnsResolver;
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
        IPAddress? proxyIp = IPAddress.TryParse(node.Host, out var ip) ? ip : null;
        if (proxyIp == null)
        {
            proxyIp = settings.DirectPhysicalDohEnabled
                ? await _dnsResolver.ResolveHostAsync(node.Host, settings.DohServerIp, ct)
                : (await Dns.GetHostAddressesAsync(node.Host, AddressFamily.InterNetwork, ct)).FirstOrDefault()
                  ?? throw new InvalidOperationException($"无法解析代理节点域名: {node.Host}");
        }

        var socket = _nicManager.CreateBoundTcpSocket(proxyIp.AddressFamily, proxyIp);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(new IPEndPoint(proxyIp, node.Port), timeoutCts.Token);

            Stream transport = new NetworkStream(socket, ownsSocket: true);

            if (node.Protocol == ProxyProtocol.HTTPS)
            {
                var ssl = new SslStream(transport, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = node.Host
                }, timeoutCts.Token);
                transport = ssl;
            }

            switch (node.Protocol)
            {
                case ProxyProtocol.DIRECT_PHYSICAL:
                    return transport;
                case ProxyProtocol.SOCKS5:
                    await HandshakeSocks5Async(transport, node, targetHost, targetPort, timeoutCts.Token);
                    return transport;
                case ProxyProtocol.SOCKS4:
                    await HandshakeSocks4Async(transport, targetHost, targetPort, timeoutCts.Token);
                    return transport;
                case ProxyProtocol.HTTP:
                case ProxyProtocol.HTTPS:
                    await HandshakeHttpConnectAsync(transport, node, targetHost, targetPort, timeoutCts.Token);
                    return transport;
                default:
                    throw new NotSupportedException($"不支持的代理协议: {node.Protocol}");
            }
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 物理网卡直接连接远端目标（不使用上游代理，仅用于显式选择的 DIRECT_PHYSICAL 场景或节点测试）
    /// </summary>
    public async Task<Stream> ConnectDirectPhysicalAsync(string host, int port, IsolationSettings settings, CancellationToken ct = default)
    {
        IPAddress ip = IPAddress.TryParse(host, out var parsed)
            ? parsed
            : await _dnsResolver.ResolveHostAsync(host, settings.DohServerIp, ct);

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
        var sb = new StringBuilder();
        sb.Append($"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\n");
        sb.Append($"Host: {targetHost}:{targetPort}\r\n");
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
