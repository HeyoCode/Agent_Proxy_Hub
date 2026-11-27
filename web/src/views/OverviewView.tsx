import React, { useEffect, useMemo, useState } from 'react';
import {
  AppSeat,
  OverviewResponse,
  UsageDashboardResponse,
  api,
  formatCompactTokens
} from '../api';
import {
  AlertCircleIcon,
  CheckTinyIcon,
  CrossTinyIcon,
  SeatBrandIcon,
  ShieldCheckIcon
} from '../components/Icons';
import { SegmentedTabs } from '../components/SegmentedTabs';
import { maskIp } from '../privacy';

interface Props {
  data: OverviewResponse;
  selectedSeatId: string;
  privacyMode?: boolean;
  onSelectSeat: (seatId: string) => void;
  onReload: () => Promise<void>;
  onToast: (msg: string) => void;
  onOpenBatch: () => void;
  onGoStats: () => void;
}

interface ChartPoint {
  label: string;
  tokens: number;
  requests: number;
  costUsd: number;
}

function buildSmoothSvgPath(pts: { x: number; y: number }[]): { line: string; area: string } {
  if (pts.length === 0) return { line: '', area: '' };
  if (pts.length === 1) {
    const p = pts[0];
    return {
      line: `M ${p.x} ${p.y}`,
      area: `M ${p.x} 118 L ${p.x} ${p.y} L ${p.x} 118 Z`
    };
  }

  let line = `M ${pts[0].x.toFixed(2)} ${pts[0].y.toFixed(2)}`;
  for (let i = 0; i < pts.length - 1; i++) {
    const p0 = pts[i === 0 ? i : i - 1];
    const p1 = pts[i];
    const p2 = pts[i + 1];
    const p3 = pts[i + 2 < pts.length ? i + 2 : i + 1];

    const cp1x = p1.x + (p2.x - p0.x) / 6;
    const cp1y = p1.y + (p2.y - p0.y) / 6;
    const cp2x = p2.x - (p3.x - p1.x) / 6;
    const cp2y = p2.y - (p3.y - p1.y) / 6;
    line += ` C ${cp1x.toFixed(2)} ${cp1y.toFixed(2)}, ${cp2x.toFixed(2)} ${cp2y.toFixed(2)}, ${p2.x.toFixed(2)} ${p2.y.toFixed(2)}`;
  }

  const last = pts[pts.length - 1];
  const area = `${line} L ${last.x.toFixed(2)} 118 L ${pts[0].x.toFixed(2)} 118 Z`;
  return { line, area };
}

export const OverviewView: React.FC<Props> = ({
  data,
  selectedSeatId,
  privacyMode = false,
  onSelectSeat,
  onReload,
  onToast,
  onOpenBatch,
  onGoStats
}) => {
  const [chartRange, setChartRange] = useState<'today' | '7d'>('today');
  const [stats, setStats] = useState<UsageDashboardResponse | null>(null);
  const [hoverIdx, setHoverIdx] = useState<number | null>(null);
  const [showAllSeats, setShowAllSeats] = useState(false);
  const [topoPage, setTopoPage] = useState(0);
  const [togglingAll, setTogglingAll] = useState(false);
  const [togglingIpv6, setTogglingIpv6] = useState(false);

  useEffect(() => {
    let active = true;
    const pullStats = () => {
      void api.getStatsDashboard({
        range: chartRange,
        appFilter: 'all',
        modelFilter: 'all',
        page: 1,
        pageSize: 1
      }).then(res => {
        if (active) {
          setStats(res);
        }
      }).catch(() => {});
    };

    pullStats();
    setHoverIdx(null);
    const timer = window.setInterval(pullStats, 5000);
    return () => {
      active = false;
      window.clearInterval(timer);
    };
  }, [chartRange]);

  const offlineEnabledSeat = data.seats.find(s => {
    if (!s.enabled) return false;
    const n = data.nodes.find(node => node.id === s.boundNodeId);
    return n?.status === 'offline';
  });
  const needsNodeSetup = data.nodes.length <= 1 || Boolean(offlineEnabledSeat);
  const allEnabled = data.seats.length > 0 && data.seats.every(s => s.enabled);

  async function handleToggleAll() {
    setTogglingAll(true);
    try {
      const next = !allEnabled;
      await api.toggleAllSeats(next);
      await onReload();
      onToast(next ? '已开启全部 AI 席位独立端口' : '已暂停全部 AI 席位监听');
    } finally {
      setTogglingAll(false);
    }
  }

  async function handleToggleSeat(seat: AppSeat) {
    const next = !seat.enabled;
    await api.updateSeat(seat.id, { enabled: next });
    await onReload();
    onToast(`${seat.name} 已${next ? '启用' : '暂停'}`);
  }

  async function handleToggleIsolation(key: 'tlsSniRecoveryEnabled' | 'globalKillSwitch' | 'enableIpv6', label: string) {
    const next = !data.isolation[key];
    if (key === 'enableIpv6') setTogglingIpv6(true);
    try {
      await api.updateIsolation({ [key]: next });
      await onReload();
      onToast(`${label}已${next ? '开启' : '关闭'}`);
    } finally {
      if (key === 'enableIpv6') setTogglingIpv6(false);
    }
  }

  const chartPoints: ChartPoint[] = useMemo(() => {
    const raw = stats?.timeline ?? [];
    if (raw.length === 0) {
      return chartRange === '7d'
        ? ['周一', '周二', '周三', '周四', '周五', '周六', '周日'].map(label => ({ label, tokens: 0, requests: 0, costUsd: 0 }))
        : ['00:00', '04:00', '08:00', '12:00', '16:00', '20:00', '23:00'].map(label => ({ label, tokens: 0, requests: 0, costUsd: 0 }));
    }

    if (raw.length <= 8) {
      return raw.map(pt => ({
        label: pt.label,
        tokens: pt.totalTokens,
        requests: pt.requests,
        costUsd: pt.costUsd
      }));
    }

    const buckets: ChartPoint[] = [];
    const chunk = Math.ceil(raw.length / 8);
    for (let i = 0; i < raw.length; i += chunk) {
      const slice = raw.slice(i, i + chunk);
      buckets.push({
        label: slice[0].label,
        tokens: slice.reduce((acc, x) => acc + x.totalTokens, 0),
        requests: slice.reduce((acc, x) => acc + x.requests, 0),
        costUsd: slice.reduce((acc, x) => acc + x.costUsd, 0)
      });
    }
    return buckets;
  }, [stats, chartRange]);

  const maxTokens = Math.max(1, ...chartPoints.map(p => p.tokens));
  const peakIdx = chartPoints.reduce((best, p, i, arr) => (p.tokens > arr[best].tokens ? i : best), 0);
  const activeIdx = hoverIdx !== null && chartPoints[hoverIdx] ? hoverIdx : (chartPoints[peakIdx]?.tokens > 0 ? peakIdx : Math.floor(chartPoints.length / 2));

  const svgCoords = useMemo(() => {
    const left = 22;
    const right = 398;
    const top = 38;
    const bottom = 104;
    const span = right - left;
    const hasData = chartPoints.some(p => p.tokens > 0);
    return chartPoints.map((pt, i) => {
      const x = chartPoints.length === 1 ? (left + right) / 2 : left + (span * i) / (chartPoints.length - 1);
      const y = hasData
        ? bottom - (pt.tokens / maxTokens) * (bottom - top)
        : 102;
      return { x, y };
    });
  }, [chartPoints, maxTokens]);

  const { line: linePath, area: areaPath } = useMemo(() => buildSmoothSvgPath(svgCoords), [svgCoords]);
  const activeCoord = svgCoords[activeIdx] ?? { x: 210, y: 80 };
  const activePoint = chartPoints[activeIdx];

  // 独立分流总控动态槽位：优先展示已启用席位，支持翻页总控全部 15 款应用
  const orderedTopoSeats = useMemo(() => {
    return [...data.seats].sort((a, b) => Number(b.enabled) - Number(a.enabled));
  }, [data.seats]);
  const topoPageSize = 4;
  const topoTotalPages = Math.max(1, Math.ceil(orderedTopoSeats.length / topoPageSize));
  const safeTopoPage = Math.min(topoPage, topoTotalPages - 1);
  const topoSlice = orderedTopoSeats.slice(safeTopoPage * topoPageSize, safeTopoPage * topoPageSize + topoPageSize);
  const slot0 = topoSlice[0];
  const slot1 = topoSlice[1];
  const slot2 = topoSlice[2];
  const slot3 = topoSlice[3];

  const nicShort = data.lockedNic ? data.lockedNic.name : '物理网卡待命';

  const renderControlNode = (
    title: string,
    sub: string,
    isOn: boolean,
    actionLabelOn: string,
    actionLabelOff: string,
    onSelect: (() => void) | null,
    onToggle: () => void,
    isFocused: boolean
  ) => (
    <div
      className={`ctrl-node ${isFocused ? 'is-focused' : ''}`}
      onClick={() => onSelect?.()}
    >
      <div className={`ctrl-status-dot ${isOn ? 'ok' : 'off'}`}>
        {isOn ? <CheckTinyIcon /> : <CrossTinyIcon />}
      </div>
      <div style={{ minWidth: 0 }}>
        <div className="ctrl-node-title">{title}</div>
        <div className="ctrl-node-sub num">{sub}</div>
        <button
          type="button"
          className={`ctrl-mini-action ${isOn ? '' : 'is-off'}`}
          onClick={e => {
            e.stopPropagation();
            onToggle();
          }}
        >
          {isOn ? actionLabelOn : actionLabelOff}
        </button>
      </div>
    </div>
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
      <div className="overview-top-grid">
        <div className="overview-left-stack">
          {/* 1. 智能状态横幅卡 */}
          <div className={`alert-banner-card ${needsNodeSetup ? 'coral' : 'indigo'}`}>
            <div>
              <div className="alert-title-row">
                {needsNodeSetup ? <AlertCircleIcon size={17} /> : <ShieldCheckIcon size={17} />}
                <span>{needsNodeSetup ? '独立专线待配置' : '物理硬件直出隔离稳态'}</span>
              </div>
              <div className="alert-body-text">
                {offlineEnabledSeat
                  ? `${offlineEnabledSeat.name} 所绑线路离线，熔断锁已阻止流量泄漏至本地。`
                  : needsNodeSetup
                    ? '当前仅启用本地物理直出，导入静态 SOCKS5 / HTTP 专线后即可分应用独立 IP。'
                    : `已自动绕过 ${data.excludedVirtualNics.length} 个虚拟隧道网卡，系统全局代理零改动。`}
              </div>
            </div>
            <button
              type="button"
              className="btn-pill btn-white btn-sm"
              onClick={needsNodeSetup ? onOpenBatch : onGoStats}
            >
              {needsNodeSetup ? '导入线路' : '查看用量'}
            </button>
          </div>

          {/* 2. 平滑三次贝塞尔用量趋势卡 */}
          <div className="surface-card surface-card-pad" style={{ flex: '1 1 auto', display: 'flex', flexDirection: 'column', justifyContent: 'space-between' }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
              <div>
                <span className="card-title-sm">Token 消耗趋势</span>
                <span className="card-subtitle-sm" style={{ marginLeft: 6 }}>(免解密会话日志)</span>
              </div>
              <SegmentedTabs
                size="sm"
                value={chartRange}
                onChange={setChartRange}
                items={[
                  { value: 'today', label: '今日' },
                  { value: '7d', label: '7 天' }
                ]}
              />
            </div>

            <div style={{ position: 'relative', marginTop: 8 }}>
              <svg viewBox="0 0 420 138" style={{ width: '100%', height: 148, overflow: 'visible' }}>
                <defs>
                  <linearGradient id="coralAreaGrad" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="0%" stopColor="#F27973" stopOpacity="0.24" />
                    <stop offset="100%" stopColor="#F27973" stopOpacity="0.01" />
                  </linearGradient>
                </defs>

                {[42, 72, 102].map(y => (
                  <line key={y} x1="16" y1={y} x2="404" y2={y} stroke="var(--divider)" strokeWidth="1" />
                ))}

                {areaPath && <path d={areaPath} fill="url(#coralAreaGrad)" />}
                {linePath && <path d={linePath} fill="none" stroke="#F27973" strokeWidth="2.4" strokeLinecap="round" />}

                <line
                  className="chart-hover-line"
                  x1={activeCoord.x}
                  y1={activeCoord.y}
                  x2={activeCoord.x}
                  y2="118"
                  stroke="#F27973"
                  strokeOpacity="0.35"
                  strokeDasharray="3 3"
                  strokeWidth="1.2"
                />
                <circle className="chart-hover-halo" cx={activeCoord.x} cy={activeCoord.y} r="7" fill="#F27973" fillOpacity="0.2" />
                <circle className="chart-hover-dot" cx={activeCoord.x} cy={activeCoord.y} r="3.6" fill="#F27973" stroke="#FFFFFF" strokeWidth="1.6" />

                {svgCoords.map((pt, i) => (
                  <g key={chartPoints[i].label}>
                    <rect
                      x={pt.x - 24}
                      y="10"
                      width="48"
                      height="112"
                      fill="transparent"
                      style={{ cursor: 'pointer' }}
                      onMouseEnter={() => setHoverIdx(i)}
                    />
                    <text
                      x={pt.x}
                      y="134"
                      textAnchor="middle"
                      fontSize="9.8"
                      fontFamily="var(--font-sans)"
                      fill={i === activeIdx ? '#EA6660' : 'var(--text-muted)'}
                      fontWeight={i === activeIdx ? '700' : '500'}
                    >
                      {chartPoints[i].label}
                    </text>
                  </g>
                ))}
              </svg>

              {activePoint && (
                <div
                  className="chart-popover-tip"
                  style={{
                    position: 'absolute',
                    left: `${Math.min(78, Math.max(22, (activeCoord.x / 420) * 100))}%`,
                    top: `${Math.max(2, Math.min(54, ((activeCoord.y - 38) / 138) * 100))}%`,
                    transform: 'translate(-50%, -25%)'
                  }}
                >
                  <div className="num" style={{ fontSize: 10, color: 'var(--text-muted)' }}>
                    {activePoint.label} · {activePoint.requests} 次请求
                  </div>
                  <div className="num" style={{ fontSize: 11.5, fontWeight: 700, color: 'var(--text-primary)' }}>
                    {activePoint.tokens > 0 ? `${formatCompactTokens(activePoint.tokens)} Tokens` : '0 Tokens'}
                  </div>
                </div>
              )}
            </div>
          </div>
        </div>

        {/* 3. 右侧：独立分流总控卡片 */}
        <div className="surface-card controls-card">
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
              <div className="card-title-sm">独立分流总控</div>
              {topoTotalPages > 1 && (
                <div
                  style={{
                    display: 'inline-flex',
                    alignItems: 'center',
                    gap: 2,
                    background: 'var(--bg-subtle)',
                    borderRadius: 7,
                    padding: '2px 4px',
                    fontSize: 11,
                    color: 'var(--text-secondary)'
                  }}
                >
                  <button
                    type="button"
                    aria-label="上一页应用"
                    style={{
                      border: 'none',
                      background: 'transparent',
                      color: 'var(--text-secondary)',
                      width: 18,
                      height: 18,
                      borderRadius: 5,
                      cursor: 'pointer',
                      fontSize: 12,
                      lineHeight: 1,
                      display: 'inline-flex',
                      alignItems: 'center',
                      justifyContent: 'center'
                    }}
                    onClick={() => setTopoPage(p => (p <= 0 ? topoTotalPages - 1 : p - 1))}
                  >
                    ‹
                  </button>
                  <span className="num" style={{ padding: '0 3px', fontSize: 10.5, fontWeight: 600 }}>
                    {safeTopoPage + 1}/{topoTotalPages}
                  </span>
                  <button
                    type="button"
                    aria-label="下一页应用"
                    style={{
                      border: 'none',
                      background: 'transparent',
                      color: 'var(--text-secondary)',
                      width: 18,
                      height: 18,
                      borderRadius: 5,
                      cursor: 'pointer',
                      fontSize: 12,
                      lineHeight: 1,
                      display: 'inline-flex',
                      alignItems: 'center',
                      justifyContent: 'center'
                    }}
                    onClick={() => setTopoPage(p => (p + 1) % topoTotalPages)}
                  >
                    ›
                  </button>
                </div>
              )}
            </div>
            <div style={{ display: 'flex', alignItems: 'center', gap: 16 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <span style={{ fontSize: 12, fontWeight: 600, color: data.isolation.enableIpv6 ? 'var(--accent-indigo)' : 'var(--text-secondary)' }}>
                  IPv6
                </span>
                <label className="nordic-switch" title="开启/关闭 IPv6 双栈代理与物理网卡直绑（默认关闭以防 IPv6 泄漏与单栈代理卡顿）">
                  <input
                    type="checkbox"
                    checked={data.isolation.enableIpv6}
                    disabled={togglingIpv6}
                    onChange={() => handleToggleIsolation('enableIpv6', 'IPv6 双栈转发')}
                  />
                  <span className="nordic-slider" />
                </label>
              </div>
              <div style={{ width: 1, height: 14, background: 'var(--divider)' }} aria-hidden="true" />
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <span style={{ fontSize: 12, fontWeight: 600, color: 'var(--text-secondary)' }}>
                  {allEnabled ? '全部开启' : '一键全开'}
                </span>
                <label className="nordic-switch" title="一键启停全部 AI 席位端口">
                  <input
                    type="checkbox"
                    checked={allEnabled}
                    disabled={togglingAll}
                    onChange={handleToggleAll}
                  />
                  <span className="nordic-slider" />
                </label>
              </div>
            </div>
          </div>

          <div className="controls-stage">
            <div className="ctrl-col">
              {slot0 ? renderControlNode(
                slot0.name,
                `:${slot0.localPort}`,
                slot0.enabled,
                '已启用',
                '点击启用',
                () => onSelectSeat(slot0.id),
                () => handleToggleSeat(slot0),
                selectedSeatId === slot0.id
              ) : <div style={{ minHeight: 58 }} />}
              {slot1 ? renderControlNode(
                slot1.name,
                `:${slot1.localPort}`,
                slot1.enabled,
                '已启用',
                '点击启用',
                () => onSelectSeat(slot1.id),
                () => handleToggleSeat(slot1),
                selectedSeatId === slot1.id
              ) : <div style={{ minHeight: 58 }} />}
              {renderControlNode(
                'SNI 域名还原',
                data.isolation.tlsSniRecoveryEnabled ? '198.18 自动剥离' : '已暂停',
                data.isolation.tlsSniRecoveryEnabled,
                '还原中',
                '点击开启',
                null,
                () => handleToggleIsolation('tlsSniRecoveryEnabled', 'TLS SNI 还原'),
                false
              )}
            </div>

            {/* 中央精密物理网卡直出拓扑核心 SVG（活跃通道附加流光虚线） */}
            <div className="ctrl-center-diagram">
              <svg viewBox="0 0 240 220" style={{ width: '100%', height: '100%', overflow: 'visible' }} aria-hidden="true">
                <path
                  d="M8 34 H58 L84 68"
                  className={slot0?.enabled ? 'ctrl-wire-active' : ''}
                  stroke={slot0?.enabled ? undefined : 'var(--divider)'}
                  strokeWidth="1.4"
                  fill="none"
                />
                <path
                  d="M8 110 H66"
                  className={slot1?.enabled ? 'ctrl-wire-active' : ''}
                  stroke={slot1?.enabled ? undefined : 'var(--divider)'}
                  strokeWidth="1.4"
                  fill="none"
                />
                <path
                  d="M8 186 H58 L84 152"
                  className={data.isolation.tlsSniRecoveryEnabled ? 'ctrl-wire-active' : ''}
                  stroke={data.isolation.tlsSniRecoveryEnabled ? undefined : 'var(--divider)'}
                  strokeWidth="1.4"
                  fill="none"
                />

                <path
                  d="M232 34 H182 L156 68"
                  className={slot2?.enabled ? 'ctrl-wire-active' : ''}
                  stroke={slot2?.enabled ? undefined : 'var(--divider)'}
                  strokeWidth="1.4"
                  fill="none"
                />
                <path
                  d="M232 110 H174"
                  className={slot3?.enabled ? 'ctrl-wire-active' : ''}
                  stroke={slot3?.enabled ? undefined : 'var(--divider)'}
                  strokeWidth="1.4"
                  fill="none"
                />
                <path
                  d="M232 186 H182 L156 152"
                  className={data.isolation.globalKillSwitch ? 'ctrl-wire-active' : ''}
                  stroke={data.isolation.globalKillSwitch ? undefined : 'var(--divider)'}
                  strokeWidth="1.4"
                  fill="none"
                />

                <circle cx="120" cy="110" r="56" fill="var(--bg-subtle)" />
                <circle cx="120" cy="110" r="46" stroke="var(--accent-indigo)" strokeOpacity="0.22" strokeWidth="1.5" strokeDasharray="4 4" fill="var(--bg-card)" />
                <path
                  d="M120 74 L148 90 V130 L120 146 L92 130 V90 Z"
                  fill="var(--bg-subtle)"
                  stroke="var(--accent-indigo)"
                  strokeWidth="1.8"
                  strokeLinejoin="round"
                />
                <circle cx="120" cy="110" r="14" fill="var(--accent-indigo)" fillOpacity="0.14" />
                <circle cx="120" cy="110" r="5.5" fill="var(--accent-indigo)" />
              </svg>

              <div style={{ textAlign: 'center', marginTop: -12 }}>
                <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-primary)' }}>
                  物理出口 · {nicShort}
                </div>
                <div className="num" style={{ fontSize: 10.5, color: 'var(--text-muted)' }}>
                  {data.lockedNic?.iPv4Address ? maskIp(data.lockedNic.iPv4Address, privacyMode) : '直绑硬件接口'}
                </div>
              </div>
            </div>

            <div className="ctrl-col">
              {slot2 ? renderControlNode(
                slot2.name,
                `:${slot2.localPort}`,
                slot2.enabled,
                '已启用',
                '点击启用',
                () => onSelectSeat(slot2.id),
                () => handleToggleSeat(slot2),
                selectedSeatId === slot2.id
              ) : <div style={{ minHeight: 58 }} />}
              {slot3 ? renderControlNode(
                slot3.name,
                `:${slot3.localPort}`,
                slot3.enabled,
                '已启用',
                '点击启用',
                () => onSelectSeat(slot3.id),
                () => handleToggleSeat(slot3),
                selectedSeatId === slot3.id
              ) : <div style={{ minHeight: 58 }} />}
              {renderControlNode(
                '防串熔断锁',
                data.isolation.globalKillSwitch ? '断线绝不回退' : '未锁定',
                data.isolation.globalKillSwitch,
                '已锁死',
                '点击锁定',
                null,
                () => handleToggleIsolation('globalKillSwitch', '全局防串 IP 熔断锁'),
                false
              )}
            </div>
          </div>
        </div>
      </div>

      {/* 下半区：AI 应用席位卡片（默认展示核心 4 席位 + 所有已启用席位，支持一键展开全部） */}
      {(() => {
        const coreIds = new Set(['claude', 'chatgpt', 'agy', 'antigravity', 'cursor']);
        const visibleSeats = showAllSeats
          ? data.seats
          : data.seats.filter(s => coreIds.has(s.id) || s.enabled || s.id === selectedSeatId);
        const hiddenCount = data.seats.length - visibleSeats.length;

        return (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            {data.seats.length > 4 && (
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '0 2px' }}>
                <span style={{ fontSize: 12, fontWeight: 600, color: 'var(--text-secondary)' }}>
                  AI 应用独立出口席位（共 {data.seats.length} 款支持应用）
                </span>
                <button
                  type="button"
                  className="btn-pill btn-soft btn-sm"
                  style={{ height: 26, fontSize: 11.5 }}
                  onClick={() => setShowAllSeats(v => !v)}
                >
                  {showAllSeats ? '收起未启用席位' : `展开全部 ${data.seats.length} 款应用${hiddenCount > 0 ? ` (+${hiddenCount})` : ''}`}
                </button>
              </div>
            )}
            <div className="seat-gauges-grid">
              {visibleSeats.map(seat => {
          const bound = data.nodes.find(n => n.id === seat.boundNodeId);
          const isSelected = seat.id === selectedSeatId;
          const isOffline = seat.enabled && bound?.status === 'offline';

          let progress = 0.16;
          let ringColor = '#CBD3E6';
          let centerText = '待命';

          if (seat.enabled) {
            if (isOffline) {
              progress = 0.24;
              ringColor = '#F27973';
              centerText = '超时';
            } else if (bound?.latencyMs) {
              const ms = bound.latencyMs;
              if (ms >= 1500) {
                progress = 0.36;
                ringColor = '#F59E0B';
              } else {
                progress = Math.max(0.45, Math.min(0.92, 1 - ms / 1600));
                ringColor = '#4A56E2';
              }
              centerText = ms >= 1000 ? `${(ms / 1000).toFixed(1)}s` : `${ms}ms`;
            } else {
              progress = 0.68;
              ringColor = '#4A56E2';
              centerText = '在线';
            }
          }

          const radius = 37;
          const circumference = 2 * Math.PI * radius;
          const dashOffset = circumference * (1 - progress);

          return (
            <button
              key={seat.id}
              type="button"
              className={`seat-gauge-card ${isSelected ? 'selected' : ''}`}
              onClick={() => onSelectSeat(seat.id)}
            >
              <div style={{ color: 'var(--text-primary)', display: 'inline-flex', alignItems: 'center', justifyContent: 'center', height: 24 }}>
                <SeatBrandIcon seatId={seat.id} size={22} />
              </div>
              <div style={{ fontSize: 13.5, fontWeight: 600, color: 'var(--text-primary)', marginTop: 8 }}>
                {seat.name}
              </div>

              <div className="gauge-ring-wrap">
                <svg width="92" height="92" viewBox="0 0 92 92" style={{ position: 'absolute', inset: 0 }} aria-hidden="true">
                  <circle cx="46" cy="46" r="40" fill="var(--bg-subtle)" />
                  <circle cx="46" cy="46" r={radius} fill="none" stroke="var(--ring-track)" strokeWidth="3.5" />
                  <circle
                    cx="46"
                    cy="46"
                    r={radius}
                    fill="none"
                    stroke={ringColor}
                    strokeWidth="3.5"
                    strokeLinecap="round"
                    strokeDasharray={circumference}
                    strokeDashoffset={dashOffset}
                    transform="rotate(-90 46 46)"
                    style={{ transition: 'stroke-dashoffset 0.38s cubic-bezier(0.22, 1, 0.36, 1), stroke 0.25s ease' }}
                  />
                </svg>
                <span className="gauge-center-label num">{centerText}</span>
              </div>

              <div
                className="num"
                style={{
                  fontSize: 11,
                  color: 'var(--text-muted)',
                  maxWidth: '100%',
                  overflow: 'hidden',
                  textOverflow: 'ellipsis',
                  whiteSpace: 'nowrap'
                }}
              >
                :{seat.localPort} · {bound?.name ?? '未绑定线路'}
              </div>
            </button>
          );
        })}
            </div>
          </div>
        );
      })()}
    </div>
  );
};
