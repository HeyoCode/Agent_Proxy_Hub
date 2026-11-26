import React, { useEffect, useState } from 'react';
import { IsolationSettings, NetworkAdapterInfo, SidecarEngineStatus, api } from '../api';
import { RefreshSpinIcon } from '../components/Icons';

interface Props {
  onReloadOverview: () => Promise<void>;
  onToast: (msg: string) => void;
}

export const IsolationView: React.FC<Props> = ({ onReloadOverview, onToast }) => {
  const [settings, setSettings] = useState<IsolationSettings | null>(null);
  const [adapters, setAdapters] = useState<NetworkAdapterInfo[]>([]);
  const [sidecar, setSidecar] = useState<SidecarEngineStatus | null>(null);
  const [installingSb, setInstallingSb] = useState(false);
  const [downloadProxy, setDownloadProxy] = useState('');
  const [customPathInput, setCustomPathInput] = useState('');

  async function load() {
    const res = await api.getIsolation();
    setSettings(res.settings);
    setAdapters(res.adapters);
    if (res.sidecarEngine) setSidecar(res.sidecarEngine);
    setCustomPathInput(res.settings.customSingBoxPath ?? '');
  }

  async function handleInstallSingBox() {
    setInstallingSb(true);
    try {
      onToast('正在从 GitHub SagerNet/sing-box 获取并安装最新扩展内核...');
      const res = await api.installSingBox(downloadProxy.trim() || undefined);
      setSidecar(res.status);
      await onReloadOverview();
      onToast(res.message);
    } catch (err) {
      onToast(err instanceof Error ? err.message : '安装 sing-box 失败');
    } finally {
      setInstallingSb(false);
    }
  }

  async function handleSaveSingBoxPath() {
    try {
      const res = await api.setSingBoxPath(customPathInput.trim());
      setSidecar(res.status);
      await onReloadOverview();
      onToast(res.message);
    } catch (err) {
      onToast(err instanceof Error ? err.message : '路径校验失败');
    }
  }

  async function handleStopSingBox() {
    const res = await api.stopSingBox();
    setSidecar(res.status);
    await onReloadOverview();
    onToast(res.message);
  }

  useEffect(() => {
    void load();
  }, []);

  async function handleSelectNic(id: string) {
    const res = await api.updateIsolation({ lockedPhysicalNicId: id });
    setSettings(res.settings);
    setAdapters(res.adapters);
    await onReloadOverview();
    onToast('已切换物理出口绑定网卡');
  }

  async function handleRescan() {
    const res = await api.rescanNics();
    setAdapters(res.adapters);
    await onReloadOverview();
    onToast(`网卡扫描完成：识别 ${res.adapters.length} 个网络接口`);
  }

  async function updateToggle(patch: Partial<IsolationSettings>, label: string) {
    const res = await api.updateIsolation(patch);
    setSettings(res.settings);
    await onReloadOverview();
    onToast(`已更新：${label}`);
  }

  const physicalNics = adapters.filter(a => !a.isVirtualOrTunnel);
  const virtualNics = adapters.filter(a => a.isVirtualOrTunnel);

  return (
    <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 18, alignItems: 'start' }}>
      <div className="surface-card surface-card-pad">
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 4 }}>
          <span className="card-title-sm">本机物理出口网卡实时锁定</span>
          <button type="button" className="btn-pill btn-soft btn-sm" onClick={handleRescan}>重新扫描网卡</button>
        </div>
        <div className="card-subtitle-sm" style={{ marginBottom: 14 }}>
          所有出站 Socket 在 connect 前直接绑定物理硬件网卡接口，强制绕过 Clash / Mihomo / Tailscale 虚拟路由
        </div>

        <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          {physicalNics.map(nic => (
            <div
              key={nic.id}
              onClick={() => nic.status === 'Up' && handleSelectNic(nic.id)}
              style={{
                cursor: nic.status === 'Up' ? 'pointer' : 'default',
                borderRadius: 12,
                padding: '12px 14px',
                background: nic.isSelected ? 'var(--accent-indigo-soft)' : 'var(--bg-subtle)',
                display: 'flex',
                justifyContent: 'space-between',
                alignItems: 'center',
                gap: 10,
                transition: 'background 0.15s ease'
              }}
            >
              <div style={{ minWidth: 0 }}>
                <div style={{ fontWeight: 600, fontSize: 12.5 }}>{nic.name} · {nic.description}</div>
                <div className="num" style={{ fontSize: 11, color: 'var(--text-secondary)', marginTop: 2 }}>
                  Index #{nic.interfaceIndex} · {nic.iPv4Address ?? '无 IPv4'} · Gateway {nic.gatewayAddress ?? '无网关'}
                  {nic.iPv6Address ? ` · IPv6 ${nic.iPv6Address}` : ''}
                </div>
              </div>
              <span className={`pill-tag ${nic.isSelected ? 'tag-indigo' : 'tag-neutral'}`}>
                {nic.isSelected ? '已锁定直出' : (nic.status === 'Up' ? '点击切换' : '未连接')}
              </span>
            </div>
          ))}

          {virtualNics.map(v => (
            <div
              key={v.id}
              style={{
                borderRadius: 12,
                padding: '11px 14px',
                background: 'var(--bg-subtle)',
                opacity: 0.78,
                display: 'flex',
                justifyContent: 'space-between',
                alignItems: 'center',
                gap: 10
              }}
            >
              <div style={{ minWidth: 0 }}>
                <div style={{ fontWeight: 600, fontSize: 12.5 }}>{v.name} · {v.description}</div>
                <div className="num" style={{ fontSize: 11, color: 'var(--text-secondary)', marginTop: 2 }}>
                  Index #{v.interfaceIndex} · {v.iPv4Address ?? '—'} · {v.exclusionReason ?? '虚拟隧道适配器'}
                </div>
              </div>
              <span className="pill-tag tag-amber">已自动排斥</span>
            </div>
          ))}
        </div>
      </div>

      <div className="surface-card">
        <div style={{ padding: '18px 20px 14px' }}>
          <div className="card-title-sm">防 Fake-IP DNS 隔离与风控熔断锁</div>
          <div className="card-subtitle-sm" style={{ marginTop: 2 }}>防止 Clash DNS 劫持污染，并防止断线回退导致封号</div>
        </div>
        <div className="soft-divider" />

        <div style={{ padding: '14px 20px', display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12 }}>
          <div>
            <div style={{ fontWeight: 600, fontSize: 13 }}>TLS SNI 域名嗅探还原 (198.18.0.0/15)</div>
            <div style={{ fontSize: 11.5, color: 'var(--text-muted)', marginTop: 2 }}>发现 Clash Fake-IP 时从首包 ClientHello 提取真实域名后再转发</div>
          </div>
          <label className="nordic-switch">
            <input
              type="checkbox"
              checked={settings?.tlsSniRecoveryEnabled ?? true}
              onChange={e => updateToggle({ tlsSniRecoveryEnabled: e.target.checked }, 'TLS SNI 还原')}
            />
            <span className="nordic-slider" />
          </label>
        </div>
        <div className="soft-divider" />

        <div style={{ padding: '14px 20px', display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12 }}>
          <div>
            <div style={{ fontWeight: 600, fontSize: 13 }}>强制远端 DNS 解析（SOCKS5h / HTTP CONNECT）</div>
            <div style={{ fontSize: 11.5, color: 'var(--text-muted)', marginTop: 2 }}>目标域名不在本机系统解析，直接交由上游专线节点远端解析</div>
          </div>
          <label className="nordic-switch">
            <input
              type="checkbox"
              checked={settings?.forceRemoteDns ?? true}
              onChange={e => updateToggle({ forceRemoteDns: e.target.checked }, '强制远端 DNS')}
            />
            <span className="nordic-slider" />
          </label>
        </div>
        <div className="soft-divider" />

        <div style={{ padding: '14px 20px', display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12 }}>
          <div>
            <div style={{ fontWeight: 600, fontSize: 13 }}>上游节点域名物理网卡独立直连 DNS</div>
            <div style={{ fontSize: 11.5, color: 'var(--text-muted)', marginTop: 2 }}>代理节点本身为域名时，绑定物理网卡向 {settings?.dohServerIp ?? '223.5.5.5'} 解析</div>
          </div>
          <label className="nordic-switch">
            <input
              type="checkbox"
              checked={settings?.directPhysicalDohEnabled ?? true}
              onChange={e => updateToggle({ directPhysicalDohEnabled: e.target.checked }, '物理直连 DNS')}
            />
            <span className="nordic-slider" />
          </label>
        </div>
        <div className="soft-divider" />

        <div style={{ padding: '14px 20px', display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12 }}>
          <div>
            <div style={{ fontWeight: 600, fontSize: 13 }}>全局防串 IP 熔断锁（Fail-Close Kill Switch）</div>
            <div style={{ fontSize: 11.5, color: 'var(--text-muted)', marginTop: 2 }}>专线连接失败时立即拦截断连，严禁静默回退至本地宽带或 Clash</div>
          </div>
          <label className="nordic-switch">
            <input
              type="checkbox"
              checked={settings?.globalKillSwitch ?? true}
              onChange={e => updateToggle({ globalKillSwitch: e.target.checked }, '防串 IP 熔断锁')}
            />
            <span className="nordic-slider" />
          </label>
        </div>
        <div className="soft-divider" />

        <div style={{ padding: '14px 20px', display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12 }}>
          <div>
            <div style={{ fontWeight: 600, fontSize: 13 }}>启用 IPv6 双栈代理与物理网卡直绑</div>
            <div style={{ fontSize: 11.5, color: 'var(--text-muted)', marginTop: 2 }}>
              关闭时自动拦截 IPv6 目标，防止 IPv6 泄漏或单栈节点卡顿；开启后支持 [::1] 环回、AAAA 解析及 IPV6_UNICAST_IF 直绑
            </div>
          </div>
          <label className="nordic-switch">
            <input
              type="checkbox"
              checked={settings?.enableIpv6 ?? false}
              onChange={e => updateToggle({ enableIpv6: e.target.checked }, 'IPv6 双栈代理')}
            />
            <span className="nordic-slider" />
          </label>
        </div>
      </div>

      {/* L2 sing-box 现代高抗干扰协议扩展引擎卡片（跨两列展示） */}
      <div className="surface-card surface-card-pad" style={{ gridColumn: 'span 2' }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
          <div>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <span className="card-title-sm">L2 现代协议扩展引擎 (sing-box Sidecar)</span>
              <span className={`pill-tag ${sidecar?.installed ? 'tag-emerald' : 'tag-amber'}`}>
                {sidecar?.installed ? `已安装 ${sidecar.version ?? ''}`.trim() : '未安装（按需下载）'}
              </span>
              <span className={`pill-tag ${sidecar?.running ? 'tag-indigo' : 'tag-neutral'}`}>
                {sidecar?.running ? `运行中 · ${sidecar.activeL2NodeCount} 节点 · 绑定 ${sidecar.boundInterface ?? '自动'}` : '懒加载待机（0 进程）'}
              </span>
            </div>
            <div className="card-subtitle-sm" style={{ marginTop: 4 }}>
              用于承载 <span className="mono">VLESS Reality / Hysteria2 / TUIC / WireGuard</span>；仅当活跃席位或前置中转使用这些协议时自动按需拉起，并强制注入物理网卡 <span className="mono">bind_interface</span>。
            </div>
          </div>

          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <button
              type="button"
              className="btn-pill btn-indigo btn-sm"
              disabled={installingSb}
              onClick={handleInstallSingBox}
            >
              <RefreshSpinIcon size={12} spinning={installingSb} />
              <span>{installingSb ? '下载安装中...' : (sidecar?.installed ? '更新内核' : '一键安装 sing-box')}</span>
            </button>
            {sidecar?.running && (
              <button type="button" className="btn-pill btn-soft btn-sm" onClick={handleStopSingBox}>
                停止子进程
              </button>
            )}
          </div>
        </div>

        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1.35fr', gap: 12, marginTop: 14 }}>
          <div>
            <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>
              在线下载加速代理（可选，例如 http://127.0.0.1:7897）
            </div>
            <input
              className="clean-input mono"
              placeholder="留空则直连 GitHub Releases 下载"
              value={downloadProxy}
              onChange={e => setDownloadProxy(e.target.value)}
            />
          </div>
          <div>
            <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>
              自定义本地 sing-box 路径（离线手动导入，留空使用托管目录）
            </div>
            <div style={{ display: 'flex', gap: 8 }}>
              <input
                className="clean-input mono"
                placeholder={sidecar?.binaryPath ?? '例如 C:\\Tools\\sing-box.exe'}
                value={customPathInput}
                onChange={e => setCustomPathInput(e.target.value)}
              />
              <button type="button" className="btn-pill btn-soft btn-sm" onClick={handleSaveSingBoxPath}>
                保存路径
              </button>
            </div>
          </div>
        </div>

        {sidecar?.lastError && (
          <div style={{ marginTop: 10, fontSize: 11.5, color: 'var(--accent-coral-text)' }}>
            最近状态提示：{sidecar.lastError}
          </div>
        )}
      </div>
    </div>
  );
};
