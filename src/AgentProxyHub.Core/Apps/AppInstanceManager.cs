using AgentProxyHub.Core.Models;
using AgentProxyHub.Core.Storage;

namespace AgentProxyHub.Core.Apps;

/// <summary>
/// 多开应用实例（独立 User-Data / Config 目录与独立本地代理端口）生命周期管理服务
/// </summary>
public sealed class AppInstanceManager
{
    public const int InstancePortBase = 17920;

    private readonly HubStateStore _store;
    private readonly AppProxyTakeoverService _takeover;

    public AppInstanceManager(HubStateStore store, AppProxyTakeoverService takeover)
    {
        _store = store;
        _takeover = takeover;
    }

    /// <summary>
    /// 在状态文档中创建一个新的独立配置目录实例（自动分配可用端口并绑定目录）
    /// </summary>
    public ManagedAppInstance CreateInstance(
        HubStateDocument doc,
        string baseSeatId,
        string name,
        string? customProfileDir = null,
        string? boundNodeId = null,
        string? extraArgs = null,
        string? workingDirectory = null)
    {
        var baseSeat = doc.Seats.FirstOrDefault(s => s.Id.Equals(baseSeatId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"未找到基准席位: {baseSeatId}");

        string cleanName = string.IsNullOrWhiteSpace(name) ? $"{baseSeat.Name}-实例" : name.Trim();
        string id = $"inst-{Guid.NewGuid():N}"[..13];

        string profileDir = !string.IsNullOrWhiteSpace(customProfileDir)
            ? customProfileDir.Trim()
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".agent-proxy-hub",
                "profiles",
                baseSeat.Id,
                id);

        Directory.CreateDirectory(profileDir);

        var usedPorts = doc.Seats.Select(s => s.LocalPort)
            .Concat(doc.Instances.Select(i => i.LocalPort))
            .Concat([doc.Isolation.DefaultMixedPort, doc.HostApiPort])
            .ToHashSet();

        int port = InstancePortBase;
        while (usedPorts.Contains(port)) port++;

        string resolvedNodeId = !string.IsNullOrWhiteSpace(boundNodeId)
            ? boundNodeId
            : (baseSeat.BoundNodeId ?? doc.Nodes.FirstOrDefault()?.Id ?? "direct-phy");

        var instance = new ManagedAppInstance
        {
            Id = id,
            BaseSeatId = baseSeat.Id,
            Name = cleanName,
            ProfileDir = profileDir,
            LocalPort = port,
            BoundNodeId = resolvedNodeId,
            KillSwitchEnabled = true,
            Enabled = true,
            ExtraArgs = string.IsNullOrWhiteSpace(extraArgs) ? null : extraArgs.Trim(),
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory.Trim()
        };

        doc.Instances.Add(instance);
        _store.Save(doc);
        return instance;
    }

    public bool DeleteInstance(HubStateDocument doc, string instanceId, out string message)
    {
        int idx = doc.Instances.FindIndex(i => i.Id.Equals(instanceId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            message = $"未找到实例 {instanceId}";
            return false;
        }

        var inst = doc.Instances[idx];
        doc.Instances.RemoveAt(idx);
        _store.Save(doc);
        message = $"已移除实例「{inst.Name}」(保留磁盘目录 {inst.ProfileDir} 以防数据误删)";
        return true;
    }

    public (bool Ok, string Message) LaunchInstance(HubStateDocument doc, string instanceId)
    {
        var inst = doc.Instances.FirstOrDefault(i => i.Id.Equals(instanceId, StringComparison.OrdinalIgnoreCase));
        if (inst == null) return (false, $"未找到实例 {instanceId}");

        var baseSeat = doc.Seats.FirstOrDefault(s => s.Id.Equals(inst.BaseSeatId, StringComparison.OrdinalIgnoreCase));
        if (baseSeat == null) return (false, $"实例基准席位不存在: {inst.BaseSeatId}");

        Directory.CreateDirectory(inst.ProfileDir);
        var virtualSeat = inst.ToVirtualSeat(baseSeat);
        var (ok, msg) = _takeover.LaunchWithProxy(virtualSeat, inst.ProfileDir, inst.ExtraArgs, inst.WorkingDirectory);
        if (ok)
        {
            inst.LastLaunchedAt = DateTimeOffset.UtcNow;
            _store.Save(doc);
        }
        return (ok, msg);
    }
}
