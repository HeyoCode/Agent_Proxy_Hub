import React, { useEffect, useState } from 'react';
import { BatchParseResult, ProxyNode, ProxyProtocol, api } from '../api';
import { CustomSelect } from '../components/CustomSelect';
import { PlusIcon, RefreshSpinIcon, UploadBatchIcon } from '../components/Icons';
import { SegmentedTabs } from '../components/SegmentedTabs';

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

const PROTOCOL_OPTIONS = [
  { value: 'SOCKS5', label: 'SOCKS5 / SOCKS5h', sublabel: '远端域名解析 · 推荐静态住宅' },
  { value: 'HTTP', label: 'HTTP CONNECT', sublabel: '标准 HTTPS 隧道握手' },
  { value: 'HTTPS', label: 'HTTPS (TLS)', sublabel: '上游代理 TLS 加密连接' },
  { value: 'SOCKS4', label: 'SOCKS4a', sublabel: '轻量兼容型 SOCKS 协议' }
];

export const NodesView: React.FC<Props> = ({
  nodes, showManual, showBatch, onCloseModals, onOpenManual, onOpenBatch, onReload, onToast
}) => {
  const [filter, setFilter] = useState<'all' | 'SOCKS' | 'HTTP'>('all');
  const [testingAll, setTestingAll] = useState(false);

  const [mProto, setMProto] = useState<ProxyProtocol>('SOCKS5');
  const [mName, setMName] = useState('');
  const [mGroup, setMGroup] = useState('AI 静态住宅池');
  const [mHost, setMHost] = useState('');
  const [mPort, setMPort] = useState('1080');
  const [mUser, setMUser] = useState('');
  const [mPass, setMPass] = useState('');

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
    <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
      <div className="surface-card">
        <div style={{ padding: '14px 18px', display: 'flex', alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap', gap: 10 }}>
          <SegmentedTabs<'all' | 'SOCKS' | 'HTTP'>
            value={filter}
            onChange={setFilter}
            items={[
              { value: 'all', label: '全部', count: nodes.length },
              { value: 'SOCKS', label: 'SOCKS5 / 4', count: nodes.filter(n => n.protocol === 'SOCKS5' || n.protocol === 'SOCKS4').length },
              { value: 'HTTP', label: 'HTTP / HTTPS', count: nodes.filter(n => n.protocol === 'HTTP' || n.protocol === 'HTTPS').length }
            ]}
          />

          <div style={{ display: 'flex', gap: 8 }}>
            <button type="button" className="btn-pill btn-soft btn-sm" onClick={handleVerifyAll} disabled={testingAll}>
              <RefreshSpinIcon size={13} spinning={testingAll} />
              <span>{testingAll ? '验真中' : '全量直连验真'}</span>
            </button>
            <button type="button" className="btn-pill btn-soft btn-sm" onClick={onOpenManual}>
              <PlusIcon size={12} />
              <span>手动添加</span>
            </button>
            <button type="button" className="btn-pill btn-indigo btn-sm" onClick={onOpenBatch}>
              <UploadBatchIcon size={13} />
              <span>批量导入线路</span>
            </button>
          </div>
        </div>

        <div className="soft-divider" />

        <table className="clean-table">
          <thead>
            <tr>
              <th>节点名称与分组</th>
              <th>协议</th>
              <th>代理入口地址</th>
              <th>真实落地出口 IP / 说明</th>
              <th>AI 可达性</th>
              <th className="num-r">延迟</th>
              <th className="num-r">操作</th>
            </tr>
          </thead>
          <tbody>
            {filtered.map(n => (
              <tr key={n.id}>
                <td>
                  <div style={{ fontWeight: 600 }}>{n.name}</div>
                  <div style={{ fontSize: 11.5, color: 'var(--text-muted)' }}>{n.group}</div>
                </td>
                <td className="nowrap"><span className="pill-tag tag-indigo mono">{n.protocol}</span></td>
                <td className="nowrap">
                  <div className="mono" style={{ fontSize: 12 }}>{n.host}:{n.port}</div>
                  <div className="num" style={{ fontSize: 11, color: 'var(--text-muted)' }}>
                    {n.username ? `认证：${n.username}` : '无认证'}
                  </div>
                </td>
                <td>
                  <div className="num" style={{ fontWeight: 600 }}>{n.exitIp}</div>
                  <div style={{ fontSize: 11.5, color: 'var(--text-secondary)' }}>{n.ispSummary}</div>
                </td>
                <td>
                  <div style={{ display: 'flex', gap: 5, flexWrap: 'wrap' }}>
                    {n.aiReachability.map(tag => (
                      <span key={tag} className={`pill-tag ${tag.includes('✕') || tag.includes('超时') ? 'tag-coral' : 'tag-neutral'}`}>
                        {tag}
                      </span>
                    ))}
                  </div>
                </td>
                <td
                  className="num-r num nowrap"
                  style={{
                    fontWeight: 700,
                    color: n.status === 'offline' ? 'var(--accent-coral-text)' : (n.latencyMs ? 'var(--accent-emerald-text)' : 'var(--text-muted)')
                  }}
                >
                  {n.latencyMs ? `${n.latencyMs} ms` : (n.status === 'offline' ? '超时' : '待测')}
                </td>
                <td className="num-r nowrap">
                  <div style={{ display: 'inline-flex', gap: 6 }}>
                    <button type="button" className="btn-pill btn-soft btn-sm" onClick={() => handleVerifyOne(n.id)}>验真</button>
                    {n.id !== 'direct-phy' && (
                      <button type="button" className="btn-pill btn-soft btn-sm" onClick={() => handleDelete(n.id)}>删除</button>
                    )}
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {/* 手动添加弹窗 */}
      {showManual && (
        <div className="modal-backdrop" onClick={onCloseModals}>
          <form className="modal-surface narrow" onSubmit={handleSaveManual} onClick={e => e.stopPropagation()}>
            <div style={{ padding: '18px 22px 14px', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
              <div>
                <div style={{ fontSize: 15, fontWeight: 700 }}>手动添加独立代理线路</div>
                <div style={{ fontSize: 12, color: 'var(--text-muted)', marginTop: 2 }}>支持 HTTP / HTTPS / SOCKS5 / SOCKS4，出站强制绑定物理网卡</div>
              </div>
              <button type="button" className="btn-pill btn-soft btn-sm" onClick={onCloseModals}>关闭</button>
            </div>
            <div className="soft-divider" />
            <div style={{ padding: '18px 22px', display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
              <div>
                <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>协议类型</div>
                <CustomSelect
                  variant="light"
                  value={mProto}
                  options={PROTOCOL_OPTIONS}
                  onChange={v => setMProto(v as ProxyProtocol)}
                  ariaLabel="选择手动添加节点协议"
                />
              </div>
              <div>
                <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>备注名称</div>
                <input className="clean-input" placeholder="例如：US-AT&T-静态住宅" value={mName} onChange={e => setMName(e.target.value)} />
              </div>
              <div>
                <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>服务器 IP / 域名 *</div>
                <input className="clean-input mono" required placeholder="例如：38.154.227.14" value={mHost} onChange={e => setMHost(e.target.value)} />
              </div>
              <div>
                <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>端口 *</div>
                <input className="clean-input num" required value={mPort} onChange={e => setMPort(e.target.value)} />
              </div>
              <div>
                <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>用户名（可选）</div>
                <input className="clean-input mono" value={mUser} onChange={e => setMUser(e.target.value)} />
              </div>
              <div>
                <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>密码（可选）</div>
                <input className="clean-input mono" type="password" value={mPass} onChange={e => setMPass(e.target.value)} />
              </div>
              <div style={{ gridColumn: 'span 2' }}>
                <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>所属分组</div>
                <input className="clean-input" value={mGroup} onChange={e => setMGroup(e.target.value)} />
              </div>
            </div>
            <div className="modal-footer-bar">
              <button type="button" className="btn-pill btn-soft" onClick={onCloseModals}>取消</button>
              <button type="submit" className="btn-pill btn-indigo">保存线路</button>
            </div>
          </form>
        </div>
      )}

      {/* 批量导入弹窗 */}
      {showBatch && (
        <div className="modal-backdrop" onClick={onCloseModals}>
          <div className="modal-surface" onClick={e => e.stopPropagation()}>
            <div style={{ padding: '18px 22px 14px', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
              <div>
                <div style={{ fontSize: 15, fontWeight: 700 }}>批量导入独立代理线路</div>
                <div style={{ fontSize: 12, color: 'var(--text-muted)', marginTop: 2 }}>
                  支持 <span className="mono">socks5://user:pass@ip:port#名称</span>、<span className="mono">http(s)://...</span>、<span className="mono">ip:port:user:pass</span>、CSV 与 Base64 订阅
                </div>
              </div>
              <button type="button" className="btn-pill btn-soft btn-sm" onClick={onCloseModals}>关闭</button>
            </div>
            <div className="soft-divider" />
            <div style={{ padding: '18px 22px', display: 'grid', gridTemplateColumns: '1.05fr 1fr', gap: 16 }}>
              <div>
                <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 6 }}>粘贴代理文本（实时语法解析）</div>
                <textarea
                  className="clean-textarea mono"
                  rows={10}
                  style={{ resize: 'none', lineHeight: 1.55 }}
                  value={batchText}
                  onChange={e => setBatchText(e.target.value)}
                />
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10, marginTop: 10 }}>
                  <div>
                    <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>无前缀行默认协议</div>
                    <CustomSelect
                      variant="light"
                      value={batchProto}
                      options={PROTOCOL_OPTIONS}
                      onChange={v => setBatchProto(v as ProxyProtocol)}
                      ariaLabel="选择无前缀行默认协议"
                    />
                  </div>
                  <div>
                    <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>统一分组标签</div>
                    <input className="clean-input" value={batchGroup} onChange={e => setBatchGroup(e.target.value)} />
                  </div>
                </div>
              </div>

              <div>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 6 }}>
                  <span style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)' }}>实时解析与去重校验</span>
                  <span className="num" style={{ fontSize: 11.5, color: 'var(--accent-indigo)', fontWeight: 600 }}>
                    有效 {preview?.validCount ?? 0} · 重复 {preview?.duplicateCount ?? 0} · 异常 {preview?.errorCount ?? 0}
                  </span>
                </div>
                <div style={{ borderRadius: 12, background: 'var(--bg-subtle)', maxHeight: 268, overflowY: 'auto' }}>
                  {(preview?.items ?? []).map(it => (
                    <div
                      key={it.lineNumber}
                      style={{
                        padding: '10px 12px',
                        borderBottom: '1px solid var(--divider)',
                        display: 'flex',
                        justifyContent: 'space-between',
                        alignItems: 'center',
                        gap: 8
                      }}
                    >
                      <div style={{ minWidth: 0 }}>
                        <div style={{ fontWeight: 600, fontSize: 12, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                          {it.valid && it.node ? it.node.name : it.rawLine}
                        </div>
                        <div className="num" style={{ fontSize: 11, color: it.valid ? 'var(--text-secondary)' : 'var(--accent-coral-text)' }}>
                          {it.valid && it.node ? `${it.node.host}:${it.node.port} · ${it.node.username ?? '无认证'}` : it.errorReason}
                        </div>
                      </div>
                      <span className={`pill-tag ${it.valid ? 'tag-emerald' : 'tag-coral'} mono`}>
                        {it.valid && it.node ? `${it.node.protocol}` : (it.isDuplicate ? '重复' : '错误')}
                      </span>
                    </div>
                  ))}
                </div>
              </div>
            </div>
            <div className="modal-footer-bar">
              <button type="button" className="btn-pill btn-soft" onClick={onCloseModals}>取消</button>
              <button type="button" className="btn-pill btn-indigo" disabled={!preview || preview.validCount === 0} onClick={handleImportBatch}>
                确认导入 ({preview?.validCount ?? 0} 条有效)
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
};
