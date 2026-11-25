using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace AgentProxyHub.Core.Networking;

/// <summary>
/// TLS ClientHello SNI 域名无损嗅探器与 Clash Fake-IP (198.18.0.0/15) 识别工具
/// </summary>
public static class TlsSniInspector
{
    /// <summary>
    /// 判断目标主机是否为 Clash / Surge 常用的 RFC2544 Fake-IP 段 (198.18.0.0/15)
    /// </summary>
    public static bool IsFakeIp(string host)
    {
        if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        Span<byte> bytes = stackalloc byte[4];
        if (!ip.TryWriteBytes(bytes, out _))
            return false;

        return bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19);
    }

    /// <summary>
    /// 尝试从 TLS ClientHello 首包中提取 SNI 主机名（不消耗也不修改原始字节流）
    /// </summary>
    public static bool TryExtractSni(ReadOnlySpan<byte> data, out string? serverName)
    {
        serverName = null;
        if (data.Length < 43)
            return false;

        // TLS Record Header: ContentType=0x16 (Handshake)
        if (data[0] != 0x16)
            return false;

        // Handshake Header starts at offset 5: HandshakeType=0x01 (ClientHello)
        if (data[5] != 0x01)
            return false;

        int pos = 5 + 4; // 跳过 HandshakeType(1) + Length(3)
        pos += 2 + 32;   // 跳过 ClientVersion(2) + Random(32)
        if (pos >= data.Length) return false;

        // Session ID
        int sessionIdLen = data[pos];
        pos += 1 + sessionIdLen;
        if (pos + 2 > data.Length) return false;

        // Cipher Suites
        int cipherSuitesLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
        pos += 2 + cipherSuitesLen;
        if (pos + 1 > data.Length) return false;

        // Compression Methods
        int compLen = data[pos];
        pos += 1 + compLen;
        if (pos + 2 > data.Length) return false;

        // Extensions total length
        int extTotalLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
        pos += 2;
        int extEnd = Math.Min(data.Length, pos + extTotalLen);

        while (pos + 4 <= extEnd)
        {
            ushort extType = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
            ushort extLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos + 2, 2));
            pos += 4;
            if (pos + extLen > extEnd) break;

            if (extType == 0x0000 && extLen >= 5) // server_name extension
            {
                int listLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
                int sniPos = pos + 2;
                int sniEnd = Math.Min(pos + extLen, sniPos + listLen);

                while (sniPos + 3 <= sniEnd)
                {
                    byte nameType = data[sniPos];
                    ushort nameLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(sniPos + 1, 2));
                    sniPos += 3;
                    if (sniPos + nameLen > sniEnd) break;

                    if (nameType == 0x00 && nameLen > 0)
                    {
                        serverName = Encoding.ASCII.GetString(data.Slice(sniPos, nameLen));
                        return !string.IsNullOrWhiteSpace(serverName);
                    }
                    sniPos += nameLen;
                }
            }

            pos += extLen;
        }

        return false;
    }
}
