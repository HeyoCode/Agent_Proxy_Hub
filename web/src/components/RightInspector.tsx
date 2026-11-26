import React, { useMemo, useState } from 'react';
import { AppSeat, OverviewResponse, ProjectMetadataDto, ProxyNode, api, formatBytes, formatRate } from '../api';
import {
  sendWindowCommand,
  startWindowDrag,
  toggleWindowMaximizeOnDoubleClick
} from '../windowBridge';
import { CustomSelect, SelectOption } from './CustomSelect';
import {
  CheckTinyIcon,
  CopyIcon,
  PlayLaunchIcon,
  PlugTakeoverIcon,
  PlusIcon,
  RadarVerifyIcon,
  RefreshSpinIcon,
  WinCloseIcon,
  WinMaxIcon,
  WinMinIcon
} from './Icons';

interface Props {
  project: ProjectMetadataDto;
  overview: OverviewResponse | null;
  selectedSeatId: string;
  throughputMode: 'realtime' | 'cumulative';
  onChangeThroughputMode: (mode: 'realtime' | 'cumulative') => void;
  maximized: boolean;
  onReload: () => Promise<void>;
  onToast: (msg: string) => void;
  onOpenBatch: () => void;
}

const RADAR_SLOTS: { x: number; y: number }[] = [
  { x: 26, y: 24 },
  { x: 70, y: 22 },
  { x: 46, y: 40 },
  { x: 80, y: 44 },
  { x: 22, y: 54 },
  { x: 63, y: 56 }
];

export const RightInspector: React.FC<Props> = ({
  project,
  overview,
  selectedSeatId,
  throughputMode,
  onChangeThroughputMode,
  maximized,
  onReload,
  onToast,
  onOpenBatch
}) => {
  const [verifying, setVerifying] = useState(false);
  const [togglingTakeover, setTogglingTakeover] = useState(false);
  const [launching, setLaunching] = useState(false);
  const [copied, setCopied] = useState(false);

  const seats = overview?.seats ?? [];
  const nodes = overview?.nodes ?? [];
  const seat: AppSeat | undefined = seats.find(s => s.id === selectedSeatId) ?? seats[0];
  const bound: ProxyNode | undefined = nodes.find(n => n.id === seat?.boundNodeId) ?? nodes[0];
  const snap = overview?.trafficSnapshots.find(t => t.seatId === seat?.id);
  const takeover = overview?.seatTakeovers?.find(t => t.seatId === seat?.id);

  const nodeOptions: SelectOption[] = useMemo(() => {
    return nodes.map(n => ({
      value: n.id,
      label: n.name,
      sublabel: n.protocol === 'DIRECT_PHYSICAL' ? '本机物理硬件直出' : `${n.host}:${n.port}`,
      tag: n.protocol === 'DIRECT_PHYSICAL' ? 'DIRECT' : n.protocol,
      dotColor: n.status === 'offline' ? '#F27973' : (n.status === 'online' ? '#34D399' : '#94A3B8')
    }));
  }, [nodes]);

  async function handleToggleSeat(enabled: boolean) {
    if (!seat) return;
    await api.updateSeat(seat.id, { enabled });
    await onReload();
    onToast(`${seat.name} 已${enabled ? '启用独立端口' : '暂停监听'}`);
  }

  async function handleBindNode(nodeId: string) {
    if (!seat) return;
    await api.updateSeat(seat.id, { boundNodeId: nodeId });
    await onReload();
    const target = nodes.find(n => n.id === nodeId);
    onToast(`${seat.name} 已切换出口：${target?.name ?? nodeId}`);
  }

  async function handleToggleKillSwitch(killSwitchEnabled: boolean) {
    if (!seat) return;
    await api.updateSeat(seat.id, { killSwitchEnabled });
    await onReload();
    onToast(`${seat.name} 防串 IP 熔断锁已${killSwitchEnabled ? '开启' : '关闭'}`);
  }

  async function handleToggleTakeover() {
    if (!seat || togglingTakeover) return;
    setTogglingTakeover(true);
    try {
      const next = !(takeover?.isTakenOver ?? false);
      const res = await api.setSeatTakeover(seat.id, next);
      await onReload();
      onToast(res.message);
    } catch (err) {
      onToast(err instanceof Error ? err.message : '接管操作失败');
    } finally {
      setTogglingTakeover(false);
    }
  }

  async function handleLaunchWithProxy() {
    if (!seat || launching) return;
    setLaunching(true);
    try {
      const res = await api.launchSeatApp(seat.id);
      await onReload();
      onToast(res.message);
    } catch (err) {
      onToast(err instanceof Error ? err.message : '启动失败');
    } finally {
      setLaunching(false);
    }
  }

  async function handleCopyEnv() {
    if (!seat) return;
    const cmd = overview?.os === 'mac'
      ? `export HTTPS_PROXY="http://127.0.0.1:${seat.localPort}" ALL_PROXY="socks5h://127.0.0.1:${seat.localPort}"`
      : `$env:HTTPS_PROXY="http://127.0.0.1:${seat.localPort}"; $env:ALL_PROXY="socks5h://127.0.0.1:${seat.localPort}"`;
    await navigator.clipboard?.writeText(cmd);
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1500);
    onToast(`已复制 ${seat.name} (:${seat.localPort}) 环境变量`);
  }

  async function handleVerifyTunnelOrNode() {
    if (!seat || verifying) return;
    setVerifying(true);
    try {
      if (seat.enabled) {
        onToast(`正在经由 :${seat.localPort} 发起全链路实流验真...`);
        const res = await api.verifySeatTunnel(seat.id);
        await onReload();
        onToast(res.message);
      } else if (bound) {
        onToast(`正在直绑物理网卡验真 ${bound.name}...`);
        const res = await api.verifyNode(bound.id);
        await onReload();
        onToast(res.status === 'online'
          ? `验真通过：${res.exitIp} (${res.latencyMs ?? 1} ms)`
          : `验真失败：${res.ispSummary}`);
      }
    } finally {
      setVerifying(false);
    }
  }

  const visibleRadarNodes = nodes.slice(0, RADAR_SLOTS.length);
  const directBindText = overview?.os === 'mac' ? 'IP_BOUND_IF' : 'IP_UNICAST_IF';
  const totalBytes = (snap?.bytesTx ?? 0) + (snap?.bytesRx ?? 0);

  let takeoverDotClass = 'idle';
  let takeoverHintText = '仅端口监听 · 点击右侧接管应用流量';
  if (takeover?.isTakenOver) {
    takeoverDotClass = 'ok';
    takeoverHintText = totalBytes === 0 && takeover.isProcessRunning
      ? `已接管配置 · 重启 ${seat?.name ?? ''} 后即走 :${seat?.localPort}`
      : `已接管配置 · 自动导流至 :${seat?.localPort}`;
  } else if (takeover?.isProcessRunning && totalBytes === 0) {
    takeoverDotClass = 'warn';
    takeoverHintText = `进程正在运行但未接管 · 需接入 :${seat?.localPort}`;
  } else if (!seat?.enabled) {
    takeoverHintText = '席位未启用 · 打开右上开关或一键接管';
  }

  return (
    <aside className="right-inspector" aria-label="选中席位规格与线路雷达">
      {/* 右上角集成无边框原生窗口三键 */}
      <div
        className="inspector-win-bar"
        onMouseDown={startWindowDrag}
        onDoubleClick={toggleWindowMaximizeOnDoubleClick}
      >
        <span className="num" style={{ fontSize: 10.5, color: '#7883A8', letterSpacing: '0.03em' }}>
          v{project.version} · <span className="mono">{directBindText}</span>
        </span>
        <div className="win-ctrl-group" data-no-drag="true">
          <button
            type="button"
            className="win-ctrl-btn"
            title="最小化到任务栏"
            aria-label="最小化"
            onClick={() => sendWindowCommand({ type: 'window:minimize' })}
          >
            <WinMinIcon />
          </button>
          <button
            type="button"
            className="win-ctrl-btn"
            title={maximized ? '还原窗口大小' : '最大化窗口'}
            aria-label="最大化或还原"
            onClick={() => sendWindowCommand({ type: 'window:toggleMaximize' })}
          >
            <WinMaxIcon maximized={maximized} />
          </button>
          <button
            type="button"
            className="win-ctrl-btn close"
            title="收起至系统托盘（保持后台独立代理在线）"
            aria-label="关闭并收起至托盘"
            onClick={() => sendWindowCommand({ type: 'window:hideToTray' })}
          >
            <WinCloseIcon />
          </button>
        </div>
      </div>

      {/* 选中 AI 席位规格检视区 */}
      <div className="inspector-spec-body">
        {seat ? (
          <div key={seat.id} className="inspector-swap-anim" style={{ display: 'flex', flexDirection: 'column', gap: 11 }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: 10 }}>
              <div>
                <h2 style={{ fontSize: 18, fontWeight: 700, color: '#FFFFFF', letterSpacing: '-0.02em', lineHeight: 1.25 }}>
                  {seat.name}
                </h2>
                <div className="num" style={{ fontSize: 11.5, color: '#8691B3', marginTop: 3 }}>
                  <span className="mono">127.0.0.1:{seat.localPort}</span> · {seat.enabled ? '独立端口监听中' : '已暂停'}
                </div>
              </div>
              <label className="nordic-switch" title="启停该 AI 独立端口">
                <input
                  type="checkbox"
                  checked={seat.enabled}
                  onChange={e => handleToggleSeat(e.target.checked)}
                />
                <span className="nordic-slider" />
              </label>
            </div>

            <div>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8 }}>
                <span className="inspector-kicker">
                  {throughputMode === 'realtime' ? '实时直出吞吐' : '累计直出吞吐'}
                </span>
                <div className="inspector-mode-switch" role="group" aria-label="切换实时或累计吞吐显示">
                  <button
                    type="button"
                    className={`inspector-mode-btn ${throughputMode === 'realtime' ? 'active' : ''}`}
                    onClick={() => onChangeThroughputMode('realtime')}
                  >
                    实时
                  </button>
                  <button
                    type="button"
                    className={`inspector-mode-btn ${throughputMode === 'cumulative' ? 'active' : ''}`}
                    onClick={() => onChangeThroughputMode('cumulative')}
                  >
                    累计
                  </button>
                </div>
              </div>

              <div
                className="inspector-big-metric num"
                style={{ cursor: 'pointer' }}
                title="点击切换「实时速率 / 累计总量」"
                onClick={() => onChangeThroughputMode(throughputMode === 'realtime' ? 'cumulative' : 'realtime')}
              >
                {throughputMode === 'realtime'
                  ? `↑ ${formatRate(snap?.bytesTxPerSec ?? 0)} / ↓ ${formatRate(snap?.bytesRxPerSec ?? 0)}`
                  : `↑ ${formatBytes(snap?.bytesTx ?? 0)} / ↓ ${formatBytes(snap?.bytesRx ?? 0)}`}
              </div>

              <div className="inspector-takeover-sub" title={takeover?.statusHint ?? ''}>
                <span className={`inspector-takeover-dot ${takeoverDotClass}`} />
                <span className="num">
                  {throughputMode === 'realtime' && totalBytes > 0
                    ? `累计 ↑ ${formatBytes(snap?.bytesTx ?? 0)} / ↓ ${formatBytes(snap?.bytesRx ?? 0)}`
                    : takeoverHintText}
                </span>
              </div>
            </div>

            <div style={{ display: 'flex', flexDirection: 'column', gap: 9 }}>
              <div className="spec-row">
                <span className="spec-key">绑定线路</span>
                <CustomSelect
                  variant="midnight"
                  size="sm"
                  minMenuWidth={252}
                  value={seat.boundNodeId ?? ''}
                  options={nodeOptions}
                  onChange={handleBindNode}
                  ariaLabel="为当前选中席位选择出口线路"
                />
              </div>

              <div className="spec-row">
                <span className="spec-key">应用接管</span>
                <div style={{ display: 'flex', justifyContent: 'flex-end', alignItems: 'center', gap: 6 }}>
                  <button
                    type="button"
                    disabled={togglingTakeover}
                    className={`inspector-mini-btn ${takeover?.isTakenOver ? 'on' : 'off'}`}
                    title={takeover?.statusHint ?? '一键无损写入或还原该应用的独立代理配置'}
                    onClick={handleToggleTakeover}
                  >
                    <PlugTakeoverIcon size={11} />
                    <span>{togglingTakeover ? '处理中' : (takeover?.isTakenOver ? '已接管' : '一键接管')}</span>
                  </button>
                  <button
                    type="button"
                    disabled={launching}
                    className="inspector-mini-btn off"
                    title="携带本席位独立端口参数启动应用或独立终端"
                    onClick={handleLaunchWithProxy}
                  >
                    <PlayLaunchIcon size={11} />
                    <span>{launching ? '拉起中' : '启动'}</span>
                  </button>
                </div>
              </div>

              <div className="spec-row">
                <span className="spec-key">真实出口 IP</span>
                <span className="spec-val num">{bound?.exitIp ?? '—'}</span>
              </div>

              <div className="spec-row">
                <span className="spec-key">延迟 / 状态</span>
                <span
                  className="spec-val num"
                  style={{ color: bound?.status === 'offline' ? '#FCA5A0' : (bound?.latencyMs ? '#6EE7B7' : '#D5DCEF') }}
                >
                  {bound?.latencyMs
                    ? `${bound.latencyMs} ms · 在线`
                    : (bound?.status === 'offline' ? '连接超时' : '待验真')}
                </span>
              </div>

              <div className="spec-row">
                <span className="spec-key">并发 / 熔断</span>
                <span className="spec-val num">
                  {snap?.activeConnections ?? 0} 活跃流 · {snap?.killSwitchBlockedCount ?? 0} 次拦截
                </span>
              </div>

              <div className="spec-row">
                <span className="spec-key">防串熔断锁</span>
                <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
                  <label className="nordic-switch" title="开启后断线绝不回退本地宽带或 Clash">
                    <input
                      type="checkbox"
                      checked={seat.killSwitchEnabled}
                      onChange={e => handleToggleKillSwitch(e.target.checked)}
                    />
                    <span className="nordic-slider" />
                  </label>
                </div>
              </div>
            </div>

            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 8, marginTop: 2 }}>
              <button
                type="button"
                className="btn-pill btn-sm"
                style={{
                  background: copied ? 'rgba(16, 185, 129, 0.22)' : 'rgba(255, 255, 255, 0.09)',
                  color: copied ? '#6EE7B7' : '#E8ECF7'
                }}
                onClick={handleCopyEnv}
              >
                {copied ? <CheckTinyIcon size={12} /> : <CopyIcon size={13} />}
                <span>{copied ? '已复制' : '复制变量'}</span>
              </button>
              <button
                type="button"
                className="btn-pill btn-sm"
                disabled={verifying}
                style={{ background: 'rgba(255, 255, 255, 0.94)', color: '#171D38' }}
                title={seat.enabled ? `通过 127.0.0.1:${seat.localPort} 实流验证全链路连通并测试直出吞吐` : '直绑物理网卡验真绑定线路'}
                onClick={handleVerifyTunnelOrNode}
              >
                {verifying ? <RefreshSpinIcon size={13} spinning /> : <RadarVerifyIcon size={13} />}
                <span>{verifying ? '验真中' : (seat.enabled ? '全链路验真' : '出口验真')}</span>
              </button>
            </div>
          </div>
        ) : (
          <div style={{ color: '#8C97B8', fontSize: 12 }}>正在同步席位配置...</div>
        )}
      </div>

      {/* 下半部：出口线路池深色坐标雷达 */}
      <div className="inspector-radar-section">
        <div style={{ padding: '0 22px', display: 'flex', justifyContent: 'space-between', alignItems: 'baseline' }}>
          <div>
            <div style={{ fontSize: 13.5, fontWeight: 600, color: '#FFFFFF' }}>独立出口线路池</div>
            <div style={{ fontSize: 11, color: '#7D88AA', marginTop: 2 }}>
              点击坐标直接绑定至 {seat?.name ?? '当前席位'}
            </div>
          </div>
          <button
            type="button"
            onClick={onOpenBatch}
            style={{
              border: 'none',
              background: 'transparent',
              color: '#F27973',
              fontSize: 12,
              fontWeight: 600,
              cursor: 'pointer',
              padding: '2px 4px',
              display: 'inline-flex',
              alignItems: 'center',
              gap: 3
            }}
          >
            <PlusIcon size={12} />
            <span>导入线路</span>
          </button>
        </div>

        <div className="radar-map-canvas">
          <svg
            viewBox="0 0 308 240"
            preserveAspectRatio="none"
            style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', opacity: 0.45 }}
            aria-hidden="true"
          >
            <path d="M-10 42 L118 76 L210 34 L320 68" stroke="#1E274A" strokeWidth="1.2" fill="none" />
            <path d="M-10 112 L94 96 L186 138 L320 105" stroke="#1E274A" strokeWidth="1.2" fill="none" />
            <path d="M38 -10 L76 92 L58 210" stroke="#1B2342" strokeWidth="1.2" fill="none" />
            <path d="M162 -10 L144 82 L206 168 L192 245" stroke="#222C52" strokeWidth="1.4" fill="none" />
            <path d="M245 -10 L218 118 L278 210" stroke="#1B2342" strokeWidth="1.1" fill="none" />
            <circle cx="154" cy="96" r="56" stroke="#1E284D" strokeDasharray="3 5" strokeWidth="1" fill="none" />
          </svg>

          {visibleRadarNodes.map((n, idx) => {
            const slot = RADAR_SLOTS[idx];
            const isActive = n.id === bound?.id;
            return (
              <button
                key={n.id}
                type="button"
                className={`radar-node-dot ${isActive ? 'active' : ''}`}
                style={{ left: `${slot.x}%`, top: `${slot.y}%` }}
                title={`${n.name} (${n.protocol}) · 点击绑定`}
                onClick={() => handleBindNode(n.id)}
              />
            );
          })}

          {bound && (
            <div className="radar-floating-card">
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8 }}>
                <strong style={{ fontSize: 12, fontWeight: 600, color: '#FFFFFF', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {bound.name}
                </strong>
                <span className="num" style={{ fontSize: 10.5, fontWeight: 600, color: bound.status === 'offline' ? '#FCA5A0' : '#6EE7B7', flexShrink: 0 }}>
                  {bound.latencyMs ? `${bound.latencyMs} ms` : bound.protocol}
                </span>
              </div>
              <div className="num" style={{ fontSize: 11, color: '#8F9BBF', marginTop: 3, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {bound.exitIp !== '—' ? `${bound.exitIp} · ${bound.ispSummary}` : `${bound.host}:${bound.port} · ${bound.ispSummary}`}
              </div>
            </div>
          )}
        </div>
      </div>
    </aside>
  );
};
