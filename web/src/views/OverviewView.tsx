import React from 'react';
import { AppSeat, OverviewResponse, ProxyNode, api, formatBytes } from '../api';

interface Props {
  data: OverviewResponse;
  onReload: () => Promise<void>;
  onToast: (msg: string) => void;
  onOpenBatch: () => void;
  onGoStats: () => void;
}

export const OverviewView: React.FC<Props> = ({ data, onReload, onToast, onOpenBatch, onGoStats }) => {
  const activeSeats = data.seats.filter(s => s.enabled).length;
  const onlineNodes = data.nodes.filter(n => n.status === 'online').length;
  const totalConns = data.trafficSnapshots.reduce((s, t) => s + t.activeConnections, 0);
  const totalTx = data.trafficSnapshots.reduce((s, t) => s + t.bytesTx, 0);
  const totalRx = data.trafficSnapshots.reduce((s, t) => s + t.bytesRx, 0);

  const nicName = data.lockedNic
    ? `${data.lockedNic.name} (${data.lockedNic.iPv4Address ?? '无IPv4'})`
    : '未检测到在线物理网卡';

  const excludedNames = data.excludedVirtualNics.length > 0
    ? data.excludedVirtualNics.map(v => `${v.name}`).slice(0, 3).join(' · ')
    : '当前未运行虚拟隧道网卡（防干扰引擎常驻待命）';

  async function handleToggleSeat(seat: AppSeat, enabled: boolean) {
    await api.updateSeat(seat.id, { enabled });
    await onReload();
    onToast(`${seat.name} 已${enabled ? '启用独立专线监听' : '暂停监听'}`);
  }

  async function handleBindNode(seat: AppSeat, nodeId: string) {
    await api.updateSeat(seat.id, { boundNodeId: nodeId });
    await onReload();
    onToast(`${seat.name} 出口线路已实时热切换`);
  }

  async function copyEnv(seat: AppSeat) {
    const cmd = data.os === 'mac'
      ? `export HTTPS_PROXY="http://127.0.0.1:${seat.localPort}" ALL_PROXY="socks5h://127.0.0.1:${seat.localPort}"`
      : `$env:HTTPS_PROXY="http://127.0.0.1:${seat.localPort}"; $env:ALL_PROXY="socks5h://127.0.0.1:${seat.localPort}"`;
    await navigator.clipboard?.writeText(cmd);
    onToast(`已复制 ${seat.name} 独立端口 (:${seat.localPort}) 环境变量`);
  }

  async function verifyBoundNode(node?: ProxyNode) {
    if (!node) return;
    onToast(`正在直绑物理网卡验真 ${node.name}...`);
    const res = await api.verifyNode(node.id);
    await onReload();
    onToast(res.status === 'online'
      ? `验真通过：${res.exitIp} (${res.latencyMs ?? 1} ms)`
      : `验真失败：${res.ispSummary}`);
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 276px', gap: 12, alignItems: 'stretch' }}>
        <div className="tx-tray">
          <div className="tx-card">
            <div style={{ padding: '11px 16px', display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <strong style={{ fontSize: 13 }}>防 Clash 干扰三段物理直出拓扑</strong>
                <span className="badge badge-emerald">
                  {data.os === 'mac' ? 'IP_BOUND_IF 直绑已激活' : 'IP_UNICAST_IF 直绑已激活'}
                </span>
              </div>
              <span className="mono" style={{ fontSize: 11, color: 'var(--text-muted)' }}>
                系统全局代理：保持未修改
              </span>
            </div>
            <div className="tx-sep" />
            <div style={{ padding: '12px 16px 14px', display: 'flex', flexDirection: 'column', gap: 9 }}>
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 20px 1.12fr 20px 1.15fr 20px 1fr', alignItems: 'center', gap: 4 }}>
                <div className="rule-well">
                  <div style={{ fontSize: 10.5, fontWeight: 600, color: 'var(--text-muted)' }}>① AI 独立应用</div>
                  <div style={{ fontSize: 12, fontWeight: 700, marginTop: 2 }}>Claude / GPT / AGY</div>
                  <div style={{ fontSize: 11, color: 'var(--text-secondary)' }}>多软件隔离会话</div>
                </div>
                <div style={{ textAlign: 'center', color: 'var(--emerald-text)', fontWeight: 700 }}>→</div>
                <div className="rule-well">
                  <div style={{ fontSize: 10.5, fontWeight: 600, color: 'var(--text-muted)' }}>② 本地混合入口 &amp; SNI</div>
                  <div className="mono" style={{ fontSize: 12, fontWeight: 700, marginTop: 2 }}>127.0.0.1:17891–94</div>
                  <div style={{ fontSize: 11, color: 'var(--text-secondary)' }}>
                    {data.isolation.tlsSniRecoveryEnabled ? '自动还原 198.18.x.x Fake-IP' : '直通远端解析'}
                  </div>
                </div>
                <div style={{ textAlign: 'center', color: 'var(--emerald-text)', fontWeight: 700 }}>→</div>
                <div className="rule-well" style={{ background: 'var(--emerald-soft)', borderColor: 'var(--emerald-border)' }}>
                  <div style={{ fontSize: 10.5, fontWeight: 600, color: 'var(--emerald-text)' }}>③ 物理出口网卡锁定</div>
                  <div style={{ fontSize: 12, fontWeight: 700, marginTop: 2 }}>{nicName}</div>
                  <div className="mono" style={{ fontSize: 11, color: 'var(--text-secondary)' }}>
                    网关 {data.lockedNic?.gatewayAddress ?? '自动'} · 直出绕过 TUN
                  </div>
                </div>
                <div style={{ textAlign: 'center', color: 'var(--emerald-text)', fontWeight: 700 }}>→</div>
                <div className="rule-well">
                  <div style={{ fontSize: 10.5, fontWeight: 600, color: 'var(--text-muted)' }}>④ 独立纯净出口</div>
                  <div style={{ fontSize: 12, fontWeight: 700, marginTop: 2 }}>SOCKS5h / HTTP(S)</div>
                  <div style={{ fontSize: 11, color: 'var(--text-secondary)' }}>防串 IP 熔断：{data.isolation.globalKillSwitch ? '已锁' : '关闭'}</div>
                </div>
              </div>

              <div style={{
                display: 'flex', alignItems: 'center', justifyContent: 'space-between',
                padding: '7px 12px', borderRadius: 11, border: '1px dashed var(--amber-border)',
                background: 'var(--amber-soft)', fontSize: 11.5, color: 'var(--text-secondary)'
              }}>
                <span>
                  <strong style={{ color: 'var(--amber-text)' }}>已自动排斥虚拟隧道：</strong>
                  <span className="mono">{excludedNames}</span>
                </span>
                <span className="badge badge-amber">0 字节经过第三方 VPN/Clash</span>
              </div>
            </div>
          </div>
        </div>

        {/* 右侧实时遥测汇总 */}
        <div className="tx-tray">
          <div className="tx-card" style={{ padding: '12px 15px', height: '100%', display: 'flex', flexDirection: 'column', justifyContent: 'space-between' }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
              <strong style={{ fontSize: 12.5 }}>实时网关与流量</strong>
              <button className="tx-btn tx-btn-sec tx-btn-sm" onClick={onGoStats}>用量大盘 →</button>
            </div>
            <div className="tx-sep" />
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline' }}>
              <span style={{ fontSize: 11.5, color: 'var(--text-secondary)' }}>活跃监听席位</span>
              <span className="mono" style={{ fontSize: 15, fontWeight: 700 }}>{activeSeats} / {data.seats.length}</span>
            </div>
            <div className="tx-sep" />
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline' }}>
              <span style={{ fontSize: 11.5, color: 'var(--text-secondary)' }}>线路池可用 / 总数</span>
              <span className="mono" style={{ fontSize: 15, fontWeight: 700 }}>{onlineNodes} / {data.nodes.length}</span>
            </div>
            <div className="tx-sep" />
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline' }}>
              <span style={{ fontSize: 11.5, color: 'var(--text-secondary)' }}>当前并发连接</span>
              <span className="mono" style={{ fontSize: 15, fontWeight: 700 }}>{totalConns} 活跃流</span>
            </div>
            <div className="tx-sep" />
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline' }}>
              <span style={{ fontSize: 11.5, color: 'var(--text-secondary)' }}>累计直出吞吐</span>
              <span className="mono" style={{ fontSize: 12, fontWeight: 700 }}>↑ {formatBytes(totalTx)} · ↓ {formatBytes(totalRx)}</span>
            </div>
          </div>
        </div>
      </div>

      {data.nodes.length <= 1 && (
        <div className="tx-tray">
          <div className="tx-card" style={{ padding: '10px 16px', display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
            <span style={{ fontSize: 12, color: 'var(--text-secondary)' }}>
              当前仅内置「物理网卡直出通道」，建议点击右侧导入您的独立 <span className="mono">SOCKS5 / HTTP(S)</span> 静态住宅线路，即可为 Claude、ChatGPT 与 Antigravity 分配不同国家独立 IP。
            </span>
            <button className="tx-btn tx-btn-primary tx-btn-sm" onClick={onOpenBatch}>立即批量导入线路</button>
          </div>
        </div>
      )}

      {/* 2x2 Bento AI 专属席位卡片 */}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: 12 }}>
        {data.seats.map(seat => {
          const bound = data.nodes.find(n => n.id === seat.boundNodeId);
          const snap = data.trafficSnapshots.find(t => t.seatId === seat.id);
          return (
            <div className="tx-tray" key={seat.id}>
              <div className="tx-card">
                <div style={{ padding: '12px 15px', display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 10 }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                    <div style={{
                      width: 34, height: 34, borderRadius: 11, background: 'var(--card-subtle)',
                      border: '1px solid var(--card-border)', display: 'flex', alignItems: 'center',
                      justifyContent: 'center', fontWeight: 700, fontSize: 12.5
                    }}>{seat.glyph}</div>
                    <div>
                      <div style={{ display: 'flex', alignItems: 'center', gap: 7 }}>
                        <strong style={{ fontSize: 13.5 }}>{seat.name}</strong>
                        <span className={`badge ${seat.enabled ? 'badge-emerald' : 'badge-neutral'}`}>
                          {seat.enabled ? '独立端口监听中' : '未启用'}
                        </span>
                      </div>
                      <div style={{ fontSize: 11.5, color: 'var(--text-secondary)' }}>{seat.subtitle}</div>
                    </div>
                  </div>
                  <label className="tx-switch">
                    <input type="checkbox" checked={seat.enabled} onChange={e => handleToggleSeat(seat, e.target.checked)} />
                    <span className="tx-slider" />
                  </label>
                </div>

                <div className="tx-sep" />

                <div style={{ padding: '12px 15px', display: 'flex', flexDirection: 'column', gap: 10 }}>
                  <div style={{ display: 'grid', gridTemplateColumns: '1.35fr 1fr', gap: 10, alignItems: 'end' }}>
                    <div>
                      <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>绑定独立出口线路</div>
                      <select
                        className="tx-select"
                        value={seat.boundNodeId ?? ''}
                        onChange={e => handleBindNode(seat, e.target.value)}
                      >
                        {data.nodes.map(n => (
                          <option key={n.id} value={n.id}>
                            {n.name} ({n.protocol})
                          </option>
                        ))}
                      </select>
                    </div>
                    <div>
                      <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>本地混合入口 (HTTP/SOCKS5)</div>
                      <div className="tx-input mono" style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                        <span>127.0.0.1:{seat.localPort}</span>
                        <span style={{ fontSize: 10.5, color: 'var(--text-muted)' }}>自适应</span>
                      </div>
                    </div>
                  </div>

                  <div className="rule-well" style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                    <div>
                      <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>真实出口 IP · 属性</div>
                      <div className="mono" style={{ fontSize: 12, fontWeight: 700 }}>
                        {bound?.exitIp ?? '—'} <span style={{ fontWeight: 500, color: 'var(--text-secondary)' }}>· {bound?.ispSummary ?? '未绑定'}</span>
                      </div>
                    </div>
                    <div style={{ textAlign: 'right' }}>
                      <div className="mono" style={{ fontSize: 12, fontWeight: 700, color: bound?.latencyMs ? 'var(--emerald-text)' : 'var(--text-secondary)' }}>
                        {bound?.latencyMs ? `${bound.latencyMs} ms` : (seat.enabled ? '待验真' : '待命')}
                      </div>
                      <div className="mono" style={{ fontSize: 11, color: 'var(--text-muted)' }}>
                        {snap ? `${snap.totalConnections} 次 · ↑${formatBytes(snap.bytesTx)} ↓${formatBytes(snap.bytesRx)}` : '0 次连接'}
                      </div>
                    </div>
                  </div>
                </div>

                <div className="tx-sep" />

                <div style={{ padding: '9px 15px', background: 'var(--card-subtle)', display: 'flex', justifyContent: 'space-between', alignItems: 'center', fontSize: 11.5 }}>
                  <span style={{ color: 'var(--text-secondary)' }}>
                    防串 IP 熔断：<strong>{seat.killSwitchEnabled ? '已锁' : '关闭'}</strong>
                    {snap && snap.killSwitchBlockedCount > 0 ? `（已拦截 ${snap.killSwitchBlockedCount} 次）` : ''}
                  </span>
                  <div style={{ display: 'flex', gap: 6 }}>
                    <button className="tx-btn tx-btn-sec tx-btn-sm" onClick={() => copyEnv(seat)}>复制变量</button>
                    <button className="tx-btn tx-btn-sec tx-btn-sm" onClick={() => verifyBoundNode(bound)}>出口验真</button>
                  </div>
                </div>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
};
