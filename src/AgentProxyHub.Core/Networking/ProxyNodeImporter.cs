using System.Text;
using System.Text.RegularExpressions;
using AgentProxyHub.Core.Models;

namespace AgentProxyHub.Core.Networking;

/// <summary>
/// 全协议代理线路智能批量解析引擎：
/// - L1 原生协议：SOCKS5/5h、SOCKS4/4a、HTTP、HTTPS、Trojan、Shadowsocks (AEAD)
/// - L2 现代扩展协议 (sing-box)：VLESS (Reality/WS/gRPC)、Hysteria2 (hy2)、TUIC、WireGuard (wg)
/// - 兼容 ip:port:user:pass、user:pass@ip:port、ip:port、CSV 及整段 Base64 订阅
/// </summary>
public static partial class ProxyNodeImporter
{
    [GeneratedRegex(@"^(socks5h?|socks4a?|https?):\/\/(?:([^:@\s]+):([^@\s]+)@)?(\[[a-fA-F0-9:%.]+\]|[a-zA-Z0-9.-]+):(\d{1,5})\/?(?:\?([^#\s]*))?(?:#(.+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex ClassicUrlPattern();

    [GeneratedRegex(@"^([^:@\s]+):([^@\s]+)@(\[[a-fA-F0-9:%.]+\]|[a-zA-Z0-9.-]+):(\d{1,5})(?:#(.+))?$")]
    private static partial Regex UserPassAtHostPattern();

    [GeneratedRegex(@"^(\[[a-fA-F0-9:%.]+\]|[a-zA-Z0-9.-]+):(\d{1,5}):([^:\s#]+):([^#\s]+)(?:#(.+))?$")]
    private static partial Regex FourColonPattern();

    [GeneratedRegex(@"^(\[[a-fA-F0-9:%.]+\]|[a-zA-Z0-9.-]+):(\d{1,5})(?:#(.+))?$")]
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

        // 1. 现代与加密协议 URI 识别 (trojan / ss / vless / hysteria2 / hy2 / tuic / wireguard / wg)
        if (line.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
            return TryParseTrojan(line, defaultGroup, out node, out error);

        if (line.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
            return TryParseShadowsocks(line, defaultGroup, out node, out error);

        if (line.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            return TryParseVless(line, defaultGroup, out node, out error);

        if (line.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase))
            return TryParseHysteria2(line, defaultGroup, out node, out error);

        if (line.StartsWith("tuic://", StringComparison.OrdinalIgnoreCase))
            return TryParseTuic(line, defaultGroup, out node, out error);

        if (line.StartsWith("wireguard://", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("wg://", StringComparison.OrdinalIgnoreCase))
            return TryParseWireGuard(line, defaultGroup, out node, out error);

        // 2. 经典 URI 格式: socks5://user:pass@ip:port?sni=...#备注
        var mUrl = ClassicUrlPattern().Match(line);
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
            string? user = NullIfEmpty(SafeUnescape(mUrl.Groups[2].Value));
            string? pass = NullIfEmpty(SafeUnescape(mUrl.Groups[3].Value));
            var qs = ParseQueryString(mUrl.Groups[6].Value);
            string? remark = NullIfEmpty(SafeUnescape(mUrl.Groups[7].Value));
            node = BuildNode(proto, host, port, user, pass, remark, defaultGroup);
            if (qs.TryGetValue("sni", out string? sni) && !string.IsNullOrWhiteSpace(sni))
                node.Sni = sni;
            if (IsTruthy(qs, "allowInsecure") || IsTruthy(qs, "insecure"))
                node.AllowInsecure = true;
            return true;
        }

        // 3. CSV 格式: proto,host,port,user,pass,name 或 host,port,user,pass
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

        // 4. user:pass@ip:port#备注
        var mAt = UserPassAtHostPattern().Match(line);
        if (mAt.Success && TryValidPort(mAt.Groups[4].Value, out int atPort))
        {
            node = BuildNode(defaultProtocol, mAt.Groups[3].Value, atPort,
                mAt.Groups[1].Value, mAt.Groups[2].Value, NullIfEmpty(SafeUnescape(mAt.Groups[5].Value)), defaultGroup);
            return true;
        }

        // 5. ip:port:user:pass#备注
        var m4 = FourColonPattern().Match(line);
        if (m4.Success && TryValidPort(m4.Groups[2].Value, out int p4))
        {
            node = BuildNode(defaultProtocol, m4.Groups[1].Value, p4,
                m4.Groups[3].Value, m4.Groups[4].Value, NullIfEmpty(SafeUnescape(m4.Groups[5].Value)), defaultGroup);
            return true;
        }

        // 6. ip:port#备注
        var m2 = HostPortPattern().Match(line);
        if (m2.Success && TryValidPort(m2.Groups[2].Value, out int pOnly))
        {
            node = BuildNode(defaultProtocol, m2.Groups[1].Value, pOnly,
                null, null, NullIfEmpty(SafeUnescape(m2.Groups[3].Value)), defaultGroup);
            return true;
        }

        error = "缺少有效协议前缀或合法端口号 (1-65535)";
        return false;
    }

    private static bool TryParseTrojan(string line, string group, out ProxyNode? node, out string? error)
    {
        node = null;
        error = null;
        if (!TrySplitGenericUri(line, out _, out string userInfo, out string host, out int port, out var qs, out string? remark, out error))
            return false;

        string password = SafeUnescape(userInfo);
        if (string.IsNullOrWhiteSpace(password))
        {
            error = "Trojan 链接缺少密码";
            return false;
        }

        node = BuildNode(ProxyProtocol.TROJAN, host, port, null, password, remark, group);
        node.Sni = GetFirstNonEmpty(qs, "sni", "peer") ?? host;
        node.AllowInsecure = IsTruthy(qs, "allowInsecure") || IsTruthy(qs, "insecure");
        if (qs.Count > 0) node.Extra = qs;
        node.IspSummary = $"Trojan TLS · SNI {node.Sni}";
        return true;
    }

    private static bool TryParseShadowsocks(string line, string group, out ProxyNode? node, out string? error)
    {
        node = null;
        error = null;

        string body = line[5..]; // strip ss://
        string? remark = null;
        int hashIdx = body.IndexOf('#');
        if (hashIdx >= 0)
        {
            remark = NullIfEmpty(SafeUnescape(body[(hashIdx + 1)..]));
            body = body[..hashIdx];
        }

        Dictionary<string, string> qs = new(StringComparer.OrdinalIgnoreCase);
        int qIdx = body.IndexOf('?');
        if (qIdx >= 0)
        {
            qs = ParseQueryString(body[(qIdx + 1)..]);
            body = body[..qIdx];
        }

        string method;
        string password;
        string host;
        int port;

        int atIdx = body.LastIndexOf('@');
        if (atIdx > 0)
        {
            string credRaw = body[..atIdx];
            string hostPortRaw = body[(atIdx + 1)..].TrimEnd('/');
            if (!TrySplitHostPort(hostPortRaw, out host, out port))
            {
                error = "Shadowsocks 服务器地址或端口格式无效";
                return false;
            }

            string decodedCred = credRaw.Contains(':') ? SafeUnescape(credRaw) : (TryDecodeBase64String(credRaw) ?? SafeUnescape(credRaw));
            int cIdx = decodedCred.IndexOf(':');
            if (cIdx <= 0)
            {
                error = "Shadowsocks 加密方式与密码格式无效";
                return false;
            }
            method = decodedCred[..cIdx].Trim();
            password = decodedCred[(cIdx + 1)..];
        }
        else
        {
            // 经典整段 Base64: ss://base64(method:password@host:port)#remark
            string? decoded = TryDecodeBase64String(body);
            if (string.IsNullOrWhiteSpace(decoded))
            {
                error = "Shadowsocks Base64 链接解码失败";
                return false;
            }
            int dAt = decoded.LastIndexOf('@');
            if (dAt <= 0 || !TrySplitHostPort(decoded[(dAt + 1)..].TrimEnd('/'), out host, out port))
            {
                error = "Shadowsocks 解码后缺少有效主机与端口";
                return false;
            }
            string cred = decoded[..dAt];
            int cIdx = cred.IndexOf(':');
            if (cIdx <= 0)
            {
                error = "Shadowsocks 解码后缺少加密方法与密码";
                return false;
            }
            method = cred[..cIdx].Trim();
            password = cred[(cIdx + 1)..];
        }

        node = BuildNode(ProxyProtocol.SHADOWSOCKS, host, port, null, password, remark, group);
        node.Method = method.ToLowerInvariant();
        if (qs.Count > 0) node.Extra = qs;
        node.IspSummary = $"Shadowsocks · {node.Method}";
        return true;
    }

    private static bool TryParseVless(string line, string group, out ProxyNode? node, out string? error)
    {
        node = null;
        error = null;
        if (!TrySplitGenericUri(line, out _, out string uuid, out string host, out int port, out var qs, out string? remark, out error))
            return false;

        if (string.IsNullOrWhiteSpace(uuid))
        {
            error = "VLESS 链接缺少 UUID";
            return false;
        }

        node = BuildNode(ProxyProtocol.VLESS, host, port, SafeUnescape(uuid), null, remark, group);
        node.Sni = GetFirstNonEmpty(qs, "sni", "servername") ?? host;
        node.AllowInsecure = IsTruthy(qs, "allowInsecure") || IsTruthy(qs, "insecure");
        node.Extra = qs;
        string sec = qs.TryGetValue("security", out string? s) ? s : "none";
        string net = qs.TryGetValue("type", out string? t) ? t : "tcp";
        node.IspSummary = $"VLESS ({sec}/{net}) · sing-box 扩展";
        return true;
    }

    private static bool TryParseHysteria2(string line, string group, out ProxyNode? node, out string? error)
    {
        node = null;
        error = null;
        if (!TrySplitGenericUri(line, out _, out string auth, out string host, out int port, out var qs, out string? remark, out error))
            return false;

        if (string.IsNullOrWhiteSpace(auth))
        {
            error = "Hysteria2 链接缺少密码";
            return false;
        }

        node = BuildNode(ProxyProtocol.HYSTERIA2, host, port, null, SafeUnescape(auth), remark, group);
        node.Sni = GetFirstNonEmpty(qs, "sni", "peer") ?? host;
        node.AllowInsecure = IsTruthy(qs, "insecure") || IsTruthy(qs, "allowInsecure");
        node.Extra = qs;
        node.IspSummary = $"Hysteria2 QUIC · SNI {node.Sni} · sing-box";
        return true;
    }

    private static bool TryParseTuic(string line, string group, out ProxyNode? node, out string? error)
    {
        node = null;
        error = null;
        if (!TrySplitGenericUri(line, out _, out string userInfo, out string host, out int port, out var qs, out string? remark, out error))
            return false;

        int colon = userInfo.IndexOf(':');
        string uuid = colon > 0 ? SafeUnescape(userInfo[..colon]) : SafeUnescape(userInfo);
        string pass = colon > 0 ? SafeUnescape(userInfo[(colon + 1)..]) : string.Empty;
        if (string.IsNullOrWhiteSpace(uuid))
        {
            error = "TUIC 链接缺少 UUID";
            return false;
        }

        node = BuildNode(ProxyProtocol.TUIC, host, port, uuid, NullIfEmpty(pass), remark, group);
        node.Sni = GetFirstNonEmpty(qs, "sni") ?? host;
        node.AllowInsecure = IsTruthy(qs, "allow_insecure") || IsTruthy(qs, "insecure");
        node.Extra = qs;
        node.IspSummary = $"TUIC v5 · SNI {node.Sni} · sing-box";
        return true;
    }

    private static bool TryParseWireGuard(string line, string group, out ProxyNode? node, out string? error)
    {
        node = null;
        error = null;
        if (!TrySplitGenericUri(line, out _, out string privKey, out string host, out int port, out var qs, out string? remark, out error))
            return false;

        if (string.IsNullOrWhiteSpace(privKey))
        {
            error = "WireGuard 链接缺少 PrivateKey";
            return false;
        }

        node = BuildNode(ProxyProtocol.WIREGUARD, host, port, null, SafeUnescape(privKey), remark, group);
        node.Extra = qs;
        node.IspSummary = "WireGuard UDP · sing-box 扩展";
        return true;
    }

    private static bool TrySplitGenericUri(
        string line,
        out string scheme,
        out string userInfo,
        out string host,
        out int port,
        out Dictionary<string, string> query,
        out string? remark,
        out string? error)
    {
        scheme = string.Empty;
        userInfo = string.Empty;
        host = string.Empty;
        port = 0;
        query = new(StringComparer.OrdinalIgnoreCase);
        remark = null;
        error = null;

        int schemeSep = line.IndexOf("://", StringComparison.Ordinal);
        if (schemeSep <= 0)
        {
            error = "缺少协议前缀";
            return false;
        }
        scheme = line[..schemeSep].ToLowerInvariant();
        string rest = line[(schemeSep + 3)..];

        int hashIdx = rest.IndexOf('#');
        if (hashIdx >= 0)
        {
            remark = NullIfEmpty(SafeUnescape(rest[(hashIdx + 1)..]));
            rest = rest[..hashIdx];
        }

        int qIdx = rest.IndexOf('?');
        if (qIdx >= 0)
        {
            query = ParseQueryString(rest[(qIdx + 1)..]);
            rest = rest[..qIdx];
        }

        int atIdx = rest.LastIndexOf('@');
        if (atIdx >= 0)
        {
            userInfo = rest[..atIdx];
            rest = rest[(atIdx + 1)..];
        }

        rest = rest.TrimEnd('/');
        if (!TrySplitHostPort(rest, out host, out port))
        {
            error = "主机地址或端口 (1~65535) 无效";
            return false;
        }

        return true;
    }

    private static bool TrySplitHostPort(string input, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;

        if (input.StartsWith('['))
        {
            int endB = input.IndexOf(']');
            if (endB <= 1 || endB + 2 > input.Length || input[endB + 1] != ':')
                return false;
            host = input[1..endB];
            return TryValidPort(input[(endB + 2)..], out port);
        }

        int colon = input.LastIndexOf(':');
        if (colon <= 0) return false;
        host = input[..colon].Trim();
        return !string.IsNullOrWhiteSpace(host) && TryValidPort(input[(colon + 1)..], out port);
    }

    private static Dictionary<string, string> ParseQueryString(string? rawQuery)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(rawQuery)) return dict;

        foreach (string pair in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            string k = SafeUnescape(pair[..eq]).Trim();
            string v = SafeUnescape(pair[(eq + 1)..]).Trim();
            if (!string.IsNullOrEmpty(k))
                dict[k] = v;
        }
        return dict;
    }

    private static string? GetFirstNonEmpty(Dictionary<string, string> qs, params string[] keys)
    {
        foreach (string k in keys)
        {
            if (qs.TryGetValue(k, out string? v) && !string.IsNullOrWhiteSpace(v))
                return v;
        }
        return null;
    }

    private static bool IsTruthy(Dictionary<string, string> qs, string key) =>
        qs.TryGetValue(key, out string? v) && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static ProxyNode BuildNode(
        ProxyProtocol proto,
        string host,
        int port,
        string? user,
        string? pass,
        string? remark,
        string group)
    {
        string cleanHost = host.Trim().Trim('[', ']');
        string displayHost = cleanHost.Contains(':') ? $"[{cleanHost}]" : cleanHost;
        return new ProxyNode
        {
            Id = "n_" + Guid.NewGuid().ToString("N")[..8],
            Protocol = proto,
            Host = cleanHost,
            Port = port,
            Username = NullIfEmpty(user),
            Password = NullIfEmpty(pass),
            Name = string.IsNullOrWhiteSpace(remark) ? $"{proto}-{displayHost}:{port}" : remark.Trim(),
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
        if (trimmed.Length < 12)
            return rawText;

        string? decoded = TryDecodeBase64String(trimmed);
        if (!string.IsNullOrWhiteSpace(decoded) && (decoded.Contains(':') || decoded.Contains("://")))
            return decoded;

        return rawText;
    }

    private static string? TryDecodeBase64String(string input)
    {
        try
        {
            string s = input.Trim().Replace('-', '+').Replace('_', '/');
            int mod = s.Length % 4;
            if (mod == 2) s += "==";
            else if (mod == 3) s += "=";
            else if (mod == 1) return null;
            byte[] bytes = Convert.FromBase64String(s);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static string SafeUnescape(string s)
    {
        try { return Uri.UnescapeDataString(s); }
        catch { return s; }
    }

    private static bool TryValidPort(string s, out int port) =>
        int.TryParse(s, out port) && port is >= 1 and <= 65535;

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
