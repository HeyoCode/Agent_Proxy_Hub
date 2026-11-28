import React, { useMemo, useState, useEffect, useCallback, useRef } from 'react';
import {
  AgentQuotaCardDto,
  ConversationAuditReport,
  ModelPricingRule,
  QuotaBudgetSettings,
  SeatQuotaRule,
  UsageDashboardResponse,
  api,
  formatBytes,
  formatCompactTokens,
  formatRate
} from '../api';
import { CustomSelect } from '../components/CustomSelect';
import { ClockRangeIcon, CpuModelIcon, PlusIcon, RefreshSpinIcon } from '../components/Icons';
import { SegmentedTabs } from '../components/SegmentedTabs';
import { maskIp } from '../privacy';

interface Props {
  privacyMode?: boolean;
  onToast: (msg: string) => void;
}

type MetricKey = 'cost' | 'requests' | 'tokens' | 'cacheRate';
type SubTab = 'logs' | 'workspaces' | 'models' | 'appsProxy' | 'pricing';

const RANGE_OPTIONS = [
  { value: 'today', label: '当天' },
  { value: '24h', label: '近 24 小时' },
  { value: '7d', label: '近 7 天' },
  { value: '30d', label: '近 30 天' },
  { value: 'all', label: '全部历史' }
];

const PAGE_SIZE_OPTIONS = [
  { value: '20', label: '每页 20 条' },
  { value: '50', label: '每页 50 条' }
];

export const UsageStatsView: React.FC<Props> = ({ privacyMode = false, onToast }) => {
  const urlRange = new URLSearchParams(window.location.search).get('range');
  const [range, setRange] = useState<string>(
    urlRange && ['today', '24h', '7d', '30d', 'all'].includes(urlRange) ? urlRange : 'today'
  );
  const [appFilter, setAppFilter] = useState<string>('all');
  const [modelFilter, setModelFilter] = useState<string>('all');
  const [page, setPage] = useState<number>(1);
  const [pageSize, setPageSize] = useState<number>(20);
  const [activeMetric, setActiveMetric] = useState<MetricKey>('cacheRate');
  const [subTab, setSubTab] = useState<SubTab>('logs');
  const [data, setData] = useState<UsageDashboardResponse | null>(null);
  const [loadingInitial, setLoadingInitial] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [pricings, setPricings] = useState<ModelPricingRule[]>([]);
  const [syncing, setSyncing] = useState(false);
  const [autoSync, setAutoSync] = useState(true);
  const inFlightRef = useRef(false);

  const [pricingDraft, setPricingDraft] = useState<Omit<ModelPricingRule, 'isCustom'> | null>(null);
  const [auditReport, setAuditReport] = useState<ConversationAuditReport | null>(null);
  const [loadingAuditId, setLoadingAuditId] = useState<string | null>(null);
  const [showAllAgents, setShowAllAgents] = useState(false);
  const [quotaModalOpen, setQuotaModalOpen] = useState(false);
  const [quotaDraft, setQuotaDraft] = useState<QuotaBudgetSettings>({
    dailyBudgetUsd: 0,
    monthlyBudgetUsd: 0,
    warnPercent: 65,
    criticalPercent: 85,
    seatRules: {}
  });
  const [savingQuota, setSavingQuota] = useState(false);

  async function openQuotaModal() {
    try {
      const cfg = await api.getQuotaSettings();
      setQuotaDraft({
        dailyBudgetUsd: cfg.dailyBudgetUsd ?? 0,
        monthlyBudgetUsd: cfg.monthlyBudgetUsd ?? 0,
        warnPercent: cfg.warnPercent ?? 65,
        criticalPercent: cfg.criticalPercent ?? 85,
        seatRules: cfg.seatRules ?? {}
      });
    } catch {
      // 使用当前默认草稿
    }
    setQuotaModalOpen(true);
  }

  async function handleSaveQuotaSettings(e: React.FormEvent) {
    e.preventDefault();
    if (savingQuota) return;
    setSavingQuota(true);
    try {
      await api.updateQuotaSettings(quotaDraft);
      setQuotaModalOpen(false);
      await loadAll(page);
      onToast('已更新全局预算护栏与分席位配额规则');
    } catch (err) {
      onToast(err instanceof Error ? err.message : '保存配额规则失败');
    } finally {
      setSavingQuota(false);
    }
  }

  async function openConversationAudit(conversationId: string) {
    setLoadingAuditId(conversationId);
    try {
      const report = await api.getConversationAudit(conversationId);
      setAuditReport(report);
    } catch (err) {
      onToast(err instanceof Error ? err.message : '加载单会话审计失败');
    } finally {
      setLoadingAuditId(null);
    }
  }

  const loadAll = useCallback(async (targetPage: number, silent = false) => {
    if (inFlightRef.current) return;
    inFlightRef.current = true;
    try {
      const [dash, rules] = await Promise.all([
        api.getStatsDashboard({ range, appFilter, modelFilter, page: targetPage, pageSize }),
        api.getPricings()
      ]);
      setData(dash);
      setPricings(rules);
      setLoadError(null);
    } catch (err) {
      if (!silent) {
        setLoadError(err instanceof Error ? err.message : '统计数据加载失败');
      }
    } finally {
      inFlightRef.current = false;
      setLoadingInitial(false);
    }
  }, [range, appFilter, modelFilter, pageSize]);

  useEffect(() => {
    void loadAll(page);
  }, [range, appFilter, modelFilter, pageSize, page, loadAll]);

  // 用量统计每 5 秒自动无感同步，新增对话无需手动点击刷新即可实时呈现；页面隐藏或正在编辑定价时暂停
  useEffect(() => {
    if (!autoSync) return;
    const timer = window.setInterval(() => {
      if (!document.hidden && !pricingDraft && !syncing) {
        void loadAll(page, true);
      }
    }, 5000);
    return () => window.clearInterval(timer);
  }, [autoSync, pricingDraft, syncing, page, loadAll]);

  async function handleSyncNow() {
    setSyncing(true);
    try {
      const res = await api.syncStatsNow();
      await loadAll(1);
      setPage(1);
      onToast(`同步完成：扫描 ${res.scannedFiles} 个会话文件，解析 ${res.parsedEntries} 条调用记录 (${res.elapsedMs}ms)`);
    } finally {
      setSyncing(false);
    }
  }

  async function handleSavePricing(e: React.FormEvent) {
    e.preventDefault();
    if (!pricingDraft) return;
    await api.upsertPricing(pricingDraft);
    setPricingDraft(null);
    await loadAll(page);
    onToast(`已更新模型 ${pricingDraft.pattern} 单价并重算全部历史成本`);
  }

  const appSegItems = useMemo(() => {
    return [
      { value: 'all', label: '全部应用' },
      ...(data?.appChips ?? []).map(c => ({ value: c.name, label: c.name, count: c.count }))
    ];
  }, [data?.appChips]);

  const modelOptions = useMemo(() => {
    return [
      { value: 'all', label: `全部模型 (${data?.availableModels.length ?? 0})` },
      ...(data?.availableModels ?? []).map(m => ({ value: m, label: m }))
    ];
  }, [data?.availableModels]);

  const timeline = data?.timeline ?? [];
  const metricValues = timeline.map(pt => {
    if (activeMetric === 'cost') return pt.costUsd;
    if (activeMetric === 'requests') return pt.requests;
    if (activeMetric === 'tokens') return pt.totalTokens;
    return pt.cacheHitRate;
  });
  const maxVal = activeMetric === 'cacheRate' ? 100 : Math.max(1, ...metricValues);
  const totalPages = data ? Math.max(1, Math.ceil(data.totalLogsCount / data.pageSize)) : 1;

  const metricLabel = {
    cost: '估算成本趋势 ($)',
    requests: '请求次数趋势',
    tokens: '总消耗 Tokens 趋势',
    cacheRate: '缓存命中率 · 分时段 (%)'
  }[activeMetric];

  const syncTimeStr = data?.lastSyncedAt
    ? new Date(data.lastSyncedAt).toLocaleTimeString('zh-CN', { hour12: false })
    : '等待同步';

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
      {/* 无框筛选条（接入定制下拉与滑动分段选择器） */}
      <div className="surface-card" style={{ padding: '14px 18px', display: 'flex', flexDirection: 'column', gap: 10 }}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap', gap: 10 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
            <CustomSelect
              variant="light"
              value={range}
              options={RANGE_OPTIONS}
              prefixIcon={<ClockRangeIcon size={13} />}
              onChange={v => { setPage(1); setRange(v); }}
              style={{ width: 122 }}
              ariaLabel="选择统计时间范围"
            />

            <SegmentedTabs
              value={appFilter}
              items={appSegItems}
              onChange={v => { setPage(1); setAppFilter(v); }}
            />
          </div>

          <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
            <CustomSelect
              variant="light"
              value={modelFilter}
              options={modelOptions}
              prefixIcon={<CpuModelIcon size={13} />}
              minMenuWidth={256}
              onChange={v => { setPage(1); setModelFilter(v); }}
              style={{ width: 164 }}
              ariaLabel="筛选 AI 模型"
            />
            <button
              type="button"
              className={`btn-pill btn-sm ${autoSync ? 'btn-soft' : 'btn-soft'}`}
              style={{
                border: autoSync ? '1px solid rgba(16, 185, 129, 0.28)' : '1px solid transparent',
                color: autoSync ? 'var(--accent-emerald-text)' : 'var(--text-muted)'
              }}
              title="切换每 5 秒自动增量同步会话用量"
              onClick={() => setAutoSync(v => !v)}
            >
              <span className={`live-sync-dot ${autoSync ? 'on' : ''}`} />
              <span>{autoSync ? '5s 自动同步' : '已暂停自动'}</span>
            </button>
            <button type="button" className="btn-pill btn-soft btn-sm" onClick={handleSyncNow} disabled={syncing}>
              <RefreshSpinIcon size={13} spinning={syncing} />
              <span>{syncing ? '同步中' : '立即同步'}</span>
            </button>
          </div>
        </div>

        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', fontSize: 11.5, color: 'var(--text-muted)', flexWrap: 'wrap', gap: 8 }}>
          <span>数据来源：本地 <span className="mono">.claude / .codex / .gemini</span> 会话日志增量解析（端到端 TLS 不拆包）</span>
          <span className="num">
            实时：↑ {formatRate(data?.totalProxyTxBytesPerSec ?? 0)} · ↓ {formatRate(data?.totalProxyRxBytesPerSec ?? 0)}
            <span style={{ margin: '0 8px', opacity: 0.45 }}>|</span>
            累计：↑ {formatBytes(data?.totalProxyTxBytes ?? 0)} · ↓ {formatBytes(data?.totalProxyRxBytes ?? 0)}
          </span>
        </div>
      </div>

      {/* 加载异常或当天无数据快捷引导条 */}
      {loadError && (
        <div
          className="surface-card"
          style={{
            padding: '10px 16px',
            display: 'flex',
            justifyContent: 'space-between',
            alignItems: 'center',
            gap: 12,
            border: '1px solid rgba(242, 121, 115, 0.38)'
          }}
        >
          <span style={{ fontSize: 12, color: 'var(--accent-coral-text)' }}>
            获取统计数据失败：{loadError}
          </span>
          <button type="button" className="btn-pill btn-soft btn-sm" onClick={() => void loadAll(page)}>
            重新加载
          </button>
        </div>
      )}

      {!loadingInitial && !loadError && range === 'today' && data && data.totalRequests === 0 && (data.activeDaysLastYear ?? 0) > 0 && (
        <div
          className="surface-card"
          style={{
            padding: '10px 16px',
            display: 'flex',
            justifyContent: 'space-between',
            alignItems: 'center',
            gap: 10,
            flexWrap: 'wrap'
          }}
        >
          <span style={{ fontSize: 12, color: 'var(--text-secondary)' }}>
            今日暂未记录到新 AI 会话（过去一年已统计 {data.activeDaysLastYear} 个活跃日）。
          </span>
          <div style={{ display: 'flex', gap: 6 }}>
            <button type="button" className="btn-pill btn-soft btn-sm" onClick={() => { setPage(1); setRange('7d'); }}>
              看近 7 天
            </button>
            <button type="button" className="btn-pill btn-indigo btn-sm" onClick={() => { setPage(1); setRange('all'); }}>
              看全部历史
            </button>
          </div>
        </div>
      )}

      {/* 四大指标联动柱图 */}
      <div className="surface-card">
        <div className="kpi-strip">
          <button type="button" className={`kpi-strip-btn ${activeMetric === 'cost' ? 'active' : ''}`} onClick={() => setActiveMetric('cost')}>
            <div style={{ fontSize: 11.5, color: 'var(--text-secondary)', marginBottom: 4 }}>总成本 (已定价请求)</div>
            <div className="num" style={{ fontSize: 23, fontWeight: 700, letterSpacing: '-0.02em' }}>
              {loadingInitial ? '—' : `$${(data?.totalCostUsd ?? 0).toFixed(2)}`}
            </div>
            <div className="num" style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 3 }}>
              {loadingInitial ? '正在扫描本地会话日志...' : (data && data.unpricedRequests > 0 ? `${data.unpricedRequests} 条未配单价` : '全部请求已按单价核算')}
            </div>
          </button>

          <button type="button" className={`kpi-strip-btn ${activeMetric === 'requests' ? 'active' : ''}`} onClick={() => setActiveMetric('requests')}>
            <div style={{ fontSize: 11.5, color: 'var(--text-secondary)', marginBottom: 4 }}>总请求数</div>
            <div className="num" style={{ fontSize: 23, fontWeight: 700, letterSpacing: '-0.02em' }}>
              {loadingInitial ? '—' : (data?.totalRequests ?? 0).toLocaleString('en-US')}
            </div>
            <div className="num" style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 3 }}>
              {loadingInitial ? '等待同步...' : `代理 ${data?.totalProxyConnections ?? 0} 次 · 已扫 ${data?.scannedFilesCount ?? 0} 文件`}
            </div>
          </button>

          <button type="button" className={`kpi-strip-btn ${activeMetric === 'tokens' ? 'active' : ''}`} onClick={() => setActiveMetric('tokens')}>
            <div style={{ fontSize: 11.5, color: 'var(--text-secondary)', marginBottom: 4 }}>真实消耗 Tokens</div>
            <div className="num" style={{ fontSize: 23, fontWeight: 700, letterSpacing: '-0.02em' }}>
              {loadingInitial ? '—' : formatCompactTokens(data?.totalTokens ?? 0)}
            </div>
            <div className="num" style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 3 }}>
              入 {formatCompactTokens(data?.effectiveInputTokens ?? 0)} · 出 {formatCompactTokens(data?.outputTokens ?? 0)} · 缓 {formatCompactTokens(data?.cacheReadTokens ?? 0)}
            </div>
          </button>

          <button type="button" className={`kpi-strip-btn ${activeMetric === 'cacheRate' ? 'active' : ''}`} onClick={() => setActiveMetric('cacheRate')}>
            <div style={{ fontSize: 11.5, color: 'var(--text-secondary)', marginBottom: 4 }}>缓存命中率 / 累计节省</div>
            <div className="num" style={{ fontSize: 23, fontWeight: 700, letterSpacing: '-0.02em', color: 'var(--accent-indigo)' }}>
              {loadingInitial ? '—' : `${(data?.cacheHitRate ?? 0).toFixed(1)}%`}
              {!loadingInitial && (
                <span style={{ fontSize: 13, fontWeight: 600, color: 'var(--accent-emerald-text)', marginLeft: 8 }}>
                  省 ${(data?.totalCacheSavedUsd ?? 0).toFixed(2)}
                </span>
              )}
            </div>
            <div className="num" style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 3 }}>
              上次同步 {syncTimeStr} ({data?.syncElapsedMs ?? 0} ms)
            </div>
          </button>
        </div>

        <div className="soft-divider" />

        {/* 自适应 AI 额度窗口与预算护栏舱（全局护栏 + 动态多活跃 Agent 矩阵） */}
        {data?.quota && (
          <>
            <div style={{ padding: '14px 18px 16px' }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 10, gap: 10, flexWrap: 'wrap' }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <span className="card-title-sm">AI 额度窗口与预算水位舱</span>
                <span className={`pill-tag pill-tag--xs ${data.quota.headlineTone === 'critical' ? 'tag-coral' : (data.quota.headlineTone === 'warn' ? 'tag-amber' : 'tag-emerald')}`}>
                  {data.quota.headlineStatus}
                </span>
              </div>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                {(data.quota.activeAgents?.length ?? 0) > 5 && (
                  <button
                    type="button"
                    className="btn-pill btn-soft btn-sm btn-tbl"
                    onClick={() => setShowAllAgents(v => !v)}
                  >
                    {showAllAgents ? '收起次要席位' : `展开全部活跃席位 (${data.quota.activeAgents.length})`}
                  </button>
                )}
                <button type="button" className="btn-pill btn-soft btn-sm btn-tbl" onClick={openQuotaModal}>
                  配额规则与预算
                </button>
              </div>
            </div>

            <div className="quota-deck-grid">
              {/* 卡 1：全局预算护栏 & Prompt Cache 净省 */}
              <div className="quota-card">
                <div className="quota-card-head">
                  <div style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0 }}>
                    <span className="quota-card-title">全局预算护栏</span>
                    <span
                      className={`pill-tag pill-tag--xs ${
                        (data.quota.dailyUsedPercent ?? 0) >= data.quota.criticalPercent
                          ? 'tag-coral'
                          : ((data.quota.dailyUsedPercent ?? 0) >= data.quota.warnPercent ? 'tag-amber' : 'tag-emerald')
                      }`}
                    >
                      {data.quota.dailyBudgetUsd > 0 ? '已启用' : '自由计数'}
                    </span>
                  </div>
                  <span className="num" style={{ fontSize: 11, color: 'var(--text-muted)' }}>
                    速率 ${data.quota.burnRateUsdPerHour.toFixed(2)}/h
                  </span>
                </div>

                <div className="quota-win-row">
                  <div className="quota-win-top">
                    <span className="quota-win-lbl">今日支出</span>
                    <span className="quota-win-val num">
                      ${data.quota.dailySpentUsd.toFixed(2)}
                      {data.quota.dailyBudgetUsd > 0 ? ` / $${data.quota.dailyBudgetUsd.toFixed(0)} (${data.quota.dailyUsedPercent ?? 0}%)` : ''}
                    </span>
                  </div>
                  <div className="meter-track">
                    <div
                      className={`meter-fill tone-${
                        (data.quota.dailyUsedPercent ?? 0) >= data.quota.criticalPercent
                          ? 'critical'
                          : ((data.quota.dailyUsedPercent ?? 0) >= data.quota.warnPercent ? 'warn' : 'ok')
                      }`}
                      style={{ width: `${data.quota.dailyBudgetUsd > 0 ? Math.max(4, data.quota.dailyUsedPercent ?? 0) : Math.min(100, Math.max(8, data.quota.dailySpentUsd * 10))}%` }}
                    />
                  </div>
                </div>

                <div className="quota-win-row">
                  <div className="quota-win-top">
                    <span className="quota-win-lbl">本月累计</span>
                    <span className="quota-win-val num">
                      ${data.quota.monthlySpentUsd.toFixed(2)}
                      {data.quota.monthlyBudgetUsd > 0 ? ` / $${data.quota.monthlyBudgetUsd.toFixed(0)} (${data.quota.monthlyUsedPercent ?? 0}%)` : ''}
                    </span>
                  </div>
                  <div className="meter-track">
                    <div
                      className={`meter-fill tone-${
                        (data.quota.monthlyUsedPercent ?? 0) >= data.quota.criticalPercent
                          ? 'critical'
                          : ((data.quota.monthlyUsedPercent ?? 0) >= data.quota.warnPercent ? 'warn' : 'indigo')
                      }`}
                      style={{ width: `${data.quota.monthlyBudgetUsd > 0 ? Math.max(4, data.quota.monthlyUsedPercent ?? 0) : Math.min(100, Math.max(8, data.quota.monthlySpentUsd * 2))}%` }}
                    />
                  </div>
                </div>

                <div className="quota-card-foot num">
                  <span>Cache 净省 ${data.quota.todayCacheSavedUsd.toFixed(2)}</span>
                  <span style={{ color: 'var(--accent-emerald-text)', fontWeight: 600 }}>
                    省 {data.quota.cacheSavingsRatioPercent}%
                  </span>
                </div>
              </div>

              {/* 卡 2..N：按实际活跃席位动态渲染（自动适配单周额度 / 5h+7d / Credits / 多开分号） */}
              {(showAllAgents ? data.quota.activeAgents : data.quota.activeAgents.slice(0, 5)).map((agent: AgentQuotaCardDto) => (
                <div key={agent.seatId} className="quota-card">
                  <div className="quota-card-head">
                    <div style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0 }}>
                      <span className={`live-sync-dot ${agent.isRunning ? 'on' : ''}`} title={agent.isRunning ? '当前在线/运行中' : '近期活跃记录'} />
                      <span className="quota-card-title cell-ellipsis" style={{ maxWidth: 120 }} title={agent.appName}>
                        {agent.appName}
                      </span>
                      {agent.planBadge && (
                        <span className="pill-tag pill-tag--xs tag-indigo mono">{agent.planBadge}</span>
                      )}
                    </div>
                    {agent.creditsBadge ? (
                      <span className="pill-tag pill-tag--xs tag-emerald num">{agent.creditsBadge}</span>
                    ) : (
                      <span className="num" style={{ fontSize: 11, color: 'var(--text-muted)' }}>
                        今日 ${agent.todayCostUsd.toFixed(2)}
                      </span>
                    )}
                  </div>

                  <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                    {agent.windows.map(win => {
                      const hasPct = win.usedPercent !== null && win.usedPercent !== undefined;
                      const barWidth = hasPct ? Math.max(win.usedPercent! > 0 ? 4 : 0, win.usedPercent!) : 100;
                      return (
                        <div key={win.key} className="quota-win-row">
                          <div className="quota-win-top">
                            <span className="quota-win-lbl">{win.label}</span>
                            <span
                              className="quota-win-val num"
                              style={{
                                color: win.tone === 'critical'
                                  ? 'var(--accent-coral-text)'
                                  : (win.tone === 'warn' ? 'var(--accent-amber-text)' : 'var(--text-primary)')
                              }}
                            >
                              {win.usedDisplay}
                              {win.subDisplay ? (
                                <span style={{ fontWeight: 400, color: 'var(--text-muted)', marginLeft: 5, fontSize: 10.5 }}>
                                  · {win.subDisplay}
                                </span>
                              ) : null}
                            </span>
                          </div>
                          {hasPct ? (
                            <div className="meter-track">
                              <div className={`meter-fill tone-${win.tone}`} style={{ width: `${barWidth}%` }} />
                            </div>
                          ) : (
                            <div className="meter-track">
                              <div className="meter-fill tone-subtle" style={{ width: agent.todayTokens > 0 ? '45%' : '0%' }} />
                            </div>
                          )}
                        </div>
                      );
                    })}
                  </div>

                  <div className="quota-card-foot num">
                    <span>今日 {formatCompactTokens(agent.todayTokens)} Tokens</span>
                    <span style={{ color: 'var(--accent-emerald-text)' }}>
                      缓省 ${agent.todayCacheSavedUsd.toFixed(2)}
                    </span>
                  </div>
                </div>
              ))}
            </div>
            </div>
            <div className="soft-divider" />
          </>
        )}

        <div style={{ padding: '16px 20px 14px' }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 6 }}>
            <span className="card-title-sm">{metricLabel}</span>
            <span className="card-subtitle-sm">免解密本地会话日志 + 独立代理隧道遥测</span>
          </div>

          <div className="bar-stage">
            {timeline.map((pt, i) => {
              const v = metricValues[i];
              const pct = maxVal > 0 ? Math.round((v / maxVal) * 100) : 0;
              const h = v > 0 ? Math.max(8, pct) : 0;
              const tip = `${pt.label} | 请求 ${pt.requests} 次 | Tokens ${formatCompactTokens(pt.totalTokens)} | 缓存 ${pt.cacheHitRate}% | $${pt.costUsd.toFixed(3)}`;
              return (
                <div className="bar-col" key={pt.label} title={tip}>
                  <div className={`bar-fill ${v <= 0 ? 'empty' : ''}`} style={{ height: `${h}%` }} />
                </div>
              );
            })}
          </div>
          <div className="bar-labels-row">
            {timeline.map((pt, idx) => {
              const step = timeline.length <= 12 ? 1 : Math.ceil(timeline.length / 9);
              const show = idx === 0 || idx % step === 0;
              return (
                <div className="bar-lbl num" key={pt.label}>
                  {show ? pt.label : ''}
                </div>
              );
            })}
          </div>
        </div>
      </div>

      {/* 365 天 AI 编码活跃热力图 */}
      {(data?.activityHeatmap?.length ?? 0) > 0 && (
        <div className="surface-card surface-card-pad" style={{ padding: '14px 20px' }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 10, flexWrap: 'wrap', gap: 8 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
              <span className="card-title-sm">365 天 AI 编码活跃热力图</span>
              <span className="pill-tag tag-indigo num">
                过去一年活跃 {data?.activeDaysLastYear ?? 0} 天
              </span>
            </div>
            <div style={{ display: 'flex', alignItems: 'center', gap: 4, fontSize: 11, color: 'var(--text-muted)' }}>
              <span>少</span>
              {[0, 1, 2, 3, 4].map(lv => (
                <span key={lv} className={`heatmap-cell lv-${lv}`} style={{ display: 'inline-block' }} />
              ))}
              <span>多</span>
            </div>
          </div>

          <div className="heatmap-scroll">
            <div className="heatmap-grid">
              {data?.activityHeatmap?.map(cell => (
                <div
                  key={cell.date}
                  className={`heatmap-cell lv-${cell.level}`}
                  title={`${cell.date} | 请求 ${cell.requests} 次 | Tokens ${formatCompactTokens(cell.totalTokens)} | 费用 $${cell.costUsd.toFixed(3)}`}
                />
              ))}
            </div>
          </div>
        </div>
      )}

      {/* 底部多维明细无框纯白卡片 */}
      <div className="surface-card">
        <div style={{ padding: '12px 18px', display: 'flex', alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap', gap: 8 }}>
          <SegmentedTabs<SubTab>
            value={subTab}
            onChange={setSubTab}
            items={[
              { value: 'logs', label: '请求日志', count: data?.totalLogsCount ?? 0 },
              { value: 'workspaces', label: '工作区排行', count: data?.workspaceBreakdown?.length ?? 0 },
              { value: 'models', label: '按模型统计', count: data?.modelBreakdown.length ?? 0 },
              { value: 'appsProxy', label: '应用与专线归属', count: data?.appAndProxyBreakdown.length ?? 0 },
              { value: 'pricing', label: '模型定价表', count: pricings.length }
            ]}
          />

          {subTab === 'pricing' && (
            <button
              type="button"
              className="btn-pill btn-indigo btn-sm"
              onClick={() => setPricingDraft({
                pattern: '',
                displayName: '',
                inputPerMillion: 1.25,
                outputPerMillion: 10.0,
                cacheReadPerMillion: 0.125,
                cacheWritePerMillion: 0
              })}
            >
              <PlusIcon size={12} />
              <span>新增模型单价</span>
            </button>
          )}
        </div>

        <div className="soft-divider" />

        {subTab === 'logs' && (
          <>
            <div className="table-scroll-wrap">
              <table className="clean-table clean-table--logs">
                <thead>
                  <tr>
                    <th>时间</th>
                    <th>应用</th>
                    <th>模型</th>
                    <th>成本</th>
                    <th className="num-r">有效输入</th>
                    <th className="num-r">输出</th>
                    <th className="num-r">缓存命中</th>
                    <th className="num-r">流速</th>
                    <th className="num-r" title="估算首字响应耗时">首字</th>
                    <th className="num-r">耗时</th>
                    <th className="num-r sticky-col-right">审计</th>
                  </tr>
                </thead>
                <tbody>
                  {(data?.logs ?? []).length === 0 ? (
                    <tr>
                      <td colSpan={11} style={{ textAlign: 'center', padding: 30, color: 'var(--text-muted)' }}>
                        当前时间范围内暂无会话记录（可切换「全部历史」或点击右上角「立即同步」）
                      </td>
                    </tr>
                  ) : (
                    (data?.logs ?? []).map(row => (
                      <tr key={row.id}>
                        <td className="num nowrap" style={{ color: 'var(--text-secondary)' }}>
                          {new Date(row.timestamp).toLocaleTimeString('zh-CN', { hour12: false })}
                        </td>
                        <td className="nowrap">
                          <span
                            className="pill-tag pill-tag--xs tag-neutral"
                            title={`${row.appName} · 来源：${row.source}`}
                          >
                            {row.appName}
                          </span>
                        </td>
                        <td>
                          <span className="mono cell-ellipsis" style={{ fontWeight: 500, fontSize: 11.5, maxWidth: 158 }} title={row.model}>
                            {row.model}
                          </span>
                        </td>
                        <td className="nowrap">
                          {row.isPriced && row.costUsd !== null && row.costUsd !== undefined ? (
                            <span className="num" style={{ color: 'var(--accent-emerald-text)', fontWeight: 600 }}>
                              ${row.costUsd.toFixed(4)}
                            </span>
                          ) : (
                            <button
                              type="button"
                              className="btn-pill btn-soft btn-sm btn-tbl"
                              onClick={() => setPricingDraft({
                                pattern: row.model,
                                displayName: row.model,
                                inputPerMillion: 1.25,
                                outputPerMillion: 10.0,
                                cacheReadPerMillion: 0.125,
                                cacheWritePerMillion: 0
                              })}
                            >
                              配价
                            </button>
                          )}
                        </td>
                        <td className="num-r num nowrap">{row.effectiveInputTokens.toLocaleString('en-US')}</td>
                        <td className="num-r num nowrap">{row.outputTokens.toLocaleString('en-US')}</td>
                        <td className="num-r num nowrap">{formatCompactTokens(row.cacheReadTokens)}</td>
                        <td className="num-r num nowrap" style={{ color: row.tokensPerSecond ? 'var(--accent-emerald-text)' : 'var(--text-muted)' }}>
                          {row.tokensPerSecond ? `~${Math.round(row.tokensPerSecond * 10) / 10} tok/s` : '—'}
                        </td>
                        <td className="num-r num nowrap" style={{ color: row.firstTokenSeconds ? 'var(--text-secondary)' : 'var(--text-muted)' }}>
                          {row.firstTokenSeconds ? `~${Math.round(row.firstTokenSeconds * 10) / 10}s` : '—'}
                        </td>
                        <td className="num-r num nowrap" style={{ color: 'var(--text-secondary)' }}>
                          {row.durationSeconds > 0 ? `~${Math.round(row.durationSeconds)}s` : '—'}
                        </td>
                        <td className="num-r nowrap sticky-col-right">
                          {row.conversationId ? (
                            <button
                              type="button"
                              className="btn-pill btn-soft btn-sm btn-tbl"
                              disabled={loadingAuditId === row.conversationId}
                              onClick={() => openConversationAudit(row.conversationId!)}
                            >
                              {loadingAuditId === row.conversationId ? '...' : '审计'}
                            </button>
                          ) : '—'}
                        </td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
            <div className="soft-divider" />
            <div style={{ padding: '10px 18px', display: 'flex', justifyContent: 'space-between', alignItems: 'center', fontSize: 12, color: 'var(--text-secondary)' }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                <span className="num">共 {data?.totalLogsCount ?? 0} 条记录</span>
                <CustomSelect
                  variant="light"
                  size="sm"
                  value={String(pageSize)}
                  options={PAGE_SIZE_OPTIONS}
                  onChange={v => { setPage(1); setPageSize(Number(v)); }}
                  style={{ width: 108 }}
                  ariaLabel="选择每页显示条数"
                />
              </div>
              <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                <button type="button" className="btn-pill btn-soft btn-sm" disabled={page <= 1} onClick={() => setPage(p => Math.max(1, p - 1))}>上一页</button>
                <span className="num" style={{ padding: '0 6px' }}>{page} / {totalPages}</span>
                <button type="button" className="btn-pill btn-soft btn-sm" disabled={page >= totalPages} onClick={() => setPage(p => Math.min(totalPages, p + 1))}>下一页</button>
              </div>
            </div>
          </>
        )}

        {subTab === 'workspaces' && (
          <div className="table-scroll-wrap">
            <table className="clean-table">
              <thead>
                <tr>
                  <th>项目工作区路径</th>
                  <th>主力应用</th>
                  <th>主力模型</th>
                  <th className="num-r">请求次数</th>
                  <th className="num-r">累计 Tokens</th>
                  <th className="num-r">估算成本</th>
                </tr>
              </thead>
              <tbody>
                {(data?.workspaceBreakdown ?? []).length === 0 ? (
                  <tr>
                    <td colSpan={6} style={{ textAlign: 'center', padding: 28, color: 'var(--text-muted)' }}>
                      当前范围内暂未扫描到含工作区标识（cwd / workspace）的会话日志
                    </td>
                  </tr>
                ) : (
                  (data?.workspaceBreakdown ?? []).map(ws => {
                    const displayPath = privacyMode
                      ? `…/${ws.workspace.split(/[\\/]/).filter(Boolean).pop() ?? ws.workspace}`
                      : ws.workspace;
                    return (
                      <tr key={ws.workspace}>
                        <td title={privacyMode ? '隐私护盾已遮蔽完整路径' : ws.workspace}>
                          <span className="mono cell-ellipsis" style={{ fontWeight: 600, fontSize: 12, maxWidth: 280 }}>
                            {displayPath}
                          </span>
                        </td>
                        <td><span className="pill-tag tag-neutral">{ws.topApp || '—'}</span></td>
                        <td>
                          <span className="mono cell-ellipsis" style={{ fontSize: 11.5, maxWidth: 180 }} title={ws.topModel || '—'}>
                            {ws.topModel || '—'}
                          </span>
                        </td>
                        <td className="num-r num">{ws.requests}</td>
                        <td className="num-r num">{formatCompactTokens(ws.totalTokens)}</td>
                        <td className="num-r num" style={{ color: 'var(--accent-emerald-text)', fontWeight: 600 }}>
                          ${ws.costUsd.toFixed(4)}
                        </td>
                      </tr>
                    );
                  })
                )}
              </tbody>
            </table>
          </div>
        )}

        {subTab === 'models' && (
          <div className="table-scroll-wrap">
            <table className="clean-table">
              <thead>
                <tr>
                  <th>模型名称</th>
                  <th className="num-r">请求数</th>
                  <th className="num-r">有效输入</th>
                  <th className="num-r">输出</th>
                  <th className="num-r">缓存命中</th>
                  <th className="num-r">命中率</th>
                  <th className="num-r">平均流速</th>
                  <th className="num-r">估算成本</th>
                </tr>
              </thead>
              <tbody>
                {(data?.modelBreakdown ?? []).map(m => (
                  <tr key={m.model}>
                    <td>
                      <span className="mono cell-ellipsis" style={{ fontWeight: 600, fontSize: 12, maxWidth: 220 }} title={m.model}>
                        {m.model}
                      </span>
                    </td>
                    <td className="num-r num">{m.requests}</td>
                    <td className="num-r num">{formatCompactTokens(m.effectiveInputTokens)}</td>
                    <td className="num-r num">{formatCompactTokens(m.outputTokens)}</td>
                    <td className="num-r num">{formatCompactTokens(m.cacheReadTokens)}</td>
                    <td className="num-r num" style={{ color: 'var(--accent-indigo)', fontWeight: 600 }}>{m.cacheHitRate}%</td>
                    <td className="num-r num">{m.avgTokensPerSecond > 0 ? `~${m.avgTokensPerSecond} tok/s` : '—'}</td>
                    <td className="num-r num">
                      {m.hasPricing ? `$${m.costUsd.toFixed(4)}` : (
                        <button
                          type="button"
                          className="btn-pill btn-soft btn-sm btn-tbl"
                          onClick={() => setPricingDraft({
                            pattern: m.model,
                            displayName: m.model,
                            inputPerMillion: 1.25,
                            outputPerMillion: 10.0,
                            cacheReadPerMillion: 0.125,
                            cacheWritePerMillion: 0
                          })}
                        >
                          配置单价
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        {subTab === 'appsProxy' && (
          <div className="table-scroll-wrap">
            <table className="clean-table">
              <thead>
                <tr>
                  <th>AI 应用 / 席位</th>
                  <th className="num-r">调用次数</th>
                  <th className="num-r">总消耗 Tokens</th>
                  <th className="num-r">缓存命中率</th>
                  <th>独立入口端口</th>
                  <th>绑定专线与出口 IP</th>
                  <th className="num-r">代理上行 / 下行流量</th>
                </tr>
              </thead>
              <tbody>
                {(data?.appAndProxyBreakdown ?? []).map(row => (
                  <tr key={row.appName}>
                    <td style={{ fontWeight: 600 }}>{row.appName}</td>
                    <td className="num-r num">{row.requests}</td>
                    <td className="num-r num">{formatCompactTokens(row.totalTokens)}</td>
                    <td className="num-r num">{row.cacheHitRate}%</td>
                    <td className="mono">{row.seatPort ?? '—'}</td>
                    <td>
                      <div className="cell-ellipsis" style={{ fontWeight: 600, maxWidth: 210 }} title={row.boundNodeName ?? '—'}>
                        {row.boundNodeName ?? '—'}
                      </div>
                      <div className="num" style={{ fontSize: 11, color: 'var(--text-secondary)' }}>{maskIp(row.exitIp, privacyMode)}</div>
                    </td>
                    <td className="num-r num nowrap">
                      ↑ {formatBytes(row.bytesTx)} · ↓ {formatBytes(row.bytesRx)} ({row.proxyConnections} 次)
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        {subTab === 'pricing' && (
          <div className="table-scroll-wrap">
            <table className="clean-table">
              <thead>
                <tr>
                  <th>模型匹配规则</th>
                  <th>显示名称</th>
                  <th className="num-r">输入 ($/1M)</th>
                  <th className="num-r">输出 ($/1M)</th>
                  <th className="num-r">缓存读 ($/1M)</th>
                  <th className="num-r">缓存写 ($/1M)</th>
                  <th className="num-r">来源</th>
                </tr>
              </thead>
              <tbody>
                {pricings.map(r => (
                  <tr key={r.pattern}>
                    <td>
                      <span className="mono cell-ellipsis" style={{ fontWeight: 600, fontSize: 12, maxWidth: 200 }} title={r.pattern}>
                        {r.pattern}
                      </span>
                    </td>
                    <td>
                      <span className="cell-ellipsis" style={{ maxWidth: 200 }} title={r.displayName}>
                        {r.displayName}
                      </span>
                    </td>
                    <td className="num-r num">${r.inputPerMillion}</td>
                    <td className="num-r num">${r.outputPerMillion}</td>
                    <td className="num-r num">${r.cacheReadPerMillion}</td>
                    <td className="num-r num">${r.cacheWritePerMillion}</td>
                    <td className="num-r">
                      <span className={`pill-tag ${r.isCustom ? 'tag-indigo' : 'tag-neutral'}`}>
                        {r.isCustom ? '自定义' : '内置'}
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {quotaModalOpen && (
        <div className="modal-backdrop" onClick={() => setQuotaModalOpen(false)}>
          <form className="modal-surface" style={{ maxWidth: 640 }} onSubmit={handleSaveQuotaSettings} onClick={e => e.stopPropagation()}>
            <div style={{ padding: '18px 22px 14px', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
              <div>
                <div style={{ fontSize: 15, fontWeight: 700 }}>AI 预算护栏与分席位配额规则设置</div>
                <div style={{ fontSize: 12, color: 'var(--text-muted)', marginTop: 2 }}>
                  仅用于自适应水位视觉提醒（填 0 表示不限制上限，默认绝不强行打断对话）
                </div>
              </div>
              <button type="button" className="btn-pill btn-soft btn-sm" onClick={() => setQuotaModalOpen(false)}>关闭</button>
            </div>
            <div className="soft-divider" />
            <div style={{ padding: '16px 22px', display: 'flex', flexDirection: 'column', gap: 14, maxHeight: '68vh', overflowY: 'auto' }}>
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 10 }}>
                <div>
                  <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 4 }}>全局日预算 ($)</div>
                  <input
                    className="clean-input num"
                    type="number"
                    min="0"
                    step="0.5"
                    value={quotaDraft.dailyBudgetUsd}
                    onChange={e => setQuotaDraft(d => ({ ...d, dailyBudgetUsd: Number(e.target.value) || 0 }))}
                  />
                </div>
                <div>
                  <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 4 }}>全局月预算 ($)</div>
                  <input
                    className="clean-input num"
                    type="number"
                    min="0"
                    step="5"
                    value={quotaDraft.monthlyBudgetUsd}
                    onChange={e => setQuotaDraft(d => ({ ...d, monthlyBudgetUsd: Number(e.target.value) || 0 }))}
                  />
                </div>
                <div>
                  <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 4 }}>琥珀预警 (%)</div>
                  <input
                    className="clean-input num"
                    type="number"
                    min="10"
                    max="95"
                    value={quotaDraft.warnPercent}
                    onChange={e => setQuotaDraft(d => ({ ...d, warnPercent: Number(e.target.value) || 65 }))}
                  />
                </div>
                <div>
                  <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 4 }}>珊瑚告警 (%)</div>
                  <input
                    className="clean-input num"
                    type="number"
                    min="20"
                    max="99"
                    value={quotaDraft.criticalPercent}
                    onChange={e => setQuotaDraft(d => ({ ...d, criticalPercent: Number(e.target.value) || 85 }))}
                  />
                </div>
              </div>

              <div className="soft-divider" />

              <div style={{ fontSize: 12, fontWeight: 600, color: 'var(--text-secondary)' }}>
                按席位定制计费与周期模式（未设定则按日志原生字段自动识别）
              </div>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                {(['agy', 'claude', 'codex', 'cursor'] as const).map(seatKey => {
                  const labelMap: Record<string, string> = { agy: 'Antigravity', claude: 'Claude Code', codex: 'Codex CLI', cursor: 'Cursor' };
                  const cur: SeatQuotaRule = quotaDraft.seatRules[seatKey] ?? { billingMode: 'auto' };
                  const templateVal = (() => {
                    if (cur.billingMode === 'auto') return 'auto';
                    if (cur.billingMode === 'budget_usd') return 'daily_usd';
                    if (cur.primaryWindowMinutes === 10080 && !cur.secondaryWindowMinutes) return 'weekly_only';
                    if (cur.primaryWindowMinutes === 1440 && !cur.secondaryWindowMinutes) return 'daily_only';
                    return '5h_and_weekly';
                  })();

                  const setSeatRule = (patch: Partial<SeatQuotaRule>) => {
                    setQuotaDraft(d => ({
                      ...d,
                      seatRules: { ...d.seatRules, [seatKey]: { ...cur, ...patch } }
                    }));
                  };

                  return (
                    <div
                      key={seatKey}
                      style={{
                        display: 'grid',
                        gridTemplateColumns: '110px 1fr 120px',
                        gap: 10,
                        alignItems: 'center',
                        background: 'var(--bg-subtle)',
                        padding: '8px 12px',
                        borderRadius: 10
                      }}
                    >
                      <span style={{ fontWeight: 600, fontSize: 12 }}>{labelMap[seatKey]}</span>
                      <select
                        className="clean-input"
                        style={{ height: 30, fontSize: 11.5 }}
                        value={templateVal}
                        onChange={e => {
                          const v = e.target.value;
                          if (v === 'auto') setSeatRule({ billingMode: 'auto', primaryWindowMinutes: null, secondaryWindowMinutes: null });
                          else if (v === '5h_and_weekly') setSeatRule({ billingMode: 'subscription_windows', primaryWindowMinutes: 300, secondaryWindowMinutes: 10080 });
                          else if (v === 'weekly_only') setSeatRule({ billingMode: 'subscription_windows', primaryWindowMinutes: 10080, secondaryWindowMinutes: null });
                          else if (v === 'daily_only') setSeatRule({ billingMode: 'subscription_windows', primaryWindowMinutes: 1440, secondaryWindowMinutes: null });
                          else if (v === 'daily_usd') setSeatRule({ billingMode: 'budget_usd', primaryWindowMinutes: 1440, secondaryWindowMinutes: null });
                        }}
                      >
                        <option value="auto">自动识别（优先读取原生窗口）</option>
                        <option value="5h_and_weekly">5 小时 + 周额度（双窗口）</option>
                        <option value="weekly_only">仅周额度（7 天滚动）</option>
                        <option value="daily_only">仅日额度（自然日重置）</option>
                        <option value="daily_usd">按日美元预算 ($)</option>
                      </select>
                      <input
                        className="clean-input num"
                        style={{ height: 30, fontSize: 11.5 }}
                        type="number"
                        min="0"
                        step="1"
                        placeholder="主上限 ($)"
                        disabled={templateVal === 'auto'}
                        value={cur.primaryLimit ?? cur.dailyBudgetUsd ?? ''}
                        onChange={e => {
                          const val = e.target.value ? Number(e.target.value) : null;
                          setSeatRule({ primaryLimit: val, dailyBudgetUsd: val });
                        }}
                      />
                    </div>
                  );
                })}
              </div>
            </div>
            <div className="modal-footer-bar">
              <button type="button" className="btn-pill btn-soft" onClick={() => setQuotaModalOpen(false)}>取消</button>
              <button type="submit" className="btn-pill btn-indigo" disabled={savingQuota}>
                {savingQuota ? '保存中...' : '保存规则'}
              </button>
            </div>
          </form>
        </div>
      )}

      {pricingDraft && (
        <div className="modal-backdrop" onClick={() => setPricingDraft(null)}>
          <form className="modal-surface narrow" onSubmit={handleSavePricing} onClick={e => e.stopPropagation()}>
            <div style={{ padding: '18px 22px 14px', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
              <div>
                <div style={{ fontSize: 15, fontWeight: 700 }}>配置模型百万 Token 单价</div>
                <div style={{ fontSize: 12, color: 'var(--text-muted)', marginTop: 2 }}>保存后立即重算全量历史调用成本（USD / 1M Tokens）</div>
              </div>
              <button type="button" className="btn-pill btn-soft btn-sm" onClick={() => setPricingDraft(null)}>关闭</button>
            </div>
            <div className="soft-divider" />
            <div style={{ padding: '18px 22px', display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
              <div>
                <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>模型匹配关键字</div>
                <input className="clean-input mono" required value={pricingDraft.pattern} onChange={e => setPricingDraft({ ...pricingDraft, pattern: e.target.value })} />
              </div>
              <div>
                <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>显示名称</div>
                <input className="clean-input" value={pricingDraft.displayName} onChange={e => setPricingDraft({ ...pricingDraft, displayName: e.target.value })} />
              </div>
              <div>
                <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>输入 ($/1M)</div>
                <input className="clean-input num" type="number" step="0.001" value={pricingDraft.inputPerMillion} onChange={e => setPricingDraft({ ...pricingDraft, inputPerMillion: Number(e.target.value) })} />
              </div>
              <div>
                <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>输出 ($/1M)</div>
                <input className="clean-input num" type="number" step="0.001" value={pricingDraft.outputPerMillion} onChange={e => setPricingDraft({ ...pricingDraft, outputPerMillion: Number(e.target.value) })} />
              </div>
              <div>
                <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>缓存读取 ($/1M)</div>
                <input className="clean-input num" type="number" step="0.001" value={pricingDraft.cacheReadPerMillion} onChange={e => setPricingDraft({ ...pricingDraft, cacheReadPerMillion: Number(e.target.value) })} />
              </div>
              <div>
                <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>缓存创建 ($/1M)</div>
                <input className="clean-input num" type="number" step="0.001" value={pricingDraft.cacheWritePerMillion} onChange={e => setPricingDraft({ ...pricingDraft, cacheWritePerMillion: Number(e.target.value) })} />
              </div>
            </div>
            <div className="modal-footer-bar">
              <button type="button" className="btn-pill btn-soft" onClick={() => setPricingDraft(null)}>取消</button>
              <button type="submit" className="btn-pill btn-indigo">保存并重算</button>
            </div>
          </form>
        </div>
      )}

      {auditReport && (
        <div className="modal-backdrop" onClick={() => setAuditReport(null)}>
          <div className="modal-surface" style={{ width: 680, maxWidth: '94vw', maxHeight: '86vh', overflowY: 'auto' }} onClick={e => e.stopPropagation()}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
              <div>
                <div className="card-title-sm">单会话上下文膨胀与工具调用审计</div>
                <div className="mono" style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 2 }}>
                  {auditReport.appName} · {auditReport.conversationId}
                  {auditReport.workspace ? ` · ${privacyMode ? 'workspace/**' : auditReport.workspace}` : ''}
                </div>
              </div>
              <button type="button" className="btn-pill btn-soft btn-sm" onClick={() => setAuditReport(null)}>关闭</button>
            </div>

            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 10 }}>
              <div style={{ background: 'var(--bg-subtle)', padding: '10px 12px', borderRadius: 10 }}>
                <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>总轮次</div>
                <div className="num" style={{ fontSize: 17, fontWeight: 700 }}>{auditReport.totalTurns}</div>
              </div>
              <div style={{ background: 'var(--bg-subtle)', padding: '10px 12px', borderRadius: 10 }}>
                <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>峰值上下文</div>
                <div className="num" style={{ fontSize: 17, fontWeight: 700 }}>{formatCompactTokens(auditReport.peakContextTokens)}</div>
              </div>
              <div style={{ background: 'var(--bg-subtle)', padding: '10px 12px', borderRadius: 10 }}>
                <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>上下文膨胀倍率</div>
                <div className="num" style={{ fontSize: 17, fontWeight: 700, color: auditReport.contextGrowthRatio >= 4 ? 'var(--accent-amber-text)' : undefined }}>
                  {auditReport.contextGrowthRatio}x
                </div>
              </div>
              <div style={{ background: 'var(--bg-subtle)', padding: '10px 12px', borderRadius: 10 }}>
                <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>费用 / 缓存省</div>
                <div className="num" style={{ fontSize: 14, fontWeight: 700, color: 'var(--accent-emerald-text)' }}>
                  ${auditReport.totalCostUsd.toFixed(3)} / -${auditReport.totalCacheSavedUsd.toFixed(3)}
                </div>
              </div>
            </div>

            {auditReport.optimizationHint && (
              <div style={{ background: 'var(--accent-amber-soft)', color: 'var(--accent-amber-text)', padding: '9px 12px', borderRadius: 10, fontSize: 12 }}>
                {auditReport.optimizationHint}
              </div>
            )}

            {auditReport.toolBreakdown.length > 0 && (
              <div>
                <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 6 }}>工具调用频次分布（零正文元数据）</div>
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
                  {auditReport.toolBreakdown.map(t => (
                    <span key={t.toolName} className="pill-tag tag-indigo mono">
                      {t.toolName} × {t.calls}
                    </span>
                  ))}
                </div>
              </div>
            )}

            <div className="table-scroll-wrap">
              <table className="clean-table">
                <thead>
                  <tr>
                    <th>#</th>
                    <th>模型</th>
                    <th className="num-r">上下文总量</th>
                    <th className="num-r">输出</th>
                    <th>调用工具</th>
                    <th className="num-r">费用</th>
                  </tr>
                </thead>
                <tbody>
                  {auditReport.turns.slice(0, 40).map(turn => (
                    <tr key={turn.turnIndex}>
                      <td className="num">{turn.turnIndex}</td>
                      <td>
                        <span className="mono cell-ellipsis" style={{ fontSize: 11.5, maxWidth: 160 }} title={turn.model}>
                          {turn.model}
                        </span>
                      </td>
                      <td className="num-r num">{formatCompactTokens(turn.cumulativeContextTokens)}</td>
                      <td className="num-r num">{formatCompactTokens(turn.outputTokens)}</td>
                      <td className="mono" style={{ fontSize: 11 }}>
                        {turn.toolsCalled.length > 0 ? turn.toolsCalled.join(', ') : '—'}
                      </td>
                      <td className="num-r num">
                        {turn.costUsd !== null && turn.costUsd !== undefined ? `$${turn.costUsd.toFixed(4)}` : '—'}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        </div>
      )}
    </div>
  );
};
