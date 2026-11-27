using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentProxyHub.Core.Models;

namespace AgentProxyHub.Core.Apps;

/// <summary>
/// AI 应用无损代理接管与受控启动服务：
/// - 在不改动系统全局代理的前提下，为 Claude Code / Cursor / Antigravity / Codex 写入或还原独立端口配置
/// - 所有写操作前自动备份原始键值，保留一切无关配置；关闭接管或端口变更时可精准还原或同步
/// - Antigravity 启动器注入含 150ms 端口探活守卫：网关运行时自动走独立专线，未运行时自动直连不中断使用
/// </summary>
public sealed class AppProxyTakeoverService
{
    private const string ManagedBlockStart = "# >>> AgentProxyHub Managed Block >>>";
    private const string ManagedBlockEnd = "# <<< AgentProxyHub Managed Block <<<";

    private sealed record VsCodeCompatibleIdeSpec(
        string SeatId,
        string DisplayName,
        string RoamingFolderName,
        string WinExeName,
        string WinInstallSubdir,
        string MacAppName,
        string[] ProcessNames);

    private static readonly Dictionary<string, VsCodeCompatibleIdeSpec> VsCodeIdeSpecs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cursor"] = new("cursor", "Cursor", "Cursor", "Cursor.exe", "cursor", "Cursor", ["Cursor"]),
        ["windsurf"] = new("windsurf", "Windsurf", "Windsurf", "Windsurf.exe", "Windsurf", "Windsurf", ["Windsurf"]),
        ["kiro"] = new("kiro", "Kiro", "Kiro", "Kiro.exe", "Kiro", "Kiro", ["Kiro"]),
        ["vscode-copilot"] = new("vscode-copilot", "VS Code Copilot", "Code", "Code.exe", "Microsoft VS Code", "Visual Studio Code", ["Code"]),
        ["trae"] = new("trae", "Trae", "Trae", "Trae.exe", "Trae", "Trae", ["Trae"]),
        ["codebuddy"] = new("codebuddy", "CodeBuddy", "CodeBuddy", "CodeBuddy.exe", "CodeBuddy", "CodeBuddy", ["CodeBuddy", "codebuddy"])
    };

    private static readonly JsonDocumentOptions JsonDocOpts = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteJsonOpts = new()
    {
        WriteIndented = true
    };

    private readonly string _userHome;
    private readonly string _appDataRoaming;
    private readonly string _appDataLocal;
    private readonly string _backupDir;
    private readonly string _launchersDir;
    private readonly Func<string[], bool> _processChecker;
    private readonly bool _isMacOs;
    private readonly object _opLock = new();
    private DateTime _inspectCacheExpiresUtc = DateTime.MinValue;
    private IReadOnlyList<SeatTakeoverStatus>? _inspectCache;

    private static readonly object ProcSnapshotLock = new();
    private static DateTime _procSnapshotExpiresUtc = DateTime.MinValue;
    private static HashSet<string> _procSnapshotNames = new(StringComparer.OrdinalIgnoreCase);

    public sealed class BackupEntry
    {
        public bool Existed { get; set; }
        public string? RawJson { get; set; }
    }

    private sealed class JsonTakeoverBackup
    {
        public string TargetPath { get; set; } = string.Empty;
        public bool FileExistedBefore { get; set; }
        public Dictionary<string, BackupEntry> Keys { get; set; } = new();
    }

    public AppProxyTakeoverService(
        string? customUserHome = null,
        string? customAppDataRoaming = null,
        string? customAppDataLocal = null,
        Func<string[], bool>? processChecker = null,
        bool? forceMacOs = null)
    {
        _userHome = string.IsNullOrWhiteSpace(customUserHome)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : customUserHome;
        _isMacOs = forceMacOs ?? OperatingSystem.IsMacOS();
        _appDataRoaming = string.IsNullOrWhiteSpace(customAppDataRoaming)
            ? (_isMacOs
                ? Path.Combine(_userHome, "Library", "Application Support")
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
            : customAppDataRoaming;
        _appDataLocal = string.IsNullOrWhiteSpace(customAppDataLocal)
            ? (_isMacOs
                ? Path.Combine(_userHome, "Library", "Application Support")
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            : customAppDataLocal;

        string hubDir = Path.Combine(_userHome, ".agent-proxy-hub");
        _backupDir = Path.Combine(hubDir, "takeover-backups");
        _launchersDir = Path.Combine(hubDir, "launchers");
        _processChecker = processChecker ?? DefaultCheckProcessRunning;
    }

    /// <summary>
    /// 使席位进程/接管检查短缓存失效（配置修改、接管切换后自动调用）
    /// </summary>
    public void InvalidateCache()
    {
        lock (_opLock)
        {
            _inspectCacheExpiresUtc = DateTime.MinValue;
            _inspectCache = null;
        }
        lock (ProcSnapshotLock)
        {
            _procSnapshotExpiresUtc = DateTime.MinValue;
        }
    }

    /// <summary>
    /// 只读检查全部席位的应用运行状态、配置文件接管状态与可用启动器路径（内置 2.5s 短缓存降低开销）
    /// </summary>
    public IReadOnlyList<SeatTakeoverStatus> InspectAll(IReadOnlyList<AppSeat> seats)
    {
        lock (_opLock)
        {
            DateTime now = DateTime.UtcNow;
            if (now <= _inspectCacheExpiresUtc && _inspectCache != null && _inspectCache.Count == seats.Count)
            {
                return _inspectCache;
            }
            var fresh = seats.Select(InspectSeatInternal).ToList();
            _inspectCache = fresh;
            _inspectCacheExpiresUtc = now.AddSeconds(2.5);
            return fresh;
        }
    }

    public SeatTakeoverStatus InspectSeat(AppSeat seat)
    {
        lock (_opLock)
        {
            return InspectSeatInternal(seat);
        }
    }

    /// <summary>
    /// 对指定席位启用或无损还原代理接管配置
    /// </summary>
    public (bool Ok, string Message, SeatTakeoverStatus Status) SetTakeover(AppSeat seat, bool enable)
    {
        lock (_opLock)
        {
            try
            {
                (bool ok, string msg) = seat.Id switch
                {
                    "claude" => enable ? EnableClaudeTakeover(seat) : RestoreClaudeTakeover(),
                    "agy" or "antigravity" => enable ? EnableAntigravityTakeover(seat) : RestoreAntigravityTakeover(),
                    "chatgpt" => enable ? EnableChatGptLauncherTakeover(seat) : RestoreChatGptLauncherTakeover(),
                    "zed" => enable ? EnableZedTakeover(seat) : RestoreZedTakeover(),
                    _ when VsCodeIdeSpecs.TryGetValue(seat.Id, out var ideSpec) =>
                        enable ? EnableVsCodeIdeTakeover(seat, ideSpec) : RestoreVsCodeIdeTakeover(ideSpec),
                    _ => enable ? EnableGenericLauncherTakeover(seat) : RestoreGenericLauncherTakeover(seat)
                };

                _inspectCacheExpiresUtc = DateTime.MinValue;
                _inspectCache = null;
                var status = InspectSeatInternal(seat);
                return (ok, msg, status);
            }
            catch (Exception ex)
            {
                var status = InspectSeatInternal(seat);
                return (false, $"接管操作失败（原配置未改动）：{ex.Message}", status);
            }
        }
    }

    /// <summary>
    /// 安全重启并携带独立代理端口重连（严格禁止终止正在运行的 Antigravity 与本网关进程）
    /// </summary>
    public async Task<(bool Ok, string Message)> GracefulRestartWithProxyAsync(AppSeat seat, CancellationToken ct = default)
    {
        if (seat.Id is "agy" or "antigravity")
        {
            return (false, "为保护当前正在运行的 Antigravity 会话，系统禁止一键强制结束或自动重启 Antigravity；请先确认已开启「一键接管」，再由您手动重开窗口。");
        }

        string[] procNames = ResolveProcessNames(seat);
        if (procNames.Length == 0 && VsCodeIdeSpecs.TryGetValue(seat.Id, out var spec))
            procNames = spec.ProcessNames;

        int killed = 0;
        foreach (string procName in procNames)
        {
            if (procName.Contains("Antigravity", StringComparison.OrdinalIgnoreCase)
                || procName.Contains("language_server", StringComparison.OrdinalIgnoreCase)
                || procName.Contains("AgentProxyHub", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    using (p)
                    {
                        try
                        {
                            if (!p.HasExited)
                            {
                                p.CloseMainWindow();
                                if (!p.WaitForExit(900))
                                    p.Kill(entireProcessTree: true);
                                killed++;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        if (killed > 0)
            await Task.Delay(350, ct);

        var (ok, msg) = LaunchWithProxy(seat);
        return ok
            ? (true, killed > 0 ? $"已关闭 {killed} 个旧进程并重连 :{seat.LocalPort}：{msg}" : msg)
            : (false, msg);
    }

    /// <summary>
    /// 以独立进程环境变量注入/独立 User-Data-Dir 方式受控启动目标 AI 应用（绝不杀掉或重启正在运行的 Antigravity 会话）
    /// </summary>
    public (bool Ok, string Message) LaunchWithProxy(
        AppSeat seat,
        string? profileDir = null,
        string? extraArgs = null,
        string? workingDirectory = null)
    {
        lock (_opLock)
        {
            string baseSeatId = seat.Id.Split(":inst:", StringSplitOptions.None)[0];
            var baseDummy = baseSeatId == seat.Id ? seat : new AppSeat
            {
                Id = baseSeatId,
                Name = seat.Name,
                LocalPort = seat.LocalPort,
                BoundNodeId = seat.BoundNodeId,
                CustomExecutablePath = seat.CustomExecutablePath,
                CustomNoProxy = seat.CustomNoProxy,
                LaunchCommandWin = seat.LaunchCommandWin,
                LaunchCommandMac = seat.LaunchCommandMac,
                ProcessWin = seat.ProcessWin,
                ProcessMac = seat.ProcessMac
            };
            var status = InspectSeatInternal(baseDummy);
            string proxyHttp = $"http://127.0.0.1:{seat.LocalPort}";
            string proxySocks = $"socks5h://127.0.0.1:{seat.LocalPort}";
            string noProxy = BuildEffectiveNoProxy(seat, _isMacOs);

            if (baseSeatId is "agy" or "antigravity")
            {
                if (string.IsNullOrWhiteSpace(profileDir) && status.IsProcessRunning)
                {
                    return (true,
                        $"检测到 Antigravity 正在运行，为保护当前会话未强制重启；请确认已开启「一键接管」，下次打开 Antigravity 即可通过 :{seat.LocalPort} 独立出口运行。");
                }

                if (_isMacOs)
                {
                    string macTarget = ResolveCustomOrDetectedExe(seat, () => "Antigravity") ?? "Antigravity";
                    var psi = new ProcessStartInfo("open")
                    {
                        UseShellExecute = false
                    };
                    psi.ArgumentList.Add("-n");
                    psi.ArgumentList.Add("-a");
                    psi.ArgumentList.Add(macTarget);
                    psi.ArgumentList.Add("--args");
                    psi.ArgumentList.Add($"--proxy-server={proxyHttp}");
                    psi.ArgumentList.Add($"--proxy-bypass-list={BuildChromiumBypassList(noProxy)}");
                    if (!string.IsNullOrWhiteSpace(profileDir))
                        psi.ArgumentList.Add($"--user-data-dir={profileDir}");
                    AppendSplitArgs(psi, extraArgs);
                    ApplyProxyEnv(psi, proxyHttp, proxySocks, noProxy);
                    Process.Start(psi);
                    return (true, $"已携带 :{seat.LocalPort} 代理参数启动 Antigravity");
                }

                string zhScript = GetAntigravityZhLaunchScriptPath();
                if (string.IsNullOrWhiteSpace(profileDir) && string.IsNullOrWhiteSpace(seat.CustomExecutablePath) && File.Exists(zhScript))
                {
                    string psExe = ResolveWindowsPowerShellExe();
                    var psi = new ProcessStartInfo(psExe)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    psi.ArgumentList.Add("-NoProfile");
                    psi.ArgumentList.Add("-WindowStyle");
                    psi.ArgumentList.Add("Hidden");
                    psi.ArgumentList.Add("-ExecutionPolicy");
                    psi.ArgumentList.Add("Bypass");
                    psi.ArgumentList.Add("-File");
                    psi.ArgumentList.Add(zhScript);
                    ApplyProxyEnv(psi, proxyHttp, proxySocks, noProxy);
                    Process.Start(psi);
                    return (true, $"已通过独立专线 (:{seat.LocalPort}) 拉起 Antigravity");
                }

                string? exe = ResolveCustomOrDetectedExe(seat, ResolveAntigravityExe);
                if (!string.IsNullOrEmpty(exe))
                {
                    var psi = new ProcessStartInfo(exe)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory) ? workingDirectory : ""
                    };
                    psi.ArgumentList.Add($"--proxy-server={proxyHttp}");
                    psi.ArgumentList.Add($"--proxy-bypass-list={BuildChromiumBypassList(noProxy)}");
                    if (!string.IsNullOrWhiteSpace(profileDir))
                        psi.ArgumentList.Add($"--user-data-dir={profileDir}");
                    AppendSplitArgs(psi, extraArgs);
                    ApplyProxyEnv(psi, proxyHttp, proxySocks, noProxy);
                    Process.Start(psi);
                    return (true, $"已携带 :{seat.LocalPort} 代理参数启动 Antigravity" + (!string.IsNullOrWhiteSpace(profileDir) ? " (独立配置目录)" : ""));
                }

                return (false, "未找到本机 Antigravity 可执行文件，可使用专属启动脚本或在「应用接管 & CLI」指定自定义路径。");
            }

            if (VsCodeIdeSpecs.TryGetValue(baseSeatId, out var launchIdeSpec))
            {
                return LaunchVsCodeCompatibleIde(seat, launchIdeSpec, proxyHttp, proxySocks, noProxy, profileDir, extraArgs, workingDirectory);
            }

            if (baseSeatId == "zed")
            {
                string? zedExe = _isMacOs ? "Zed" : ResolveCustomOrDetectedExe(seat, ResolveZedExe);
                if (!string.IsNullOrEmpty(zedExe))
                {
                    var psi = _isMacOs ? new ProcessStartInfo("open") { UseShellExecute = false } : new ProcessStartInfo(zedExe) { UseShellExecute = false };
                    if (_isMacOs)
                    {
                        psi.ArgumentList.Add("-n");
                        psi.ArgumentList.Add("-a");
                        psi.ArgumentList.Add(zedExe);
                        psi.ArgumentList.Add("--args");
                    }
                    if (!string.IsNullOrWhiteSpace(profileDir))
                    {
                        psi.ArgumentList.Add("--user-data-dir");
                        psi.ArgumentList.Add(profileDir);
                    }
                    if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
                        psi.WorkingDirectory = workingDirectory;
                    AppendSplitArgs(psi, extraArgs);
                    ApplyProxyEnv(psi, proxyHttp, proxySocks, noProxy);
                    Process.Start(psi);
                    return (true, $"已携带 :{seat.LocalPort} 独立代理环境变量启动 Zed");
                }
                return (false, "未找到 Zed 可执行文件；可在「应用接管 & CLI」自定义路径或使用 settings.json 接管。");
            }

            if (baseSeatId == "claude-desktop")
            {
                if (_isMacOs)
                {
                    string macTarget = ResolveCustomOrDetectedExe(seat, () => "Claude") ?? "Claude";
                    var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                    psi.ArgumentList.Add("-a");
                    psi.ArgumentList.Add(macTarget);
                    psi.ArgumentList.Add("--args");
                    psi.ArgumentList.Add($"--proxy-server={proxyHttp}");
                    psi.ArgumentList.Add($"--proxy-bypass-list={BuildChromiumBypassList(noProxy)}");
                    if (!string.IsNullOrWhiteSpace(profileDir))
                        psi.ArgumentList.Add($"--user-data-dir={profileDir}");
                    ApplyProxyEnv(psi, proxyHttp, proxySocks, noProxy);
                    Process.Start(psi);
                    return (true, $"已携带 :{seat.LocalPort} 代理参数启动 Claude Desktop");
                }

                string? claudeExe = ResolveCustomOrDetectedExe(seat, ResolveClaudeDesktopExe);
                if (!string.IsNullOrEmpty(claudeExe))
                {
                    var psi = new ProcessStartInfo(claudeExe) { UseShellExecute = false };
                    psi.ArgumentList.Add($"--proxy-server={proxyHttp}");
                    psi.ArgumentList.Add($"--proxy-bypass-list={BuildChromiumBypassList(noProxy)}");
                    if (!string.IsNullOrWhiteSpace(profileDir))
                        psi.ArgumentList.Add($"--user-data-dir={profileDir}");
                    AppendSplitArgs(psi, extraArgs);
                    ApplyProxyEnv(psi, proxyHttp, proxySocks, noProxy);
                    Process.Start(psi);
                    return (true, $"已携带 :{seat.LocalPort} 代理参数启动 Claude Desktop");
                }
            }

            // Claude Code / Codex / Gemini CLI / Grok Build / OpenCode 等：打开已预注入该席位代理环境变量（含可选隔离目录）的独立终端窗口
            string bannerTitle = seat.Name;
            string cliHint = !string.IsNullOrWhiteSpace(seat.CustomExecutablePath)
                ? seat.CustomExecutablePath.Trim()
                : ResolveCliHint(baseDummy);

            if (_isMacOs)
            {
                string scriptPath = WriteMacLauncherScript(seat, bannerTitle, cliHint, profileDir);
                var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                psi.ArgumentList.Add("-a");
                psi.ArgumentList.Add("Terminal");
                psi.ArgumentList.Add(scriptPath);
                Process.Start(psi);
                return (true, $"已打开预注入 :{seat.LocalPort} 独立专线的 Terminal 终端");
            }

            string profileEnvCmd = string.IsNullOrWhiteSpace(profileDir)
                ? string.Empty
                : $"$env:CLAUDE_CONFIG_DIR='{profileDir}'; $env:CODEX_HOME='{profileDir}'; $env:GEMINI_CLI_HOME='{profileDir}'; $env:XDG_CONFIG_HOME='{profileDir}'; ";
            string cdCmd = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)
                ? $"Set-Location '{workingDirectory.Replace("'", "''")}'; "
                : string.Empty;

            string initCmd =
                cdCmd +
                $"$env:HTTP_PROXY='{proxyHttp}'; $env:HTTPS_PROXY='{proxyHttp}'; $env:ALL_PROXY='{proxySocks}'; $env:NO_PROXY='{noProxy}'; " +
                profileEnvCmd +
                $"Write-Host '[Agent Proxy Hub] {bannerTitle} 独立专线已挂载 -> {proxyHttp}' -ForegroundColor Cyan; " +
                $"Write-Host '直接输入 {cliHint} 即可从专属出口通道通信，不影响系统或 Clash。' -ForegroundColor DarkGray";

            StartWindowsInteractiveShell(initCmd);
            return (true, $"已打开已注入 :{seat.LocalPort} 独立专线的交互式终端" + (!string.IsNullOrWhiteSpace(profileDir) ? $"（目录隔离: {profileDir}）" : ""));
        }
    }

    private SeatTakeoverStatus InspectSeatInternal(AppSeat seat)
    {
        string expectedProxy = $"http://127.0.0.1:{seat.LocalPort}";

        return seat.Id switch
        {
            "claude" => InspectClaude(seat, expectedProxy),
            "agy" or "antigravity" => InspectAntigravity(seat, expectedProxy),
            "chatgpt" => InspectChatGpt(seat, expectedProxy),
            "zed" => InspectZed(seat, expectedProxy),
            _ when VsCodeIdeSpecs.TryGetValue(seat.Id, out var spec) => InspectVsCodeIde(seat, expectedProxy, spec),
            _ => InspectGenericLauncher(seat, expectedProxy)
        };
    }

    private SeatTakeoverStatus InspectGenericLauncher(AppSeat seat, string expectedProxy)
    {
        string launcherPath = GetGenericLauncherPath(seat.Id);
        string[] procNames = ResolveProcessNames(seat);
        bool running = procNames.Length > 0 && _processChecker(procNames);
        bool takenOver = File.Exists(launcherPath) && File.ReadAllText(launcherPath).Contains(expectedProxy, StringComparison.OrdinalIgnoreCase);

        return new SeatTakeoverStatus
        {
            SeatId = seat.Id,
            IsProcessRunning = running,
            SupportsConfigTakeover = true,
            IsTakenOver = takenOver,
            ModeSummary = seat.Id == "claude-desktop"
                ? "Claude Desktop 桌面代理路由启动器"
                : $"{seat.Name} 独立代理环境启动器",
            TargetPath = launcherPath,
            LaunchExecutable = seat.Id == "claude-desktop" ? (ResolveClaudeDesktopExe() ?? "Claude") : ResolveCliHint(seat),
            StatusHint = takenOver
                ? $"已生成专属隔离启动脚本 ({expectedProxy})；点击「专线启动」即可以独立出口运行 {seat.Name}。"
                : $"未生成启动封装：点击「一键接管」生成专属隔离启动脚本，或直接点击「专线启动」拉起预注入代理环境的终端。"
        };
    }

    private (bool Ok, string Message) EnableGenericLauncherTakeover(AppSeat seat)
    {
        Directory.CreateDirectory(_launchersDir);
        string path = GetGenericLauncherPath(seat.Id);
        string http = $"http://127.0.0.1:{seat.LocalPort}";
        string socks = $"socks5h://127.0.0.1:{seat.LocalPort}";
        string noProxy = BuildEffectiveNoProxy(seat, _isMacOs);
        string cli = ResolveCliHint(seat);

        if (_isMacOs)
        {
            string body = seat.Id == "claude-desktop"
                ? $"#!/usr/bin/env bash\nexport HTTPS_PROXY=\"{http}\"\nexport HTTP_PROXY=\"{http}\"\nexport ALL_PROXY=\"{socks}\"\nexport NO_PROXY=\"{noProxy}\"\nopen -a Claude --args --proxy-server=\"{http}\"\n"
                : $"#!/usr/bin/env bash\nexport HTTPS_PROXY=\"{http}\"\nexport HTTP_PROXY=\"{http}\"\nexport ALL_PROXY=\"{socks}\"\nexport NO_PROXY=\"{noProxy}\"\necho \"[Agent Proxy Hub] {seat.Name} 独立出口已就绪 ({http})\"\nexec {cli} \"$@\"\n";
            File.WriteAllText(path, body, new UTF8Encoding(false));
        }
        else
        {
            string body = seat.Id == "claude-desktop"
                ? $"$env:HTTPS_PROXY=\"{http}\"\r\n$env:HTTP_PROXY=\"{http}\"\r\n$env:ALL_PROXY=\"{socks}\"\r\n$env:NO_PROXY=\"{noProxy}\"\r\n$exe = \"$env:LOCALAPPDATA\\AnthropicClaude\\Claude.exe\"\r\nif (Test-Path $exe) {{ Start-Process -FilePath $exe -ArgumentList \"--proxy-server={http}\" }} else {{ Write-Host \"未在默认目录找到 Claude.exe，请手动启动并附带 --proxy-server={http}\" }}\r\n"
                : $"$env:HTTPS_PROXY=\"{http}\"\r\n$env:HTTP_PROXY=\"{http}\"\r\n$env:ALL_PROXY=\"{socks}\"\r\n$env:NO_PROXY=\"{noProxy}\"\r\nWrite-Host \"[Agent Proxy Hub] {seat.Name} 独立出口已就绪 ({http})\" -ForegroundColor Cyan\r\nWrite-Host \"直接运行: {cli}\" -ForegroundColor Green\r\n";
            File.WriteAllText(path, body, new UTF8Encoding(true));
        }

        return (true, $"已为 {seat.Name} 生成独立隔离启动器 ({http})");
    }

    private (bool Ok, string Message) RestoreGenericLauncherTakeover(AppSeat seat)
    {
        string path = GetGenericLauncherPath(seat.Id);
        if (File.Exists(path)) File.Delete(path);
        return (true, $"已移除 {seat.Name} 托管启动脚本");
    }

    private string GetGenericLauncherPath(string seatId)
    {
        string safe = Regex.Replace(seatId, @"[^a-zA-Z0-9_-]", "_");
        return Path.Combine(_launchersDir, _isMacOs ? $"launch-{safe}.sh" : $"launch-{safe}.ps1");
    }

    private string? ResolveClaudeDesktopExe()
    {
        string[] candidates =
        [
            Path.Combine(_appDataLocal, "AnthropicClaude", "Claude.exe"),
            Path.Combine(_appDataLocal, "Programs", "claude-desktop", "Claude.exe"),
            Path.Combine(_appDataLocal, "Programs", "Claude", "Claude.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string ResolveCliHint(AppSeat seat) => seat.Id switch
    {
        "claude" => "claude",
        "chatgpt" => "codex",
        "claude-desktop" => "Claude",
        "gemini-cli" => "gemini",
        "grok-build" => "grok",
        "opencode" => "opencode",
        "openclaw" => "openclaw",
        "hermes" => "hermes",
        "pi" => "pi",
        "minimax-code" => "minimax",
        "kimi-code" => "kimi",
        "deepseek-harness" => "deepseek",
        "oh-my-pi" => "omp",
        "windsurf" => "windsurf",
        "kiro" => "kiro",
        "vscode-copilot" => "code",
        "zed" => "zed",
        "trae" => "trae",
        "codebuddy" => "codebuddy",
        _ => string.IsNullOrWhiteSpace(seat.LaunchCommandWin) ? seat.Id : seat.LaunchCommandWin.Split(' ')[0]
    };

    private static string[] ResolveProcessNames(AppSeat seat) => seat.Id switch
    {
        "claude" => ["claude"],
        "claude-desktop" => ["Claude", "claude"],
        "cursor" => ["Cursor"],
        "chatgpt" => ["codex", "ChatGPT"],
        "gemini-cli" => ["gemini"],
        "grok-build" => ["grok"],
        "opencode" => ["opencode"],
        "openclaw" => ["openclaw"],
        "hermes" => ["hermes"],
        "pi" => ["pi"],
        "minimax-code" => ["minimax"],
        "kimi-code" => ["kimi"],
        "deepseek-harness" => ["deepseek"],
        "oh-my-pi" => ["omp"],
        "windsurf" => ["Windsurf"],
        "kiro" => ["Kiro"],
        "vscode-copilot" => ["Code"],
        "zed" => ["Zed", "zed"],
        "trae" => ["Trae"],
        "codebuddy" => ["CodeBuddy", "codebuddy"],
        _ => []
    };

    private SeatTakeoverStatus InspectClaude(AppSeat seat, string expectedProxy)
    {
        string path = GetClaudeSettingsPath();
        bool running = _processChecker(["claude"]);
        bool takenOver = false;

        if (File.Exists(path))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(path), null, JsonDocOpts) as JsonObject;
                string? current = root?["env"]?["HTTPS_PROXY"]?.GetValue<string>();
                takenOver = string.Equals(current, expectedProxy, StringComparison.OrdinalIgnoreCase);
            }
            catch { }
        }

        return new SeatTakeoverStatus
        {
            SeatId = seat.Id,
            IsProcessRunning = running,
            SupportsConfigTakeover = true,
            IsTakenOver = takenOver,
            ModeSummary = "Claude Code 配置直写 (~/.claude/settings.json)",
            TargetPath = path,
            LaunchExecutable = "claude",
            StatusHint = takenOver
                ? $"已将 HTTPS_PROXY / HTTP_PROXY 写入 Claude Code 配置 ({expectedProxy})，保留全部已有凭据。"
                : (running
                    ? $"检测到 claude 正在运行但未接入 :{seat.LocalPort}，点击「一键接管」写入配置后新建对话/重启 CLI 生效。"
                    : $"未接管配置：点击「一键接管」可将 {expectedProxy} 无损写入 ~/.claude/settings.json。")
        };
    }

    private SeatTakeoverStatus InspectVsCodeIde(AppSeat seat, string expectedProxy, VsCodeCompatibleIdeSpec spec)
    {
        string path = GetVsCodeIdeSettingsPath(spec);
        bool running = _processChecker(spec.ProcessNames);
        bool takenOver = false;

        if (File.Exists(path))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(path), null, JsonDocOpts) as JsonObject;
                string? current = root?["http.proxy"]?.GetValue<string>();
                takenOver = string.Equals(current, expectedProxy, StringComparison.OrdinalIgnoreCase);
            }
            catch { }
        }

        string? exe = _isMacOs ? $"/Applications/{spec.MacAppName}.app" : ResolveVsCodeIdeExe(spec);

        return new SeatTakeoverStatus
        {
            SeatId = seat.Id,
            IsProcessRunning = running,
            SupportsConfigTakeover = true,
            IsTakenOver = takenOver,
            ModeSummary = $"{spec.DisplayName} User/settings.json 热接管",
            TargetPath = path,
            LaunchExecutable = exe,
            StatusHint = takenOver
                ? $"已接入 {spec.DisplayName} 配置 (http.proxy = {expectedProxy})，修改配置即刻热生效。"
                : (running
                    ? $"检测到 {spec.DisplayName} 正在运行但未指向 :{seat.LocalPort}，点击「一键接管」立即写入 http.proxy 热生效。"
                    : $"未接管配置：点击「一键接管」即可把 {spec.DisplayName} 流量定向到 :{seat.LocalPort}。")
        };
    }

    private SeatTakeoverStatus InspectZed(AppSeat seat, string expectedProxy)
    {
        string path = GetZedSettingsPath();
        bool running = _processChecker(["Zed", "zed"]);
        bool takenOver = false;

        if (File.Exists(path))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(path), null, JsonDocOpts) as JsonObject;
                string? current = root?["proxy"]?.GetValue<string>();
                takenOver = string.Equals(current, expectedProxy, StringComparison.OrdinalIgnoreCase);
            }
            catch { }
        }

        string? exe = _isMacOs ? "/Applications/Zed.app" : ResolveZedExe();
        return new SeatTakeoverStatus
        {
            SeatId = seat.Id,
            IsProcessRunning = running,
            SupportsConfigTakeover = true,
            IsTakenOver = takenOver,
            ModeSummary = "Zed settings.json 原生代理字段热接管",
            TargetPath = path,
            LaunchExecutable = exe,
            StatusHint = takenOver
                ? $"已将 proxy = {expectedProxy} 无损写入 Zed settings.json。"
                : $"未接管：点击「一键接管」把 Zed 独立配置定向到 :{seat.LocalPort}。"
        };
    }

    private SeatTakeoverStatus InspectAntigravity(AppSeat seat, string expectedProxy)
    {
        string zhScript = GetAntigravityZhLaunchScriptPath();
        bool hasZhLauncher = !_isMacOs && File.Exists(zhScript);
        string agySettingsPath = GetAntigravityIdeSettingsPath();
        string launcherCmd = GetAntigravityProxyLauncherPath();
        bool running = _processChecker(["Antigravity", "language_server"]);

        bool zhMatched = false;
        if (hasZhLauncher)
        {
            try
            {
                string content = File.ReadAllText(zhScript);
                zhMatched = content.Contains(ManagedBlockStart, StringComparison.Ordinal)
                    && content.Contains(expectedProxy, StringComparison.OrdinalIgnoreCase);
            }
            catch { }
        }

        bool cmdMatched = false;
        if (File.Exists(launcherCmd))
        {
            try
            {
                string content = File.ReadAllText(launcherCmd);
                cmdMatched = content.Contains(expectedProxy, StringComparison.OrdinalIgnoreCase);
            }
            catch { }
        }

        bool takenOver = hasZhLauncher ? zhMatched : cmdMatched;
        string? exe = _isMacOs ? "/Applications/Antigravity.app" : ResolveAntigravityExe();

        string hint;
        if (takenOver && running)
        {
            hint = $"启动器已接管 ({expectedProxy})；当前运行中的 Antigravity 若是在接管前启动的，重启一次应用后流量即切入 :{seat.LocalPort}。";
        }
        else if (takenOver)
        {
            hint = $"启动器已接管（含 150ms 端口探活守卫）：直接从桌面快捷方式打开 Antigravity 即走 :{seat.LocalPort}。";
        }
        else if (running)
        {
            hint = $"当前运行中的 Antigravity 启动时未传入 :{seat.LocalPort}（因此吞吐显示 0 B）；点击「一键接管」后下次打开即自动接入。";
        }
        else
        {
            hint = $"未接管：点击「一键接管」将注入带端口探活守卫的启动配置并生成专属快捷脚本 ({expectedProxy})。";
        }

        return new SeatTakeoverStatus
        {
            SeatId = seat.Id,
            IsProcessRunning = running,
            SupportsConfigTakeover = true,
            IsTakenOver = takenOver,
            ModeSummary = hasZhLauncher
                ? "Antigravity 自愈启动器守护接管 (带探活守卫)"
                : "Antigravity 专属代理启动器 + CLI 环境",
            TargetPath = hasZhLauncher ? zhScript : (File.Exists(agySettingsPath) ? agySettingsPath : launcherCmd),
            LaunchExecutable = exe ?? launcherCmd,
            StatusHint = hint
        };
    }

    private SeatTakeoverStatus InspectChatGpt(AppSeat seat, string expectedProxy)
    {
        string launcherPath = GetCodexLauncherPath();
        bool running = _processChecker(["codex", "ChatGPT"]);
        bool takenOver = false;

        if (File.Exists(launcherPath))
        {
            try
            {
                takenOver = File.ReadAllText(launcherPath).Contains(expectedProxy, StringComparison.OrdinalIgnoreCase);
            }
            catch { }
        }

        return new SeatTakeoverStatus
        {
            SeatId = seat.Id,
            IsProcessRunning = running,
            SupportsConfigTakeover = true,
            IsTakenOver = takenOver,
            ModeSummary = "Codex / ChatGPT 独立专线启动器",
            TargetPath = launcherPath,
            LaunchExecutable = launcherPath,
            StatusHint = takenOver
                ? $"已生成 Codex 独立代理启动脚本 ({expectedProxy})，也可直接点击「专线启动」打开终端。"
                : $"未生成启动封装：点击「一键接管」生成专属启动脚本，或点击「专线启动」拉起已注入环境的终端。"
        };
    }

    private (bool Ok, string Message) EnableClaudeTakeover(AppSeat seat)
    {
        string path = GetClaudeSettingsPath();
        string proxy = $"http://127.0.0.1:{seat.LocalPort}";
        var updates = new Dictionary<string, JsonNode?>
        {
            ["HTTP_PROXY"] = JsonValue.Create(proxy),
            ["HTTPS_PROXY"] = JsonValue.Create(proxy),
            ["NO_PROXY"] = JsonValue.Create(BuildEffectiveNoProxy(seat, _isMacOs))
        };

        UpdateJsonObjectWithBackup("claude", path, ["env"], updates);
        return (true, $"已无损接管 Claude Code 配置（HTTPS_PROXY -> {proxy}）");
    }

    private (bool Ok, string Message) RestoreClaudeTakeover()
    {
        bool restored = RestoreJsonObjectFromBackup("claude", ["env"]);
        return restored
            ? (true, "已按备份无损还原 ~/.claude/settings.json 原始代理设置")
            : (true, "未检测到历史接管备份，~/.claude/settings.json 保持原样");
    }

    private (bool Ok, string Message) EnableVsCodeIdeTakeover(AppSeat seat, VsCodeCompatibleIdeSpec spec)
    {
        string path = GetVsCodeIdeSettingsPath(spec);
        string proxy = $"http://127.0.0.1:{seat.LocalPort}";
        var updates = new Dictionary<string, JsonNode?>
        {
            ["http.proxy"] = JsonValue.Create(proxy),
            ["http.proxySupport"] = JsonValue.Create("override")
        };

        UpdateJsonObjectWithBackup(spec.SeatId, path, [], updates);
        return (true, $"已无损写入 {spec.DisplayName} settings.json（http.proxy -> {proxy}）");
    }

    private (bool Ok, string Message) RestoreVsCodeIdeTakeover(VsCodeCompatibleIdeSpec spec)
    {
        bool restored = RestoreJsonObjectFromBackup(spec.SeatId, []);
        return restored
            ? (true, $"已按备份无损还原 {spec.DisplayName} 原始代理设置")
            : (true, $"未检测到 {spec.DisplayName} 历史接管备份，原配置未改动");
    }

    private (bool Ok, string Message) EnableZedTakeover(AppSeat seat)
    {
        string path = GetZedSettingsPath();
        string proxy = $"http://127.0.0.1:{seat.LocalPort}";
        UpdateJsonObjectWithBackup("zed", path, [], new Dictionary<string, JsonNode?>
        {
            ["proxy"] = JsonValue.Create(proxy)
        });
        return (true, $"已无损写入 Zed settings.json（proxy -> {proxy}）");
    }

    private (bool Ok, string Message) RestoreZedTakeover()
    {
        bool restored = RestoreJsonObjectFromBackup("zed", []);
        return restored
            ? (true, "已按备份无损还原 Zed 原始代理设置")
            : (true, "未检测到 Zed 历史接管备份，原配置未改动");
    }

    private (bool Ok, string Message) LaunchVsCodeCompatibleIde(
        AppSeat seat,
        VsCodeCompatibleIdeSpec spec,
        string proxyHttp,
        string proxySocks,
        string noProxy,
        string? profileDir,
        string? extraArgs,
        string? workingDirectory)
    {
        if (_isMacOs)
        {
            string macTarget = ResolveCustomOrDetectedExe(seat, () => spec.MacAppName) ?? spec.MacAppName;
            var psi = new ProcessStartInfo("open") { UseShellExecute = false };
            if (!string.IsNullOrWhiteSpace(profileDir)) psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(macTarget);
            psi.ArgumentList.Add("--args");
            psi.ArgumentList.Add($"--proxy-server={proxyHttp}");
            psi.ArgumentList.Add($"--proxy-bypass-list={BuildChromiumBypassList(noProxy)}");
            if (!string.IsNullOrWhiteSpace(profileDir))
                psi.ArgumentList.Add($"--user-data-dir={profileDir}");
            AppendSplitArgs(psi, extraArgs);
            ApplyProxyEnv(psi, proxyHttp, proxySocks, noProxy);
            Process.Start(psi);
            return (true, $"已携带 :{seat.LocalPort} 代理参数启动 {spec.DisplayName}");
        }

        string? exe = ResolveCustomOrDetectedExe(seat, () => ResolveVsCodeIdeExe(spec));
        if (!string.IsNullOrEmpty(exe))
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
                psi.WorkingDirectory = workingDirectory;
            psi.ArgumentList.Add($"--proxy-server={proxyHttp}");
            psi.ArgumentList.Add($"--proxy-bypass-list={BuildChromiumBypassList(noProxy)}");
            if (!string.IsNullOrWhiteSpace(profileDir))
                psi.ArgumentList.Add($"--user-data-dir={profileDir}");
            AppendSplitArgs(psi, extraArgs);
            ApplyProxyEnv(psi, proxyHttp, proxySocks, noProxy);
            Process.Start(psi);
            return (true, $"已携带 :{seat.LocalPort} 代理参数启动 {spec.DisplayName}" + (!string.IsNullOrWhiteSpace(profileDir) ? " (独立配置目录)" : ""));
        }

        return (false, $"未在常见路径找到 {spec.WinExeName}；可在「应用接管 & CLI」指定自定义可执行路径，或使用 settings.json 热接管。");
    }

    private (bool Ok, string Message) EnableAntigravityTakeover(AppSeat seat)
    {
        string proxy = $"http://127.0.0.1:{seat.LocalPort}";
        string socksProxy = $"socks5h://127.0.0.1:{seat.LocalPort}";
        string noProxy = BuildEffectiveNoProxy(seat, _isMacOs);

        // 1. 生成独立代理启动脚本（Windows .cmd 或 macOS .command）
        Directory.CreateDirectory(_launchersDir);
        string launcherPath = GetAntigravityProxyLauncherPath();
        if (_isMacOs)
        {
            string script =
                "#!/bin/bash\n" +
                $"export HTTP_PROXY=\"{proxy}\"\n" +
                $"export HTTPS_PROXY=\"{proxy}\"\n" +
                $"export ALL_PROXY=\"{socksProxy}\"\n" +
                $"export NO_PROXY=\"{noProxy}\"\n" +
                $"open -a Antigravity --args --proxy-server=\"{proxy}\" --proxy-bypass-list=\"<-loopback>;localhost;127.0.0.1;[::1]\"\n";
            File.WriteAllText(launcherPath, script, new UTF8Encoding(false));
        }
        else
        {
            string exe = ResolveAntigravityExe() ?? @"%LOCALAPPDATA%\Programs\antigravity\Antigravity.exe";
            string script =
                "@echo off\r\n" +
                $"set HTTP_PROXY={proxy}\r\n" +
                $"set HTTPS_PROXY={proxy}\r\n" +
                $"set ALL_PROXY={socksProxy}\r\n" +
                $"set NO_PROXY={noProxy}\r\n" +
                $"start \"\" \"{exe}\" --proxy-server=\"{proxy}\" --proxy-bypass-list=\"<-loopback>;localhost;127.0.0.1;[::1]\"\r\n";
            File.WriteAllText(launcherPath, script, new UTF8Encoding(false));
        }

        // 2. 若本机存在 Antigravity-Zh-Auto 启动器，幂等注入带 150ms 探活守卫的托管块
        string zhScript = GetAntigravityZhLaunchScriptPath();
        bool injectedZh = false;
        if (!_isMacOs && File.Exists(zhScript))
        {
            Directory.CreateDirectory(_backupDir);
            string bakPath = Path.Combine(_backupDir, "agy-run-launch.ps1.bak");
            string raw = File.ReadAllText(zhScript);
            if (!File.Exists(bakPath))
            {
                File.WriteAllText(bakPath, StripManagedBlock(raw), new UTF8Encoding(true));
            }

            string block = BuildPowerShellProbeBlock(seat.LocalPort, proxy, socksProxy, noProxy);
            string updated = UpsertManagedBlockInPowerShell(raw, block);
            File.WriteAllText(zhScript, updated, new UTF8Encoding(true));
            injectedZh = true;
        }

        // 3. 若用户本来已有 VS Code 兼容的用户 settings.json（或 Antigravity CLI settings.json），一并做带备份无损同步
        string ideSettings = GetAntigravityIdeSettingsPath();
        if (File.Exists(ideSettings))
        {
            UpdateJsonObjectWithBackup("agy-ide", ideSettings, [], new Dictionary<string, JsonNode?>
            {
                ["http.proxy"] = JsonValue.Create(proxy),
                ["http.proxySupport"] = JsonValue.Create("override")
            });
        }

        return (true, injectedZh
            ? $"已接管 Antigravity 启动器（:{seat.LocalPort}，含未启动网关自动直连守卫）；下次打开 Antigravity 即生效。"
            : $"已生成 Antigravity 专属代理启动器 ({launcherPath})，后续通过该启动器或本控制台启动即走 :{seat.LocalPort}。");
    }

    private (bool Ok, string Message) RestoreAntigravityTakeover()
    {
        string zhScript = GetAntigravityZhLaunchScriptPath();
        if (!_isMacOs && File.Exists(zhScript))
        {
            string raw = File.ReadAllText(zhScript);
            if (raw.Contains(ManagedBlockStart, StringComparison.Ordinal))
            {
                string cleaned = StripManagedBlock(raw);
                File.WriteAllText(zhScript, cleaned, new UTF8Encoding(true));
            }
            string bakPath = Path.Combine(_backupDir, "agy-run-launch.ps1.bak");
            if (File.Exists(bakPath)) File.Delete(bakPath);
        }

        string launcherPath = GetAntigravityProxyLauncherPath();
        if (File.Exists(launcherPath)) File.Delete(launcherPath);

        _ = RestoreJsonObjectFromBackup("agy-ide", []);
        return (true, "已安全移除 Antigravity 托管代理块并还原原始启动配置");
    }

    private (bool Ok, string Message) EnableChatGptLauncherTakeover(AppSeat seat)
    {
        Directory.CreateDirectory(_launchersDir);
        string proxy = $"http://127.0.0.1:{seat.LocalPort}";
        string socksProxy = $"socks5h://127.0.0.1:{seat.LocalPort}";
        string noProxy = BuildEffectiveNoProxy(seat, _isMacOs);
        string path = GetCodexLauncherPath();

        if (_isMacOs)
        {
            string content =
                "#!/bin/bash\n" +
                $"export HTTP_PROXY=\"{proxy}\"\n" +
                $"export HTTPS_PROXY=\"{proxy}\"\n" +
                $"export ALL_PROXY=\"{socksProxy}\"\n" +
                $"export NO_PROXY=\"{noProxy}\"\n" +
                "echo \"[Agent Proxy Hub] Codex 独立专线已挂载 -> $HTTPS_PROXY\"\n" +
                "exec $SHELL -l\n";
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        else
        {
            string block = BuildPowerShellProbeBlock(seat.LocalPort, proxy, socksProxy, noProxy);
            string content =
                "# Agent Proxy Hub — Codex CLI / ChatGPT 独立代理终端启动脚本\r\n" +
                block + "\r\n" +
                $"Write-Host '[Agent Proxy Hub] Codex CLI 独立出口已准备 -> {proxy}' -ForegroundColor Cyan\r\n";
            File.WriteAllText(path, content, new UTF8Encoding(true));
        }

        return (true, $"已生成 Codex / ChatGPT 专属专线启动脚本（:{seat.LocalPort}）");
    }

    private (bool Ok, string Message) RestoreChatGptLauncherTakeover()
    {
        string path = GetCodexLauncherPath();
        if (File.Exists(path)) File.Delete(path);
        return (true, "已移除 Codex / ChatGPT 托管启动脚本");
    }

    private void UpdateJsonObjectWithBackup(
        string backupKey,
        string targetFilePath,
        string[] nestedObjectPath,
        Dictionary<string, JsonNode?> updates)
    {
        Directory.CreateDirectory(_backupDir);
        string? parentDir = Path.GetDirectoryName(targetFilePath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        bool fileExisted = File.Exists(targetFilePath);
        JsonObject rootObj;
        if (fileExisted)
        {
            string raw = File.ReadAllText(targetFilePath);
            var parsed = string.IsNullOrWhiteSpace(raw)
                ? new JsonObject()
                : JsonNode.Parse(raw, null, JsonDocOpts) as JsonObject;
            rootObj = parsed ?? throw new InvalidOperationException($"目标文件不是合法 JSON 对象: {targetFilePath}");
        }
        else
        {
            rootObj = new JsonObject();
        }

        JsonObject targetObj = rootObj;
        foreach (string seg in nestedObjectPath)
        {
            if (targetObj[seg] is JsonObject child)
            {
                targetObj = child;
            }
            else if (targetObj[seg] == null)
            {
                var created = new JsonObject();
                targetObj[seg] = created;
                targetObj = created;
            }
            else
            {
                throw new InvalidOperationException($"配置键 '{seg}' 不是对象，已中止写入以保护原文件");
            }
        }

        string backupFile = Path.Combine(_backupDir, $"{backupKey}.backup.json");
        if (!File.Exists(backupFile))
        {
            var bak = new JsonTakeoverBackup
            {
                TargetPath = targetFilePath,
                FileExistedBefore = fileExisted
            };
            foreach (string key in updates.Keys)
            {
                bool existed = targetObj.TryGetPropertyValue(key, out var existingNode);
                bak.Keys[key] = new BackupEntry
                {
                    Existed = existed,
                    RawJson = existed ? existingNode?.ToJsonString() : null
                };
            }
            File.WriteAllText(backupFile, JsonSerializer.Serialize(bak, WriteJsonOpts), new UTF8Encoding(false));
        }

        foreach (var kv in updates)
        {
            targetObj[kv.Key] = kv.Value?.DeepClone();
        }

        File.WriteAllText(targetFilePath, rootObj.ToJsonString(WriteJsonOpts), new UTF8Encoding(false));
    }

    private bool RestoreJsonObjectFromBackup(string backupKey, string[] nestedObjectPath)
    {
        string backupFile = Path.Combine(_backupDir, $"{backupKey}.backup.json");
        if (!File.Exists(backupFile)) return false;

        var bak = JsonSerializer.Deserialize<JsonTakeoverBackup>(File.ReadAllText(backupFile), WriteJsonOpts);
        if (bak == null || string.IsNullOrWhiteSpace(bak.TargetPath)) return false;

        if (File.Exists(bak.TargetPath))
        {
            var rootObj = JsonNode.Parse(File.ReadAllText(bak.TargetPath), null, JsonDocOpts) as JsonObject;
            if (rootObj != null)
            {
                JsonObject? targetObj = rootObj;
                foreach (string seg in nestedObjectPath)
                {
                    targetObj = targetObj?[seg] as JsonObject;
                }

                if (targetObj != null)
                {
                    foreach (var kv in bak.Keys)
                    {
                        if (!kv.Value.Existed)
                        {
                            targetObj.Remove(kv.Key);
                        }
                        else
                        {
                            targetObj[kv.Key] = kv.Value.RawJson == null
                                ? null
                                : JsonNode.Parse(kv.Value.RawJson, null, JsonDocOpts);
                        }
                    }

                    File.WriteAllText(bak.TargetPath, rootObj.ToJsonString(WriteJsonOpts), new UTF8Encoding(false));
                }
            }
        }

        File.Delete(backupFile);
        return true;
    }

    private static string BuildPowerShellProbeBlock(int port, string proxyHttp, string proxySocks, string noProxy)
    {
        return string.Join("\r\n",
        [
            ManagedBlockStart,
            "try {",
            "  $aphTcp = New-Object System.Net.Sockets.TcpClient",
            $"  $aphWait = $aphTcp.BeginConnect('127.0.0.1', {port}, $null, $null)",
            "  if ($aphWait.AsyncWaitHandle.WaitOne(150) -and $aphTcp.Connected) {",
            $"    $env:HTTP_PROXY = '{proxyHttp}'",
            $"    $env:HTTPS_PROXY = '{proxyHttp}'",
            $"    $env:ALL_PROXY = '{proxySocks}'",
            $"    $env:NO_PROXY = '{noProxy}'",
            "  }",
            "  $aphTcp.Close()",
            "} catch {}",
            ManagedBlockEnd
        ]);
    }

    private static string UpsertManagedBlockInPowerShell(string raw, string block)
    {
        string cleaned = StripManagedBlock(raw);
        var match = Regex.Match(cleaned, @"^(\$ErrorActionPreference[^\r\n]*\r?\n)", RegexOptions.Multiline);
        if (match.Success)
        {
            int insertPos = match.Index + match.Length;
            return cleaned[..insertPos] + block + "\r\n" + cleaned[insertPos..].TrimStart('\r', '\n');
        }
        return block + "\r\n" + cleaned.TrimStart('\r', '\n');
    }

    private static string StripManagedBlock(string raw)
    {
        string pattern = Regex.Escape(ManagedBlockStart) + @"[\s\S]*?" + Regex.Escape(ManagedBlockEnd) + @"(\r?\n)?";
        return Regex.Replace(raw, pattern, string.Empty, RegexOptions.Multiline);
    }

    private string WriteMacLauncherScript(AppSeat seat, string title, string cliHint, string? profileDir = null)
    {
        Directory.CreateDirectory(_launchersDir);
        string path = Path.Combine(_launchersDir, $"open-{seat.Id.Replace(":", "_")}.command");
        string proxyHttp = $"http://127.0.0.1:{seat.LocalPort}";
        string proxySocks = $"socks5h://127.0.0.1:{seat.LocalPort}";
        string profileExports = string.IsNullOrWhiteSpace(profileDir)
            ? string.Empty
            : $"export CLAUDE_CONFIG_DIR=\"{profileDir}\"\nexport CODEX_HOME=\"{profileDir}\"\nexport GEMINI_CLI_HOME=\"{profileDir}\"\nexport XDG_CONFIG_HOME=\"{profileDir}\"\n";
        string script =
            "#!/bin/bash\n" +
            $"export HTTP_PROXY=\"{proxyHttp}\"\n" +
            $"export HTTPS_PROXY=\"{proxyHttp}\"\n" +
            $"export ALL_PROXY=\"{proxySocks}\"\n" +
            "export NO_PROXY=\"" + BuildEffectiveNoProxy(seat, true) + "\"\n" +
            profileExports +
            $"echo \"[Agent Proxy Hub] {title} 独立专线已挂载 -> {proxyHttp}\"\n" +
            $"echo \"输入 {cliHint} 即可走专属出口通道。\"\n" +
            "exec $SHELL -l\n";
        File.WriteAllText(path, script, new UTF8Encoding(false));
        return path;
    }

    private static void AppendSplitArgs(ProcessStartInfo psi, string? extraArgs)
    {
        if (string.IsNullOrWhiteSpace(extraArgs)) return;
        foreach (string part in extraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            psi.ArgumentList.Add(part.Trim());
        }
    }

    public static string BuildEffectiveNoProxy(AppSeat seat, bool isMacOs)
    {
        var list = new List<string>(isMacOs
            ? ["localhost", "127.0.0.1", "::1", "192.168.0.0/16", "10.0.0.0/8"]
            : ["localhost", "127.0.0.1", "::1", "192.168.0.0/16", "10.0.0.0/8", "172.16.0.0/12"]);

        if (!string.IsNullOrWhiteSpace(seat.CustomNoProxy))
        {
            foreach (string raw in seat.CustomNoProxy.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                string item = raw.Trim();
                if (!string.IsNullOrEmpty(item) && !list.Contains(item, StringComparer.OrdinalIgnoreCase))
                    list.Add(item);
            }
        }
        return string.Join(",", list);
    }

    private static string BuildChromiumBypassList(string noProxy)
    {
        var items = new List<string> { "<-loopback>" };
        foreach (string part in noProxy.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string p = part.Trim();
            if (p == "::1") p = "[::1]";
            if (!string.IsNullOrEmpty(p) && !items.Contains(p, StringComparer.OrdinalIgnoreCase))
                items.Add(p);
        }
        return string.Join(";", items);
    }

    private static string? ResolveCustomOrDetectedExe(AppSeat seat, Func<string?> fallback)
    {
        if (!string.IsNullOrWhiteSpace(seat.CustomExecutablePath))
        {
            string trimmed = seat.CustomExecutablePath.Trim().Trim('"');
            if (File.Exists(trimmed) || Directory.Exists(trimmed))
                return trimmed;
        }
        return fallback();
    }

    private static string ResolveWindowsPowerShellExe()
    {
        string pf7 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (File.Exists(pf7)) return pf7;

        string storeAlias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "pwsh.exe");
        if (File.Exists(storeAlias)) return storeAlias;

        return "powershell.exe";
    }

    private static void StartWindowsInteractiveShell(string initCmd)
    {
        string psExe = ResolveWindowsPowerShellExe();
        string wtAlias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");

        if (File.Exists(wtAlias))
        {
            try
            {
                var wtPsi = new ProcessStartInfo(wtAlias) { UseShellExecute = true };
                wtPsi.ArgumentList.Add("-w");
                wtPsi.ArgumentList.Add("new");
                wtPsi.ArgumentList.Add(psExe);
                wtPsi.ArgumentList.Add("-NoExit");
                wtPsi.ArgumentList.Add("-NoProfile");
                wtPsi.ArgumentList.Add("-Command");
                wtPsi.ArgumentList.Add(initCmd);
                Process.Start(wtPsi);
                return;
            }
            catch
            {
                // 回退直接拉起 PowerShell
            }
        }

        var termPsi = new ProcessStartInfo(psExe) { UseShellExecute = true };
        termPsi.ArgumentList.Add("-NoExit");
        termPsi.ArgumentList.Add("-NoProfile");
        termPsi.ArgumentList.Add("-Command");
        termPsi.ArgumentList.Add(initCmd);
        Process.Start(termPsi);
    }

    private static void ApplyProxyEnv(ProcessStartInfo psi, string httpProxy, string socksProxy, string noProxy)
    {
        psi.Environment["HTTP_PROXY"] = httpProxy;
        psi.Environment["HTTPS_PROXY"] = httpProxy;
        psi.Environment["ALL_PROXY"] = socksProxy;
        psi.Environment["NO_PROXY"] = noProxy;
    }

    private string GetClaudeSettingsPath() => Path.Combine(_userHome, ".claude", "settings.json");

    private string GetVsCodeIdeSettingsPath(VsCodeCompatibleIdeSpec spec) => _isMacOs
        ? Path.Combine(_userHome, "Library", "Application Support", spec.RoamingFolderName, "User", "settings.json")
        : Path.Combine(_appDataRoaming, spec.RoamingFolderName, "User", "settings.json");

    private string GetZedSettingsPath()
    {
        if (!_isMacOs)
        {
            string winRoaming = Path.Combine(_appDataRoaming, "Zed", "settings.json");
            if (File.Exists(winRoaming) || Directory.Exists(Path.Combine(_appDataRoaming, "Zed")))
                return winRoaming;
        }
        return Path.Combine(_userHome, ".config", "zed", "settings.json");
    }

    private string GetAntigravityIdeSettingsPath() => _isMacOs
        ? Path.Combine(_userHome, "Library", "Application Support", "Antigravity", "User", "settings.json")
        : Path.Combine(_appDataRoaming, "Antigravity", "User", "settings.json");

    private string GetAntigravityZhLaunchScriptPath() =>
        Path.Combine(_appDataLocal, "Antigravity-Zh-Auto", "run-launch.ps1");

    private string GetAntigravityProxyLauncherPath() => Path.Combine(
        _launchersDir,
        _isMacOs ? "Start-Antigravity-Proxy.command" : "Start-Antigravity-Proxy.cmd");

    private string GetCodexLauncherPath() => Path.Combine(
        _launchersDir,
        _isMacOs ? "Start-Codex-Proxy.command" : "Start-Codex-Proxy.ps1");

    private string? ResolveAntigravityExe()
    {
        string[] candidates =
        [
            Path.Combine(_appDataLocal, "Programs", "antigravity", "Antigravity.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Antigravity", "Antigravity.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private string? ResolveVsCodeIdeExe(VsCodeCompatibleIdeSpec spec)
    {
        string[] candidates =
        [
            Path.Combine(_appDataLocal, "Programs", spec.WinInstallSubdir, spec.WinExeName),
            Path.Combine(_appDataLocal, "Programs", spec.RoamingFolderName, spec.WinExeName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), spec.WinInstallSubdir, spec.WinExeName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), spec.RoamingFolderName, spec.WinExeName)
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private string? ResolveZedExe()
    {
        string[] candidates =
        [
            Path.Combine(_appDataLocal, "Programs", "Zed", "Zed.exe"),
            Path.Combine(_appDataLocal, "Zed", "bin", "zed.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zed", "Zed.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool DefaultCheckProcessRunning(string[] processNames)
    {
        if (processNames.Length == 0) return false;
        try
        {
            HashSet<string> names;
            lock (ProcSnapshotLock)
            {
                DateTime now = DateTime.UtcNow;
                if (now > _procSnapshotExpiresUtc)
                {
                    var fresh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in Process.GetProcesses())
                    {
                        using (p)
                        {
                            try
                            {
                                string pn = p.ProcessName;
                                if (!string.IsNullOrEmpty(pn)) fresh.Add(pn);
                            }
                            catch { }
                        }
                    }
                    _procSnapshotNames = fresh;
                    _procSnapshotExpiresUtc = now.AddSeconds(3);
                }
                names = _procSnapshotNames;
            }

            foreach (string name in processNames)
            {
                if (names.Contains(name))
                    return true;
            }
        }
        catch { }
        return false;
    }
}
