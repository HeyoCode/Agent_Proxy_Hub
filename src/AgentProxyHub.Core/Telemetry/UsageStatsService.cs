using AgentProxyHub.Core.Models;

namespace AgentProxyHub.Core.Telemetry;

/// <summary>
/// 双源融合用量统计与大盘聚合服务：
/// 融合「免解密 AI 会话日志统计」与「代理隧道实时流量归属」
/// </summary>
public sealed class UsageStatsService
{
    private readonly SessionLogScanner _scanner;
    private readonly ModelPricingCatalog _pricing;
    private readonly object _lock = new();
    private List<UsageLogEntry> _entries = new();
    private SyncStatsResult? _lastSync;

    public UsageStatsService(SessionLogScanner scanner, ModelPricingCatalog pricing)
    {
        _scanner = scanner;
        _pricing = pricing;
    }

    public SyncStatsResult SyncNow()
    {
        var (parsed, info) = _scanner.ScanIncremental();
        ApplyPricing(parsed);
        lock (_lock)
        {
            _entries = parsed;
            _lastSync = info;
        }
        return info;
    }

    public void RecalculatePricing()
    {
        lock (_lock)
        {
            ApplyPricing(_entries);
        }
    }

    private void ApplyPricing(List<UsageLogEntry> items)
    {
        foreach (var e in items)
        {
            var (cost, isPriced) = _pricing.CalculateCost(e);
            e.CostUsd = cost;
            e.IsPriced = isPriced;
        }
    }

    public UsageDashboardResponse QueryDashboard(
        string range,
        string? appFilter,
        string? modelFilter,
        int page,
        int pageSize,
        IReadOnlyList<SeatTrafficSnapshot> proxySnapshots,
        DateTimeOffset? nowOverride = null)
    {
        List<UsageLogEntry> snapshot;
        SyncStatsResult? syncInfo;
        lock (_lock)
        {
            if (_lastSync == null)
            {
                var (parsed, info) = _scanner.ScanIncremental();
                ApplyPricing(parsed);
                _entries = parsed;
                _lastSync = info;
            }
            snapshot = _entries.ToList();
            syncInfo = _lastSync;
        }

        var now = (nowOverride ?? DateTimeOffset.Now).ToLocalTime();
        DateTimeOffset? cutoff = range switch
        {
            "today" => new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset),
            "24h" => now.AddHours(-24),
            "7d" => now.AddDays(-7),
            "30d" => now.AddDays(-30),
            _ => null
        };

        var inRange = cutoff.HasValue
            ? snapshot.Where(e => e.Timestamp.ToLocalTime() >= cutoff.Value).ToList()
            : snapshot;

        // 统计当前时间范围内各应用的请求角标
        var appChips = inRange
            .GroupBy(e => e.AppName)
            .OrderByDescending(g => g.Count())
            .Select(g => new AppFilterChip { Name = g.Key, Count = g.Count() })
            .ToList();

        var availableModels = inRange
            .Select(e => e.Model)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m)
            .ToList();

        IEnumerable<UsageLogEntry> filteredQuery = inRange;
        if (!string.IsNullOrWhiteSpace(appFilter) && !appFilter.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            filteredQuery = filteredQuery.Where(e => e.AppName.Equals(appFilter, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(modelFilter) && !modelFilter.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            filteredQuery = filteredQuery.Where(e => e.Model.Equals(modelFilter, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = filteredQuery.OrderByDescending(e => e.Timestamp).ToList();

        long effIn = filtered.Sum(e => e.EffectiveInputTokens);
        long outTok = filtered.Sum(e => e.OutputTokens);
        long cacheRead = filtered.Sum(e => e.CacheReadTokens);
        long cacheWrite = filtered.Sum(e => e.CacheWriteTokens);
        long totalTokens = effIn + outTok + cacheRead + cacheWrite;
        long totalPrompt = effIn + cacheWrite + cacheRead;
        double cacheHitRate = totalPrompt > 0 ? Math.Round(cacheRead * 100.0 / totalPrompt, 1) : 0.0;
        double totalCost = Math.Round(filtered.Where(e => e.IsPriced && e.CostUsd.HasValue).Sum(e => e.CostUsd!.Value), 4);

        // 生成时间轴分桶（当天/24h 按小时，其余按天）
        var timeline = BuildTimeline(range, filtered, now);

        // 模型维度聚合
        var modelBreakdown = filtered
            .GroupBy(e => e.Model)
            .Select(g =>
            {
                long mIn = g.Sum(x => x.EffectiveInputTokens);
                long mOut = g.Sum(x => x.OutputTokens);
                long mCr = g.Sum(x => x.CacheReadTokens);
                long mCw = g.Sum(x => x.CacheWriteTokens);
                long mPrompt = mIn + mCw + mCr;
                var speeds = g.Where(x => x.TokensPerSecond.HasValue).Select(x => x.TokensPerSecond!.Value).ToList();
                return new ModelUsageSummary
                {
                    Model = g.Key,
                    Requests = g.Count(),
                    EffectiveInputTokens = mIn,
                    OutputTokens = mOut,
                    CacheReadTokens = mCr,
                    TotalTokens = mIn + mOut + mCr + mCw,
                    CacheHitRate = mPrompt > 0 ? Math.Round(mCr * 100.0 / mPrompt, 1) : 0,
                    AvgTokensPerSecond = speeds.Count > 0 ? Math.Round(speeds.Average(), 1) : 0,
                    CostUsd = Math.Round(g.Where(x => x.IsPriced && x.CostUsd.HasValue).Sum(x => x.CostUsd!.Value), 4),
                    HasPricing = g.Any(x => x.IsPriced)
                };
            })
            .OrderByDescending(m => m.TotalTokens)
            .ToList();

        // 应用与代理专线二维合并表
        var appAndProxy = MergeAppAndProxySummaries(inRange, proxySnapshots);

        int safeSize = Math.Clamp(pageSize, 10, 100);
        int safePage = Math.Max(1, page);
        var pagedLogs = filtered.Skip((safePage - 1) * safeSize).Take(safeSize).ToList();

        return new UsageDashboardResponse
        {
            Range = range,
            LastSyncedAt = syncInfo?.SyncedAt,
            ScannedFilesCount = syncInfo?.ScannedFiles ?? 0,
            SyncElapsedMs = syncInfo?.ElapsedMs ?? 0,
            TotalCostUsd = totalCost,
            TotalRequests = filtered.Count,
            TotalTokens = totalTokens,
            EffectiveInputTokens = effIn,
            OutputTokens = outTok,
            CacheReadTokens = cacheRead,
            CacheWriteTokens = cacheWrite,
            CacheHitRate = cacheHitRate,
            PricedRequests = filtered.Count(e => e.IsPriced),
            UnpricedRequests = filtered.Count(e => !e.IsPriced),
            TotalProxyTxBytes = proxySnapshots.Sum(s => s.BytesTx),
            TotalProxyRxBytes = proxySnapshots.Sum(s => s.BytesRx),
            TotalProxyConnections = proxySnapshots.Sum(s => s.TotalConnections),
            AppChips = appChips,
            AvailableModels = availableModels,
            Timeline = timeline,
            Logs = pagedLogs,
            TotalLogsCount = filtered.Count,
            Page = safePage,
            PageSize = safeSize,
            ModelBreakdown = modelBreakdown,
            AppAndProxyBreakdown = appAndProxy
        };
    }

    private static List<UsageBucketPoint> BuildTimeline(string range, List<UsageLogEntry> entries, DateTimeOffset now)
    {
        var points = new List<UsageBucketPoint>();
        if (range == "today")
        {
            int maxHour = Math.Max(0, now.Hour);
            var byHour = entries.GroupBy(e => e.Timestamp.ToLocalTime().Hour)
                .ToDictionary(g => g.Key, g => g.ToList());

            for (int h = 0; h <= maxHour; h++)
            {
                byHour.TryGetValue(h, out var list);
                points.Add(CreateBucket($"{h:00}:00", list));
            }
            return points;
        }

        if (range == "24h")
        {
            var startHour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset).AddHours(-23);
            var byKey = entries.GroupBy(e =>
            {
                var lt = e.Timestamp.ToLocalTime();
                return new DateTimeOffset(lt.Year, lt.Month, lt.Day, lt.Hour, 0, 0, lt.Offset);
            }).ToDictionary(g => g.Key, g => g.ToList());

            for (int i = 0; i < 24; i++)
            {
                var slot = startHour.AddHours(i);
                byKey.TryGetValue(slot, out var list);
                points.Add(CreateBucket($"{slot:HH:00}", list));
            }
            return points;
        }

        int days = range == "7d" ? 7 : 30;
        var todayDate = now.Date;
        var byDay = entries.GroupBy(e => e.Timestamp.ToLocalTime().Date)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (range == "all" && entries.Count > 0)
        {
            var minDate = entries.Min(e => e.Timestamp.ToLocalTime().Date);
            days = Math.Clamp((todayDate - minDate).Days + 1, 7, 60);
        }

        for (int i = days - 1; i >= 0; i--)
        {
            var d = todayDate.AddDays(-i);
            byDay.TryGetValue(d, out var list);
            points.Add(CreateBucket($"{d:MM-dd}", list));
        }
        return points;
    }

    private static UsageBucketPoint CreateBucket(string label, List<UsageLogEntry>? list)
    {
        if (list == null || list.Count == 0)
            return new UsageBucketPoint { Label = label };

        long effIn = list.Sum(x => x.EffectiveInputTokens);
        long outTok = list.Sum(x => x.OutputTokens);
        long cr = list.Sum(x => x.CacheReadTokens);
        long cw = list.Sum(x => x.CacheWriteTokens);
        long prompt = effIn + cw + cr;
        return new UsageBucketPoint
        {
            Label = label,
            Requests = list.Count,
            TotalTokens = effIn + outTok + cr + cw,
            EffectiveInputTokens = effIn,
            OutputTokens = outTok,
            CacheReadTokens = cr,
            CacheHitRate = prompt > 0 ? Math.Round(cr * 100.0 / prompt, 1) : 0,
            CostUsd = Math.Round(list.Where(x => x.IsPriced && x.CostUsd.HasValue).Sum(x => x.CostUsd!.Value), 4)
        };
    }

    private static List<AppUsageAndProxySummary> MergeAppAndProxySummaries(
        List<UsageLogEntry> entries,
        IReadOnlyList<SeatTrafficSnapshot> proxySnapshots)
    {
        var usageByApp = entries.GroupBy(e => e.AppName).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        static string MapUsageKeyToSeatId(string appName) => appName.ToLowerInvariant() switch
        {
            "claude code" or "claude" => "claude",
            "codex" or "chatgpt" => "chatgpt",
            "antigravity" => "agy",
            "cursor" => "cursor",
            _ => appName.ToLowerInvariant()
        };

        var result = new List<AppUsageAndProxySummary>();
        var matchedSeats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in usageByApp.OrderByDescending(k => k.Value.Sum(x => x.TotalTokens)))
        {
            string seatId = MapUsageKeyToSeatId(kv.Key);
            var seat = proxySnapshots.FirstOrDefault(s => s.SeatId.Equals(seatId, StringComparison.OrdinalIgnoreCase));
            if (seat != null) matchedSeats.Add(seat.SeatId);

            long effIn = kv.Value.Sum(x => x.EffectiveInputTokens);
            long cr = kv.Value.Sum(x => x.CacheReadTokens);
            long cw = kv.Value.Sum(x => x.CacheWriteTokens);
            long prompt = effIn + cr + cw;

            result.Add(new AppUsageAndProxySummary
            {
                AppName = kv.Key,
                Requests = kv.Value.Count,
                TotalTokens = kv.Value.Sum(x => x.TotalTokens),
                CacheHitRate = prompt > 0 ? Math.Round(cr * 100.0 / prompt, 1) : 0,
                CostUsd = Math.Round(kv.Value.Where(x => x.IsPriced && x.CostUsd.HasValue).Sum(x => x.CostUsd!.Value), 4),
                SeatPort = seat != null ? $"127.0.0.1:{seat.LocalPort}" : "直连本地日志",
                BoundNodeName = seat?.BoundNodeName ?? "—",
                ExitIp = seat?.ExitIp ?? "—",
                ProxyConnections = seat?.TotalConnections ?? 0,
                BytesTx = seat?.BytesTx ?? 0,
                BytesRx = seat?.BytesRx ?? 0
            });
        }

        foreach (var seat in proxySnapshots.Where(s => !matchedSeats.Contains(s.SeatId)))
        {
            result.Add(new AppUsageAndProxySummary
            {
                AppName = seat.AppName,
                Requests = 0,
                TotalTokens = 0,
                CacheHitRate = 0,
                CostUsd = 0,
                SeatPort = $"127.0.0.1:{seat.LocalPort}",
                BoundNodeName = seat.BoundNodeName,
                ExitIp = seat.ExitIp,
                ProxyConnections = seat.TotalConnections,
                BytesTx = seat.BytesTx,
                BytesRx = seat.BytesRx
            });
        }

        return result;
    }
}
