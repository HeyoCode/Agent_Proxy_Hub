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
    public string? ConversationId { get; set; }
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
    public string? EffectiveNodeName { get; set; }
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
    public List<WorkspaceUsageSummary> WorkspaceBreakdown { get; set; } = new();
    public QuotaOverviewDto? Quota { get; set; }
}

/// <summary>
/// 从本地会话日志提取的原生速率限制快照（例如 Codex rate_limits：支持 5h+7d 双窗、单周 prolite、日窗与 Credits）
/// </summary>
public sealed class CodexRateLimitSnapshot
{
    public string SeatKey { get; set; } = "codex";
    public DateTimeOffset Timestamp { get; set; }
    public string? PlanType { get; set; }
    public double? PrimaryUsedPercent { get; set; }
    public int? PrimaryWindowMinutes { get; set; }
    public DateTimeOffset? PrimaryResetsAt { get; set; }
    public double? SecondaryUsedPercent { get; set; }
    public int? SecondaryWindowMinutes { get; set; }
    public DateTimeOffset? SecondaryResetsAt { get; set; }
    public bool HasCredits { get; set; }
    public bool UnlimitedCredits { get; set; }
    public double? CreditsBalance { get; set; }
}

/// <summary>
/// 自适应单个配额窗口槽位（由实际周期分钟数决定显示 5h 窗口 / 日额度 / 周额度 / 月额度）
/// </summary>
public sealed class QuotaWindowDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int WindowMinutes { get; set; }
    public double? UsedPercent { get; set; }
    public string UsedDisplay { get; set; } = string.Empty;
    public string? SubDisplay { get; set; }
    public DateTimeOffset? ResetsAt { get; set; }
    public bool IsNativeServerPercent { get; set; }
    public string Tone { get; set; } = "ok"; // ok | warn | critical
}

/// <summary>
/// 单个活跃 AI 席位（或多开隔离实例）的动态配额微卡
/// </summary>
public sealed class AgentQuotaCardDto
{
    public string SeatId { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string? PlanBadge { get; set; }
    public string? CreditsBadge { get; set; }
    public bool IsRunning { get; set; }
    public double TodayCostUsd { get; set; }
    public long TodayTokens { get; set; }
    public double TodayCacheSavedUsd { get; set; }
    public List<QuotaWindowDto> Windows { get; set; } = new();
    public string HighestTone { get; set; } = "ok"; // ok | warn | critical
    public double MaxUsedPercent { get; set; }
}

/// <summary>
/// 订阅源或分组导入池聚合流量额度与到期卡片（融合 Subscription-Userinfo 头与伪节点提取结果）
/// </summary>
public sealed class SubscriptionTrafficCardDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public bool IsManagedSubscription { get; set; }
    public int AutoRefreshHours { get; set; }
    public string? LastError { get; set; }
    public int NodeCount { get; set; }
    public int InfoEntryCount { get; set; }
    public long? UsedBytes { get; set; }
    public long? TotalBytes { get; set; }
    public long? RemainingBytes { get; set; }
    public double? UsedPercent { get; set; }
    public double? RemainingPercent { get; set; }
    public DateTimeOffset? ExpireAt { get; set; }
    public int? RemainingDays { get; set; }
    public int? ResetInDays { get; set; }
    public string Tone { get; set; } = "ok"; // ok | warn | critical
}

/// <summary>
/// 全局与多 Agent 自适应配额、预算护栏及订阅流量总览
/// </summary>
public sealed class QuotaOverviewDto
{
    public double DailySpentUsd { get; set; }
    public double DailyBudgetUsd { get; set; }
    public double? DailyUsedPercent { get; set; }
    public double MonthlySpentUsd { get; set; }
    public double MonthlyBudgetUsd { get; set; }
    public double? MonthlyUsedPercent { get; set; }
    public double BurnRateUsdPerHour { get; set; }
    public double TodayCacheSavedUsd { get; set; }
    public double CacheSavingsRatioPercent { get; set; }
    public int WarnPercent { get; set; } = 65;
    public int CriticalPercent { get; set; } = 85;
    public string HeadlineStatus { get; set; } = "配额稳态";
    public string HeadlineTone { get; set; } = "ok"; // ok | warn | critical
    public List<AgentQuotaCardDto> ActiveAgents { get; set; } = new();
    public List<SubscriptionTrafficCardDto> TrafficPools { get; set; } = new();
}

/// <summary>
/// 按项目工作区聚合的成本与算力排行
/// </summary>
public sealed class WorkspaceUsageSummary
{
    public string Workspace { get; set; } = string.Empty;
    public int Requests { get; set; }
    public long TotalTokens { get; set; }
    public double CostUsd { get; set; }
    public string TopApp { get; set; } = string.Empty;
    public string TopModel { get; set; } = string.Empty;
}

/// <summary>
/// 单会话内工具调用频次分布
/// </summary>
public sealed class ToolUsageItem
{
    public string ToolName { get; set; } = string.Empty;
    public int Calls { get; set; }
}

/// <summary>
/// 单次对话轮次的上下文膨胀与工具审计明细
/// </summary>
public sealed class ConversationTurnAudit
{
    public int TurnIndex { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string Model { get; set; } = string.Empty;
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CumulativeContextTokens { get; set; }
    public double DurationSeconds { get; set; }
    public double? CostUsd { get; set; }
    public List<string> ToolsCalled { get; set; } = new();
}

/// <summary>
/// 单对话上下文轨迹与工具调用审计报告
/// </summary>
public sealed class ConversationAuditReport
{
    public string ConversationId { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string? Workspace { get; set; }
    public string? FilePath { get; set; }
    public int TotalTurns { get; set; }
    public long PeakContextTokens { get; set; }
    public double TotalCostUsd { get; set; }
    public double TotalCacheSavedUsd { get; set; }
    public double ContextGrowthRatio { get; set; }
    public string? OptimizationHint { get; set; }
    public List<ToolUsageItem> ToolBreakdown { get; set; } = new();
    public List<ConversationTurnAudit> Turns { get; set; } = new();
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
