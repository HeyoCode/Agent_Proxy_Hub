using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private const int BridgePortBase = 17920;

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

            bool running = _process != null && !_process.HasExited;
            return new SidecarEngineStatus
            {
                Installed = installed,
                Running = running,
                BinaryPath = bin,
                Version = _cachedVersion,
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
        (string configJson, Dictionary<string, int> portMap) = BuildBridgeConfigJson(
            distinctNodes,
            _boundInterfaceName,
            _lastIsolation,
            BridgePortBase,
            _nodeBridgePorts);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configJson)));

        if (_process != null && !_process.HasExited && hash == _lastConfigHash)
        {
            return true;
        }

        Directory.CreateDirectory(_runtimeDir);
        File.WriteAllText(_configPath, configJson, new UTF8Encoding(false));

        StopProcessLocked();
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

                string targetBin = Path.Combine(_binDir, OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box");
                Stop();

                if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var zip = ZipFile.OpenRead(tmpArchive);
                    var entry = zip.Entries.FirstOrDefault(e =>
                        e.Name.Equals("sing-box.exe", StringComparison.OrdinalIgnoreCase) ||
                        e.Name.Equals("sing-box", StringComparison.OrdinalIgnoreCase));
                    if (entry == null)
                        return (false, "压缩包内未找到 sing-box 可执行文件", GetStatus());
                    entry.ExtractToFile(targetBin, overwrite: true);
                }
                else
                {
                    string extractDir = Path.Combine(Path.GetTempPath(), $"aph-sb-ext-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(extractDir);
                    try
                    {
                        var tar = Process.Start(new ProcessStartInfo("tar", $"-xzf \"{tmpArchive}\" -C \"{extractDir}\"")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                        if (tar != null) await tar.WaitForExitAsync(ct);
                        string? found = Directory.EnumerateFiles(extractDir, "sing-box", SearchOption.AllDirectories).FirstOrDefault();
                        if (string.IsNullOrEmpty(found))
                            return (false, "tar.gz 解压后未找到 sing-box 二进制文件", GetStatus());
                        File.Copy(found, targetBin, overwrite: true);
                        Process.Start(new ProcessStartInfo("chmod", $"+x \"{targetBin}\"") { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(2000);
                    }
                    finally
                    {
                        try { Directory.Delete(extractDir, true); } catch { }
                    }
                }

                lock (_syncLock)
                {
                    _cachedVersion = ReadSingBoxVersion(targetBin);
                    _lastError = null;
                }
                return (true, $"已成功安装 sing-box {tag} 扩展内核", GetStatus());
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
    /// 生成 sing-box 隔离桥接配置（每个 L2 节点映射唯一本地环回端口，并绑定物理硬件网卡绕过 Clash）
    /// </summary>
    public static (string Json, Dictionary<string, int> PortMap) BuildBridgeConfigJson(
        IReadOnlyList<ProxyNode> l2Nodes,
        string? boundInterfaceName,
        IsolationSettings iso,
        int basePort = BridgePortBase,
        IReadOnlyDictionary<string, int>? previousPorts = null)
    {
        var portMap = new Dictionary<string, int>(StringComparer.Ordinal);
        var usedPorts = new HashSet<int>();
        var inbounds = new JsonArray();
        var outbounds = new JsonArray();
        var rules = new JsonArray();

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
                while (usedPorts.Contains(nextCandidate)) nextCandidate++;
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

            outbounds.Add(BuildSingBoxOutbound(node, outTag, boundInterfaceName, iso));

            rules.Add(new JsonObject
            {
                ["inbound"] = new JsonArray(inTag),
                ["outbound"] = outTag
            });
        }

        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn" },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(new JsonObject
                {
                    ["tag"] = "clean-dns",
                    ["address"] = string.IsNullOrWhiteSpace(iso.DohServerIp) ? "223.5.5.5" : iso.DohServerIp,
                    ["detour"] = "direct"
                }),
                ["strategy"] = iso.EnableIpv6 ? "prefer_ipv4" : "ipv4_only"
            },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject
            {
                ["rules"] = rules,
                ["final"] = "direct",
                ["auto_detect_interface"] = string.IsNullOrWhiteSpace(boundInterfaceName)
            }
        };

        string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return (json, portMap);
    }

    private static JsonObject BuildSingBoxOutbound(ProxyNode node, string tag, string? boundInterfaceName, IsolationSettings iso)
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
        obj["domain_strategy"] = iso.EnableIpv6 ? "prefer_ipv4" : "ipv4_only";

        switch (node.Protocol)
        {
            case ProxyProtocol.VLESS:
                obj["type"] = "vless";
                obj["uuid"] = node.Username ?? string.Empty;
                if (extra.TryGetValue("flow", out string? flow) && !string.IsNullOrWhiteSpace(flow))
                    obj["flow"] = flow;

                string sec = extra.TryGetValue("security", out string? s) ? s.ToLowerInvariant() : "none";
                if (sec is "tls" or "reality")
                {
                    var tls = new JsonObject
                    {
                        ["enabled"] = true,
                        ["server_name"] = node.Sni ?? node.Host,
                        ["insecure"] = node.AllowInsecure
                    };
                    if (extra.TryGetValue("fp", out string? fp) && !string.IsNullOrWhiteSpace(fp))
                        tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fp };
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
                    if (extra.TryGetValue("path", out string? path)) ws["path"] = path;
                    if (extra.TryGetValue("host", out string? h)) ws["headers"] = new JsonObject { ["Host"] = h };
                    obj["transport"] = ws;
                }
                else if (net == "grpc")
                {
                    var grpc = new JsonObject { ["type"] = "grpc" };
                    if (extra.TryGetValue("serviceName", out string? sn)) grpc["service_name"] = sn;
                    obj["transport"] = grpc;
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
                obj["tls"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["server_name"] = node.Sni ?? node.Host,
                    ["insecure"] = node.AllowInsecure
                };
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
                if (extra.TryGetValue("alpn", out string? alpn) && !string.IsNullOrWhiteSpace(alpn))
                {
                    var alpnArr = new JsonArray();
                    foreach (string a in alpn.Split(',', StringSplitOptions.RemoveEmptyEntries)) alpnArr.Add(a.Trim());
                    tuicTls["alpn"] = alpnArr;
                }
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

    private void StopProcessLocked()
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
