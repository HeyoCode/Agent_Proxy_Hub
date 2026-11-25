using System.Text;
using System.Text.RegularExpressions;
using AgentProxyHub.Core.Models;

namespace AgentProxyHub.Core.Networking;

/// <summary>
/// 多格式代理线路批量导入智能解析引擎（支持 URI、ip:port:user:pass、user:pass@ip:port、ip:port、CSV 及 Base64）
/// </summary>
public static partial class ProxyNodeImporter
{
    [GeneratedRegex(@"^(socks5h?|socks4a?|https?):\/\/(?:([^:@\s]+):([^@\s]+)@)?(\[?[a-zA-Z0-9.:-]+\]?):(\d{1,5})\/?(?:#(.+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"^([^:@\s]+):([^@\s]+)@([a-zA-Z0-9.-]+):(\d{1,5})(?:#(.+))?$")]
    private static partial Regex UserPassAtHostPattern();

    [GeneratedRegex(@"^([a-zA-Z0-9.-]+):(\d{1,5}):([^:\s#]+):([^#\s]+)(?:#(.+))?$")]
    private static partial Regex FourColonPattern();

    [GeneratedRegex(@"^([a-zA-Z0-9.-]+):(\d{1,5})(?:#(.+))?$")]
    private static partial Regex HostPortPattern();

    public static BatchParseResult Parse(
        string rawText,
        ProxyProtocol defaultProtocol = ProxyProtocol.SOCKS5,
        string defaultGroup = "批量导入池",
        IEnumerable<ProxyNode>? existingNodes = null)
    {
        string normalized = TryDecodeBase64Block(rawText);
        var rawLines = normalized
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#') && !l.StartsWith("//"))
            .ToList();

        var existingFingerprints = existingNodes?
            .Select(n => n.Fingerprint)
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

        var seenInBatch = new HashSet<string>(StringComparer.Ordinal);
        var result = new BatchParseResult { TotalLines = rawLines.Count };

        for (int i = 0; i < rawLines.Count; i++)
        {
            string line = rawLines[i];
            int lineNo = i + 1;

            if (TryParseSingleLine(line, defaultProtocol, defaultGroup, out var node, out string? error))
            {
                bool isDup = existingFingerprints.Contains(node!.Fingerprint) || !seenInBatch.Add(node.Fingerprint);
                result.Items.Add(new BatchParseItem
                {
                    LineNumber = lineNo,
                    RawLine = line,
                    Valid = !isDup,
                    IsDuplicate = isDup,
                    ErrorReason = isDup ? "与已有或同批节点重复，已自动跳过" : null,
                    Node = node
                });
                if (isDup) result.DuplicateCount++;
                else result.ValidCount++;
            }
            else
            {
                result.Items.Add(new BatchParseItem
                {
                    LineNumber = lineNo,
                    RawLine = line,
                    Valid = false,
                    IsDuplicate = false,
                    ErrorReason = error ?? $"第 {lineNo} 行无法识别格式"
                });
                result.ErrorCount++;
            }
        }

        return result;
    }

    private static bool TryParseSingleLine(
        string line,
        ProxyProtocol defaultProtocol,
        string defaultGroup,
        out ProxyNode? node,
        out string? error)
    {
        node = null;
        error = null;

        // 1. URI 格式: socks5://user:pass@ip:port#备注
        var mUrl = UrlPattern().Match(line);
        if (mUrl.Success)
        {
            string scheme = mUrl.Groups[1].Value.ToUpperInvariant();
            ProxyProtocol proto = scheme switch
            {
                "SOCKS5" or "SOCKS5H" => ProxyProtocol.SOCKS5,
                "SOCKS4" or "SOCKS4A" => ProxyProtocol.SOCKS4,
                "HTTPS" => ProxyProtocol.HTTPS,
                _ => ProxyProtocol.HTTP
            };
            if (!TryValidPort(mUrl.Groups[5].Value, out int port))
            {
                error = "端口号超出 1~65535 有效范围";
                return false;
            }
            string host = mUrl.Groups[4].Value.Trim('[', ']');
            string? user = NullIfEmpty(Uri.UnescapeDataString(mUrl.Groups[2].Value));
            string? pass = NullIfEmpty(Uri.UnescapeDataString(mUrl.Groups[3].Value));
            string? remark = NullIfEmpty(mUrl.Groups[6].Value);
            node = BuildNode(proto, host, port, user, pass, remark, defaultGroup);
            return true;
        }

        // 2. CSV 格式: proto,host,port,user,pass,name 或 host,port,user,pass
        if (line.Contains(','))
        {
            var cols = line.Split(',').Select(c => c.Trim()).ToArray();
            if (cols.Length >= 2 && int.TryParse(cols[1], out int p2) && p2 is >= 1 and <= 65535)
            {
                node = BuildNode(defaultProtocol, cols[0], p2,
                    cols.ElementAtOrDefault(2), cols.ElementAtOrDefault(3), cols.ElementAtOrDefault(4), defaultGroup);
                return true;
            }
            if (cols.Length >= 3 && Enum.TryParse<ProxyProtocol>(cols[0], true, out var csvProto) &&
                int.TryParse(cols[2], out int p3) && p3 is >= 1 and <= 65535)
            {
                node = BuildNode(csvProto, cols[1], p3,
                    cols.ElementAtOrDefault(3), cols.ElementAtOrDefault(4), cols.ElementAtOrDefault(5), defaultGroup);
                return true;
            }
        }

        // 3. user:pass@ip:port#备注
        var mAt = UserPassAtHostPattern().Match(line);
        if (mAt.Success && TryValidPort(mAt.Groups[4].Value, out int atPort))
        {
            node = BuildNode(defaultProtocol, mAt.Groups[3].Value, atPort,
                mAt.Groups[1].Value, mAt.Groups[2].Value, NullIfEmpty(mAt.Groups[5].Value), defaultGroup);
            return true;
        }

        // 4. ip:port:user:pass#备注
        var m4 = FourColonPattern().Match(line);
        if (m4.Success && TryValidPort(m4.Groups[2].Value, out int p4))
        {
            node = BuildNode(defaultProtocol, m4.Groups[1].Value, p4,
                m4.Groups[3].Value, m4.Groups[4].Value, NullIfEmpty(m4.Groups[5].Value), defaultGroup);
            return true;
        }

        // 5. ip:port#备注
        var m2 = HostPortPattern().Match(line);
        if (m2.Success && TryValidPort(m2.Groups[2].Value, out int pOnly))
        {
            node = BuildNode(defaultProtocol, m2.Groups[1].Value, pOnly,
                null, null, NullIfEmpty(m2.Groups[3].Value), defaultGroup);
            return true;
        }

        error = "缺少有效协议前缀或合法端口号 (1-65535)";
        return false;
    }

    private static ProxyNode BuildNode(
        ProxyProtocol proto,
        string host,
        int port,
        string? user,
        string? pass,
        string? remark,
        string group)
    {
        return new ProxyNode
        {
            Id = "n_" + Guid.NewGuid().ToString("N")[..8],
            Protocol = proto,
            Host = host.Trim(),
            Port = port,
            Username = NullIfEmpty(user),
            Password = NullIfEmpty(pass),
            Name = string.IsNullOrWhiteSpace(remark) ? $"{proto}-{host}:{port}" : remark.Trim(),
            Group = string.IsNullOrWhiteSpace(group) ? "批量导入池" : group.Trim(),
            Status = "unverified",
            ExitIp = "—",
            IspSummary = "新导入 · 待物理直连验真"
        };
    }

    private static string TryDecodeBase64Block(string rawText)
    {
        string trimmed = rawText.Trim();
        if (trimmed.Contains('\n') || trimmed.Contains(':') || trimmed.Contains(','))
            return rawText;
        if (trimmed.Length < 12 || trimmed.Length % 4 != 0)
            return rawText;
        try
        {
            byte[] bytes = Convert.FromBase64String(trimmed);
            string decoded = Encoding.UTF8.GetString(bytes);
            if (decoded.Contains(':') || decoded.Contains("://"))
                return decoded;
        }
        catch
        {
            // 非 Base64 文本，按原样解析
        }
        return rawText;
    }

    private static bool TryValidPort(string s, out int port) =>
        int.TryParse(s, out port) && port is >= 1 and <= 65535;

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
