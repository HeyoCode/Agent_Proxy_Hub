import React, { useEffect, useMemo, useState } from 'react';
import {
  AppSeat,
  CountryCheckRecord,
  OverviewResponse,
  ProjectMetadataDto,
  ProxyNode,
  RecentConnectionEntry,
  api,
  formatBytes,
  formatRate
} from '../api';
import { maskEndpoint, maskIp } from '../privacy';
import {
  sendWindowCommand,
  startWindowDrag,
  toggleWindowMaximizeOnDoubleClick
} from '../windowBridge';
import { CustomSelect, SelectOption } from './CustomSelect';
import {
  AlertTriangleIcon,
  CheckTinyIcon,
  CopyIcon,
  GlobeGuardIcon,
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
  privacyMode?: boolean;
  throughputMode: 'realtime' | 'cumulative';
  onChangeThroughputMode: (mode: 'realtime' | 'cumulative') => void;
  maximized: boolean;
  onReload: () => Promise<void>;
  onToast: (msg: string) => void;
  onOpenBatch: () => void;
}

const COUNTRY_GUARD_OPTIONS: SelectOption[] = [
  { value: '', label: '不限制出口地区', sublabel: '按绑定节点直接放行' },
  { value: 'US', label: '🇺🇸 美国 (US)', sublabel: '推荐用于 Claude / OpenAI / Cursor' },
  { value: 'JP', label: '🇯🇵 日本 (JP)', sublabel: '亚洲低延迟干净原生出口' },
  { value: 'SG', label: '🇸🇬 新加坡 (SG)', sublabel: '东南亚低延迟节点' },
  { value: 'GB', label: '🇬🇧 英国 (GB)', sublabel: '欧洲纯净住宅/原生 IP' },
  { value: 'DE', label: '🇩🇪 德国 (DE)', sublabel: '欧洲原生出口' },
  { value: 'CA', label: '🇨🇦 加拿大 (CA)', sublabel: '北美合规出口' },
  { value: 'TW', label: '🇹🇼 台湾 (TW)', sublabel: '部分 Gemini 服务适用' }
];

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
  privacyMode = false,
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
  const [checkingCountry, setCheckingCountry] = useState(false);
  const [showConnections, setShowConnections] = useState(false);
  const [recentConns, setRecentConns] = useState<RecentConnectionEntry[]>([]);
  const [lastCountryCheck, setLastCountryCheck] = useState<CountryCheckRecord | null>(null);
  const [copied, setCopied] = useState(false);

  const seats = overview?.seats ?? [];
  const nodes = overview?.nodes ?? [];
  const seat: AppSeat | undefined = seats.find(s => s.id === selectedSeatId) ?? seats[0];
  const bound: ProxyNode | undefined = nodes.find(n => n.id === seat?.boundNodeId) ?? nodes[0];
  const relay: ProxyNode | undefined = seat?.relayNodeId ? nodes.find(n => n.id === seat.relayNodeId) : undefined;
  const snap = overview?.trafficSnapshots.find(t => t.seatId === seat?.id);
  const takeover = overview?.seatTakeovers?.find(t => t.seatId === seat?.id);
  const isDoubleHopRoute = Boolean(relay && bound && snap?.effectiveNodeName === `${relay.name} → ${bound.name}`);

  useEffect(() => {
    if (!seat?.id) return;
    void api.getSeatConnections(seat.id, 12).then(res => {
      setRecentConns(res.connections ?? []);
      setLastCountryCheck(res.countryHistory?.[0] ?? null);
    }).catch(() => {});
  }, [seat?.id, snap?.totalConnections]);

  const selectableNodes = useMemo(() => nodes.filter(n => !n.isInfoEntry), [nodes]);

  const nodeOptions: SelectOption[] = useMemo(() => {
    return selectableNodes.map(n => ({
      value: n.id,
      label: n.name,
      sublabel: n.protocol === 'DIRECT_PHYSICAL'
        ? '本机物理硬件直出'
        : `${maskEndpoint(n.host, n.port, privacyMode)}${n.requiresSidecarEngine ? ' · sing-box' : ''}${n.allowInsecure && !n.insecureApproved ? ' · 待批准' : ''}`,
      tag: n.protocol === 'DIRECT_PHYSICAL' ? 'DIRECT' : n.protocol,
      dotColor: n.status === 'offline' ? '#F27973' : (n.status === 'online' ? '#34D399' : '#94A3B8')
    }));
  }, [selectableNodes, privacyMode]);

  const relayOptions: SelectOption[] = useMemo(() => {
    const base: SelectOption[] = [
      {
        value: '',
        label: '直连落地（无中转）',
        sublabel: '不经二次跳板，直接由物理网卡连向绑定线路',
        dotColor: '#64748B'
      }
    ];
    for (const n of selectableNodes) {
      if (n.protocol === 'DIRECT_PHYSICAL' || n.id === seat?.boundNodeId) continue;
      base.push({
        value: n.id,
        label: n.name,
        sublabel: `${n.protocol} · ${n.host}:${n.port}`,
        tag: n.requiresSidecarEngine ? 'L2' : 'L1',
        dotColor: n.status === 'offline' ? '#F27973' : (n.status === 'online' ? '#34D399' : '#94A3B8')
      });
    }
    return base;
  }, [selectableNodes, seat?.boundNodeId]);

  async function handleBindRelay(relayNodeId: string) {
    if (!seat) return;
    await api.updateSeat(seat.id, { relayNodeId });
    await onReload();
    if (!relayNodeId) {
      onToast(`${seat.name} 已恢复直连落地（取消前置中转）`);
    } else {
      const target = nodes.find(n => n.id === relayNodeId);
      onToast(`${seat.name} 已开启双跳链式加速：${target?.name ?? relayNodeId} → 落地线路`);
    }
  }

  const fallbackOptions: SelectOption[] = useMemo(() => {
    const base: SelectOption[] = [
      {
        value: '',
        label: '无显式备用',
        sublabel: '主线路失败时仅依同分组策略容灾或直接熔断',
        dotColor: '#64748B'
      }
    ];
    for (const n of selectableNodes) {
      if (n.id === seat?.boundNodeId) continue;
      base.push({
        value: n.id,
        label: n.name,
        sublabel: `${n.protocol} · ${n.host}:${n.port}`,
        tag: n.requiresSidecarEngine ? 'L2' : 'L1',
        dotColor: n.status === 'offline' ? '#F27973' : (n.status === 'online' ? '#34D399' : '#94A3B8')
      });
    }
    return base;
  }, [selectableNodes, seat?.boundNodeId]);

  async function handleBindFallback(fallbackNodeId: string) {
    if (!seat) return;
    try {
      await api.updateSeat(seat.id, { fallbackNodeId });
      await onReload();
      if (!fallbackNodeId) {
        onToast(`${seat.name} 已清除显式备用容灾线路`);
      } else {
        const target = nodes.find(n => n.id === fallbackNodeId);
        onToast(`${seat.name} 显式备用线路已设为：${target?.name ?? fallbackNodeId}`);
      }
    } catch (err) {
      onToast(err instanceof Error ? err.message : '设置备用线路失败');
    }
  }

  async function handleToggleSeat(enabled: boolean) {
    if (!seat) return;
    await api.updateSeat(seat.id, { enabled });
    await onReload();
    onToast(`${seat.name} 已${enabled ? '启用独立端口' : '暂停监听'}`);
  }

  async function handleBindNode(nodeId: string) {
    if (!seat) return;
    try {
      await api.updateSeat(seat.id, { boundNodeId: nodeId });
      await onReload();
      const target = nodes.find(n => n.id === nodeId);
      onToast(`${seat.name} 已切换出口：${target?.name ?? nodeId}`);
    } catch (err) {
      onToast(err instanceof Error ? err.message : '绑定失败');
    }
  }

  async function handleExpectedCountryChange(code: string) {
    if (!seat) return;
    await api.updateSeat(seat.id, { expectedCountryCode: code });
    await onReload();
    onToast(code ? `${seat.name} 已启用出口国家硬门禁：仅允许 ${code}` : `${seat.name} 已取消出口国家限制`);
  }

  async function handleCheckCountryNow() {
    if (!seat || checkingCountry) return;
    setCheckingCountry(true);
    try {
      const res = await api.checkSeatCountry(seat.id);
      setLastCountryCheck(res.check);
      onToast(res.check.reason);
    } finally {
      setCheckingCountry(false);
    }
  }

  async function handleAckDrift() {
    if (!seat) return;
    await api.updateSeat(seat.id, { acknowledgeDrift: true });
    await onReload();
    onToast(`${seat.name} 已确认锁定当前快照线路`);
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
        const safeMsg = privacyMode && res.exitIp && res.exitIp !== '—'
          ? res.message.split(res.exitIp).join(maskIp(res.exitIp, true))
          : res.message;
        onToast(safeMsg);
      } else if (bound) {
        onToast(`正在直绑物理网卡验真 ${bound.name}...`);
        const res = await api.verifyNode(bound.id);
        await onReload();
        onToast(res.status === 'online'
          ? `验真通过：${maskIp(res.exitIp, privacyMode)} (${res.latencyMs ?? 1} ms)`
          : `验真失败：${res.ispSummary}`);
      }
    } catch (err) {
      onToast(err instanceof Error ? err.message : '验真超时或失败，已自动停止');
      void onReload();
    } finally {
      setVerifying(false);
    }
  }

  const visibleRadarNodes = selectableNodes.slice(0, RADAR_SLOTS.length);
  const quotaSummary = overview?.quotaSummary ?? null;
  const seatQuotaCard = quotaSummary?.activeAgents.find(a => a.seatId === seat?.id);
  const primarySeatWin = seatQuotaCard?.windows?.[0];
  const boundTrafficPool = quotaSummary?.trafficPools.find(
    p => (bound?.sourceSubscriptionId && p.id === bound.sourceSubscriptionId) || (bound?.group && p.group === bound.group)
  ) ?? (bound?.protocol !== 'DIRECT_PHYSICAL' ? quotaSummary?.trafficPools[0] : undefined);
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
        <span className="num" style={{ fontSize: 10.5, color: 'var(--insp-sub)', letterSpacing: '0.03em' }}>
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
                <h2 style={{ fontSize: 18, fontWeight: 700, color: 'var(--insp-title)', letterSpacing: '-0.02em', lineHeight: 1.25 }}>
                  {seat.name}
                </h2>
                <div className="num" style={{ fontSize: 11.5, color: 'var(--insp-sub)', marginTop: 3 }}>
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

            {(seatQuotaCard || boundTrafficPool) && (
              <div
                className="insp-quota-strip"
                title={
                  [
                    seatQuotaCard ? seatQuotaCard.windows.map(w => `${w.label}: ${w.usedDisplay}${w.subDisplay ? ` (${w.subDisplay})` : ''}`).join(' | ') : '',
                    boundTrafficPool?.remainingBytes != null ? `${boundTrafficPool.name}: 剩余 ${formatBytes(boundTrafficPool.remainingBytes)}` : ''
                  ].filter(Boolean).join('\n')
                }
              >
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', fontSize: 10.5, gap: 6 }}>
                  <span className="cell-ellipsis" style={{ color: 'var(--insp-val)', fontWeight: 600 }}>
                    {primarySeatWin
                      ? `${primarySeatWin.label} ${primarySeatWin.usedDisplay}`
                      : (quotaSummary?.dailySpentUsd ? `今日 $${quotaSummary.dailySpentUsd.toFixed(2)}` : '配额就绪')}
                  </span>
                  <span
                    className="num nowrap"
                    style={{
                      fontWeight: 600,
                      color: boundTrafficPool?.tone === 'critical'
                        ? 'var(--insp-latency-err)'
                        : (boundTrafficPool?.tone === 'warn' ? 'var(--accent-amber-text)' : 'var(--insp-latency-ok)')
                    }}
                  >
                    {boundTrafficPool?.remainingBytes != null
                      ? `余 ${formatBytes(boundTrafficPool.remainingBytes)}`
                      : (seatQuotaCard?.creditsBadge ?? (bound?.protocol === 'DIRECT_PHYSICAL' ? '直出无限制' : '专线在线'))}
                  </span>
                </div>
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 6 }}>
                  <div className="meter-track" style={{ height: 4 }}>
                    <div
                      className={`meter-fill tone-${primarySeatWin?.tone ?? 'ok'}`}
                      style={{
                        width: `${primarySeatWin?.usedPercent != null
                          ? Math.max(primarySeatWin.usedPercent > 0 ? 6 : 0, primarySeatWin.usedPercent)
                          : (seatQuotaCard && seatQuotaCard.todayTokens > 0 ? 35 : 0)}%`
                      }}
                    />
                  </div>
                  <div className="meter-track" style={{ height: 4 }}>
                    <div
                      className={`meter-fill tone-${boundTrafficPool?.tone ?? 'ok'}`}
                      style={{
                        width: `${boundTrafficPool?.remainingPercent != null
                          ? Math.max(6, boundTrafficPool.remainingPercent)
                          : (boundTrafficPool?.remainingBytes != null ? 100 : 100)}%`
                      }}
                    />
                  </div>
                </div>
              </div>
            )}

            <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              {seat.hasNodeDrift && (
                <div className="insp-drift-banner">
                  <span style={{ display: 'inline-flex', alignItems: 'center', gap: 5 }}>
                    <AlertTriangleIcon size={12} />
                    <span>订阅已更新，仍锁定原出口快照防漂移</span>
                  </span>
                  <button
                    type="button"
                    className="btn-pill btn-soft btn-sm"
                    style={{ height: 22, padding: '0 8px', fontSize: 10.5 }}
                    onClick={handleAckDrift}
                  >
                    确认
                  </button>
                </div>
              )}

              <div className="spec-row">
                <span className="spec-key">绑定线路</span>
                <CustomSelect
                  variant="light"
                  size="sm"
                  minMenuWidth={252}
                  value={seat.boundNodeId ?? ''}
                  options={nodeOptions}
                  onChange={handleBindNode}
                  ariaLabel="为当前选中席位选择出口线路"
                />
              </div>

              <div className="spec-row">
                <span className="spec-key">前置中转</span>
                <CustomSelect
                  variant="light"
                  size="sm"
                  minMenuWidth={252}
                  value={seat.relayNodeId ?? ''}
                  options={relayOptions}
                  onChange={handleBindRelay}
                  ariaLabel="为当前席位选择可选前置高速中转节点"
                />
              </div>

              <div className="spec-row">
                <span className="spec-key">备用容灾</span>
                <CustomSelect
                  variant="light"
                  size="sm"
                  minMenuWidth={252}
                  value={seat.fallbackNodeId ?? ''}
                  options={fallbackOptions}
                  onChange={handleBindFallback}
                  ariaLabel="为主线路宕机时指定显式备用容灾线路"
                />
              </div>

              {snap?.effectiveNodeName && bound && snap.effectiveNodeName !== bound.name && (
                <div
                  className={isDoubleHopRoute ? 'insp-route-banner' : 'insp-drift-banner'}
                  style={{ padding: '5px 9px' }}
                  title={snap.effectiveNodeName}
                >
                  <span className="cell-ellipsis" style={{ maxWidth: '100%' }}>
                    {isDoubleHopRoute ? '双跳加速：' : '容灾切换：'}
                    <strong className="mono">{snap.effectiveNodeName}</strong>
                  </span>
                </div>
              )}

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
                <span className="spec-val num">{maskIp(bound?.exitIp, privacyMode)}</span>
              </div>

              <div className="spec-row">
                <span className="spec-key">国家门禁</span>
                <div style={{ display: 'flex', alignItems: 'center', gap: 5, minWidth: 0 }}>
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <CustomSelect
                      variant="light"
                      size="sm"
                      minMenuWidth={228}
                      value={seat.expectedCountryCode ?? ''}
                      options={COUNTRY_GUARD_OPTIONS}
                      onChange={handleExpectedCountryChange}
                      ariaLabel="设置启动前出口国家门禁"
                    />
                  </div>
                  <button
                    type="button"
                    className="inspector-mini-btn off"
                    style={{ padding: '0 7px', flexShrink: 0 }}
                    disabled={checkingCountry}
                    title={lastCountryCheck?.reason ?? '立即核验出口国家/地区'}
                    onClick={handleCheckCountryNow}
                  >
                    <GlobeGuardIcon size={11} />
                    <span>{checkingCountry ? '测' : (lastCountryCheck?.observedCountry ?? '核验')}</span>
                  </button>
                </div>
              </div>

              <div className="spec-row">
                <span className="spec-key">延迟 / 状态</span>
                <span
                  className="spec-val num"
                  style={{ color: bound?.status === 'offline' ? 'var(--insp-latency-err)' : (bound?.latencyMs ? 'var(--insp-latency-ok)' : 'var(--insp-latency-idle)') }}
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
                  background: copied ? 'var(--insp-copy-ok-bg)' : 'var(--insp-copy-bg)',
                  color: copied ? 'var(--insp-copy-ok-text)' : 'var(--insp-copy-text)'
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
                style={{ background: 'var(--insp-verify-bg)', color: 'var(--insp-verify-text)' }}
                title={seat.enabled ? `通过 127.0.0.1:${seat.localPort} 实流验证全链路连通并测试直出吞吐` : '直绑物理网卡验真绑定线路'}
                onClick={handleVerifyTunnelOrNode}
              >
                {verifying ? <RefreshSpinIcon size={13} spinning /> : <RadarVerifyIcon size={13} />}
                <span>{verifying ? '验真中' : (seat.enabled ? '全链路验真' : '出口验真')}</span>
              </button>
            </div>

            {/* 近期穿透连接实流审计（零正文隐私，仅域名/路由/字节） */}
            <div>
              <button
                type="button"
                style={{
                  width: '100%',
                  border: 'none',
                  background: 'transparent',
                  padding: '2px 0',
                  display: 'flex',
                  justifyContent: 'space-between',
                  alignItems: 'center',
                  cursor: 'pointer',
                  fontSize: 11,
                  fontWeight: 600,
                  color: 'var(--insp-sub)'
                }}
                onClick={() => setShowConnections(v => !v)}
              >
                <span>近期穿透审计 ({recentConns.length})</span>
                <span>{showConnections ? '收起 ▲' : '展开 ▼'}</span>
              </button>
              {showConnections && (
                <div className="insp-conn-list" style={{ marginTop: 6 }}>
                  {recentConns.length === 0 ? (
                    <div style={{ fontSize: 10.5, color: 'var(--insp-sub)', padding: '6px 4px' }}>
                      暂无连接记录（接管后发起对话或点「全链路验真」即可观测）
                    </div>
                  ) : (
                    recentConns.slice(0, 8).map((c, i) => (
                      <div key={`${c.timestamp}-${i}`} className="insp-conn-item">
                        <span className="mono" style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', color: 'var(--insp-val)' }}>
                          {privacyMode ? `${c.targetHost.slice(0, 4)}***:${c.targetPort}` : `${c.targetHost}:${c.targetPort}`}
                        </span>
                        <span className="num" style={{ flexShrink: 0, color: c.outcome === 'OK' ? 'var(--insp-latency-ok)' : 'var(--insp-latency-err)' }}>
                          {c.outcome === 'OK' ? `${formatBytes(c.bytesTx + c.bytesRx)}` : c.outcome}
                        </span>
                      </div>
                    ))
                  )}
                </div>
              )}
            </div>
          </div>
        ) : (
          <div style={{ color: 'var(--insp-sub)', fontSize: 12 }}>正在同步席位配置...</div>
        )}
      </div>

      {/* 下半部：出口线路池坐标雷达 */}
      <div className="inspector-radar-section">
        <div style={{ padding: '0 22px', display: 'flex', justifyContent: 'space-between', alignItems: 'baseline' }}>
          <div>
            <div style={{ fontSize: 13.5, fontWeight: 600, color: 'var(--insp-title)' }}>独立出口线路池</div>
            <div style={{ fontSize: 11, color: 'var(--insp-sub)', marginTop: 2 }}>
              点击坐标直接绑定至 {seat?.name ?? '当前席位'}
            </div>
          </div>
          <button
            type="button"
            onClick={onOpenBatch}
            style={{
              border: 'none',
              background: 'transparent',
              color: 'var(--accent-coral-text)',
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
            style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', opacity: 0.6 }}
            aria-hidden="true"
          >
            <path d="M-10 42 L118 76 L210 34 L320 68" stroke="var(--insp-radar-stroke)" strokeWidth="1.2" fill="none" />
            <path d="M-10 112 L94 96 L186 138 L320 105" stroke="var(--insp-radar-stroke)" strokeWidth="1.2" fill="none" />
            <path d="M38 -10 L76 92 L58 210" stroke="var(--insp-radar-stroke)" strokeWidth="1.2" fill="none" />
            <path d="M162 -10 L144 82 L206 168 L192 245" stroke="var(--insp-radar-stroke)" strokeWidth="1.4" fill="none" />
            <path d="M245 -10 L218 118 L278 210" stroke="var(--insp-radar-stroke)" strokeWidth="1.1" fill="none" />
            <circle cx="154" cy="96" r="56" stroke="var(--insp-radar-stroke)" strokeDasharray="3 5" strokeWidth="1" fill="none" />
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
                <strong style={{ fontSize: 12, fontWeight: 600, color: 'var(--insp-title)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {bound.name}
                </strong>
                <span className="num" style={{ fontSize: 10.5, fontWeight: 600, color: bound.status === 'offline' ? 'var(--insp-latency-err)' : 'var(--insp-latency-ok)', flexShrink: 0 }}>
                  {bound.latencyMs ? `${bound.latencyMs} ms` : bound.protocol}
                </span>
              </div>
              <div className="num" style={{ fontSize: 11, color: 'var(--insp-sub)', marginTop: 3, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {bound.exitIp !== '—'
                  ? `${maskIp(bound.exitIp, privacyMode)} · ${bound.ispSummary}`
                  : `${maskEndpoint(bound.host, bound.port, privacyMode)} · ${bound.ispSummary}`}
              </div>
            </div>
          )}
        </div>
      </div>
    </aside>
  );
};
