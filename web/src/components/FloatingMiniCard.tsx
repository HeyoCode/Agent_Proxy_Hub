import React, { useCallback, useEffect, useRef, useState } from 'react';
import { api, OverviewResponse, connectOverviewStream, formatBytes, formatRate } from '../api';
import { isDesktopShell, subscribeMiniVisibility } from '../windowBridge';
import { BrandLogoIcon, SeatBrandIcon, WinCloseIcon } from './Icons';

export const FloatingMiniCard: React.FC = () => {
  const inFlightRef = useRef(false);
  const [visible, setVisible] = useState<boolean>(
    () => !isDesktopShell() || new URLSearchParams(window.location.search).get('shown') === '1'
  );
  const [data, setData] = useState<OverviewResponse | null>(null);
  const [privacyMode, setPrivacyMode] = useState<boolean>(() => localStorage.getItem('aph_privacy_mode') === '1');
  const [busyId, setBusyId] = useState<string | null>(null);

  const load = useCallback(async () => {
    if (inFlightRef.current) return;
    inFlightRef.current = true;
    try {
      const ov = await api.getOverview();
      setData(ov);
    } catch {
      // 等待网关响应
    } finally {
      inFlightRef.current = false;
    }
  }, []);

  useEffect(() => {
    const unsubVis = subscribeMiniVisibility(nextVisible => {
      setVisible(nextVisible);
      if (nextVisible) void load();
    });
    const onStorage = (ev: StorageEvent) => {
      if (ev.key === 'aph_privacy_mode') setPrivacyMode(ev.newValue === '1');
    };
    window.addEventListener('storage', onStorage);
    return () => {
      unsubVis();
      window.removeEventListener('storage', onStorage);
    };
  }, [load]);

  useEffect(() => {
    if (!visible) return;
    void load();
    const unsubWs = connectOverviewStream(ov => setData(ov));
    const t = window.setInterval(() => {
      if (!document.hidden) {
        void load();
      }
    }, 20000);
    return () => {
      unsubWs();
      window.clearInterval(t);
    };
  }, [visible, load]);

  const seats = data?.seats ?? [];
  const activeSeats = seats.filter(s => s.enabled && !s.isHidden).slice(0, 3);
  const snaps = data?.trafficSnapshots ?? [];
  const txSec = snaps.reduce((acc, s) => acc + (s.bytesTxPerSec ?? 0), 0);
  const rxSec = snaps.reduce((acc, s) => acc + (s.bytesRxPerSec ?? 0), 0);
  const activeConns = snaps.reduce((acc, s) => acc + (s.activeConnections ?? 0), 0);

  const postMini = (msg: Record<string, unknown>) => {
    window.chrome?.webview?.postMessage(msg);
  };

  const handleToggleSeat = async (seatId: string, currentEnabled: boolean) => {
    setBusyId(seatId);
    try {
      await api.updateSeat(seatId, { enabled: !currentEnabled });
      await load();
    } finally {
      setBusyId(null);
    }
  };

  useEffect(() => {
    const h = 114 + Math.max(1, activeSeats.length) * 26;
    postMini({ type: 'mini:resize', height: h });
  }, [activeSeats.length]);

  return (
    <div className="mini-card-shell">
      <div
        className="mini-card-header"
        onMouseDown={e => {
          if (e.button === 0 && !(e.target as HTMLElement).closest('button')) {
            postMini({ type: 'mini:drag' });
          }
        }}
      >
        <div className="mini-card-brand">
          <BrandLogoIcon size={14} />
          <span>Agent Proxy Hub</span>
          <span className={`mini-live-dot ${activeSeats.length > 0 ? 'on' : ''}`} />
        </div>
        <div className="mini-card-controls">
          <button
            type="button"
            className="mini-ctrl-btn"
            title="回到主控制台"
            onClick={() => postMini({ type: 'mini:openMain' })}
          >
            主页
          </button>
          <button
            type="button"
            className="mini-ctrl-btn mini-close"
            title="隐藏悬浮监控卡片"
            onClick={() => postMini({ type: 'mini:close' })}
          >
            <WinCloseIcon />
          </button>
        </div>
      </div>

      <div className="mini-metrics-row num">
        <span>实时 ↑{formatRate(txSec)} ↓{formatRate(rxSec)}</span>
        <span className="pill-tag tag-indigo" style={{ padding: '1px 6px', fontSize: 10 }}>
          {activeConns} 并发
        </span>
      </div>

      <div className="mini-seat-list">
        {activeSeats.length === 0 ? (
          <div className="mini-empty-row">当前暂无开启的独立出口席位</div>
        ) : (
          activeSeats.map(s => {
            const snap = snaps.find(t => t.seatId === s.id);
            const bound = data?.nodes.find(n => n.id === s.boundNodeId);
            return (
              <div key={s.id} className="mini-seat-item">
                <div style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0 }}>
                  <SeatBrandIcon seatId={s.id} size={13} />
                  <span className="mini-seat-name">{s.name}</span>
                  <span className="mono" style={{ fontSize: 10, color: 'var(--text-muted)' }}>:{s.localPort}</span>
                </div>
                <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                  <span className="num" style={{ fontSize: 10.5, color: 'var(--text-secondary)' }}>
                    {privacyMode ? '•••' : (snap ? formatBytes(snap.bytesTx + snap.bytesRx) : (bound?.exitIp ?? '直出'))}
                  </span>
                  <label className="mini-tiny-switch">
                    <input
                      type="checkbox"
                      checked={s.enabled}
                      disabled={busyId === s.id}
                      onChange={() => handleToggleSeat(s.id, s.enabled)}
                    />
                    <span className="mini-tiny-slider" />
                  </label>
                </div>
              </div>
            );
          })
        )}
      </div>
    </div>
  );
};
