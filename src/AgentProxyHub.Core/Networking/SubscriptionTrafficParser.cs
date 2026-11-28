using System.Globalization;
using System.Text.RegularExpressions;
using AgentProxyHub.Core.Models;

namespace AgentProxyHub.Core.Networking;

/// <summary>
/// 订阅源流量额度、到期时间与提示伪节点语义提取器：
/// 1. 解析 HTTP 响应头 Subscription-Userinfo (upload / download / total / expire)
/// 2. 自动识别节点列表中夹带的「剩余流量：282.58 GB」「套餐到期：2027-03-15」「距离下次重置：18天」等提示伪节点并提炼成结构化卡片
/// </summary>
public static partial class SubscriptionTrafficParser
{
    public sealed record ParsedTrafficHint(
        long? UploadBytes = null,
        long? DownloadBytes = null,
        long? TotalBytes = null,
        long? RemainingBytes = null,
        DateTimeOffset? ExpireAt = null,
        int? ResetInDays = null,
        int InfoEntryCount = 0);

    [GeneratedRegex(@"(?:剩余流量|可用流量|剩余额度|剩余|Traffic\s*Left|Remaining(?:\s*Traffic)?)\s*[:：=]?\s*([0-9]+(?:\.[0-9]+)?)\s*(TB|GB|MB|KB|TiB|GiB|MiB|T|G|M|B)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RemainingTrafficRegex();

    [GeneratedRegex(@"(?:已用流量|已用|Used(?:\s*Traffic)?)\s*[:：=]?\s*([0-9]+(?:\.[0-9]+)?)\s*(TB|GB|MB|KB|TiB|GiB|MiB|T|G|M|B)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UsedTrafficRegex();

    [GeneratedRegex(@"(?:总流量|套餐流量|总额度|Total(?:\s*Traffic)?)\s*[:：=]?\s*([0-9]+(?:\.[0-9]+)?)\s*(TB|GB|MB|KB|TiB|GiB|MiB|T|G|M|B)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TotalTrafficRegex();

    [GeneratedRegex(@"(?:距离下次重置(?:剩余)?|重置剩余|流量重置|下次重置|Reset\s*in)\s*[:：=]?\s*(\d{1,4})\s*(?:天|日|d|days?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResetDaysRegex();

    [GeneratedRegex(@"(?:套餐到期|到期时间|过期时间|有效期至|到期|Expire(?:s|d)?(?:\s*at|\s*on)?)\s*[:：=]?\s*(\d{4}[-/.年]\d{1,2}[-/.月]\d{1,2})日?", RegexOptions.IgnoreCase)]
    private static partial Regex ExpireDateRegex();

    [GeneratedRegex(@"^(?:[\p{So}\p{Cs}\s【\[（(]*)(?:剩余流量|可用流量|已用流量|总流量|套餐流量|距离下次重置|流量重置|下次重置|套餐到期|到期时间|过期时间|有效期至|官网|最新官网|备用官网|地址发布|防失联|官方群|群组|公告|通知|提示|客服|使用说明|订阅说明|Expire|Remaining\s*Traffic|Traffic\s*Left)(?:[\]】）)\s:：=\-—|]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex InfoNodePrefixRegex();

    /// <summary>
    /// 判断节点名称是否属于非真实代理转发用途的「流量/到期/公告提示伪节点」
    /// </summary>
    public static bool IsInformationalNodeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string trimmed = name.Trim();

        if (InfoNodePrefixRegex().IsMatch(trimmed))
            return true;

        if (RemainingTrafficRegex().IsMatch(trimmed) ||
            ResetDaysRegex().IsMatch(trimmed) ||
            ExpireDateRegex().IsMatch(trimmed))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 解析标准机场/专线协议响应头 Subscription-Userinfo: upload=123; download=456; total=107374182400; expire=1798761600
    /// </summary>
    public static ParsedTrafficHint ParseSubscriptionUserInfoHeader(IDictionary<string, IEnumerable<string>>? headers)
    {
        if (headers == null || headers.Count == 0)
            return new ParsedTrafficHint();

        string? raw = null;
        foreach (var kv in headers)
        {
            if (string.Equals(kv.Key, "subscription-userinfo", StringComparison.OrdinalIgnoreCase))
            {
                raw = kv.Value.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                if (!string.IsNullOrWhiteSpace(raw)) break;
            }
        }

        if (string.IsNullOrWhiteSpace(raw))
            return new ParsedTrafficHint();

        long? upload = null;
        long? download = null;
        long? total = null;
        DateTimeOffset? expireAt = null;

        var parts = raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string k = part[..eq].Trim().ToLowerInvariant();
            string v = part[(eq + 1)..].Trim();
            if (string.IsNullOrEmpty(v)) continue;

            switch (k)
            {
                case "upload":
                    if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long up) && up >= 0)
                        upload = up;
                    break;
                case "download":
                    if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long down) && down >= 0)
                        download = down;
                    break;
                case "total":
                    if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long tot) && tot > 0)
                        total = tot;
                    break;
                case "expire":
                    if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double expVal) && expVal > 0)
                    {
                        long unixSec = expVal > 99999999999 ? (long)(expVal / 1000.0) : (long)expVal;
                        if (unixSec > 946684800 && unixSec < 4102444800)
                        {
                            expireAt = DateTimeOffset.FromUnixTimeSeconds(unixSec).ToLocalTime();
                        }
                    }
                    break;
            }
        }

        long? remaining = null;
        if (total.HasValue && total.Value > 0)
        {
            long used = (upload ?? 0) + (download ?? 0);
            remaining = Math.Max(0, total.Value - used);
        }

        return new ParsedTrafficHint(upload, download, total, remaining, expireAt);
    }

    /// <summary>
    /// 从一批节点名称中扫描提示伪节点并提取流量余量、总量、到期日与重置倒计时
    /// </summary>
    public static ParsedTrafficHint ExtractHintsFromNodes(IEnumerable<ProxyNode>? nodes)
    {
        if (nodes == null) return new ParsedTrafficHint();

        long? remaining = null;
        long? used = null;
        long? total = null;
        DateTimeOffset? expireAt = null;
        int? resetDays = null;
        int infoCount = 0;

        foreach (var node in nodes)
        {
            string name = node.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(name)) continue;

            if (IsInformationalNodeName(name))
                infoCount++;

            if (!remaining.HasValue)
            {
                var mRem = RemainingTrafficRegex().Match(name);
                if (mRem.Success && TryParseSizeToBytes(mRem.Groups[1].Value, mRem.Groups[2].Value, out long remBytes))
                    remaining = remBytes;
            }

            if (!used.HasValue)
            {
                var mUsed = UsedTrafficRegex().Match(name);
                if (mUsed.Success && TryParseSizeToBytes(mUsed.Groups[1].Value, mUsed.Groups[2].Value, out long usedBytes))
                    used = usedBytes;
            }

            if (!total.HasValue)
            {
                var mTot = TotalTrafficRegex().Match(name);
                if (mTot.Success && TryParseSizeToBytes(mTot.Groups[1].Value, mTot.Groups[2].Value, out long totBytes))
                    total = totBytes;
            }

            if (!resetDays.HasValue)
            {
                var mRst = ResetDaysRegex().Match(name);
                if (mRst.Success && int.TryParse(mRst.Groups[1].Value, out int d) && d >= 0)
                    resetDays = d;
            }

            if (!expireAt.HasValue)
            {
                var mExp = ExpireDateRegex().Match(name);
                if (mExp.Success)
                {
                    string normalizedDate = mExp.Groups[1].Value
                        .Replace('年', '-')
                        .Replace('月', '-')
                        .Replace('/', '-')
                        .Replace('.', '-');
                    if (DateTime.TryParse(normalizedDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    {
                        expireAt = new DateTimeOffset(dt.Year, dt.Month, dt.Day, 23, 59, 59, TimeSpan.FromHours(8));
                    }
                }
            }
        }

        if (!remaining.HasValue && total.HasValue && used.HasValue)
            remaining = Math.Max(0, total.Value - used.Value);
        if (!total.HasValue && remaining.HasValue && used.HasValue)
            total = remaining.Value + used.Value;

        return new ParsedTrafficHint(null, used, total, remaining, expireAt, resetDays, infoCount);
    }

    /// <summary>
    /// 将协议头提取的流量信息与伪节点提取的补充信息合并写回 SubscriptionSource
    /// </summary>
    public static void ApplyTrafficHints(SubscriptionSource sub, ParsedTrafficHint headerHint, ParsedTrafficHint nodeHint)
    {
        sub.UploadBytes = headerHint.UploadBytes ?? sub.UploadBytes;
        sub.DownloadBytes = headerHint.DownloadBytes ?? nodeHint.DownloadBytes ?? sub.DownloadBytes;
        sub.TotalBytes = headerHint.TotalBytes ?? nodeHint.TotalBytes ?? sub.TotalBytes;
        sub.RemainingBytes = headerHint.RemainingBytes ?? nodeHint.RemainingBytes ?? sub.RemainingBytes;
        sub.ExpireAt = headerHint.ExpireAt ?? nodeHint.ExpireAt ?? sub.ExpireAt;
        sub.ResetInDays = nodeHint.ResetInDays ?? sub.ResetInDays;
        sub.InfoEntryCount = nodeHint.InfoEntryCount;
    }

    /// <summary>
    /// 聚合生成前端「独立线路池」与「右栏检视器」使用的订阅/分组流量额度卡片列表
    /// 同时兼容：1) 已托管 URL 订阅源  2) 批量粘贴导入池中带「剩余流量: 282.58 GB」伪节点的分组
    /// </summary>
    public static List<SubscriptionTrafficCardDto> BuildTrafficPoolCards(
        IReadOnlyList<SubscriptionSource>? subscriptions,
        IReadOnlyList<ProxyNode>? allNodes,
        DateTimeOffset now)
    {
        var cards = new List<SubscriptionTrafficCardDto>();
        var nodes = allNodes ?? [];
        var subs = subscriptions ?? [];
        var coveredNodeIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sub in subs)
        {
            var subNodes = nodes
                .Where(n => string.Equals(n.SourceSubscriptionId, sub.Id, StringComparison.Ordinal))
                .ToList();
            foreach (var sn in subNodes) coveredNodeIds.Add(sn.Id);

            var nodeHint = ExtractHintsFromNodes(subNodes);
            int realCount = subNodes.Count(n => !n.IsInfoEntry);
            int infoCount = subNodes.Count(n => n.IsInfoEntry);

            long? total = sub.TotalBytes ?? nodeHint.TotalBytes;
            long? used = (sub.UploadBytes.HasValue || sub.DownloadBytes.HasValue)
                ? (sub.UploadBytes ?? 0) + (sub.DownloadBytes ?? 0)
                : nodeHint.DownloadBytes;
            long? remaining = sub.RemainingBytes ?? nodeHint.RemainingBytes;
            if (!remaining.HasValue && total.HasValue && used.HasValue)
                remaining = Math.Max(0, total.Value - used.Value);
            if (!used.HasValue && total.HasValue && remaining.HasValue)
                used = Math.Max(0, total.Value - remaining.Value);

            DateTimeOffset? expireAt = sub.ExpireAt ?? nodeHint.ExpireAt;
            int? resetDays = sub.ResetInDays ?? nodeHint.ResetInDays;
            var dto = CreateTrafficCard(
                id: sub.Id,
                name: string.IsNullOrWhiteSpace(sub.Name) ? sub.Group : sub.Name,
                group: sub.Group,
                isManaged: true,
                autoRefreshHours: sub.AutoRefreshHours,
                lastError: sub.LastError,
                nodeCount: realCount > 0 || infoCount > 0 ? realCount : sub.NodeCount,
                infoCount: infoCount > 0 ? infoCount : sub.InfoEntryCount,
                used: used,
                total: total,
                remaining: remaining,
                expireAt: expireAt,
                resetDays: resetDays,
                now: now);
            cards.Add(dto);
        }

        // 扫描未归属托管订阅源、但分组内存在「剩余流量 / 到期」提示伪节点的批量导入分组
        var unmanagedGroups = nodes
            .Where(n => !coveredNodeIds.Contains(n.Id) && n.Protocol != ProxyProtocol.DIRECT_PHYSICAL)
            .GroupBy(n => string.IsNullOrWhiteSpace(n.Group) ? "批量导入池" : n.Group.Trim(), StringComparer.OrdinalIgnoreCase);

        foreach (var grp in unmanagedGroups)
        {
            var grpNodes = grp.ToList();
            var hint = ExtractHintsFromNodes(grpNodes);
            if (hint.InfoEntryCount == 0 && !hint.RemainingBytes.HasValue && !hint.ExpireAt.HasValue && !hint.ResetInDays.HasValue)
                continue;

            int realCount = grpNodes.Count(n => !n.IsInfoEntry);
            cards.Add(CreateTrafficCard(
                id: $"group:{grp.Key}",
                name: grp.Key,
                group: grp.Key,
                isManaged: false,
                autoRefreshHours: 0,
                lastError: null,
                nodeCount: realCount,
                infoCount: hint.InfoEntryCount,
                used: hint.DownloadBytes,
                total: hint.TotalBytes,
                remaining: hint.RemainingBytes,
                expireAt: hint.ExpireAt,
                resetDays: hint.ResetInDays,
                now: now));
        }

        return cards;
    }

    private static SubscriptionTrafficCardDto CreateTrafficCard(
        string id,
        string name,
        string group,
        bool isManaged,
        int autoRefreshHours,
        string? lastError,
        int nodeCount,
        int infoCount,
        long? used,
        long? total,
        long? remaining,
        DateTimeOffset? expireAt,
        int? resetDays,
        DateTimeOffset now)
    {
        double? usedPct = null;
        double? remPct = null;
        if (total.HasValue && total.Value > 0 && remaining.HasValue)
        {
            remPct = Math.Round(Math.Clamp(remaining.Value * 100.0 / total.Value, 0, 100), 1);
            usedPct = Math.Round(Math.Clamp(100.0 - remPct.Value, 0, 100), 1);
        }

        int? remDays = null;
        if (expireAt.HasValue)
        {
            remDays = (int)Math.Ceiling((expireAt.Value.ToLocalTime().Date - now.ToLocalTime().Date).TotalDays);
        }

        string tone = "ok";
        if ((remPct.HasValue && remPct.Value <= 15.0)
            || (remaining.HasValue && !total.HasValue && remaining.Value <= 10L * 1024 * 1024 * 1024)
            || (remDays.HasValue && remDays.Value <= 2))
        {
            tone = "critical";
        }
        else if ((remPct.HasValue && remPct.Value <= 30.0)
                 || (remaining.HasValue && !total.HasValue && remaining.Value <= 30L * 1024 * 1024 * 1024)
                 || (remDays.HasValue && remDays.Value <= 7))
        {
            tone = "warn";
        }

        return new SubscriptionTrafficCardDto
        {
            Id = id,
            Name = name,
            Group = group,
            IsManagedSubscription = isManaged,
            AutoRefreshHours = autoRefreshHours,
            LastError = lastError,
            NodeCount = nodeCount,
            InfoEntryCount = infoCount,
            UsedBytes = used,
            TotalBytes = total,
            RemainingBytes = remaining,
            UsedPercent = usedPct,
            RemainingPercent = remPct,
            ExpireAt = expireAt,
            RemainingDays = remDays,
            ResetInDays = resetDays,
            Tone = tone
        };
    }

    private static bool TryParseSizeToBytes(string numStr, string unitStr, out long bytes)
    {
        bytes = 0;
        if (!double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double val) || val < 0)
            return false;

        double factor = unitStr.Trim().ToUpperInvariant() switch
        {
            "TB" or "TIB" or "T" => 1024.0 * 1024 * 1024 * 1024,
            "GB" or "GIB" or "G" => 1024.0 * 1024 * 1024,
            "MB" or "MIB" or "M" => 1024.0 * 1024,
            "KB" or "KIB" or "K" => 1024.0,
            _ => 1.0
        };

        bytes = (long)Math.Round(val * factor);
        return true;
    }
}
