import React, { useEffect, useState } from 'react';
import {
  ModelPricingRule,
  UsageDashboardResponse,
  api,
  formatBytes,
  formatCompactTokens
} from '../api';

interface Props {
  onToast: (msg: string) => void;
}

type MetricKey = 'cost' | 'requests' | 'tokens' | 'cacheRate';
type SubTab = 'logs' | 'models' | 'appsProxy' | 'pricing';

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

  // 快速配置单价弹窗状态
  const [pricingDraft, setPricingDraft] = useState<Omit<ModelPricingRule, 'isCustom'> | null>(null);

  async function loadAll(targetPage = page) {
    const [dash, rules] = await Promise.all([
      api.getStatsDashboard({ range, appFilter, modelFilter, page: targetPage, pageSize }),
      api.getPricings()
    ]);
    setData(dash);
    setPricings(rules);
  }

  useEffect(() => {
    void loadAll(1);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [range, appFilter, modelFilter, pageSize]);

  useEffect(() => {
    void loadAll(page);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [page]);

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
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      {/* 顶部筛选栏 + 按需同步 + 代理专线流量标识 */}
      <div className="tx-tray">
        <div className="tx-card" style={{ padding: '10px 14px', display: 'flex', alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap', gap: 10 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
            <select
              className="tx-select"
              style={{ width: 'auto', minWidth: 104, fontWeight: 600 }}
              value={range}
              onChange={e => { setPage(1); setRange(e.target.value); }}
            >
              <option value="today">当天</option>
              <option value="24h">近 24 小时</option>
              <option value="7d">近 7 天</option>
              <option value="30d">近 30 天</option>
              <option value="all">全部历史</option>
            </select>

            <div className="dock-tray" style={{ padding: 3, borderRadius: 11 }}>
              <button
                className={`dock-tab ${appFilter === 'all' ? 'active' : ''}`}
                style={{ height: 25, fontSize: 11.5, padding: '0 10px', borderRadius: 8 }}
                onClick={() => { setPage(1); setAppFilter('all'); }}
              >
                全部应用
              </button>
              {(data?.appChips ?? []).map(chip => (
                <button
                  key={chip.name}
                  className={`dock-tab ${appFilter === chip.name ? 'active' : ''}`}
                  style={{ height: 25, fontSize: 11.5, padding: '0 10px', borderRadius: 8 }}
                  onClick={() => { setPage(1); setAppFilter(chip.name); }}
                >
                  <span>{chip.name}</span>
                  <span className="kbd">{chip.count}</span>
                </button>
              ))}
            </div>

            <select
              className="tx-select"
              style={{ width: 'auto', minWidth: 155 }}
              value={modelFilter}
              onChange={e => { setPage(1); setModelFilter(e.target.value); }}
            >
              <option value="all">全部模型 ({data?.availableModels.length ?? 0})</option>
              {(data?.availableModels ?? []).map(m => (
                <option key={m} value={m}>{m}</option>
              ))}
            </select>
          </div>

          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <span className="badge badge-neutral mono" title="经过本软件独立代理隧道的真实网络上下行总量">
              专线流量 ↑ {formatBytes(data?.totalProxyTxBytes ?? 0)} · ↓ {formatBytes(data?.totalProxyRxBytes ?? 0)}
            </span>
            <button className="tx-btn tx-btn-sec tx-btn-sm" onClick={handleSyncNow} disabled={syncing}>
              <span>{syncing ? '同步中...' : '↻ 按需同步'}</span>
            </button>
          </div>
        </div>
      </div>

      {/* 4 大可点击联动指标卡 + 分时段柱状图 */}
      <div className="tx-tray">
        <div className="tx-card">
          <div className="kpi-4-grid">
            <button className={`kpi-btn ${activeMetric === 'cost' ? 'active' : ''}`} onClick={() => setActiveMetric('cost')}>
              <div style={{ fontSize: 11.5, color: 'var(--text-secondary)', marginBottom: 4 }}>总成本 (已定价请求)</div>
              <div className="mono" style={{ fontSize: 22, fontWeight: 700 }}>${(data?.totalCostUsd ?? 0).toFixed(2)}</div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 2 }}>
                {data && data.unpricedRequests > 0
                  ? `${data.unpricedRequests} 条未配单价`
                  : '全部请求已按单价核算'}
              </div>
            </button>

            <button className={`kpi-btn ${activeMetric === 'requests' ? 'active' : ''}`} onClick={() => setActiveMetric('requests')}>
              <div style={{ fontSize: 11.5, color: 'var(--text-secondary)', marginBottom: 4 }}>总请求数</div>
              <div className="mono" style={{ fontSize: 22, fontWeight: 700 }}>{(data?.totalRequests ?? 0).toLocaleString('en-US')}</div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 2 }}>
                代理连接 {data?.totalProxyConnections ?? 0} 次 · 已扫 {data?.scannedFilesCount ?? 0} 文件
              </div>
            </button>

            <button className={`kpi-btn ${activeMetric === 'tokens' ? 'active' : ''}`} onClick={() => setActiveMetric('tokens')}>
              <div style={{ fontSize: 11.5, color: 'var(--text-secondary)', marginBottom: 4 }}>真实消耗 Tokens</div>
              <div className="mono" style={{ fontSize: 22, fontWeight: 700 }}>{formatCompactTokens(data?.totalTokens ?? 0)}</div>
              <div className="mono" style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 2 }}>
                入 {formatCompactTokens(data?.effectiveInputTokens ?? 0)} · 出 {formatCompactTokens(data?.outputTokens ?? 0)} · 缓 {formatCompactTokens(data?.cacheReadTokens ?? 0)}
              </div>
            </button>

            <button className={`kpi-btn ${activeMetric === 'cacheRate' ? 'active' : ''}`} onClick={() => setActiveMetric('cacheRate')}>
              <div style={{ fontSize: 11.5, color: 'var(--text-secondary)', marginBottom: 4 }}>缓存命中率</div>
              <div className="mono" style={{ fontSize: 22, fontWeight: 700, color: 'var(--sky-text)' }}>
                {(data?.cacheHitRate ?? 0).toFixed(1)}%
              </div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 2 }}>
                上次同步 {syncTimeStr} ({data?.syncElapsedMs ?? 0} ms)
              </div>
            </button>
          </div>

          <div className="tx-sep" />

          <div className="chart-area">
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 4 }}>
              <span style={{ fontSize: 12, fontWeight: 600, color: 'var(--text-secondary)' }}>{metricLabel}</span>
              <span className="mono" style={{ fontSize: 11, color: 'var(--text-muted)' }}>
                数据来源：本地免解密会话日志 + 独立物理直出代理遥测
              </span>
            </div>

            <div className="chart-bars">
              {timeline.map((pt, i) => {
                const v = metricValues[i];
                const pct = maxVal > 0 ? Math.round((v / maxVal) * 100) : 0;
                const h = v > 0 ? Math.max(8, pct) : 0;
                const tip = `${pt.label} | 请求 ${pt.requests} 次 | Tokens ${formatCompactTokens(pt.totalTokens)} | 缓存命中 ${pt.cacheHitRate}% | $${pt.costUsd.toFixed(3)}`;
                return (
                  <div className="chart-col" key={pt.label} title={tip}>
                    <div
                      className={`chart-bar ${v <= 0 ? 'empty' : ''}`}
                      style={{ height: `${h}%` }}
                    />
                  </div>
                );
              })}
            </div>
            <div className="chart-labels">
              {timeline.map((pt, idx) => {
                const show = timeline.length <= 16 || idx % 2 === 0 || idx === timeline.length - 1;
                return (
                  <div className="chart-lbl" key={pt.label}>
                    {show ? pt.label : ''}
                  </div>
                );
              })}
            </div>
          </div>
        </div>
      </div>

      {/* 底部多维明细卡片：请求日志 / 模型分布 / 应用与专线归属 / 模型定价 */}
      <div className="tx-tray">
        <div className="tx-card">
          <div style={{ padding: '10px 14px', display: 'flex', alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap', gap: 8 }}>
            <div className="dock-tray" style={{ padding: 3, borderRadius: 11 }}>
              <button className={`dock-tab ${subTab === 'logs' ? 'active' : ''}`} style={{ height: 26, fontSize: 12, borderRadius: 8 }} onClick={() => setSubTab('logs')}>
                请求日志 <span className="kbd">{data?.totalLogsCount ?? 0}</span>
              </button>
              <button className={`dock-tab ${subTab === 'models' ? 'active' : ''}`} style={{ height: 26, fontSize: 12, borderRadius: 8 }} onClick={() => setSubTab('models')}>
                按模型统计 <span className="kbd">{data?.modelBreakdown.length ?? 0}</span>
              </button>
              <button className={`dock-tab ${subTab === 'appsProxy' ? 'active' : ''}`} style={{ height: 26, fontSize: 12, borderRadius: 8 }} onClick={() => setSubTab('appsProxy')}>
                应用与专线归属 <span className="kbd">{data?.appAndProxyBreakdown.length ?? 0}</span>
              </button>
              <button className={`dock-tab ${subTab === 'pricing' ? 'active' : ''}`} style={{ height: 26, fontSize: 12, borderRadius: 8 }} onClick={() => setSubTab('pricing')}>
                模型定价表 <span className="kbd">{pricings.length}</span>
              </button>
            </div>

            {subTab === 'pricing' && (
              <button
                className="tx-btn tx-btn-primary tx-btn-sm"
                onClick={() => setPricingDraft({
                  pattern: '',
                  displayName: '',
                  inputPerMillion: 1.25,
                  outputPerMillion: 10.0,
                  cacheReadPerMillion: 0.125,
                  cacheWritePerMillion: 0
                })}
              >
                + 新增模型单价
              </button>
            )}
          </div>

          <div className="tx-sep" />

          {subTab === 'logs' && (
            <>
              <div style={{ overflowX: 'auto' }}>
                <table className="data-table">
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
                      <th className="num-r">耗时</th>
                    </tr>
                  </thead>
                  <tbody>
                    {(data?.logs ?? []).length === 0 ? (
                      <tr>
                        <td colSpan={10} style={{ textAlign: 'center', padding: 26, color: 'var(--text-muted)' }}>
                          当前时间范围内暂无会话记录（可切换「全部历史」或点击右上角「按需同步」）
                        </td>
                      </tr>
                    ) : (
                      (data?.logs ?? []).map(row => (
                        <tr key={row.id}>
                          <td className="mono" style={{ color: 'var(--text-secondary)' }}>
                            {new Date(row.timestamp).toLocaleTimeString('zh-CN', { hour12: false })}
                          </td>
                          <td><span className="badge badge-neutral">{row.appName}</span></td>
                          <td style={{ color: 'var(--text-secondary)' }}>{row.source}</td>
                          <td className="mono" style={{ fontWeight: 600 }}>{row.model}</td>
                          <td>
                            {row.isPriced && row.costUsd !== null && row.costUsd !== undefined ? (
                              <span className="mono" style={{ color: 'var(--emerald-text)', fontWeight: 600 }}>
                                ${row.costUsd.toFixed(4)}
                              </span>
                            ) : (
                              <button
                                className="tx-btn tx-btn-sec tx-btn-sm"
                                style={{ height: 22, fontSize: 11, padding: '0 7px' }}
                                onClick={() => setPricingDraft({
                                  pattern: row.model,
                                  displayName: row.model,
                                  inputPerMillion: 1.25,
                                  outputPerMillion: 10.0,
                                  cacheReadPerMillion: 0.125,
                                  cacheWritePerMillion: 0
                                })}
                                title="点击为该模型配置百万 Token 单价"
                              >
                                未定价 · 配价
                              </button>
                            )}
                          </td>
                          <td className="num-r mono">{row.effectiveInputTokens.toLocaleString('en-US')}</td>
                          <td className="num-r mono">{row.outputTokens.toLocaleString('en-US')}</td>
                          <td className="num-r mono">{formatCompactTokens(row.cacheReadTokens)}</td>
                          <td className="num-r mono" style={{ color: row.tokensPerSecond ? 'var(--emerald-text)' : 'var(--text-muted)' }}>
                            {row.tokensPerSecond ? `~${row.tokensPerSecond} tok/s` : '—'}
                          </td>
                          <td className="num-r mono" style={{ color: 'var(--text-secondary)' }}>
                            {row.durationSeconds > 0 ? `~${row.durationSeconds}s` : '—'}
                          </td>
                        </tr>
                      ))
                    )}
                  </tbody>
                </table>
              </div>
              <div className="tx-sep" />
              <div style={{ padding: '9px 14px', display: 'flex', justifyContent: 'space-between', alignItems: 'center', fontSize: 12, color: 'var(--text-secondary)' }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                  <span className="mono">共 {data?.totalLogsCount ?? 0} 条记录</span>
                  <select className="tx-select" style={{ width: 'auto', padding: '3px 7px' }} value={pageSize} onChange={e => { setPage(1); setPageSize(Number(e.target.value)); }}>
                    <option value={20}>每页 20 条</option>
                    <option value={50}>每页 50 条</option>
                  </select>
                </div>
                <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                  <button className="tx-btn tx-btn-sec tx-btn-sm" disabled={page <= 1} onClick={() => setPage(p => Math.max(1, p - 1))}>上一页</button>
                  <span className="mono" style={{ padding: '0 6px' }}>{page} / {totalPages}</span>
                  <button className="tx-btn tx-btn-sec tx-btn-sm" disabled={page >= totalPages} onClick={() => setPage(p => Math.min(totalPages, p + 1))}>下一页</button>
                </div>
              </div>
            </>
          )}

          {subTab === 'models' && (
            <table className="data-table">
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
                    <td className="mono" style={{ fontWeight: 700 }}>{m.model}</td>
                    <td className="num-r mono">{m.requests}</td>
                    <td className="num-r mono">{formatCompactTokens(m.effectiveInputTokens)}</td>
                    <td className="num-r mono">{formatCompactTokens(m.outputTokens)}</td>
                    <td className="num-r mono">{formatCompactTokens(m.cacheReadTokens)}</td>
                    <td className="num-r mono" style={{ color: 'var(--sky-text)', fontWeight: 600 }}>{m.cacheHitRate}%</td>
                    <td className="num-r mono">{m.avgTokensPerSecond > 0 ? `~${m.avgTokensPerSecond} tok/s` : '—'}</td>
                    <td className="num-r mono">
                      {m.hasPricing ? `$${m.costUsd.toFixed(4)}` : (
                        <button
                          className="tx-btn tx-btn-sec tx-btn-sm"
                          style={{ height: 22, fontSize: 11 }}
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
            <table className="data-table">
              <thead>
                <tr>
                  <th>AI 应用 / 席位</th>
                  <th className="num-r">调用次数</th>
                  <th className="num-r">总消耗 Tokens</th>
                  <th className="num-r">缓存命中率</th>
                  <th>独立入口端口</th>
                  <th>当前绑定专线与出口 IP</th>
                  <th className="num-r">代理上行 / 下行流量</th>
                </tr>
              </thead>
              <tbody>
                {(data?.appAndProxyBreakdown ?? []).map(row => (
                  <tr key={row.appName}>
                    <td style={{ fontWeight: 700 }}>{row.appName}</td>
                    <td className="num-r mono">{row.requests}</td>
                    <td className="num-r mono">{formatCompactTokens(row.totalTokens)}</td>
                    <td className="num-r mono">{row.cacheHitRate}%</td>
                    <td className="mono">{row.seatPort ?? '—'}</td>
                    <td>
                      <div style={{ fontWeight: 600 }}>{row.boundNodeName ?? '—'}</div>
                      <div className="mono" style={{ fontSize: 11, color: 'var(--text-secondary)' }}>{row.exitIp ?? '—'}</div>
                    </td>
                    <td className="num-r mono">
                      ↑ {formatBytes(row.bytesTx)} · ↓ {formatBytes(row.bytesRx)} ({row.proxyConnections} 次)
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}

          {subTab === 'pricing' && (
            <table className="data-table">
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
                    <td className="mono" style={{ fontWeight: 700 }}>{r.pattern}</td>
                    <td>{r.displayName}</td>
                    <td className="num-r mono">${r.inputPerMillion}</td>
                    <td className="num-r mono">${r.outputPerMillion}</td>
                    <td className="num-r mono">${r.cacheReadPerMillion}</td>
                    <td className="num-r mono">${r.cacheWritePerMillion}</td>
                    <td className="num-r">
                      <span className={`badge ${r.isCustom ? 'badge-indigo' : 'badge-neutral'}`}>
                        {r.isCustom ? '自定义' : '内置'}
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </div>

      {/* 新增/修改模型定价弹窗 */}
      {pricingDraft && (
        <div className="modal-mask">
          <div className="modal-tray narrow">
            <form className="modal-card" onSubmit={handleSavePricing}>
              <div style={{ padding: '14px 18px', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                <div>
                  <strong style={{ fontSize: 14 }}>配置模型百万 Token 单价</strong>
                  <div style={{ fontSize: 11.5, color: 'var(--text-secondary)' }}>保存后将立即重算全量历史调用成本（单位：USD / 1M Tokens）</div>
                </div>
                <button type="button" className="tx-btn tx-btn-sec tx-btn-sm" onClick={() => setPricingDraft(null)}>关闭</button>
              </div>
              <div className="tx-sep" />
              <div style={{ padding: '15px 18px', display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
                <div>
                  <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>模型匹配关键字 / 完整名</div>
                  <input className="tx-input mono" required value={pricingDraft.pattern} onChange={e => setPricingDraft({ ...pricingDraft, pattern: e.target.value })} />
                </div>
                <div>
                  <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>显示名称</div>
                  <input className="tx-input" value={pricingDraft.displayName} onChange={e => setPricingDraft({ ...pricingDraft, displayName: e.target.value })} />
                </div>
                <div>
                  <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>输入单价 ($/1M)</div>
                  <input className="tx-input mono" type="number" step="0.001" value={pricingDraft.inputPerMillion} onChange={e => setPricingDraft({ ...pricingDraft, inputPerMillion: Number(e.target.value) })} />
                </div>
                <div>
                  <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>输出单价 ($/1M)</div>
                  <input className="tx-input mono" type="number" step="0.001" value={pricingDraft.outputPerMillion} onChange={e => setPricingDraft({ ...pricingDraft, outputPerMillion: Number(e.target.value) })} />
                </div>
                <div>
                  <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>缓存读取 ($/1M)</div>
                  <input className="tx-input mono" type="number" step="0.001" value={pricingDraft.cacheReadPerMillion} onChange={e => setPricingDraft({ ...pricingDraft, cacheReadPerMillion: Number(e.target.value) })} />
                </div>
                <div>
                  <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>缓存创建 ($/1M)</div>
                  <input className="tx-input mono" type="number" step="0.001" value={pricingDraft.cacheWritePerMillion} onChange={e => setPricingDraft({ ...pricingDraft, cacheWritePerMillion: Number(e.target.value) })} />
                </div>
              </div>
              <div className="tx-sep" />
              <div style={{ padding: '12px 18px', background: 'var(--card-subtle)', display: 'flex', justifyContent: 'flex-end', gap: 8 }}>
                <button type="button" className="tx-btn tx-btn-sec" onClick={() => setPricingDraft(null)}>取消</button>
                <button type="submit" className="tx-btn tx-btn-primary">保存并重算成本</button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
};
