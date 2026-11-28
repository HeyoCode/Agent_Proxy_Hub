using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AgentProxyHub.Core.Models;

namespace AgentProxyHub.Core.Telemetry;

/// <summary>
/// 本地 AI 工具会话日志增量扫描器（免 MITM 解密、零正文读取）
/// 仅提取时间戳、模型名称、去重 ID 与 Token 计数字段，绝不读取提示词/代码正文
/// </summary>
public sealed class SessionLogScanner
{
    private sealed record CachedFileResult(
        DateTime LastWriteUtc,
        long Length,
        List<UsageLogEntry> Entries,
        CodexRateLimitSnapshot? LatestRateLimit = null);

    private readonly string _antigravityBrainDir;
    private readonly string _antigravityCliBrainDir;
    private readonly string _claudeProjectsDir;
    private readonly string _codexSessionsDir;
    private readonly ConcurrentDictionary<string, string> _extraCodexDirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedFileResult> _fileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _conversationFileMap = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, CodexRateLimitSnapshot> _latestRateLimits = new(StringComparer.OrdinalIgnoreCase);
    private List<UsageLogEntry>? _cachedOrdered;
    private int _lastFileCount = -1;

    /// <summary>
    /// 同步受管多开实例的独立 Codex 会话目录，使工作号/个人号隔离实例也能各自采集原生 rate_limits
    /// </summary>
    public void SyncExtraCodexProfileDirs(IEnumerable<ManagedAppInstance>? instances)
    {
        var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (instances != null)
        {
            foreach (var inst in instances)
            {
                if (!inst.BaseSeatId.Equals("codex", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(inst.ProfileDir)) continue;
                string sessionsDir = Path.Combine(inst.ProfileDir.Trim(), "sessions");
                string seatKey = $"codex:inst:{inst.Id}";
                activeKeys.Add(seatKey);
                _extraCodexDirs[seatKey] = sessionsDir;
            }
        }
        foreach (string k in _extraCodexDirs.Keys)
        {
            if (!activeKeys.Contains(k)) _extraCodexDirs.TryRemove(k, out _);
        }
    }

    public IReadOnlyDictionary<string, CodexRateLimitSnapshot> GetLatestCodexRateLimits()
        => _latestRateLimits;

    public SessionLogScanner(string? userHomeOverride = null)
    {
        string home = string.IsNullOrWhiteSpace(userHomeOverride)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userHomeOverride;

        _antigravityBrainDir = Path.Combine(home, ".gemini", "antigravity", "brain");
        _antigravityCliBrainDir = Path.Combine(home, ".gemini", "antigravity-cli", "brain");
        _claudeProjectsDir = Path.Combine(home, ".claude", "projects");
        _codexSessionsDir = Path.Combine(home, ".codex", "sessions");
    }

    public (List<UsageLogEntry> Entries, SyncStatsResult SyncInfo) ScanIncremental()
    {
        var (entries, info, _) = ScanIncrementalWithChangeFlag();
        return (entries, info);
    }

    /// <summary>
    /// 高频增量扫描（支持 5 秒自动轮询）：同时合并 ~/.gemini/antigravity 与 antigravity-cli 会话日志并按 ID 去重
    /// </summary>
    public (List<UsageLogEntry> Entries, SyncStatsResult SyncInfo, bool HasChanges) ScanIncrementalWithChangeFlag()
    {
        var sw = Stopwatch.StartNew();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileCandidates = new List<(string Path, Func<string, (List<UsageLogEntry> Entries, CodexRateLimitSnapshot? RateLimit)> Parser)>();

        if (Directory.Exists(_antigravityBrainDir))
        {
            foreach (string file in SafeEnumerateFiles(_antigravityBrainDir, "transcript.jsonl"))
            {
                if (seenFiles.Add(file)) fileCandidates.Add((file, p => (ParseAntigravityTranscript(p), null)));
            }
        }

        if (Directory.Exists(_antigravityCliBrainDir))
        {
            foreach (string file in SafeEnumerateFiles(_antigravityCliBrainDir, "transcript.jsonl"))
            {
                if (seenFiles.Add(file)) fileCandidates.Add((file, p => (ParseAntigravityCliTranscript(p), null)));
            }
        }

        if (Directory.Exists(_claudeProjectsDir))
        {
            foreach (string file in SafeEnumerateFiles(_claudeProjectsDir, "*.jsonl"))
            {
                if (seenFiles.Add(file)) fileCandidates.Add((file, p => (ParseClaudeSession(p), null)));
            }
        }

        if (Directory.Exists(_codexSessionsDir))
        {
            foreach (string file in SafeEnumerateFiles(_codexSessionsDir, "*.jsonl"))
            {
                if (seenFiles.Add(file)) fileCandidates.Add((file, p => ParseCodexSessionWithRateLimit(p, "codex")));
            }
        }

        foreach (var extra in _extraCodexDirs)
        {
            if (Directory.Exists(extra.Value))
            {
                string seatKey = extra.Key;
                foreach (string file in SafeEnumerateFiles(extra.Value, "*.jsonl"))
                {
                    if (seenFiles.Add(file)) fileCandidates.Add((file, p => ParseCodexSessionWithRateLimit(p, seatKey)));
                }
            }
        }

        int scannedFiles = fileCandidates.Count;
        int reparsedCount = 0;
        var allEntries = new List<UsageLogEntry>();
        var seenEntryIds = new HashSet<string>(StringComparer.Ordinal);
        var rateLimitMap = new Dictionary<string, CodexRateLimitSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var (file, parser) in fileCandidates)
        {
            var (items, rl) = ParseWithCache(file, parser, ref reparsedCount);
            if (rl != null)
            {
                if (!rateLimitMap.TryGetValue(rl.SeatKey, out var existingRl) || rl.Timestamp >= existingRl.Timestamp)
                    rateLimitMap[rl.SeatKey] = rl;
            }
            foreach (var item in items)
            {
                if (!string.IsNullOrWhiteSpace(item.ConversationId))
                {
                    // Desktop 会话优先覆盖 CLI 镜像，保证文件定位一致
                    _conversationFileMap.AddOrUpdate(
                        item.ConversationId,
                        file,
                        (_, existing) => existing.Contains(".system_generated", StringComparison.OrdinalIgnoreCase) && !file.Contains(".system_generated", StringComparison.OrdinalIgnoreCase)
                            ? existing
                            : file);
                }
                // Desktop 与 antigravity-cli 可能镜像同一对话 ID，严格去重避免双计
                if (seenEntryIds.Add(item.Id))
                    allEntries.Add(item);
            }
        }
        _latestRateLimits = rateLimitMap;

        // 清理已从磁盘移除文件的旧缓存项
        bool removedAny = false;
        foreach (string cachedKey in _fileCache.Keys)
        {
            if (!seenFiles.Contains(cachedKey))
            {
                if (_fileCache.TryRemove(cachedKey, out _)) removedAny = true;
            }
        }
        foreach (var kv in _conversationFileMap)
        {
            if (!seenFiles.Contains(kv.Value))
            {
                _conversationFileMap.TryRemove(kv.Key, out _);
            }
        }

        bool changed = _cachedOrdered == null
            || reparsedCount > 0
            || removedAny
            || scannedFiles != _lastFileCount;

        List<UsageLogEntry> ordered;
        if (!changed && _cachedOrdered != null)
        {
            ordered = _cachedOrdered;
        }
        else
        {
            ordered = allEntries
                .OrderByDescending(e => e.Timestamp)
                .ToList();
            _cachedOrdered = ordered;
            _lastFileCount = scannedFiles;
        }

        sw.Stop();
        // 仅在首次冷启动等大批量重新解析文件时触发轻量优化回收，5 秒空闲轮询零 GC
        if (reparsedCount >= 8)
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        }

        var info = new SyncStatsResult
        {
            SyncedAt = DateTimeOffset.UtcNow,
            ScannedFiles = scannedFiles,
            ParsedEntries = ordered.Count,
            ElapsedMs = sw.ElapsedMilliseconds
        };

        return (ordered, info, changed);
    }

    private (List<UsageLogEntry> Entries, CodexRateLimitSnapshot? RateLimit) ParseWithCache(
        string filePath,
        Func<string, (List<UsageLogEntry> Entries, CodexRateLimitSnapshot? RateLimit)> parser,
        ref int reparsedCount)
    {
        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists || fi.Length == 0) return ([], null);

            if (_fileCache.TryGetValue(filePath, out var cached)
                && cached.LastWriteUtc == fi.LastWriteTimeUtc
                && cached.Length == fi.Length)
            {
                return (cached.Entries, cached.LatestRateLimit);
            }

            var (parsed, rl) = parser(filePath);
            _fileCache[filePath] = new CachedFileResult(fi.LastWriteTimeUtc, fi.Length, parsed, rl);
            reparsedCount++;
            return (parsed, rl);
        }
        catch
        {
            return ([], null);
        }
    }

    public static List<UsageLogEntry> ParseAntigravityCliTranscript(string filePath)
    {
        var list = ParseAntigravityTranscript(filePath);
        foreach (var e in list)
            e.Source = "AGY CLI 日志";
        return list;
    }

    /// <summary>
    /// 解析 Antigravity transcript.jsonl
    /// 注意：PLANNER_RESPONSE 的 created_at 是该步开始创建时间，因此真实耗时应向后取「后续步骤 created_at - 当前步 created_at」
    /// </summary>
    public static List<UsageLogEntry> ParseAntigravityTranscript(string filePath)
    {
        var results = new List<UsageLogEntry>();
        string fileTag = ExtractAntigravityConversationTag(filePath);
        string convId = $"agy:{fileTag}";
        string? detectedWorkspace = null;
        var pending = new List<(UsageLogEntry Entry, bool HasThinking, int ThinkingChars, double? ExplicitTtftSec)>();
        var timeline = new List<(DateTimeOffset Ts, int PendingIdx)>();
        int lineIdx = 0;

        foreach (string line in ReadLinesShared(filePath))
        {
            lineIdx++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (detectedWorkspace == null)
            {
                detectedWorkspace ??= TryQuickExtractJsonString(line, "\"workspace\"")
                    ?? TryQuickExtractJsonString(line, "\"cwd\"")
                    ?? TryQuickExtractJsonString(line, "\"working_directory\"");
            }

            if (!line.Contains("\"PLANNER_RESPONSE\"", StringComparison.Ordinal))
            {
                DateTimeOffset? quickTs = TryQuickExtractTimestamp(line, "\"created_at\"");
                if (quickTs.HasValue) timeline.Add((quickTs.Value, -1));
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                DateTimeOffset? ts = TryReadTimestamp(root, "created_at");
                string? stepType = TryGetString(root, "type");
                detectedWorkspace ??= TryGetString(root, "workspace")
                    ?? TryGetString(root, "cwd")
                    ?? TryGetString(root, "working_directory");

                if (stepType == "PLANNER_RESPONSE")
                {
                    string model = TryGetString(root, "model") ?? "antigravity-unknown";
                    long input = TryGetInt64(root, "input_tokens");
                    long cacheRead = TryGetInt64(root, "cache_read_tokens");
                    long output = TryGetInt64(root, "output_tokens");

                    if (input + cacheRead + output > 0 && ts.HasValue)
                    {
                        string? thinking = TryGetString(root, "thinking");
                        bool hasThinking = !string.IsNullOrWhiteSpace(thinking);
                        int thinkingChars = thinking?.Length ?? 0;

                        double? explicitTtft = null;
                        long ttftMs = TryGetInt64(root, "first_token_ms");
                        if (ttftMs <= 0) ttftMs = TryGetInt64(root, "ttft_ms");
                        if (ttftMs > 0) explicitTtft = Math.Round(ttftMs / 1000.0, 1);

                        int stepIndex = (int)TryGetInt64(root, "step_index");
                        var entry = new UsageLogEntry
                        {
                            Id = $"agy:{fileTag}:{stepIndex}:{lineIdx}",
                            Timestamp = ts.Value,
                            AppName = "Antigravity",
                            Source = "会话日志",
                            Model = model,
                            EffectiveInputTokens = input,
                            OutputTokens = output,
                            CacheReadTokens = cacheRead,
                            CacheWriteTokens = 0,
                            Workspace = detectedWorkspace,
                            ConversationId = convId
                        };

                        int pIdx = pending.Count;
                        pending.Add((entry, hasThinking, thinkingChars, explicitTtft));
                        timeline.Add((ts.Value, pIdx));
                        continue;
                    }
                }

                if (ts.HasValue) timeline.Add((ts.Value, -1));
            }
            catch
            {
                // 跳过单行格式不完整记录（例如正在写入中的最后一行）
            }
        }

        // 向后匹配后续步骤时间戳计算每条 PLANNER_RESPONSE 的估算总耗时、流速与首字时间
        for (int i = 0; i < timeline.Count; i++)
        {
            int pIdx = timeline[i].PendingIdx;
            if (pIdx < 0) continue;

            var (entry, hasThinking, thinkingChars, explicitTtft) = pending[pIdx];
            DateTimeOffset startTs = timeline[i].Ts;
            double durationSec = 0;

            for (int j = i + 1; j < timeline.Count; j++)
            {
                double diff = (timeline[j].Ts - startTs).TotalSeconds;
                if (diff > 0.05)
                {
                    if (diff <= 600) durationSec = Math.Round(diff, 1);
                    break;
                }
            }

            entry.DurationSeconds = durationSec;
            entry.TokensPerSecond = (durationSec >= 0.2 && entry.OutputTokens > 0)
                ? Math.Round(entry.OutputTokens / durationSec, 1)
                : null;
            entry.FirstTokenSeconds = explicitTtft ?? EstimateFirstTokenSeconds(
                durationSec,
                entry.EffectiveInputTokens + entry.CacheReadTokens,
                entry.OutputTokens,
                hasThinking,
                thinkingChars);
            if (string.IsNullOrWhiteSpace(entry.Workspace) && !string.IsNullOrWhiteSpace(detectedWorkspace))
                entry.Workspace = detectedWorkspace;

            results.Add(entry);
        }

        return results;
    }

    private static double? EstimateFirstTokenSeconds(
        double durationSec,
        long promptTokens,
        long outputTokens,
        bool hasThinking,
        int thinkingChars)
    {
        if (durationSec < 0.2) return null;

        // 依据上下文预填充与思考阶段占比推算首字时间（TTFT），并钳位在合理区间内
        double prefillRatio = Math.Clamp(0.08 + Math.Log10(Math.Max(100, promptTokens)) * 0.05, 0.12, 0.35);
        double thinkBoost = 0;
        if (hasThinking)
        {
            double approxThinkTokens = thinkingChars / 3.5;
            double totalGen = Math.Max(1, approxThinkTokens + outputTokens);
            thinkBoost = Math.Clamp((approxThinkTokens / totalGen) * 0.52, 0.12, 0.45);
        }

        double ratio = Math.Clamp(prefillRatio + thinkBoost, 0.12, 0.78);
        double est = Math.Round(durationSec * ratio, 1);
        if (est >= durationSec) est = Math.Round(Math.Max(0.1, durationSec - 0.1), 1);
        return Math.Max(0.1, est);
    }

    /// <summary>
    /// 解析 Claude Code 项目会话 JSONL（按 message.id 对流式分段记录去重取终态）
    /// </summary>
    public static List<UsageLogEntry> ParseClaudeSession(string filePath)
    {
        var byMessageId = new Dictionary<string, UsageLogEntry>(StringComparer.Ordinal);
        string sessionTag = Path.GetFileNameWithoutExtension(filePath);
        string convId = $"claude:{sessionTag}";
        string? detectedWorkspace = null;
        DateTimeOffset? prevTime = null;
        int seq = 0;

        foreach (string line in ReadLinesShared(filePath))
        {
            seq++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (detectedWorkspace == null && line.Contains("\"cwd\"", StringComparison.Ordinal))
            {
                detectedWorkspace = TryQuickExtractJsonString(line, "\"cwd\"");
            }

            if (!line.Contains("\"usage\"", StringComparison.Ordinal))
            {
                DateTimeOffset? quickTs = TryQuickExtractTimestamp(line, "\"timestamp\"");
                if (quickTs.HasValue) prevTime = quickTs;
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                DateTimeOffset? ts = TryReadTimestamp(root, "timestamp");
                detectedWorkspace ??= TryGetString(root, "cwd");

                if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object
                    && msg.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    string model = TryGetString(msg, "model") ?? "claude-unknown";
                    if (model == "<synthetic>") { if (ts.HasValue) prevTime = ts; continue; }

                    long input = TryGetInt64(usage, "input_tokens");
                    long cacheWrite = TryGetInt64(usage, "cache_creation_input_tokens");
                    long cacheRead = TryGetInt64(usage, "cache_read_input_tokens");
                    long output = TryGetInt64(usage, "output_tokens");

                    if (input + cacheWrite + cacheRead + output > 0 && ts.HasValue)
                    {
                        string msgId = TryGetString(msg, "id") ?? $"{sessionTag}:{seq}";
                        double durationSec = 0;
                        if (prevTime.HasValue)
                        {
                            double diff = (ts.Value - prevTime.Value).TotalSeconds;
                            if (diff > 0.05 && diff <= 600) durationSec = Math.Round(diff, 1);
                        }
                        double? tps = (durationSec >= 0.2 && output > 0)
                            ? Math.Round(output / durationSec, 1)
                            : null;

                        var candidate = new UsageLogEntry
                        {
                            Id = $"claude:{sessionTag}:{msgId}",
                            Timestamp = ts.Value,
                            AppName = "Claude Code",
                            Source = "会话日志",
                            Model = model,
                            EffectiveInputTokens = input,
                            CacheWriteTokens = cacheWrite,
                            CacheReadTokens = cacheRead,
                            OutputTokens = output,
                            DurationSeconds = durationSec,
                            FirstTokenSeconds = EstimateFirstTokenSeconds(durationSec, input + cacheRead + cacheWrite, output, false, 0),
                            TokensPerSecond = tps,
                            Workspace = detectedWorkspace,
                            ConversationId = convId
                        };

                        if (!byMessageId.TryGetValue(msgId, out var existing) || candidate.TotalTokens >= existing.TotalTokens)
                        {
                            byMessageId[msgId] = candidate;
                        }
                    }
                }

                if (ts.HasValue) prevTime = ts;
            }
            catch
            {
                // 跳过坏行
            }
        }

        if (!string.IsNullOrWhiteSpace(detectedWorkspace))
        {
            foreach (var v in byMessageId.Values)
            {
                if (string.IsNullOrWhiteSpace(v.Workspace)) v.Workspace = detectedWorkspace;
            }
        }

        return byMessageId.Values.ToList();
    }

    public static List<UsageLogEntry> ParseCodexSession(string filePath)
        => ParseCodexSessionWithRateLimit(filePath, "codex").Entries;

    /// <summary>
    /// 解析 OpenAI Codex 会话 JSONL（关联 turn_context.model、token_count 与原生 rate_limits 配额快照）
    /// </summary>
    public static (List<UsageLogEntry> Entries, CodexRateLimitSnapshot? LatestRateLimit) ParseCodexSessionWithRateLimit(
        string filePath,
        string seatKey = "codex")
    {
        var results = new List<UsageLogEntry>();
        CodexRateLimitSnapshot? latestRateLimit = null;
        string sessionTag = Path.GetFileNameWithoutExtension(filePath);
        string convId = $"codex:{sessionTag}";
        string currentModel = "codex-unknown";
        string? detectedWorkspace = null;
        DateTimeOffset? turnStartTime = null;
        long lastCumulativeTotal = -1;
        int seq = 0;

        foreach (string line in ReadLinesShared(filePath))
        {
            seq++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (detectedWorkspace == null && line.Contains("\"cwd\"", StringComparison.Ordinal))
            {
                detectedWorkspace = TryQuickExtractJsonString(line, "\"cwd\"");
            }

            if (!line.Contains("\"turn_context\"", StringComparison.Ordinal)
                && !line.Contains("\"token_count\"", StringComparison.Ordinal)
                && !line.Contains("\"task_started\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                string? rowType = TryGetString(root, "type");
                DateTimeOffset? ts = TryReadTimestamp(root, "timestamp");

                if (rowType == "turn_context" && root.TryGetProperty("payload", out var tc))
                {
                    string? m = TryGetString(tc, "model");
                    if (!string.IsNullOrWhiteSpace(m)) currentModel = m;
                    detectedWorkspace ??= TryGetString(tc, "cwd");
                    if (ts.HasValue) turnStartTime = ts;
                }
                else if (rowType == "event_msg" && root.TryGetProperty("payload", out var payload))
                {
                    string? pType = TryGetString(payload, "type");
                    detectedWorkspace ??= TryGetString(payload, "cwd");
                    if (pType == "task_started" && ts.HasValue)
                    {
                        turnStartTime = ts;
                    }
                    else if (pType == "token_count")
                    {
                        // 提取原生 rate_limits 快照（过滤 primary/secondary 全空且无可用点数的高频状态帧）
                        if (payload.TryGetProperty("rate_limits", out var rlEl) && rlEl.ValueKind == JsonValueKind.Object)
                        {
                            var extracted = TryExtractCodexRateLimit(rlEl, ts ?? DateTimeOffset.UtcNow, seatKey);
                            if (extracted != null)
                            {
                                latestRateLimit = extracted;
                            }
                        }

                        if (payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                            && info.TryGetProperty("last_token_usage", out var lastUsage) && lastUsage.ValueKind == JsonValueKind.Object)
                        {
                            long cumulative = info.TryGetProperty("total_token_usage", out var totUsage) && totUsage.ValueKind == JsonValueKind.Object
                                ? TryGetInt64(totUsage, "total_tokens")
                                : seq;

                            // Codex 同一轮可能会多次推送相同累计值的 token_count，跳过重复快照
                            if (cumulative == lastCumulativeTotal) continue;
                            lastCumulativeTotal = cumulative;

                            long rawInput = TryGetInt64(lastUsage, "input_tokens");
                            long cacheRead = TryGetInt64(lastUsage, "cached_input_tokens");
                            long cacheWrite = TryGetInt64(lastUsage, "cache_write_input_tokens");
                            long output = TryGetInt64(lastUsage, "output_tokens");
                            long effectiveInput = Math.Max(0, rawInput - cacheRead);

                            if (effectiveInput + cacheRead + cacheWrite + output > 0 && ts.HasValue)
                            {
                                double durationSec = 0;
                                if (turnStartTime.HasValue)
                                {
                                    double diff = (ts.Value - turnStartTime.Value).TotalSeconds;
                                    if (diff > 0.05 && diff <= 600) durationSec = Math.Round(diff, 1);
                                }
                                double? tps = (durationSec >= 0.2 && output > 0)
                                    ? Math.Round(output / durationSec, 1)
                                    : null;

                                results.Add(new UsageLogEntry
                                {
                                    Id = $"codex:{sessionTag}:{seq}",
                                    Timestamp = ts.Value,
                                    AppName = seatKey == "codex" ? "Codex" : seatKey,
                                    Source = "会话日志",
                                    Model = currentModel,
                                    EffectiveInputTokens = effectiveInput,
                                    CacheReadTokens = cacheRead,
                                    CacheWriteTokens = cacheWrite,
                                    OutputTokens = output,
                                    DurationSeconds = durationSec,
                                    TokensPerSecond = tps,
                                    Workspace = detectedWorkspace,
                                    ConversationId = convId
                                });
                                turnStartTime = ts;
                            }
                        }
                    }
                }
            }
            catch
            {
                // 跳过单行错误
            }
        }

        if (!string.IsNullOrWhiteSpace(detectedWorkspace))
        {
            foreach (var r in results)
            {
                if (string.IsNullOrWhiteSpace(r.Workspace)) r.Workspace = detectedWorkspace;
            }
        }

        return (results, latestRateLimit);
    }

    private static CodexRateLimitSnapshot? TryExtractCodexRateLimit(
        JsonElement rlEl,
        DateTimeOffset eventTs,
        string seatKey)
    {
        static (double? Pct, int? Mins, DateTimeOffset? ResetAt) ReadBucket(JsonElement root, string prop)
        {
            if (!root.TryGetProperty(prop, out var b) || b.ValueKind != JsonValueKind.Object)
                return (null, null, null);

            double? pct = null;
            if (b.TryGetProperty("used_percent", out var pEl) && pEl.ValueKind == JsonValueKind.Number && pEl.TryGetDouble(out double pVal))
                pct = Math.Clamp(Math.Round(pVal, 1), 0, 100);

            int? mins = null;
            if (b.TryGetProperty("window_minutes", out var wEl) && wEl.ValueKind == JsonValueKind.Number && wEl.TryGetInt32(out int wVal) && wVal > 0)
                mins = wVal;

            DateTimeOffset? resetAt = null;
            if (b.TryGetProperty("resets_at", out var rEl) && rEl.ValueKind == JsonValueKind.Number && rEl.TryGetInt64(out long sec) && sec > 946684800)
                resetAt = DateTimeOffset.FromUnixTimeSeconds(sec).ToLocalTime();

            return (pct, mins, resetAt);
        }

        var (pPct, pMins, pReset) = ReadBucket(rlEl, "primary");
        var (sPct, sMins, sReset) = ReadBucket(rlEl, "secondary");

        bool hasCredits = false;
        bool unlimited = false;
        double? balance = null;
        if (rlEl.TryGetProperty("credits", out var crEl) && crEl.ValueKind == JsonValueKind.Object)
        {
            if (crEl.TryGetProperty("has_credits", out var hc) && hc.ValueKind == JsonValueKind.True)
                hasCredits = true;
            if (crEl.TryGetProperty("unlimited", out var ul) && ul.ValueKind == JsonValueKind.True)
                unlimited = true;
            if (crEl.TryGetProperty("balance", out var balEl))
            {
                if (balEl.ValueKind == JsonValueKind.String && double.TryParse(balEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double b1))
                    balance = Math.Round(b1, 2);
                else if (balEl.ValueKind == JsonValueKind.Number && balEl.TryGetDouble(out double b2))
                    balance = Math.Round(b2, 2);
            }
        }

        // 若主副窗口全空且无有效点数/无限权限（例如某些 limit_id="premium" 的空探针），直接忽略
        if (!pMins.HasValue && !sMins.HasValue && !(hasCredits && balance > 0) && !unlimited)
            return null;

        string? planType = TryGetString(rlEl, "plan_type");
        if (string.IsNullOrWhiteSpace(planType))
        {
            string? lid = TryGetString(rlEl, "limit_name") ?? TryGetString(rlEl, "limit_id");
            if (!string.IsNullOrWhiteSpace(lid) && !lid.Equals("codex", StringComparison.OrdinalIgnoreCase))
                planType = lid;
        }

        return new CodexRateLimitSnapshot
        {
            SeatKey = seatKey,
            Timestamp = eventTs,
            PlanType = planType,
            PrimaryUsedPercent = pPct,
            PrimaryWindowMinutes = pMins,
            PrimaryResetsAt = pReset,
            SecondaryUsedPercent = sPct,
            SecondaryWindowMinutes = sMins,
            SecondaryResetsAt = sReset,
            HasCredits = hasCredits,
            UnlimitedCredits = unlimited,
            CreditsBalance = balance
        };
    }

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        while (sr.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string rootDir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(rootDir, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MaxRecursionDepth = 6
            });
        }
        catch
        {
            return [];
        }
    }

    private static string? TryGetString(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    private static long TryGetInt64(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long val))
                return val;
        }
        return 0;
    }

    private static DateTimeOffset? TryReadTimestamp(JsonElement el, string prop)
    {
        string? raw = TryGetString(el, prop);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            return dto.ToLocalTime();
        return null;
    }

    private static DateTimeOffset? TryQuickExtractTimestamp(string line, string quotedKey)
    {
        int keyIdx = line.IndexOf(quotedKey, StringComparison.Ordinal);
        if (keyIdx < 0) return null;
        int colonIdx = line.IndexOf(':', keyIdx + quotedKey.Length);
        if (colonIdx < 0) return null;
        int q1 = line.IndexOf('"', colonIdx + 1);
        if (q1 < 0) return null;
        int q2 = line.IndexOf('"', q1 + 1);
        if (q2 <= q1 + 1 || q2 - q1 > 48) return null;
        var slice = line.AsSpan(q1 + 1, q2 - q1 - 1);
        if (DateTimeOffset.TryParse(slice, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            return dto.ToLocalTime();
        return null;
    }

    private static string ExtractAntigravityConversationTag(string filePath)
    {
        var parent = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(parent)) return "agy";
        var folderName = Path.GetFileName(parent);
        if (folderName.Equals("logs", StringComparison.OrdinalIgnoreCase))
        {
            var sysGen = Path.GetDirectoryName(parent);
            var convDir = sysGen != null && Path.GetFileName(sysGen).Equals(".system_generated", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(sysGen)
                : sysGen;
            var convName = convDir != null ? Path.GetFileName(convDir) : null;
            if (!string.IsNullOrWhiteSpace(convName)) return convName;
        }
        return string.IsNullOrWhiteSpace(folderName) ? "agy" : folderName;
    }

    private static string? TryQuickExtractJsonString(string line, string quotedKey)
    {
        int keyIdx = line.IndexOf(quotedKey, StringComparison.Ordinal);
        if (keyIdx < 0) return null;
        int colonIdx = line.IndexOf(':', keyIdx + quotedKey.Length);
        if (colonIdx < 0) return null;
        int q1 = line.IndexOf('"', colonIdx + 1);
        if (q1 < 0) return null;
        int q2 = q1 + 1;
        while (q2 < line.Length && q2 - q1 <= 260)
        {
            if (line[q2] == '"' && line[q2 - 1] != '\\')
            {
                string raw = line.Substring(q1 + 1, q2 - q1 - 1);
                return raw.Replace("\\\\", "\\", StringComparison.Ordinal);
            }
            q2++;
        }
        return null;
    }

    /// <summary>
    /// 按需读取单个会话的上下文膨胀轨迹与工具调用元数据（绝不读取提示词与代码正文）
    /// </summary>
    public ConversationAuditReport? BuildConversationAudit(string conversationId, ModelPricingCatalog pricing)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return null;

        if (!_conversationFileMap.TryGetValue(conversationId, out string? filePath) || !File.Exists(filePath))
        {
            ScanIncrementalWithChangeFlag();
            if (!_conversationFileMap.TryGetValue(conversationId, out filePath) || !File.Exists(filePath))
                return null;
        }

        List<UsageLogEntry> entries;
        string appName;
        var turnTools = new Dictionary<int, List<string>>();
        var toolCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (conversationId.StartsWith("claude:", StringComparison.OrdinalIgnoreCase))
        {
            appName = "Claude Code";
            entries = ParseClaudeSession(filePath).OrderBy(e => e.Timestamp).ToList();
            var toolsByMsg = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string line in ReadLinesShared(filePath))
            {
                if (!line.Contains("\"tool_use\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
                    {
                        string? msgId = TryGetString(msg, "id");
                        if (string.IsNullOrWhiteSpace(msgId)) continue;
                        if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var block in content.EnumerateArray())
                            {
                                if (TryGetString(block, "type") == "tool_use")
                                {
                                    string? tName = TryGetString(block, "name");
                                    if (!string.IsNullOrWhiteSpace(tName))
                                    {
                                        if (!toolsByMsg.TryGetValue(msgId, out var list)) toolsByMsg[msgId] = list = new();
                                        list.Add(tName);
                                        toolCounts[tName] = toolCounts.GetValueOrDefault(tName) + 1;
                                    }
                                }
                            }
                        }
                    }
                }
                catch { /* 跳过单行异常 */ }
            }

            string sessionTag = Path.GetFileNameWithoutExtension(filePath);
            string prefix = $"claude:{sessionTag}:";
            for (int i = 0; i < entries.Count; i++)
            {
                string msgKey = entries[i].Id.StartsWith(prefix, StringComparison.Ordinal)
                    ? entries[i].Id[prefix.Length..]
                    : entries[i].Id;
                if (toolsByMsg.TryGetValue(msgKey, out var tList))
                    turnTools[i] = tList;
            }
        }
        else if (conversationId.StartsWith("codex:", StringComparison.OrdinalIgnoreCase))
        {
            appName = "Codex";
            entries = ParseCodexSession(filePath).OrderBy(e => e.Timestamp).ToList();
            int currentTurn = -1;
            int codexTurnCount = 0;
            foreach (string line in ReadLinesShared(filePath))
            {
                if (!line.Contains("token_count", StringComparison.Ordinal)
                    && !line.Contains("function_call", StringComparison.Ordinal)
                    && !line.Contains("custom_tool_call", StringComparison.Ordinal)
                    && !line.Contains("local_shell_call", StringComparison.Ordinal))
                {
                    continue;
                }
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string? rowType = TryGetString(root, "type");
                    if (rowType == "event_msg" && root.TryGetProperty("payload", out var payload))
                    {
                        if (TryGetString(payload, "type") == "token_count" && codexTurnCount < entries.Count)
                        {
                            currentTurn = Math.Min(entries.Count - 1, Math.Max(0, codexTurnCount++));
                        }
                    }
                    else if (rowType == "response_item" && root.TryGetProperty("payload", out var rPayload))
                    {
                        string? pType = TryGetString(rPayload, "type");
                        string? tName = pType switch
                        {
                            "function_call" or "custom_tool_call" => TryGetString(rPayload, "name"),
                            "local_shell_call" => "shell",
                            _ => null
                        };
                        if (!string.IsNullOrWhiteSpace(tName))
                        {
                            int targetIdx = Math.Max(0, Math.Min(entries.Count - 1, currentTurn < 0 ? 0 : currentTurn));
                            if (!turnTools.TryGetValue(targetIdx, out var list)) turnTools[targetIdx] = list = new();
                            list.Add(tName);
                            toolCounts[tName] = toolCounts.GetValueOrDefault(tName) + 1;
                        }
                    }
                }
                catch { /* 跳过单行异常 */ }
            }
        }
        else
        {
            appName = "Antigravity";
            entries = ParseAntigravityTranscript(filePath).OrderBy(e => e.Timestamp).ToList();
            int plannerIdx = -1;
            foreach (string line in ReadLinesShared(filePath))
            {
                if (!line.Contains("PLANNER_RESPONSE", StringComparison.Ordinal)
                    && !line.Contains("tool_calls", StringComparison.Ordinal)
                    && !line.Contains("TOOL_CALL", StringComparison.Ordinal)
                    && !line.Contains("COMMAND_EXECUTION", StringComparison.Ordinal))
                {
                    continue;
                }
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string? st = TryGetString(root, "type");
                    if (st == "PLANNER_RESPONSE")
                    {
                        long tok = TryGetInt64(root, "input_tokens") + TryGetInt64(root, "cache_read_tokens") + TryGetInt64(root, "output_tokens");
                        if (tok > 0) plannerIdx = Math.Min(entries.Count - 1, plannerIdx + 1);
                        if (plannerIdx >= 0 && root.TryGetProperty("tool_calls", out var tcArr) && tcArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tc in tcArr.EnumerateArray())
                            {
                                string? tName = TryGetString(tc, "name") ?? TryGetString(tc, "tool_name");
                                if (tName == null && tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                                    tName = TryGetString(fn, "name");
                                if (!string.IsNullOrWhiteSpace(tName))
                                {
                                    if (!turnTools.TryGetValue(plannerIdx, out var list)) turnTools[plannerIdx] = list = new();
                                    list.Add(tName);
                                    toolCounts[tName] = toolCounts.GetValueOrDefault(tName) + 1;
                                }
                            }
                        }
                    }
                    else if ((st == "TOOL_CALL" || st == "COMMAND_EXECUTION" || st == "CODE_ACTION") && plannerIdx >= 0)
                    {
                        string? tName = TryGetString(root, "tool_name") ?? TryGetString(root, "name") ?? st;
                        if (!string.IsNullOrWhiteSpace(tName))
                        {
                            if (!turnTools.TryGetValue(plannerIdx, out var list)) turnTools[plannerIdx] = list = new();
                            list.Add(tName);
                            toolCounts[tName] = toolCounts.GetValueOrDefault(tName) + 1;
                        }
                    }
                }
                catch { /* 跳过单行异常 */ }
            }
        }

        double totalCost = 0;
        double totalSaved = 0;
        long peakContext = 0;
        long firstContext = 0;
        long lastContext = 0;
        var turns = new List<ConversationTurnAudit>(entries.Count);

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var (cost, saved, isPriced) = pricing.CalculateCostAndSavings(e);
            if (isPriced && cost.HasValue) totalCost += cost.Value;
            totalSaved += saved;

            long ctx = e.EffectiveInputTokens + e.CacheReadTokens + e.CacheWriteTokens;
            if (i == 0) firstContext = ctx;
            lastContext = ctx;
            if (ctx > peakContext) peakContext = ctx;

            turns.Add(new ConversationTurnAudit
            {
                TurnIndex = i + 1,
                Timestamp = e.Timestamp,
                Model = e.Model,
                InputTokens = e.EffectiveInputTokens,
                OutputTokens = e.OutputTokens,
                CacheReadTokens = e.CacheReadTokens,
                CumulativeContextTokens = ctx,
                DurationSeconds = e.DurationSeconds,
                CostUsd = isPriced ? cost : null,
                ToolsCalled = turnTools.TryGetValue(i, out var tList) ? tList : []
            });
        }

        double growthRatio = firstContext > 0 ? Math.Round((double)lastContext / firstContext, 2) : 1.0;
        string? hint = null;
        if (peakContext >= 100_000)
            hint = $"峰值上下文达 {peakContext:N0} Tokens，建议开启新会话或执行 /compact 以减少高额输入成本与首字延迟。";
        else if (growthRatio >= 4.0 || turns.Count >= 25)
            hint = $"对话共 {turns.Count} 轮且上下文膨胀 {growthRatio:F1}x，适时拆分任务会话可显著提升命中率与响应速度。";

        return new ConversationAuditReport
        {
            ConversationId = conversationId,
            AppName = entries.FirstOrDefault()?.AppName ?? appName,
            Workspace = entries.Select(e => e.Workspace).FirstOrDefault(w => !string.IsNullOrWhiteSpace(w)),
            FilePath = filePath,
            TotalTurns = turns.Count,
            PeakContextTokens = peakContext,
            TotalCostUsd = Math.Round(totalCost, 4),
            TotalCacheSavedUsd = Math.Round(totalSaved, 4),
            ContextGrowthRatio = growthRatio,
            OptimizationHint = hint,
            ToolBreakdown = toolCounts
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new ToolUsageItem { ToolName = kv.Key, Calls = kv.Value })
                .ToList(),
            Turns = turns
        };
    }
}
