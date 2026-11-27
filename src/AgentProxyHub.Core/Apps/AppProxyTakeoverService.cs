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
    /// 只读检查全部席位的应用运行状态、配置文件接管状态与可用启动器路径
    /// </summary>
    public IReadOnlyList<SeatTakeoverStatus> InspectAll(IReadOnlyList<AppSeat> seats)
    {
        lock (_opLock)
        {
            return seats.Select(InspectSeatInternal).ToList();
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
                    "cursor" => enable ? EnableCursorTakeover(seat) : RestoreCursorTakeover(),
                    "agy" or "antigravity" => enable ? EnableAntigravityTakeover(seat) : RestoreAntigravityTakeover(),
                    "chatgpt" => enable ? EnableChatGptLauncherTakeover(seat) : RestoreChatGptLauncherTakeover(),
                    _ => enable ? EnableGenericLauncherTakeover(seat) : RestoreGenericLauncherTakeover(seat)
                };

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
    /// 以独立进程环境变量注入方式受控启动目标 AI 应用（绝不杀掉或重启正在运行的 Antigravity 会话）
    /// </summary>
    public (bool Ok, string Message) LaunchWithProxy(AppSeat seat)
    {
        lock (_opLock)
        {
            var status = InspectSeatInternal(seat);
            string proxyHttp = $"http://127.0.0.1:{seat.LocalPort}";
            string proxySocks = $"socks5h://127.0.0.1:{seat.LocalPort}";
            string noProxy = BuildEffectiveNoProxy(seat, _isMacOs);

            if (seat.Id is "agy" or "antigravity")
            {
                if (status.IsProcessRunning)
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
                    psi.ArgumentList.Add("-a");
                    psi.ArgumentList.Add(macTarget);
                    psi.ArgumentList.Add("--args");
                    psi.ArgumentList.Add($"--proxy-server={proxyHttp}");
                    psi.ArgumentList.Add($"--proxy-bypass-list={BuildChromiumBypassList(noProxy)}");
                    ApplyProxyEnv(psi, proxyHttp, proxySocks, noProxy);
                    Process.Start(psi);
                    return (true, $"已携带 :{seat.LocalPort} 代理参数启动 Antigravity");
                }

                string zhScript = GetAntigravityZhLaunchScriptPath();
                if (string.IsNullOrWhiteSpace(seat.CustomExecutablePath) && File.Exists(zhScript))
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
                        UseShellExecute = false
                    };
                    psi.ArgumentList.Add($"--proxy-server={proxyHttp}");
                    psi.ArgumentList.Add($"--proxy-bypass-list={BuildChromiumBypassList(noProxy)}");
                    ApplyProxyEnv(psi, proxyHttp, proxySocks, noProxy);
                    Process.Start(psi);
                    return (true, $"已携带 :{seat.LocalPort} 代理参数启动 Antigravity");
                }

                return (false, "未找到本机 Antigravity 可执行文件，可使用专属启动脚本或在「应用接管 & CLI」指定自定义路径。");
            }

            if (seat.Id == "cursor")
            {
                if (_isMacOs)
                {
                    string macTarget = ResolveCustomOrDetectedExe(seat, () => "Cursor") ?? "Cursor";
                    var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                    psi.ArgumentList.Add("-a");
                    psi.ArgumentList.Add(macTarget);
                    psi.ArgumentList.Add("--args");
                    psi.ArgumentList.Add($"--proxy-server={proxyHttp}");
                    psi.ArgumentList.Add($"--proxy-bypass-list={BuildChromiumBypassList(noProxy)}");
                    ApplyProxyEnv(psi, proxyHttp, proxySocks, noProxy);
                    Process.Start(psi);
                    return (true, $"已携带 :{seat.LocalPort} 代理参数启动 Cursor");
                }

                string? cursorExe = ResolveCustomOrDetectedExe(seat, ResolveCursorExe);
                if (!string.IsNullOrEmpty(cursorExe))
                {
                    var psi = new ProcessStartInfo(cursorExe) { UseShellExecute = false };
                    psi.ArgumentList.Add($"--proxy-server={proxyHttp}");
                    psi.ArgumentList.Add($"--proxy-bypass-list={BuildChromiumBypassList(noProxy)}");
                    ApplyProxyEnv(psi, proxyHttp, proxySocks, noProxy);
                    Process.Start(psi);
                    return (true, $"已携带 :{seat.LocalPort} 代理参数启动 Cursor");
                }

                return (false, "未在常见路径找到 Cursor.exe；可在「应用接管 & CLI」指定自定义可执行路径，或使用 settings.json 热接管。");
            }

            if (seat.Id == "claude-desktop")
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
                    ApplyProxyEnv(psi, proxyHttp, proxySocks, noProxy);
                    Process.Start(psi);
                    return (true, $"已携带 :{seat.LocalPort} 代理参数启动 Claude Desktop");
                }
            }

            // Claude Code / Codex / Gemini CLI / Grok Build / OpenCode 等：打开已预注入该席位代理环境变量的独立终端窗口
            string bannerTitle = seat.Name;
            string cliHint = !string.IsNullOrWhiteSpace(seat.CustomExecutablePath)
                ? seat.CustomExecutablePath.Trim()
                : ResolveCliHint(seat);

            if (_isMacOs)
            {
                string scriptPath = WriteMacLauncherScript(seat, bannerTitle, cliHint);
                var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                psi.ArgumentList.Add("-a");
                psi.ArgumentList.Add("Terminal");
                psi.ArgumentList.Add(scriptPath);
                Process.Start(psi);
                return (true, $"已打开预注入 :{seat.LocalPort} 独立专线的 Terminal 终端");
            }

            string initCmd =
                $"$env:HTTP_PROXY='{proxyHttp}'; $env:HTTPS_PROXY='{proxyHttp}'; $env:ALL_PROXY='{proxySocks}'; $env:NO_PROXY='{noProxy}'; " +
                $"Write-Host '[Agent Proxy Hub] {bannerTitle} 独立专线已挂载 -> {proxyHttp}' -ForegroundColor Cyan; " +
                $"Write-Host '直接输入 {cliHint} 即可从专属出口通道通信，不影响系统或 Clash。' -ForegroundColor DarkGray";

            StartWindowsInteractiveShell(initCmd);
            return (true, $"已打开已注入 :{seat.LocalPort} 独立专线的交互式终端");
        }
    }

    private SeatTakeoverStatus InspectSeatInternal(AppSeat seat)
    {
        string expectedProxy = $"http://127.0.0.1:{seat.LocalPort}";

        return seat.Id switch
        {
            "claude" => InspectClaude(seat, expectedProxy),
            "cursor" => InspectCursor(seat, expectedProxy),
            "agy" or "antigravity" => InspectAntigravity(seat, expectedProxy),
            "chatgpt" => InspectChatGpt(seat, expectedProxy),
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
        _ => string.IsNullOrWhiteSpace(seat.LaunchCommandWin) ? seat.Id : seat.LaunchCommandWin.Split(' ')[0]
    };

    private static string[] ResolveProcessNames(AppSeat seat) => seat.Id switch
    {
        "claude-desktop" => ["Claude", "claude"],
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

    private SeatTakeoverStatus InspectCursor(AppSeat seat, string expectedProxy)
    {
        string path = GetCursorSettingsPath();
        bool running = _processChecker(["Cursor"]);
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

        string? exe = _isMacOs ? "/Applications/Cursor.app" : ResolveCursorExe();

        return new SeatTakeoverStatus
        {
            SeatId = seat.Id,
            IsProcessRunning = running,
            SupportsConfigTakeover = true,
            IsTakenOver = takenOver,
            ModeSummary = "Cursor User/settings.json 热接管",
            TargetPath = path,
            LaunchExecutable = exe,
            StatusHint = takenOver
                ? $"已接入 Cursor 配置 (http.proxy = {expectedProxy})，修改配置即刻热生效。"
                : (running
                    ? $"检测到 Cursor 正在运行但未指向 :{seat.LocalPort}，点击「一键接管」立即写入 http.proxy 热生效。"
                    : $"未接管配置：点击「一键接管」即可把 Cursor 流量定向到 :{seat.LocalPort}。")
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

    private (bool Ok, string Message) EnableCursorTakeover(AppSeat seat)
    {
        string path = GetCursorSettingsPath();
        string proxy = $"http://127.0.0.1:{seat.LocalPort}";
        var updates = new Dictionary<string, JsonNode?>
        {
            ["http.proxy"] = JsonValue.Create(proxy),
            ["http.proxySupport"] = JsonValue.Create("override")
        };

        UpdateJsonObjectWithBackup("cursor", path, [], updates);
        return (true, $"已无损写入 Cursor settings.json（http.proxy -> {proxy}）");
    }

    private (bool Ok, string Message) RestoreCursorTakeover()
    {
        bool restored = RestoreJsonObjectFromBackup("cursor", []);
        return restored
            ? (true, "已按备份无损还原 Cursor 原始代理设置")
            : (true, "未检测到 Cursor 历史接管备份，原配置未改动");
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

    private string WriteMacLauncherScript(AppSeat seat, string title, string cliHint)
    {
        Directory.CreateDirectory(_launchersDir);
        string path = Path.Combine(_launchersDir, $"open-{seat.Id}.command");
        string proxyHttp = $"http://127.0.0.1:{seat.LocalPort}";
        string proxySocks = $"socks5h://127.0.0.1:{seat.LocalPort}";
        string script =
            "#!/bin/bash\n" +
            $"export HTTP_PROXY=\"{proxyHttp}\"\n" +
            $"export HTTPS_PROXY=\"{proxyHttp}\"\n" +
            $"export ALL_PROXY=\"{proxySocks}\"\n" +
            "export NO_PROXY=\"" + BuildEffectiveNoProxy(seat, true) + "\"\n" +
            $"echo \"[Agent Proxy Hub] {title} 独立专线已挂载 -> {proxyHttp}\"\n" +
            $"echo \"输入 {cliHint} 即可走专属出口通道。\"\n" +
            "exec $SHELL -l\n";
        File.WriteAllText(path, script, new UTF8Encoding(false));
        return path;
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

    private string GetCursorSettingsPath() => _isMacOs
        ? Path.Combine(_userHome, "Library", "Application Support", "Cursor", "User", "settings.json")
        : Path.Combine(_appDataRoaming, "Cursor", "User", "settings.json");

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

    private string? ResolveCursorExe()
    {
        string[] candidates =
        [
            Path.Combine(_appDataLocal, "Programs", "cursor", "Cursor.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Cursor", "Cursor.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool DefaultCheckProcessRunning(string[] processNames)
    {
        try
        {
            foreach (string name in processNames)
            {
                if (Process.GetProcessesByName(name).Length > 0)
                    return true;
            }
        }
        catch { }
        return false;
    }
}
