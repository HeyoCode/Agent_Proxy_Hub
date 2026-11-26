using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace AgentProxyHub.Core.Networking;

/// <summary>
/// 绑定物理网卡的轻量独立 DNS 解析器（仅在代理节点地址为域名时绕过系统/Clash DNS，支持按 IPv6 开关解析 A / AAAA）
/// </summary>
public sealed class CleanDnsResolver
{
    private readonly PhysicalNicManager _nicManager;
    private readonly ConcurrentDictionary<string, (IPAddress Ip, DateTimeOffset ExpiresAt)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CleanDnsResolver(PhysicalNicManager nicManager)
    {
        _nicManager = nicManager;
    }

    public async Task<IPAddress> ResolveHostAsync(
        string host,
        string dnsServerIp = "223.5.5.5",
        bool enableIpv6 = false,
        CancellationToken ct = default)
    {
        string cleanHost = host.Trim().Trim('[', ']');
        if (IPAddress.TryParse(cleanHost, out var parsed))
        {
            if (parsed.AddressFamily == AddressFamily.InterNetworkV6 && !enableIpv6)
                throw new InvalidOperationException("IPv6 开关已关闭，已阻断 IPv6 连接");
            return parsed;
        }

        string cacheKey = $"{cleanHost}|v{(enableIpv6 ? 6 : 4)}";
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Ip;

        if (IPAddress.TryParse(dnsServerIp, out var dnsIp) &&
            (dnsIp.AddressFamily == AddressFamily.InterNetwork || enableIpv6))
        {
            // 1. 优先尝试物理网卡直连 UDP 53 查询（极低延迟）
            try
            {
                var resolved = await QueryDnsRecordDirectAsync(cleanHost, dnsIp, 1, ct)
                               ?? (enableIpv6 ? await QueryDnsRecordDirectAsync(cleanHost, dnsIp, 28, ct) : null);
                if (resolved != null && !IsFakeIpAddress(resolved))
                {
                    _cache[cacheKey] = (resolved, DateTimeOffset.UtcNow.AddMinutes(5));
                    return resolved;
                }
            }
            catch
            {
                // 企业内网屏蔽外网 UDP 53 时自动进入下方物理直连 HTTPS DoH
            }

            // 2. 物理网卡直连 HTTPS DoH (RFC 8484 TCP 443) 兜底，绕过 UDP 53 拦截与 Clash Fake-IP 污染
            try
            {
                var dohResolved = await QueryDnsOverHttpsDirectAsync(cleanHost, dnsIp, 1, ct)
                                  ?? (enableIpv6 ? await QueryDnsOverHttpsDirectAsync(cleanHost, dnsIp, 28, ct) : null);
                if (dohResolved != null && !IsFakeIpAddress(dohResolved))
                {
                    _cache[cacheKey] = (dohResolved, DateTimeOffset.UtcNow.AddMinutes(5));
                    return dohResolved;
                }
            }
            catch
            {
                // DoH 不可达时继续尝试系统解析并过滤 Fake-IP
            }
        }

        IPAddress? fallback;
        if (!enableIpv6)
        {
            var v4List = await Dns.GetHostAddressesAsync(cleanHost, AddressFamily.InterNetwork, ct);
            fallback = v4List.FirstOrDefault(a => !IsFakeIpAddress(a));
        }
        else
        {
            var all = await Dns.GetHostAddressesAsync(cleanHost, ct);
            fallback = all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IsFakeIpAddress(a))
                       ?? all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !IsFakeIpAddress(a));
        }

        if (fallback == null)
            throw new InvalidOperationException($"无法解析干净主机地址（或结果被 Fake-IP 污染）: {cleanHost}");

        _cache[cacheKey] = (fallback, DateTimeOffset.UtcNow.AddMinutes(2));
        return fallback;
    }

    private static bool IsFakeIpAddress(IPAddress ip) =>
        ip.AddressFamily == AddressFamily.InterNetwork && TlsSniInspector.IsFakeIp(ip.ToString());

    private async Task<IPAddress?> QueryDnsOverHttpsDirectAsync(string domain, IPAddress dohIp, ushort qType, CancellationToken ct)
    {
        byte[] wireQuery = BuildDnsQuery(domain, qType, out ushort txId);
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = async (ctx, token) =>
            {
                var sock = _nicManager.CreateBoundTcpSocket(dohIp.AddressFamily, dohIp);
                try
                {
                    await sock.ConnectAsync(new IPEndPoint(dohIp, 443), token);
                    return new NetworkStream(sock, ownsSocket: true);
                }
                catch
                {
                    sock.Dispose();
                    throw;
                }
            }
        };

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
        string uri = dohIp.AddressFamily == AddressFamily.InterNetworkV6
            ? $"https://[{dohIp}]/dns-query"
            : $"https://{dohIp}/dns-query";
        using var req = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(wireQuery)
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        if (!resp.IsSuccessStatusCode) return null;
        byte[] wireResp = await resp.Content.ReadAsByteArrayAsync(ct);
        return ParseDnsResponse(wireResp, txId, qType);
    }

    private async Task<IPAddress?> QueryDnsRecordDirectAsync(string domain, IPAddress dnsIp, ushort qType, CancellationToken ct)
    {
        byte[] query = BuildDnsQuery(domain, qType, out ushort txId);
        using var udp = _nicManager.CreateBoundUdpSocket(dnsIp.AddressFamily);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(1800));

        var remoteEp = new IPEndPoint(dnsIp, 53);
        await udp.SendToAsync(query, SocketFlags.None, remoteEp, timeoutCts.Token);

        var recvBuf = new byte[512];
        var result = await udp.ReceiveFromAsync(recvBuf, SocketFlags.None, remoteEp, timeoutCts.Token);
        return ParseDnsResponse(recvBuf.AsSpan(0, result.ReceivedBytes), txId, qType);
    }

    private static byte[] BuildDnsQuery(string domain, ushort qType, out ushort txId)
    {
        txId = (ushort)Random.Shared.Next(1, 65535);
        using var ms = new MemoryStream(128);
        Span<byte> hdr = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(hdr[0..2], txId);
        BinaryPrimitives.WriteUInt16BigEndian(hdr[2..4], 0x0100); // RD=1
        BinaryPrimitives.WriteUInt16BigEndian(hdr[4..6], 1);      // QDCOUNT=1
        ms.Write(hdr);

        foreach (var label in domain.Trim('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0);

        Span<byte> qTail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(qTail[0..2], qType); // TYPE A=1 / AAAA=28
        BinaryPrimitives.WriteUInt16BigEndian(qTail[2..4], 1);     // CLASS IN
        ms.Write(qTail);

        return ms.ToArray();
    }

    private static IPAddress? ParseDnsResponse(ReadOnlySpan<byte> resp, ushort expectedTxId, ushort expectedType)
    {
        if (resp.Length < 12) return null;
        if (BinaryPrimitives.ReadUInt16BigEndian(resp[0..2]) != expectedTxId) return null;

        ushort qdCount = BinaryPrimitives.ReadUInt16BigEndian(resp[4..6]);
        ushort anCount = BinaryPrimitives.ReadUInt16BigEndian(resp[6..8]);
        int pos = 12;

        for (int i = 0; i < qdCount && pos < resp.Length; i++)
        {
            pos = SkipDnsName(resp, pos);
            pos += 4;
        }

        for (int i = 0; i < anCount && pos + 12 <= resp.Length; i++)
        {
            pos = SkipDnsName(resp, pos);
            if (pos + 10 > resp.Length) break;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(resp.Slice(pos, 2));
            ushort rdLen = BinaryPrimitives.ReadUInt16BigEndian(resp.Slice(pos + 8, 2));
            pos += 10;
            if (pos + rdLen > resp.Length) break;

            if (type == expectedType)
            {
                if (expectedType == 1 && rdLen == 4)
                    return new IPAddress(resp.Slice(pos, 4));
                if (expectedType == 28 && rdLen == 16)
                    return new IPAddress(resp.Slice(pos, 16));
            }
            pos += rdLen;
        }

        return null;
    }

    private static int SkipDnsName(ReadOnlySpan<byte> buf, int pos)
    {
        while (pos < buf.Length)
        {
            byte len = buf[pos];
            if (len == 0) return pos + 1;
            if ((len & 0xC0) == 0xC0) return pos + 2;
            pos += 1 + len;
        }
        return pos;
    }
}

/// <summary>
/// 本地管理 API 环回安全守卫：拦截 DNS Rebinding 非法 Host 头与外部网页跨站写操作 (CSRF)
/// </summary>
public static class LoopbackSecurityGuard
{
    public static bool IsAllowedRequest(string? hostHeader, string method, string? originHeader, bool isApiRoute)
    {
        if (!string.IsNullOrWhiteSpace(hostHeader))
        {
            string hostOnly = ExtractHostWithoutPort(hostHeader.Trim());
            if (!IsLoopbackHost(hostOnly))
                return false;
        }

        string m = (method ?? "GET").Trim().ToUpperInvariant();
        bool isWriteMethod = m is not ("GET" or "HEAD" or "OPTIONS");
        if (isApiRoute && isWriteMethod && !string.IsNullOrWhiteSpace(originHeader))
        {
            string trimmedOrigin = originHeader.Trim();
            if (trimmedOrigin.Equals("null", StringComparison.OrdinalIgnoreCase))
                return false;
            if (Uri.TryCreate(trimmedOrigin, UriKind.Absolute, out var originUri))
            {
                if (!IsLoopbackHost(originUri.Host))
                    return false;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private static string ExtractHostWithoutPort(string rawHost)
    {
        if (rawHost.StartsWith('['))
        {
            int end = rawHost.IndexOf(']');
            return end > 1 ? rawHost[1..end] : rawHost;
        }
        int colon = rawHost.IndexOf(':');
        return colon > 0 ? rawHost[..colon] : rawHost;
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("[::1]", StringComparison.OrdinalIgnoreCase);
}
