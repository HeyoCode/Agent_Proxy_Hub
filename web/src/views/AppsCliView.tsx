import React, { useState } from 'react';
import { OverviewResponse } from '../api';

interface Props {
  data: OverviewResponse;
  onToast: (msg: string) => void;
}

export const AppsCliView: React.FC<Props> = ({ data, onToast }) => {
  const [cliTab, setCliTab] = useState<'ps' | 'zsh' | 'claude' | 'agy'>(data.os === 'mac' ? 'zsh' : 'ps');
  const codeText = data.cliSnippets[cliTab] ?? '';

  async function handleCopy() {
    await navigator.clipboard?.writeText(codeText);
    onToast('已复制独立 CLI / IDE 隔离配置命令');
  }

  return (
    <div style={{ display: 'grid', gridTemplateColumns: '1.15fr 1fr', gap: 12, alignItems: 'start' }}>
      <div className="tx-tray">
        <div className="tx-card tx-card-pad">
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 4 }}>
            <strong style={{ fontSize: 13.5 }}>AI 应用进程与独立端口映射</strong>
            <span className="badge badge-neutral">
              {data.os === 'mac' ? 'macOS 混合端口 + 启动器' : 'Windows 混合端口 + 进程注入'}
            </span>
          </div>
          <div style={{ fontSize: 12, color: 'var(--text-secondary)', marginBottom: 12 }}>
            绝不改动系统全局代理设置；每个 AI 软件通过独立本机混合端口与进程环境变量注入完成隔离，与 Clash 彻底平行互不干扰。
          </div>

          <div style={{ display: 'flex', flexDirection: 'column', gap: 9 }}>
            {data.seats.map(seat => {
              const bound = data.nodes.find(n => n.id === seat.boundNodeId);
              const proc = data.os === 'mac' ? seat.processMac : seat.processWin;
              return (
                <div className="rule-well" key={seat.id}>
                  <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 6 }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 9 }}>
                      <div style={{
                        width: 28, height: 28, borderRadius: 8, background: 'var(--card-solid)',
                        border: '1px solid var(--card-border)', display: 'flex', alignItems: 'center',
                        justifyContent: 'center', fontWeight: 700, fontSize: 11.5
                      }}>{seat.glyph}</div>
                      <div>
                        <div style={{ fontWeight: 700, fontSize: 12.5 }}>{seat.name}</div>
                        <div className="mono" style={{ fontSize: 11, color: 'var(--text-secondary)' }}>目标进程：{proc}</div>
                      </div>
                    </div>
                    <span className={`badge ${seat.enabled ? 'badge-emerald' : 'badge-neutral'}`}>
                      {seat.enabled ? '端口活跃' : '未启用'}
                    </span>
                  </div>
                  <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11.5, color: 'var(--text-secondary)' }}>
                    <span>专属入口：<strong className="mono" style={{ color: 'var(--text-primary)' }}>127.0.0.1:{seat.localPort}</strong></span>
                    <span>绑定专线：<strong style={{ color: 'var(--text-primary)' }}>{bound?.name ?? '未绑定'}</strong></span>
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      </div>

      <div className="tx-tray">
        <div className="tx-card tx-card-pad">
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 4 }}>
            <strong style={{ fontSize: 13.5 }}>CLI / IDE 一键隔离配置注入</strong>
            <span className="badge badge-indigo">防串线</span>
          </div>
          <div style={{ fontSize: 12, color: 'var(--text-secondary)', marginBottom: 10 }}>
            将终端代理指向独立端口，绕过系统里 Clash 设置的全局代理变量
          </div>

          <div className="dock-tray" style={{ padding: 3, borderRadius: 11, marginBottom: 10 }}>
            <button className={`dock-tab ${cliTab === 'ps' ? 'active' : ''}`} style={{ height: 26, fontSize: 11.5, borderRadius: 8 }} onClick={() => setCliTab('ps')}>PowerShell</button>
            <button className={`dock-tab ${cliTab === 'zsh' ? 'active' : ''}`} style={{ height: 26, fontSize: 11.5, borderRadius: 8 }} onClick={() => setCliTab('zsh')}>macOS / Zsh</button>
            <button className={`dock-tab ${cliTab === 'claude' ? 'active' : ''}`} style={{ height: 26, fontSize: 11.5, borderRadius: 8 }} onClick={() => setCliTab('claude')}>Claude Code</button>
            <button className={`dock-tab ${cliTab === 'agy' ? 'active' : ''}`} style={{ height: 26, fontSize: 11.5, borderRadius: 8 }} onClick={() => setCliTab('agy')}>Antigravity</button>
          </div>

          <div className="terminal-well">{codeText}</div>

          <div style={{ display: 'flex', gap: 8, marginTop: 11 }}>
            <button className="tx-btn tx-btn-primary" onClick={handleCopy}>复制隔离配置命令</button>
          </div>

          <div className="rule-well" style={{ marginTop: 12 }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 4 }}>
              <strong style={{ fontSize: 12 }}>关于会话用量免解密统计</strong>
              <span className="badge badge-emerald">零 MITM 风险</span>
            </div>
            <div style={{ fontSize: 11.5, color: 'var(--text-secondary)' }}>
              代理隧道保持端到端 TLS 1.3 原生加密不拆包；Token 与模型消耗通过增量扫描 <span className="mono">.claude / .codex / .gemini</span> 结构化日志元数据完成精确汇总。
            </div>
          </div>
        </div>
      </div>
    </div>
  );
};
