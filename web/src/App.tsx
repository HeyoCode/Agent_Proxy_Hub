import React, { useCallback, useEffect, useState } from 'react';
import { OverviewResponse, api } from './api';
import {
  AppsNavIcon,
  BrandLogoIcon,
  GitHubIcon,
  IsolationNavIcon,
  MoonIcon,
  NodesNavIcon,
  OverviewNavIcon,
  SunIcon,
  SystemThemeIcon,
  UsageNavIcon
} from './components/Icons';
import {
  ResolvedTheme,
  ThemePreference,
  applyResolvedTheme,
  loadThemePreference,
  nextThemePreference,
  resolveTheme,
  saveThemePreference
} from './theme';
import { RightInspector } from './components/RightInspector';
import { AppsCliView } from './views/AppsCliView';
import { IsolationView } from './views/IsolationView';
import { NodesView } from './views/NodesView';
import { OverviewView } from './views/OverviewView';
import { UsageStatsView } from './views/UsageStatsView';
import {
  WindowResizeHandles,
  isDesktopShell,
  sendWindowCommand,
  startWindowDrag,
  subscribeMaximizedState,
  toggleWindowMaximizeOnDoubleClick
} from './windowBridge';

type TabKey = 'overview' | 'usage' | 'nodes' | 'apps' | 'isolation';

const FALLBACK_PROJECT = {
  name: 'Agent Proxy Hub',
  version: '0.4.3',
  repoOwner: 'HeyoCode',
  repoName: 'Agent_Proxy_Hub',
  repoSlug: 'HeyoCode/Agent_Proxy_Hub',
  repoUrl: 'https://github.com/HeyoCode/Agent_Proxy_Hub',
  cloneUrl: 'https://github.com/HeyoCode/Agent_Proxy_Hub.git',
  author: 'Heyo (@HeyoCode)',
  description: 'AI 专用独立出口代理与双源用量统计桌面网关 (Windows / macOS)'
};

const TAB_TITLES: Record<TabKey, string> = {
  overview: '控制台',
  usage: '用量统计',
  nodes: '独立线路池',
  apps: '应用接管与 CLI',
  isolation: '网卡隔离与熔断'
};

export const App: React.FC = () => {
  const query = new URLSearchParams(window.location.search);
  const initialTab = (query.get('view') as TabKey) || 'overview';

  const [tab, setTab] = useState<TabKey>(
    ['overview', 'usage', 'nodes', 'apps', 'isolation'].includes(initialTab) ? initialTab : 'overview'
  );
  const [themePref, setThemePref] = useState<ThemePreference>(() => loadThemePreference());
  const [resolvedTheme, setResolvedTheme] = useState<ResolvedTheme>(() => resolveTheme(loadThemePreference()));
  const [overview, setOverview] = useState<OverviewResponse | null>(null);
  const [selectedSeatId, setSelectedSeatId] = useState<string>('claude');
  const [throughputMode, setThroughputMode] = useState<'realtime' | 'cumulative'>(() => {
    try {
      return window.localStorage.getItem('aph_throughput_mode') === 'cumulative' ? 'cumulative' : 'realtime';
    } catch {
      return 'realtime';
    }
  });
  const [maximized, setMaximized] = useState<boolean>(false);
  const [showManual, setShowManual] = useState(query.get('manual') === '1');
  const [showBatch, setShowBatch] = useState(query.get('batch') === '1');
  const [showRepoModal, setShowRepoModal] = useState(query.get('about') === '1');
  const [toastMsg, setToastMsg] = useState<string | null>(null);

  useEffect(() => {
    const apply = () => {
      const next = resolveTheme(themePref);
      setResolvedTheme(next);
      applyResolvedTheme(next);
    };
    apply();
    if (themePref !== 'system' || !window.matchMedia) return;
    const mql = window.matchMedia('(prefers-color-scheme: dark)');
    mql.addEventListener?.('change', apply);
    return () => mql.removeEventListener?.('change', apply);
  }, [themePref]);

  useEffect(() => {
    return subscribeMaximizedState(setMaximized);
  }, []);

  const toast = useCallback((msg: string) => {
    setToastMsg(msg);
    window.setTimeout(() => setToastMsg(prev => (prev === msg ? null : prev)), 2600);
  }, []);

  const reloadOverview = useCallback(async () => {
    const res = await api.getOverview();
    setOverview(res);
  }, []);

  const handleChangeThroughputMode = useCallback((mode: 'realtime' | 'cumulative') => {
    setThroughputMode(mode);
    try {
      window.localStorage.setItem('aph_throughput_mode', mode);
    } catch { }
  }, []);

  useEffect(() => {
    void reloadOverview();
    const intervalMs = throughputMode === 'realtime' ? 1500 : 4000;
    const timer = window.setInterval(() => {
      void reloadOverview();
    }, intervalMs);
    return () => window.clearInterval(timer);
  }, [reloadOverview, throughputMode]);

  useEffect(() => {
    if (!overview?.seats.length) return;
    if (!overview.seats.some(s => s.id === selectedSeatId)) {
      setSelectedSeatId(overview.seats[0].id);
    }
  }, [overview, selectedSeatId]);

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
    if (isDesktopShell()) {
      sendWindowCommand({ type: 'window:openUrl', url: project.repoUrl });
      toast(`已在系统浏览器打开 ${project.repoSlug}`);
      return;
    }
    try {
      const res = await api.openRepo();
      if (res.opened) {
        toast(`已在系统浏览器打开 ${project.repoSlug}`);
        return;
      }
    } catch {
      // ignore and fall back
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
    : '扫描中';
  const activeSeatsCount = overview?.seats.filter(s => s.enabled).length ?? 0;
  const totalSeatsCount = overview?.seats.length ?? 4;
  const bypassedCount = overview?.excludedVirtualNics.length ?? 0;

  const navItems: { key: TabKey; label: string; icon: React.ReactNode }[] = [
    { key: 'overview', label: '控制台', icon: <OverviewNavIcon /> },
    { key: 'usage', label: '用量统计', icon: <UsageNavIcon /> },
    { key: 'nodes', label: `独立线路 (${overview?.nodes.length ?? 0})`, icon: <NodesNavIcon /> },
    { key: 'apps', label: '应用接管 & CLI', icon: <AppsNavIcon /> },
    { key: 'isolation', label: '网卡隔离', icon: <IsolationNavIcon /> }
  ];

  const activeNavIdx = Math.max(0, navItems.findIndex(item => item.key === tab));

  return (
    <div className="app-shell">
      <WindowResizeHandles />

      {/* 1. 左侧纯白极简图标栏 (72px) */}
      <aside className="left-rail" aria-label="主导航">
        <div
          className="rail-logo-slot"
          title={`${project.name} v${project.version} · 按住可拖动窗口`}
          onMouseDown={startWindowDrag}
          onDoubleClick={toggleWindowMaximizeOnDoubleClick}
        >
          <BrandLogoIcon size={25} />
        </div>

        <nav className="rail-nav">
          <div
            className="rail-active-indicator"
            style={{ transform: `translate3d(0, ${activeNavIdx * 54}px, 0)` }}
            aria-hidden="true"
          />
          {navItems.map(item => (
            <button
              key={item.key}
              type="button"
              className={`rail-btn ${tab === item.key ? 'active' : ''}`}
              aria-label={item.label}
              onClick={() => setTab(item.key)}
            >
              {item.icon}
              <span className="rail-tooltip">{item.label}</span>
            </button>
          ))}
        </nav>

        <div className="rail-bottom">
          <button
            type="button"
            className="rail-utility-btn"
            aria-label="打开 GitHub 开源项目信息"
            onClick={() => setShowRepoModal(true)}
          >
            <GitHubIcon size={17} />
            <span className="rail-tooltip">{project.repoSlug} v{project.version}</span>
          </button>

          <button
            type="button"
            className="rail-utility-btn"
            aria-label="切换主题模式（跟随系统 / 浅色 / 深色）"
            onClick={() => {
              const next = nextThemePreference(themePref);
              setThemePref(next);
              saveThemePreference(next);
              toast(
                next === 'system'
                  ? `已切换：跟随系统（当前${resolveTheme('system') === 'dark' ? '深色' : '浅色'}）`
                  : next === 'light'
                    ? '已切换：浅色模式'
                    : '已切换：深色模式'
              );
            }}
          >
            {themePref === 'system' ? (
              <SystemThemeIcon size={17} />
            ) : themePref === 'light' ? (
              <SunIcon size={17} />
            ) : (
              <MoonIcon size={17} />
            )}
            <span className="rail-tooltip">
              {themePref === 'system'
                ? `跟随系统（当前${resolvedTheme === 'dark' ? '深色' : '浅色'}）· 点按切浅色`
                : themePref === 'light'
                  ? '浅色模式 · 点按切深色'
                  : '深色模式 · 点按跟随系统'}
            </span>
          </button>
        </div>
      </aside>

      {/* 2. 中间冷灰雾白主画布 */}
      <div className="center-column">
        <header
          className="top-bar"
          onMouseDown={startWindowDrag}
          onDoubleClick={toggleWindowMaximizeOnDoubleClick}
        >
          <h1 className="top-title">{TAB_TITLES[tab]}</h1>

          <div className="top-telemetry-row">
            <span className="top-metric-item">
              <span className="top-dot-live" />
              <span className="top-metric-label">物理网卡:</span>
              <span className="top-metric-val num">{lockedNicLabel}</span>
            </span>
            <span className="top-metric-item">
              <span className="top-metric-label">已绕过隧道:</span>
              <span className="top-metric-val num">{bypassedCount} 个</span>
            </span>
            <span className="top-metric-item">
              <span className="top-metric-label">活跃席位:</span>
              <span className="top-metric-val num">{activeSeatsCount}/{totalSeatsCount}</span>
            </span>
            <span className="top-metric-item">
              <span className="top-metric-label">IPv6:</span>
              <span
                className="top-metric-val"
                style={{ color: overview?.isolation.enableIpv6 ? 'var(--accent-indigo)' : 'var(--text-secondary)' }}
              >
                {overview?.isolation.enableIpv6 ? '已开启' : '已关闭'}
              </span>
            </span>
            <span className="top-metric-item">
              <span className="top-metric-label">内存:</span>
              <span className="top-metric-val num">{overview?.memoryMb ?? '—'} MB</span>
            </span>
          </div>
        </header>

        <main className="canvas-scroll">
          {!overview ? (
            <div className="surface-card surface-card-pad" style={{ textAlign: 'center', color: 'var(--text-secondary)' }}>
              正在连接 Agent Proxy Hub 本地核心网关...
            </div>
          ) : (
            <div key={tab} className="view-stage-enter">
              {tab === 'overview' && (
                <OverviewView
                  data={overview}
                  selectedSeatId={selectedSeatId}
                  onSelectSeat={setSelectedSeatId}
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
              {tab === 'apps' && <AppsCliView data={overview} onReload={reloadOverview} onToast={toast} />}
              {tab === 'isolation' && <IsolationView onReloadOverview={reloadOverview} onToast={toast} />}
            </div>
          )}
        </main>
      </div>

      {/* 3. 右侧午夜深蓝常驻检视栏 (308px) */}
      <RightInspector
        project={project}
        overview={overview}
        selectedSeatId={selectedSeatId}
        throughputMode={throughputMode}
        onChangeThroughputMode={handleChangeThroughputMode}
        maximized={maximized}
        onReload={reloadOverview}
        onToast={toast}
        onOpenBatch={() => { setTab('nodes'); setShowBatch(true); }}
      />

      {/* 单层极简 GitHub 项目信息浮窗 */}
      {showRepoModal && (
        <div className="modal-backdrop" onClick={() => setShowRepoModal(false)}>
          <div className="modal-surface narrow" style={{ maxWidth: 540 }} onClick={e => e.stopPropagation()}>
            <div style={{ padding: '20px 22px 16px', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                <div
                  style={{
                    width: 42,
                    height: 42,
                    borderRadius: 12,
                    background: 'var(--bg-subtle)',
                    display: 'flex',
                    alignItems: 'center',
                    justifyContent: 'center',
                    color: 'var(--text-primary)'
                  }}
                >
                  <GitHubIcon size={20} />
                </div>
                <div>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                    <strong style={{ fontSize: 15.5 }}>{project.name}</strong>
                    <span className="pill-tag tag-indigo num">v{project.version}</span>
                  </div>
                  <div className="num" style={{ fontSize: 12, color: 'var(--text-muted)', marginTop: 2 }}>
                    <span className="mono">{project.repoSlug}</span> · {project.author}
                  </div>
                </div>
              </div>
              <button type="button" className="btn-pill btn-soft btn-sm" onClick={() => setShowRepoModal(false)}>关闭</button>
            </div>

            <div className="soft-divider" />

            <div style={{ padding: '16px 22px', display: 'flex', flexDirection: 'column', gap: 12 }}>
              <div style={{ fontSize: 12.5, color: 'var(--text-secondary)', lineHeight: 1.6 }}>
                {project.description}。支持物理网卡直绑绕过 Clash/Wintun、Fake-IP TLS SNI 自动还原、全协议静态代理调度与免解密双源 AI 用量统计。
              </div>

              <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
                <span className="pill-tag tag-emerald">IP_UNICAST_IF / IP_BOUND_IF 直绑</span>
                <span className="pill-tag tag-indigo mono">SOCKS5 / HTTP / HTTPS</span>
                <span className="pill-tag tag-neutral">Windows &amp; macOS</span>
              </div>

              <div className="code-surface" style={{ padding: '11px 14px' }}>
                {`git clone ${project.cloneUrl}`}
              </div>
            </div>

            <div className="modal-footer-bar" style={{ justifyContent: 'space-between' }}>
              <div style={{ display: 'flex', gap: 8 }}>
                <button type="button" className="btn-pill btn-soft btn-sm" onClick={() => handleCopyText(project.repoUrl, '仓库链接')}>复制链接</button>
                <button type="button" className="btn-pill btn-soft btn-sm" onClick={() => handleCopyText(`git clone ${project.cloneUrl}`, ' Clone 命令')}>复制 Clone</button>
              </div>
              <button type="button" className="btn-pill btn-indigo" onClick={handleOpenGitHub}>
                <GitHubIcon size={14} />
                <span>打开 GitHub 仓库</span>
              </button>
            </div>
          </div>
        </div>
      )}

      {toastMsg && <div className="toast-float">{toastMsg}</div>}
    </div>
  );
};
