import React, { useCallback, useEffect, useState } from 'react';
import { OverviewResponse, api } from './api';
import { OverviewView } from './views/OverviewView';
import { UsageStatsView } from './views/UsageStatsView';
import { NodesView } from './views/NodesView';
import { AppsCliView } from './views/AppsCliView';
import { IsolationView } from './views/IsolationView';

type TabKey = 'overview' | 'usage' | 'nodes' | 'apps' | 'isolation';

const FALLBACK_PROJECT = {
  name: 'Agent Proxy Hub',
  version: '0.2.0',
  repoOwner: 'HeyoCode',
  repoName: 'Agent_Proxy_Hub',
  repoSlug: 'HeyoCode/Agent_Proxy_Hub',
  repoUrl: 'https://github.com/HeyoCode/Agent_Proxy_Hub',
  cloneUrl: 'https://github.com/HeyoCode/Agent_Proxy_Hub.git',
  author: 'Heyo (@HeyoCode)',
  description: 'AI 专用独立出口代理与双源用量统计桌面网关 (Windows / macOS)'
};

const GitHubMark: React.FC<{ size?: number }> = ({ size = 14 }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
    <path d="M12 .5C5.65.5.5 5.65.5 12c0 5.08 3.29 9.39 7.86 10.91.57.1.78-.25.78-.55 0-.27-.01-1.17-.02-2.12-3.2.7-3.88-1.36-3.88-1.36-.52-1.33-1.28-1.68-1.28-1.68-1.05-.72.08-.7.08-.7 1.16.08 1.77 1.19 1.77 1.19 1.03 1.77 2.7 1.26 3.36.96.1-.75.4-1.26.73-1.55-2.55-.29-5.24-1.28-5.24-5.68 0-1.26.45-2.28 1.19-3.08-.12-.29-.52-1.46.11-3.05 0 0 .97-.31 3.18 1.18a11.1 11.1 0 0 1 5.79 0c2.21-1.49 3.18-1.18 3.18-1.18.63 1.59.23 2.76.11 3.05.74.8 1.19 1.82 1.19 3.08 0 4.41-2.69 5.39-5.25 5.67.41.36.78 1.06.78 2.14 0 1.54-.01 2.79-.01 3.17 0 .31.21.66.79.55A11.51 11.51 0 0 0 23.5 12C23.5 5.65 18.35.5 12 .5Z" />
  </svg>
);

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
  const [showRepoModal, setShowRepoModal] = useState(query.get('about') === '1');
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

  const project = overview?.project ?? FALLBACK_PROJECT;

  async function handleOpenGitHub() {
    try {
      const res = await api.openRepo();
      if (res.opened) {
        toast(`已在系统浏览器打开 ${project.repoSlug}`);
        return;
      }
    } catch {
      // ignore and fall back to window.open
    }
    window.open(project.repoUrl, '_blank', 'noopener,noreferrer');
    toast(`已打开 ${project.repoSlug}`);
  }

  async function handleCopyText(text: string, label: string) {
    await navigator.clipboard?.writeText(text);
    toast(`已复制${label}`);
  }

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
            <strong style={{ fontSize: 13.5, letterSpacing: '-0.02em' }}>{project.name}</strong>
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

        <div style={{ display: 'flex', justifyContent: 'flex-end', alignItems: 'center', gap: 8 }}>
          <button
            className="gh-capsule"
            onClick={() => setShowRepoModal(true)}
            title={`查看 GitHub 项目仓库 ${project.repoSlug}`}
          >
            <GitHubMark size={14} />
            <span className="mono" style={{ fontWeight: 700 }}>{project.repoSlug}</span>
            <span className="gh-ver-tag mono">v{project.version}</span>
          </button>
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
              <div className="tx-sep" style={{ marginTop: 2 }} />
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', fontSize: 11, color: 'var(--text-secondary)' }}>
                <span style={{ display: 'inline-flex', alignItems: 'center', gap: 5 }}>
                  <GitHubMark size={12} />
                  <span className="mono">{project.repoSlug}</span>
                </span>
                <button className="tx-btn tx-btn-sec tx-btn-sm" style={{ height: 22, fontSize: 11 }} onClick={() => { setShowTray(false); setShowRepoModal(true); }}>
                  项目详情
                </button>
              </div>
            </div>
          </div>
        </div>
      )}

      {/* Cult UI 双层同心圆角 GitHub 项目铭牌弹窗 */}
      {showRepoModal && (
        <div className="modal-mask" onClick={() => setShowRepoModal(false)}>
          <div className="modal-tray narrow" style={{ maxWidth: 580 }} onClick={e => e.stopPropagation()}>
            <div className="modal-card">
              <div style={{ padding: '16px 20px', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                  <div style={{
                    width: 40, height: 40, borderRadius: 12, background: 'var(--card-subtle)',
                    border: '1px solid var(--card-border)', display: 'flex', alignItems: 'center', justifyContent: 'center'
                  }}>
                    <GitHubMark size={20} />
                  </div>
                  <div>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                      <strong style={{ fontSize: 15 }}>{project.name}</strong>
                      <span className="gh-ver-tag mono">v{project.version}</span>
                      <span className="badge badge-neutral">GitHub 仓库</span>
                    </div>
                    <div className="mono" style={{ fontSize: 12, color: 'var(--text-secondary)', marginTop: 2 }}>
                      {project.repoSlug} · 维护者 {project.author}
                    </div>
                  </div>
                </div>
                <button className="tx-btn tx-btn-sec tx-btn-sm" onClick={() => setShowRepoModal(false)}>关闭</button>
              </div>

              <div className="tx-sep" />

              <div style={{ padding: '15px 20px', display: 'flex', flexDirection: 'column', gap: 12 }}>
                <div style={{ fontSize: 12.5, color: 'var(--text-secondary)', lineHeight: 1.55 }}>
                  {project.description}。集成物理网卡直绑隔离（绕过 Clash / Wintun / utun）、TLS SNI Fake-IP 还原、全协议代理调度与免解密双源 AI 用量统计。
                </div>

                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
                  <span className="badge badge-emerald">IP_UNICAST_IF / IP_BOUND_IF 直绑</span>
                  <span className="badge badge-indigo">SOCKS5 / HTTP / HTTPS</span>
                  <span className="badge badge-sky">CC Switch 级双源统计</span>
                  <span className="badge badge-neutral">Windows &amp; macOS</span>
                </div>

                <div className="rule-well" style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                  <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', fontSize: 11.5 }}>
                    <span style={{ color: 'var(--text-muted)', fontWeight: 600 }}>GitHub 仓库地址</span>
                    <span className="mono" style={{ color: 'var(--text-primary)', fontWeight: 600 }}>{project.repoUrl}</span>
                  </div>
                  <div className="terminal-well" style={{ padding: '9px 11px', fontSize: 11.5 }}>
                    {`git clone ${project.cloneUrl}`}
                  </div>
                </div>
              </div>

              <div className="tx-sep" />

              <div style={{ padding: '12px 20px', background: 'var(--card-subtle)', display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8 }}>
                <div style={{ display: 'flex', gap: 8 }}>
                  <button className="tx-btn tx-btn-sec tx-btn-sm" onClick={() => handleCopyText(project.repoUrl, '仓库链接')}>复制链接</button>
                  <button className="tx-btn tx-btn-sec tx-btn-sm" onClick={() => handleCopyText(`git clone ${project.cloneUrl}`, ' git clone 命令')}>复制 Clone 命令</button>
                </div>
                <button className="tx-btn tx-btn-primary" onClick={handleOpenGitHub}>
                  <GitHubMark size={13} />
                  <span>在浏览器打开 GitHub ↗</span>
                </button>
              </div>
            </div>
          </div>
        </div>
      )}

      {/* 底部通栏项目铭牌状态条 */}
      <footer className="status-footer">
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <span><strong>{project.name}</strong> <span className="mono">v{project.version}</span></span>
          <span style={{ opacity: 0.4 }}>·</span>
          <span>物理网卡硬件直绑隔离 · 系统全局代理零修改</span>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <button
            className="footer-gh-link"
            onClick={() => setShowRepoModal(true)}
            title="打开 GitHub 项目铭牌"
          >
            <GitHubMark size={12} />
            <span className="mono">github.com/{project.repoSlug}</span>
            <span style={{ color: 'var(--emerald-text)', fontWeight: 700 }}>↗</span>
          </button>
          <button
            className="tx-btn tx-btn-sec tx-btn-sm"
            style={{ height: 22, fontSize: 11, padding: '0 8px' }}
            onClick={() => handleCopyText(project.repoUrl, ' GitHub 仓库地址')}
          >
            复制地址
          </button>
        </div>
      </footer>

      {toastMsg && <div className="toast-pill">{toastMsg}</div>}
    </div>
  );
};
