using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentProxyHub.Core.Diagnostics;
using AgentProxyHub.Core.Models;

namespace AgentProxyHub.Core.Networking;

/// <summary>
/// L2 现代高抗干扰协议懒加载扩展引擎管理器 (sing-box)：
/// - 承载 VLESS (Reality / WS / gRPC / Vision)、Hysteria2、TUIC、WireGuard
/// - 仅当活跃席位或待验真节点需要 L2 协议时按需拉起；空闲时零进程、零内存占用
/// - 自动注入物理硬件网卡 bind_interface，与 L1 保持一致的防 Clash / Wintun 隔离标准
/// - Windows 下绑定 Win32 Job Object (KILL_ON_JOB_CLOSE)，主进程退出或异常崩溃时内核自动清理
/// </summary>
public sealed class SingBoxSidecarManager : IDisposable
{
    public const int BridgePortBase = 18020;

    private readonly string _binDir;
    private readonly string _runtimeDir;
    private readonly string _configPath;
    private readonly object _syncLock = new();
    private readonly Dictionary<string, int> _nodeBridgePorts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProxyNode> _topologyNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ProxyNode Node, DateTimeOffset ExpiresAt)> _ephemeralNodes = new(StringComparer.Ordinal);
    private readonly Queue<string> _recentLogs = new();

    private string? _customBinaryPath;
    private Process? _process;
    private IntPtr _winJobHandle = IntPtr.Zero;
    private string? _lastConfigHash;
    private string? _cachedVersion;
    private string? _cachedSha256;
    private string? _boundInterfaceName;
    private IsolationSettings _lastIsolation = new();
    private string? _lastError;
    private bool _disposed;

    public SingBoxSidecarManager(string? customUserHome = null)
    {
        string home = string.IsNullOrWhiteSpace(customUserHome)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : customUserHome;
        string hubDir = Path.Combine(home, ".agent-proxy-hub");
        _binDir = Path.Combine(hubDir, "bin");
        _runtimeDir = Path.Combine(hubDir, "runtime");
        _configPath = Path.Combine(_runtimeDir, "singbox-bridge.json");
    }

    public void SetCustomBinaryPath(string? path)
    {
        lock (_syncLock)
        {
            _customBinaryPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
            _cachedVersion = null;
            _cachedSha256 = null;
        }
    }

    public string? ResolveBinaryPath()
    {
        if (!string.IsNullOrWhiteSpace(_customBinaryPath) && File.Exists(_customBinaryPath))
            return _customBinaryPath;

        string exeName = OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box";
        string managed = Path.Combine(_binDir, exeName);
        if (File.Exists(managed)) return managed;

        // 兜底查找 PATH 中已安装的 sing-box
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), exeName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }

        return null;
    }

    public SidecarEngineStatus GetStatus()
    {
        lock (_syncLock)
        {
            string? bin = ResolveBinaryPath();
            bool installed = !string.IsNullOrWhiteSpace(bin);
            if (installed && _cachedVersion == null)
            {
                _cachedVersion = ReadSingBoxVersion(bin!);
            }
            if (installed && _cachedSha256 == null)
            {
                _cachedSha256 = ComputeFileSha256Safe(bin!);
            }

            bool running = _process != null && !_process.HasExited;
            return new SidecarEngineStatus
            {
                Installed = installed,
                Running = running,
                BinaryPath = bin,
                Version = _cachedVersion,
                BinarySha256 = _cachedSha256,
                ActiveL2NodeCount = _nodeBridgePorts.Count,
                BoundInterface = _boundInterfaceName,
                LastError = _lastError
            };
        }
    }

    public int? GetBridgePort(string nodeId)
    {
        lock (_syncLock)
        {
            if (_process == null || _process.HasExited) return null;
            return _nodeBridgePorts.TryGetValue(nodeId, out int port) ? port : null;
        }
    }

    /// <summary>
    /// 同步常驻拓扑所需的活跃 L2 节点（与未过期的临时验真节点取并集，不中断在用验真或席位连接）
    /// </summary>
    public bool EnsureNodesReady(
        IReadOnlyList<ProxyNode> l2Nodes,
        string? boundInterfaceName,
        IsolationSettings iso,
        out string? error)
    {
        lock (_syncLock)
        {
            _topologyNodes.Clear();
            foreach (var n in l2Nodes.Where(x => x.RequiresSidecarEngine && x.Enabled))
            {
                _topologyNodes[n.Id] = n;
            }
            _boundInterfaceName = boundInterfaceName;
            _lastIsolation = iso;
            return ReconcileLocked(out error);
        }
    }

    /// <summary>
    /// 为单节点或全量节点验真临时扩容 L2 桥接端口（与当前活跃席位 L2 节点取并集，绝不冲掉在用席位）
    /// </summary>
    public bool EnsureEphemeralNodesReady(
        IReadOnlyList<ProxyNode> verifyL2Nodes,
        string? boundInterfaceName,
        IsolationSettings iso,
        out string? error,
        TimeSpan? ttl = null)
    {
        lock (_syncLock)
        {
            DateTimeOffset exp = DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromSeconds(90));
            foreach (var n in verifyL2Nodes.Where(x => x.RequiresSidecarEngine))
            {
                _ephemeralNodes[n.Id] = (n, exp);
            }
            _boundInterfaceName = boundInterfaceName;
            _lastIsolation = iso;
            return ReconcileLocked(out error);
        }
    }

    /// <summary>
    /// 验真结束后释放临时节点引用；若无常驻活跃 L2 席位则自动停机归零占用
    /// </summary>
    public void ReleaseEphemeralNodes(IEnumerable<string>? nodeIds = null)
    {
        lock (_syncLock)
        {
            if (nodeIds == null)
            {
                _ephemeralNodes.Clear();
            }
            else
            {
                foreach (string id in nodeIds) _ephemeralNodes.Remove(id);
            }
            if (_topologyNodes.Count == 0 && _ephemeralNodes.Count == 0)
            {
                StopProcessLocked();
                _nodeBridgePorts.Clear();
                _lastConfigHash = null;
            }
        }
    }

    private bool ReconcileLocked(out string? error)
    {
        error = null;
        if (_disposed)
        {
            error = "sing-box 管理器已释放";
            return false;
        }

        // 清理已过期的临时验真节点
        var now = DateTimeOffset.UtcNow;
        foreach (string expiredKey in _ephemeralNodes.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList())
        {
            _ephemeralNodes.Remove(expiredKey);
        }

        // 合并常驻拓扑节点 ∪ 临时验真节点（按稳定顺序排列并尽量复用已有端口）
        var merged = new Dictionary<string, ProxyNode>(StringComparer.Ordinal);
        foreach (var kv in _topologyNodes) merged[kv.Key] = kv.Value;
        foreach (var kv in _ephemeralNodes) merged.TryAdd(kv.Key, kv.Value.Node);

        if (merged.Count == 0)
        {
            StopProcessLocked();
            _nodeBridgePorts.Clear();
            _lastConfigHash = null;
            _lastError = null;
            return true;
        }

        string? bin = ResolveBinaryPath();
        if (string.IsNullOrWhiteSpace(bin))
        {
            _lastError = "未安装 sing-box 扩展内核，请在「网卡隔离」页点击一键安装后启用 VLESS / Hysteria2 / TUIC / WireGuard 节点。";
            error = _lastError;
            return false;
        }

        var distinctNodes = merged.Values.OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        bool processAlive = _process != null && !_process.HasExited;
        IReadOnlyDictionary<string, int>? reusablePorts = null;
        if (_nodeBridgePorts.Count > 0)
        {
            if (processAlive)
            {
                reusablePorts = _nodeBridgePorts;
            }
            else
            {
                reusablePorts = _nodeBridgePorts
                    .Where(kv => IsLoopbackPortAvailable(kv.Value))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            }
        }

        _cachedVersion ??= ReadSingBoxVersion(bin);
        bool modernSchema = IsModernSingBoxVersion(_cachedVersion);

        (string configJson, Dictionary<string, int> portMap) = BuildBridgeConfigJson(
            distinctNodes,
            _boundInterfaceName,
            _lastIsolation,
            BridgePortBase,
            reusablePorts,
            probeSystemPort: true,
            modernSchema: modernSchema);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configJson)));

        if (processAlive && hash == _lastConfigHash)
        {
            return true;
        }

        Directory.CreateDirectory(_runtimeDir);
        File.WriteAllText(_configPath, configJson, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(_configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch { }
        }

        StopProcessLocked(deleteConfig: false);
        _nodeBridgePorts.Clear();
        foreach (var kv in portMap) _nodeBridgePorts[kv.Key] = kv.Value;
        _recentLogs.Clear();

        try
        {
            var psi = new ProcessStartInfo(bin)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = _runtimeDir
            };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(_configPath);

            var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) => AppendSidecarLog(e.Data);
            proc.ErrorDataReceived += (_, e) => AppendSidecarLog(e.Data);

            if (!proc.Start())
                throw new InvalidOperationException("无法启动 sing-box 子进程");

            // 立即开启异步排空读取，彻底杜绝长跑日志填满系统管道缓冲区引发的子进程死锁
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            AttachToKillOnCloseJob(proc);

            if (proc.WaitForExit(350))
            {
                string errText = GetRecentLogsText();
                proc.Dispose();
                _lastError = $"sing-box 启动失败：{(string.IsNullOrWhiteSpace(errText) ? "配置或端口异常" : errText)}";
                error = _lastError;
                return false;
            }

            _process = proc;
            _lastConfigHash = hash;
            _lastError = null;
            return true;
        }
        catch (Exception ex)
        {
            _lastError = $"启动 sing-box 异常：{ex.Message}";
            error = _lastError;
            return false;
        }
    }

    private void AppendSidecarLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_recentLogs)
        {
            if (_recentLogs.Count >= 24) _recentLogs.Dequeue();
            _recentLogs.Enqueue(line.Trim());
        }
    }

    private string GetRecentLogsText()
    {
        lock (_recentLogs)
        {
            return string.Join(" | ", _recentLogs);
        }
    }

    public void Stop()
    {
        lock (_syncLock)
        {
            StopProcessLocked();
            _topologyNodes.Clear();
            _ephemeralNodes.Clear();
            _nodeBridgePorts.Clear();
            _lastConfigHash = null;
        }
    }

    /// <summary>
    /// 从 GitHub SagerNet/sing-box 官方 Release 一键下载并安装适配当前系统的扩展内核
    /// </summary>
    public async Task<(bool Ok, string Message, SidecarEngineStatus Status)> InstallLatestAsync(
        string? optionalProxyUrl = null,
        CancellationToken ct = default)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(15),
                AutomaticDecompression = DecompressionMethods.All
            };
            if (!string.IsNullOrWhiteSpace(optionalProxyUrl))
            {
                handler.Proxy = new WebProxy(optionalProxyUrl);
                handler.UseProxy = true;
            }
            else
            {
                handler.UseProxy = false;
            }
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"AgentProxyHub/{ProjectMetadata.Version}");

            string relJson = await http.GetStringAsync("https://api.github.com/repos/SagerNet/sing-box/releases/latest", ct);
            using var doc = JsonDocument.Parse(relJson);
            string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "latest";

            string targetPattern = OperatingSystem.IsWindows()
                ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "windows-arm64.zip" : "windows-amd64.zip")
                : (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "darwin-arm64.tar.gz" : "darwin-amd64.tar.gz");

            string? downloadUrl = null;
            string? assetName = null;
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? string.Empty;
                if (name.EndsWith(targetPattern, StringComparison.OrdinalIgnoreCase) && !name.Contains("legacy", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    assetName = name;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(downloadUrl) || string.IsNullOrWhiteSpace(assetName))
                return (false, $"未在 sing-box {tag} 发布列表找到匹配资产 ({targetPattern})", GetStatus());

            Directory.CreateDirectory(_binDir);
            string tmpArchive = Path.Combine(Path.GetTempPath(), $"aph-sb-{Guid.NewGuid():N}-{assetName}");
            try
            {
                using (var resp = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var fs = File.Create(tmpArchive);
                    await resp.Content.CopyToAsync(fs, ct);
                }

                Directory.CreateDirectory(_binDir);
                string targetBin = Path.Combine(_binDir, OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box");
                Stop();
                var (extOk, extMsg) = await ExtractSingBoxArchiveSafeAsync(tmpArchive, targetBin, null, ct);
                if (!extOk)
                    return (false, extMsg, GetStatus());

                lock (_syncLock)
                {
                    _cachedVersion = ReadSingBoxVersion(targetBin);
                    _cachedSha256 = ComputeFileSha256Safe(targetBin);
                    _lastError = null;
                }
                return (true, $"已成功安装 sing-box {tag} 扩展内核（SHA-256: {_cachedSha256?[..12]}...）", GetStatus());
            }
            finally
            {
                try { if (File.Exists(tmpArchive)) File.Delete(tmpArchive); } catch { }
            }
        }
        catch (Exception ex)
        {
            lock (_syncLock) _lastError = ex.Message;
            return (false, $"在线安装 sing-box 失败：{ex.Message}（也可手动下载 sing-box 后在此导入本地路径）", GetStatus());
        }
    }

    /// <summary>
    /// 离线导入本地 sing-box 安装包（.zip / .tar.gz / 可执行文件），带 Zip-Slip 防护与可选 SHA-256 严格验真
    /// </summary>
    public async Task<(bool Ok, string Message, SidecarEngineStatus Status)> ImportOfflinePackageAsync(
        string localFilePath,
        string? expectedSha256 = null,
        CancellationToken ct = default)
    {
        try
        {
            string cleanPath = localFilePath.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(cleanPath) || !File.Exists(cleanPath))
                return (false, $"未找到本地安装包或可执行文件: {cleanPath}", GetStatus());

            Directory.CreateDirectory(_binDir);
            string targetBin = Path.Combine(_binDir, OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box");
            string? expectedNorm = string.IsNullOrWhiteSpace(expectedSha256)
                ? null
                : expectedSha256.Trim().Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
            string sourceHash = ComputeFileSha256Safe(cleanPath) ?? string.Empty;

            Stop();

            if (cleanPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || cleanPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                || cleanPath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                string tmpBin = targetBin + ".import-tmp";
                var (ok, msg) = await ExtractSingBoxArchiveSafeAsync(cleanPath, tmpBin, null, ct);
                if (!ok)
                {
                    if (File.Exists(tmpBin)) File.Delete(tmpBin);
                    return (false, msg, GetStatus());
                }

                string binHash = ComputeFileSha256Safe(tmpBin) ?? string.Empty;
                if (expectedNorm != null && sourceHash != expectedNorm && binHash != expectedNorm)
                {
                    if (File.Exists(tmpBin)) File.Delete(tmpBin);
                    return (false, $"SHA-256 校验失败：期望 {expectedNorm}，实际文件为 {sourceHash}", GetStatus());
                }
                File.Move(tmpBin, targetBin, overwrite: true);
            }
            else
            {
                if (expectedNorm != null && sourceHash != expectedNorm)
                    return (false, $"SHA-256 校验失败：期望 {expectedNorm}，实际文件为 {sourceHash}", GetStatus());

                File.Copy(cleanPath, targetBin, overwrite: true);
                if (!OperatingSystem.IsWindows())
                {
                    var chmodPsi = new ProcessStartInfo("chmod")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    chmodPsi.ArgumentList.Add("+x");
                    chmodPsi.ArgumentList.Add(targetBin);
                    Process.Start(chmodPsi)?.WaitForExit(2000);
                }
            }

            lock (_syncLock)
            {
                _customBinaryPath = null;
                _cachedVersion = ReadSingBoxVersion(targetBin);
                _cachedSha256 = ComputeFileSha256Safe(targetBin);
                _lastError = null;
            }
            return (true, $"已通过安全校验并导入 sing-box 内核（SHA-256: {_cachedSha256?[..16]}...）", GetStatus());
        }
        catch (Exception ex)
        {
            lock (_syncLock) _lastError = ex.Message;
            return (false, $"离线导入 sing-box 失败：{ex.Message}", GetStatus());
        }
    }

    public static string? ComputeFileSha256Safe(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(bool Ok, string Message)> ExtractSingBoxArchiveSafeAsync(
        string archivePath,
        string targetBin,
        string? expectedBinarySha256,
        CancellationToken ct)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(archivePath);
            string virtualRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "aph-zip-guard")) + Path.DirectorySeparatorChar;
            ZipArchiveEntry? matched = null;

            foreach (var entry in zip.Entries)
            {
                string destFull = Path.GetFullPath(Path.Combine(virtualRoot, entry.FullName));
                if (!destFull.StartsWith(virtualRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, $"检测到压缩包存在非法路径穿越条目 (Zip-Slip): {entry.FullName}");
                }
                if (entry.Name.Equals("sing-box.exe", StringComparison.OrdinalIgnoreCase)
                    || entry.Name.Equals("sing-box", StringComparison.OrdinalIgnoreCase))
                {
                    matched ??= entry;
                }
            }

            if (matched == null)
                return (false, "压缩包内未找到 sing-box 可执行文件");

            matched.ExtractToFile(targetBin, overwrite: true);
            if (!string.IsNullOrWhiteSpace(expectedBinarySha256))
            {
                string actual = ComputeFileSha256Safe(targetBin) ?? string.Empty;
                if (!actual.Equals(expectedBinarySha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(targetBin);
                    return (false, $"内核 SHA-256 校验未通过: {actual}");
                }
            }
            return (true, "OK");
        }

        string extractDir = Path.Combine(Path.GetTempPath(), $"aph-sb-ext-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);
        string extractRootFull = Path.GetFullPath(extractDir) + Path.DirectorySeparatorChar;
        try
        {
            // 先列出归档条目校验是否存在目录穿越，再执行解压
            var listPsi = new ProcessStartInfo("tar")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            listPsi.ArgumentList.Add("-tzf");
            listPsi.ArgumentList.Add(archivePath);
            using (var listProc = Process.Start(listPsi))
            {
                if (listProc != null)
                {
                    string listing = await listProc.StandardOutput.ReadToEndAsync(ct);
                    await listProc.WaitForExitAsync(ct);
                    foreach (string entry in listing.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        string trimmedEntry = entry.TrimStart('.');
                        if (Path.IsPathRooted(entry) || entry.Contains("..", StringComparison.Ordinal))
                            return (false, $"检测到 tar.gz 压缩包存在非法路径条目: {entry}");
                        string combinedFull = Path.GetFullPath(Path.Combine(extractDir, entry));
                        if (!combinedFull.StartsWith(extractRootFull, StringComparison.OrdinalIgnoreCase) && !trimmedEntry.Equals("/", StringComparison.Ordinal))
                            return (false, $"检测到 tar.gz 压缩包存在目录穿越条目: {entry}");
                    }
                }
            }

            var tarPsi = new ProcessStartInfo("tar")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            tarPsi.ArgumentList.Add("-xzf");
            tarPsi.ArgumentList.Add(archivePath);
            tarPsi.ArgumentList.Add("-C");
            tarPsi.ArgumentList.Add(extractDir);
            using var tar = Process.Start(tarPsi);
            if (tar != null) await tar.WaitForExitAsync(ct);

            string? found = Directory.EnumerateFiles(extractDir, "sing-box*", SearchOption.AllDirectories)
                .FirstOrDefault(f =>
                {
                    var fi = new FileInfo(f);
                    return (fi.Name is "sing-box" or "sing-box.exe") && fi.LinkTarget == null;
                });
            if (string.IsNullOrEmpty(found))
                return (false, "tar.gz 解压后未找到合法的 sing-box 二进制文件");
            File.Copy(found, targetBin, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                var chmodPsi = new ProcessStartInfo("chmod")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                chmodPsi.ArgumentList.Add("+x");
                chmodPsi.ArgumentList.Add(targetBin);
                Process.Start(chmodPsi)?.WaitForExit(2000);
            }
            return (true, "OK");
        }
        finally
        {
            try { Directory.Delete(extractDir, true); } catch { }
        }
    }

    /// <summary>
    /// 探测指定本地环回端口当前是否可被独占绑定监听
    /// </summary>
    public static bool IsLoopbackPortAvailable(int port)
    {
        if (port is < 1024 or > 65535) return false;
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, port);
            if (OperatingSystem.IsWindows())
            {
                probe.ExclusiveAddressUse = true;
            }
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 判断当前 sing-box 内核是否为 1.12+ 现代配置规范（1.14.0 已彻底移除旧版 dns.servers address 与内置 wireguard outbound）
    /// </summary>
    public static bool IsModernSingBoxVersion(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText)) return true;
        string token = versionText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].TrimStart('v', 'V');
        string[] parts = token.Split(['.', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[0], out int major) && int.TryParse(parts[1], out int minor))
        {
            return major > 1 || (major == 1 && minor >= 12);
        }
        return true;
    }

    /// <summary>
    /// 生成 sing-box 隔离桥接配置（每个 L2 节点映射唯一本地环回端口，并绑定物理硬件网卡绕过 Clash）
    /// </summary>
    public static (string Json, Dictionary<string, int> PortMap) BuildBridgeConfigJson(
        IReadOnlyList<ProxyNode> l2Nodes,
        string? boundInterfaceName,
        IsolationSettings iso,
        int basePort = BridgePortBase,
        IReadOnlyDictionary<string, int>? previousPorts = null,
        bool probeSystemPort = false,
        bool modernSchema = true)
    {
        var portMap = new Dictionary<string, int>(StringComparer.Ordinal);
        var usedPorts = new HashSet<int>();
        var inbounds = new JsonArray();
        var outbounds = new JsonArray();
        var endpoints = new JsonArray();
        var rules = new JsonArray();
        string cleanDnsIp = string.IsNullOrWhiteSpace(iso.DohServerIp) ? "223.5.5.5" : iso.DohServerIp.Trim();
        string domainStrategy = iso.EnableIpv6 ? "prefer_ipv4" : "ipv4_only";

        // 先保留已分配节点的原端口，确保扩容新验真节点时已有活跃节点端口不漂移
        foreach (var node in l2Nodes)
        {
            if (previousPorts != null && previousPorts.TryGetValue(node.Id, out int existingPort) && usedPorts.Add(existingPort))
            {
                portMap[node.Id] = existingPort;
            }
        }

        int nextCandidate = basePort;
        for (int i = 0; i < l2Nodes.Count; i++)
        {
            var node = l2Nodes[i];
            if (!portMap.TryGetValue(node.Id, out int port))
            {
                while (usedPorts.Contains(nextCandidate) || (probeSystemPort && !IsLoopbackPortAvailable(nextCandidate)))
                {
                    nextCandidate++;
                    if (nextCandidate > 65535)
                        throw new InvalidOperationException("本地无可用回环桥接端口");
                }
                port = nextCandidate++;
                usedPorts.Add(port);
                portMap[node.Id] = port;
            }
            string inTag = $"in-{node.Id}";
            string outTag = $"out-{node.Id}";

            inbounds.Add(new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = inTag,
                ["listen"] = "127.0.0.1",
                ["listen_port"] = port
            });

            if (modernSchema && node.Protocol == ProxyProtocol.WIREGUARD)
            {
                endpoints.Add(BuildSingBoxWireGuardEndpoint(node, outTag, boundInterfaceName, domainStrategy));
            }
            else
            {
                outbounds.Add(BuildSingBoxOutbound(node, outTag, boundInterfaceName, domainStrategy, modernSchema));
            }

            rules.Add(new JsonObject
            {
                ["inbound"] = new JsonArray(inTag),
                ["outbound"] = outTag
            });
        }

        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });

        JsonObject dnsServerObj;
        if (modernSchema)
        {
            dnsServerObj = new JsonObject
            {
                ["type"] = "udp",
                ["tag"] = "clean-dns",
                ["server"] = cleanDnsIp,
                ["server_port"] = 53
            };
            if (!string.IsNullOrWhiteSpace(boundInterfaceName))
                dnsServerObj["bind_interface"] = boundInterfaceName;
        }
        else
        {
            dnsServerObj = new JsonObject
            {
                ["tag"] = "clean-dns",
                ["address"] = cleanDnsIp,
                ["detour"] = "direct"
            };
        }

        var routeObj = new JsonObject
        {
            ["rules"] = rules,
            ["final"] = "direct",
            ["auto_detect_interface"] = string.IsNullOrWhiteSpace(boundInterfaceName)
        };
        if (modernSchema)
        {
            routeObj["default_domain_resolver"] = new JsonObject
            {
                ["server"] = "clean-dns",
                ["strategy"] = domainStrategy
            };
        }

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn" },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(dnsServerObj),
                ["strategy"] = domainStrategy
            },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["route"] = routeObj
        };

        if (endpoints.Count > 0)
        {
            root["endpoints"] = endpoints;
        }

        string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return (json, portMap);
    }

    private static JsonObject BuildSingBoxWireGuardEndpoint(
        ProxyNode node,
        string tag,
        string? boundInterfaceName,
        string domainStrategy)
    {
        var extra = node.Extra ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var localAddr = new JsonArray();
        string addrRaw = extra.TryGetValue("address", out string? aStr) && !string.IsNullOrWhiteSpace(aStr)
            ? aStr
            : "10.0.0.2/32";
        foreach (string a in addrRaw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            localAddr.Add(a.Trim());

        var peer = new JsonObject
        {
            ["address"] = node.Host,
            ["port"] = node.Port,
            ["allowed_ips"] = new JsonArray("0.0.0.0/0", "::/0")
        };
        if (extra.TryGetValue("publickey", out string? pub) || extra.TryGetValue("public_key", out pub))
            peer["public_key"] = pub;

        var ep = new JsonObject
        {
            ["type"] = "wireguard",
            ["tag"] = tag,
            ["address"] = localAddr,
            ["private_key"] = node.Password ?? string.Empty,
            ["peers"] = new JsonArray(peer),
            ["domain_resolver"] = new JsonObject
            {
                ["server"] = "clean-dns",
                ["strategy"] = domainStrategy
            }
        };

        if (!string.IsNullOrWhiteSpace(boundInterfaceName))
            ep["bind_interface"] = boundInterfaceName;
        if (extra.TryGetValue("mtu", out string? mtuStr) && int.TryParse(mtuStr, out int mtu) && mtu >= 1200)
            ep["mtu"] = mtu;

        return ep;
    }

    private static JsonObject BuildSingBoxOutbound(
        ProxyNode node,
        string tag,
        string? boundInterfaceName,
        string domainStrategy,
        bool modernSchema)
    {
        var extra = node.Extra ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var obj = new JsonObject
        {
            ["tag"] = tag,
            ["server"] = node.Host,
            ["server_port"] = node.Port
        };

        if (!string.IsNullOrWhiteSpace(boundInterfaceName))
        {
            obj["bind_interface"] = boundInterfaceName;
        }
        if (modernSchema)
        {
            obj["domain_resolver"] = new JsonObject
            {
                ["server"] = "clean-dns",
                ["strategy"] = domainStrategy
            };
        }
        else
        {
            obj["domain_strategy"] = domainStrategy;
        }

        switch (node.Protocol)
        {
            case ProxyProtocol.VLESS:
                obj["type"] = "vless";
                obj["uuid"] = node.Username ?? string.Empty;
                if (extra.TryGetValue("flow", out string? flow) && !string.IsNullOrWhiteSpace(flow))
                    obj["flow"] = flow;
                if (TryGetFirstExtra(extra, out string? pktEnc, "packetEncoding", "packet-encoding", "packet_encoding"))
                    obj["packet_encoding"] = pktEnc;

                string sec = extra.TryGetValue("security", out string? s) ? s.ToLowerInvariant() : "none";
                bool tlsFlag = extra.TryGetValue("tls", out string? tf) && (tf == "1" || tf.Equals("true", StringComparison.OrdinalIgnoreCase));
                if (sec is "tls" or "reality" || tlsFlag)
                {
                    var tls = new JsonObject
                    {
                        ["enabled"] = true,
                        ["server_name"] = node.Sni ?? node.Host,
                        ["insecure"] = node.AllowInsecure
                    };
                    if (extra.TryGetValue("fp", out string? fp) && !string.IsNullOrWhiteSpace(fp))
                        tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fp };
                    if (TryBuildAlpnArray(extra, out var vlessAlpn))
                        tls["alpn"] = vlessAlpn;
                    if (sec == "reality")
                    {
                        var reality = new JsonObject { ["enabled"] = true };
                        if (extra.TryGetValue("pbk", out string? pbk)) reality["public_key"] = pbk;
                        if (extra.TryGetValue("sid", out string? sid)) reality["short_id"] = sid;
                        tls["reality"] = reality;
                    }
                    obj["tls"] = tls;
                }

                string net = extra.TryGetValue("type", out string? netType) ? netType.ToLowerInvariant() : "tcp";
                if (net == "ws")
                {
                    var ws = new JsonObject { ["type"] = "ws" };
                    int? earlyData = null;
                    if (extra.TryGetValue("path", out string? rawWsPath) && !string.IsNullOrWhiteSpace(rawWsPath))
                    {
                        (string cleanPath, int? parsedEd) = SplitWsPathAndEarlyData(rawWsPath);
                        ws["path"] = cleanPath;
                        earlyData = parsedEd;
                    }
                    if (!earlyData.HasValue && TryGetFirstExtra(extra, out string? edStr, "max-early-data", "max_early_data") && int.TryParse(edStr, out int edNum) && edNum > 0)
                    {
                        earlyData = edNum;
                    }
                    if (earlyData is > 0)
                    {
                        ws["max_early_data"] = earlyData.Value;
                        ws["early_data_header_name"] = TryGetFirstExtra(extra, out string? edHdr, "early-data-header-name", "early_data_header_name")
                            ? edHdr
                            : "Sec-WebSocket-Protocol";
                    }
                    if (extra.TryGetValue("host", out string? h) && !string.IsNullOrWhiteSpace(h))
                        ws["headers"] = new JsonObject { ["Host"] = h };
                    obj["transport"] = ws;
                }
                else if (net == "grpc")
                {
                    var grpc = new JsonObject { ["type"] = "grpc" };
                    if (extra.TryGetValue("serviceName", out string? sn) && !string.IsNullOrWhiteSpace(sn))
                        grpc["service_name"] = sn;
                    obj["transport"] = grpc;
                }
                else if (net == "httpupgrade")
                {
                    var hu = new JsonObject { ["type"] = "httpupgrade" };
                    if (extra.TryGetValue("path", out string? huPath) && !string.IsNullOrWhiteSpace(huPath))
                        hu["path"] = huPath;
                    if (extra.TryGetValue("host", out string? huHost) && !string.IsNullOrWhiteSpace(huHost))
                        hu["host"] = huHost;
                    obj["transport"] = hu;
                }
                else if (net is "http" or "h2")
                {
                    var httpTr = new JsonObject { ["type"] = "http" };
                    if (extra.TryGetValue("path", out string? h2Path) && !string.IsNullOrWhiteSpace(h2Path))
                        httpTr["path"] = h2Path;
                    if (extra.TryGetValue("host", out string? h2Host) && !string.IsNullOrWhiteSpace(h2Host))
                    {
                        var hosts = new JsonArray();
                        foreach (string item in h2Host.Split(',', StringSplitOptions.RemoveEmptyEntries))
                            hosts.Add(item.Trim());
                        httpTr["host"] = hosts;
                    }
                    obj["transport"] = httpTr;
                }
                break;

            case ProxyProtocol.HYSTERIA2:
                obj["type"] = "hysteria2";
                obj["password"] = node.Password ?? string.Empty;
                if (extra.TryGetValue("obfs", out string? obfs) && !string.IsNullOrWhiteSpace(obfs))
                {
                    var obfsObj = new JsonObject { ["type"] = obfs };
                    if (extra.TryGetValue("obfs-password", out string? obfsPass)) obfsObj["password"] = obfsPass;
                    obj["obfs"] = obfsObj;
                }
                var hy2Tls = new JsonObject
                {
                    ["enabled"] = true,
                    ["server_name"] = node.Sni ?? node.Host,
                    ["insecure"] = node.AllowInsecure
                };
                if (TryBuildAlpnArray(extra, out var hy2Alpn))
                    hy2Tls["alpn"] = hy2Alpn;
                obj["tls"] = hy2Tls;
                break;

            case ProxyProtocol.TUIC:
                obj["type"] = "tuic";
                obj["uuid"] = node.Username ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(node.Password)) obj["password"] = node.Password;
                obj["congestion_control"] = extra.TryGetValue("congestion_control", out string? cc) ? cc : "bbr";
                var tuicTls = new JsonObject
                {
                    ["enabled"] = true,
                    ["server_name"] = node.Sni ?? node.Host,
                    ["insecure"] = node.AllowInsecure
                };
                if (TryBuildAlpnArray(extra, out var tuicAlpn))
                    tuicTls["alpn"] = tuicAlpn;
                obj["tls"] = tuicTls;
                break;

            case ProxyProtocol.WIREGUARD:
                obj["type"] = "wireguard";
                obj["private_key"] = node.Password ?? string.Empty;
                if (extra.TryGetValue("publickey", out string? pub) || extra.TryGetValue("public_key", out pub))
                    obj["peer_public_key"] = pub;
                var localAddr = new JsonArray();
                string addrRaw = extra.TryGetValue("address", out string? aStr) && !string.IsNullOrWhiteSpace(aStr)
                    ? aStr
                    : "10.0.0.2/32";
                foreach (string a in addrRaw.Split(',', StringSplitOptions.RemoveEmptyEntries)) localAddr.Add(a.Trim());
                obj["local_address"] = localAddr;
                if (extra.TryGetValue("mtu", out string? mtuStr) && int.TryParse(mtuStr, out int mtu) && mtu >= 1200)
                    obj["mtu"] = mtu;
                break;
        }

        return obj;
    }

    private static bool TryGetFirstExtra(Dictionary<string, string> extra, out string val, params string[] keys)
    {
        foreach (string k in keys)
        {
            if (extra.TryGetValue(k, out string? found) && !string.IsNullOrWhiteSpace(found))
            {
                val = found.Trim();
                return true;
            }
        }
        val = string.Empty;
        return false;
    }

    private static bool TryBuildAlpnArray(Dictionary<string, string> extra, out JsonArray arr)
    {
        arr = new JsonArray();
        if (!extra.TryGetValue("alpn", out string? raw) || string.IsNullOrWhiteSpace(raw))
            return false;
        foreach (string a in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string clean = a.Trim();
            if (!string.IsNullOrEmpty(clean)) arr.Add(clean);
        }
        return arr.Count > 0;
    }

    private static (string CleanPath, int? EarlyData) SplitWsPathAndEarlyData(string rawPath)
    {
        int qIdx = rawPath.IndexOf('?');
        if (qIdx < 0) return (rawPath, null);

        string basePath = rawPath[..qIdx];
        string query = rawPath[(qIdx + 1)..];
        var keptParams = new List<string>();
        int? earlyData = null;

        foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Regex.Match(part, @"^ed=(\d+)$", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int ed) && ed > 0)
            {
                earlyData = ed;
                continue;
            }
            keptParams.Add(part);
        }

        string normalizedPath = string.IsNullOrEmpty(basePath) ? "/" : basePath;
        if (keptParams.Count > 0)
            normalizedPath += "?" + string.Join("&", keptParams);

        return (normalizedPath, earlyData);
    }

    private void StopProcessLocked(bool deleteConfig = true)
    {
        if (_process != null)
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch { }
            try { _process.Dispose(); } catch { }
            _process = null;
        }

        if (deleteConfig)
        {
            try
            {
                if (File.Exists(_configPath)) File.Delete(_configPath);
            }
            catch { }
        }
    }

    private static string? ReadSingBoxVersion(string binPath)
    {
        try
        {
            var psi = new ProcessStartInfo(binPath, "version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string outStr = p.StandardOutput.ReadToEnd();
            p.WaitForExit(1500);
            string? firstLine = outStr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return firstLine?.Replace("sing-box version ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        }
        catch
        {
            return null;
        }
    }

    private void AttachToKillOnCloseJob(Process proc)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (_winJobHandle == IntPtr.Zero)
            {
                _winJobHandle = CreateJobObject(IntPtr.Zero, null);
                if (_winJobHandle != IntPtr.Zero)
                {
                    var info = new JobObjectExtendedLimitInformation
                    {
                        BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = 0x2000 } // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    };
                    int len = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
                    IntPtr ptr = Marshal.AllocHGlobal(len);
                    try
                    {
                        Marshal.StructureToPtr(info, ptr, false);
                        _ = SetInformationJobObject(_winJobHandle, 9, ptr, (uint)len);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ptr);
                    }
                }
            }
            if (_winJobHandle != IntPtr.Zero)
            {
                _ = AssignProcessToJobObject(_winJobHandle, proc.Handle);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_syncLock)
        {
            StopProcessLocked();
            if (_winJobHandle != IntPtr.Zero)
            {
                try { CloseHandle(_winJobHandle); } catch { }
                _winJobHandle = IntPtr.Zero;
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
