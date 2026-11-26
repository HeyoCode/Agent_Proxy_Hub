export type ProxyProtocol =
  | 'SOCKS5'
  | 'SOCKS4'
  | 'HTTP'
  | 'HTTPS'
  | 'DIRECT_PHYSICAL'
  | 'TROJAN'
  | 'SHADOWSOCKS'
  | 'VLESS'
  | 'HYSTERIA2'
  | 'TUIC'
  | 'WIREGUARD';

export interface ProxyNode {
  id: string;
  name: string;
  group: string;
  protocol: ProxyProtocol;
  host: string;
  port: number;
  username?: string | null;
  password?: string | null;
  method?: string | null;
  sni?: string | null;
  allowInsecure?: boolean;
  extra?: Record<string, string> | null;
  requiresSidecarEngine?: boolean;
  engineKind?: 'native' | 'sing-box';
  enabled: boolean;
  status: 'online' | 'offline' | 'unverified';
  latencyMs?: number | null;
  exitIp: string;
  ispSummary: string;
  aiReachability: string[];
}

export interface AppSeat {
  id: string;
  glyph: string;
  name: string;
  subtitle: string;
  processWin: string;
  processMac: string;
  localPort: number;
  boundNodeId?: string | null;
  relayNodeId?: string | null;
  enabled: boolean;
  killSwitchEnabled: boolean;
  allowSameGroupFailover: boolean;
  captureMode: string;
  launchCommandWin?: string | null;
  launchCommandMac?: string | null;
}

export interface NetworkAdapterInfo {
  id: string;
  name: string;
  description: string;
  interfaceIndex: number;
  iPv4Address?: string | null;
  iPv6InterfaceIndex?: number;
  iPv6Address?: string | null;
  gatewayAddress?: string | null;
  status: string;
  isVirtualOrTunnel: boolean;
  exclusionReason?: string | null;
  isSelected: boolean;
}

export interface IsolationSettings {
  defaultMixedPort: number;
  lockedPhysicalNicId?: string | null;
  autoBypassVirtualNics: boolean;
  tlsSniRecoveryEnabled: boolean;
  forceRemoteDns: boolean;
  directPhysicalDohEnabled: boolean;
  dohServerIp: string;
  globalKillSwitch: boolean;
  enableIpv6: boolean;
  customSingBoxPath?: string | null;
}

export interface SidecarEngineStatus {
  installed: boolean;
  running: boolean;
  binaryPath?: string | null;
  version?: string | null;
  activeL2NodeCount: number;
  boundInterface?: string | null;
  lastError?: string | null;
}

export interface SeatTrafficSnapshot {
  seatId: string;
  appName: string;
  localPort: number;
  boundNodeName: string;
  exitIp: string;
  activeConnections: number;
  totalConnections: number;
  bytesTx: number;
  bytesRx: number;
  bytesTxPerSec?: number;
  bytesRxPerSec?: number;
  sniRecoveredCount: number;
  killSwitchBlockedCount: number;
  ipv6BlockedCount?: number;
}

export interface SeatTakeoverStatus {
  seatId: string;
  isProcessRunning: boolean;
  supportsConfigTakeover: boolean;
  isTakenOver: boolean;
  modeSummary: string;
  targetPath?: string | null;
  launchExecutable?: string | null;
  statusHint: string;
}

export interface TunnelVerificationResult {
  ok: boolean;
  seatId: string;
  localPort: number;
  exitIp: string;
  latencyMs?: number | null;
  bytesTx: number;
  bytesRx: number;
  aiReachability: string[];
  message: string;
  node?: ProxyNode | null;
}

export interface ProjectMetadataDto {
  name: string;
  version: string;
  repoOwner: string;
  repoName: string;
  repoSlug: string;
  repoUrl: string;
  cloneUrl: string;
  author: string;
  description: string;
}

export interface OverviewResponse {
  project?: ProjectMetadataDto;
  os: 'win' | 'mac';
  arch: string;
  memoryMb: number;
  lockedNic?: NetworkAdapterInfo | null;
  hasDetectedTunnel: boolean;
  excludedVirtualNics: NetworkAdapterInfo[];
  isolation: IsolationSettings;
  seats: AppSeat[];
  nodes: ProxyNode[];
  trafficSnapshots: SeatTrafficSnapshot[];
  seatTakeovers?: SeatTakeoverStatus[];
  sidecarEngine?: SidecarEngineStatus;
  cliSnippets: Record<string, string>;
}

export interface BatchParseResult {
  totalLines: number;
  validCount: number;
  duplicateCount: number;
  errorCount: number;
  items: {
    lineNumber: number;
    rawLine: string;
    valid: boolean;
    isDuplicate: boolean;
    errorReason?: string | null;
    node?: ProxyNode | null;
  }[];
}

export interface UsageLogEntry {
  id: string;
  timestamp: string;
  appName: string;
  source: string;
  model: string;
  effectiveInputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  totalTokens: number;
  durationSeconds: number;
  firstTokenSeconds?: number | null;
  tokensPerSecond?: number | null;
  costUsd?: number | null;
  isPriced: boolean;
}

export interface UsageBucketPoint {
  label: string;
  requests: number;
  totalTokens: number;
  effectiveInputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheHitRate: number;
  costUsd: number;
}

export interface ModelUsageSummary {
  model: string;
  requests: number;
  effectiveInputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  totalTokens: number;
  cacheHitRate: number;
  avgTokensPerSecond: number;
  costUsd: number;
  hasPricing: boolean;
}

export interface AppUsageAndProxySummary {
  appName: string;
  requests: number;
  totalTokens: number;
  cacheHitRate: number;
  costUsd: number;
  seatPort?: string | null;
  boundNodeName?: string | null;
  exitIp?: string | null;
  proxyConnections: number;
  bytesTx: number;
  bytesRx: number;
}

export interface UsageDashboardResponse {
  range: string;
  lastSyncedAt?: string | null;
  scannedFilesCount: number;
  syncElapsedMs: number;
  totalCostUsd: number;
  totalRequests: number;
  totalTokens: number;
  effectiveInputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  cacheHitRate: number;
  pricedRequests: number;
  unpricedRequests: number;
  totalProxyTxBytes: number;
  totalProxyRxBytes: number;
  totalProxyTxBytesPerSec?: number;
  totalProxyRxBytesPerSec?: number;
  totalProxyConnections: number;
  appChips: { name: string; count: number }[];
  availableModels: string[];
  timeline: UsageBucketPoint[];
  logs: UsageLogEntry[];
  totalLogsCount: number;
  page: number;
  pageSize: number;
  modelBreakdown: ModelUsageSummary[];
  appAndProxyBreakdown: AppUsageAndProxySummary[];
}

export interface ModelPricingRule {
  pattern: string;
  displayName: string;
  inputPerMillion: number;
  outputPerMillion: number;
  cacheReadPerMillion: number;
  cacheWritePerMillion: number;
  isCustom: boolean;
}

async function requestJson<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, {
    headers: { 'Content-Type': 'application/json' },
    ...init
  });
  if (!res.ok) {
    const txt = await res.text();
    try {
      const parsed = JSON.parse(txt) as { message?: string };
      if (parsed?.message) throw new Error(parsed.message);
    } catch (inner) {
      if (inner instanceof Error && inner.message && inner.message !== txt && !inner.message.startsWith('Unexpected token')) {
        throw inner;
      }
    }
    throw new Error(txt || `HTTP ${res.status}`);
  }
  return res.json() as Promise<T>;
}

export const api = {
  getOverview: () => requestJson<OverviewResponse>('/api/overview'),
  addManualNode: (body: {
    protocol: ProxyProtocol;
    name: string;
    group: string;
    host: string;
    port: number;
    username?: string;
    password?: string;
    method?: string;
    sni?: string;
    allowInsecure?: boolean;
    extra?: Record<string, string>;
  }) => requestJson<ProxyNode>('/api/nodes/manual', { method: 'POST', body: JSON.stringify(body) }),
  fetchSubscription: (url: string, defaultGroup: string, proxyUrl?: string) =>
    requestJson<{ rawText: string; preview: BatchParseResult; detectedFormat: string }>('/api/nodes/fetch-subscription', {
      method: 'POST',
      body: JSON.stringify({ url, defaultGroup, proxyUrl: proxyUrl?.trim() || undefined })
    }),
  previewBatch: (rawText: string, defaultProtocol: ProxyProtocol, defaultGroup: string) =>
    requestJson<BatchParseResult>('/api/nodes/batch-preview', {
      method: 'POST',
      body: JSON.stringify({ rawText, defaultProtocol, defaultGroup })
    }),
  importBatch: (rawText: string, defaultProtocol: ProxyProtocol, defaultGroup: string) =>
    requestJson<{ addedCount: number; duplicateCount: number; errorCount: number }>('/api/nodes/batch-import', {
      method: 'POST',
      body: JSON.stringify({ rawText, defaultProtocol, defaultGroup })
    }),
  deleteNode: (id: string) => requestJson<{ removed: number }>(`/api/nodes/${id}`, { method: 'DELETE' }),
  verifyNode: (id: string) => requestJson<ProxyNode>(`/api/nodes/${id}/verify`, { method: 'POST' }),
  verifyAllNodes: () => requestJson<ProxyNode[]>('/api/nodes/verify-all', { method: 'POST' }),
  updateSeat: (id: string, body: { enabled?: boolean; boundNodeId?: string; relayNodeId?: string; killSwitchEnabled?: boolean }) =>
    requestJson<AppSeat>(`/api/seats/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  toggleAllSeats: (enabled: boolean) =>
    requestJson<AppSeat[]>('/api/seats/toggle-all', { method: 'POST', body: JSON.stringify({ enabled }) }),
  setSeatTakeover: (id: string, enabled: boolean) =>
    requestJson<{ ok: boolean; message: string; status: SeatTakeoverStatus; seatTakeovers: SeatTakeoverStatus[] }>(
      `/api/seats/${id}/takeover`,
      { method: 'POST', body: JSON.stringify({ enabled }) }
    ),
  launchSeatApp: (id: string) =>
    requestJson<{ ok: boolean; message: string; seatTakeovers: SeatTakeoverStatus[] }>(
      `/api/seats/${id}/launch`,
      { method: 'POST' }
    ),
  verifySeatTunnel: (id: string) =>
    requestJson<TunnelVerificationResult>(`/api/seats/${id}/verify-tunnel`, { method: 'POST' }),
  getIsolation: () =>
    requestJson<{
      settings: IsolationSettings;
      adapters: NetworkAdapterInfo[];
      lockedAdapter?: NetworkAdapterInfo | null;
      sidecarEngine?: SidecarEngineStatus;
    }>('/api/isolation'),
  rescanNics: () =>
    requestJson<{ adapters: NetworkAdapterInfo[]; lockedAdapter?: NetworkAdapterInfo | null }>('/api/isolation/rescan', { method: 'POST' }),
  updateIsolation: (body: Partial<IsolationSettings>) =>
    requestJson<{
      settings: IsolationSettings;
      adapters: NetworkAdapterInfo[];
      lockedAdapter?: NetworkAdapterInfo | null;
      sidecarEngine?: SidecarEngineStatus;
    }>('/api/isolation', {
      method: 'PUT',
      body: JSON.stringify(body)
    }),
  getSingBoxStatus: () => requestJson<SidecarEngineStatus>('/api/engine/singbox'),
  installSingBox: (proxyUrl?: string) =>
    requestJson<{ ok: boolean; message: string; status: SidecarEngineStatus }>('/api/engine/singbox/install', {
      method: 'POST',
      body: JSON.stringify({ proxyUrl: proxyUrl || null })
    }),
  setSingBoxPath: (binaryPath: string) =>
    requestJson<{ ok: boolean; message: string; status: SidecarEngineStatus }>('/api/engine/singbox/path', {
      method: 'PUT',
      body: JSON.stringify({ binaryPath })
    }),
  stopSingBox: () =>
    requestJson<{ ok: boolean; message: string; status: SidecarEngineStatus }>('/api/engine/singbox/stop', {
      method: 'POST'
    }),
  getStatsDashboard: (params: { range: string; appFilter: string; modelFilter: string; page: number; pageSize: number }) => {
    const qs = new URLSearchParams({
      range: params.range,
      appFilter: params.appFilter,
      modelFilter: params.modelFilter,
      page: String(params.page),
      pageSize: String(params.pageSize)
    });
    return requestJson<UsageDashboardResponse>(`/api/stats/dashboard?${qs.toString()}`);
  },
  syncStatsNow: () => requestJson<{ syncedAt: string; scannedFiles: number; parsedEntries: number; elapsedMs: number }>('/api/stats/sync', { method: 'POST' }),
  getPricings: () => requestJson<ModelPricingRule[]>('/api/stats/pricing'),
  upsertPricing: (body: Omit<ModelPricingRule, 'isCustom'>) =>
    requestJson<ModelPricingRule[]>('/api/stats/pricing', { method: 'PUT', body: JSON.stringify(body) }),
  openRepo: () =>
    requestJson<{ opened: boolean; url: string; error?: string }>('/api/system/open-repo', { method: 'POST' })
};

export function formatBytes(bytes: number): string {
  if (!bytes || bytes <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB'];
  let v = bytes;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v >= 10 ? v.toFixed(1) : v.toFixed(2)} ${units[i]}`;
}

export function formatRate(bytesPerSec: number): string {
  if (!bytesPerSec || bytesPerSec <= 0) return '0 B/s';
  return `${formatBytes(bytesPerSec)}/s`;
}

export function formatCompactTokens(tokens: number): string {
  if (tokens >= 1_000_000_000) return `${(tokens / 1_000_000_000).toFixed(2)}B`;
  if (tokens >= 1_000_000) return `${(tokens / 1_000_000).toFixed(1)}M`;
  if (tokens >= 10_000) return `${Math.round(tokens / 1000)}K`;
  return tokens.toLocaleString('en-US');
}
