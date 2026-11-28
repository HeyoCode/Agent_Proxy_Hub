using AgentProxyHub.Core.Models;
using AgentProxyHub.Core.Networking;

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
    private readonly object _syncExecLock = new();
    private List<UsageLogEntry> _entries = new();
    private SyncStatsResult? _lastSync;

    public UsageStatsService(SessionLogScanner scanner, ModelPricingCatalog pricing)
    {
        _scanner = scanner;
        _pricing = pricing;
    }

    public SyncStatsResult SyncNow()
    {
        lock (_syncExecLock)
        {
            var (parsed, info, hasChanges) = _scanner.ScanIncrementalWithChangeFlag();
            if (hasChanges || _lastSync == null)
            {
                ApplyPricing(parsed);
                lock (_lock)
                {
                    _entries = parsed;
                    _lastSync = info;
                }
            }
            else
            {
                lock (_lock)
                {
                    _lastSync = info;
                }
            }
            return info;
        }
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
            var (cost, saved, isPriced) = _pricing.CalculateCostAndSavings(e);
            e.CostUsd = cost;
            e.CacheSavedUsd = saved;
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
        bool needsInitialSync;
        lock (_lock)
        {
            needsInitialSync = _lastSync == null;
        }
        if (needsInitialSync)
        {
            SyncNow();
        }

        List<UsageLogEntry> snapshot;
        SyncStatsResult? syncInfo;
        lock (_lock)
        {
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
        long cacheHitDenominator = effIn + cacheWrite + cacheRead;
        double cacheHitRate = cacheHitDenominator > 0 ? Math.Round(cacheRead * 100.0 / cacheHitDenominator, 1) : 0.0;
        double totalCost = Math.Round(filtered.Where(e => e.IsPriced && e.CostUsd.HasValue).Sum(e => e.CostUsd!.Value), 4);
        double totalCacheSaved = Math.Round(filtered.Where(e => e.IsPriced).Sum(e => e.CacheSavedUsd), 4);

        // 过去 365 天活跃热力图仅随应用/模型筛选变化，不随 today/24h/7d 时间窗口萎缩
        IEnumerable<UsageLogEntry> heatmapQuery = snapshot;
        if (!string.IsNullOrWhiteSpace(appFilter) && !appFilter.Equals("all", StringComparison.OrdinalIgnoreCase))
            heatmapQuery = heatmapQuery.Where(e => e.AppName.Equals(appFilter, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(modelFilter) && !modelFilter.Equals("all", StringComparison.OrdinalIgnoreCase))
            heatmapQuery = heatmapQuery.Where(e => e.Model.Equals(modelFilter, StringComparison.OrdinalIgnoreCase));
        var (heatmapCells, activeDaysLastYear) = BuildYearHeatmap(heatmapQuery, now.Date);

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

        // 项目工作区成本排行（Top 12）
        var workspaceBreakdown = filtered
            .Where(e => !string.IsNullOrWhiteSpace(e.Workspace))
            .GroupBy(e => e.Workspace!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                string topApp = g.GroupBy(x => x.AppName)
                    .OrderByDescending(ag => ag.Count())
                    .Select(ag => ag.Key)
                    .FirstOrDefault() ?? "";
                string topModel = g.GroupBy(x => x.Model)
                    .OrderByDescending(mg => mg.Sum(m => m.TotalTokens))
                    .Select(mg => mg.Key)
                    .FirstOrDefault() ?? "";
                return new WorkspaceUsageSummary
                {
                    Workspace = g.Key,
                    Requests = g.Count(),
                    TotalTokens = g.Sum(x => x.TotalTokens),
                    CostUsd = Math.Round(g.Where(x => x.IsPriced && x.CostUsd.HasValue).Sum(x => x.CostUsd!.Value), 4),
                    TopApp = topApp,
                    TopModel = topModel
                };
            })
            .OrderByDescending(w => w.CostUsd)
            .ThenByDescending(w => w.TotalTokens)
            .Take(12)
            .ToList();

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
            TotalCacheSavedUsd = totalCacheSaved,
            TotalRequests = filtered.Count,
            TotalTokens = totalTokens,
            EffectiveInputTokens = effIn,
            OutputTokens = outTok,
            CacheReadTokens = cacheRead,
            CacheWriteTokens = cacheWrite,
            CacheHitRate = cacheHitRate,
            PricedRequests = filtered.Count(e => e.IsPriced),
            UnpricedRequests = filtered.Count(e => !e.IsPriced),
            ActiveDaysLastYear = activeDaysLastYear,
            ActivityHeatmap = heatmapCells,
            TotalProxyTxBytes = proxySnapshots.Sum(s => s.BytesTx),
            TotalProxyRxBytes = proxySnapshots.Sum(s => s.BytesRx),
            TotalProxyTxBytesPerSec = proxySnapshots.Sum(s => s.BytesTxPerSec),
            TotalProxyRxBytesPerSec = proxySnapshots.Sum(s => s.BytesRxPerSec),
            TotalProxyConnections = proxySnapshots.Sum(s => s.TotalConnections),
            AppChips = appChips,
            AvailableModels = availableModels,
            Timeline = timeline,
            Logs = pagedLogs,
            TotalLogsCount = filtered.Count,
            Page = safePage,
            PageSize = safeSize,
            ModelBreakdown = modelBreakdown,
            AppAndProxyBreakdown = appAndProxy,
            WorkspaceBreakdown = workspaceBreakdown
        };
    }

    public ConversationAuditReport? GetConversationAudit(string conversationId)
        => _scanner.BuildConversationAudit(conversationId, _pricing);

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

    private static (List<DailyHeatmapCell> Cells, int ActiveDays) BuildYearHeatmap(
        IEnumerable<UsageLogEntry> entries,
        DateTime todayDate)
    {
        var startDate = todayDate.AddDays(-364);
        var byDay = entries
            .GroupBy(e => e.Timestamp.ToLocalTime().Date)
            .ToDictionary(
                g => g.Key,
                g => (
                    Requests: g.Count(),
                    Tokens: g.Sum(x => x.TotalTokens),
                    Cost: Math.Round(g.Where(x => x.IsPriced && x.CostUsd.HasValue).Sum(x => x.CostUsd!.Value), 4)));

        var positives = byDay
            .Where(kv => kv.Key >= startDate && kv.Key <= todayDate && kv.Value.Tokens > 0)
            .Select(kv => kv.Value.Tokens)
            .OrderBy(v => v)
            .ToList();

        long q1 = positives.Count > 0 ? positives[(int)(positives.Count * 0.25)] : 1;
        long q2 = positives.Count > 0 ? positives[(int)(positives.Count * 0.50)] : 2;
        long q3 = positives.Count > 0 ? positives[(int)(positives.Count * 0.75)] : 3;

        var cells = new List<DailyHeatmapCell>(365);
        int activeDays = 0;

        for (int i = 0; i < 365; i++)
        {
            var d = startDate.AddDays(i);
            if (byDay.TryGetValue(d, out var stat) && stat.Requests > 0)
            {
                activeDays++;
                int level = stat.Tokens <= 0 ? 1 :
                    stat.Tokens <= q1 ? 1 :
                    stat.Tokens <= q2 ? 2 :
                    stat.Tokens <= q3 ? 3 : 4;
                cells.Add(new DailyHeatmapCell
                {
                    Date = d.ToString("yyyy-MM-dd"),
                    Requests = stat.Requests,
                    TotalTokens = stat.Tokens,
                    CostUsd = stat.Cost,
                    Level = level
                });
            }
            else
            {
                cells.Add(new DailyHeatmapCell
                {
                    Date = d.ToString("yyyy-MM-dd"),
                    Requests = 0,
                    TotalTokens = 0,
                    CostUsd = 0,
                    Level = 0
                });
            }
        }

        return (cells, activeDays);
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

    /// <summary>
    /// 构建自适应多规则配额、预算护栏与订阅流量聚合总览
    /// </summary>
    public QuotaOverviewDto BuildQuotaOverview(
        HubStateDocument doc,
        IReadOnlyList<SeatTrafficSnapshot> proxySnapshots,
        IReadOnlyList<SeatTakeoverStatus>? takeovers = null,
        DateTimeOffset? nowOverride = null,
        IReadOnlyList<UsageLogEntry>? entriesOverride = null,
        IReadOnlyDictionary<string, CodexRateLimitSnapshot>? rateLimitsOverride = null)
    {
        bool needsInitialSync;
        lock (_lock)
        {
            needsInitialSync = _lastSync == null;
        }
        if (entriesOverride == null && needsInitialSync)
        {
            _scanner.SyncExtraCodexProfileDirs(doc.Instances);
            SyncNow();
        }
        else
        {
            _scanner.SyncExtraCodexProfileDirs(doc.Instances);
        }

        List<UsageLogEntry> snapshot;
        lock (_lock)
        {
            snapshot = entriesOverride != null ? entriesOverride.ToList() : _entries.ToList();
        }

        var rateLimits = rateLimitsOverride ?? _scanner.GetLatestCodexRateLimits();
        var now = (nowOverride ?? DateTimeOffset.Now).ToLocalTime();
        var quotaCfg = doc.QuotaSettings ?? new QuotaBudgetSettings();
        int warnPct = Math.Clamp(quotaCfg.WarnPercent, 10, 98);
        int criticalPct = Math.Clamp(quotaCfg.CriticalPercent, warnPct + 1, 99);

        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        var weekAgo = now.AddDays(-7);

        var todayEntries = snapshot.Where(e => e.Timestamp.ToLocalTime() >= todayStart).ToList();
        var monthEntries = snapshot.Where(e => e.Timestamp.ToLocalTime() >= monthStart).ToList();
        var weekEntries = snapshot.Where(e => e.Timestamp.ToLocalTime() >= weekAgo).ToList();

        double dailySpent = Math.Round(todayEntries.Where(e => e.IsPriced && e.CostUsd.HasValue).Sum(e => e.CostUsd!.Value), 4);
        double monthlySpent = Math.Round(monthEntries.Where(e => e.IsPriced && e.CostUsd.HasValue).Sum(e => e.CostUsd!.Value), 4);
        double todaySaved = Math.Round(todayEntries.Where(e => e.IsPriced).Sum(e => e.CacheSavedUsd), 4);

        double savingsRatio = (dailySpent + todaySaved) > 0
            ? Math.Round(todaySaved * 100.0 / (dailySpent + todaySaved), 1)
            : 0;

        double burnRate = 0;
        if (todayEntries.Count > 0 && dailySpent > 0)
        {
            var firstTs = todayEntries.Min(e => e.Timestamp.ToLocalTime());
            double activeHours = Math.Max(0.25, (now - firstTs).TotalHours);
            burnRate = Math.Round(dailySpent / activeHours, 2);
        }

        double? dailyPct = quotaCfg.DailyBudgetUsd > 0
            ? Math.Round(Math.Clamp(dailySpent * 100.0 / quotaCfg.DailyBudgetUsd, 0, 100), 1)
            : null;
        double? monthlyPct = quotaCfg.MonthlyBudgetUsd > 0
            ? Math.Round(Math.Clamp(monthlySpent * 100.0 / quotaCfg.MonthlyBudgetUsd, 0, 100), 1)
            : null;

        // 构造待评估席位列表（基础席位 + 多开实例虚拟席位）
        var allSeats = doc.Seats.ToList();
        foreach (var inst in doc.Instances)
        {
            var baseSeat = doc.Seats.FirstOrDefault(s => s.Id.Equals(inst.BaseSeatId, StringComparison.OrdinalIgnoreCase));
            if (baseSeat != null) allSeats.Add(inst.ToVirtualSeat(baseSeat));
        }

        var agentCards = new List<AgentQuotaCardDto>();
        foreach (var seat in allSeats)
        {
            var seatToday = FilterEntriesForSeat(todayEntries, seat);
            var seatWeek = FilterEntriesForSeat(weekEntries, seat);
            rateLimits.TryGetValue(seat.Id, out var codexRl);
            quotaCfg.SeatRules.TryGetValue(seat.Id, out var rule);
            var snap = proxySnapshots.FirstOrDefault(s => s.SeatId.Equals(seat.Id, StringComparison.OrdinalIgnoreCase));
            var tk = takeovers?.FirstOrDefault(t => t.SeatId.Equals(seat.Id, StringComparison.OrdinalIgnoreCase));

            bool isRunning = (tk?.IsProcessRunning ?? false) || (snap?.ActiveConnections > 0) || seat.Enabled;
            bool hasActivity = isRunning || seatWeek.Count > 0 || codexRl != null || rule != null;
            if (!hasActivity || seat.IsHidden && seatWeek.Count == 0 && !(tk?.IsProcessRunning ?? false))
                continue;

            double sTodayCost = Math.Round(seatToday.Where(e => e.IsPriced && e.CostUsd.HasValue).Sum(e => e.CostUsd!.Value), 4);
            long sTodayTokens = seatToday.Sum(e => e.TotalTokens);
            double sTodaySaved = Math.Round(seatToday.Where(e => e.IsPriced).Sum(e => e.CacheSavedUsd), 4);

            var windows = new List<QuotaWindowDto>();
            string? planBadge = null;
            string? creditsBadge = null;

            bool hasCustomRule = rule != null && !string.Equals(rule.BillingMode, "auto", StringComparison.OrdinalIgnoreCase);
            if (hasCustomRule && rule != null)
            {
                BuildWindowsFromCustomRule(seat, rule, snapshot, quotaCfg, warnPct, criticalPct, now, windows);
                planBadge = rule.BillingMode switch
                {
                    "subscription_windows" => "订阅规则",
                    "budget_usd" => "预算上限",
                    "token_quota" => "Token 配额",
                    _ => null
                };
            }
            else if (codexRl != null)
            {
                if (!string.IsNullOrWhiteSpace(codexRl.PlanType))
                    planBadge = codexRl.PlanType.ToUpperInvariant();

                if (codexRl.UnlimitedCredits)
                    creditsBadge = "Unlimited";
                else if (codexRl.HasCredits && codexRl.CreditsBalance.HasValue && codexRl.CreditsBalance.Value > 0)
                    creditsBadge = $"Credits {codexRl.CreditsBalance.Value:N0}";

                if (codexRl.PrimaryWindowMinutes.HasValue)
                    windows.Add(BuildNativeCodexWindow("primary", codexRl.PrimaryWindowMinutes.Value, codexRl.PrimaryUsedPercent ?? 0, codexRl.PrimaryResetsAt, warnPct, criticalPct, now));

                if (codexRl.SecondaryWindowMinutes.HasValue)
                    windows.Add(BuildNativeCodexWindow("secondary", codexRl.SecondaryWindowMinutes.Value, codexRl.SecondaryUsedPercent ?? 0, codexRl.SecondaryResetsAt, warnPct, criticalPct, now));
            }
            else if (seat.Id.Equals("claude", StringComparison.OrdinalIgnoreCase))
            {
                // Claude Code：自动追踪当前 5 小时滚动会话块
                var blockCutoff = now.AddHours(-5);
                var blockEntries = FilterEntriesForSeat(snapshot, seat)
                    .Where(e => e.Timestamp.ToLocalTime() >= blockCutoff)
                    .ToList();

                double blockCost = Math.Round(blockEntries.Where(e => e.IsPriced && e.CostUsd.HasValue).Sum(e => e.CostUsd!.Value), 4);
                long blockTokens = blockEntries.Sum(e => e.TotalTokens);
                DateTimeOffset? resetsAt = blockEntries.Count > 0
                    ? blockEntries.Min(e => e.Timestamp.ToLocalTime()).AddHours(5)
                    : null;

                double? pct = (rule?.DailyBudgetUsd is > 0 && sTodayCost > 0)
                    ? Math.Round(Math.Clamp(sTodayCost * 100.0 / rule.DailyBudgetUsd.Value, 0, 100), 1)
                    : null;

                windows.Add(new QuotaWindowDto
                {
                    Key = "claude-5h",
                    Label = "5h 会话块",
                    WindowMinutes = 300,
                    UsedPercent = pct,
                    UsedDisplay = blockEntries.Count > 0
                        ? $"${blockCost:F2} · {FormatCompactTokenCount(blockTokens)}"
                        : "当前空闲",
                    SubDisplay = resetsAt.HasValue ? FormatResetCountdown(resetsAt.Value, now) : "新对话即刻开启 5h 计费块",
                    ResetsAt = resetsAt,
                    IsNativeServerPercent = false,
                    Tone = EvalTone(pct, warnPct, criticalPct)
                });

                if (seatWeek.Count > 0)
                {
                    double wCost = Math.Round(seatWeek.Where(e => e.IsPriced && e.CostUsd.HasValue).Sum(e => e.CostUsd!.Value), 4);
                    long wTokens = seatWeek.Sum(e => e.TotalTokens);
                    windows.Add(new QuotaWindowDto
                    {
                        Key = "claude-7d",
                        Label = "近 7 日累计",
                        WindowMinutes = 10080,
                        UsedPercent = null,
                        UsedDisplay = $"${wCost:F2} · {FormatCompactTokenCount(wTokens)}",
                        SubDisplay = $"本周 {seatWeek.Count} 次调用",
                        Tone = "ok"
                    });
                }
            }
            else
            {
                double seatCap = rule?.DailyBudgetUsd is > 0
                    ? rule.DailyBudgetUsd.Value
                    : (quotaCfg.DailyBudgetUsd > 0 ? quotaCfg.DailyBudgetUsd : 0);
                double? pct = seatCap > 0
                    ? Math.Round(Math.Clamp(sTodayCost * 100.0 / seatCap, 0, 100), 1)
                    : null;

                windows.Add(new QuotaWindowDto
                {
                    Key = "daily",
                    Label = "今日支出",
                    WindowMinutes = 1440,
                    UsedPercent = pct,
                    UsedDisplay = sTodayTokens > 0
                        ? (pct.HasValue ? $"${sTodayCost:F2} / ${seatCap:F0} ({pct:0.#}%)" : $"${sTodayCost:F2} · {FormatCompactTokenCount(sTodayTokens)}")
                        : (isRunning ? "端口监听中 · 待调用" : "今日暂无调用"),
                    SubDisplay = seatToday.Count > 0
                        ? $"今日 {seatToday.Count} 次 · 省 ${sTodaySaved:F2}"
                        : (seatWeek.Count > 0 ? $"近 7 日 {seatWeek.Count} 次" : null),
                    ResetsAt = todayStart.AddDays(1),
                    Tone = EvalTone(pct, warnPct, criticalPct)
                });

                if (seatWeek.Count > seatToday.Count)
                {
                    double wCost = Math.Round(seatWeek.Where(e => e.IsPriced && e.CostUsd.HasValue).Sum(e => e.CostUsd!.Value), 4);
                    long wTokens = seatWeek.Sum(e => e.TotalTokens);
                    windows.Add(new QuotaWindowDto
                    {
                        Key = "weekly",
                        Label = "近 7 日累计",
                        WindowMinutes = 10080,
                        UsedPercent = null,
                        UsedDisplay = $"${wCost:F2} · {FormatCompactTokenCount(wTokens)}",
                        SubDisplay = $"共 {seatWeek.Count} 次调用",
                        Tone = "ok"
                    });
                }
            }

            string highestTone = "ok";
            double maxPct = 0;
            foreach (var w in windows)
            {
                if (w.UsedPercent.HasValue && w.UsedPercent.Value > maxPct)
                    maxPct = w.UsedPercent.Value;
                if (ToneRank(w.Tone) > ToneRank(highestTone))
                    highestTone = w.Tone;
            }

            agentCards.Add(new AgentQuotaCardDto
            {
                SeatId = seat.Id,
                AppName = seat.Name,
                PlanBadge = planBadge,
                CreditsBadge = creditsBadge,
                IsRunning = isRunning,
                TodayCostUsd = sTodayCost,
                TodayTokens = sTodayTokens,
                TodayCacheSavedUsd = sTodaySaved,
                Windows = windows,
                HighestTone = highestTone,
                MaxUsedPercent = maxPct
            });
        }

        agentCards = agentCards
            .OrderByDescending(a => ToneRank(a.HighestTone))
            .ThenByDescending(a => a.MaxUsedPercent)
            .ThenByDescending(a => a.IsRunning)
            .ThenByDescending(a => a.TodayTokens)
            .ToList();

        var trafficPools = SubscriptionTrafficParser.BuildTrafficPoolCards(doc.Subscriptions, doc.Nodes, now);

        string headlineTone = "ok";
        string headlineStatus = agentCards.Count > 0
            ? $"配额稳态 · {agentCards.Count} 席活跃"
            : "配额待命";

        string dailyTone = EvalTone(dailyPct, warnPct, criticalPct);
        if (ToneRank(dailyTone) > ToneRank(headlineTone)) headlineTone = dailyTone;

        var topAlertAgent = agentCards.FirstOrDefault(a => a.HighestTone != "ok");
        if (topAlertAgent != null)
        {
            var topWin = topAlertAgent.Windows.OrderByDescending(w => w.UsedPercent ?? 0).FirstOrDefault();
            headlineTone = topAlertAgent.HighestTone;
            headlineStatus = topWin?.UsedPercent != null
                ? $"{topAlertAgent.AppName} {topWin.Label} {topWin.UsedPercent:0.#}%"
                : $"{topAlertAgent.AppName} 接近配额阈值";
        }
        else if (dailyPct.HasValue && dailyPct.Value >= warnPct)
        {
            headlineStatus = $"今日预算已用 {dailyPct:0.#}%";
        }
        else if (trafficPools.Any(t => t.Tone == "critical"))
        {
            var lowPool = trafficPools.First(t => t.Tone == "critical");
            headlineTone = "critical";
            headlineStatus = $"{lowPool.Name} 流量/期限告急";
        }

        return new QuotaOverviewDto
        {
            DailySpentUsd = dailySpent,
            DailyBudgetUsd = quotaCfg.DailyBudgetUsd,
            DailyUsedPercent = dailyPct,
            MonthlySpentUsd = monthlySpent,
            MonthlyBudgetUsd = quotaCfg.MonthlyBudgetUsd,
            MonthlyUsedPercent = monthlyPct,
            BurnRateUsdPerHour = burnRate,
            TodayCacheSavedUsd = todaySaved,
            CacheSavingsRatioPercent = savingsRatio,
            WarnPercent = warnPct,
            CriticalPercent = criticalPct,
            HeadlineStatus = headlineStatus,
            HeadlineTone = headlineTone,
            ActiveAgents = agentCards,
            TrafficPools = trafficPools
        };
    }

    private static List<UsageLogEntry> FilterEntriesForSeat(List<UsageLogEntry> entries, AppSeat seat)
    {
        if (seat.IsInstance)
        {
            return entries.Where(e => e.AppName.Equals(seat.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        string id = seat.Id.ToLowerInvariant();
        return entries.Where(e =>
        {
            string app = e.AppName.ToLowerInvariant();
            return id switch
            {
                "claude" => app is "claude code" or "claude",
                "codex" => app is "codex",
                "agy" or "antigravity" => app is "antigravity" or "agy",
                _ => app.Equals(id, StringComparison.OrdinalIgnoreCase) || app.Equals(seat.Name, StringComparison.OrdinalIgnoreCase)
            };
        }).ToList();
    }

    private static QuotaWindowDto BuildNativeCodexWindow(
        string key,
        int windowMinutes,
        double usedPercent,
        DateTimeOffset? resetsAt,
        int warnPct,
        int criticalPct,
        DateTimeOffset now)
    {
        bool expired = resetsAt.HasValue && resetsAt.Value <= now;
        double effectivePct = expired ? 0 : Math.Clamp(Math.Round(usedPercent, 1), 0, 100);
        string subText = resetsAt.HasValue
            ? (expired ? "新周期已重置" : FormatResetCountdown(resetsAt.Value, now))
            : "原生配额窗口";

        return new QuotaWindowDto
        {
            Key = key,
            Label = FormatWindowLabel(windowMinutes),
            WindowMinutes = windowMinutes,
            UsedPercent = effectivePct,
            UsedDisplay = $"{effectivePct:0.#}%",
            SubDisplay = subText,
            ResetsAt = resetsAt,
            IsNativeServerPercent = true,
            Tone = EvalTone(effectivePct, warnPct, criticalPct)
        };
    }

    private static void BuildWindowsFromCustomRule(
        AppSeat seat,
        SeatQuotaRule rule,
        List<UsageLogEntry> allEntries,
        QuotaBudgetSettings globalCfg,
        int warnPct,
        int criticalPct,
        DateTimeOffset now,
        List<QuotaWindowDto> output)
    {
        void AddBucket(string key, int windowMins, double? limitVal)
        {
            DateTimeOffset start = windowMins switch
            {
                1440 => new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset),
                43200 => new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset),
                _ => now.AddMinutes(-windowMins)
            };
            var list = FilterEntriesForSeat(allEntries, seat)
                .Where(e => e.Timestamp.ToLocalTime() >= start)
                .ToList();

            double cost = Math.Round(list.Where(x => x.IsPriced && x.CostUsd.HasValue).Sum(x => x.CostUsd!.Value), 4);
            long tokens = list.Sum(x => x.TotalTokens);
            DateTimeOffset resetAt = windowMins switch
            {
                1440 => start.AddDays(1),
                43200 => start.AddMonths(1),
                _ => list.Count > 0 ? list.Min(x => x.Timestamp.ToLocalTime()).AddMinutes(windowMins) : now.AddMinutes(windowMins)
            };

            double? pct = null;
            string display;
            if (rule.BillingMode == "token_quota" && limitVal is > 0)
            {
                double capTokens = limitVal.Value * 1_000_000.0; // 输入单位为 M Tokens
                pct = Math.Round(Math.Clamp(tokens * 100.0 / capTokens, 0, 100), 1);
                display = $"{FormatCompactTokenCount(tokens)} / {limitVal.Value:0.#}M ({pct:0.#}%)";
            }
            else if (limitVal is > 0)
            {
                pct = Math.Round(Math.Clamp(cost * 100.0 / limitVal.Value, 0, 100), 1);
                display = $"${cost:F2} / ${limitVal.Value:0.##} ({pct:0.#}%)";
            }
            else
            {
                display = $"${cost:F2} · {FormatCompactTokenCount(tokens)}";
            }

            output.Add(new QuotaWindowDto
            {
                Key = key,
                Label = FormatWindowLabel(windowMins),
                WindowMinutes = windowMins,
                UsedPercent = pct,
                UsedDisplay = display,
                SubDisplay = list.Count > 0 ? FormatResetCountdown(resetAt, now) : "当前周期尚无消耗",
                ResetsAt = resetAt,
                Tone = EvalTone(pct, warnPct, criticalPct)
            });
        }

        int pMins = rule.PrimaryWindowMinutes is > 0 ? rule.PrimaryWindowMinutes.Value : (rule.BillingMode == "budget_usd" ? 1440 : 300);
        double? pLim = rule.PrimaryLimit ?? rule.DailyBudgetUsd ?? (rule.BillingMode == "budget_usd" && globalCfg.DailyBudgetUsd > 0 ? globalCfg.DailyBudgetUsd : null);
        AddBucket("primary", pMins, pLim);

        if (rule.SecondaryWindowMinutes is > 0)
        {
            AddBucket("secondary", rule.SecondaryWindowMinutes.Value, rule.SecondaryLimit);
        }
    }

    public static string FormatWindowLabel(int windowMinutes) => windowMinutes switch
    {
        <= 60 => $"{Math.Max(1, windowMinutes)}m 窗口",
        300 => "5h 窗口",
        1440 => "日额度",
        10080 => "周额度",
        >= 40320 and <= 44640 => "月额度",
        < 1440 when windowMinutes % 60 == 0 => $"{windowMinutes / 60}h 窗口",
        >= 1440 when windowMinutes % 1440 == 0 => $"{windowMinutes / 1440}d 额度",
        _ => $"{windowMinutes}m 窗口"
    };

    private static string FormatResetCountdown(DateTimeOffset resetsAt, DateTimeOffset now)
    {
        var diff = resetsAt - now;
        if (diff <= TimeSpan.Zero) return "新周期已重置";
        if (diff.TotalHours < 1) return $"{Math.Max(1, (int)Math.Ceiling(diff.TotalMinutes))}m 后重置";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h {diff.Minutes}m 后重置";
        return $"{(int)diff.TotalDays}d {diff.Hours}h 后重置";
    }

    private static string EvalTone(double? pct, int warnPct, int criticalPct)
    {
        if (!pct.HasValue) return "ok";
        if (pct.Value >= criticalPct) return "critical";
        if (pct.Value >= warnPct) return "warn";
        return "ok";
    }

    private static int ToneRank(string tone) => tone switch
    {
        "critical" => 2,
        "warn" => 1,
        _ => 0
    };

    private static string FormatCompactTokenCount(long tokens)
    {
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:0.##}M";
        if (tokens >= 1_000) return $"{tokens / 1_000.0:0.#}K";
        return tokens.ToString();
    }
}
