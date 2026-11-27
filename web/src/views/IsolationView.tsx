import React, { useEffect, useState } from 'react';
import {
  HubUiPreferences,
  IsolationSettings,
  NetworkAdapterInfo,
  SidecarEngineStatus,
  StorageStatsDto,
  api,
  formatBytes
} from '../api';
import { RefreshSpinIcon } from '../components/Icons';
import { sendWindowCommand } from '../windowBridge';

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
  const [importPkgPath, setImportPkgPath] = useState('');
  const [importSha256, setImportSha256] = useState('');
  const [importingSb, setImportingSb] = useState(false);

  const [prefs, setPrefs] = useState<HubUiPreferences>({
    closeAction: 'minimize_to_tray',
    trayLeftClickAction: 'toggle_flyout',
    miniCardEnabled: false
  });
  const [wdUrl, setWdUrl] = useState('');
  const [wdUser, setWdUser] = useState('');
  const [wdPass, setWdPass] = useState('');
  const [wdPhrase, setWdPhrase] = useState('');
  const [wdBusy, setWdBusy] = useState(false);
  const [storageStats, setStorageStats] = useState<StorageStatsDto | null>(null);
  const [cleaningStorage, setCleaningStorage] = useState(false);

  async function load() {
    const [res, prefRes, stRes] = await Promise.all([
      api.getIsolation(),
      api.getPreferences(),
      api.getMaintenanceStats()
    ]);
    setSettings(res.settings);
    setAdapters(res.adapters);
    if (res.sidecarEngine) setSidecar(res.sidecarEngine);
    setCustomPathInput(res.settings.customSingBoxPath ?? '');
    setPrefs(prefRes);
    setWdUrl(prefRes.webDavUrl ?? '');
    setWdUser(prefRes.webDavUsername ?? '');
    setStorageStats(stRes);
  }

  async function handleCleanupStorage() {
    if (cleaningStorage) return;
    setCleaningStorage(true);
    try {
      const res = await api.cleanupMaintenance();
      setStorageStats(res.stats);
      onToast(res.message);
    } finally {
      setCleaningStorage(false);
    }
  }

  async function handleImportOfflineSingBox() {
    if (!importPkgPath.trim() || importingSb) return;
    setImportingSb(true);
    try {
      const res = await api.importSingBox(importPkgPath.trim(), importSha256.trim() || undefined);
      setSidecar(res.status);
      await onReloadOverview();
      onToast(res.message);
      if (res.ok) {
        setImportPkgPath('');
        setImportSha256('');
      }
    } catch (err) {
      onToast(err instanceof Error ? err.message : '离线导入 sing-box 失败');
    } finally {
      setImportingSb(false);
    }
  }

  async function savePreferences(partial: Partial<HubUiPreferences>, tip: string) {
    const updated = await api.updatePreferences(partial);
    setPrefs(updated);
    sendWindowCommand({ type: 'window:preferencesSync' });
    await onReloadOverview();
    onToast(tip);
  }

  async function handleSaveWebDavMeta() {
    setWdBusy(true);
    try {
      const updated = await api.updatePreferences({
        webDavUrl: wdUrl.trim(),
        webDavUsername: wdUser.trim(),
        ...(wdPass ? { webDavPassword: wdPass } : {}),
        ...(wdPhrase ? { webDavSyncPassphrase: wdPhrase } : {})
      });
      setPrefs(updated);
      setWdPass('');
      setWdPhrase('');
      onToast('已加密保存 WebDAV 同步凭据');
    } finally {
      setWdBusy(false);
    }
  }

  async function handleTestWebDav() {
    setWdBusy(true);
    try {
      const res = await api.testWebDav({
        webDavUrl: wdUrl.trim() || undefined,
        webDavUsername: wdUser.trim() || undefined,
        webDavPassword: wdPass || undefined
      });
      onToast(res.message);
    } finally {
      setWdBusy(false);
    }
  }

  async function handlePushWebDav() {
    setWdBusy(true);
    try {
      const res = await api.pushWebDav({
        webDavUrl: wdUrl.trim() || undefined,
        webDavUsername: wdUser.trim() || undefined,
        webDavPassword: wdPass || undefined,
        passphrase: wdPhrase || undefined
      });
      if (res.lastWebDavSyncAt) {
        setPrefs(p => ({ ...p, lastWebDavSyncAt: res.lastWebDavSyncAt }));
      }
      onToast(res.message);
    } finally {
      setWdBusy(false);
    }
  }

  async function handlePullWebDav() {
    setWdBusy(true);
    try {
      const res = await api.pullWebDav({
        webDavUrl: wdUrl.trim() || undefined,
        webDavUsername: wdUser.trim() || undefined,
        webDavPassword: wdPass || undefined,
        passphrase: wdPhrase || undefined
      });
      await load();
      await onReloadOverview();
      onToast(res.message);
    } finally {
      setWdBusy(false);
    }
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

        {sidecar?.binarySha256 && (
          <div style={{ marginTop: 10, fontSize: 11, color: 'var(--text-muted)' }}>
            内核 SHA-256 指纹：<span className="mono">{sidecar.binarySha256}</span>
          </div>
        )}

        <div style={{ display: 'grid', gridTemplateColumns: '1.2fr 1fr auto', gap: 10, marginTop: 12, alignItems: 'end' }}>
          <div>
            <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>
              离线导入安装包（支持 .zip / .tar.gz / sing-box.exe，内置 Zip-Slip 防护）
            </div>
            <input
              className="clean-input mono"
              placeholder="例如 D:\Downloads\sing-box-windows-amd64.zip"
              value={importPkgPath}
              onChange={e => setImportPkgPath(e.target.value)}
            />
          </div>
          <div>
            <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>
              预期 SHA-256（可选，严格校验防篡改）
            </div>
            <input
              className="clean-input mono"
              placeholder="留空跳过，或粘贴 64 位 SHA-256"
              value={importSha256}
              onChange={e => setImportSha256(e.target.value)}
            />
          </div>
          <button
            type="button"
            className="btn-pill btn-indigo btn-sm"
            style={{ height: 34 }}
            disabled={importingSb || !importPkgPath.trim()}
            onClick={handleImportOfflineSingBox}
          >
            {importingSb ? '校验导入中...' : '校验并导入'}
          </button>
        </div>

        {sidecar?.lastError && (
          <div style={{ marginTop: 10, fontSize: 11.5, color: 'var(--accent-coral-text)' }}>
            最近状态提示：{sidecar.lastError}
          </div>
        )}
      </div>

      {/* 桌面交互偏好与跨机器 WebDAV 加密同步卡片 */}
      <div className="surface-card surface-card-pad" style={{ gridColumn: 'span 2' }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 12, flexWrap: 'wrap', gap: 8 }}>
          <div>
            <div className="card-title-sm">桌面客户端交互偏好 &amp; WebDAV 跨机器加密同步</div>
            <div className="card-subtitle-sm">
              本地采用 PBKDF2-SHA256 + AES-256-GCM 加密后上传至您的私有 WebDAV 网盘，云端零明文存储节点密码与订阅地址
            </div>
          </div>
          {prefs.lastWebDavSyncAt && (
            <span className="pill-tag tag-emerald num">
              最近同步：{new Date(prefs.lastWebDavSyncAt).toLocaleString('zh-CN', { hour12: false })}
            </span>
          )}
        </div>

        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1.4fr', gap: 18 }}>
          <div style={{ background: 'var(--bg-subtle)', borderRadius: 12, padding: 14, display: 'flex', flexDirection: 'column', gap: 10 }}>
            <strong style={{ fontSize: 12.5 }}>桌面窗口与悬浮监控偏好</strong>
            <div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 4 }}>主窗口右上角「关闭」按钮行为</div>
              <select
                className="clean-input"
                value={prefs.closeAction}
                onChange={e => savePreferences({ closeAction: e.target.value as 'minimize_to_tray' | 'exit' }, '已更新窗口关闭行为')}
              >
                <option value="minimize_to_tray">最小化到系统托盘（保持后台代理常驻）</option>
                <option value="exit">直接彻底退出程序</option>
              </select>
            </div>
            <div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 4 }}>系统托盘图标「左键单击」动作</div>
              <select
                className="clean-input"
                value={prefs.trayLeftClickAction}
                onChange={e => savePreferences({ trayLeftClickAction: e.target.value as 'toggle_flyout' | 'show_main' | 'toggle_mini' }, '已更新托盘点击动作')}
              >
                <option value="toggle_flyout">呼出托盘快捷开关面板</option>
                <option value="show_main">直接拉起主控制台窗口</option>
                <option value="toggle_mini">切换置顶极简悬浮监控卡片</option>
              </select>
            </div>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginTop: 2 }}>
              <span style={{ fontSize: 12 }}>置顶极简悬浮监控窗（Always-on-Top）</span>
              <label className="nordic-switch">
                <input
                  type="checkbox"
                  checked={prefs.miniCardEnabled}
                  onChange={e => savePreferences({ miniCardEnabled: e.target.checked }, e.target.checked ? '已开启置顶悬浮监控窗' : '已关闭置顶悬浮监控窗')}
                />
                <span className="nordic-slider" />
              </label>
            </div>
          </div>

          <div style={{ background: 'var(--bg-subtle)', borderRadius: 12, padding: 14, display: 'flex', flexDirection: 'column', gap: 10 }}>
            <strong style={{ fontSize: 12.5 }}>WebDAV 零明文加密同步（坚果云 / Nextcloud / Alist）</strong>
            <div style={{ display: 'grid', gridTemplateColumns: '1.5fr 1fr', gap: 8 }}>
              <input
                className="clean-input mono"
                placeholder="WebDAV 目录 URL（如 https://dav.jianguoyun.com/dav/）"
                value={wdUrl}
                onChange={e => setWdUrl(e.target.value)}
              />
              <input
                className="clean-input"
                placeholder="WebDAV 用户名"
                value={wdUser}
                onChange={e => setWdUser(e.target.value)}
              />
            </div>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 8 }}>
              <input
                type="password"
                className="clean-input"
                placeholder={prefs.hasWebDavPassword ? '已加密保存应用密码（留空沿用）' : 'WebDAV 应用密码'}
                value={wdPass}
                onChange={e => setWdPass(e.target.value)}
              />
              <input
                type="password"
                className="clean-input"
                placeholder={prefs.hasSyncPassphrase ? '已设置 AES-GCM 主口令（留空沿用）' : '同步加密主口令（至少 4 位）'}
                value={wdPhrase}
                onChange={e => setWdPhrase(e.target.value)}
              />
            </div>
            <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', justifyContent: 'flex-end' }}>
              <button type="button" className="btn-pill btn-soft btn-sm" disabled={wdBusy} onClick={handleSaveWebDavMeta}>保存配置</button>
              <button type="button" className="btn-pill btn-soft btn-sm" disabled={wdBusy} onClick={handleTestWebDav}>测试连通</button>
              <button type="button" className="btn-pill btn-indigo btn-sm" disabled={wdBusy} onClick={handlePushWebDav}>AES-GCM 加密上传</button>
              <button type="button" className="btn-pill btn-soft btn-sm" disabled={wdBusy} onClick={handlePullWebDav}>拉取解密恢复</button>
            </div>
          </div>
        </div>
      </div>

      {/* 本地存储维护与 aph CLI 命令行自动化 */}
      <div className="surface-card surface-card-pad" style={{ gridColumn: 'span 2' }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 12, flexWrap: 'wrap', gap: 8 }}>
          <div>
            <div className="card-title-sm">本地存储维护 &amp; 独立命令行工具 (<span className="mono">aph</span>)</div>
            <div className="card-subtitle-sm">
              一键安全清理失效接管备份与临时缓存文件（绝对保留 <span className="mono">state.json / vault.key</span> 与多开数据），并通过 <span className="mono">aph</span> 脚本自动化控制本网关
            </div>
          </div>
          <button
            type="button"
            className="btn-pill btn-indigo btn-sm"
            disabled={cleaningStorage}
            onClick={handleCleanupStorage}
          >
            {cleaningStorage ? '清理中...' : '安全清理失效备份与临时文件'}
          </button>
        </div>

        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1.4fr', gap: 18 }}>
          <div style={{ background: 'var(--bg-subtle)', borderRadius: 12, padding: 14, display: 'flex', flexDirection: 'column', gap: 8 }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 12 }}>
              <span style={{ color: 'var(--text-muted)' }}>配置根目录</span>
              <span className="mono" style={{ fontSize: 11 }}>{storageStats?.hubDir ?? '~/.agent-proxy-hub'}</span>
            </div>
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 8, marginTop: 4 }}>
              <div>
                <div style={{ fontSize: 10.5, color: 'var(--text-muted)' }}>总占用</div>
                <div className="num" style={{ fontWeight: 700, fontSize: 13 }}>{formatBytes(storageStats?.totalBytes ?? 0)}</div>
              </div>
              <div>
                <div style={{ fontSize: 10.5, color: 'var(--text-muted)' }}>备份文件</div>
                <div className="num" style={{ fontWeight: 700, fontSize: 13 }}>{storageStats?.takeoverBackupCount ?? 0}</div>
              </div>
              <div>
                <div style={{ fontSize: 10.5, color: 'var(--text-muted)' }}>临时残片</div>
                <div className="num" style={{ fontWeight: 700, fontSize: 13 }}>{storageStats?.tempFileCount ?? 0}</div>
              </div>
              <div>
                <div style={{ fontSize: 10.5, color: 'var(--text-muted)' }}>多开目录</div>
                <div className="num" style={{ fontWeight: 700, fontSize: 13 }}>{storageStats?.profileCount ?? 0}</div>
              </div>
            </div>
          </div>

          <div className="code-surface" style={{ fontSize: 11.5, padding: '10px 14px' }}>
            {`# aph 自动化命令行工具（支持 --json 输出，自动绕过外部系统代理）\naph status --json\naph bind cursor <nodeId> --fallback <backupNodeId>\naph takeover windsurf on && aph verify --all`}
          </div>
        </div>
      </div>
    </div>
  );
};
