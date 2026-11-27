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
  hasCredential?: boolean;
  method?: string | null;
  sni?: string | null;
  allowInsecure?: boolean;
  insecureApproved?: boolean;
  sourceSubscriptionId?: string | null;
  extra?: Record<string, string> | null;
  requiresSidecarEngine?: boolean;
  engineKind?: 'native' | 'sing-box';
  enabled: boolean;
  status: 'online' | 'offline' | 'unverified';
  latencyMs?: number | null;
  exitIp: string;
  ispSummary: string;
  aiReachability: string[];
  lastCheckedAt?: string | null;
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
  fallbackNodeId?: string | null;
  enabled: boolean;
  killSwitchEnabled: boolean;
  allowSameGroupFailover: boolean;
  captureMode: string;
  launchCommandWin?: string | null;
  launchCommandMac?: string | null;
  expectedCountryCode?: string | null;
  customNoProxy?: string | null;
  customExecutablePath?: string | null;
  boundNodeSnapshotHash?: string | null;
  hasNodeDrift?: boolean;
  sortOrder?: number;
  isHidden?: boolean;
  isPinned?: boolean;
}

export interface ManagedAppInstance {
  id: string;
  baseSeatId: string;
  name: string;
  profileDir: string;
  localPort: number;
  boundNodeId: string;
  killSwitchEnabled: boolean;
  enabled: boolean;
  extraArgs?: string | null;
  workingDirectory?: string | null;
  lastLaunchedAt?: string | null;
}

export interface HubUiPreferences {
  closeAction: 'minimize_to_tray' | 'exit';
  trayLeftClickAction: 'toggle_flyout' | 'show_main' | 'toggle_mini';
  miniCardEnabled: boolean;
  miniCardX?: number | null;
  miniCardY?: number | null;
  webDavUrl?: string | null;
  webDavUsername?: string | null;
  webDavPassword?: string | null;
  webDavSyncPassphrase?: string | null;
  lastWebDavSyncAt?: string | null;
  hasWebDavPassword?: boolean;
  hasSyncPassphrase?: boolean;
}

export interface SubscriptionSourceDto {
  id: string;
  name: string;
  group: string;
  hostHint: string;
  maskedUrl: string;
  proxyUrl?: string | null;
  autoRefreshHours: number;
  lastSyncedAt?: string | null;
  nodeCount: number;
  driftCount: number;
  lastError?: string | null;
}

export interface CountryCheckRecord {
  timestamp: string;
  seatId: string;
  expectedCountry?: string | null;
  observedCountry?: string | null;
  exitIp: string;
  latencyMs?: number | null;
  passed: boolean;
  reason: string;
}

export interface RecentConnectionEntry {
  timestamp: string;
  seatId: string;
  targetHost: string;
  targetPort: number;
  sniRecovered: boolean;
  routeSummary: string;
  bytesTx: number;
  bytesRx: number;
  durationMs: number;
  outcome: string;
}

export interface HubLogEntry {
  timestamp: string;
  level: string;
  category: string;
  message: string;
}

export interface UpdateCheckResult {
  ok: boolean;
  currentVersion: string;
  latestVersion: string;
  hasUpdate: boolean;
  publishedAt?: string | null;
  htmlUrl: string;
  releaseNotes?: string | null;
  error?: string | null;
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
  autoHealthCheckEnabled?: boolean;
  autoHealthCheckMinutes?: number;
}

export interface SidecarEngineStatus {
  installed: boolean;
  running: boolean;
  binaryPath?: string | null;
  version?: string | null;
  binarySha256?: string | null;
  activeL2NodeCount: number;
  boundInterface?: string | null;
  lastError?: string | null;
}

export interface SeatTrafficSnapshot {
  seatId: string;
  appName: string;
  localPort: number;
  boundNodeName: string;
  effectiveNodeName?: string | null;
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
  instances?: ManagedAppInstance[];
  preferences?: HubUiPreferences;
  nodes: ProxyNode[];
  subscriptions?: SubscriptionSourceDto[];
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
  cacheSavedUsd?: number;
  isPriced: boolean;
  workspace?: string | null;
  conversationId?: string | null;
}

export interface WorkspaceUsageSummary {
  workspace: string;
  requests: number;
  totalTokens: number;
  costUsd: number;
  topApp: string;
  topModel: string;
}

export interface ConversationAuditReport {
  conversationId: string;
  appName: string;
  workspace?: string | null;
  filePath?: string | null;
  totalTurns: number;
  peakContextTokens: number;
  totalCostUsd: number;
  totalCacheSavedUsd: number;
  contextGrowthRatio: number;
  optimizationHint?: string | null;
  toolBreakdown: { toolName: string; calls: number }[];
  turns: {
    turnIndex: number;
    timestamp: string;
    model: string;
    inputTokens: number;
    outputTokens: number;
    cacheReadTokens: number;
    cumulativeContextTokens: number;
    durationSeconds: number;
    costUsd?: number | null;
    toolsCalled: string[];
  }[];
}

export interface DailyHeatmapCell {
  date: string;
  requests: number;
  totalTokens: number;
  costUsd: number;
  level: number;
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
  totalCacheSavedUsd?: number;
  totalRequests: number;
  totalTokens: number;
  effectiveInputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  cacheHitRate: number;
  pricedRequests: number;
  unpricedRequests: number;
  activeDaysLastYear?: number;
  activityHeatmap?: DailyHeatmapCell[];
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
  workspaceBreakdown?: WorkspaceUsageSummary[];
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

export interface StorageStatsDto {
  hubDir: string;
  totalBytes: number;
  fileCount: number;
  takeoverBackupCount: number;
  tempFileCount: number;
  launcherFileCount: number;
  profileCount: number;
}

export interface StorageCleanupResult {
  removedFiles: number;
  freedBytes: number;
  message: string;
  stats: StorageStatsDto;
}

async function requestJson<T>(url: string, init?: RequestInit, timeoutMs = 8000): Promise<T> {
  const ctrl = new AbortController();
  const extSignal = init?.signal;
  let timedOut = false;

  const onExternalAbort = () => ctrl.abort();
  if (extSignal) {
    if (extSignal.aborted) {
      ctrl.abort();
    } else {
      extSignal.addEventListener('abort', onExternalAbort, { once: true });
    }
  }

  const timer = window.setTimeout(() => {
    timedOut = true;
    ctrl.abort();
  }, Math.max(1500, timeoutMs));

  try {
    const res = await fetch(url, {
      ...init,
      signal: ctrl.signal,
      headers: { 'Content-Type': 'application/json', ...(init?.headers ?? {}) }
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
  } catch (err) {
    if (timedOut && !extSignal?.aborted) {
      throw new Error(`请求超时（${Math.round(timeoutMs / 1000)}s），已自动停止`);
    }
    throw err;
  } finally {
    window.clearTimeout(timer);
    extSignal?.removeEventListener('abort', onExternalAbort);
  }
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
    requestJson<{ rawText: string; preview: BatchParseResult; detectedFormat: string; suggestedTitle?: string }>('/api/nodes/fetch-subscription', {
      method: 'POST',
      body: JSON.stringify({ url, defaultGroup, proxyUrl: proxyUrl?.trim() || undefined })
    }, 22000),
  getSubscriptions: () => requestJson<SubscriptionSourceDto[]>('/api/subscriptions'),
  upsertSubscription: (body: { id?: string; name?: string; url: string; group?: string; proxyUrl?: string; autoRefreshHours?: number }) =>
    requestJson<{ subscription: SubscriptionSourceDto; diff: { addedCount: number; updatedCount: number; removedCount: number; driftCount: number; totalInSubscription: number } }>(
      '/api/subscriptions',
      { method: 'POST', body: JSON.stringify(body) },
      22000
    ),
  refreshSubscription: (id: string) =>
    requestJson<{ subscription: SubscriptionSourceDto; diff: { addedCount: number; updatedCount: number; removedCount: number; driftCount: number; totalInSubscription: number } }>(
      `/api/subscriptions/${id}/refresh`,
      { method: 'POST' },
      22000
    ),
  deleteSubscription: (id: string, removeNodes = false) =>
    requestJson<{ removed: number }>(`/api/subscriptions/${id}?removeNodes=${removeNodes}`, { method: 'DELETE' }),
  approveNodeInsecure: (id: string) =>
    requestJson<ProxyNode>(`/api/nodes/${id}/approve-insecure`, { method: 'POST' }),
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
  verifyNode: (id: string, signal?: AbortSignal) => requestJson<ProxyNode>(`/api/nodes/${id}/verify`, { method: 'POST', signal }, 9500),
  verifyAllNodes: (signal?: AbortSignal) => requestJson<ProxyNode[]>('/api/nodes/verify-all', { method: 'POST', signal }, 25000),
  updateSeat: (id: string, body: {
    enabled?: boolean;
    boundNodeId?: string;
    relayNodeId?: string;
    fallbackNodeId?: string;
    killSwitchEnabled?: boolean;
    expectedCountryCode?: string;
    customNoProxy?: string;
    customExecutablePath?: string;
    acknowledgeDrift?: boolean;
    isHidden?: boolean;
    isPinned?: boolean;
    sortOrder?: number;
  }) =>
    requestJson<AppSeat>(`/api/seats/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  reorderSeats: (orderedSeatIds: string[]) =>
    requestJson<AppSeat[]>('/api/seats/reorder', { method: 'POST', body: JSON.stringify({ orderedSeatIds }) }),
  toggleAllSeats: (enabled: boolean) =>
    requestJson<AppSeat[]>('/api/seats/toggle-all', { method: 'POST', body: JSON.stringify({ enabled }) }),
  setSeatTakeover: (id: string, enabled: boolean) =>
    requestJson<{ ok: boolean; message: string; status: SeatTakeoverStatus; seatTakeovers: SeatTakeoverStatus[] }>(
      `/api/seats/${id}/takeover`,
      { method: 'POST', body: JSON.stringify({ enabled }) }
    ),
  launchSeatApp: (id: string) =>
    requestJson<{ ok: boolean; blockedByCountryGuard?: boolean; countryCheck?: CountryCheckRecord; message: string; seatTakeovers: SeatTakeoverStatus[] }>(
      `/api/seats/${id}/launch`,
      { method: 'POST' },
      12000
    ),
  restartSeatApp: (id: string) =>
    requestJson<{ ok: boolean; message: string; seatTakeovers: SeatTakeoverStatus[] }>(
      `/api/seats/${id}/restart`,
      { method: 'POST' },
      15000
    ),
  getInstances: () => requestJson<ManagedAppInstance[]>('/api/instances'),
  createInstance: (body: { baseSeatId: string; name: string; profileDir?: string; boundNodeId?: string; extraArgs?: string; workingDirectory?: string }) =>
    requestJson<ManagedAppInstance>('/api/instances', { method: 'POST', body: JSON.stringify(body) }),
  updateInstance: (id: string, body: { enabled?: boolean; boundNodeId?: string; killSwitchEnabled?: boolean; name?: string; extraArgs?: string; workingDirectory?: string }) =>
    requestJson<ManagedAppInstance>(`/api/instances/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  deleteInstance: (id: string) => requestJson<{ ok: boolean; message: string }>(`/api/instances/${id}`, { method: 'DELETE' }),
  launchInstance: (id: string) => requestJson<{ ok: boolean; message: string; instance?: ManagedAppInstance }>(`/api/instances/${id}/launch`, { method: 'POST' }),
  getPreferences: () => requestJson<HubUiPreferences>('/api/preferences'),
  updatePreferences: (body: Partial<HubUiPreferences>) =>
    requestJson<HubUiPreferences>('/api/preferences', { method: 'PUT', body: JSON.stringify(body) }),
  importSingBox: (localPath: string, expectedSha256?: string) =>
    requestJson<{ ok: boolean; message: string; status: SidecarEngineStatus }>('/api/engine/singbox/import', {
      method: 'POST',
      body: JSON.stringify({ localPath, expectedSha256: expectedSha256 || null })
    }),
  testWebDav: (body: { webDavUrl?: string; webDavUsername?: string; webDavPassword?: string }) =>
    requestJson<{ ok: boolean; message: string }>('/api/webdav/test', { method: 'POST', body: JSON.stringify(body) }, 22000),
  pushWebDav: (body: { webDavUrl?: string; webDavUsername?: string; webDavPassword?: string; passphrase?: string }) =>
    requestJson<{ ok: boolean; message: string; lastWebDavSyncAt?: string | null }>('/api/webdav/push', { method: 'POST', body: JSON.stringify(body) }, 22000),
  pullWebDav: (body: { webDavUrl?: string; webDavUsername?: string; webDavPassword?: string; passphrase?: string }) =>
    requestJson<{ ok: boolean; message: string; lastWebDavSyncAt?: string | null }>('/api/webdav/pull', { method: 'POST', body: JSON.stringify(body) }, 22000),
  getConversationAudit: (conversationId: string) =>
    requestJson<ConversationAuditReport>(`/api/stats/conversations/${encodeURIComponent(conversationId)}/audit`),
  checkSeatCountry: (id: string) =>
    requestJson<{ check: CountryCheckRecord; history: CountryCheckRecord[] }>(
      `/api/seats/${id}/check-country`,
      { method: 'POST' },
      9000
    ),
  getSeatConnections: (id: string, limit = 20) =>
    requestJson<{ connections: RecentConnectionEntry[]; countryHistory: CountryCheckRecord[] }>(
      `/api/seats/${id}/connections?limit=${limit}`
    ),
  verifySeatTunnel: (id: string) =>
    requestJson<TunnelVerificationResult>(`/api/seats/${id}/verify-tunnel`, { method: 'POST' }, 10500),
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
    }, 185000),
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
  getDiagnosticLogs: (params?: { level?: string; keyword?: string; limit?: number }) => {
    const qs = new URLSearchParams();
    if (params?.level) qs.set('level', params.level);
    if (params?.keyword) qs.set('keyword', params.keyword);
    if (params?.limit) qs.set('limit', String(params.limit));
    return requestJson<HubLogEntry[]>(`/api/diagnostics/logs?${qs.toString()}`);
  },
  clearDiagnosticLogs: () => requestJson<{ cleared: boolean }>('/api/diagnostics/logs', { method: 'DELETE' }),
  checkUpdate: (force = false, proxyUrl?: string) => {
    const qs = new URLSearchParams();
    if (force) qs.set('force', 'true');
    if (proxyUrl?.trim()) qs.set('proxyUrl', proxyUrl.trim());
    return requestJson<UpdateCheckResult>(`/api/system/update-check?${qs.toString()}`);
  },
  openRepo: (targetUrl?: string) =>
    requestJson<{ opened: boolean; url: string; error?: string }>(
      targetUrl ? `/api/system/open-repo?targetUrl=${encodeURIComponent(targetUrl)}` : '/api/system/open-repo',
      { method: 'POST' }
    ),
  getMaintenanceStats: () => requestJson<StorageStatsDto>('/api/maintenance/stats'),
  cleanupMaintenance: () => requestJson<StorageCleanupResult>('/api/maintenance/cleanup', { method: 'POST' })
};

export function connectOverviewStream(
  onOverview: (data: OverviewResponse) => void,
  onStatusChange?: (connected: boolean) => void
): () => void {
  if (typeof window === 'undefined' || typeof WebSocket === 'undefined') {
    return () => {};
  }

  let closedByCaller = false;
  let ws: WebSocket | null = null;
  let retryTimer: number | null = null;

  const connect = () => {
    if (closedByCaller) return;
    try {
      const proto = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
      ws = new WebSocket(`${proto}//${window.location.host}/ws/events`);
      ws.onopen = () => onStatusChange?.(true);
      ws.onmessage = ev => {
        try {
          const parsed = JSON.parse(String(ev.data)) as { type?: string; data?: OverviewResponse };
          if (parsed?.type === 'overview' && parsed.data) {
            onOverview(parsed.data);
          }
        } catch { }
      };
      ws.onerror = () => {
        try { ws?.close(); } catch { }
      };
      ws.onclose = () => {
        onStatusChange?.(false);
        if (!closedByCaller) {
          retryTimer = window.setTimeout(connect, 2200);
        }
      };
    } catch {
      onStatusChange?.(false);
      if (!closedByCaller) {
        retryTimer = window.setTimeout(connect, 3000);
      }
    }
  };

  connect();

  return () => {
    closedByCaller = true;
    if (retryTimer !== null) window.clearTimeout(retryTimer);
    try { ws?.close(); } catch { }
  };
}

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
