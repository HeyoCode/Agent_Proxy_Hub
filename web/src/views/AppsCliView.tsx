import React, { useEffect, useState } from 'react';
import { OverviewResponse, api, formatBytes } from '../api';
import { CheckTinyIcon, CopyIcon, PlayLaunchIcon, PlugTakeoverIcon, SeatBrandIcon } from '../components/Icons';
import { SegmentedTabs } from '../components/SegmentedTabs';
import { maskIp } from '../privacy';

interface Props {
  data: OverviewResponse;
  privacyMode?: boolean;
  onReload: () => Promise<void>;
  onToast: (msg: string) => void;
}

export const AppsCliView: React.FC<Props> = ({ data, privacyMode = false, onReload, onToast }) => {
  const [cliTab, setCliTab] = useState<'ps' | 'zsh' | 'claude' | 'agy'>(data.os === 'mac' ? 'zsh' : 'ps');
  const [copied, setCopied] = useState(false);
  const [busySeat, setBusySeat] = useState<string | null>(null);
  const [focusedSeatId, setFocusedSeatId] = useState<string>(data.seats[0]?.id ?? 'claude');
  const [appSearch, setAppSearch] = useState('');
  const [appFilter, setAppFilter] = useState<'all' | 'active'>('all');

  const focusedSeat = data.seats.find(s => s.id === focusedSeatId) ?? data.seats[0];
  const [customExeDraft, setCustomExeDraft] = useState('');
  const [customNoProxyDraft, setCustomNoProxyDraft] = useState('');
  const [savingSeatPrefs, setSavingSeatPrefs] = useState(false);

  useEffect(() => {
    setCustomExeDraft(focusedSeat?.customExecutablePath ?? '');
    setCustomNoProxyDraft(focusedSeat?.customNoProxy ?? '');
  }, [focusedSeat?.id, focusedSeat?.customExecutablePath, focusedSeat?.customNoProxy]);

  const focusedCmdWin = focusedSeat?.launchCommandWin || focusedSeat?.id || 'claude';
  const focusedCmdMac = focusedSeat?.launchCommandMac || focusedSeat?.id || 'claude';
  const focusedPort = focusedSeat?.localPort ?? 17891;
  const effectiveNoProxyWin = [
    'localhost,127.0.0.1,::1,192.168.0.0/16,10.0.0.0/8',
    ...(focusedSeat?.customNoProxy ? focusedSeat.customNoProxy.split(',').map(s => s.trim()).filter(Boolean) : [])
  ].join(',');
  const effectiveNoProxyMac = [
    'localhost,127.0.0.1,::1',
    ...(focusedSeat?.customNoProxy ? focusedSeat.customNoProxy.split(',').map(s => s.trim()).filter(Boolean) : [])
  ].join(',');

  const codeText = (() => {
    if (cliTab === 'ps' && focusedSeat) {
      return `# Windows PowerShell · ${focusedSeat.name} 独立专线注入（不改系统代理，绕过 Clash）\n$env:HTTPS_PROXY="http://127.0.0.1:${focusedPort}"\n$env:HTTP_PROXY="http://127.0.0.1:${focusedPort}"\n$env:ALL_PROXY="socks5h://127.0.0.1:${focusedPort}"\n$env:NO_PROXY="${effectiveNoProxyWin}"\n${focusedCmdWin}`;
    }
    if (cliTab === 'zsh' && focusedSeat) {
      return `# macOS / Linux Zsh · ${focusedSeat.name} 独立专线注入（不改系统代理，绕过 Surge/Clash）\nexport HTTPS_PROXY="http://127.0.0.1:${focusedPort}"\nexport HTTP_PROXY="http://127.0.0.1:${focusedPort}"\nexport ALL_PROXY="socks5h://127.0.0.1:${focusedPort}"\nexport NO_PROXY="${effectiveNoProxyMac}"\n${focusedCmdMac}`;
    }
    return data.cliSnippets[cliTab] ?? '';
  })();

  async function handleSaveSeatOverrides(e: React.FormEvent) {
    e.preventDefault();
    if (!focusedSeat || savingSeatPrefs) return;
    setSavingSeatPrefs(true);
    try {
      await api.updateSeat(focusedSeat.id, {
        customExecutablePath: customExeDraft.trim(),
        customNoProxy: customNoProxyDraft.trim()
      });
      await onReload();
      onToast(`已保存 ${focusedSeat.name} 自定义可执行路径与内网绕过规则`);
    } finally {
      setSavingSeatPrefs(false);
    }
  }

  const filteredSeats = data.seats.filter(seat => {
    const tk = data.seatTakeovers?.find(t => t.seatId === seat.id);
    if (appFilter === 'active' && !seat.enabled && !tk?.isTakenOver && !tk?.isProcessRunning) return false;
    const kw = appSearch.trim().toLowerCase();
    if (!kw) return true;
    return `${seat.name} ${seat.id} ${seat.subtitle} ${seat.processWin} ${seat.localPort}`.toLowerCase().includes(kw);
  });

  async function handleCopy() {
    await navigator.clipboard?.writeText(codeText);
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1500);
    onToast('已复制独立 CLI / IDE 隔离配置命令');
  }

  async function handleToggleTakeover(seatId: string, enable: boolean) {
    setBusySeat(seatId);
    try {
      const res = await api.setSeatTakeover(seatId, enable);
      await onReload();
      onToast(res.message);
    } catch (err) {
      onToast(err instanceof Error ? err.message : '接管操作失败');
    } finally {
      setBusySeat(null);
    }
  }

  async function handleLaunch(seatId: string) {
    setBusySeat(seatId);
    try {
      const res = await api.launchSeatApp(seatId);
      await onReload();
      onToast(res.message);
    } catch (err) {
      onToast(err instanceof Error ? err.message : '启动失败');
    } finally {
      setBusySeat(null);
    }
  }

  return (
    <div style={{ display: 'grid', gridTemplateColumns: '1.18fr 1fr', gap: 18, alignItems: 'start' }}>
      <div className="surface-card surface-card-pad">
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 4 }}>
          <span className="card-title-sm">AI 应用无损接管与独立端口映射</span>
          <span className="pill-tag tag-indigo num">
            支持 {data.seats.length} 款应用
          </span>
        </div>
        <div className="card-subtitle-sm" style={{ marginBottom: 12 }}>
          不改系统全局代理；点击「一键接管」即可自动备份并绑定对应软件配置（支持无损一键还原）。
        </div>

        <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginBottom: 12, flexWrap: 'wrap' }}>
          <input
            className="clean-input"
            style={{ flex: 1, minWidth: 160, height: 32, fontSize: 12 }}
            placeholder="搜索应用名称、进程或端口（如 Gemini / OpenClaw / Kimi）..."
            value={appSearch}
            onChange={e => setAppSearch(e.target.value)}
          />
          <SegmentedTabs<'all' | 'active'>
            value={appFilter}
            onChange={setAppFilter}
            items={[
              { value: 'all', label: '全部', count: data.seats.length },
              {
                value: 'active',
                label: '已启用/接管',
                count: data.seats.filter(s => {
                  const tk = data.seatTakeovers?.find(t => t.seatId === s.id);
                  return s.enabled || tk?.isTakenOver || tk?.isProcessRunning;
                }).length
              }
            ]}
          />
        </div>

        <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          {filteredSeats.map(seat => {
            const bound = data.nodes.find(n => n.id === seat.boundNodeId);
            const proc = data.os === 'mac' ? seat.processMac : seat.processWin;
            const takeover = data.seatTakeovers?.find(t => t.seatId === seat.id);
            const snap = data.trafficSnapshots.find(t => t.seatId === seat.id);
            const isBusy = busySeat === seat.id;
            const isFocused = focusedSeat?.id === seat.id;

            return (
              <div
                key={seat.id}
                onClick={() => setFocusedSeatId(seat.id)}
                style={{
                  background: 'var(--bg-subtle)',
                  borderRadius: 12,
                  padding: '12px 14px',
                  border: isFocused ? '1px solid var(--accent-indigo)' : '1px solid transparent',
                  cursor: 'pointer',
                  display: 'flex',
                  flexDirection: 'column',
                  gap: 7,
                  transition: 'border-color 0.16s ease'
                }}
              >
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8 }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 10, minWidth: 0 }}>
                    <div
                      style={{
                        width: 32,
                        height: 32,
                        borderRadius: 10,
                        background: 'var(--bg-card)',
                        display: 'flex',
                        alignItems: 'center',
                        justifyContent: 'center',
                        color: 'var(--text-primary)',
                        flexShrink: 0
                      }}
                    >
                      <SeatBrandIcon seatId={seat.id} size={19} />
                    </div>
                    <div style={{ minWidth: 0 }}>
                      <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                        <span style={{ fontWeight: 600, fontSize: 13 }}>{seat.name}</span>
                        {takeover?.isProcessRunning && (
                          <span className="pill-tag tag-indigo" style={{ fontSize: 10, padding: '1px 7px' }}>运行中</span>
                        )}
                      </div>
                      <div className="num" style={{ fontSize: 11, color: 'var(--text-muted)' }}>
                        目标进程：<span className="mono">{proc}</span>
                      </div>
                    </div>
                  </div>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0 }}>
                    <span className={`pill-tag ${takeover?.isTakenOver ? 'tag-emerald' : 'tag-amber'}`}>
                      {takeover?.isTakenOver ? '已接管' : '待接管'}
                    </span>
                    <span className={`pill-tag ${seat.enabled ? 'tag-indigo' : 'tag-neutral'}`}>
                      {seat.enabled ? `:${seat.localPort}` : '暂停'}
                    </span>
                  </div>
                </div>

                <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11.5, color: 'var(--text-secondary)' }}>
                  <span>绑定专线：<strong style={{ color: 'var(--text-primary)' }}>{bound?.name ?? '未绑定'}{bound?.exitIp && bound.exitIp !== '—' ? ` (${maskIp(bound.exitIp, privacyMode)})` : ''}</strong></span>
                  <span className="num">直出吞吐：<strong style={{ color: 'var(--text-primary)' }}>↑ {formatBytes(snap?.bytesTx ?? 0)} / ↓ {formatBytes(snap?.bytesRx ?? 0)}</strong></span>
                </div>

                {takeover && (
                  <div style={{ fontSize: 11, color: 'var(--text-muted)', lineHeight: 1.45 }}>
                    {takeover.statusHint}
                  </div>
                )}

                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8, paddingTop: 2 }}>
                  <span className="mono" style={{ fontSize: 10.5, color: 'var(--text-muted)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                    {takeover?.modeSummary ?? '独立端口模式'}
                  </span>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0 }}>
                    <button
                      type="button"
                      disabled={isBusy}
                      className={`btn-pill btn-sm ${takeover?.isTakenOver ? 'btn-soft' : 'btn-indigo'}`}
                      onClick={() => handleToggleTakeover(seat.id, !(takeover?.isTakenOver ?? false))}
                    >
                      <PlugTakeoverIcon size={12} />
                      <span>{takeover?.isTakenOver ? '还原默认' : '一键接管'}</span>
                    </button>
                    <button
                      type="button"
                      disabled={isBusy}
                      className="btn-pill btn-sm btn-soft"
                      onClick={() => handleLaunch(seat.id)}
                    >
                      <PlayLaunchIcon size={12} />
                      <span>专线启动</span>
                    </button>
                  </div>
                </div>
              </div>
            );
          })}
        </div>
      </div>

      <div className="surface-card surface-card-pad">
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 4 }}>
          <span className="card-title-sm">CLI / IDE 隔离配置命令参考</span>
          <span className="pill-tag tag-indigo mono">
            {focusedSeat ? `${focusedSeat.name} · :${focusedSeat.localPort}` : '防串线'}
          </span>
        </div>
        <div className="card-subtitle-sm" style={{ marginBottom: 12 }}>
          点击左侧任意应用即可切换专属命令；在任意终端粘贴即可临时覆盖 Clash 系统变量
        </div>

        <div style={{ marginBottom: 12 }}>
          <SegmentedTabs<'ps' | 'zsh' | 'claude' | 'agy'>
            value={cliTab}
            onChange={setCliTab}
            items={[
              { value: 'ps', label: 'PowerShell' },
              { value: 'zsh', label: 'macOS / Zsh' },
              { value: 'claude', label: 'Claude Code' },
              { value: 'agy', label: 'Antigravity' }
            ]}
          />
        </div>

        <div className="code-surface">{codeText}</div>

        <div style={{ display: 'flex', gap: 8, marginTop: 12 }}>
          <button type="button" className={`btn-pill ${copied ? 'btn-soft' : 'btn-indigo'}`} onClick={handleCopy}>
            {copied ? <CheckTinyIcon size={13} /> : <CopyIcon size={13} />}
            <span>{copied ? '已复制命令' : '复制隔离配置命令'}</span>
          </button>
        </div>

        <div style={{ marginTop: 14, padding: '12px 14px', borderRadius: 12, background: 'var(--bg-subtle)' }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 4 }}>
            <strong style={{ fontSize: 12.5 }}>关于吞吐计数与用量统计</strong>
            <span className="pill-tag tag-emerald">双源零冲突</span>
          </div>
          <div style={{ fontSize: 12, color: 'var(--text-secondary)', lineHeight: 1.55 }}>
            Token 用量直接扫描 <span className="mono">.claude / .codex / .gemini</span> 会话日志；右栏「累计直出吞吐」统计经由 <span className="mono">127.0.0.1:1789x</span> 端口的真实 TCP 流量。完成左侧「一键接管」并重启对应软件后，流量与 Token 即会同步归集。
          </div>
        </div>

        {focusedSeat && (
          <form
            onSubmit={handleSaveSeatOverrides}
            style={{ marginTop: 14, padding: '12px 14px', borderRadius: 12, background: 'var(--bg-subtle)', display: 'flex', flexDirection: 'column', gap: 10 }}
          >
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
              <strong style={{ fontSize: 12.5 }}>{focusedSeat.name} · 自定义启动路径与绕过域</strong>
              <span className="pill-tag tag-indigo mono">NO_PROXY</span>
            </div>
            <div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 4 }}>
                自定义可执行文件 / CLI 命令路径（留空自动探测）
              </div>
              <input
                className="clean-input mono"
                style={{ fontSize: 11.5 }}
                placeholder={data.os === 'mac' ? '例如：/Applications/Cursor.app' : '例如：D:\\Apps\\Cursor\\Cursor.exe'}
                value={customExeDraft}
                onChange={e => setCustomExeDraft(e.target.value)}
              />
            </div>
            <div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 4 }}>
                附加内网绕过规则（追加到 NO_PROXY，逗号分隔）
              </div>
              <input
                className="clean-input mono"
                style={{ fontSize: 11.5 }}
                placeholder="例如：*.internal.corp, git.company.local, 172.25.0.0/16"
                value={customNoProxyDraft}
                onChange={e => setCustomNoProxyDraft(e.target.value)}
              />
            </div>
            <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
              <button type="submit" className="btn-pill btn-indigo btn-sm" disabled={savingSeatPrefs}>
                {savingSeatPrefs ? '保存中...' : '保存应用级配置'}
              </button>
            </div>
          </form>
        )}
      </div>
    </div>
  );
};
