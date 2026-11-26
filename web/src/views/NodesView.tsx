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

  // 批量导入与机场订阅拉取（支持本地记忆最近一次订阅配置）
  const [importMode, setImportMode] = useState<'subscription' | 'text'>('subscription');
  const [savedSub, setSavedSub] = useState<{ url: string; group: string; proxy: string } | null>(() => {
    try {
      const raw = window.localStorage.getItem('aph_saved_sub_meta');
      return raw ? (JSON.parse(raw) as { url: string; group: string; proxy: string }) : null;
    } catch {
      return null;
    }
  });
  const [subUrl, setSubUrl] = useState(() => savedSub?.url ?? '');
  const [subProxy, setSubProxy] = useState(() => savedSub?.proxy ?? '');
  const [subDetectedFormat, setSubDetectedFormat] = useState<string | null>(null);
  const [fetchingSub, setFetchingSub] = useState(false);
  const [refreshingSavedSub, setRefreshingSavedSub] = useState(false);
  const [previewSearch, setPreviewSearch] = useState('');
  const [batchText, setBatchText] = useState(
    'socks5://user:pass@38.154.227.14:1080#US-静态住宅\ntrojan://your-pass@jp.example.com:443?sni=jp.example.com#JP-Trojan原生\nss://YWVzLTI1Ni1nY206cGFzc3dvcmQ=@us.example.com:8388#US-SS原生\nvless://uuid@edge.example.com:443?security=reality&sni=www.microsoft.com&pbk=pubkey&sid=01&fp=chrome#Reality-singbox\nhy2://your-pass@hk.example.com:443?sni=hk.example.com#Hy2-singbox'
  );
  const [batchProto, setBatchProto] = useState<ProxyProtocol>('SOCKS5');
  const [batchGroup, setBatchGroup] = useState(() => savedSub?.group || '批量导入池');
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
      const targetGroup = batchGroup || '订阅导入池';
      const res = await api.fetchSubscription(subUrl.trim(), targetGroup, subProxy);
      setBatchText(res.rawText);
      setPreview(res.preview);
      setSubDetectedFormat(res.detectedFormat);
      const nextSaved = { url: subUrl.trim(), group: targetGroup, proxy: subProxy.trim() };
      setSavedSub(nextSaved);
      try {
        window.localStorage.setItem('aph_saved_sub_meta', JSON.stringify(nextSaved));
      } catch { }
      onToast(`订阅拉取成功（${res.detectedFormat}）：识别 ${res.preview.validCount} 条新节点，重复 ${res.preview.duplicateCount} 条`);
    } catch (err) {
      onToast(err instanceof Error ? err.message : '拉取订阅失败');
    } finally {
      setFetchingSub(false);
    }
  }

  async function handleQuickRefreshSavedSub() {
    if (!savedSub?.url || refreshingSavedSub) return;
    setRefreshingSavedSub(true);
    try {
      const fetched = await api.fetchSubscription(savedSub.url, savedSub.group || '订阅导入池', savedSub.proxy);
      if (fetched.preview.validCount === 0) {
        onToast(`订阅已是最新（共核对 ${fetched.preview.duplicateCount} 条已有节点，无新增）`);
        return;
      }
      const imported = await api.importBatch(fetched.rawText, 'SOCKS5', savedSub.group || '订阅导入池');
      await onReload();
      onToast(`订阅刷新完成：新增 ${imported.addedCount} 条新节点，跳过重复 ${imported.duplicateCount} 条`);
    } catch (err) {
      onToast(err instanceof Error ? err.message : '刷新订阅失败');
    } finally {
      setRefreshingSavedSub(false);
    }
  }

  const savedSubHostHint = (() => {
    if (!savedSub?.url) return '';
    try {
      return new URL(savedSub.url).host;
    } catch {
      return '已保存订阅';
    }
  })();

  function formatCompactCred(node: ProxyNode): string {
    if (node.protocol === 'SHADOWSOCKS') return node.method ?? 'AEAD';
    if (node.protocol === 'TROJAN') return node.sni ? `SNI ${node.sni}` : 'TLS 1.3';
    if (node.protocol === 'WIREGUARD') return 'WireGuard 密钥';
    if (node.username) {
      return node.username.length > 14 ? `${node.username.slice(0, 8)}…` : node.username;
    }
    return node.password ? '密钥认证' : '无认证';
  }

  const previewItems = (preview?.items ?? []).filter(it => {
    const kw = previewSearch.trim().toLowerCase();
    if (!kw) return true;
    const text = `${it.node?.name ?? ''} ${it.node?.host ?? ''} ${it.node?.protocol ?? ''} ${it.rawLine}`.toLowerCase();
    return text.includes(kw);
  });

  const protocolBreakdown = Object.entries(
    (preview?.items ?? []).reduce<Record<string, number>>((acc, it) => {
      if (it.valid && it.node) {
        acc[it.node.protocol] = (acc[it.node.protocol] ?? 0) + 1;
      }
      return acc;
    }, {})
  );

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

          <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
            {savedSub?.url && (
              <button
                type="button"
                className="btn-pill btn-soft btn-sm"
                title={`一键拉取并增量导入来自 ${savedSubHostHint} 的最新节点`}
                onClick={handleQuickRefreshSavedSub}
                disabled={refreshingSavedSub}
              >
                <RefreshSpinIcon size={13} spinning={refreshingSavedSub} />
                <span>{refreshingSavedSub ? '同步中' : '刷新订阅'}</span>
              </button>
            )}
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

      {/* 批量导入与机场订阅拉取弹窗（重构优雅双模式工作台） */}
      {showBatch && (
        <div className="modal-backdrop" onClick={onCloseModals}>
          <div className="modal-surface wide-import" onClick={e => e.stopPropagation()}>
            {/* 头部：标题 + 模式切换 + 关闭 */}
            <div style={{ padding: '16px 22px', display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
              <div style={{ minWidth: 0 }}>
                <div style={{ fontSize: 15.5, fontWeight: 700, color: 'var(--text-primary)' }}>导入独立代理线路</div>
                <div style={{ fontSize: 12, color: 'var(--text-muted)', marginTop: 2 }}>
                  自动识别 Clash YAML / Base64 机场订阅与全协议节点链接，导入前实时去重校验
                </div>
              </div>
              <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                <SegmentedTabs<'subscription' | 'text'>
                  value={importMode}
                  onChange={setImportMode}
                  items={[
                    { value: 'subscription', label: '订阅链接拉取' },
                    { value: 'text', label: '文本 / 链接粘贴' }
                  ]}
                />
                <button type="button" className="btn-pill btn-soft btn-sm" onClick={onCloseModals}>关闭</button>
              </div>
            </div>
            <div className="soft-divider" />

            {/* 主体双栏工作区 */}
            <div className="import-modal-body">
              {/* 左栏：订阅拉取 或 文本粘贴 */}
              <div style={{ display: 'flex', flexDirection: 'column', gap: 12, minWidth: 0 }}>
                {importMode === 'subscription' ? (
                  <>
                    <div className="import-sub-card">
                      <div>
                        <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 6 }}>
                          机场订阅链接 URL
                        </div>
                        <input
                          className="clean-input mono"
                          placeholder="https://example.com/config?token=..."
                          value={subUrl}
                          onChange={e => setSubUrl(e.target.value)}
                        />
                      </div>

                      <div className="import-meta-grid">
                        <div>
                          <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 5 }}>统一分组标签</div>
                          <input
                            className="clean-input"
                            placeholder="例如：机场高速中转"
                            value={batchGroup}
                            onChange={e => setBatchGroup(e.target.value)}
                          />
                        </div>
                        <div>
                          <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 5 }}>拉取代理（可选）</div>
                          <input
                            className="clean-input mono"
                            placeholder="留空直连，或 :7897"
                            title="若订阅域名被拦截，可填写本机代理如 http://127.0.0.1:7897"
                            value={subProxy}
                            onChange={e => setSubProxy(e.target.value)}
                          />
                        </div>
                      </div>

                      <button
                        type="button"
                        className="btn-pill btn-indigo"
                        style={{ width: '100%', justifyContent: 'center', height: 36 }}
                        disabled={!subUrl.trim() || fetchingSub}
                        onClick={handleFetchSubscription}
                      >
                        <RefreshSpinIcon size={13} spinning={fetchingSub} />
                        <span>{fetchingSub ? '正在拉取并解析订阅...' : '拉取并解析订阅'}</span>
                      </button>
                    </div>

                    {/* 解析摘要卡片（取代丑陋的 Base64 原始乱码展示） */}
                    <div className="import-sub-card" style={{ flex: 1, justifyContent: 'space-between' }}>
                      <div>
                        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 8 }}>
                          <span style={{ fontSize: 12, fontWeight: 600, color: 'var(--text-primary)' }}>
                            {subDetectedFormat ? `已识别格式：${subDetectedFormat}` : '支持格式与智能识别'}
                          </span>
                          {subDetectedFormat && (
                            <span className="pill-tag tag-emerald">解析就绪</span>
                          )}
                        </div>

                        {protocolBreakdown.length > 0 ? (
                          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginTop: 6 }}>
                            {protocolBreakdown.map(([proto, count]) => {
                              const isL2 = ['VLESS', 'HYSTERIA2', 'TUIC', 'WIREGUARD'].includes(proto);
                              return (
                                <span key={proto} className={`pill-tag ${isL2 ? 'tag-indigo' : 'tag-emerald'} mono`}>
                                  {proto} · {count}
                                </span>
                              );
                            })}
                          </div>
                        ) : (
                          <div style={{ fontSize: 11.5, color: 'var(--text-muted)', lineHeight: 1.7 }}>
                            • 自动兼容 <strong>Clash / Mihomo YAML</strong> 与 <strong>Base64 / v2rayN</strong> 订阅<br />
                            • L1 原生支持 SOCKS5 / HTTP(S) / Trojan / Shadowsocks<br />
                            • L2 按需支持 VLESS / Hysteria2 / TUIC / WireGuard
                          </div>
                        )}
                      </div>

                      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', paddingTop: 10, borderTop: '1px solid var(--divider)' }}>
                        <span style={{ fontSize: 11, color: 'var(--text-muted)' }}>
                          需要手动微调或查看原文？
                        </span>
                        <button
                          type="button"
                          className="btn-pill btn-soft btn-sm"
                          onClick={() => setImportMode('text')}
                        >
                          查看原始文本 →
                        </button>
                      </div>
                    </div>
                  </>
                ) : (
                  <>
                    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                      <span style={{ fontSize: 12, fontWeight: 600, color: 'var(--text-secondary)' }}>
                        粘贴节点链接 / Base64 / Clash YAML
                      </span>
                      <button
                        type="button"
                        style={{ border: 'none', background: 'transparent', color: 'var(--text-muted)', fontSize: 11.5, cursor: 'pointer' }}
                        onClick={() => { setBatchText(''); setSubDetectedFormat(null); }}
                      >
                        清空
                      </button>
                    </div>
                    <textarea
                      className="clean-textarea mono"
                      rows={10}
                      style={{ resize: 'none', lineHeight: 1.55, wordBreak: 'break-all', height: 224 }}
                      placeholder="每行一条链接，支持 socks5://, http(s)://, trojan://, ss://, vless://, hy2://, tuic://, wg:// 或整段 Base64 / Clash YAML"
                      value={batchText}
                      onChange={e => setBatchText(e.target.value)}
                    />
                    <div className="import-meta-grid">
                      <div>
                        <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 5 }}>无前缀行默认协议</div>
                        <CustomSelect
                          variant="light"
                          value={batchProto}
                          options={L1_DEFAULT_PROTOCOL_OPTIONS}
                          onChange={v => setBatchProto(v as ProxyProtocol)}
                          ariaLabel="选择无前缀行默认协议"
                        />
                      </div>
                      <div>
                        <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 5 }}>统一分组标签</div>
                        <input
                          className="clean-input"
                          value={batchGroup}
                          onChange={e => setBatchGroup(e.target.value)}
                        />
                      </div>
                    </div>
                  </>
                )}
              </div>

              {/* 右栏：实时解析与去重预览列表 */}
              <div style={{ display: 'flex', flexDirection: 'column', gap: 10, minWidth: 0 }}>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
                  <span style={{ fontSize: 12, fontWeight: 600, color: 'var(--text-secondary)' }}>实时解析与去重预览</span>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                    <span className="pill-tag tag-emerald num">有效 {preview?.validCount ?? 0}</span>
                    {(preview?.duplicateCount ?? 0) > 0 && (
                      <span className="pill-tag tag-neutral num">重复 {preview?.duplicateCount}</span>
                    )}
                    {(preview?.errorCount ?? 0) > 0 && (
                      <span className="pill-tag tag-coral num">异常 {preview?.errorCount}</span>
                    )}
                  </div>
                </div>

                <input
                  className="clean-input"
                  style={{ height: 32, fontSize: 12 }}
                  placeholder="按地区、名称或协议筛选预览节点..."
                  value={previewSearch}
                  onChange={e => setPreviewSearch(e.target.value)}
                />

                <div className="import-preview-shell">
                  {previewItems.length === 0 ? (
                    <div style={{ margin: 'auto', textAlign: 'center', padding: 28, color: 'var(--text-muted)', fontSize: 12 }}>
                      {previewSearch.trim() ? '没有匹配筛选条件的节点' : '粘贴订阅链接或节点文本后将在此实时预览'}
                    </div>
                  ) : (
                    previewItems.map(it => {
                      const isL2 = it.node && ['VLESS', 'HYSTERIA2', 'TUIC', 'WIREGUARD'].includes(it.node.protocol);
                      const tagClass = it.valid
                        ? (isL2 ? 'tag-indigo' : 'tag-emerald')
                        : (it.isDuplicate ? 'tag-neutral' : 'tag-coral');
                      return (
                        <div key={it.lineNumber} className="import-preview-row">
                          <span className="import-row-idx num">{String(it.lineNumber).padStart(2, '0')}</span>
                          <div className="import-row-text">
                            <div
                              className="import-row-title"
                              title={it.valid && it.node ? it.node.name : it.rawLine}
                            >
                              {it.valid && it.node ? it.node.name : it.rawLine}
                            </div>
                            <div
                              className="import-row-sub num"
                              style={{ color: it.valid ? 'var(--text-secondary)' : (it.isDuplicate ? 'var(--text-muted)' : 'var(--accent-coral-text)') }}
                              title={it.valid && it.node ? `${it.node.host}:${it.node.port}` : (it.errorReason ?? '')}
                            >
                              {it.valid && it.node
                                ? `${it.node.host}:${it.node.port} · ${formatCompactCred(it.node)}`
                                : it.errorReason}
                            </div>
                          </div>
                          <span className={`pill-tag ${tagClass} mono`} style={{ flexShrink: 0 }}>
                            {it.valid && it.node ? it.node.protocol : (it.isDuplicate ? '重复' : '错误')}
                          </span>
                        </div>
                      );
                    })
                  )}
                </div>
              </div>
            </div>

            {/* 底部汇总与操作栏 */}
            <div className="modal-footer-bar" style={{ justifyContent: 'space-between' }}>
              <span className="num" style={{ fontSize: 12, color: 'var(--text-muted)' }}>
                将导入 <strong style={{ color: 'var(--text-primary)' }}>{preview?.validCount ?? 0}</strong> 条有效线路至「{batchGroup || '批量导入池'}」
              </span>
              <div style={{ display: 'flex', gap: 10 }}>
                <button type="button" className="btn-pill btn-soft" onClick={onCloseModals}>取消</button>
                <button type="button" className="btn-pill btn-indigo" disabled={!preview || preview.validCount === 0} onClick={handleImportBatch}>
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
