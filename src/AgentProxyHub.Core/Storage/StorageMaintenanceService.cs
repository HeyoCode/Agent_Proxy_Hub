namespace AgentProxyHub.Core.Storage;

/// <summary>
/// 本地配置目录体积统计与冗余接管备份/临时文件安全清理服务
/// （严禁删改 state.json、vault.key 与当前在用接管备份）
/// </summary>
public sealed class StorageMaintenanceService
{
    public sealed class StorageStatsDto
    {
        public string HubDir { get; set; } = string.Empty;
        public long TotalBytes { get; set; }
        public int FileCount { get; set; }
        public int TakeoverBackupCount { get; set; }
        public int TempFileCount { get; set; }
        public int LauncherFileCount { get; set; }
        public int ProfileCount { get; set; }
    }

    public sealed class StorageCleanupResult
    {
        public int RemovedFiles { get; set; }
        public long FreedBytes { get; set; }
        public string Message { get; set; } = string.Empty;
        public StorageStatsDto Stats { get; set; } = new();
    }

    private readonly string _hubDir;

    public StorageMaintenanceService(string? customUserHome = null)
    {
        string home = string.IsNullOrWhiteSpace(customUserHome)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : customUserHome;
        _hubDir = Path.Combine(home, ".agent-proxy-hub");
    }

    public StorageStatsDto GetStorageStats()
    {
        var dto = new StorageStatsDto { HubDir = _hubDir };
        if (!Directory.Exists(_hubDir)) return dto;

        try
        {
            foreach (string file in Directory.EnumerateFiles(_hubDir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    dto.TotalBytes += fi.Length;
                    dto.FileCount++;

                    string name = Path.GetFileName(file);
                    if (file.Contains("takeover-backups", StringComparison.OrdinalIgnoreCase))
                        dto.TakeoverBackupCount++;
                    if (file.Contains("launchers", StringComparison.OrdinalIgnoreCase))
                        dto.LauncherFileCount++;
                    if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".import-tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        dto.TempFileCount++;
                    }
                }
                catch { }
            }

            string profilesDir = Path.Combine(_hubDir, "profiles");
            if (Directory.Exists(profilesDir))
            {
                foreach (string appDir in Directory.EnumerateDirectories(profilesDir))
                {
                    dto.ProfileCount += Directory.EnumerateDirectories(appDir).Count();
                }
            }
        }
        catch { }

        return dto;
    }

    /// <summary>
    /// 仅安全清理未在接管中的冗余备份与中断临时文件，绝不动 state.json / vault.key / profiles 用户数据
    /// </summary>
    public StorageCleanupResult CleanupRedundantFiles(IReadOnlyCollection<string> activeTakenOverSeatIds)
    {
        int removed = 0;
        long freed = 0;
        if (!Directory.Exists(_hubDir))
        {
            return new StorageCleanupResult
            {
                RemovedFiles = 0,
                FreedBytes = 0,
                Message = "配置目录尚未生成冗余文件",
                Stats = GetStorageStats()
            };
        }

        // 清理所有残留 .tmp / .import-tmp 临时文件
        foreach (string tmp in Directory.EnumerateFiles(_hubDir, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(tmp);
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".import-tmp", StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(tmp, ref removed, ref freed);
            }
        }

        // 清理当前未处于接管状态的残留历史备份（文件名形如 <seatId>.json.bak）
        string backupDir = Path.Combine(_hubDir, "takeover-backups");
        if (Directory.Exists(backupDir))
        {
            var activeSet = activeTakenOverSeatIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string bak in Directory.EnumerateFiles(backupDir, "*.bak"))
            {
                string fileName = Path.GetFileName(bak);
                string seatKey = fileName.Split('.')[0];
                if (seatKey == "agy-ide" || seatKey == "agy-run-launch") seatKey = "agy";
                if (!activeSet.Contains(seatKey))
                {
                    TryDelete(bak, ref removed, ref freed);
                }
            }
        }

        var after = GetStorageStats();
        return new StorageCleanupResult
        {
            RemovedFiles = removed,
            FreedBytes = freed,
            Message = removed > 0
                ? $"已安全清理 {removed} 个临时/失效备份文件，释放 {(freed / 1024.0):F1} KB（已保留全部 state.json 与在用接管备份）"
                : "当前环境非常整洁，无可清理的失效备份或临时文件。",
            Stats = after
        };
    }

    private static void TryDelete(string path, ref int removed, ref long freed)
    {
        try
        {
            long len = new FileInfo(path).Length;
            File.Delete(path);
            removed++;
            freed += len;
        }
        catch { }
    }
}
