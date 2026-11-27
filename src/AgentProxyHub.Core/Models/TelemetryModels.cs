namespace AgentProxyHub.Core.Models;

/// <summary>
/// 模型百万 Token 定价规则（美元 / 1M Tokens）
/// </summary>
public sealed class ModelPricingRule
{
    public string Pattern { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public double InputPerMillion { get; set; }
    public double OutputPerMillion { get; set; }
    public double CacheReadPerMillion { get; set; }
    public double CacheWritePerMillion { get; set; }
    public bool IsCustom { get; set; }
}

/// <summary>
/// 单次 AI 模型调用记录（源自免解密本地会话日志或本地计量网关）
/// </summary>
public sealed class UsageLogEntry
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string AppName { get; set; } = string.Empty;   // Antigravity | Claude Code | Codex
    public string Source { get; set; } = "会话日志";       // 会话日志 | 计量网关
    public string Model { get; set; } = string.Empty;
    public long EffectiveInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public long TotalTokens => EffectiveInputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;
    public double DurationSeconds { get; set; }
    public double? FirstTokenSeconds { get; set; }
    public double? TokensPerSecond { get; set; }
    public double? CostUsd { get; set; }
    public double CacheSavedUsd { get; set; }
    public bool IsPriced { get; set; }
    public string? Workspace { get; set; }
}

/// <summary>
/// 过去 365 天活跃热力图单元格
/// </summary>
public sealed class DailyHeatmapCell
{
    public string Date { get; set; } = string.Empty; // yyyy-MM-dd
    public int Requests { get; set; }
    public long TotalTokens { get; set; }
    public double CostUsd { get; set; }
    public int Level { get; set; } // 0..4
}

/// <summary>
/// 本地代理隧道实时流量快照（按席位与绑定专线统计）
/// </summary>
public sealed class SeatTrafficSnapshot
{
    public string SeatId { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public int LocalPort { get; set; }
    public string BoundNodeName { get; set; } = "未绑定";
    public string ExitIp { get; set; } = "—";
    public long ActiveConnections { get; set; }
    public long TotalConnections { get; set; }
    public long BytesTx { get; set; }
    public long BytesRx { get; set; }
    public long BytesTxPerSec { get; set; }
    public long BytesRxPerSec { get; set; }
    public long SniRecoveredCount { get; set; }
    public long KillSwitchBlockedCount { get; set; }
    public long Ipv6BlockedCount { get; set; }
}

/// <summary>
/// 趋势图时间桶（按小时或按天）
/// </summary>
public sealed class UsageBucketPoint
{
    public string Label { get; set; } = string.Empty;
    public int Requests { get; set; }
    public long TotalTokens { get; set; }
    public long EffectiveInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public double CacheHitRate { get; set; }
    public double CostUsd { get; set; }
}

/// <summary>
/// 按模型维度聚合汇总
/// </summary>
public sealed class ModelUsageSummary
{
    public string Model { get; set; } = string.Empty;
    public int Requests { get; set; }
    public long EffectiveInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long TotalTokens { get; set; }
    public double CacheHitRate { get; set; }
    public double AvgTokensPerSecond { get; set; }
    public double CostUsd { get; set; }
    public bool HasPricing { get; set; }
}

/// <summary>
/// 应用用量与代理专线流量二维合并视图
/// </summary>
public sealed class AppUsageAndProxySummary
{
    public string AppName { get; set; } = string.Empty;
    public int Requests { get; set; }
    public long TotalTokens { get; set; }
    public double CacheHitRate { get; set; }
    public double CostUsd { get; set; }
    public string? SeatPort { get; set; }
    public string? BoundNodeName { get; set; }
    public string? ExitIp { get; set; }
    public long ProxyConnections { get; set; }
    public long BytesTx { get; set; }
    public long BytesRx { get; set; }
}

/// <summary>
/// 统计页大盘聚合结果
/// </summary>
public sealed class UsageDashboardResponse
{
    public string Range { get; set; } = "today";
    public DateTimeOffset? LastSyncedAt { get; set; }
    public int ScannedFilesCount { get; set; }
    public long SyncElapsedMs { get; set; }

    // 四大核心 KPI
    public double TotalCostUsd { get; set; }
    public double TotalCacheSavedUsd { get; set; }
    public int TotalRequests { get; set; }
    public long TotalTokens { get; set; }
    public long EffectiveInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public double CacheHitRate { get; set; }
    public int PricedRequests { get; set; }
    public int UnpricedRequests { get; set; }

    // 365 天活跃热力图
    public int ActiveDaysLastYear { get; set; }
    public List<DailyHeatmapCell> ActivityHeatmap { get; set; } = new();

    // 代理网络层总流量
    public long TotalProxyTxBytes { get; set; }
    public long TotalProxyRxBytes { get; set; }
    public long TotalProxyTxBytesPerSec { get; set; }
    public long TotalProxyRxBytesPerSec { get; set; }
    public long TotalProxyConnections { get; set; }

    // 筛选选项与计数
    public List<AppFilterChip> AppChips { get; set; } = new();
    public List<string> AvailableModels { get; set; } = new();

    // 序列与明细
    public List<UsageBucketPoint> Timeline { get; set; } = new();
    public List<UsageLogEntry> Logs { get; set; } = new();
    public int TotalLogsCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<ModelUsageSummary> ModelBreakdown { get; set; } = new();
    public List<AppUsageAndProxySummary> AppAndProxyBreakdown { get; set; } = new();
}

public sealed class AppFilterChip
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class SyncStatsResult
{
    public DateTimeOffset SyncedAt { get; set; }
    public int ScannedFiles { get; set; }
    public int ParsedEntries { get; set; }
    public long ElapsedMs { get; set; }
}
