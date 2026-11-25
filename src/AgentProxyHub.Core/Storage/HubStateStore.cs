using System.Text.Json;
using AgentProxyHub.Core.Models;

namespace AgentProxyHub.Core.Storage;

/// <summary>
/// 跨平台配置持久化仓库（存储于 ~/.agent-proxy-hub/state.json）
/// 首次启动时注入清晰预设 AI 席位模板，不含虚构的假节点出口数据
/// </summary>
public sealed class HubStateStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _statePath;
    private readonly object _ioLock = new();

    public HubStateStore(string? customDirectory = null)
    {
        string dir = string.IsNullOrWhiteSpace(customDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agent-proxy-hub")
            : customDirectory;
        Directory.CreateDirectory(dir);
        _statePath = Path.Combine(dir, "state.json");
    }

    public HubStateDocument LoadOrCreateDefault()
    {
        lock (_ioLock)
        {
            if (File.Exists(_statePath))
            {
                try
                {
                    string json = File.ReadAllText(_statePath);
                    var doc = JsonSerializer.Deserialize<HubStateDocument>(json, JsonOpts);
                    if (doc != null) return EnsureDefaults(doc);
                }
                catch
                {
                    // 配置文件损坏时回退默认结构
                }
            }

            var created = CreateDefaultDocument();
            Save(created);
            return created;
        }
    }

    public void Save(HubStateDocument doc)
    {
        lock (_ioLock)
        {
            string json = JsonSerializer.Serialize(doc, JsonOpts);
            File.WriteAllText(_statePath, json);
        }
    }

    private static HubStateDocument EnsureDefaults(HubStateDocument doc)
    {
        if (doc.Seats.Count == 0)
        {
            doc.Seats = CreateDefaultSeats();
        }
        return doc;
    }

    private static HubStateDocument CreateDefaultDocument() => new()
    {
        Isolation = new IsolationSettings
        {
            DefaultMixedPort = 17890,
            AutoBypassVirtualNics = true,
            TlsSniRecoveryEnabled = true,
            ForceRemoteDns = true,
            DirectPhysicalDohEnabled = true,
            DohServerIp = "223.5.5.5",
            GlobalKillSwitch = true
        },
        Nodes =
        [
            new ProxyNode
            {
                Id = "direct-phy",
                Name = "物理网卡直出通道（无上游代理示例）",
                Group = "本地直出",
                Protocol = ProxyProtocol.DIRECT_PHYSICAL,
                Host = "physical-nic",
                Port = 0,
                Enabled = true,
                Status = "online",
                ExitIp = "物理网卡直出",
                IspSummary = "硬件网卡直绑示例 · 建议导入您的 SOCKS5/HTTP 专线替换",
                AiReachability = ["直连通道"]
            }
        ],
        Seats = CreateDefaultSeats()
    };

    private static List<AppSeat> CreateDefaultSeats() =>
    [
        new AppSeat
        {
            Id = "claude",
            Glyph = "CL",
            Name = "Claude",
            Subtitle = "Claude Desktop + Claude Code CLI",
            ProcessWin = "claude.exe / node.exe",
            ProcessMac = "Claude.app",
            LocalPort = 17891,
            BoundNodeId = "direct-phy",
            Enabled = true,
            KillSwitchEnabled = true,
            CaptureMode = "独立端口 :17891 + CLI 注入",
            LaunchCommandWin = "claude",
            LaunchCommandMac = "open -a Claude"
        },
        new AppSeat
        {
            Id = "chatgpt",
            Glyph = "GP",
            Name = "ChatGPT",
            Subtitle = "ChatGPT Desktop + Codex CLI",
            ProcessWin = "ChatGPT.exe / codex.exe",
            ProcessMac = "ChatGPT.app",
            LocalPort = 17892,
            BoundNodeId = "direct-phy",
            Enabled = true,
            KillSwitchEnabled = true,
            CaptureMode = "独立端口 :17892 + CLI 注入",
            LaunchCommandWin = "codex",
            LaunchCommandMac = "open -a ChatGPT"
        },
        new AppSeat
        {
            Id = "agy",
            Glyph = "AG",
            Name = "Antigravity",
            Subtitle = "Antigravity IDE + Agent 内核",
            ProcessWin = "Antigravity.exe",
            ProcessMac = "Antigravity.app",
            LocalPort = 17893,
            BoundNodeId = "direct-phy",
            Enabled = true,
            KillSwitchEnabled = true,
            CaptureMode = "专属混合端口 :17893",
            LaunchCommandWin = "Antigravity --proxy-server=socks5://127.0.0.1:17893",
            LaunchCommandMac = "open -a Antigravity --args --proxy-server=socks5://127.0.0.1:17893"
        },
        new AppSeat
        {
            Id = "cursor",
            Glyph = "CR",
            Name = "Cursor",
            Subtitle = "AI 代码编辑器 (按需启用)",
            ProcessWin = "Cursor.exe",
            ProcessMac = "Cursor.app",
            LocalPort = 17894,
            BoundNodeId = "direct-phy",
            Enabled = false,
            KillSwitchEnabled = true,
            CaptureMode = "专属混合端口 :17894",
            LaunchCommandWin = "Cursor --proxy-server=socks5://127.0.0.1:17894",
            LaunchCommandMac = "open -a Cursor --args --proxy-server=socks5://127.0.0.1:17894"
        }
    ];
}
