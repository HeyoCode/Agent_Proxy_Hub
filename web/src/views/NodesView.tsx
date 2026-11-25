import React, { useEffect, useState } from 'react';
import { BatchParseResult, ProxyNode, ProxyProtocol, api } from '../api';

interface Props {
  nodes: ProxyNode[];
  showManual: boolean;
  showBatch: boolean;
  onCloseModals: () => void;
  onOpenManual: () => void;
  onOpenBatch: () => void;
  onReload: () => Promise<void>;
  onToast: (msg: string) => void;
}

export const NodesView: React.FC<Props> = ({
  nodes, showManual, showBatch, onCloseModals, onOpenManual, onOpenBatch, onReload, onToast
}) => {
  const [filter, setFilter] = useState<'all' | 'SOCKS' | 'HTTP'>('all');
  const [testingAll, setTestingAll] = useState(false);

  // 手动添加表单
  const [mProto, setMProto] = useState<ProxyProtocol>('SOCKS5');
  const [mName, setMName] = useState('');
  const [mGroup, setMGroup] = useState('AI 静态住宅池');
  const [mHost, setMHost] = useState('');
  const [mPort, setMPort] = useState('1080');
  const [mUser, setMUser] = useState('');
  const [mPass, setMPass] = useState('');

  // 批量导入表单
  const [batchText, setBatchText] = useState(
    'socks5://user:pass@127.0.0.1:1080#示例SOCKS5线路\nhttp://user:pass@127.0.0.1:8080#示例HTTP线路'
  );
  const [batchProto, setBatchProto] = useState<ProxyProtocol>('SOCKS5');
  const [batchGroup, setBatchGroup] = useState('批量导入池');
  const [preview, setPreview] = useState<BatchParseResult | null>(null);

  useEffect(() => {
    if (!showBatch) return;
    const t = setTimeout(() => {
      void api.previewBatch(batchText, batchProto, batchGroup).then(setPreview);
    }, 150);
    return () => clearTimeout(t);
  }, [showBatch, batchText, batchProto, batchGroup]);

  const filtered = nodes.filter(n => {
    if (filter === 'SOCKS') return n.protocol === 'SOCKS5' || n.protocol === 'SOCKS4';
    if (filter === 'HTTP') return n.protocol === 'HTTP' || n.protocol === 'HTTPS';
    return true;
  });

  async function handleSaveManual(e: React.FormEvent) {
    e.preventDefault();
    await api.addManualNode({
      protocol: mProto,
      name: mName,
      group: mGroup,
      host: mHost,
      port: Number(mPort) || 1080,
      username: mUser || undefined,
      password: mPass || undefined
    });
    setMHost('');
    setMName('');
    onCloseModals();
    await onReload();
    onToast('已添加独立线路到线路池');
  }

  async function handleImportBatch() {
    const res = await api.importBatch(batchText, batchProto, batchGroup);
    onCloseModals();
    await onReload();
    onToast(`导入完成：新增 ${res.addedCount} 条，重复跳过 ${res.duplicateCount} 条，异常 ${res.errorCount} 条`);
  }

  async function handleVerifyOne(id: string) {
    onToast('正在直绑物理网卡测试出口与 AI 连通性...');
    const res = await api.verifyNode(id);
    await onReload();
    onToast(res.status === 'online' ? `节点在线：${res.exitIp} (${res.latencyMs ?? 1} ms)` : `节点不可达：${res.ispSummary}`);
  }

  async function handleVerifyAll() {
    setTestingAll(true);
    try {
      await api.verifyAllNodes();
      await onReload();
      onToast('已完成全量线路物理直连验真');
    } finally {
      setTestingAll(false);
    }
  }

  async function handleDelete(id: string) {
    await api.deleteNode(id);
    await onReload();
    onToast('已移除线路');
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      <div className="tx-tray">
        <div className="tx-card">
          <div style={{ padding: '10px 14px', display: 'flex', alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap', gap: 10 }}>
            <div className="dock-tray" style={{ padding: 3, borderRadius: 11 }}>
              <button className={`dock-tab ${filter === 'all' ? 'active' : ''}`} style={{ height: 26, fontSize: 12, borderRadius: 8 }} onClick={() => setFilter('all')}>
                全部 ({nodes.length})
              </button>
              <button className={`dock-tab ${filter === 'SOCKS' ? 'active' : ''}`} style={{ height: 26, fontSize: 12, borderRadius: 8 }} onClick={() => setFilter('SOCKS')}>
                SOCKS5 / 4 ({nodes.filter(n => n.protocol === 'SOCKS5' || n.protocol === 'SOCKS4').length})
              </button>
              <button className={`dock-tab ${filter === 'HTTP' ? 'active' : ''}`} style={{ height: 26, fontSize: 12, borderRadius: 8 }} onClick={() => setFilter('HTTP')}>
                HTTP / HTTPS ({nodes.filter(n => n.protocol === 'HTTP' || n.protocol === 'HTTPS').length})
              </button>
            </div>

            <div style={{ display: 'flex', gap: 8 }}>
              <button className="tx-btn tx-btn-sec tx-btn-sm" onClick={handleVerifyAll} disabled={testingAll}>
                {testingAll ? '验真中...' : '全部物理直连验真'}
              </button>
              <button className="tx-btn tx-btn-sec tx-btn-sm" onClick={onOpenManual}>+ 手动添加</button>
              <button className="tx-btn tx-btn-primary tx-btn-sm" onClick={onOpenBatch}>批量导入</button>
            </div>
          </div>

          <div className="tx-sep" />

          <table className="data-table">
            <thead>
              <tr>
                <th>节点名称与分组</th>
                <th>协议</th>
                <th>代理入口地址</th>
                <th>真实落地出口 IP / 说明</th>
                <th>AI 可达性验真</th>
                <th className="num-r">延迟</th>
                <th className="num-r">操作</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map(n => (
                <tr key={n.id}>
                  <td>
                    <div style={{ fontWeight: 700 }}>{n.name}</div>
                    <div style={{ fontSize: 11, color: 'var(--text-secondary)' }}>{n.group}</div>
                  </td>
                  <td><span className="badge badge-indigo mono">{n.protocol}</span></td>
                  <td>
                    <div className="mono">{n.host}:{n.port}</div>
                    <div className="mono" style={{ fontSize: 11, color: 'var(--text-muted)' }}>
                      {n.username ? `认证：${n.username}` : '无认证'}
                    </div>
                  </td>
                  <td>
                    <div className="mono" style={{ fontWeight: 600 }}>{n.exitIp}</div>
                    <div style={{ fontSize: 11, color: 'var(--text-secondary)' }}>{n.ispSummary}</div>
                  </td>
                  <td>
                    <div style={{ display: 'flex', gap: 4, flexWrap: 'wrap' }}>
                      {n.aiReachability.map(tag => (
                        <span key={tag} className={`badge ${tag.includes('✕') || tag.includes('超时') ? 'badge-red' : 'badge-neutral'}`}>{tag}</span>
                      ))}
                    </div>
                  </td>
                  <td className="num-r mono" style={{ fontWeight: 700, color: n.status === 'offline' ? 'var(--red-text)' : (n.latencyMs ? 'var(--emerald-text)' : 'var(--text-muted)') }}>
                    {n.latencyMs ? `${n.latencyMs} ms` : (n.status === 'offline' ? '超时' : '待测')}
                  </td>
                  <td className="num-r">
                    <div style={{ display: 'inline-flex', gap: 6 }}>
                      <button className="tx-btn tx-btn-sec tx-btn-sm" onClick={() => handleVerifyOne(n.id)}>验真</button>
                      {n.id !== 'direct-phy' && (
                        <button className="tx-btn tx-btn-sec tx-btn-sm" onClick={() => handleDelete(n.id)}>删除</button>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {/* 手动添加弹窗 */}
      {showManual && (
        <div className="modal-mask">
          <div className="modal-tray narrow">
            <form className="modal-card" onSubmit={handleSaveManual}>
              <div style={{ padding: '14px 18px', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                <div>
                  <strong style={{ fontSize: 14.5 }}>手动添加独立代理线路</strong>
                  <div style={{ fontSize: 11.5, color: 'var(--text-secondary)' }}>支持 HTTP / HTTPS / SOCKS5 / SOCKS4，出站强制绑定物理网卡</div>
                </div>
                <button type="button" className="tx-btn tx-btn-sec tx-btn-sm" onClick={onCloseModals}>关闭</button>
              </div>
              <div className="tx-sep" />
              <div style={{ padding: '15px 18px', display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
                <div>
                  <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>协议类型</div>
                  <select className="tx-select" value={mProto} onChange={e => setMProto(e.target.value as ProxyProtocol)}>
                    <option value="SOCKS5">SOCKS5 / SOCKS5h</option>
                    <option value="HTTP">HTTP CONNECT</option>
                    <option value="HTTPS">HTTPS (TLS)</option>
                    <option value="SOCKS4">SOCKS4a</option>
                  </select>
                </div>
                <div>
                  <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>备注名称</div>
                  <input className="tx-input" placeholder="例如：US-AT&T-静态住宅" value={mName} onChange={e => setMName(e.target.value)} />
                </div>
                <div>
                  <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>服务器 IP / 域名 *</div>
                  <input className="tx-input mono" required placeholder="例如：38.154.227.14" value={mHost} onChange={e => setMHost(e.target.value)} />
                </div>
                <div>
                  <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>端口 *</div>
                  <input className="tx-input mono" required value={mPort} onChange={e => setMPort(e.target.value)} />
                </div>
                <div>
                  <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>用户名（可选）</div>
                  <input className="tx-input mono" value={mUser} onChange={e => setMUser(e.target.value)} />
                </div>
                <div>
                  <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>密码（可选）</div>
                  <input className="tx-input mono" type="password" value={mPass} onChange={e => setMPass(e.target.value)} />
                </div>
                <div style={{ gridColumn: 'span 2' }}>
                  <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>所属分组</div>
                  <input className="tx-input" value={mGroup} onChange={e => setMGroup(e.target.value)} />
                </div>
              </div>
              <div className="tx-sep" />
              <div style={{ padding: '12px 18px', background: 'var(--card-subtle)', display: 'flex', justifyContent: 'flex-end', gap: 8 }}>
                <button type="button" className="tx-btn tx-btn-sec" onClick={onCloseModals}>取消</button>
                <button type="submit" className="tx-btn tx-btn-primary">保存线路</button>
              </div>
            </form>
          </div>
        </div>
      )}

      {/* 批量导入弹窗 */}
      {showBatch && (
        <div className="modal-mask">
          <div className="modal-tray">
            <div className="modal-card">
              <div style={{ padding: '14px 18px', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                <div>
                  <strong style={{ fontSize: 14.5 }}>批量导入独立代理线路</strong>
                  <div style={{ fontSize: 11.5, color: 'var(--text-secondary)' }}>
                    支持 <span className="mono">socks5://user:pass@ip:port#名称</span>、<span className="mono">http(s)://...</span>、<span className="mono">ip:port:user:pass</span>、CSV 与 Base64 订阅
                  </div>
                </div>
                <button className="tx-btn tx-btn-sec tx-btn-sm" onClick={onCloseModals}>关闭</button>
              </div>
              <div className="tx-sep" />
              <div style={{ padding: '15px 18px', display: 'grid', gridTemplateColumns: '1.05fr 1fr', gap: 14 }}>
                <div>
                  <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>粘贴代理文本（后端实时语法解析）</div>
                  <textarea
                    className="tx-textarea mono"
                    rows={10}
                    style={{ resize: 'none', lineHeight: 1.5 }}
                    value={batchText}
                    onChange={e => setBatchText(e.target.value)}
                  />
                  <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 8, marginTop: 8 }}>
                    <div>
                      <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>无前缀行默认协议</div>
                      <select className="tx-select" value={batchProto} onChange={e => setBatchProto(e.target.value as ProxyProtocol)}>
                        <option value="SOCKS5">SOCKS5h (远端解析)</option>
                        <option value="HTTP">HTTP CONNECT</option>
                        <option value="HTTPS">HTTPS (TLS)</option>
                        <option value="SOCKS4">SOCKS4a</option>
                      </select>
                    </div>
                    <div>
                      <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>统一分组标签</div>
                      <input className="tx-input" value={batchGroup} onChange={e => setBatchGroup(e.target.value)} />
                    </div>
                  </div>
                </div>

                <div>
                  <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 4 }}>
                    <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)' }}>实时解析与去重校验</span>
                    <span className="mono" style={{ fontSize: 11.5, color: 'var(--emerald-text)', fontWeight: 600 }}>
                      有效 {preview?.validCount ?? 0} · 重复 {preview?.duplicateCount ?? 0} · 异常 {preview?.errorCount ?? 0}
                    </span>
                  </div>
                  <div style={{ border: '1px solid var(--card-border)', borderRadius: 12, background: 'var(--card-subtle)', maxHeight: 256, overflowY: 'auto' }}>
                    {(preview?.items ?? []).map(it => (
                      <div key={it.lineNumber} style={{ padding: '8px 11px', borderBottom: '1px solid var(--card-border)', display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8 }}>
                        <div>
                          <div style={{ fontWeight: 600, fontSize: 12 }}>
                            {it.valid && it.node ? it.node.name : it.rawLine}
                          </div>
                          <div className="mono" style={{ fontSize: 11, color: it.valid ? 'var(--text-secondary)' : 'var(--red-text)' }}>
                            {it.valid && it.node ? `${it.node.host}:${it.node.port} · ${it.node.username ?? '无认证'}` : it.errorReason}
                          </div>
                        </div>
                        <span className={`badge ${it.valid ? 'badge-emerald' : 'badge-red'} mono`}>
                          {it.valid && it.node ? `${it.node.protocol} ✓` : (it.isDuplicate ? '重复' : '错误')}
                        </span>
                      </div>
                    ))}
                  </div>
                </div>
              </div>
              <div className="tx-sep" />
              <div style={{ padding: '12px 18px', background: 'var(--card-subtle)', display: 'flex', justifyContent: 'flex-end', gap: 8 }}>
                <button className="tx-btn tx-btn-sec" onClick={onCloseModals}>取消</button>
                <button className="tx-btn tx-btn-primary" disabled={!preview || preview.validCount === 0} onClick={handleImportBatch}>
                  确认导入 ({preview?.validCount ?? 0} 条有效)
                </button>
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
};
