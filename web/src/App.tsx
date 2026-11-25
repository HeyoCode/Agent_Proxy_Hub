import React, { useCallback, useEffect, useState } from 'react';
import { OverviewResponse, api } from './api';
import { OverviewView } from './views/OverviewView';
import { UsageStatsView } from './views/UsageStatsView';
import { NodesView } from './views/NodesView';
import { AppsCliView } from './views/AppsCliView';
import { IsolationView } from './views/IsolationView';

type TabKey = 'overview' | 'usage' | 'nodes' | 'apps' | 'isolation';

export const App: React.FC = () => {
  const query = new URLSearchParams(window.location.search);
  const initialTab = (query.get('view') as TabKey) || 'overview';
  const initialTheme = query.get('theme') === 'light' ? 'light' : 'dark';

  const [tab, setTab] = useState<TabKey>(
    ['overview', 'usage', 'nodes', 'apps', 'isolation'].includes(initialTab) ? initialTab : 'overview'
  );
  const [theme, setTheme] = useState<'dark' | 'light'>(initialTheme);
  const [overview, setOverview] = useState<OverviewResponse | null>(null);
  const [showManual, setShowManual] = useState(false);
  const [showBatch, setShowBatch] = useState(false);
  const [showTray, setShowTray] = useState(false);
  const [toastMsg, setToastMsg] = useState<string | null>(null);

  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme);
  }, [theme]);

  const toast = useCallback((msg: string) => {
    setToastMsg(msg);
    window.setTimeout(() => setToastMsg(prev => (prev === msg ? null : prev)), 2500);
  }, []);

  const reloadOverview = useCallback(async () => {
    const res = await api.getOverview();
    setOverview(res);
  }, []);

  useEffect(() => {
    void reloadOverview();
    const timer = window.setInterval(() => {
      void reloadOverview();
    }, 5000);
    return () => window.clearInterval(timer);
  }, [reloadOverview]);

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (!(e.ctrlKey || e.metaKey)) return;
      const map: Record<string, TabKey> = {
        '1': 'overview',
        '2': 'usage',
        '3': 'nodes',
        '4': 'apps',
        '5': 'isolation'
      };
      if (map[e.key]) {
        e.preventDefault();
        setTab(map[e.key]);
      }
    }
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);

  const lockedNicLabel = overview?.lockedNic
    ? `${overview.lockedNic.name} (${overview.lockedNic.iPv4Address ?? '在线'})`
    : '扫描物理网卡中...';

  return (
    <div className="app-root">
      {/* 顶栏 + Cult UI 灵动岛状态胶囊 */}
      <header className="topbar">
        <div className="brand-lockup">
          <div className="brand-mark">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none">
              <path d="M12 2.5L4.5 6.3v5.4c0 4.8 3.2 9.1 7.5 10.3 4.3-1.2 7.5-5.5 7.5-10.3V6.3L12 2.5z" stroke="#10B981" strokeWidth="2.1" />
              <circle cx="12" cy="11.8" r="2.6" fill="currentColor" />
            </svg>
          </div>
          <div>
            <strong style={{ fontSize: 13.5, letterSpacing: '-0.02em' }}>Agent Proxy Hub</strong>
            <span className="mono" style={{ fontSize: 11, color: 'var(--text-muted)', marginLeft: 7 }}>
              .NET 10 Core · {overview?.memoryMb ?? '—'} MB
            </span>
          </div>
        </div>

        <div className="dynamic-island">
          <span className="pulse-dot" />
          <span>物理直出：<strong>{lockedNicLabel}</strong></span>
          <span className="island-sep" />
          <span className={overview?.hasDetectedTunnel ? 'island-bypass' : 'island-clean'}>
            {overview?.hasDetectedTunnel
              ? `已绕过 ${overview.excludedVirtualNics.length} 个虚拟隧道网卡 · 0 B`
              : '直绑物理硬件网卡 · 隔离稳态'}
          </span>
        </div>

        <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8 }}>
          <button
            className="tx-btn tx-btn-sec tx-btn-sm"
            onClick={() => setTheme(t => (t === 'dark' ? 'light' : 'dark'))}
          >
            {theme === 'dark' ? '☀ 浅色' : '☾ 深色'}
          </button>
          <button
            className="tx-btn tx-btn-sec tx-btn-sm"
            onClick={() => setShowTray(v => !v)}
          >
            <span style={{ width: 6, height: 6, borderRadius: '50%', background: '#10B981' }} />
            <span>托盘快捷窗</span>
          </button>
        </div>
      </header>

      {/* 悬浮胶囊分段导航 Dock */}
      <div className="dock-row">
        <div className="dock-tray">
          <button className={`dock-tab ${tab === 'overview' ? 'active' : ''}`} onClick={() => setTab('overview')}>
            <span>专线席位</span><span className="kbd">⌘1</span>
          </button>
          <button className={`dock-tab ${tab === 'usage' ? 'active' : ''}`} onClick={() => setTab('usage')}>
            <span>用量统计</span><span className="kbd">⌘2</span>
          </button>
          <button className={`dock-tab ${tab === 'nodes' ? 'active' : ''}`} onClick={() => setTab('nodes')}>
            <span>线路池 ({overview?.nodes.length ?? 0})</span><span className="kbd">⌘3</span>
          </button>
          <button className={`dock-tab ${tab === 'apps' ? 'active' : ''}`} onClick={() => setTab('apps')}>
            <span>进程与 CLI</span><span className="kbd">⌘4</span>
          </button>
          <button className={`dock-tab ${tab === 'isolation' ? 'active' : ''}`} onClick={() => setTab('isolation')}>
            <span>网卡隔离</span><span className="kbd">⌘5</span>
          </button>
        </div>

        <div style={{ display: 'flex', gap: 8 }}>
          <button className="tx-btn tx-btn-sec" onClick={() => { setTab('nodes'); setShowManual(true); }}>+ 手动添加</button>
          <button className="tx-btn tx-btn-primary" onClick={() => { setTab('nodes'); setShowBatch(true); }}>批量导入线路</button>
        </div>
      </div>

      {/* 主滚动画布 */}
      <main className="main-scroll">
        {!overview ? (
          <div className="tx-tray">
            <div className="tx-card tx-card-pad" style={{ textAlign: 'center', color: 'var(--text-secondary)' }}>
              正在连接 Agent Proxy Hub 本地引擎...
            </div>
          </div>
        ) : (
          <>
            {tab === 'overview' && (
              <OverviewView
                data={overview}
                onReload={reloadOverview}
                onToast={toast}
                onOpenBatch={() => { setTab('nodes'); setShowBatch(true); }}
                onGoStats={() => setTab('usage')}
              />
            )}
            {tab === 'usage' && <UsageStatsView onToast={toast} />}
            {tab === 'nodes' && (
              <NodesView
                nodes={overview.nodes}
                showManual={showManual}
                showBatch={showBatch}
                onCloseModals={() => { setShowManual(false); setShowBatch(false); }}
                onOpenManual={() => setShowManual(true)}
                onOpenBatch={() => setShowBatch(true)}
                onReload={reloadOverview}
                onToast={toast}
              />
            )}
            {tab === 'apps' && <AppsCliView data={overview} onToast={toast} />}
            {tab === 'isolation' && <IsolationView onReloadOverview={reloadOverview} onToast={toast} />}
          </>
        )}
      </main>

      {/* 右上角托盘悬浮小窗 */}
      {showTray && overview && (
        <div className="floating-tray">
          <div className="floating-card">
            <div style={{ padding: '11px 14px', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 7 }}>
                <span className="pulse-dot" />
                <strong style={{ fontSize: 12.5 }}>Agent Proxy Hub · 快捷面板</strong>
              </div>
              <span className="badge badge-emerald">物理直出</span>
            </div>
            <div className="tx-sep" />
            <div style={{ padding: '11px 14px', display: 'flex', flexDirection: 'column', gap: 8 }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11.5, color: 'var(--text-secondary)' }}>
                <span>物理网卡：<strong>{overview.lockedNic?.name ?? '—'}</strong></span>
                <span className="mono">{overview.memoryMb} MB</span>
              </div>
              {overview.seats.slice(0, 3).map(seat => {
                const bound = overview.nodes.find(n => n.id === seat.boundNodeId);
                return (
                  <div className="rule-well" key={seat.id} style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '7px 10px' }}>
                    <div>
                      <div style={{ fontWeight: 600, fontSize: 12 }}>{seat.name} (:{seat.localPort})</div>
                      <div className="mono" style={{ fontSize: 11, color: 'var(--text-secondary)' }}>{bound?.exitIp ?? '—'} · {bound?.name ?? '未绑定'}</div>
                    </div>
                    <span className={`badge ${seat.enabled ? 'badge-emerald' : 'badge-neutral'}`}>{seat.enabled ? '在线' : '关闭'}</span>
                  </div>
                );
              })}
              <div style={{ display: 'flex', gap: 8, marginTop: 2 }}>
                <button className="tx-btn tx-btn-sec tx-btn-sm" style={{ flex: 1 }} onClick={() => { setShowTray(false); setTab('usage'); }}>查看用量统计</button>
                <button className="tx-btn tx-btn-primary tx-btn-sm" onClick={() => setShowTray(false)}>收起</button>
              </div>
            </div>
          </div>
        </div>
      )}

      {toastMsg && <div className="toast-pill">{toastMsg}</div>}
    </div>
  );
};
