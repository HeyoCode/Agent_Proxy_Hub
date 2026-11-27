using AgentProxyHub.Core.Diagnostics;
using AgentProxyHub.Core.Models;

namespace AgentProxyHub.Core.Networking;

/// <summary>
/// 订阅源增量同步与「节点不可变快照」防漂移引擎：
/// - 若某席位已绑定了旧订阅节点且新订阅中对应节点发生变化，则保留原节点加注 [快照锁定] 不动，并置位 HasNodeDrift 提醒用户
/// - 未被任何席位锁定的旧订阅节点随订阅刷新平滑更新
/// </summary>
public static class SubscriptionSyncEngine
{
    public sealed record SyncDiffResult(
        int AddedCount,
        int UpdatedCount,
        int RemovedCount,
        int DriftCount,
        int TotalInSubscription);

    public static SyncDiffResult MergeParsedNodes(
        HubStateDocument doc,
        SubscriptionSource sub,
        IReadOnlyList<ProxyNode> incomingNodes)
    {
        var boundNodeIds = doc.GetReferencedNodeIds();

        var oldSubNodes = doc.Nodes
            .Where(n => string.Equals(n.SourceSubscriptionId, sub.Id, StringComparison.Ordinal))
            .ToList();

        var incomingByFp = new Dictionary<string, ProxyNode>(StringComparer.Ordinal);
        var incomingByName = new Dictionary<string, ProxyNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var inc in incomingNodes)
        {
            inc.SourceSubscriptionId = sub.Id;
            inc.Group = string.IsNullOrWhiteSpace(sub.Group) ? inc.Group : sub.Group;
            incomingByFp.TryAdd(inc.Fingerprint, inc);
            if (!string.IsNullOrWhiteSpace(inc.Name))
                incomingByName.TryAdd(inc.Name.Trim(), inc);
        }

        int updated = 0;
        int removed = 0;
        int drift = 0;
        var matchedIncomingFps = new HashSet<string>(StringComparer.Ordinal);

        foreach (var oldNode in oldSubNodes)
        {
            // 1. 指纹完全一致：检查配置哈希是否变化（例如密码/SNI/allowInsecure 变动）
            if (incomingByFp.TryGetValue(oldNode.Fingerprint, out var sameFpNew))
            {
                if (oldNode.ConfigSnapshotHash == sameFpNew.ConfigSnapshotHash)
                {
                    matchedIncomingFps.Add(sameFpNew.Fingerprint);
                    oldNode.Name = sameFpNew.Name;
                    oldNode.Group = sameFpNew.Group;
                    continue;
                }

                if (boundNodeIds.Contains(oldNode.Id))
                {
                    // 旧节点保留为 [快照锁定] 供已绑定席位继续使用，新配置节点仍需进入订阅池供后续切换
                    LockOldSnapshotAndFlagDrift(doc, oldNode);
                    drift++;
                }
                else
                {
                    matchedIncomingFps.Add(sameFpNew.Fingerprint);
                    ApplyNodeConfig(oldNode, sameFpNew);
                    updated++;
                }
                continue;
            }

            // 2. 同名节点但主机/端口/协议发生变化
            if (boundNodeIds.Contains(oldNode.Id))
            {
                LockOldSnapshotAndFlagDrift(doc, oldNode);
                drift++;
            }
            else
            {
                doc.Nodes.Remove(oldNode);
                removed++;
            }
        }

        var existingUnlockedHashes = doc.Nodes
            .Where(n => !n.Name.Contains("[快照锁定]", StringComparison.Ordinal))
            .Select(n => n.ConfigSnapshotHash)
            .ToHashSet(StringComparer.Ordinal);

        int added = 0;
        foreach (var inc in incomingNodes)
        {
            if (matchedIncomingFps.Contains(inc.Fingerprint))
                continue;

            // 若全局已有未锁定的同指纹同来源节点，则认领更新而非重复添加
            var globalMatch = doc.Nodes.FirstOrDefault(n =>
                n.Fingerprint == inc.Fingerprint &&
                n.SourceSubscriptionId == null &&
                !n.Name.Contains("[快照锁定]", StringComparison.Ordinal));
            if (globalMatch != null)
            {
                globalMatch.SourceSubscriptionId = sub.Id;
                updated++;
                continue;
            }

            if (existingUnlockedHashes.Add(inc.ConfigSnapshotHash))
            {
                doc.Nodes.Add(inc);
                added++;
            }
        }

        sub.LastSyncedAt = DateTimeOffset.UtcNow;
        sub.NodeCount = doc.Nodes.Count(n => string.Equals(n.SourceSubscriptionId, sub.Id, StringComparison.Ordinal));
        sub.DriftCount = drift;
        sub.LastError = null;

        HubEventLogger.Default.Info("SUB", $"订阅源「{sub.Name}」同步完成：新增 {added}，更新 {updated}，移除 {removed}，快照防漂移锁定 {drift}");
        return new SyncDiffResult(added, updated, removed, drift, sub.NodeCount);
    }

    private static void LockOldSnapshotAndFlagDrift(HubStateDocument doc, ProxyNode oldNode)
    {
        // 从订阅脱钩保留原快照，防止后续自动刷新覆盖或误删正被 AI 席位使用的出口
        oldNode.SourceSubscriptionId = null;
        if (!oldNode.Name.Contains("[快照锁定]", StringComparison.Ordinal))
            oldNode.Name = $"{oldNode.Name} [快照锁定]";

        foreach (var seat in doc.Seats)
        {
            if (seat.BoundNodeId == oldNode.Id || seat.RelayNodeId == oldNode.Id || seat.FallbackNodeId == oldNode.Id)
            {
                seat.HasNodeDrift = true;
            }
        }
    }

    private static void ApplyNodeConfig(ProxyNode target, ProxyNode src)
    {
        target.Name = src.Name;
        target.Group = src.Group;
        target.Protocol = src.Protocol;
        target.Host = src.Host;
        target.Port = src.Port;
        target.Username = src.Username;
        target.Password = src.Password;
        target.HasCredential = src.HasCredential;
        target.Method = src.Method;
        target.Sni = src.Sni;
        target.AllowInsecure = src.AllowInsecure;
        target.InsecureApproved = !src.AllowInsecure;
        target.Extra = src.Extra != null ? new Dictionary<string, string>(src.Extra, StringComparer.OrdinalIgnoreCase) : null;
        target.IspSummary = src.IspSummary;
    }
}
