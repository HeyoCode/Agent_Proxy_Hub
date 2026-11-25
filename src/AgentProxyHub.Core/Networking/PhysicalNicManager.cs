using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using AgentProxyHub.Core.Models;

namespace AgentProxyHub.Core.Networking;

/// <summary>
/// 物理网卡管理与内核 Socket 硬件接口直绑引擎（Windows / macOS 双端适配）
/// 通过绑定真实物理接口绕过 Clash/Mihomo/Surge/Tailscale 的 TUN 虚拟路由表
/// </summary>
public sealed class PhysicalNicManager
{
    private static readonly string[] KernelShimKeywords =
    [
        "wfp", "qos packet scheduler", "ndis capture", "lightweight filter",
        "native mac layer", "npcap packet driver", "wan miniport", "kernel debug",
        "ras async", "virtual switch extension"
    ];

    private static readonly string[] VirtualKeywords =
    [
        "wintun", "mihomo", "clash", "meta", "sing-box", "nekoray", "v2ray",
        "utun", "tun", "tap", "wireguard", "tailscale", "zerotier", "hamachi",
        "hyper-v", "vethernet", "vmware", "virtualbox", "vbox", "loopback",
        "teredo", "isatap", "npcap", "bridge", "awdl", "llw", "anpi"
    ];

    private const int IpProtoIp = 0;
    private const int WinIpUnicastIf = 31;
    private const int MacIpBoundIf = 25;

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int setsockopt(IntPtr s, int level, int optname, ref int optval, int optlen);

    [DllImport("libc", EntryPoint = "setsockopt", SetLastError = true)]
    private static extern int mac_setsockopt(int s, int level, int optname, ref uint optval, uint optlen);

    private readonly object _lock = new();
    private List<NetworkAdapterInfo> _cachedAdapters = new();
    private NetworkAdapterInfo? _lockedAdapter;

    public PhysicalNicManager(string? preferredNicId = null)
    {
        Refresh(preferredNicId);
    }

    public IReadOnlyList<NetworkAdapterInfo> GetAdapters()
    {
        lock (_lock)
        {
            return _cachedAdapters.ToList();
        }
    }

    public NetworkAdapterInfo? GetLockedAdapter()
    {
        lock (_lock)
        {
            return _lockedAdapter;
        }
    }

    public bool HasDetectedClashOrTunnel =>
        GetAdapters().Any(a => a.IsVirtualOrTunnel && a.Status == "Up");

    /// <summary>
    /// 扫描本机所有网卡并自动排斥虚拟隧道接口，选中优先物理网卡
    /// </summary>
    public List<NetworkAdapterInfo> Refresh(string? preferredNicId = null)
    {
        var list = new List<NetworkAdapterInfo>();
        NetworkInterface[] nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            nics = Array.Empty<NetworkInterface>();
        }

        foreach (var nic in nics)
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            string combined = $"{nic.Name} {nic.Description}".ToLowerInvariant();
            if (KernelShimKeywords.Any(k => combined.Contains(k, StringComparison.Ordinal)))
                continue;

            var ipProps = SafeGetIpProperties(nic);
            int ifIndex = SafeGetIpv4Index(nic, ipProps);

            string? ipv4 = ipProps?.UnicastAddresses
                .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address))
                ?.Address.ToString();

            // 忽略无接口索引且无 IPv4 的幽灵虚拟过滤层
            if (ifIndex <= 0 && string.IsNullOrEmpty(ipv4))
                continue;

            string? gateway = ipProps?.GatewayAddresses
                .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                ?.Address.ToString();

            bool isVirtual = VirtualKeywords.Any(k => combined.Contains(k, StringComparison.Ordinal))
                             || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel
                             || (ipv4 != null && ipv4.StartsWith("198.18.", StringComparison.Ordinal));

            string? reason = null;
            if (isVirtual)
            {
                reason = combined.Contains("clash") || combined.Contains("mihomo") || combined.Contains("wintun") || combined.Contains("meta") || (ipv4?.StartsWith("198.18.") ?? false)
                    ? "检测到代理虚拟隧道网卡 · 已自动排斥"
                    : "虚拟/隧道适配器 · 已排斥";
            }

            list.Add(new NetworkAdapterInfo
            {
                Id = nic.Id,
                Name = nic.Name,
                Description = string.IsNullOrWhiteSpace(nic.Description) ? nic.Name : nic.Description,
                InterfaceIndex = ifIndex,
                IPv4Address = ipv4,
                GatewayAddress = gateway,
                Status = nic.OperationalStatus == OperationalStatus.Up ? "Up" : "Down",
                IsVirtualOrTunnel = isVirtual,
                ExclusionReason = reason
            });
        }

        // 排序：已启用的物理有网关网卡优先，其次无网关物理网卡，最后排斥虚拟网卡
        list = list
            .OrderBy(a => a.IsVirtualOrTunnel ? 1 : 0)
            .ThenBy(a => a.Status == "Up" ? 0 : 1)
            .ThenBy(a => string.IsNullOrEmpty(a.GatewayAddress) ? 1 : 0)
            .ThenBy(a => a.InterfaceIndex)
            .ToList();

        NetworkAdapterInfo? selected = null;
        if (!string.IsNullOrWhiteSpace(preferredNicId))
        {
            selected = list.FirstOrDefault(a => a.Id == preferredNicId && !a.IsVirtualOrTunnel && a.Status == "Up");
        }
        selected ??= list.FirstOrDefault(a => !a.IsVirtualOrTunnel && a.Status == "Up" && !string.IsNullOrEmpty(a.GatewayAddress))
                  ?? list.FirstOrDefault(a => !a.IsVirtualOrTunnel && a.Status == "Up");

        foreach (var item in list)
        {
            item.IsSelected = selected != null && item.Id == selected.Id;
        }

        lock (_lock)
        {
            _cachedAdapters = list;
            _lockedAdapter = selected;
        }

        return list;
    }

    /// <summary>
    /// 创建并直绑物理网卡的 TCP Socket（避开虚拟隧道）
    /// 当目标为本机环回地址（用于集成测试或本地路由）时跳过物理网卡绑定
    /// </summary>
    public Socket CreateBoundTcpSocket(AddressFamily family, IPAddress? targetIpHint = null)
    {
        var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        ConfigureKeepAlive(socket);

        if (targetIpHint != null && IPAddress.IsLoopback(targetIpHint))
        {
            return socket;
        }

        var nic = GetLockedAdapter();
        if (nic == null || nic.InterfaceIndex <= 0 || family != AddressFamily.InterNetwork)
        {
            return socket;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!string.IsNullOrEmpty(nic.IPv4Address) && IPAddress.TryParse(nic.IPv4Address, out var localIp))
                {
                    socket.Bind(new IPEndPoint(localIp, 0));
                }

                // Windows IP_UNICAST_IF 要求网络字节序接口索引
                int netOrderIndex = IPAddress.HostToNetworkOrder(nic.InterfaceIndex);
                setsockopt(socket.Handle, IpProtoIp, WinIpUnicastIf, ref netOrderIndex, sizeof(int));
            }
            else if (OperatingSystem.IsMacOS())
            {
                uint idx = (uint)nic.InterfaceIndex;
                mac_setsockopt((int)socket.Handle, IpProtoIp, MacIpBoundIf, ref idx, sizeof(uint));
            }
        }
        catch
        {
            // 在受限沙盒或接口瞬态变化时优雅降级，由调用方连接与熔断逻辑兜底
        }

        return socket;
    }

    /// <summary>
    /// 创建直绑物理网卡的 UDP Socket（用于上游代理域名的独立防污染 DNS 解析）
    /// </summary>
    public Socket CreateBoundUdpSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var nic = GetLockedAdapter();
        if (nic == null || nic.InterfaceIndex <= 0)
            return socket;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!string.IsNullOrEmpty(nic.IPv4Address) && IPAddress.TryParse(nic.IPv4Address, out var localIp))
                {
                    socket.Bind(new IPEndPoint(localIp, 0));
                }

                int netOrderIndex = IPAddress.HostToNetworkOrder(nic.InterfaceIndex);
                setsockopt(socket.Handle, IpProtoIp, WinIpUnicastIf, ref netOrderIndex, sizeof(int));
            }
            else if (OperatingSystem.IsMacOS())
            {
                uint idx = (uint)nic.InterfaceIndex;
                mac_setsockopt((int)socket.Handle, IpProtoIp, MacIpBoundIf, ref idx, sizeof(uint));
            }
        }
        catch
        {
            // ignore
        }

        return socket;
    }

    private static void ConfigureKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 25);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch
        {
            // 部分平台精简容器可能不支持细粒度 KeepAlive 参数，保持默认 KeepAlive
        }
    }

    private static IPInterfaceProperties? SafeGetIpProperties(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties();
        }
        catch
        {
            return null;
        }
    }

    private static int SafeGetIpv4Index(NetworkInterface nic, IPInterfaceProperties? ipProps)
    {
        if (ipProps == null || !nic.Supports(NetworkInterfaceComponent.IPv4))
            return 0;

        try
        {
            return ipProps.GetIPv4Properties()?.Index ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}
