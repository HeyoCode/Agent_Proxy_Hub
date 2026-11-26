import React, { useCallback, useEffect, useRef, useState } from 'react';
import { AppSeat, OverviewResponse, api, formatBytes, formatRate } from '../api';
import { sendWindowCommand, subscribeTrayShown } from '../windowBridge';
import { CheckTinyIcon, GitHubIcon, RefreshSpinIcon } from './Icons';

const TRAY_SEAT_TITLES: Record<string, string> = {
  claude: 'Claude (Desktop + Code)',
  chatgpt: 'ChatGPT (Desktop + Codex)',
  agy: 'Antigravity (IDE + Agent)',
  antigravity: 'Antigravity (IDE + Agent)',
  cursor: 'Cursor (AI 代码编辑器)'
};

export const TrayQuickPanel: React.FC = () => {
  const rootRef = useRef<HTMLDivElement>(null);
  const [overview, setOverview] = useState<OverviewResponse | null>(null);
  const [busySeatId, setBusySeatId] = useState<string | null>(null);
  const [copiedEnv, setCopiedEnv] = useState(false);
  const [syncing, setSyncing] = useState(false);
  const [syncedDone, setSyncedDone] = useState(false);
  const [inlineNote, setInlineNote] = useState<string | null>(null);

  const reload = useCallback(async () => {
    try {
      const res = await api.getOverview();
      setOverview(res);
    } catch {
      // 等待本地网关就绪
    }
  }, []);

  useEffect(() => {
    void reload();
    const timer = window.setInterval(() => {
      void reload();
    }, 3500);
    const unsub = subscribeTrayShown(() => {
      void reload();
    });
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        sendWindowCommand({ type: 'tray:hide' });
      }
    };
    window.addEventListener('keydown', onKey);
    return () => {
      window.clearInterval(timer);
      unsub();
      window.removeEventListener('keydown', onKey);
    };
  }, [reload]);

  useEffect(() => {
    const el = rootRef.current;
    if (!el) return;

    const postHeight = () => {
      const h = Math.ceil(el.getBoundingClientRect().height);
      if (h > 100) {
        sendWindowCommand({ type: 'tray:resize', height: h });
      }
    };
    postHeight();

    if (typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(postHeight);
    ro.observe(el);
    return () => ro.disconnect();
  }, [overview]);

  function flashNote(msg: string) {
    setInlineNote(msg);
    window.setTimeout(() => setInlineNote(prev => (prev === msg ? null : prev)), 2200);
  }

  async function handleToggleSeat(seat: AppSeat) {
    setBusySeatId(seat.id);
    try {
      const next = !seat.enabled;
      await api.updateSeat(seat.id, { enabled: next });
      await reload();
    } finally {
      setBusySeatId(null);
    }
  }

  async function handleQuickTakeover(seat: AppSeat) {
    const takeover = overview?.seatTakeovers?.find(t => t.seatId === seat.id);
    setBusySeatId(seat.id);
    try {
      const res = await api.setSeatTakeover(seat.id, !(takeover?.isTakenOver ?? false));
      await reload();
      flashNote(res.ok ? `${seat.name} ${takeover?.isTakenOver ? '已还原' : '已接管'}` : res.message);
    } finally {
      setBusySeatId(null);
    }
  }

  async function handleCopyEnv() {
    const seats = overview?.seats ?? [];
    const primary = seats.find(s => s.enabled) ?? seats[0];
    if (!primary) return;

    const isMac = overview?.os === 'mac';
    const cmd = isMac
      ? `export HTTPS_PROXY="http://127.0.0.1:${primary.localPort}" HTTP_PROXY="http://127.0.0.1:${primary.localPort}" ALL_PROXY="socks5h://127.0.0.1:${primary.localPort}"`
      : `$env:HTTPS_PROXY="http://127.0.0.1:${primary.localPort}"; $env:HTTP_PROXY="http://127.0.0.1:${primary.localPort}"; $env:ALL_PROXY="socks5h://127.0.0.1:${primary.localPort}"`;

    await navigator.clipboard?.writeText(cmd);
    setCopiedEnv(true);
    window.setTimeout(() => setCopiedEnv(false), 1600);
  }

  async function handleSyncStats() {
    if (syncing) return;
    setSyncing(true);
    try {
      const res = await api.syncStatsNow();
      setSyncedDone(true);
      flashNote(`已同步 ${res.parsedEntries} 条会话记录`);
      window.setTimeout(() => setSyncedDone(false), 1800);
    } finally {
      setSyncing(false);
    }
  }

  const seats = overview?.seats ?? [];
  const nodes = overview?.nodes ?? [];
  const anyEnabled = seats.some(s => s.enabled);
  const lockedNicText = overview?.lockedNic
    ? `${overview.lockedNic.name} (${overview.lockedNic.iPv4Address ?? '在线'})`
    : '自动扫描中';
  const tunnelBypassed = (overview?.excludedVirtualNics?.length ?? 0) > 0;
  const project = overview?.project;

  return (
    <div ref={rootRef} className="tray-panel-shell">
      {/* 顶部标题栏（对标图1：呼吸状态点 + 标题 + 翠绿胶囊徽章） */}
      <div className="tray-header-row">
        <div className="tray-title-group">
          <span className={`tray-live-dot ${anyEnabled ? 'on' : 'idle'}`} />
          <span className="tray-title-text">Agent Proxy Hub · 快捷面板</span>
        </div>
        <span className="tray-mode-badge">
          {overview?.isolation.enableIpv6 ? '物理直出 · IPv6' : '物理直出'}
        </span>
      </div>

      {/* 物理网卡与防串状态行 */}
      <div className="tray-nic-row">
        <span className="tray-nic-left num">
          锁定物理卡: <strong>{lockedNicText}</strong>
        </span>
        <span className="tray-nic-right">
          {tunnelBypassed ? 'Clash / 隧道已绕过' : '系统代理零改动'}
        </span>
      </div>

      {/* AI 席位独立开关列表 */}
      <div className="tray-seat-list">
        {seats.map(seat => {
          const bound = nodes.find(n => n.id === seat.boundNodeId);
          const snap = overview?.trafficSnapshots.find(t => t.seatId === seat.id);
          const takeover = overview?.seatTakeovers?.find(t => t.seatId === seat.id);
          const totalBytes = (snap?.bytesTx ?? 0) + (snap?.bytesRx ?? 0);
          const totalRate = (snap?.bytesTxPerSec ?? 0) + (snap?.bytesRxPerSec ?? 0);

          let subLine: string;
          if (!bound || bound.protocol === 'DIRECT_PHYSICAL') {
            subLine = `127.0.0.1:${seat.localPort} · 物理网卡直出`;
          } else {
            const parts: string[] = [
              bound.exitIp !== '—' ? bound.exitIp : `${bound.host}:${bound.port}`,
              bound.group || bound.protocol
            ];
            if (bound.latencyMs) parts.push(`${bound.latencyMs}ms`);
            subLine = parts.join(' · ');
          }

          if (totalRate > 0 && snap) {
            subLine += ` · 实时 ↑${formatRate(snap.bytesTxPerSec ?? 0)} ↓${formatRate(snap.bytesRxPerSec ?? 0)}`;
          } else if (totalBytes > 0 && snap) {
            subLine += ` · ↑${formatBytes(snap.bytesTx)} ↓${formatBytes(snap.bytesRx)}`;
          }

          return (
            <div key={seat.id} className={`tray-seat-item ${seat.enabled ? 'is-on' : ''}`}>
              <div className="tray-seat-meta">
                <div className="tray-seat-title-line">
                  <span className="tray-seat-name">{TRAY_SEAT_TITLES[seat.id] ?? seat.name}</span>
                  {takeover?.supportsConfigTakeover && (
                    <button
                      type="button"
                      className={`tray-takeover-chip ${takeover.isTakenOver ? 'taken' : ''}`}
                      disabled={busySeatId === seat.id}
                      title={takeover.statusHint}
                      onClick={() => handleQuickTakeover(seat)}
                    >
                      {takeover.isTakenOver ? '已接管' : '接管'}
                    </button>
                  )}
                </div>
                <div className="tray-seat-sub num">{subLine}</div>
              </div>

              <label className="tray-green-switch" title={`启停 ${seat.name} (:${seat.localPort})`}>
                <input
                  type="checkbox"
                  checked={seat.enabled}
                  disabled={busySeatId === seat.id}
                  onChange={() => handleToggleSeat(seat)}
                />
                <span className="tray-green-slider" />
              </label>
            </div>
          );
        })}
      </div>

      {/* 主操作按钮栏（对标图1：复制变量 + 控制台 + 收起） */}
      <div className="tray-actions-row">
        <button
          type="button"
          className={`tray-btn-secondary ${copiedEnv ? 'is-copied' : ''}`}
          onClick={handleCopyEnv}
        >
          {copiedEnv && <CheckTinyIcon size={12} />}
          <span>{copiedEnv ? '已复制代理变量' : '复制终端代理变量'}</span>
        </button>

        <button
          type="button"
          className="tray-btn-console"
          onClick={() => sendWindowCommand({ type: 'tray:openMain' })}
        >
          控制台
        </button>

        <button
          type="button"
          className="tray-btn-dark"
          onClick={() => sendWindowCommand({ type: 'tray:hide' })}
        >
          收起
        </button>
      </div>

      {/* 极简底部工具栏：替代传统系统右键菜单中的同步、仓库与彻底退出 */}
      <div className="tray-footer-bar">
        <div className="tray-footer-left">
          <button
            type="button"
            className="tray-link-btn"
            disabled={syncing}
            onClick={handleSyncStats}
          >
            {syncing ? <RefreshSpinIcon size={11} spinning /> : null}
            <span>{syncedDone ? '已同步统计' : (syncing ? '同步中...' : '同步 AI 用量')}</span>
          </button>
          <span className="tray-footer-sep">·</span>
          <button
            type="button"
            className="tray-link-btn"
            onClick={() => {
              sendWindowCommand({
                type: 'window:openUrl',
                url: project?.repoUrl ?? 'https://github.com/HeyoCode/Agent_Proxy_Hub'
              });
              sendWindowCommand({ type: 'tray:hide' });
            }}
          >
            <GitHubIcon size={11} />
            <span>v{project?.version ?? '0.3.4'}</span>
          </button>
        </div>

        <button
          type="button"
          className="tray-link-btn danger"
          onClick={() => sendWindowCommand({ type: 'tray:exitApp' })}
        >
          彻底退出
        </button>
      </div>

      {inlineNote && <div className="tray-inline-note">{inlineNote}</div>}
    </div>
  );
};
