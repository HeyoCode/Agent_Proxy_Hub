export function loadPrivacyMode(): boolean {
  try {
    return window.localStorage.getItem('aph_privacy_mode') === '1';
  } catch {
    return false;
  }
}

export function savePrivacyMode(enabled: boolean): void {
  try {
    window.localStorage.setItem('aph_privacy_mode', enabled ? '1' : '0');
  } catch {
    // ignore storage quota errors
  }
}

export function maskIp(ip: string | null | undefined, privacyMode: boolean): string {
  if (!ip || ip === '—') return ip ?? '—';
  if (!privacyMode) return ip;
  if (ip === '物理网卡直出') return '物理出口 (已脱敏)';
  if (ip.includes('.')) {
    const parts = ip.split('.');
    if (parts.length === 4) return `${parts[0]}.${parts[1]}.*.*`;
  }
  if (ip.includes(':')) {
    const parts = ip.split(':').filter(Boolean);
    return parts.length >= 2 ? `${parts[0]}:${parts[1]}:*:*` : 'IPv6:*:*';
  }
  return '•••.•••';
}

export function maskHost(host: string | null | undefined, privacyMode: boolean): string {
  if (!host) return '—';
  if (!privacyMode) return host;
  if (/^\d+\.\d+\.\d+\.\d+$/.test(host) || host.includes(':')) return maskIp(host, true);
  const segs = host.split('.');
  if (segs.length >= 2) {
    const head = segs[0].length > 2 ? `${segs[0].slice(0, 2)}***` : '***';
    return `${head}.${segs.slice(-1)[0]}`;
  }
  return '***';
}

export function maskEndpoint(host: string | null | undefined, port: number, privacyMode: boolean): string {
  if (!privacyMode) return `${host ?? '—'}:${port}`;
  return `${maskHost(host, true)}:${port}`;
}

export function maskGeneric(text: string | null | undefined, privacyMode: boolean): string {
  if (!text) return '—';
  if (!privacyMode) return text;
  if (text.length <= 4) return '••••';
  return `${text.slice(0, 2)}••••${text.slice(-2)}`;
}
