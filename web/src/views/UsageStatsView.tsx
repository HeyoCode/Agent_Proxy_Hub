import React, { useMemo, useState, useEffect, useCallback } from 'react';
import {
  ModelPricingRule,
  UsageDashboardResponse,
  api,
  formatBytes,
  formatCompactTokens,
  formatRate
} from '../api';
import { CustomSelect } from '../components/CustomSelect';
import { ClockRangeIcon, CpuModelIcon, PlusIcon, RefreshSpinIcon } from '../components/Icons';
import { SegmentedTabs } from '../components/SegmentedTabs';

interface Props {
  onToast: (msg: string) => void;
}

type MetricKey = 'cost' | 'requests' | 'tokens' | 'cacheRate';
type SubTab = 'logs' | 'models' | 'appsProxy' | 'pricing';

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

export const UsageStatsView: React.FC<Props> = ({ onToast }) => {
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
  const [pricings, setPricings] = useState<ModelPricingRule[]>([]);
  const [syncing, setSyncing] = useState(false);
  const [autoSync, setAutoSync] = useState(true);

  const [pricingDraft, setPricingDraft] = useState<Omit<ModelPricingRule, 'isCustom'> | null>(null);

  const loadAll = useCallback(async (targetPage: number) => {
    const [dash, rules] = await Promise.all([
      api.getStatsDashboard({ range, appFilter, modelFilter, page: targetPage, pageSize }),
      api.getPricings()
    ]);
    setData(dash);
    setPricings(rules);
  }, [range, appFilter, modelFilter, pageSize]);

  useEffect(() => {
    void loadAll(1);
  }, [range, appFilter, modelFilter, pageSize, loadAll]);

  useEffect(() => {
    void loadAll(page);
  }, [page, loadAll]);

  // 用量统计每 5 秒自动无感同步，新增对话无需手动点击刷新即可实时呈现
  useEffect(() => {
    if (!autoSync) return;
    const timer = window.setInterval(() => {
      if (!pricingDraft && !syncing) {
        void loadAll(page).catch(() => {});
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

      {/* 四大指标联动柱图 */}
      <div className="surface-card">
        <div className="kpi-strip">
          <button type="button" className={`kpi-strip-btn ${activeMetric === 'cost' ? 'active' : ''}`} onClick={() => setActiveMetric('cost')}>
            <div style={{ fontSize: 11.5, color: 'var(--text-secondary)', marginBottom: 4 }}>总成本 (已定价请求)</div>
            <div className="num" style={{ fontSize: 23, fontWeight: 700, letterSpacing: '-0.02em' }}>${(data?.totalCostUsd ?? 0).toFixed(2)}</div>
            <div className="num" style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 3 }}>
              {data && data.unpricedRequests > 0 ? `${data.unpricedRequests} 条未配单价` : '全部请求已按单价核算'}
            </div>
          </button>

          <button type="button" className={`kpi-strip-btn ${activeMetric === 'requests' ? 'active' : ''}`} onClick={() => setActiveMetric('requests')}>
            <div style={{ fontSize: 11.5, color: 'var(--text-secondary)', marginBottom: 4 }}>总请求数</div>
            <div className="num" style={{ fontSize: 23, fontWeight: 700, letterSpacing: '-0.02em' }}>{(data?.totalRequests ?? 0).toLocaleString('en-US')}</div>
            <div className="num" style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 3 }}>
              代理 {data?.totalProxyConnections ?? 0} 次 · 已扫 {data?.scannedFilesCount ?? 0} 文件
            </div>
          </button>

          <button type="button" className={`kpi-strip-btn ${activeMetric === 'tokens' ? 'active' : ''}`} onClick={() => setActiveMetric('tokens')}>
            <div style={{ fontSize: 11.5, color: 'var(--text-secondary)', marginBottom: 4 }}>真实消耗 Tokens</div>
            <div className="num" style={{ fontSize: 23, fontWeight: 700, letterSpacing: '-0.02em' }}>{formatCompactTokens(data?.totalTokens ?? 0)}</div>
            <div className="num" style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 3 }}>
              入 {formatCompactTokens(data?.effectiveInputTokens ?? 0)} · 出 {formatCompactTokens(data?.outputTokens ?? 0)} · 缓 {formatCompactTokens(data?.cacheReadTokens ?? 0)}
            </div>
          </button>

          <button type="button" className={`kpi-strip-btn ${activeMetric === 'cacheRate' ? 'active' : ''}`} onClick={() => setActiveMetric('cacheRate')}>
            <div style={{ fontSize: 11.5, color: 'var(--text-secondary)', marginBottom: 4 }}>缓存命中率</div>
            <div className="num" style={{ fontSize: 23, fontWeight: 700, letterSpacing: '-0.02em', color: 'var(--accent-indigo)' }}>
              {(data?.cacheHitRate ?? 0).toFixed(1)}%
            </div>
            <div className="num" style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 3 }}>
              上次同步 {syncTimeStr} ({data?.syncElapsedMs ?? 0} ms)
            </div>
          </button>
        </div>

        <div className="soft-divider" />

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

      {/* 底部多维明细无框纯白卡片 */}
      <div className="surface-card">
        <div style={{ padding: '12px 18px', display: 'flex', alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap', gap: 8 }}>
          <SegmentedTabs<SubTab>
            value={subTab}
            onChange={setSubTab}
            items={[
              { value: 'logs', label: '请求日志', count: data?.totalLogsCount ?? 0 },
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
            <div style={{ overflowX: 'auto' }}>
              <table className="clean-table">
                <thead>
                  <tr>
                    <th>时间</th>
                    <th>应用</th>
                    <th>来源</th>
                    <th>模型</th>
                    <th>成本</th>
                    <th className="num-r">有效输入</th>
                    <th className="num-r">输出</th>
                    <th className="num-r">缓存命中</th>
                    <th className="num-r">流速</th>
                    <th className="num-r">首字(估)</th>
                    <th className="num-r">耗时</th>
                  </tr>
                </thead>
                <tbody>
                  {(data?.logs ?? []).length === 0 ? (
                    <tr>
                      <td colSpan={11} style={{ textAlign: 'center', padding: 30, color: 'var(--text-muted)' }}>
                        当前时间范围内暂无会话记录（可切换「全部历史」或点击右上角「按需同步」）
                      </td>
                    </tr>
                  ) : (
                    (data?.logs ?? []).map(row => (
                      <tr key={row.id}>
                        <td className="num nowrap" style={{ color: 'var(--text-secondary)' }}>
                          {new Date(row.timestamp).toLocaleTimeString('zh-CN', { hour12: false })}
                        </td>
                        <td className="nowrap"><span className="pill-tag tag-neutral">{row.appName}</span></td>
                        <td className="nowrap" style={{ color: 'var(--text-secondary)' }}>{row.source}</td>
                        <td className="mono nowrap" style={{ fontWeight: 500, fontSize: 12 }}>{row.model}</td>
                        <td className="nowrap">
                          {row.isPriced && row.costUsd !== null && row.costUsd !== undefined ? (
                            <span className="num" style={{ color: 'var(--accent-emerald-text)', fontWeight: 600 }}>
                              ${row.costUsd.toFixed(4)}
                            </span>
                          ) : (
                            <button
                              type="button"
                              className="btn-pill btn-soft btn-sm"
                              style={{ height: 24, fontSize: 11, padding: '0 8px' }}
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
                          {row.tokensPerSecond ? `~${row.tokensPerSecond} tok/s` : '—'}
                        </td>
                        <td className="num-r num nowrap" style={{ color: row.firstTokenSeconds ? 'var(--text-secondary)' : 'var(--text-muted)' }}>
                          {row.firstTokenSeconds ? `~${row.firstTokenSeconds}s` : '—'}
                        </td>
                        <td className="num-r num nowrap" style={{ color: 'var(--text-secondary)' }}>
                          {row.durationSeconds > 0 ? `~${row.durationSeconds}s` : '—'}
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

        {subTab === 'models' && (
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
                  <td className="mono" style={{ fontWeight: 600, fontSize: 12 }}>{m.model}</td>
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
                        className="btn-pill btn-soft btn-sm"
                        style={{ height: 24, fontSize: 11 }}
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
        )}

        {subTab === 'appsProxy' && (
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
                    <div style={{ fontWeight: 600 }}>{row.boundNodeName ?? '—'}</div>
                    <div className="num" style={{ fontSize: 11, color: 'var(--text-secondary)' }}>{row.exitIp ?? '—'}</div>
                  </td>
                  <td className="num-r num">
                    ↑ {formatBytes(row.bytesTx)} · ↓ {formatBytes(row.bytesRx)} ({row.proxyConnections} 次)
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        {subTab === 'pricing' && (
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
                  <td className="mono" style={{ fontWeight: 600, fontSize: 12 }}>{r.pattern}</td>
                  <td>{r.displayName}</td>
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
        )}
      </div>

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
    </div>
  );
};
