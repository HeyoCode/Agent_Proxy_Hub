using System.Text.RegularExpressions;

namespace AgentProxyHub.Core.Diagnostics;

/// <summary>
/// 脱敏运行时事件记录（仅保留事件级别、分类、时间与脱敏摘要，绝不写入密码/Token/正文字段）
/// </summary>
public sealed class HubLogEntry
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Level { get; set; } = "INFO";       // INFO | WARN | ERROR
    public string Category { get; set; } = "SYSTEM";  // NIC | DNS | PROXY | GUARD | SUB | SYSTEM
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// 线程安全内存环形日志（上限 500 条） + 按日滚动本地脱敏日志（保留最近 5 天）
/// </summary>
public sealed partial class HubEventLogger
{
    public static HubEventLogger Default { get; } = new();

    private const int MaxMemoryEntries = 500;
    private const int MaxDailyLogFiles = 5;

    private readonly object _lock = new();
    private readonly LinkedList<HubLogEntry> _entries = new();
    private readonly string _logDir;

    [GeneratedRegex(@"enc:v1:[A-Za-z0-9+/=_-]+", RegexOptions.Compiled)]
    private static partial Regex EncBlobPattern();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"://[^/\s:@]+:[^@\s]+@", RegexOptions.Compiled)]
    private static partial Regex UrlCredsPattern();

    [GeneratedRegex(@"([?&])(token|key|password|auth|secret)=([^&\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex QuerySecretPattern();

    public HubEventLogger(string? customDirectory = null)
    {
        string baseDir = string.IsNullOrWhiteSpace(customDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agent-proxy-hub", "logs")
            : customDirectory;
        _logDir = baseDir;
        try
        {
            Directory.CreateDirectory(_logDir);
        }
        catch
        {
            // 忽略日志目录创建异常，保证内存日志可用
        }
    }

    public static string RedactSecrets(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        string s = EncBlobPattern().Replace(text, "enc:v1:[REDACTED]");
        s = BearerPattern().Replace(s, "Bearer [REDACTED]");
        s = UrlCredsPattern().Replace(s, "://***:***@");
        s = QuerySecretPattern().Replace(s, "$1$2=[REDACTED]");
        return s;
    }

    public void Info(string category, string message) => Write("INFO", category, message);
    public void Warn(string category, string message) => Write("WARN", category, message);
    public void Error(string category, string message) => Write("ERROR", category, message);

    public void Write(string level, string category, string message)
    {
        string normLevel = string.IsNullOrWhiteSpace(level) ? "INFO" : level.Trim().ToUpperInvariant();
        string normCat = string.IsNullOrWhiteSpace(category) ? "SYSTEM" : category.Trim().ToUpperInvariant();
        string safeMsg = RedactSecrets(message);

        var entry = new HubLogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Level = normLevel,
            Category = normCat,
            Message = safeMsg
        };

        lock (_lock)
        {
            _entries.AddLast(entry);
            while (_entries.Count > MaxMemoryEntries)
                _entries.RemoveFirst();

            AppendToDailyFile(entry);
        }
    }

    public List<HubLogEntry> Query(string? level = null, string? keyword = null, int limit = 200)
    {
        int cap = Math.Clamp(limit, 1, MaxMemoryEntries);
        string? lv = string.IsNullOrWhiteSpace(level) || level.Equals("ALL", StringComparison.OrdinalIgnoreCase)
            ? null
            : level.Trim().ToUpperInvariant();
        string? kw = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();

        lock (_lock)
        {
            IEnumerable<HubLogEntry> q = _entries.Reverse();
            if (lv != null)
                q = q.Where(e => e.Level.Equals(lv, StringComparison.OrdinalIgnoreCase));
            if (kw != null)
                q = q.Where(e => e.Message.Contains(kw, StringComparison.OrdinalIgnoreCase)
                              || e.Category.Contains(kw, StringComparison.OrdinalIgnoreCase));
            return q.Take(cap).ToList();
        }
    }

    public void ClearMemory()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    private void AppendToDailyFile(HubLogEntry entry)
    {
        try
        {
            Directory.CreateDirectory(_logDir);
            string fileName = $"hub-{entry.Timestamp.ToLocalTime():yyyyMMdd}.log";
            string filePath = Path.Combine(_logDir, fileName);
            string line = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{entry.Level}] [{entry.Category}] {entry.Message}{Environment.NewLine}";
            File.AppendAllText(filePath, line);

            // 仅在必要时清理超出保留天数的历史日志文件
            var files = Directory.GetFiles(_logDir, "hub-*.log")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .Skip(MaxDailyLogFiles);
            foreach (string old in files)
            {
                try { File.Delete(old); } catch { }
            }
        }
        catch
        {
            // 磁盘写入失败静默忽略，防止影响代理转发热路径
        }
    }
}
