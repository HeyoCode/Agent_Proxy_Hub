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
        var sw = Stopwatch.StartNew();
        var allEntries = new List<UsageLogEntry>();
        int scannedFiles = 0;

        // 1. 扫描 Antigravity 会话轨迹日志
        if (Directory.Exists(_antigravityBrainDir))
        {
            foreach (string file in SafeEnumerateFiles(_antigravityBrainDir, "transcript.jsonl"))
            {
                scannedFiles++;
                allEntries.AddRange(ParseWithCache(file, ParseAntigravityTranscript));
            }
        }

        // 2. 扫描 Claude Code 本地项目会话日志
        if (Directory.Exists(_claudeProjectsDir))
        {
            foreach (string file in SafeEnumerateFiles(_claudeProjectsDir, "*.jsonl"))
            {
                scannedFiles++;
                allEntries.AddRange(ParseWithCache(file, ParseClaudeSession));
            }
        }

        // 3. 扫描 OpenAI Codex 会话日志
        if (Directory.Exists(_codexSessionsDir))
        {
            foreach (string file in SafeEnumerateFiles(_codexSessionsDir, "*.jsonl"))
            {
                scannedFiles++;
                allEntries.AddRange(ParseWithCache(file, ParseCodexSession));
            }
        }

        sw.Stop();
        if (scannedFiles > 0)
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        }
        var ordered = allEntries
            .OrderByDescending(e => e.Timestamp)
            .ToList();

        var info = new SyncStatsResult
        {
            SyncedAt = DateTimeOffset.UtcNow,
            ScannedFiles = scannedFiles,
            ParsedEntries = ordered.Count,
            ElapsedMs = sw.ElapsedMilliseconds
        };

        return (ordered, info);
    }

    private List<UsageLogEntry> ParseWithCache(string filePath, Func<string, List<UsageLogEntry>> parser)
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
            return parsed;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 解析 Antigravity transcript.jsonl
    /// </summary>
    public static List<UsageLogEntry> ParseAntigravityTranscript(string filePath)
    {
        var results = new List<UsageLogEntry>();
        string fileTag = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(filePath)) ?? "agy");
        DateTimeOffset? prevStepTime = null;
        int lineIdx = 0;

        foreach (string line in ReadLinesShared(filePath))
        {
            lineIdx++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (!line.Contains("\"PLANNER_RESPONSE\"", StringComparison.Ordinal))
            {
                DateTimeOffset? quickTs = TryQuickExtractTimestamp(line, "\"created_at\"");
                if (quickTs.HasValue) prevStepTime = quickTs;
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
                        double durationSec = 0;
                        if (prevStepTime.HasValue)
                        {
                            double diff = (ts.Value - prevStepTime.Value).TotalSeconds;
                            if (diff > 0.05 && diff <= 600) durationSec = Math.Round(diff, 1);
                        }

                        double? tps = (durationSec >= 0.2 && output > 0)
                            ? Math.Round(output / durationSec, 1)
                            : null;

                        int stepIndex = (int)TryGetInt64(root, "step_index");
                        results.Add(new UsageLogEntry
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
                            DurationSeconds = durationSec,
                            TokensPerSecond = tps
                        });
                    }
                }

                if (ts.HasValue) prevStepTime = ts;
            }
            catch
            {
                // 跳过单行格式不完整记录（例如正在写入中的最后一行）
            }
        }

        return results;
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
