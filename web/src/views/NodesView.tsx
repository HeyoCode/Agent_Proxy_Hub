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
  { value: 'SOCKS5', label: 'SOCKS5 / SOCKS5h', sublabel: 'L1 原生 · 远端解析 · 静态住宅推荐' },
  { value: 'HTTPS', label: 'HTTPS (TLS)', sublabel: 'L1 原生 · TLS 加密 · 支持自定义 SNI' },
  { value: 'TROJAN', label: 'Trojan (TLS 1.3)', sublabel: 'L1 原生 · 强抗干扰加密隧道' },
  { value: 'SHADOWSOCKS', label: 'Shadowsocks AEAD', sublabel: 'L1 原生 · AES-GCM / ChaCha20' },
  { value: 'HTTP', label: 'HTTP CONNECT', sublabel: 'L1 原生 · 标准 HTTPS 隧道握手' },
  { value: 'SOCKS4', label: 'SOCKS4a', sublabel: 'L1 原生 · 轻量兼容协议' },
  { value: 'VLESS', label: 'VLESS (Reality / TLS)', sublabel: 'L2 sing-box · 现代抗封锁协议' },
  { value: 'HYSTERIA2', label: 'Hysteria2 (QUIC)', sublabel: 'L2 sing-box · 高丢包弱网高速加速' },
  { value: 'TUIC', label: 'TUIC v5 (QUIC)', sublabel: 'L2 sing-box · 0-RTT UDP 多路复用' },
  { value: 'WIREGUARD', label: 'WireGuard (VPN)', sublabel: 'L2 sing-box · 静态住宅 / 家宽直连' }
];

const L1_DEFAULT_PROTOCOL_OPTIONS = PROTOCOL_OPTIONS.slice(0, 6);

const SS_METHOD_OPTIONS = [
  { value: 'aes-256-gcm', label: 'aes-256-gcm' },
  { value: 'chacha20-ietf-poly1305', label: 'chacha20-ietf-poly1305' },
  { value: 'aes-128-gcm', label: 'aes-128-gcm' }
];

const VLESS_SECURITY_OPTIONS = [
  { value: 'reality', label: 'Reality (XTLS)' },
  { value: 'tls', label: 'TLS' },
  { value: 'none', label: '无加密 (none)' }
];

const TUIC_CC_OPTIONS = [
  { value: 'bbr', label: 'BBR（推荐）' },
  { value: 'cubic', label: 'CUBIC' },
  { value: 'new_reno', label: 'NewReno' }
];

export const NodesView: React.FC<Props> = ({
  nodes, showManual, showBatch, onCloseModals, onOpenManual, onOpenBatch, onReload, onToast
}) => {
  const [filter, setFilter] = useState<'all' | 'NATIVE' | 'SIDECAR'>('all');
  const [testingAll, setTestingAll] = useState(false);

  // 手动添加表单（L1 + L2 全协议）
  const [mProto, setMProto] = useState<ProxyProtocol>('SOCKS5');
  const [mName, setMName] = useState('');
  const [mGroup, setMGroup] = useState('AI 静态住宅池');
  const [mHost, setMHost] = useState('');
  const [mPort, setMPort] = useState('1080');
  const [mUser, setMUser] = useState('');
  const [mPass, setMPass] = useState('');
  const [mMethod, setMMethod] = useState('aes-256-gcm');
  const [mSni, setMSni] = useState('');
  const [mInsecure, setMInsecure] = useState(false);
  const [mQuickUri, setMQuickUri] = useState('');
  // L2 扩展专属字段
  const [mVlessSecurity, setMVlessSecurity] = useState('reality');
  const [mVlessFlow, setMVlessFlow] = useState('xtls-rprx-vision');
  const [mVlessPbk, setMVlessPbk] = useState('');
  const [mVlessSid, setMVlessSid] = useState('');
  const [mVlessFp, setMVlessFp] = useState('chrome');
  const [mHy2Obfs, setMHy2Obfs] = useState('');
  const [mHy2ObfsPass, setMHy2ObfsPass] = useState('');
  const [mTuicCc, setMTuicCc] = useState('bbr');
  const [mWgPubKey, setMWgPubKey] = useState('');
  const [mWgAddr, setMWgAddr] = useState('10.0.0.2/32');
  const [mWgMtu, setMWgMtu] = useState('1280');

  // 批量导入与机场订阅拉取
  const [subUrl, setSubUrl] = useState('');
  const [subProxy, setSubProxy] = useState('');
  const [fetchingSub, setFetchingSub] = useState(false);
  const [batchText, setBatchText] = useState(
    'socks5://user:pass@38.154.227.14:1080#US-静态住宅\ntrojan://your-pass@jp.example.com:443?sni=jp.example.com#JP-Trojan原生\nss://YWVzLTI1Ni1nY206cGFzc3dvcmQ=@us.example.com:8388#US-SS原生\nvless://uuid@edge.example.com:443?security=reality&sni=www.microsoft.com&pbk=pubkey&sid=01&fp=chrome#Reality-singbox\nhy2://your-pass@hk.example.com:443?sni=hk.example.com#Hy2-singbox'
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
    const isL2 = n.requiresSidecarEngine || ['VLESS', 'HYSTERIA2', 'TUIC', 'WIREGUARD'].includes(n.protocol);
    if (filter === 'NATIVE') return !isL2;
    if (filter === 'SIDECAR') return isL2;
    return true;
  });

  async function handleQuickParseUri() {
    const trimmed = mQuickUri.trim();
    if (!trimmed) return;
    try {
      const res = await api.previewBatch(trimmed, mProto, mGroup);
      const first = res.items.find(it => it.node)?.node;
      if (!first) {
        onToast(res.items[0]?.errorReason || '无法识别该节点链接格式');
        return;
      }
      setMProto(first.protocol);
      setMName(first.name);
      setMHost(first.host);
      setMPort(String(first.port));
      setMUser(first.username ?? '');
      setMPass(first.password ?? '');
      if (first.method) setMMethod(first.method);
      setMSni(first.sni ?? '');
      setMInsecure(!!first.allowInsecure);
      const ex = first.extra ?? {};
      if (first.protocol === 'VLESS') {
        setMVlessSecurity(ex.security || 'reality');
        setMVlessFlow(ex.flow || '');
        setMVlessPbk(ex.pbk || '');
        setMVlessSid(ex.sid || '');
        setMVlessFp(ex.fp || 'chrome');
      } else if (first.protocol === 'HYSTERIA2') {
        setMHy2Obfs(ex.obfs || '');
        setMHy2ObfsPass(ex['obfs-password'] || '');
      } else if (first.protocol === 'TUIC') {
        setMTuicCc(ex.congestion_control || 'bbr');
      } else if (first.protocol === 'WIREGUARD') {
        setMWgPubKey(ex.publickey || '');
        setMWgAddr(ex.address || '10.0.0.2/32');
        setMWgMtu(ex.mtu || '1280');
      }
      onToast(`已自动解析 ${first.protocol} 节点并回填表单`);
    } catch (err) {
      onToast(err instanceof Error ? err.message : '解析链接失败');
    }
  }

  async function handleSaveManual(e: React.FormEvent) {
    e.preventDefault();
    const extra: Record<string, string> = {};
    if (mProto === 'VLESS') {
      extra.security = mVlessSecurity;
      if (mVlessFlow.trim()) extra.flow = mVlessFlow.trim();
      if (mVlessSecurity === 'reality') {
        if (mVlessPbk.trim()) extra.pbk = mVlessPbk.trim();
        if (mVlessSid.trim()) extra.sid = mVlessSid.trim();
      }
      if (mVlessFp.trim()) extra.fp = mVlessFp.trim();
    } else if (mProto === 'HYSTERIA2') {
      if (mHy2Obfs.trim()) extra.obfs = mHy2Obfs.trim();
      if (mHy2ObfsPass.trim()) extra['obfs-password'] = mHy2ObfsPass.trim();
    } else if (mProto === 'TUIC') {
      extra.congestion_control = mTuicCc;
    } else if (mProto === 'WIREGUARD') {
      if (mWgPubKey.trim()) extra.publickey = mWgPubKey.trim();
      if (mWgAddr.trim()) extra.address = mWgAddr.trim();
      if (mWgMtu.trim()) extra.mtu = mWgMtu.trim();
    }

    const supportsTls = ['HTTPS', 'TROJAN', 'VLESS', 'HYSTERIA2', 'TUIC'].includes(mProto);
    await api.addManualNode({
      protocol: mProto,
      name: mName,
      group: mGroup,
      host: mHost,
      port: Number(mPort) || (mProto === 'WIREGUARD' ? 51820 : supportsTls ? 443 : 1080),
      username: mUser || undefined,
      password: mPass || undefined,
      method: mProto === 'SHADOWSOCKS' ? mMethod : undefined,
      sni: supportsTls && mSni.trim() ? mSni.trim() : undefined,
      allowInsecure: supportsTls ? mInsecure : false,
      extra: Object.keys(extra).length > 0 ? extra : undefined
    });
    setMHost('');
    setMName('');
    setMSni('');
    setMQuickUri('');
    onCloseModals();
    await onReload();
    onToast(`已添加 ${mProto} 独立线路到线路池`);
  }

  async function handleFetchSubscription() {
    if (!subUrl.trim() || fetchingSub) return;
    setFetchingSub(true);
    try {
      const res = await api.fetchSubscription(subUrl.trim(), batchGroup || '订阅导入池', subProxy);
      setBatchText(res.rawText);
      setPreview(res.preview);
      onToast(`订阅拉取成功（${res.detectedFormat}）：识别 ${res.preview.validCount} 条新节点，重复 ${res.preview.duplicateCount} 条`);
    } catch (err) {
      onToast(err instanceof Error ? err.message : '拉取订阅失败');
    } finally {
      setFetchingSub(false);
    }
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
          <SegmentedTabs<'all' | 'NATIVE' | 'SIDECAR'>
            value={filter}
            onChange={setFilter}
            items={[
              { value: 'all', label: '全部线路', count: nodes.length },
              {
                value: 'NATIVE',
                label: 'L1 原生引擎',
                count: nodes.filter(n => !(n.requiresSidecarEngine || ['VLESS', 'HYSTERIA2', 'TUIC', 'WIREGUARD'].includes(n.protocol))).length
              },
              {
                value: 'SIDECAR',
                label: 'L2 sing-box',
                count: nodes.filter(n => n.requiresSidecarEngine || ['VLESS', 'HYSTERIA2', 'TUIC', 'WIREGUARD'].includes(n.protocol)).length
              }
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
                <td className="nowrap">
                  <div style={{ display: 'inline-flex', alignItems: 'center', gap: 5 }}>
                    <span className={`pill-tag ${n.requiresSidecarEngine ? 'tag-amber' : 'tag-indigo'} mono`}>{n.protocol}</span>
                    {n.requiresSidecarEngine && (
                      <span className="pill-tag tag-neutral" style={{ fontSize: 10, padding: '1px 6px' }}>sing-box</span>
                    )}
                  </div>
                </td>
                <td className="nowrap">
                  <div className="mono" style={{ fontSize: 12 }}>{n.host}:{n.port}</div>
                  <div className="num" style={{ fontSize: 11, color: 'var(--text-muted)' }}>
                    {n.method ? `算法：${n.method}` : (n.sni ? `SNI：${n.sni}` : (n.username ? `认证：${n.username}` : '无认证'))}
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

      {/* 手动添加弹窗（支持 L1 + L2 全协议动态表单与链接回填） */}
      {showManual && (
        <div className="modal-backdrop" onClick={onCloseModals}>
          <form className="modal-surface" style={{ maxWidth: 560 }} onSubmit={handleSaveManual} onClick={e => e.stopPropagation()}>
            <div style={{ padding: '18px 22px 14px', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
              <div>
                <div style={{ fontSize: 15, fontWeight: 700 }}>手动添加独立代理线路</div>
                <div style={{ fontSize: 12, color: 'var(--text-muted)', marginTop: 2 }}>
                  支持 L1 原生 (SOCKS5 / HTTP(S) / Trojan / SS) 与 L2 sing-box (VLESS / Hy2 / TUIC / WG)
                </div>
              </div>
              <button type="button" className="btn-pill btn-soft btn-sm" onClick={onCloseModals}>关闭</button>
            </div>
            <div className="soft-divider" />
            <div style={{ padding: '16px 22px', display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 11, maxHeight: '68vh', overflowY: 'auto' }}>
              {/* 快捷粘贴单条节点链接回填 */}
              <div style={{ gridColumn: 'span 2', display: 'flex', gap: 8, alignItems: 'center' }}>
                <input
                  className="clean-input mono"
                  style={{ flex: 1 }}
                  placeholder="可选：粘贴单条节点链接 (vless:// / hy2:// / trojan:// / ss:// / socks5://) 一键填表"
                  value={mQuickUri}
                  onChange={e => setMQuickUri(e.target.value)}
                />
                <button type="button" className="btn-pill btn-soft btn-sm" style={{ flexShrink: 0 }} onClick={handleQuickParseUri}>
                  解析填表
                </button>
              </div>

              <div>
                <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>协议类型</div>
                <CustomSelect
                  variant="light"
                  value={mProto}
                  options={PROTOCOL_OPTIONS}
                  onChange={v => {
                    const next = v as ProxyProtocol;
                    setMProto(next);
                    if (next === 'WIREGUARD' && (mPort === '1080' || mPort === '443')) setMPort('51820');
                    else if (['HTTPS', 'TROJAN', 'VLESS', 'HYSTERIA2', 'TUIC'].includes(next) && mPort === '1080') setMPort('443');
                  }}
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

              {/* 协议专属动态字段 */}
              {mProto === 'SHADOWSOCKS' && (
                <>
                  <div>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>AEAD 加密算法</div>
                    <CustomSelect
                      variant="light"
                      value={mMethod}
                      options={SS_METHOD_OPTIONS}
                      onChange={setMMethod}
                      ariaLabel="选择 Shadowsocks 加密算法"
                    />
                  </div>
                  <div>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>密码 / 密钥 *</div>
                    <input className="clean-input mono" type="password" value={mPass} onChange={e => setMPass(e.target.value)} />
                  </div>
                </>
              )}

              {(mProto === 'TROJAN' || mProto === 'HTTPS') && (
                <>
                  <div>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>TLS SNI（可选）</div>
                    <input className="clean-input mono" placeholder="默认使用服务器域名" value={mSni} onChange={e => setMSni(e.target.value)} />
                  </div>
                  <div>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>
                      {mProto === 'TROJAN' ? 'Trojan 密码 *' : '密码（可选）'}
                    </div>
                    <input className="clean-input mono" type="password" value={mPass} onChange={e => setMPass(e.target.value)} />
                  </div>
                  {mProto === 'HTTPS' && (
                    <div style={{ gridColumn: 'span 2' }}>
                      <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>用户名（可选）</div>
                      <input className="clean-input mono" value={mUser} onChange={e => setMUser(e.target.value)} />
                    </div>
                  )}
                </>
              )}

              {(mProto === 'SOCKS5' || mProto === 'SOCKS4' || mProto === 'HTTP') && (
                <>
                  <div>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>用户名（可选）</div>
                    <input className="clean-input mono" value={mUser} onChange={e => setMUser(e.target.value)} />
                  </div>
                  <div>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>密码（可选）</div>
                    <input className="clean-input mono" type="password" value={mPass} onChange={e => setMPass(e.target.value)} />
                  </div>
                </>
              )}

              {mProto === 'VLESS' && (
                <>
                  <div style={{ gridColumn: 'span 2' }}>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>UUID *</div>
                    <input className="clean-input mono" required placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx" value={mUser} onChange={e => setMUser(e.target.value)} />
                  </div>
                  <div>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>传输加密</div>
                    <CustomSelect
                      variant="light"
                      value={mVlessSecurity}
                      options={VLESS_SECURITY_OPTIONS}
                      onChange={setMVlessSecurity}
                      ariaLabel="选择 VLESS 传输加密"
                    />
                  </div>
                  <div>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>SNI / 伪装域名</div>
                    <input className="clean-input mono" placeholder="例如：www.apple.com" value={mSni} onChange={e => setMSni(e.target.value)} />
                  </div>
                  <div>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>Flow（可选）</div>
                    <input className="clean-input mono" placeholder="xtls-rprx-vision" value={mVlessFlow} onChange={e => setMVlessFlow(e.target.value)} />
                  </div>
                  <div>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>uTLS 指纹</div>
                    <input className="clean-input mono" placeholder="chrome" value={mVlessFp} onChange={e => setMVlessFp(e.target.value)} />
                  </div>
                  {mVlessSecurity === 'reality' && (
                    <>
                      <div>
                        <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>Reality PublicKey (pbk)</div>
                        <input className="clean-input mono" placeholder="公钥字符串" value={mVlessPbk} onChange={e => setMVlessPbk(e.target.value)} />
                      </div>
                      <div>
                        <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>Reality ShortId (sid)</div>
                        <input className="clean-input mono" placeholder="例如：01ab" value={mVlessSid} onChange={e => setMVlessSid(e.target.value)} />
                      </div>
                    </>
                  )}
                </>
              )}

              {mProto === 'HYSTERIA2' && (
                <>
                  <div>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>认证密码 *</div>
                    <input className="clean-input mono" type="password" required value={mPass} onChange={e => setMPass(e.target.value)} />
                  </div>
                  <div>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>TLS SNI（可选）</div>
                    <input className="clean-input mono" placeholder="默认使用服务器域名" value={mSni} onChange={e => setMSni(e.target.value)} />
                  </div>
                  <div>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>混淆类型（可选）</div>
                    <input className="clean-input mono" placeholder="例如：salamander" value={mHy2Obfs} onChange={e => setMHy2Obfs(e.target.value)} />
                  </div>
                  <div>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>混淆密码（可选）</div>
                    <input className="clean-input mono" type="password" value={mHy2ObfsPass} onChange={e => setMHy2ObfsPass(e.target.value)} />
                  </div>
                </>
              )}

              {mProto === 'TUIC' && (
                <>
                  <div>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>UUID *</div>
                    <input className="clean-input mono" required value={mUser} onChange={e => setMUser(e.target.value)} />
                  </div>
                  <div>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>密码</div>
                    <input className="clean-input mono" type="password" value={mPass} onChange={e => setMPass(e.target.value)} />
                  </div>
                  <div>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>TLS SNI（可选）</div>
                    <input className="clean-input mono" placeholder="默认使用服务器域名" value={mSni} onChange={e => setMSni(e.target.value)} />
                  </div>
                  <div>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>拥塞控制算法</div>
                    <CustomSelect
                      variant="light"
                      value={mTuicCc}
                      options={TUIC_CC_OPTIONS}
                      onChange={setMTuicCc}
                      ariaLabel="选择 TUIC 拥塞控制算法"
                    />
                  </div>
                </>
              )}

              {mProto === 'WIREGUARD' && (
                <>
                  <div style={{ gridColumn: 'span 2' }}>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>本端私钥 (PrivateKey) *</div>
                    <input className="clean-input mono" type="password" required value={mPass} onChange={e => setMPass(e.target.value)} />
                  </div>
                  <div style={{ gridColumn: 'span 2' }}>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>对端公钥 (Peer PublicKey) *</div>
                    <input className="clean-input mono" required value={mWgPubKey} onChange={e => setMWgPubKey(e.target.value)} />
                  </div>
                  <div>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>本端隧道 IP (CIDR)</div>
                    <input className="clean-input mono" placeholder="10.0.0.2/32" value={mWgAddr} onChange={e => setMWgAddr(e.target.value)} />
                  </div>
                  <div>
                    <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>MTU</div>
                    <input className="clean-input num" placeholder="1280" value={mWgMtu} onChange={e => setMWgMtu(e.target.value)} />
                  </div>
                </>
              )}

              <div style={{ gridColumn: 'span 2', display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12 }}>
                <div style={{ flex: 1 }}>
                  <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 5 }}>所属分组</div>
                  <input className="clean-input" value={mGroup} onChange={e => setMGroup(e.target.value)} />
                </div>
                {['HTTPS', 'TROJAN', 'VLESS', 'HYSTERIA2', 'TUIC'].includes(mProto) && (
                  <label style={{ display: 'inline-flex', alignItems: 'center', gap: 6, fontSize: 12, color: 'var(--text-secondary)', marginTop: 18, cursor: 'pointer', flexShrink: 0 }}>
                    <input type="checkbox" checked={mInsecure} onChange={e => setMInsecure(e.target.checked)} />
                    <span>跳过证书校验</span>
                  </label>
                )}
              </div>
            </div>
            <div className="modal-footer-bar">
              <button type="button" className="btn-pill btn-soft" onClick={onCloseModals}>取消</button>
              <button type="submit" className="btn-pill btn-indigo">保存线路</button>
            </div>
          </form>
        </div>
      )}

      {/* 批量导入与机场订阅拉取弹窗 */}
      {showBatch && (
        <div className="modal-backdrop" onClick={onCloseModals}>
          <div className="modal-surface" onClick={e => e.stopPropagation()}>
            <div style={{ padding: '18px 22px 14px', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
              <div>
                <div style={{ fontSize: 15, fontWeight: 700 }}>批量导入与机场订阅拉取</div>
                <div style={{ fontSize: 12, color: 'var(--text-muted)', marginTop: 2 }}>
                  支持 <strong>Clash / Base64 机场订阅链接</strong>，以及 <span className="mono">socks5 / http(s) / trojan / ss / vless / hy2 / tuic / wg</span> 文本
                </div>
              </div>
              <button type="button" className="btn-pill btn-soft btn-sm" onClick={onCloseModals}>关闭</button>
            </div>
            <div className="soft-divider" />
            <div style={{ padding: '14px 22px 0' }}>
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 180px auto', gap: 8, alignItems: 'center' }}>
                <input
                  className="clean-input mono"
                  placeholder="粘贴机场订阅链接（如 https://.../config?token=...，自动识别 Clash YAML / Base64）"
                  value={subUrl}
                  onChange={e => setSubUrl(e.target.value)}
                />
                <input
                  className="clean-input mono"
                  placeholder="可选拉取代理 (如 :7897)"
                  title="若订阅域名被拦截，可填写本机代理如 http://127.0.0.1:7897，留空则直连"
                  value={subProxy}
                  onChange={e => setSubProxy(e.target.value)}
                />
                <button
                  type="button"
                  className="btn-pill btn-indigo btn-sm"
                  disabled={!subUrl.trim() || fetchingSub}
                  onClick={handleFetchSubscription}
                >
                  <RefreshSpinIcon size={12} spinning={fetchingSub} />
                  <span>{fetchingSub ? '拉取中' : '拉取订阅'}</span>
                </button>
              </div>
            </div>
            <div style={{ padding: '14px 22px 18px', display: 'grid', gridTemplateColumns: '1.05fr 1fr', gap: 16 }}>
              <div>
                <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 6 }}>代理文本 / 订阅内容（实时语法解析）</div>
                <textarea
                  className="clean-textarea mono"
                  rows={9}
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
                      options={L1_DEFAULT_PROTOCOL_OPTIONS}
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
