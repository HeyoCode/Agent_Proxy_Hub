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
    private sealed record CachedFileResult(DateTime LastWriteUtc, long Length, List<UsageLogEntry> Entries);

    private readonly string _antigravityBrainDir;
    private readonly string _claudeProjectsDir;
    private readonly string _codexSessionsDir;
    private readonly ConcurrentDictionary<string, CachedFileResult> _fileCache = new(StringComparer.OrdinalIgnoreCase);
    private List<UsageLogEntry>? _cachedOrdered;
    private int _lastFileCount = -1;

    public SessionLogScanner(string? userHomeOverride = null)
    {
        string home = string.IsNullOrWhiteSpace(userHomeOverride)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userHomeOverride;

        _antigravityBrainDir = Path.Combine(home, ".gemini", "antigravity", "brain");
        _claudeProjectsDir = Path.Combine(home, ".claude", "projects");
        _codexSessionsDir = Path.Combine(home, ".codex", "sessions");
    }

    public (List<UsageLogEntry> Entries, SyncStatsResult SyncInfo) ScanIncremental()
    {
        var (entries, info, _) = ScanIncrementalWithChangeFlag();
        return (entries, info);
    }

    /// <summary>
    /// 高频增量扫描（支持 5 秒自动轮询）：文件未变化时零排序直接命中缓存，避免周期性 GC 抖动
    /// </summary>
    public (List<UsageLogEntry> Entries, SyncStatsResult SyncInfo, bool HasChanges) ScanIncrementalWithChangeFlag()
    {
        var sw = Stopwatch.StartNew();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileCandidates = new List<(string Path, Func<string, List<UsageLogEntry>> Parser)>();

        if (Directory.Exists(_antigravityBrainDir))
        {
            foreach (string file in SafeEnumerateFiles(_antigravityBrainDir, "transcript.jsonl"))
            {
                if (seenFiles.Add(file)) fileCandidates.Add((file, ParseAntigravityTranscript));
            }
        }

        if (Directory.Exists(_claudeProjectsDir))
        {
            foreach (string file in SafeEnumerateFiles(_claudeProjectsDir, "*.jsonl"))
            {
                if (seenFiles.Add(file)) fileCandidates.Add((file, ParseClaudeSession));
            }
        }

        if (Directory.Exists(_codexSessionsDir))
        {
            foreach (string file in SafeEnumerateFiles(_codexSessionsDir, "*.jsonl"))
            {
                if (seenFiles.Add(file)) fileCandidates.Add((file, ParseCodexSession));
            }
        }

        int scannedFiles = fileCandidates.Count;
        int reparsedCount = 0;
        var allEntries = new List<UsageLogEntry>();

        foreach (var (file, parser) in fileCandidates)
        {
            allEntries.AddRange(ParseWithCache(file, parser, ref reparsedCount));
        }

        // 清理已从磁盘移除文件的旧缓存项
        bool removedAny = false;
        foreach (string cachedKey in _fileCache.Keys)
        {
            if (!seenFiles.Contains(cachedKey))
            {
                if (_fileCache.TryRemove(cachedKey, out _)) removedAny = true;
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

    private List<UsageLogEntry> ParseWithCache(
        string filePath,
        Func<string, List<UsageLogEntry>> parser,
        ref int reparsedCount)
    {
        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists || fi.Length == 0) return [];

            if (_fileCache.TryGetValue(filePath, out var cached)
                && cached.LastWriteUtc == fi.LastWriteTimeUtc
                && cached.Length == fi.Length)
            {
                return cached.Entries;
            }

            var parsed = parser(filePath);
            _fileCache[filePath] = new CachedFileResult(fi.LastWriteTimeUtc, fi.Length, parsed);
            reparsedCount++;
            return parsed;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 解析 Antigravity transcript.jsonl
    /// 注意：PLANNER_RESPONSE 的 created_at 是该步开始创建时间，因此真实耗时应向后取「后续步骤 created_at - 当前步 created_at」
    /// </summary>
    public static List<UsageLogEntry> ParseAntigravityTranscript(string filePath)
    {
        var results = new List<UsageLogEntry>();
        string fileTag = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(filePath)) ?? "agy");
        var pending = new List<(UsageLogEntry Entry, bool HasThinking, int ThinkingChars, double? ExplicitTtftSec)>();
        var timeline = new List<(DateTimeOffset Ts, int PendingIdx)>();
        int lineIdx = 0;

        foreach (string line in ReadLinesShared(filePath))
        {
            lineIdx++;
            if (string.IsNullOrWhiteSpace(line)) continue;

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
                            CacheWriteTokens = 0
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
        DateTimeOffset? prevTime = null;
        int seq = 0;

        foreach (string line in ReadLinesShared(filePath))
        {
            seq++;
            if (string.IsNullOrWhiteSpace(line)) continue;

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
                            TokensPerSecond = tps
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

        return byMessageId.Values.ToList();
    }

    /// <summary>
    /// 解析 OpenAI Codex 会话 JSONL（关联 turn_context.model 与 token_count 事件并按累计值去重）
    /// </summary>
    public static List<UsageLogEntry> ParseCodexSession(string filePath)
    {
        var results = new List<UsageLogEntry>();
        string sessionTag = Path.GetFileNameWithoutExtension(filePath);
        string currentModel = "codex-unknown";
        DateTimeOffset? turnStartTime = null;
        long lastCumulativeTotal = -1;
        int seq = 0;

        foreach (string line in ReadLinesShared(filePath))
        {
            seq++;
            if (string.IsNullOrWhiteSpace(line)) continue;

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
                    if (ts.HasValue) turnStartTime = ts;
                }
                else if (rowType == "event_msg" && root.TryGetProperty("payload", out var payload))
                {
                    string? pType = TryGetString(payload, "type");
                    if (pType == "task_started" && ts.HasValue)
                    {
                        turnStartTime = ts;
                    }
                    else if (pType == "token_count"
                             && payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
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
                                AppName = "Codex",
                                Source = "会话日志",
                                Model = currentModel,
                                EffectiveInputTokens = effectiveInput,
                                CacheReadTokens = cacheRead,
                                CacheWriteTokens = cacheWrite,
                                OutputTokens = output,
                                DurationSeconds = durationSec,
                                TokensPerSecond = tps
                            });
                            turnStartTime = ts;
                        }
                    }
                }
            }
            catch
            {
                // 跳过单行错误
            }
        }

        return results;
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
}
