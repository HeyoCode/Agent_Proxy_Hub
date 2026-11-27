using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AgentProxyHub.Core.Models;

/// <summary>
/// 支持的上游代理协议类型
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProxyProtocol
{
    SOCKS5,
    SOCKS4,
    HTTP,
    HTTPS,
    DIRECT_PHYSICAL,
    TROJAN,
    SHADOWSOCKS,
    VLESS,
    HYSTERIA2,
    TUIC,
    WIREGUARD
}

/// <summary>
/// 独立代理线路节点配置与实时状态
/// </summary>
public sealed class ProxyNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = string.Empty;
    public string Group { get; set; } = "默认分组";
    public ProxyProtocol Protocol { get; set; } = ProxyProtocol.SOCKS5;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool HasCredential { get; set; }
    public string? Method { get; set; }
    public string? Sni { get; set; }
    public bool AllowInsecure { get; set; }
    /// <summary>
    /// 声明跳过证书校验的节点是否已获得用户明确风险批准
    /// </summary>
    public bool InsecureApproved { get; set; } = true;
    /// <summary>
    /// 若节点来自订阅源，记录所属订阅源 ID，用于刷新与防静默漂移检测
    /// </summary>
    public string? SourceSubscriptionId { get; set; }
    public Dictionary<string, string>? Extra { get; set; }
    public bool Enabled { get; set; } = true;

    // 验真与运行状态
    public string Status { get; set; } = "unverified"; // online | offline | unverified
    public int? LatencyMs { get; set; }
    public string ExitIp { get; set; } = "—";
    public string IspSummary { get; set; } = "待验真";
    public List<string> AiReachability { get; set; } = new() { "待验真" };
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>
    /// 是否需要 L2 sing-box 懒加载扩展引擎承载
    /// </summary>
    public bool RequiresSidecarEngine => Protocol is
        ProxyProtocol.VLESS or
        ProxyProtocol.HYSTERIA2 or
        ProxyProtocol.TUIC or
        ProxyProtocol.WIREGUARD;

    public string EngineKind => RequiresSidecarEngine ? "sing-box" : "native";

    /// <summary>
    /// 用于去重的唯一指纹（协议 + 主机 + 端口 + 用户名/方法）
    /// </summary>
    public string Fingerprint =>
        $"{Protocol}|{Host.Trim().ToLowerInvariant()}|{Port}|{Username?.Trim() ?? string.Empty}|{Method?.Trim().ToLowerInvariant() ?? string.Empty}";

    /// <summary>
    /// 节点核心配置不可变指纹摘要（含落地主机、端口、凭据与 TLS 参数，用于检测订阅刷新是否悄然更换了出口）
    /// </summary>
    [JsonIgnore]
    public string ConfigSnapshotHash
    {
        get
        {
            string raw = $"{Protocol}|{Host.Trim().ToLowerInvariant()}|{Port}|{Username ?? ""}|{Password ?? ""}|{Method ?? ""}|{Sni ?? ""}|{AllowInsecure}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
        }
    }

    /// <summary>
    /// 生成面向前端与导出接口的脱敏副本（隐藏密码与敏感扩展字段）
    /// </summary>
    public ProxyNode ToRedactedCopy()
    {
        Dictionary<string, string>? redactedExtra = null;
        bool extraHasSecret = false;
        if (Extra != null)
        {
            redactedExtra = new Dictionary<string, string>(Extra, StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[] { "obfs-password", "private_key", "preshared_key", "password" })
            {
                if (redactedExtra.TryGetValue(key, out string? val) && !string.IsNullOrEmpty(val))
                {
                    redactedExtra[key] = "••••••••";
                    extraHasSecret = true;
                }
            }
        }

        return new ProxyNode
        {
            Id = Id,
            Name = Name,
            Group = Group,
            Protocol = Protocol,
            Host = Host,
            Port = Port,
            Username = Username,
            Password = null,
            HasCredential = !string.IsNullOrEmpty(Password) || HasCredential || extraHasSecret,
            Method = Method,
            Sni = Sni,
            AllowInsecure = AllowInsecure,
            InsecureApproved = InsecureApproved,
            SourceSubscriptionId = SourceSubscriptionId,
            Extra = redactedExtra,
            Enabled = Enabled,
            Status = Status,
            LatencyMs = LatencyMs,
            ExitIp = ExitIp,
            IspSummary = IspSummary,
            AiReachability = AiReachability.ToList(),
            LastCheckedAt = LastCheckedAt
        };
    }
}

/// <summary>
/// AI 软件独立代理席位
/// </summary>
public sealed class AppSeat
{
    public string Id { get; set; } = string.Empty;
    public string Glyph { get; set; } = "AI";
    public string Name { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string ProcessWin { get; set; } = string.Empty;
    public string ProcessMac { get; set; } = string.Empty;
    public int LocalPort { get; set; }
    public string? BoundNodeId { get; set; }
    public string? RelayNodeId { get; set; }
    public bool Enabled { get; set; } = true;
    public bool KillSwitchEnabled { get; set; } = true;
    public bool AllowSameGroupFailover { get; set; } = true;
    public string CaptureMode { get; set; } = "独立混合端口 + 环境变量注入";
    public string? LaunchCommandWin { get; set; }
    public string? LaunchCommandMac { get; set; }
    /// <summary>
    /// 启动前出口国家/地区硬门禁（ISO 两字母国家码，如 US / JP / SG；空值不限制）
    /// </summary>
    public string? ExpectedCountryCode { get; set; }
    /// <summary>
    /// 按应用追加的内网/私有域绕过规则（逗号分隔追加至 NO_PROXY）
    /// </summary>
    public string? CustomNoProxy { get; set; }
    /// <summary>
    /// 用户自定义指定的客户端可执行文件或 .app 路径
    /// </summary>
    public string? CustomExecutablePath { get; set; }
    /// <summary>
    /// 绑定节点时的不可变快照指纹
    /// </summary>
    public string? BoundNodeSnapshotHash { get; set; }
    /// <summary>
    /// 订阅刷新检测到对应节点已变更时置位，提醒用户确认
    /// </summary>
    public bool HasNodeDrift { get; set; }
}

/// <summary>
/// 订阅源持久化实体
/// </summary>
public sealed class SubscriptionSource
{
    public string Id { get; set; } = "sub_" + Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = string.Empty;
    public string Group { get; set; } = "订阅导入池";
    public string Url { get; set; } = string.Empty;
    public string? ProxyUrl { get; set; }
    public int AutoRefreshHours { get; set; } = 0; // 0 = 手动；6 = 每 6 小时后台静默刷新
    public DateTimeOffset? LastSyncedAt { get; set; }
    public int NodeCount { get; set; }
    public int DriftCount { get; set; }
    public string? LastError { get; set; }

    public SubscriptionPublicDto ToPublicDto()
    {
        string hostHint = "未知订阅源";
        string maskedUrl = "***";
        if (Uri.TryCreate(Url, UriKind.Absolute, out var uri))
        {
            hostHint = uri.Host;
            maskedUrl = $"{uri.Scheme}://{uri.Host}/***";
        }

        return new SubscriptionPublicDto
        {
            Id = Id,
            Name = string.IsNullOrWhiteSpace(Name) ? hostHint : Name,
            Group = Group,
            HostHint = hostHint,
            MaskedUrl = maskedUrl,
            ProxyUrl = ProxyUrl,
            AutoRefreshHours = AutoRefreshHours,
            LastSyncedAt = LastSyncedAt,
            NodeCount = NodeCount,
            DriftCount = DriftCount,
            LastError = LastError
        };
    }
}

public sealed class SubscriptionPublicDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string HostHint { get; set; } = string.Empty;
    public string MaskedUrl { get; set; } = string.Empty;
    public string? ProxyUrl { get; set; }
    public int AutoRefreshHours { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public int NodeCount { get; set; }
    public int DriftCount { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// 席位启动前出口国家核验记录（仅保留状态与国家码，绝不输出凭据）
/// </summary>
public sealed class CountryCheckRecord
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string SeatId { get; set; } = string.Empty;
    public string? ExpectedCountry { get; set; }
    public string? ObservedCountry { get; set; }
    public string ExitIp { get; set; } = "—";
    public int? LatencyMs { get; set; }
    public bool Passed { get; set; }
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// 单席位近期穿透连接审计摘要（仅保留时间、域名/SNI、路由与字节统计，零请求正文）
/// </summary>
public sealed class RecentConnectionEntry
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string SeatId { get; set; } = string.Empty;
    public string TargetHost { get; set; } = string.Empty;
    public int TargetPort { get; set; }
    public bool SniRecovered { get; set; }
    public string RouteSummary { get; set; } = string.Empty;
    public long BytesTx { get; set; }
    public long BytesRx { get; set; }
    public long DurationMs { get; set; }
    public string Outcome { get; set; } = "OK"; // OK | KILLSWITCH | BLOCKED_IPV6 | UNAPPROVED_TLS | FAIL
}

/// <summary>
/// 本机网络适配器信息
/// </summary>
public sealed class NetworkAdapterInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int InterfaceIndex { get; set; }
    public string? IPv4Address { get; set; }
    public int IPv6InterfaceIndex { get; set; }
    public string? IPv6Address { get; set; }
    public string? GatewayAddress { get; set; }
    public string Status { get; set; } = "Down";
    public bool IsVirtualOrTunnel { get; set; }
    public string? ExclusionReason { get; set; }
    public bool IsSelected { get; set; }
}

/// <summary>
/// 网络隔离与防干扰全局配置
/// </summary>
public sealed class IsolationSettings
{
    public int DefaultMixedPort { get; set; } = 17890;
    public string? LockedPhysicalNicId { get; set; }
    public bool AutoBypassVirtualNics { get; set; } = true;
    public bool TlsSniRecoveryEnabled { get; set; } = true;
    public bool ForceRemoteDns { get; set; } = true;
    public bool DirectPhysicalDohEnabled { get; set; } = true;
    public string DohServerIp { get; set; } = "223.5.5.5";
    public bool GlobalKillSwitch { get; set; } = true;
    public bool EnableIpv6 { get; set; } = false;
    public string? CustomSingBoxPath { get; set; }
}

/// <summary>
/// 持久化整体状态文档
/// </summary>
public sealed class HubStateDocument
{
    public IsolationSettings Isolation { get; set; } = new();
    public List<ProxyNode> Nodes { get; set; } = new();
    public List<AppSeat> Seats { get; set; } = new();
    public List<SubscriptionSource> Subscriptions { get; set; } = new();
    public List<ModelPricingRule> CustomPricings { get; set; } = new();
    public int HostApiPort { get; set; } = 17880;
}

/// <summary>
/// 批量解析单行预览结果
/// </summary>
public sealed class BatchParseItem
{
    public int LineNumber { get; set; }
    public string RawLine { get; set; } = string.Empty;
    public bool Valid { get; set; }
    public bool IsDuplicate { get; set; }
    public string? ErrorReason { get; set; }
    public ProxyNode? Node { get; set; }
}

/// <summary>
/// 批量解析响应
/// </summary>
public sealed class BatchParseResult
{
    public int TotalLines { get; set; }
    public int ValidCount { get; set; }
    public int DuplicateCount { get; set; }
    public int ErrorCount { get; set; }
    public List<BatchParseItem> Items { get; set; } = new();
}

/// <summary>
/// 单个 AI 席位的应用配置/启动器接管状态
/// </summary>
public sealed class SeatTakeoverStatus
{
    public string SeatId { get; set; } = string.Empty;
    public bool IsProcessRunning { get; set; }
    public bool SupportsConfigTakeover { get; set; } = true;
    public bool IsTakenOver { get; set; }
    public string ModeSummary { get; set; } = string.Empty;
    public string? TargetPath { get; set; }
    public string? LaunchExecutable { get; set; }
    public string StatusHint { get; set; } = string.Empty;
}

/// <summary>
/// 经由本地席位监听端口的全链路实流验真结果
/// </summary>
public sealed class TunnelVerificationResult
{
    public bool Ok { get; set; }
    public string SeatId { get; set; } = string.Empty;
    public int LocalPort { get; set; }
    public string ExitIp { get; set; } = "—";
    public int? LatencyMs { get; set; }
    public long BytesTx { get; set; }
    public long BytesRx { get; set; }
    public List<string> AiReachability { get; set; } = new();
    public string Message { get; set; } = string.Empty;
    public ProxyNode? Node { get; set; }
}

/// <summary>
/// L2 sing-box 懒加载扩展引擎运行状态
/// </summary>
public sealed class SidecarEngineStatus
{
    public bool Installed { get; set; }
    public bool Running { get; set; }
    public string? BinaryPath { get; set; }
    public string? Version { get; set; }
    public int ActiveL2NodeCount { get; set; }
    public string? BoundInterface { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// 项目仓库与版本单一元数据中心
/// </summary>
public static class ProjectMetadata
{
    public const string Name = "Agent Proxy Hub";
    public const string Version = "0.5.0";
    public const string RepoOwner = "HeyoCode";
    public const string RepoName = "Agent_Proxy_Hub";
    public const string RepoSlug = "HeyoCode/Agent_Proxy_Hub";
    public const string RepoUrl = "https://github.com/HeyoCode/Agent_Proxy_Hub";
    public const string CloneUrl = "https://github.com/HeyoCode/Agent_Proxy_Hub.git";
    public const string Author = "Heyo (@HeyoCode)";
    public const string Description = "AI 专用独立出口代理与双源用量统计桌面网关 (Windows / macOS)";

    public static object ToDto() => new
    {
        name = Name,
        version = Version,
        repoOwner = RepoOwner,
        repoName = RepoName,
        repoSlug = RepoSlug,
        repoUrl = RepoUrl,
        cloneUrl = CloneUrl,
        author = Author,
        description = Description
    };
}
