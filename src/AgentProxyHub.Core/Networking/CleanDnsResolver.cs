using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AgentProxyHub.Core.Networking;

/// <summary>
/// 绑定物理网卡的轻量独立 DNS 解析器（仅在代理节点地址为域名时绕过系统/Clash DNS）
/// </summary>
public sealed class CleanDnsResolver
{
    private readonly PhysicalNicManager _nicManager;
    private readonly ConcurrentDictionary<string, (IPAddress Ip, DateTimeOffset ExpiresAt)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CleanDnsResolver(PhysicalNicManager nicManager)
    {
        _nicManager = nicManager;
    }

    public async Task<IPAddress> ResolveHostAsync(string host, string dnsServerIp = "223.5.5.5", CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var parsed))
            return parsed;

        if (_cache.TryGetValue(host, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Ip;

        try
        {
            if (IPAddress.TryParse(dnsServerIp, out var dnsIp))
            {
                var resolved = await QueryARecordDirectAsync(host, dnsIp, ct);
                if (resolved != null)
                {
                    _cache[host] = (resolved, DateTimeOffset.UtcNow.AddMinutes(5));
                    return resolved;
                }
            }
        }
        catch
        {
            // 若企业内网屏蔽外网 UDP 53，则降级系统解析兜底
        }

        var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct);
        var fallback = addresses.FirstOrDefault()
                       ?? throw new InvalidOperationException($"无法解析主机地址: {host}");
        _cache[host] = (fallback, DateTimeOffset.UtcNow.AddMinutes(2));
        return fallback;
    }

    private async Task<IPAddress?> QueryARecordDirectAsync(string domain, IPAddress dnsIp, CancellationToken ct)
    {
        byte[] query = BuildDnsAQuery(domain, out ushort txId);
        using var udp = _nicManager.CreateBoundUdpSocket();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(1800));

        var remoteEp = new IPEndPoint(dnsIp, 53);
        await udp.SendToAsync(query, SocketFlags.None, remoteEp, timeoutCts.Token);

        var recvBuf = new byte[512];
        var result = await udp.ReceiveFromAsync(recvBuf, SocketFlags.None, remoteEp, timeoutCts.Token);
        return ParseDnsAResponse(recvBuf.AsSpan(0, result.ReceivedBytes), txId);
    }

    private static byte[] BuildDnsAQuery(string domain, out ushort txId)
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
        BinaryPrimitives.WriteUInt16BigEndian(qTail[0..2], 1); // TYPE A
        BinaryPrimitives.WriteUInt16BigEndian(qTail[2..4], 1); // CLASS IN
        ms.Write(qTail);

        return ms.ToArray();
    }

    private static IPAddress? ParseDnsAResponse(ReadOnlySpan<byte> resp, ushort expectedTxId)
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

            if (type == 1 && rdLen == 4)
            {
                return new IPAddress(resp.Slice(pos, 4));
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
