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
    private readonly CredentialVault _vault;
    private readonly object _ioLock = new();

    private static readonly string[] SensitiveExtraKeys = ["obfs-password", "private_key", "preshared_key", "password"];

    public HubStateStore(string? customDirectory = null)
    {
        string dir = string.IsNullOrWhiteSpace(customDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agent-proxy-hub")
            : customDirectory;
        Directory.CreateDirectory(dir);
        _statePath = Path.Combine(dir, "state.json");
        _vault = new CredentialVault(dir);
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
                    if (doc != null)
                    {
                        bool needsMigration = DetectUnencryptedSecrets(doc);
                        DecryptSecretsInPlace(doc);
                        doc = EnsureDefaults(doc);
                        if (needsMigration)
                        {
                            Save(doc);
                        }
                        return doc;
                    }
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
            var protectedClone = CreateEncryptedClone(doc);
            string json = JsonSerializer.Serialize(protectedClone, JsonOpts);
            string tmpPath = _statePath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, _statePath, overwrite: true);
        }
    }

    private static bool DetectUnencryptedSecrets(HubStateDocument doc)
    {
        foreach (var node in doc.Nodes)
        {
            if (!string.IsNullOrEmpty(node.Password) && !CredentialVault.IsEncrypted(node.Password))
                return true;
            if (node.Extra != null)
            {
                foreach (string k in SensitiveExtraKeys)
                {
                    if (node.Extra.TryGetValue(k, out string? v) && !string.IsNullOrEmpty(v) && !CredentialVault.IsEncrypted(v))
                        return true;
                }
            }
        }
        foreach (var sub in doc.Subscriptions)
        {
            if (!string.IsNullOrEmpty(sub.Url) && !CredentialVault.IsEncrypted(sub.Url))
                return true;
            if (!string.IsNullOrEmpty(sub.ProxyUrl) && !CredentialVault.IsEncrypted(sub.ProxyUrl))
                return true;
        }
        return false;
    }

    private void DecryptSecretsInPlace(HubStateDocument doc)
    {
        doc.Subscriptions ??= new();
        foreach (var node in doc.Nodes)
        {
            if (!string.IsNullOrEmpty(node.Password))
            {
                node.Password = _vault.Unprotect(node.Password);
                node.HasCredential = !string.IsNullOrEmpty(node.Password);
            }
            if (node.Extra != null)
            {
                foreach (string k in SensitiveExtraKeys)
                {
                    if (node.Extra.TryGetValue(k, out string? v) && !string.IsNullOrEmpty(v))
                    {
                        node.Extra[k] = _vault.Unprotect(v);
                        if (!string.IsNullOrEmpty(node.Extra[k])) node.HasCredential = true;
                    }
                }
            }
        }
        foreach (var sub in doc.Subscriptions)
        {
            sub.Url = _vault.Unprotect(sub.Url);
            if (!string.IsNullOrEmpty(sub.ProxyUrl))
                sub.ProxyUrl = _vault.Unprotect(sub.ProxyUrl);
        }
    }

    private HubStateDocument CreateEncryptedClone(HubStateDocument doc)
    {
        return new HubStateDocument
        {
            Isolation = doc.Isolation,
            HostApiPort = doc.HostApiPort,
            CustomPricings = doc.CustomPricings,
            Seats = doc.Seats,
            Subscriptions = (doc.Subscriptions ?? []).Select(s => new SubscriptionSource
            {
                Id = s.Id,
                Name = s.Name,
                Group = s.Group,
                Url = _vault.Protect(s.Url) ?? string.Empty,
                ProxyUrl = string.IsNullOrEmpty(s.ProxyUrl) ? s.ProxyUrl : _vault.Protect(s.ProxyUrl),
                AutoRefreshHours = s.AutoRefreshHours,
                LastSyncedAt = s.LastSyncedAt,
                NodeCount = s.NodeCount,
                DriftCount = s.DriftCount,
                LastError = s.LastError
            }).ToList(),
            Nodes = doc.Nodes.Select(n =>
            {
                Dictionary<string, string>? clonedExtra = null;
                if (n.Extra != null)
                {
                    clonedExtra = new Dictionary<string, string>(n.Extra, StringComparer.OrdinalIgnoreCase);
                    foreach (string k in SensitiveExtraKeys)
                    {
                        if (clonedExtra.TryGetValue(k, out string? v) && !string.IsNullOrEmpty(v))
                            clonedExtra[k] = _vault.Protect(v) ?? v;
                    }
                }
                return new ProxyNode
                {
                    Id = n.Id,
                    Name = n.Name,
                    Group = n.Group,
                    Protocol = n.Protocol,
                    Host = n.Host,
                    Port = n.Port,
                    Username = n.Username,
                    Password = string.IsNullOrEmpty(n.Password) ? n.Password : _vault.Protect(n.Password),
                    HasCredential = !string.IsNullOrEmpty(n.Password) || n.HasCredential,
                    Method = n.Method,
                    Sni = n.Sni,
                    AllowInsecure = n.AllowInsecure,
                    InsecureApproved = n.InsecureApproved,
                    SourceSubscriptionId = n.SourceSubscriptionId,
                    Extra = clonedExtra,
                    Enabled = n.Enabled,
                    Status = n.Status,
                    LatencyMs = n.LatencyMs,
                    ExitIp = n.ExitIp,
                    IspSummary = n.IspSummary,
                    AiReachability = n.AiReachability,
                    LastCheckedAt = n.LastCheckedAt
                };
            }).ToList()
        };
    }

    private static HubStateDocument EnsureDefaults(HubStateDocument doc)
    {
        var defaults = CreateDefaultSeats();
        if (doc.Seats.Count == 0)
        {
            doc.Seats = defaults;
            return doc;
        }

        // 无损增量补齐新增支持的内置 AI 应用席位（保留用户所有已有席位配置不动）
        var existingIds = doc.Seats.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedPorts = doc.Seats.Select(s => s.LocalPort).ToHashSet();
        string defaultNodeId = doc.Nodes.FirstOrDefault()?.Id ?? "direct-phy";

        foreach (var def in defaults)
        {
            if (existingIds.Contains(def.Id)) continue;
            int port = def.LocalPort;
            while (usedPorts.Contains(port)) port++;
            def.LocalPort = port;
            def.BoundNodeId = defaultNodeId;
            def.Enabled = false;
            usedPorts.Add(port);
            doc.Seats.Add(def);
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
            GlobalKillSwitch = true,
            EnableIpv6 = false
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
        },
        new AppSeat
        {
            Id = "claude-desktop",
            Glyph = "CD",
            Name = "Claude Desktop",
            Subtitle = "Anthropic Claude 桌面客户端（路由）",
            ProcessWin = "Claude.exe",
            ProcessMac = "Claude.app",
            LocalPort = 17895,
            BoundNodeId = "direct-phy",
            Enabled = false,
            KillSwitchEnabled = true,
            CaptureMode = "独立端口 :17895 + 桌面路由",
            LaunchCommandWin = "Claude --proxy-server=http://127.0.0.1:17895",
            LaunchCommandMac = "open -a Claude --args --proxy-server=http://127.0.0.1:17895"
        },
        new AppSeat
        {
            Id = "gemini-cli",
            Glyph = "GM",
            Name = "Gemini CLI",
            Subtitle = "Google Gemini 终端编码代理",
            ProcessWin = "gemini.exe / node.exe",
            ProcessMac = "gemini",
            LocalPort = 17896,
            BoundNodeId = "direct-phy",
            Enabled = false,
            KillSwitchEnabled = true,
            CaptureMode = "独立端口 :17896 + CLI 注入",
            LaunchCommandWin = "gemini",
            LaunchCommandMac = "gemini"
        },
        new AppSeat
        {
            Id = "grok-build",
            Glyph = "GK",
            Name = "Grok Build",
            Subtitle = "xAI Grok Build 编码代理",
            ProcessWin = "grok.exe / node.exe",
            ProcessMac = "grok",
            LocalPort = 17897,
            BoundNodeId = "direct-phy",
            Enabled = false,
            KillSwitchEnabled = true,
            CaptureMode = "独立端口 :17897 + CLI 注入",
            LaunchCommandWin = "grok",
            LaunchCommandMac = "grok"
        },
        new AppSeat
        {
            Id = "opencode",
            Glyph = "OC",
            Name = "OpenCode",
            Subtitle = "OpenCode 开源终端编码助手",
            ProcessWin = "opencode.exe",
            ProcessMac = "opencode",
            LocalPort = 17898,
            BoundNodeId = "direct-phy",
            Enabled = false,
            KillSwitchEnabled = true,
            CaptureMode = "独立端口 :17898 + CLI 注入",
            LaunchCommandWin = "opencode",
            LaunchCommandMac = "opencode"
        },
        new AppSeat
        {
            Id = "openclaw",
            Glyph = "CW",
            Name = "OpenClaw",
            Subtitle = "OpenClaw 自主智能体网关",
            ProcessWin = "openclaw.exe / node.exe",
            ProcessMac = "openclaw",
            LocalPort = 17899,
            BoundNodeId = "direct-phy",
            Enabled = false,
            KillSwitchEnabled = true,
            CaptureMode = "独立端口 :17899 + CLI 注入",
            LaunchCommandWin = "openclaw",
            LaunchCommandMac = "openclaw"
        },
        new AppSeat
        {
            Id = "hermes",
            Glyph = "HM",
            Name = "Hermes",
            Subtitle = "Hermes AI 代理与推理终端",
            ProcessWin = "hermes.exe",
            ProcessMac = "hermes",
            LocalPort = 17900,
            BoundNodeId = "direct-phy",
            Enabled = false,
            KillSwitchEnabled = true,
            CaptureMode = "独立端口 :17900 + CLI 注入",
            LaunchCommandWin = "hermes",
            LaunchCommandMac = "hermes"
        },
        new AppSeat
        {
            Id = "pi",
            Glyph = "PI",
            Name = "Pi",
            Subtitle = "Pi 轻量化终端编码代理",
            ProcessWin = "pi.exe / node.exe",
            ProcessMac = "pi",
            LocalPort = 17901,
            BoundNodeId = "direct-phy",
            Enabled = false,
            KillSwitchEnabled = true,
            CaptureMode = "独立端口 :17901 + CLI 注入",
            LaunchCommandWin = "pi",
            LaunchCommandMac = "pi"
        },
        new AppSeat
        {
            Id = "minimax-code",
            Glyph = "MM",
            Name = "MiniMax Code",
            Subtitle = "MiniMax 代码助手 CLI",
            ProcessWin = "minimax.exe / node.exe",
            ProcessMac = "minimax",
            LocalPort = 17902,
            BoundNodeId = "direct-phy",
            Enabled = false,
            KillSwitchEnabled = true,
            CaptureMode = "独立端口 :17902 + CLI 注入",
            LaunchCommandWin = "minimax",
            LaunchCommandMac = "minimax"
        },
        new AppSeat
        {
            Id = "kimi-code",
            Glyph = "KM",
            Name = "Kimi Code",
            Subtitle = "Moonshot Kimi Code CLI",
            ProcessWin = "kimi.exe / node.exe",
            ProcessMac = "kimi",
            LocalPort = 17903,
            BoundNodeId = "direct-phy",
            Enabled = false,
            KillSwitchEnabled = true,
            CaptureMode = "独立端口 :17903 + CLI 注入",
            LaunchCommandWin = "kimi",
            LaunchCommandMac = "kimi"
        },
        new AppSeat
        {
            Id = "deepseek-harness",
            Glyph = "DS",
            Name = "DeepSeek Harness",
            Subtitle = "DeepSeek Harness Agent CLI",
            ProcessWin = "deepseek.exe / node.exe",
            ProcessMac = "deepseek",
            LocalPort = 17904,
            BoundNodeId = "direct-phy",
            Enabled = false,
            KillSwitchEnabled = true,
            CaptureMode = "独立端口 :17904 + CLI 注入",
            LaunchCommandWin = "deepseek",
            LaunchCommandMac = "deepseek"
        },
        new AppSeat
        {
            Id = "oh-my-pi",
            Glyph = "OP",
            Name = "Oh My Pi",
            Subtitle = "Oh My Pi 增强代理框架",
            ProcessWin = "omp.exe / node.exe",
            ProcessMac = "omp",
            LocalPort = 17905,
            BoundNodeId = "direct-phy",
            Enabled = false,
            KillSwitchEnabled = true,
            CaptureMode = "独立端口 :17905 + CLI 注入",
            LaunchCommandWin = "omp",
            LaunchCommandMac = "omp"
        }
    ];
}
