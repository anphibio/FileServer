import React, { useEffect, useMemo, useState } from "react";
import { createRoot } from "react-dom/client";
import {
  Activity,
  AlertTriangle,
  BarChart3,
  Bell,
  CheckCircle2,
  ClipboardList,
  Clock3,
  Database,
  Download,
  FileClock,
  Files,
  FolderTree,
  HardDrive,
  KeyRound,
  LogOut,
  LockKeyhole,
  Plus,
  RefreshCcw,
  Search,
  Server,
  ShieldCheck,
  ShieldAlert,
  ScanLine,
  Trash2
} from "lucide-react";
import "./styles.css";
import {
  buildReportQueryParams,
  createDefaultReportFilters,
  createFiltersForScenario,
  getReportScenario,
  reportScenarios,
  summarizeReportFilters,
  type ReportFilters,
  type ReportGrouping,
  type ReportScenario,
  type ReportScenarioId
} from "./report-definitions";

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL ?? "http://localhost:8080";
const apiKey = import.meta.env.VITE_API_KEY ?? "";
const actorName = import.meta.env.VITE_ACTOR_NAME ?? "";

type HealthResponse = {
  service: string;
  status: string;
  timestampUtc: string;
  storageProvider: string;
  storedEvents: number;
  lastEventUtc: string | null;
  timeline: TimelineMaterializationMetrics;
};

type TimelineMaterializationMetrics = {
  status: string;
  provider: string;
  totalJobs: number | null;
  pendingJobs: number | null;
  processingJobs: number | null;
  retryingJobs: number | null;
  maxAttemptCount: number | null;
  oldestJobCreatedUtc: string | null;
  oldestJobAgeSeconds: number | null;
  lastErrorUtc: string | null;
  lastError: string | null;
  queryDurationMs: number;
  error: string | null;
};

type AuthStatusResponse = {
  enabled: boolean;
  configurationStatus: string;
  loginMode: string;
  updatedUtc: string;
};

type AuthConfig = {
  enabled: boolean;
  host: string;
  port: number;
  security: string;
  timeoutSeconds: number;
  validateTlsCertificate: boolean;
  baseDn: string;
  bindFormat: string;
  domainSuffix: string;
  netbiosDomain: string;
  adminGroupDn: string;
  operatorGroupDn: string;
  readerGroupDn: string;
  configurationStatus: string;
  loginMode: string;
  updatedUtc: string;
};

type RetentionConfig = {
  enabled: boolean;
  eventsDays: number;
  timelineDays: number;
  alertsDays: number;
  intervalHours: number;
  purgeBatchSize: number;
  updatedUtc: string;
};

type InventoryScanConfig = {
  enabled: boolean;
  intervalHours: number;
  batchSize: number;
  maxItemsPerScan: number;
  includeLastAccessTime: boolean;
  windowStartLocal: string;
  windowEndLocal: string;
  rootPath: string;
  server: string;
  share: string;
  updatedUtc: string;
  runRequestedUtc?: string | null;
};

type AuthenticatedUser = {
  username: string;
  displayName: string;
  distinguishedName: string;
  role: "admin" | "operator" | "reader" | string;
  groups: string[];
};

type LoginResponse = {
  token: string;
  user: AuthenticatedUser;
  expiresUtc: string;
};

type FileAuditEvent = {
  id: string;
  timestampUtc: string;
  server: string;
  share: string;
  path: string;
  previousPath?: string | null;
  objectType: string;
  action: string;
  user: string;
  sid?: string | null;
  sourceHost?: string | null;
  sourceIp?: string | null;
  processName?: string | null;
  fileSizeBytes?: number | null;
  extension?: string | null;
  result: string;
  severity: string;
  source: string;
};

type DisplayEvent = FileAuditEvent & {
  displayAction?: string;
  displayTarget?: string;
};

type TimelinePageResponse = {
  items: DisplayEvent[];
  page: number;
  pageSize: number;
  totalItems: number;
  totalPages: number;
  windowRawEvents: number;
};

type PaginationState = {
  page: number;
  totalPages: number;
  pageItems: number;
  totalItems: number;
  onPrevious: () => void;
  onNext: () => void;
};

type FileServerAlert = {
  id: string;
  rule: string;
  severity: string;
  status: string;
  title: string;
  description: string;
  server: string;
  user: string;
  eventCount: number;
  firstEventUtc: string;
  lastEventUtc: string;
  createdUtc: string;
  acknowledgedUtc?: string | null;
  samplePaths: string[];
};

type AlertRuleConfig = {
  rule: string;
  title: string;
  description: string;
  enabled: boolean;
  severity: string;
  threshold?: number | null;
  secondaryThreshold?: number | null;
  secondarySeverity?: string | null;
  serverFilter?: string | null;
  shareFilter?: string | null;
  pathFilter?: string | null;
  activeFromHour?: number | null;
  activeToHour?: number | null;
  activeDays?: string | null;
  excludedUsers?: string | null;
  excludedHosts?: string | null;
  excludedProcesses?: string | null;
  timeZoneId?: string | null;
  updatedUtc: string;
};

type AlertRuleSimulationResponse = {
  rule: string;
  title: string;
  fromUtc: string;
  toUtc: string;
  evaluatedEvents: number;
  matchingEvents: number;
  alertCount: number;
  alerts: FileServerAlert[];
};

type AgentHealth = {
  agentId: string;
  server: string;
  status: string;
  lastHeartbeatUtc: string | null;
  version?: string | null;
  lastRecordId: number;
  lastUsnByVolume: Record<string, number>;
  message?: string | null;
  pendingQueueEvents: number;
  lastSuccessfulSendUtc?: string | null;
  lastCollectedEventUtc?: string | null;
  lastCycle?: AgentCycleMetrics | null;
  backlogWarningThreshold: number;
  isStale: boolean;
  staleAfterMinutes: number;
  operationalStatus: string;
  operationalMessage?: string | null;
  lastHeartbeatAgeSeconds?: number | null;
  lastSuccessfulSendAgeSeconds?: number | null;
  lastCollectedEventAgeSeconds?: number | null;
  hasCycleError: boolean;
};

type AgentCycleMetrics = {
  startedUtc?: string | null;
  finishedUtc?: string | null;
  durationMs: number;
  securityEventsRead: number;
  usnEventsRead: number;
  correlatedEvents: number;
  sentEvents: number;
  queuedEvents: number;
  error?: string | null;
};

type MonitoredPath = {
  id: string;
  server: string;
  share: string;
  path: string;
  status: string;
  priority: string;
  owner?: string | null;
  notes?: string | null;
  createdUtc: string;
  updatedUtc: string;
};

type MonitoredPathForm = {
  server: string;
  share: string;
  path: string;
  status: string;
  priority: string;
  owner: string;
  notes: string;
};

type ActivitySummary = {
  fromUtc: string;
  toUtc: string;
  totalEvents: number;
  byAction: ActivitySummaryItem[];
  byShare: ActivitySummaryItem[];
  byUser: ActivitySummaryItem[];
};

type BaselineAnomalyResponse = {
  fromUtc: string;
  toUtc: string;
  baselineWindows: number;
  byAction: BaselineAnomalyItem[];
  byShare: BaselineAnomalyItem[];
  byUser: BaselineAnomalyItem[];
};

type ActivitySummaryItem = {
  name: string;
  eventCount: number;
};

type BaselineAnomalyItem = {
  name: string;
  currentCount: number;
  baselineAverage: number;
  deltaPercent: number;
};

type ActivitySummaryFilters = {
  periodHours: string;
  server: string;
  share: string;
  user: string;
  action: string;
};

type InvestigationFilters = {
  server: string;
  user: string;
  path: string;
  action: string;
  periodHours: string;
  periodMode: "preset" | "custom";
  fromDate: string;
  toDate: string;
};

type AdminAuditEntry = {
  id: string;
  timestampUtc: string;
  action: string;
  entityType: string;
  entityId: string;
  actor: string;
  sourceIp?: string | null;
  detailsJson?: string | null;
};

type DatabaseCapacityResponse = {
  generatedUtc: string;
  provider: string;
  status: string;
  totalRows: number;
  totalReservedMb: number;
  timelineRows: number;
  timelineFromUtc?: string | null;
  timelineToUtc?: string | null;
  tables: DatabaseCapacityTable[];
  windows: DatabaseCapacityWindow[];
  dailyCounts: DatabaseCapacityDailyCount[];
  message?: string | null;
};

type DatabaseCapacityTable = {
  name: string;
  physicalName: string;
  rowCount: number;
  reservedMb: number;
  usedMb: number;
};

type DatabaseCapacityWindow = {
  name: string;
  rowCount: number;
  fromUtc?: string | null;
  toUtc?: string | null;
};

type DatabaseCapacityDailyCount = {
  date: string;
  series: string;
  count: number;
};

type InventorySummary = {
  snapshotId?: string | null;
  server?: string | null;
  share?: string | null;
  rootPath?: string | null;
  startedUtc?: string | null;
  finishedUtc?: string | null;
  status: string;
  fileCount: number;
  folderCount: number;
  totalBytes: number;
  errorCount: number;
  governance: InventoryGovernanceMetrics;
  topFolders: InventoryTopFolder[];
  topExtensions: InventoryTopExtension[];
  contentCategories: InventoryContentCategory[];
  topLargeFiles: InventoryFileCandidate[];
  topInactiveFiles: InventoryFileCandidate[];
  topExecutableFiles: InventoryFileCandidate[];
  ageBuckets: InventoryAgeBucket[];
  observedActivity: InventoryObservedActivity;
  growth: InventoryGrowthSummary;
  comparison: InventoryCycleComparison;
  insight: InventoryManagerialInsight;
  executiveOverview: InventoryExecutiveOverview;
  recommendations: InventoryRecommendation[];
};

type InventorySnapshot = {
  id: string;
  server: string;
  share: string;
  rootPath: string;
  startedUtc: string;
  finishedUtc?: string | null;
  status: string;
  fileCount: number;
  folderCount: number;
  totalBytes: number;
  errorCount: number;
  error?: string | null;
};

type InventoryItem = {
  id: string;
  scannedAtUtc: string;
  server: string;
  share: string;
  rootPath: string;
  path: string;
  relativePath: string;
  name: string;
  itemType: string;
  extension?: string | null;
  sizeBytes: number;
  depth: number;
  createdUtc?: string | null;
  modifiedUtc?: string | null;
  accessedUtc?: string | null;
  status: string;
  error?: string | null;
};

type InventoryGovernanceMetrics = {
  inactive180DaysFileCount: number;
  inactive180DaysBytes: number;
  inactive365DaysFileCount: number;
  inactive365DaysBytes: number;
  neverAccessedFileCount: number;
  neverAccessedBytes: number;
  largeFileCount: number;
  largeFileBytes: number;
  executableFileCount: number;
  executableFileBytes: number;
};

type InventoryTopFolder = {
  path: string;
  fileCount: number;
  folderCount: number;
  totalBytes: number;
};

type InventoryTopExtension = {
  extension: string;
  fileCount: number;
  totalBytes: number;
};

type InventoryContentCategory = {
  category: string;
  fileCount: number;
  totalBytes: number;
};

type InventoryGrowthSummary = {
  fileCountDelta: number;
  folderCountDelta: number;
  totalBytesDelta: number;
  topGrowingFolders: InventoryFolderGrowth[];
};

type InventoryCycleComparison = {
  previousSnapshotId?: string | null;
  previousStartedUtc?: string | null;
  previousFileCount: number;
  previousFolderCount: number;
  previousTotalBytes: number;
  previousErrorCount: number;
  totalBytesGrowthPercent: number;
  errorCountDelta: number;
};

type InventoryManagerialInsight = {
  score: number;
  tone: "green" | "amber" | "danger";
  trend: "improved" | "stable" | "worsened";
  positives: string[];
  stables: string[];
  attentions: string[];
};

type InventoryExecutiveOverview = {
  headlines: string[];
  storageHotspots: InventoryExecutiveArea[];
  activityHotspots: InventoryExecutiveArea[];
  userHotspots: InventoryExecutiveActor[];
  priorities: InventoryExecutivePriority[];
};

type InventoryExecutiveArea = {
  path: string;
  label: string;
  primaryValue: number;
  primaryText: string;
  secondaryText: string;
  tone: string;
};

type InventoryExecutiveActor = {
  user: string;
  eventCount: number;
  primaryText: string;
  secondaryText: string;
  tone: string;
};

type InventoryExecutivePriority = {
  title: string;
  detail: string;
  severity: string;
  tone: string;
};

type InventoryFolderGrowth = {
  path: string;
  fileCountDelta: number;
  folderCountDelta: number;
  totalBytesDelta: number;
};

type InventoryFileCandidate = {
  path: string;
  name: string;
  extension?: string | null;
  sizeBytes: number;
  modifiedUtc?: string | null;
  accessedUtc?: string | null;
  ageDays?: number | null;
};

type InventoryAgeBucket = {
  label: string;
  fileCount: number;
  totalBytes: number;
};

type InventoryRecommendation = {
  title: string;
  detail: string;
  severity: string;
};

type InventoryObservedActivity = {
  totalEvents: number;
  topFolders: InventoryTopActivityFolder[];
  topUsers: InventoryTopActivityUser[];
};

type InventoryTopActivityFolder = {
  path: string;
  eventCount: number;
  lastActivityUtc: string;
  topAction: string;
};

type InventoryTopActivityUser = {
  user: string;
  eventCount: number;
  lastActivityUtc: string;
  topAction: string;
};

type Notice = {
  tone: "success" | "warning" | "danger";
  message: string;
};

type GeneratedReport = {
  title: string;
  generatedAt: string;
  filtersSummary: string;
  events: DisplayEvent[];
  executiveSummary: string;
  highlights: string[];
  risks: string[];
  nextSteps: string[];
  topActions: Array<{ label: string; count: number }>;
  topUsers: Array<{ label: string; count: number }>;
  topPaths: Array<{ label: string; count: number }>;
  sections: Array<{ title: string; items: string[] }>;
  inventorySummary?: InventorySummary | null;
};

type Tab = "dashboard" | "events" | "investigation" | "reports" | "inventory" | "alerts" | "agents" | "paths" | "capacity" | "audit" | "auth";
type AccessRole = "admin" | "operator" | "reader";

type AccessPolicy = {
  role: AccessRole;
  canManageAlerts: boolean;
  canManagePaths: boolean;
  canViewAgents: boolean;
  canViewCapacity: boolean;
  canViewAdminAudit: boolean;
  canManageAuth: boolean;
};

const authTokenStorageKey = "fileserver-monitor.auth.token";
const authUserStorageKey = "fileserver-monitor.auth.user";

const emptyMonitoredPathForm: MonitoredPathForm = {
  server: "FileServer",
  share: "",
  path: "",
  status: "planned",
  priority: "normal",
  owner: "",
  notes: ""
};

const defaultSummaryFilters: ActivitySummaryFilters = {
  periodHours: "24",
  server: "",
  share: "",
  user: "",
  action: ""
};

const defaultInvestigationFilters: InvestigationFilters = {
  server: "",
  user: "",
  path: "",
  action: "",
  periodHours: "24",
  periodMode: "preset",
  fromDate: "",
  toDate: ""
};

const EVENTS_PAGE_SIZE = 25;

function App() {
  const [activeTab, setActiveTab] = useState<Tab>("dashboard");
  const [authStatus, setAuthStatus] = useState<AuthStatusResponse | null>(null);
  const [authUser, setAuthUser] = useState<AuthenticatedUser | null>(() => readStoredAuthUser());
  const [health, setHealth] = useState<HealthResponse | null>(null);
  const [events, setEvents] = useState<DisplayEvent[]>([]);
  const [alerts, setAlerts] = useState<FileServerAlert[]>([]);
  const [alertRules, setAlertRules] = useState<AlertRuleConfig[]>([]);
  const [agents, setAgents] = useState<AgentHealth[]>([]);
  const [monitoredPaths, setMonitoredPaths] = useState<MonitoredPath[]>([]);
  const [activitySummary, setActivitySummary] = useState<ActivitySummary | null>(null);
  const [baselineAnomalies, setBaselineAnomalies] = useState<BaselineAnomalyResponse | null>(null);
  const [adminAudit, setAdminAudit] = useState<AdminAuditEntry[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [eventFilter, setEventFilter] = useState("");
  const [eventsPage, setEventsPage] = useState(1);
  const [eventsTotalItems, setEventsTotalItems] = useState(0);
  const [eventsTotalPages, setEventsTotalPages] = useState(1);
  const [summaryFilters, setSummaryFilters] = useState<ActivitySummaryFilters>(defaultSummaryFilters);
  const accessPolicy = useMemo(() => buildAccessPolicy(authStatus, authUser), [authStatus?.enabled, authUser?.role]);
  const visibleTabs = useMemo(() => getVisibleTabs(accessPolicy), [accessPolicy]);

  async function loadAuthStatus() {
    try {
      setAuthStatus(await fetchJson<AuthStatusResponse>("/api/auth/status"));
    } catch (statusError) {
      console.warn("Falha ao consultar status de autenticacao.", statusError);
      setAuthStatus({ enabled: false, configurationStatus: "unknown", loginMode: "api-key", updatedUtc: new Date().toISOString() });
    }
  }

  async function loadData(showLoading = true) {
    if (showLoading) {
      setLoading(true);
    }
    setError(null);

    try {
      const [healthResult, eventsResult, alertsResult, alertRulesResult, agentsResult, pathsResult, summaryResult, anomaliesResult, auditResult] = await Promise.all([
        fetchJson<HealthResponse>("/health"),
        fetchJson<TimelinePageResponse>(buildTimelinePageUrl(eventsPage, eventFilter), { timeoutMs: 12_000 }).catch((timelineError) => {
          console.warn("Falha ao carregar linha do tempo inicial.", timelineError);
          return {
            items: events,
            page: eventsPage,
            pageSize: EVENTS_PAGE_SIZE,
            totalItems: eventsTotalItems,
            totalPages: eventsTotalPages
          };
        }),
        fetchJson<FileServerAlert[]>("/api/alerts?take=100"),
        fetchJson<AlertRuleConfig[]>("/api/alert-rules"),
        fetchJson<AgentHealth[]>("/api/agents/health"),
        fetchJson<MonitoredPath[]>("/api/monitored-paths"),
        fetchJson<ActivitySummary>(buildActivitySummaryUrl(summaryFilters)),
        fetchJson<BaselineAnomalyResponse>(buildBaselineAnomaliesUrl(summaryFilters), { timeoutMs: 4_000 }).catch((anomalyError) => {
          console.warn("Falha ao carregar anomalias de baseline.", anomalyError);
          return null;
        }),
        fetchJson<AdminAuditEntry[]>("/api/admin-audit?take=100").catch(() => [])
      ]);

      setHealth(healthResult);
      setEvents(eventsResult.items);
      setEventsPage(eventsResult.page);
      setEventsTotalItems(eventsResult.totalItems);
      setEventsTotalPages(eventsResult.totalPages);
      setAlerts(alertsResult);
      setAlertRules(alertRulesResult);
      setAgents(agentsResult);
      setMonitoredPaths(pathsResult);
      setActivitySummary(summaryResult);
      setBaselineAnomalies(anomaliesResult);
      setAdminAudit(auditResult);
    } catch (loadError) {
      setError(loadError instanceof Error ? loadError.message : "Falha ao carregar dados.");
    } finally {
      if (showLoading) {
        setLoading(false);
      }
    }
  }

  useEffect(() => {
    loadAuthStatus();
  }, []);

  useEffect(() => {
    if (authStatus?.enabled && !authUser) {
      return undefined;
    }

    loadData();
    const timer = window.setInterval(() => loadData(false), 30000);
    return () => window.clearInterval(timer);
  }, [summaryFilters, eventsPage, eventFilter, authStatus?.enabled, authUser]);

  useEffect(() => {
    if (!visibleTabs.includes(activeTab)) {
      setActiveTab("dashboard");
    }
  }, [activeTab, visibleTabs]);

  useEffect(() => {
    if (!notice) {
      return undefined;
    }

    const timer = window.setTimeout(() => setNotice(null), 4500);
    return () => window.clearTimeout(timer);
  }, [notice]);

  const openAlerts = alerts.filter((alert) => alert.status === "open");
  const criticalAlerts = openAlerts.filter((alert) => alert.severity === "critical");
  const offlineAgents = agents.filter((agent) =>
    agent.isStale
    || agent.operationalStatus !== "ok"
    || agent.status !== "running"
    || agent.pendingQueueEvents >= agent.backlogWarningThreshold);

  const displayedEvents = events;
  const totalDisplayedEventCount = eventsTotalItems;
  const totalEventPages = eventsTotalPages;
  const safeEventsPage = Math.min(eventsPage, totalEventPages);
  const visibleDisplayedEvents = displayedEvents;
  const displayedEventCount = visibleDisplayedEvents.length;
  const shouldShowLogin = authStatus?.enabled && !authUser;

  function handleLogin(response: LoginResponse) {
    localStorage.setItem(authTokenStorageKey, response.token);
    localStorage.setItem(authUserStorageKey, JSON.stringify(response.user));
    setAuthUser(response.user);
    setNotice({ tone: "success", message: "Login corporativo realizado." });
  }

  function handleLogout() {
    localStorage.removeItem(authTokenStorageKey);
    localStorage.removeItem(authUserStorageKey);
    setAuthUser(null);
    setHealth(null);
    setEvents([]);
    setActiveTab("dashboard");
  }

  useEffect(() => {
    setEventsPage((current) => Math.min(current, totalEventPages));
  }, [totalEventPages]);

  useEffect(() => {
    setEventsPage(1);
  }, [eventFilter]);

  const eventPagination = useMemo<PaginationState>(
    () => ({
      page: safeEventsPage,
      totalPages: totalEventPages,
      pageItems: visibleDisplayedEvents.length,
      totalItems: totalDisplayedEventCount,
      onPrevious: () => setEventsPage((current) => Math.max(1, current - 1)),
      onNext: () => setEventsPage((current) => Math.min(totalEventPages, current + 1))
    }),
    [safeEventsPage, totalEventPages, visibleDisplayedEvents.length, totalDisplayedEventCount]
  );

  if (shouldShowLogin) {
    return <LoginPage onLogin={handleLogin} />;
  }

  return (
    <main className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <ShieldAlert size={28} />
          <div>
            <strong>File Server Monitor</strong>
            <span>Windows Server 2022</span>
          </div>
        </div>

        <nav className="nav-tabs" aria-label="Navegacao principal">
          <TabButton icon={<Activity size={18} />} active={activeTab === "dashboard"} onClick={() => setActiveTab("dashboard")} meta={health?.status === "ok" ? "ok" : "..."}>
            Dashboard
          </TabButton>
          <TabButton icon={<FileClock size={18} />} active={activeTab === "events"} onClick={() => setActiveTab("events")} meta={`${displayedEventCount.toLocaleString("pt-BR")}/${totalDisplayedEventCount.toLocaleString("pt-BR")}`}>
            Eventos
          </TabButton>
          <TabButton icon={<Search size={18} />} active={activeTab === "investigation"} onClick={() => setActiveTab("investigation")} meta="até 500">
            Investigação
          </TabButton>
          <TabButton icon={<BarChart3 size={18} />} active={activeTab === "reports"} onClick={() => setActiveTab("reports")} meta="guiados">
            Relatórios
          </TabButton>
          <TabButton icon={<FolderTree size={18} />} active={activeTab === "inventory"} onClick={() => setActiveTab("inventory")} meta="scan">
            Inventário
          </TabButton>
          {visibleTabs.includes("alerts") && (
            <TabButton icon={<Bell size={18} />} active={activeTab === "alerts"} onClick={() => setActiveTab("alerts")} meta={openAlerts.length.toLocaleString("pt-BR")}>
              Alertas
            </TabButton>
          )}
          {visibleTabs.includes("agents") && (
            <TabButton icon={<Server size={18} />} active={activeTab === "agents"} onClick={() => setActiveTab("agents")} meta={offlineAgents.length.toLocaleString("pt-BR")}>
              Agentes
            </TabButton>
          )}
          {visibleTabs.includes("paths") && (
            <TabButton icon={<FolderTree size={18} />} active={activeTab === "paths"} onClick={() => setActiveTab("paths")} meta={monitoredPaths.length.toLocaleString("pt-BR")}>
              Caminhos
            </TabButton>
          )}
          {visibleTabs.includes("capacity") && (
            <TabButton icon={<Database size={18} />} active={activeTab === "capacity"} onClick={() => setActiveTab("capacity")} meta="SQL">
              Banco
            </TabButton>
          )}
          {visibleTabs.includes("audit") && (
            <TabButton icon={<ClipboardList size={18} />} active={activeTab === "audit"} onClick={() => setActiveTab("audit")} meta={adminAudit.length.toLocaleString("pt-BR")}>
              Auditoria
            </TabButton>
          )}
          {visibleTabs.includes("auth") && (
            <TabButton icon={<LockKeyhole size={18} />} active={activeTab === "auth"} onClick={() => setActiveTab("auth")} meta={authStatus?.enabled ? "AD" : "off"}>
              Configuração
            </TabButton>
          )}
        </nav>
      </aside>

      <section className="content">
        <header className="topbar">
          <div>
            <h1>{titleForTab(activeTab)}</h1>
            <p>{health ? `${health.service} · ${health.storageProvider}` : "Aguardando API"}</p>
          </div>
          <div className="topbar-actions">
            <div className="topbar-pills" aria-label="Resumo rápido">
              <span className="pill">eventos {displayedEventCount.toLocaleString("pt-BR")}/{totalDisplayedEventCount.toLocaleString("pt-BR")}</span>
              <span className={`badge ${criticalAlerts.length > 0 ? "critical" : openAlerts.length > 0 ? "warning" : "low"}`}>
                alertas {openAlerts.length.toLocaleString("pt-BR")}
              </span>
              <span className={`status ${offlineAgents.length > 0 ? "degraded" : "running"}`}>
                agentes {offlineAgents.length > 0 ? `${offlineAgents.length} atenção` : "estáveis"}
              </span>
            </div>
            {authUser && (
              <button className="text-button" type="button" onClick={handleLogout} title="Sair">
                <LogOut size={16} />
                {authUser.displayName}
                <span className="user-role">{labelForRole(accessPolicy.role)}</span>
              </button>
            )}
            <button className="icon-button" onClick={() => loadData()} disabled={loading} title="Atualizar dados">
              <RefreshCcw size={18} />
            </button>
          </div>
        </header>

        {loading && <div className="sync-banner">Atualizando dados do painel...</div>}
        {error && <div className="error-banner">{error}</div>}
        {notice && <FeedbackBanner tone={notice.tone} message={notice.message} onClose={() => setNotice(null)} />}

        {activeTab === "dashboard" && (
          <Dashboard
            health={health}
            events={events}
            displayEvents={displayedEvents}
            openAlerts={openAlerts}
            criticalAlerts={criticalAlerts}
            offlineAgents={offlineAgents}
            activitySummary={activitySummary}
            baselineAnomalies={baselineAnomalies}
            summaryFilters={summaryFilters}
            onSummaryFiltersChange={setSummaryFilters}
            onNotify={setNotice}
          />
        )}

        {activeTab === "events" && (
          <EventsView events={visibleDisplayedEvents} filter={eventFilter} onFilterChange={setEventFilter} onNotify={setNotice} pagination={eventPagination} />
        )}

        {activeTab === "investigation" && <InvestigationView onNotify={setNotice} />}

        {activeTab === "reports" && <ReportsView onNotify={setNotice} />}

        {activeTab === "inventory" && <InventoryGovernanceView onNotify={setNotice} />}

        {activeTab === "alerts" && (
          <AlertsView
            alerts={alerts}
            rules={alertRules}
            canManageAlerts={accessPolicy.canManageAlerts}
            onChanged={loadData}
            onAcknowledge={loadData}
            onNotify={setNotice}
          />
        )}

        {activeTab === "agents" && <AgentsView agents={agents} timeline={health?.timeline ?? null} />}

        {activeTab === "paths" && <MonitoredPathsView paths={monitoredPaths} canManagePaths={accessPolicy.canManagePaths} onChanged={loadData} onNotify={setNotice} />}

        {activeTab === "capacity" && <DatabaseCapacityView onNotify={setNotice} />}

        {activeTab === "audit" && <AdminAuditView entries={adminAudit} />}

        {activeTab === "auth" && <LdapAuthView onNotify={setNotice} onChanged={loadAuthStatus} />}
      </section>
    </main>
  );
}

function LoginPage({ onLogin }: { onLogin: (response: LoginResponse) => void }) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setSubmitting(true);
    setError(null);

    try {
      const response = await fetch(`${apiBaseUrl}/api/auth/login`, {
        method: "POST",
        headers: buildJsonHeaders(),
        body: JSON.stringify({ username, password })
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response, "Credenciais invalidas ou usuario sem grupo autorizado."));
      }

      onLogin((await response.json()) as LoginResponse);
    } catch (loginError) {
      setError(loginError instanceof Error ? loginError.message : "Nao foi possivel autenticar.");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <main className="login-shell">
      <section className="login-visual" aria-label="Auditoria de arquivos">
        <div className="login-visual-art">
          <div className="server-stack">
            <span />
            <span />
            <span />
          </div>
          <div className="shield-core">
            <ShieldCheck size={82} />
          </div>
          <div className="audit-doc doc-a" />
          <div className="audit-doc doc-b" />
          <div className="audit-line line-a" />
          <div className="audit-line line-b" />
        </div>
        <div className="login-copy">
          <span>auditoria, rastreabilidade e evidencias</span>
          <h1>Arquivos sob controle.</h1>
          <p>Monitore criacoes, exclusoes, renomeacoes, acessos e movimentacoes em compartilhamentos corporativos.</p>
        </div>
      </section>

      <section className="login-panel">
        <div className="login-heading">
          <div className="login-mark">
            <ShieldAlert size={34} />
          </div>
          <div>
            <span>FILE SERVER MONITOR</span>
            <h2>Auditoria de Arquivos</h2>
            <p>Acesso com credenciais institucionais ao painel de eventos, alertas e relatorios.</p>
          </div>
        </div>

        <form className="login-card" onSubmit={submit}>
          <span>Painel institucional</span>
          <h3>Entrar</h3>
          <label>
            Usuario
            <input value={username} onChange={(event) => setUsername(event.target.value)} placeholder="Digite seu usuario institucional" autoComplete="username" />
          </label>
          <label>
            Senha
            <input value={password} onChange={(event) => setPassword(event.target.value)} placeholder="Digite sua senha" type="password" autoComplete="current-password" />
          </label>
          {error && <div className="login-error">{error}</div>}
          <button type="submit" disabled={submitting}>
            {submitting ? "Validando..." : "Entrar"}
          </button>
        </form>
      </section>
    </main>
  );
}

function LdapAuthView({ onNotify, onChanged }: { onNotify: (notice: Notice | null) => void; onChanged: () => void }) {
  const [config, setConfig] = useState<AuthConfig | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    fetchJson<AuthConfig>("/api/auth/config")
      .then(setConfig)
      .catch((error) => onNotify({ tone: "danger", message: error instanceof Error ? error.message : "Nao foi possivel carregar LDAP/AD." }));
  }, [onNotify]);

  function update<K extends keyof AuthConfig>(key: K, value: AuthConfig[K]) {
    setConfig((current) => current ? { ...current, [key]: value } : current);
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault();

    if (!config) {
      return;
    }

    setSaving(true);
    try {
      const response = await fetch(`${apiBaseUrl}/api/auth/config`, {
        method: "PUT",
        headers: buildJsonHeaders(),
        body: JSON.stringify(config)
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response, "Nao foi possivel salvar LDAP/AD."));
      }

      setConfig((await response.json()) as AuthConfig);
      onChanged();
      onNotify({ tone: "success", message: "Configuracao LDAP/AD salva." });
    } catch (error) {
      onNotify({ tone: "danger", message: error instanceof Error ? error.message : "Nao foi possivel salvar LDAP/AD." });
    } finally {
      setSaving(false);
    }
  }

  if (!config) {
    return <Panel title="LDAP/AD" subtitle="Carregando configuracao corporativa..." />;
  }

  return (
    <div className="view-stack auth-view">
      <section className="auth-hero">
        <div>
          <span>Controle de acesso</span>
          <h2>Autenticacao LDAP/AD</h2>
          <p>Defina a conexao com o Active Directory, mapeie grupos administrativos e valide o acesso corporativo antes de liberar o uso.</p>
        </div>
        <div className="auth-status-grid">
          <StatusCard label="Integracao" value={config.enabled ? "Ativa" : "Inativa"} detail="Estado atual do login corporativo" />
          <StatusCard label="Configuracao" value={config.configurationStatus === "complete" ? "Completa" : "Incompleta"} detail="Host, base DN e grupos" />
          <StatusCard label="Modo de login" value={config.enabled ? "LDAP/AD" : "Chave API"} detail="Admin local fica como contingencia" />
        </div>
      </section>

      <Panel title="Configuracao do acesso corporativo" subtitle="Preencha o servidor LDAP/AD, escolha o formato de autenticacao e informe os grupos que definem os perfis da aplicacao.">
        <form className="auth-form" onSubmit={submit}>
          <label className="check-row">
            <input type="checkbox" checked={config.enabled} onChange={(event) => update("enabled", event.target.checked)} />
            Habilitar autenticacao LDAP/AD
          </label>
          <label className="check-row">
            <input type="checkbox" checked={config.validateTlsCertificate} onChange={(event) => update("validateTlsCertificate", event.target.checked)} />
            Validar certificado TLS
          </label>

          <div className="form-grid four">
            <label>
              Host LDAP/AD
              <input value={config.host} onChange={(event) => update("host", event.target.value)} placeholder="172.16.200.6" />
            </label>
            <label>
              Porta
              <input value={config.port} onChange={(event) => update("port", Number(event.target.value))} inputMode="numeric" />
            </label>
            <label>
              Seguranca
              <select value={config.security} onChange={(event) => update("security", event.target.value)}>
                <option value="LDAPS">LDAPS</option>
                <option value="LDAP">LDAP</option>
              </select>
            </label>
            <label>
              Timeout (segundos)
              <input value={config.timeoutSeconds} onChange={(event) => update("timeoutSeconds", Number(event.target.value))} inputMode="numeric" />
            </label>
          </div>

          <label>
            Base DN
            <input value={config.baseDn} onChange={(event) => update("baseDn", event.target.value)} placeholder="DC=tceal,DC=tc,DC=br" />
          </label>

          <div className="form-grid three">
            <label>
              Formato de bind
              <select value={config.bindFormat} onChange={(event) => update("bindFormat", event.target.value)}>
                <option value="DOMINIO\\usuario">DOMINIO\usuario</option>
                <option value="usuario@dominio">usuario@dominio</option>
                <option value="DN">DN informado pelo usuario</option>
              </select>
            </label>
            <label>
              Sufixo do dominio
              <input value={config.domainSuffix} onChange={(event) => update("domainSuffix", event.target.value)} placeholder="tceal.tc.br" />
            </label>
            <label>
              Dominio NetBIOS
              <input value={config.netbiosDomain} onChange={(event) => update("netbiosDomain", event.target.value)} placeholder="tce-al" />
            </label>
          </div>

          <div className="form-grid three">
            <label>
              Grupo Administrador
              <input value={config.adminGroupDn} onChange={(event) => update("adminGroupDn", event.target.value)} placeholder="CN=FILESERV_ADMIN,OU=Grupos,DC=tceal,DC=tc,DC=br" />
            </label>
            <label>
              Grupo Operador
              <input value={config.operatorGroupDn} onChange={(event) => update("operatorGroupDn", event.target.value)} placeholder="CN=FILESERV_OPERADOR,OU=Grupos,DC=tceal,DC=tc,DC=br" />
            </label>
            <label>
              Grupo Leitor
              <input value={config.readerGroupDn} onChange={(event) => update("readerGroupDn", event.target.value)} placeholder="CN=FILESERV_LEITOR,OU=Grupos,DC=tceal,DC=tc,DC=br" />
            </label>
          </div>

          <div className="auth-badges">
            <span className="badge low">Configuracao salva</span>
            <span className="badge info">Login comum via LDAP/AD</span>
            <span className="badge neutral">Admin local para contingencia</span>
          </div>

          <button className="primary-button" type="submit" disabled={saving}>
            <KeyRound size={18} />
            {saving ? "Salvando..." : "Salvar configuracao"}
          </button>
        </form>
      </Panel>

      <InventoryScanConfigPanel onNotify={onNotify} />

      <RetentionConfigPanel onNotify={onNotify} />
    </div>
  );
}

function InventoryScanConfigPanel({ onNotify }: { onNotify: (notice: Notice | null) => void }) {
  const [config, setConfig] = useState<InventoryScanConfig | null>(null);
  const [saving, setSaving] = useState(false);
  const [requestingScan, setRequestingScan] = useState(false);

  useEffect(() => {
    fetchJson<InventoryScanConfig>("/api/inventory/config")
      .then(setConfig)
      .catch((error) => onNotify({ tone: "danger", message: error instanceof Error ? error.message : "Nao foi possivel carregar inventario." }));
  }, [onNotify]);

  function update<K extends keyof InventoryScanConfig>(key: K, value: InventoryScanConfig[K]) {
    setConfig((current) => current ? { ...current, [key]: value } : current);
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault();

    if (!config) {
      return;
    }

    setSaving(true);
    try {
      const response = await fetch(`${apiBaseUrl}/api/inventory/config`, {
        method: "PUT",
        headers: buildJsonHeaders(),
        body: JSON.stringify(config)
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response, "Nao foi possivel salvar inventario."));
      }

      setConfig((await response.json()) as InventoryScanConfig);
      onNotify({ tone: "success", message: "Configuracao do inventario salva." });
    } catch (error) {
      onNotify({ tone: "danger", message: error instanceof Error ? error.message : "Nao foi possivel salvar inventario." });
    } finally {
      setSaving(false);
    }
  }

  async function requestScanNow() {
    setRequestingScan(true);
    try {
      const response = await fetch(`${apiBaseUrl}/api/inventory/scan-now`, {
        method: "POST",
        headers: buildJsonHeaders()
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response, "Nao foi possivel solicitar o scan."));
      }

      setConfig((await response.json()) as InventoryScanConfig);
      onNotify({ tone: "success", message: "Scan de inventario solicitado ao agente." });
    } catch (error) {
      onNotify({ tone: "danger", message: error instanceof Error ? error.message : "Nao foi possivel solicitar o scan." });
    } finally {
      setRequestingScan(false);
    }
  }

  if (!config) {
    return <Panel title="Inventário gerencial" subtitle="Carregando configuracao de scan..." />;
  }

  return (
    <Panel title="Inventário gerencial" subtitle="Controle a varredura da estrutura de arquivos para indicadores de governança e capacidade.">
      <form className="auth-form retention-form" onSubmit={submit}>
        <div className="retention-summary">
          <StatusCard label="Estado" value={config.enabled ? "Ativo" : "Inativo"} detail={config.enabled ? "Agentes podem executar o scan" : "Nenhum scan automatico sera solicitado"} />
          <StatusCard label="Raiz" value={config.rootPath || "Nao definida"} detail={`${config.server || "Servidor"} · ${config.share || "Share"}`} />
          <StatusCard label="Agenda" value={`${config.intervalHours} h`} detail={config.windowStartLocal || config.windowEndLocal ? `${config.windowStartLocal || "00:00"} ate ${config.windowEndLocal || "23:59"}` : "Sem janela fixa"} />
        </div>

        <label className="check-row">
          <input type="checkbox" checked={config.enabled} onChange={(event) => update("enabled", event.target.checked)} />
          Habilitar scan de inventário
        </label>

        <label className="check-row">
          <input type="checkbox" checked={config.includeLastAccessTime} onChange={(event) => update("includeLastAccessTime", event.target.checked)} />
          Coletar data de último acesso
        </label>

        <div className="form-grid three">
          <label>
            Servidor
            <input value={config.server} onChange={(event) => update("server", event.target.value)} placeholder="FileServer" />
          </label>
          <label>
            Compartilhamento
            <input value={config.share} onChange={(event) => update("share", event.target.value)} placeholder="Corporativo" />
          </label>
          <label>
            Raiz do scan
            <input value={config.rootPath} onChange={(event) => update("rootPath", event.target.value)} placeholder="C:\\Corporativo" />
          </label>
        </div>

        <div className="form-grid five">
          <label>
            Intervalo (horas)
            <input value={config.intervalHours} onChange={(event) => update("intervalHours", Number(event.target.value))} inputMode="numeric" min={1} max={168} type="number" />
          </label>
          <label>
            Lote do scan
            <input value={config.batchSize} onChange={(event) => update("batchSize", Number(event.target.value))} inputMode="numeric" min={100} max={2000} step={100} type="number" />
          </label>
          <label>
            Limite de itens
            <input value={config.maxItemsPerScan} onChange={(event) => update("maxItemsPerScan", Number(event.target.value))} inputMode="numeric" min={0} max={10000000} step={1000} type="number" />
          </label>
          <label>
            Janela início
            <input value={config.windowStartLocal} onChange={(event) => update("windowStartLocal", event.target.value)} placeholder="01:00" />
          </label>
          <label>
            Janela fim
            <input value={config.windowEndLocal} onChange={(event) => update("windowEndLocal", event.target.value)} placeholder="05:00" />
          </label>
        </div>

        <div className="auth-badges">
          <span className="badge info">Configuração enviada ao agente</span>
          <span className="badge neutral">Use limite 0 para varrer tudo</span>
          <span className="badge low">Atualizado em {formatDate(config.updatedUtc)}</span>
          {config.runRequestedUtc ? <span className="badge info">Scan solicitado em {formatDate(config.runRequestedUtc)}</span> : null}
        </div>

        <div className="form-actions">
          <button className="secondary-button" type="button" onClick={requestScanNow} disabled={requestingScan || saving}>
            <RefreshCcw size={18} />
            {requestingScan ? "Solicitando..." : "Executar scan agora"}
          </button>
          <button className="primary-button" type="submit" disabled={saving || requestingScan}>
            <FolderTree size={18} />
            {saving ? "Salvando..." : "Salvar inventário"}
          </button>
        </div>
      </form>
    </Panel>
  );
}

function RetentionConfigPanel({ onNotify }: { onNotify: (notice: Notice | null) => void }) {
  const [config, setConfig] = useState<RetentionConfig | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    fetchJson<RetentionConfig>("/api/retention/config")
      .then(setConfig)
      .catch((error) => onNotify({ tone: "danger", message: error instanceof Error ? error.message : "Nao foi possivel carregar retencao." }));
  }, [onNotify]);

  function update<K extends keyof RetentionConfig>(key: K, value: RetentionConfig[K]) {
    setConfig((current) => current ? { ...current, [key]: value } : current);
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault();

    if (!config) {
      return;
    }

    setSaving(true);
    try {
      const response = await fetch(`${apiBaseUrl}/api/retention/config`, {
        method: "PUT",
        headers: buildJsonHeaders(),
        body: JSON.stringify(config)
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response, "Nao foi possivel salvar retencao."));
      }

      setConfig((await response.json()) as RetentionConfig);
      onNotify({ tone: "success", message: "Retencao de dados salva." });
    } catch (error) {
      onNotify({ tone: "danger", message: error instanceof Error ? error.message : "Nao foi possivel salvar retencao." });
    } finally {
      setSaving(false);
    }
  }

  if (!config) {
    return <Panel title="Retencao de dados" subtitle="Carregando politica de limpeza..." />;
  }

  return (
    <Panel title="Retencao de dados" subtitle="Controle quanto tempo eventos brutos, linha do tempo correlacionada e alertas ficam no banco.">
      <form className="auth-form retention-form" onSubmit={submit}>
        <div className="retention-summary">
          <StatusCard label="Estado" value={config.enabled ? "Ativa" : "Inativa"} detail={config.enabled ? "Limpeza automatica em execucao" : "Banco mantem os dados sem purga automatica"} />
          <StatusCard label="Linha do tempo" value={`${config.timelineDays} dias`} detail="Eventos correlacionados usados por telas e relatorios" />
          <StatusCard label="Execucao" value={`${config.intervalHours} h`} detail={`Lotes de ate ${config.purgeBatchSize.toLocaleString("pt-BR")} registros`} />
        </div>

        <label className="check-row">
          <input type="checkbox" checked={config.enabled} onChange={(event) => update("enabled", event.target.checked)} />
          Habilitar limpeza automatica
        </label>

        <div className="form-grid five">
          <label>
            Eventos brutos (dias)
            <input value={config.eventsDays} onChange={(event) => update("eventsDays", Number(event.target.value))} inputMode="numeric" min={7} max={3650} type="number" />
          </label>
          <label>
            Linha do tempo (dias)
            <input value={config.timelineDays} onChange={(event) => update("timelineDays", Number(event.target.value))} inputMode="numeric" min={7} max={3650} type="number" />
          </label>
          <label>
            Alertas (dias)
            <input value={config.alertsDays} onChange={(event) => update("alertsDays", Number(event.target.value))} inputMode="numeric" min={7} max={3650} type="number" />
          </label>
          <label>
            Intervalo (horas)
            <input value={config.intervalHours} onChange={(event) => update("intervalHours", Number(event.target.value))} inputMode="numeric" min={1} max={168} type="number" />
          </label>
          <label>
            Lote maximo
            <input value={config.purgeBatchSize} onChange={(event) => update("purgeBatchSize", Number(event.target.value))} inputMode="numeric" min={100} max={100000} step={100} type="number" />
          </label>
        </div>

        <div className="auth-badges">
          <span className="badge info">Relatorios usam timeline persistida</span>
          <span className="badge neutral">Limpeza em lotes para reduzir impacto</span>
          <span className="badge low">Atualizado em {formatDate(config.updatedUtc)}</span>
        </div>

        <button className="primary-button" type="submit" disabled={saving}>
          <Database size={18} />
          {saving ? "Salvando..." : "Salvar retencao"}
        </button>
      </form>
    </Panel>
  );
}

function StatusCard({ label, value, detail }: { label: string; value: string; detail: string }) {
  return (
    <article className="auth-status-card">
      <span>{label}</span>
      <strong>{value}</strong>
      <p>{detail}</p>
    </article>
  );
}

function Dashboard({
  health,
  events,
  displayEvents,
  openAlerts,
  criticalAlerts,
  offlineAgents,
  activitySummary,
  baselineAnomalies,
  summaryFilters,
  onSummaryFiltersChange,
  onNotify
}: {
  health: HealthResponse | null;
  events: FileAuditEvent[];
  displayEvents: DisplayEvent[];
  openAlerts: FileServerAlert[];
  criticalAlerts: FileServerAlert[];
  offlineAgents: AgentHealth[];
  activitySummary: ActivitySummary | null;
  baselineAnomalies: BaselineAnomalyResponse | null;
  summaryFilters: ActivitySummaryFilters;
  onSummaryFiltersChange: (filters: ActivitySummaryFilters) => void;
  onNotify: (notice: Notice | null) => void;
}) {
  const latestEvents = displayEvents.slice(0, 8);
  const highestAnomaly = getHighestAnomaly(baselineAnomalies);
  const posture = getOperationalPosture(openAlerts.length, criticalAlerts.length, offlineAgents.length, highestAnomaly);
  const updateFilter = (field: keyof ActivitySummaryFilters, value: string) => {
    onSummaryFiltersChange({ ...summaryFilters, [field]: value });
  };

  return (
    <div className="view-stack">
      <section className="executive-grid">
        <ExecutiveCard
          title="Postura Atual"
          value={posture.label}
          detail={posture.detail}
          tone={posture.tone}
        />
        <ExecutiveCard
          title="Maior Desvio"
          value={highestAnomaly?.name ?? "Sem desvio forte"}
          detail={
            highestAnomaly
              ? `${highestAnomaly.currentCount.toLocaleString("pt-BR")} agora vs ${highestAnomaly.baselineAverage.toLocaleString("pt-BR")} na média`
              : "O período atual está próximo do histórico recente."
          }
          tone={highestAnomaly && highestAnomaly.deltaPercent > 100 ? "danger" : "neutral"}
        />
        <ExecutiveCard
          title="Janela Analisada"
          value={labelForPeriod(summaryFilters.periodHours)}
          detail={buildFilterSummary(summaryFilters)}
          tone="neutral"
        />
      </section>

      <section className="metrics-grid">
        <Metric icon={<Database size={20} />} label="Eventos" value={health?.storedEvents ?? events.length} tone="neutral" />
        <Metric icon={<AlertTriangle size={20} />} label="Alertas abertos" value={openAlerts.length} tone="warning" />
        <Metric icon={<ShieldAlert size={20} />} label="Críticos" value={criticalAlerts.length} tone="danger" />
        <Metric icon={<Server size={20} />} label="Agentes atenção" value={offlineAgents.length} tone="neutral" />
        <Metric icon={<BarChart3 size={20} />} label="Eventos no período" value={activitySummary?.totalEvents ?? 0} tone="neutral" />
      </section>

      <Panel title="Filtros do Relatório" subtitle="Use um mesmo recorte para acompanhar volume, anomalias e exportações.">
        <div className="report-filters">
          <label>
            <span>Período</span>
            <select value={summaryFilters.periodHours} onChange={(event) => updateFilter("periodHours", event.target.value)}>
              <option value="1">Última hora</option>
              <option value="6">Últimas 6 horas</option>
              <option value="24">Últimas 24 horas</option>
              <option value="168">Últimos 7 dias</option>
              <option value="720">Últimos 30 dias</option>
            </select>
          </label>
          <label>
            <span>Servidor</span>
            <input value={summaryFilters.server} onChange={(event) => updateFilter("server", event.target.value)} placeholder="FileServer" />
          </label>
          <label>
            <span>Compartilhamento</span>
            <input value={summaryFilters.share} onChange={(event) => updateFilter("share", event.target.value)} placeholder="Departamentos" />
          </label>
          <label>
            <span>Usuário</span>
            <input value={summaryFilters.user} onChange={(event) => updateFilter("user", event.target.value)} placeholder="EMPRESA\\usuario" />
          </label>
          <label>
            <span>Ação</span>
            <select value={summaryFilters.action} onChange={(event) => updateFilter("action", event.target.value)}>
              <option value="">Todas</option>
              <option value="created">Criado</option>
              <option value="modified">Alterado</option>
              <option value="deleted">Excluído</option>
              <option value="renamed">Renomeado</option>
              <option value="moved">Movido</option>
              <option value="permission_changed">Permissão</option>
            </select>
          </label>
        </div>
        <div className="toolbar export-toolbar">
          <button className="text-button" type="button" onClick={() => downloadAlertsCsv(openAlerts, onNotify)}>
            <Download size={16} />
            Exportar Alertas
          </button>
          <button className="text-button" type="button" onClick={() => downloadBaselineAnomaliesCsv(summaryFilters, onNotify)}>
            <Download size={16} />
            Exportar Anomalias
          </button>
        </div>
      </Panel>

      <section className="analytics-grid">
        <Panel title="Top Ações" subtitle="Volume concentrado no período selecionado.">
          <SummaryBars items={activitySummary?.byAction ?? []} />
        </Panel>
        <Panel title="Top Shares" subtitle="Compartilhamentos com mais atividade recente.">
          <SummaryBars items={activitySummary?.byShare ?? []} />
        </Panel>
        <Panel title="Top Usuários" subtitle="Usuários mais presentes no recorte atual.">
          <SummaryBars items={activitySummary?.byUser ?? []} />
        </Panel>
      </section>

      <section className="analytics-grid">
        <Panel title="Anomalias por Ação" subtitle="Comparação com a média dos 7 períodos anteriores.">
          <BaselineList items={baselineAnomalies?.byAction ?? []} />
        </Panel>
        <Panel title="Anomalias por Share" subtitle="Desvios de comportamento por compartilhamento.">
          <BaselineList items={baselineAnomalies?.byShare ?? []} />
        </Panel>
        <Panel title="Anomalias por Usuário" subtitle="Usuários acima do padrão recente.">
          <BaselineList items={baselineAnomalies?.byUser ?? []} />
        </Panel>
      </section>

      <section className="split-grid">
        <Panel title="Eventos Recentes" subtitle="Linha curta para leitura operacional rápida.">
          <EventTable events={latestEvents} compact />
        </Panel>
        <Panel title="Alertas Recentes" subtitle="Itens abertos mais recentes e mais acionáveis.">
          <AlertList alerts={openAlerts.slice(0, 8)} />
        </Panel>
      </section>
    </div>
  );
}

function SummaryBars({ items }: { items: ActivitySummaryItem[] }) {
  if (items.length === 0) {
    return <EmptyState text="Sem atividade no período." />;
  }

  const max = Math.max(...items.map((item) => item.eventCount), 1);

  return (
    <div className="summary-bars">
      {items.map((item) => (
        <div className="summary-bar" key={item.name}>
          <div>
            <strong>{item.name}</strong>
            <span>{item.eventCount.toLocaleString("pt-BR")}</span>
          </div>
          <meter min={0} max={max} value={item.eventCount} aria-label={item.name} />
        </div>
      ))}
    </div>
  );
}

function BaselineList({ items }: { items: BaselineAnomalyItem[] }) {
  if (items.length === 0) {
    return <EmptyState text="Sem anomalias relevantes neste período." />;
  }

  return (
    <div className="summary-bars">
      {items.map((item) => (
        <div className="summary-bar" key={item.name}>
          <div>
            <strong>{item.name}</strong>
            <span>
              {item.currentCount.toLocaleString("pt-BR")} vs {item.baselineAverage.toLocaleString("pt-BR")}
            </span>
          </div>
          <small className={item.deltaPercent > 100 ? "anomaly-high" : "anomaly-medium"}>
            {item.deltaPercent > 0 ? "+" : ""}
            {item.deltaPercent.toFixed(0)}%
          </small>
        </div>
      ))}
    </div>
  );
}

function EventsView({
  events,
  filter,
  onFilterChange,
  onNotify,
  pagination
}: {
  events: DisplayEvent[];
  filter: string;
  onFilterChange: (value: string) => void;
  onNotify: (notice: Notice | null) => void;
  pagination: PaginationState;
}) {
  return (
    <div className="view-stack">
      <div className="toolbar">
        <label className="search-box">
          <Search size={18} />
          <input
            value={filter}
            onChange={(event) => onFilterChange(event.target.value)}
            placeholder="Filtrar por usuário, caminho, ação ou origem"
          />
        </label>
        <button className="text-button" type="button" onClick={() => downloadEventsCsv(onNotify)}>
          <Download size={16} />
          Exportar CSV
        </button>
      </div>
      <Panel title="Linha do Tempo">
        <EventTable events={events} pagination={pagination} />
      </Panel>
    </div>
  );
}

function InvestigationView({ onNotify }: { onNotify: (notice: Notice | null) => void }) {
  const [filters, setFilters] = useState<InvestigationFilters>(defaultInvestigationFilters);
  const [events, setEvents] = useState<DisplayEvent[]>([]);
  const [searched, setSearched] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const displayEvents = useMemo(() => events as DisplayEvent[], [events]);
  const [page, setPage] = useState(1);
  const uniqueUsers = useMemo(() => new Set(displayEvents.map((event) => event.user).filter(Boolean)).size, [displayEvents]);
  const dominantAction = useMemo(() => getTopEventAction(displayEvents), [displayEvents]);
  const totalPages = Math.max(1, Math.ceil(displayEvents.length / EVENTS_PAGE_SIZE));
  const safePage = Math.min(page, totalPages);
  const visibleDisplayEvents = useMemo(
    () => displayEvents.slice((safePage - 1) * EVENTS_PAGE_SIZE, safePage * EVENTS_PAGE_SIZE),
    [displayEvents, safePage]
  );

  useEffect(() => {
    setPage((current) => Math.min(current, totalPages));
  }, [totalPages]);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    if (filters.periodMode === "custom") {
      if (!filters.fromDate) {
        setError("Selecione pelo menos a data inicial para consultar um dia ou intervalo.");
        onNotify({ tone: "warning", message: "Escolha a data inicial para abrir a investigação por dia ou intervalo." });
        return;
      }

      if (filters.toDate && filters.toDate < filters.fromDate) {
        setError("A data final não pode ser anterior à data inicial.");
        onNotify({ tone: "warning", message: "Revise o intervalo informado antes de consultar." });
        return;
      }
    }

    setLoading(true);
    setError(null);

    try {
      const result = await fetchJson<DisplayEvent[]>(buildInvestigationUrl(filters));
      const displayResult = result as DisplayEvent[];
      setEvents(result);
      setPage(1);
      setSearched(true);
      onNotify({
        tone: displayResult.length > 0 ? "success" : "warning",
        message: displayResult.length > 0 ? `Investigação atualizada com ${displayResult.length.toLocaleString("pt-BR")} evento(s).` : "Nenhum evento encontrado no recorte consultado."
      });
    } catch (searchError) {
      setError(searchError instanceof Error ? searchError.message : "Falha ao consultar eventos.");
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="view-stack">
      <section className="executive-grid">
        <ExecutiveCard
          title="Eventos Encontrados"
          value={searched ? displayEvents.length.toLocaleString("pt-BR") : "-"}
          detail={searched ? "Resultado do último recorte consultado." : "Faça a primeira busca para abrir a linha investigativa."}
          tone={displayEvents.length > 0 ? "warning" : "neutral"}
        />
        <ExecutiveCard
          title="Usuários no Recorte"
          value={searched ? uniqueUsers.toLocaleString("pt-BR") : "-"}
          detail={dominantAction ? `Ação dominante: ${dominantAction}.` : "Sem ação dominante até o momento."}
          tone={displayEvents.length > 100 ? "danger" : "neutral"}
        />
        <ExecutiveCard
          title="Escopo Atual"
          value={filters.periodMode === "custom" ? "Período manual" : labelForPeriod(filters.periodHours)}
          detail={buildInvestigationSummary(filters)}
          tone="neutral"
        />
      </section>

      <Panel title="Consulta de Investigação" subtitle="Refine o recorte antes de abrir a linha do tempo detalhada.">
        <form className="path-form investigation-form" onSubmit={submit}>
          <label>
            Período
            <select value={filters.periodMode} onChange={(event) => setFilters({ ...filters, periodMode: event.target.value as "preset" | "custom" })}>
              <option value="preset">Faixa rápida</option>
              <option value="custom">Dia ou intervalo</option>
            </select>
          </label>
          {filters.periodMode === "preset" ? (
            <label>
              Faixa rápida
              <select value={filters.periodHours} onChange={(event) => setFilters({ ...filters, periodHours: event.target.value })}>
                <option value="1">Última hora</option>
                <option value="6">Últimas 6 horas</option>
                <option value="24">Últimas 24 horas</option>
                <option value="168">Últimos 7 dias</option>
                <option value="720">Últimos 30 dias</option>
              </select>
            </label>
          ) : (
            <>
              <label>
                De
                <input type="date" value={filters.fromDate} onChange={(event) => setFilters({ ...filters, fromDate: event.target.value })} />
              </label>
              <label>
                Até
                <input type="date" value={filters.toDate} onChange={(event) => setFilters({ ...filters, toDate: event.target.value })} />
              </label>
            </>
          )}
          <label>
            Servidor
            <input value={filters.server} onChange={(event) => setFilters({ ...filters, server: event.target.value })} placeholder="FileServer" />
          </label>
          <label>
            Usuário
            <input value={filters.user} onChange={(event) => setFilters({ ...filters, user: event.target.value })} placeholder="EMPRESA\\usuario" />
          </label>
          <label className="wide-field">
            Caminho
            <input value={filters.path} onChange={(event) => setFilters({ ...filters, path: event.target.value })} placeholder="D:\\Shares\\Departamentos ou parte do arquivo" />
          </label>
          <label>
            Ação
            <select value={filters.action} onChange={(event) => setFilters({ ...filters, action: event.target.value })}>
              <option value="">Todas</option>
              <option value="created">Criado</option>
              <option value="modified">Alterado</option>
              <option value="deleted">Excluído</option>
              <option value="renamed">Renomeado</option>
              <option value="moved">Movido</option>
              <option value="permission_changed">Permissão</option>
            </select>
          </label>
          <button className="text-button path-submit" type="submit" disabled={loading}>
            <Search size={16} />
            Consultar
          </button>
        </form>
      </Panel>

      {error && <div className="error-banner">{error}</div>}

      <Panel title="Linha do Tempo Investigativa" subtitle="Eventos completos para rastrear autoria, origem, processo e movimento do arquivo.">
        {searched ? (
          <InvestigationTable
            events={visibleDisplayEvents}
            pagination={{
              page: safePage,
              totalPages,
              pageItems: visibleDisplayEvents.length,
              totalItems: displayEvents.length,
              onPrevious: () => setPage((current) => Math.max(1, current - 1)),
              onNext: () => setPage((current) => Math.min(totalPages, current + 1))
            }}
          />
        ) : (
          <EmptyState text="Preencha os filtros e consulte para iniciar a investigação." />
        )}
      </Panel>
    </div>
  );
}

function ReportsView({ onNotify }: { onNotify: (notice: Notice | null) => void }) {
  const [mode, setMode] = useState<"guided" | "custom">("guided");
  const [selectedScenario, setSelectedScenario] = useState<ReportScenarioId>("folder-activity");
  const [filters, setFilters] = useState<ReportFilters>(createFiltersForScenario("folder-activity"));
  const [events, setEvents] = useState<DisplayEvent[]>([]);
  const [searched, setSearched] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [generatedReport, setGeneratedReport] = useState<GeneratedReport | null>(null);
  const [activeReportResultTab, setActiveReportResultTab] = useState<"summary" | "timeline" | "groups">("summary");
  const [showAdvancedReportFilters, setShowAdvancedReportFilters] = useState(false);
  const [page, setPage] = useState(1);
  const scenario = getReportScenario(selectedScenario);
  const reportTitle = mode === "guided" ? scenario.title : "Relatorio personalizado";
  const totalPages = Math.max(1, Math.ceil(events.length / EVENTS_PAGE_SIZE));
  const safePage = Math.min(page, totalPages);
  const visibleEvents = useMemo(
    () => events.slice((safePage - 1) * EVENTS_PAGE_SIZE, safePage * EVENTS_PAGE_SIZE),
    [events, safePage]
  );
  const groupedRows = useMemo(() => buildReportGroups(events, filters.groupBy), [events, filters.groupBy]);
  const reportScenarioGroups = useMemo(() => groupReportScenariosByCategory(reportScenarios), []);
  const uniqueUsers = useMemo(() => new Set(events.map((event) => event.user).filter(Boolean)).size, [events]);
  const affectedPaths = useMemo(() => new Set(events.map((event) => event.path).filter(Boolean)).size, [events]);
  const dominantAction = useMemo(() => getTopEventAction(events), [events]);

  useEffect(() => {
    setPage((current) => Math.min(current, totalPages));
  }, [totalPages]);

  function selectScenario(id: ReportScenarioId) {
    setSelectedScenario(id);
    setMode("guided");
    setFilters(createFiltersForScenario(id));
    setGeneratedReport(null);
    setActiveReportResultTab("summary");
    setError(null);
  }

  function startCustomReport() {
    setMode("custom");
    setFilters(createDefaultReportFilters());
    setGeneratedReport(null);
    setActiveReportResultTab("summary");
    setError(null);
  }

  async function loadReportEventsForCurrentFilters(options: { showNotice: boolean; activateTimeline: boolean }) {
    const result = await fetchJson<DisplayEvent[]>(buildReportEventsUrl(filters, getReportEventTake(mode === "guided" ? selectedScenario : null)));
    setEvents(result);
    setPage(1);
    setSearched(true);

    if (options.activateTimeline) {
      setActiveReportResultTab("timeline");
    }

    if (options.showNotice) {
      onNotify({
        tone: result.length > 0 ? "success" : "warning",
        message: result.length > 0
          ? `Relatorio atualizado com ${result.length.toLocaleString("pt-BR")} evento(s).`
          : "Nenhum evento encontrado para este recorte."
      });
    }

    return result;
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    if (!validateReportFilters(filters, setError, onNotify)) {
      return;
    }

    setLoading(true);
    setError(null);
    setGeneratedReport(null);

    try {
      await loadReportEventsForCurrentFilters({ showNotice: true, activateTimeline: true });
    } catch (searchError) {
      setError(searchError instanceof Error ? searchError.message : "Falha ao consultar relatorio.");
    } finally {
      setLoading(false);
    }
  }

  async function generateReport() {
    if (!validateReportFilters(filters, setError, onNotify)) {
      return;
    }

    setLoading(true);
    setError(null);
    setGeneratedReport(null);

    try {
      const reportEvents = await loadReportEventsForCurrentFilters({ showNotice: false, activateTimeline: false });
      const inventorySummary = await fetchInventorySummaryForReport(filters);
      setGeneratedReport(buildGeneratedReport(reportTitle, filters, reportEvents, inventorySummary));
      setActiveReportResultTab("summary");
      onNotify({
        tone: "success",
        message: `Relatorio gerado com ${reportEvents.length.toLocaleString("pt-BR")} evento(s) no recorte.`
      });
    } catch (error) {
      const fallbackEvents = searched ? events : [];
      onNotify({
        tone: "warning",
        message: error instanceof Error
          ? `Previa textual gerada, mas a leitura de inventario nao foi anexada: ${error.message}`
          : "Previa textual gerada sem o resumo de inventario."
      });
      setGeneratedReport(buildGeneratedReport(reportTitle, filters, fallbackEvents, null));
      setActiveReportResultTab("summary");
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="view-stack reports-view">
      <section className="executive-grid reports-kpi-strip">
        <ExecutiveCard
          title="Eventos no recorte"
          value={searched ? events.length.toLocaleString("pt-BR") : "-"}
          detail={searched ? summarizeReportFilters(filters) || "Sem filtros adicionais." : "Escolha um relatorio e investigue o recorte."}
          tone={events.length > 500 ? "warning" : "neutral"}
        />
        <ExecutiveCard
          title="Usuarios envolvidos"
          value={searched ? uniqueUsers.toLocaleString("pt-BR") : "-"}
          detail={dominantAction ? `Acao dominante: ${dominantAction}.` : "Aguardando consulta."}
          tone={uniqueUsers > 20 ? "warning" : "neutral"}
        />
        <ExecutiveCard
          title="Caminhos afetados"
          value={searched ? affectedPaths.toLocaleString("pt-BR") : "-"}
          detail={`Agrupamento: ${labelForReportGroup(filters.groupBy)}.`}
          tone={affectedPaths > 100 ? "danger" : "neutral"}
        />
      </section>

      <section className="report-workflow">
        <Panel title="Modelos de relatório" subtitle="Escolha um atalho guiado ou monte um recorte livre.">
          <div className="report-mode-bar">
            <button className={mode === "guided" ? "active" : ""} type="button" onClick={() => setMode("guided")}>
              Guiados
            </button>
            <button className={mode === "custom" ? "active" : ""} type="button" onClick={startCustomReport}>
              Personalizado
            </button>
          </div>

          {generatedReport && (
            <div className="report-current-model">
              <div>
                <span>Relatório gerado</span>
                <strong>{generatedReport.title}</strong>
              </div>
              <button className="text-button" type="button" onClick={() => setGeneratedReport(null)}>
                Trocar modelo
              </button>
            </div>
          )}

          {mode === "guided" ? (
            <div className="report-scenario-groups">
              {reportScenarioGroups.map((group) => (
                <section key={group.category} className="report-scenario-group">
                  <div className="report-scenario-group-head">
                    <strong>{group.category}</strong>
                    <span>{group.items.length} modelo(s)</span>
                  </div>
                  <div className="report-card-grid compact">
                    {group.items.map((item) => (
                      <button
                        key={item.id}
                        className={`report-card ${selectedScenario === item.id ? "active" : ""}`}
                        type="button"
                        onClick={() => selectScenario(item.id)}
                      >
                        <strong>{item.title}</strong>
                        <span>{item.description}</span>
                      </button>
                    ))}
                  </div>
                </section>
              ))}
            </div>
          ) : (
            <div className="custom-report-note">
              <strong>Relatorio personalizado</strong>
              <span>Monte livremente o periodo, escopo, acao, origem e agrupamento antes de investigar.</span>
            </div>
          )}
        </Panel>
      </section>

      <Panel title={reportTitle} subtitle={mode === "guided" ? scenario.focus : "Use filtros livres e gere um recorte reutilizavel."}>
        <form className="path-form report-form" onSubmit={submit}>
          <label>
            Periodo
            <select value={filters.periodMode} onChange={(event) => setFilters({ ...filters, periodMode: event.target.value as "preset" | "custom" })}>
              <option value="preset">Faixa rapida</option>
              <option value="custom">Dia ou intervalo</option>
            </select>
          </label>
          {filters.periodMode === "preset" ? (
            <label>
              Faixa rapida
              <select value={filters.periodHours} onChange={(event) => setFilters({ ...filters, periodHours: event.target.value })}>
                <option value="1">Ultima hora</option>
                <option value="6">Ultimas 6 horas</option>
                <option value="24">Ultimas 24 horas</option>
                <option value="168">Ultimos 7 dias</option>
                <option value="720">Ultimos 30 dias</option>
              </select>
            </label>
          ) : (
            <>
              <label>
                De
                <input type="date" value={filters.fromDate} onChange={(event) => setFilters({ ...filters, fromDate: event.target.value })} />
              </label>
              <label>
                Ate
                <input type="date" value={filters.toDate} onChange={(event) => setFilters({ ...filters, toDate: event.target.value })} />
              </label>
            </>
          )}
          <label>
            Servidor
            <input value={filters.server} onChange={(event) => setFilters({ ...filters, server: event.target.value })} placeholder="FileServer" />
          </label>
          <label>
            Compartilhamento
            <input value={filters.share} onChange={(event) => setFilters({ ...filters, share: event.target.value })} placeholder="Corporativo" />
          </label>
          <label>
            Usuario
            <input value={filters.user} onChange={(event) => setFilters({ ...filters, user: event.target.value })} placeholder="DOMINIO\\usuario" />
          </label>
          <label className="wide-field">
            Caminho
            <input value={filters.path} onChange={(event) => setFilters({ ...filters, path: event.target.value })} placeholder="C:\\Corporativo\\RH ou parte do caminho" />
          </label>
          <label>
            Acao
            <select value={filters.action} onChange={(event) => setFilters({ ...filters, action: event.target.value })}>
              <option value="">Todas</option>
              <option value="created">Criado</option>
              <option value="modified">Alterado</option>
              <option value="accessed">Acessado</option>
              <option value="deleted">Excluido</option>
              <option value="renamed">Renomeado</option>
              <option value="moved">Movido</option>
              <option value="permission_changed">Permissao</option>
            </select>
          </label>
          <button className="text-button report-advanced-toggle" type="button" onClick={() => setShowAdvancedReportFilters((current) => !current)}>
            <Plus size={16} />
            {showAdvancedReportFilters ? "Ocultar filtros avançados" : "Filtros avançados"}
          </button>
          {showAdvancedReportFilters && (
            <div className="report-advanced-grid">
              <label>
                Host origem
                <input value={filters.sourceHost} onChange={(event) => setFilters({ ...filters, sourceHost: event.target.value })} placeholder="NOTE-01" />
              </label>
              <label>
                IP origem
                <input value={filters.sourceIp} onChange={(event) => setFilters({ ...filters, sourceIp: event.target.value })} placeholder="192.168.2.10" />
              </label>
              <label>
                Extensoes
                <input value={filters.extension} onChange={(event) => setFilters({ ...filters, extension: event.target.value })} placeholder=".exe,.ps1,.bat" />
              </label>
              <label>
                Resultado
                <input value={filters.result} onChange={(event) => setFilters({ ...filters, result: event.target.value })} placeholder="success, denied..." />
              </label>
              <label>
                Severidade
                <select value={filters.severity} onChange={(event) => setFilters({ ...filters, severity: event.target.value })}>
                  <option value="">Todas</option>
                  <option value="info">Info</option>
                  <option value="warning">Warning</option>
                  <option value="critical">Critical</option>
                </select>
              </label>
              <label>
                Agrupar por
                <select value={filters.groupBy} onChange={(event) => setFilters({ ...filters, groupBy: event.target.value as ReportGrouping })}>
                  <option value="action">Acao</option>
                  <option value="user">Usuario</option>
                  <option value="server">Servidor</option>
                  <option value="share">Compartilhamento</option>
                  <option value="sourceHost">Host origem</option>
                  <option value="path">Caminho</option>
                  <option value="extension">Extensao</option>
                  <option value="severity">Severidade</option>
                </select>
              </label>
            </div>
          )}
          <div className="report-actions">
            <button className="text-button" type="submit" disabled={loading}>
              <Search size={16} />
              Investigar
            </button>
            <button className="text-button" type="button" onClick={() => downloadReportCsv(filters, onNotify)}>
              <Download size={16} />
              Exportar CSV
            </button>
            <button className="text-button" type="button" onClick={generateReport} disabled={loading}>
              <ClipboardList size={16} />
              Gerar relatorio
            </button>
          </div>
        </form>
      </Panel>

      {error && <div className="error-banner">{error}</div>}

      <Panel
        title={generatedReport ? `Resultado: ${generatedReport.title}` : "Resultado da investigação"}
        subtitle={generatedReport ? "Relatório gerado com o mesmo recorte investigado." : "Consulte um recorte para liberar resumo, linha do tempo e agrupamentos."}
      >
        <div className="report-result-shell">
          <div className="report-result-tabs">
            <button className={activeReportResultTab === "summary" ? "active" : ""} type="button" onClick={() => setActiveReportResultTab("summary")}>
              Resumo
            </button>
            <button className={activeReportResultTab === "timeline" ? "active" : ""} type="button" onClick={() => setActiveReportResultTab("timeline")}>
              Linha do tempo
            </button>
            <button className={activeReportResultTab === "groups" ? "active" : ""} type="button" onClick={() => setActiveReportResultTab("groups")}>
              Agrupamentos
            </button>
          </div>

          {activeReportResultTab === "summary" && (
            generatedReport ? (
              <div className="report-preview">
                <div className="report-preview-head">
                  <div>
                    <strong>{generatedReport.title}</strong>
                    <span>Gerado em {formatDate(generatedReport.generatedAt)}</span>
                  </div>
                  <button className="text-button" type="button" onClick={() => window.print()}>
                    <Download size={16} />
                    Imprimir / salvar PDF
                  </button>
                </div>
                <p>{generatedReport.filtersSummary || "Sem filtros adicionais."}</p>
                <div className="report-preview-metrics">
                  <span>{generatedReport.events.length.toLocaleString("pt-BR")} evento(s)</span>
                  <span>{new Set(generatedReport.events.map((event) => event.user).filter(Boolean)).size.toLocaleString("pt-BR")} usuario(s)</span>
                  <span>{new Set(generatedReport.events.map((event) => event.path).filter(Boolean)).size.toLocaleString("pt-BR")} caminho(s)</span>
                </div>
                <div className="report-qbr-grid">
                  <article className="report-qbr-hero">
                    <span className="inventory-kicker">Resumo executivo</span>
                    <strong>{generatedReport.title}</strong>
                    <p>{generatedReport.executiveSummary}</p>
                  </article>
                  <div className="report-qbr-cards">
                    <ReportTextList title="Destaques do recorte" items={generatedReport.highlights} emptyText="Sem destaque relevante no recorte." />
                    <ReportTextList title="Pontos de atencao" items={generatedReport.risks} emptyText="Sem ponto de atencao relevante no recorte." />
                    <ReportTextList title="Proximos passos" items={generatedReport.nextSteps} emptyText="Sem proximo passo sugerido automaticamente." />
                  </div>
                </div>
                <div className="report-qbr-sections">
                  {generatedReport.sections.map((section) => (
                    <article key={section.title} className="report-qbr-section">
                      <div className="report-qbr-section-head">
                        <span className="inventory-kicker">Seção</span>
                        <strong>{section.title}</strong>
                      </div>
                      {section.items.length === 0 ? (
                        <p>Sem observações relevantes para esta seção no recorte atual.</p>
                      ) : (
                        <ul>
                          {section.items.map((item) => (
                            <li key={`${section.title}-${item}`}>{item}</li>
                          ))}
                        </ul>
                      )}
                    </article>
                  ))}
                </div>
                {generatedReport.inventorySummary && (
                  <div className="report-preview-metrics">
                    <span>{generatedReport.inventorySummary.share ?? "Inventario"} · {formatBytes(generatedReport.inventorySummary.totalBytes)}</span>
                    <span>{generatedReport.inventorySummary.fileCount.toLocaleString("pt-BR")} arquivo(s)</span>
                    <span>{generatedReport.inventorySummary.recommendations.length.toLocaleString("pt-BR")} recomendacao(oes)</span>
                    <span>score {generatedReport.inventorySummary.insight.score}</span>
                    <span>snapshot {generatedReport.inventorySummary.finishedUtc ? formatDate(generatedReport.inventorySummary.finishedUtc) : "em andamento"}</span>
                  </div>
                )}
              </div>
            ) : (
              <div className="report-empty-summary">
                <ClipboardList size={28} />
                <strong>Nenhum relatório gerado ainda</strong>
                <p>Use Investigar para validar os eventos e depois clique em Gerar relatório. O resumo aparecerá aqui, separado dos modelos guiados.</p>
              </div>
            )
          )}

          {activeReportResultTab === "timeline" && (
            searched ? (
              <InvestigationTable
                events={visibleEvents}
                pagination={{
                  page: safePage,
                  totalPages,
                  pageItems: visibleEvents.length,
                  totalItems: events.length,
                  onPrevious: () => setPage((current) => Math.max(1, current - 1)),
                  onNext: () => setPage((current) => Math.min(totalPages, current + 1))
                }}
              />
            ) : (
              <EmptyState text="Escolha um relatório, ajuste os filtros e clique em Investigar." />
            )
          )}

          {activeReportResultTab === "groups" && (
            <div className="report-group-layout">
              <Panel title="Resumo por agrupamento" subtitle={`Top ${labelForReportGroup(filters.groupBy).toLowerCase()} no recorte investigado.`}>
                {groupedRows.length > 0 ? <ReportGroupList rows={groupedRows} total={events.length} /> : <EmptyState text="Consulte um recorte para gerar o resumo." />}
              </Panel>
              {generatedReport && (
                <>
                  <Panel title="Top ações" subtitle="Distribuição dominante no recorte consultado.">
                    <ReportGroupList rows={generatedReport.topActions} total={generatedReport.events.length} />
                  </Panel>
                  <Panel title="Top usuários" subtitle="Principais atores envolvidos no período.">
                    <ReportGroupList rows={generatedReport.topUsers} total={generatedReport.events.length} />
                  </Panel>
                  <Panel title="Top caminhos" subtitle="Áreas mais impactadas no recorte.">
                    <ReportGroupList rows={generatedReport.topPaths} total={generatedReport.events.length} />
                  </Panel>
                </>
              )}
            </div>
          )}
        </div>
      </Panel>
    </div>
  );
}

function AlertsView({
  alerts,
  rules,
  canManageAlerts,
  onChanged,
  onAcknowledge,
  onNotify
}: {
  alerts: FileServerAlert[];
  rules: AlertRuleConfig[];
  canManageAlerts: boolean;
  onChanged: () => void;
  onAcknowledge: () => void;
  onNotify: (notice: Notice | null) => void;
}) {
  const enabledRules = rules.filter((rule) => rule.enabled).length;
  const criticalAlerts = alerts.filter((alert) => alert.severity === "critical").length;
  const openAlerts = alerts.filter((alert) => alert.status === "open").length;
  const [expandedAlertId, setExpandedAlertId] = useState<string | null>(null);
  const [loadingAlertId, setLoadingAlertId] = useState<string | null>(null);
  const [operationsByAlert, setOperationsByAlert] = useState<Record<string, FileAuditEvent[]>>({});

  async function toggleAlertOperations(alert: FileServerAlert) {
    if (expandedAlertId === alert.id) {
      setExpandedAlertId(null);
      return;
    }

    if (operationsByAlert[alert.id]) {
      setExpandedAlertId(alert.id);
      return;
    }

    setLoadingAlertId(alert.id);

    try {
      const result = await fetchJson<FileAuditEvent[]>(buildAlertOperationsUrl(alert));
      const filtered = filterAlertOperations(alert, result);
      setOperationsByAlert((current) => ({ ...current, [alert.id]: filtered }));
      setExpandedAlertId(alert.id);
    } catch (error) {
      console.error(error);
      onNotify({ tone: "danger", message: "Nao foi possivel carregar a lista de operações deste alerta." });
    } finally {
      setLoadingAlertId(null);
    }
  }

  return (
    <div className="view-stack">
      <section className="executive-grid">
        <ExecutiveCard
          title="Alertas Abertos"
          value={openAlerts.toLocaleString("pt-BR")}
          detail={`${criticalAlerts} em criticidade alta para tratamento prioritário.`}
          tone={criticalAlerts > 0 ? "danger" : openAlerts > 0 ? "warning" : "neutral"}
        />
        <ExecutiveCard
          title="Regras Ativas"
          value={enabledRules.toLocaleString("pt-BR")}
          detail={`${rules.length.toLocaleString("pt-BR")} regras configuradas na operação.`}
          tone="neutral"
        />
        <ExecutiveCard
          title="Último Alerta"
          value={alerts[0] ? formatDate(alerts[0].createdUtc) : "Sem alerta"}
          detail={alerts[0] ? `${alerts[0].server} · ${alerts[0].title}` : "Nenhum disparo recente registrado."}
          tone={alerts[0]?.severity === "critical" ? "danger" : "neutral"}
        />
      </section>

      {canManageAlerts && (
        <Panel title="Regras de Alerta" subtitle="Ajuste thresholds, escopo, exceções e janelas operacionais sem sair da tela.">
          <p className="inline-note">Os tipos de alerta já vêm prontos. Aqui você ajusta as regras existentes e o comportamento de cada uma.</p>
          <AlertRulesEditor rules={rules} onChanged={onChanged} onNotify={onNotify} />
        </Panel>
      )}
      <Panel title="Alertas" subtitle="Fila operacional dos itens abertos e já reconhecidos mais recentes.">
        <div className="alert-table">
          {alerts.map((alert) => (
            <article className="alert-row" key={alert.id}>
              <div className="alert-main">
                <span className={`badge ${alert.severity}`}>{alert.severity}</span>
                <strong>{alert.title}</strong>
                <p>{alert.description}</p>
                <small>
                  {alert.server} · {alert.user} · {formatDate(alert.createdUtc)}
                </small>
                <div className="alert-links">
                  <button className="text-button subtle-button" type="button" onClick={() => toggleAlertOperations(alert)}>
                    {loadingAlertId === alert.id ? "Carregando operações..." : expandedAlertId === alert.id ? "Ocultar operações" : "Ver operações"}
                  </button>
                </div>
                {expandedAlertId === alert.id && (
                  <AlertOperationList
                    alert={alert}
                    events={operationsByAlert[alert.id] ?? []}
                    loading={loadingAlertId === alert.id}
                  />
                )}
              </div>
              <div className="alert-actions">
                <span className={`status ${alert.status}`}>{alert.status}</span>
                {canManageAlerts && alert.status === "open" && (
                  <button className="text-button" onClick={() => acknowledgeAlert(alert.id, onAcknowledge, onNotify)}>
                    <CheckCircle2 size={16} />
                    Reconhecer
                  </button>
                )}
              </div>
            </article>
          ))}
          {alerts.length === 0 && <EmptyState text="Nenhum alerta encontrado." />}
        </div>
      </Panel>
    </div>
  );
}

function AlertOperationList({
  alert,
  events,
  loading
}: {
  alert: FileServerAlert;
  events: FileAuditEvent[];
  loading: boolean;
}) {
  if (loading) {
    return <div className="alert-operations"><small>Carregando operações relacionadas...</small></div>;
  }

  return (
    <div className="alert-operations">
      <strong>Operações relacionadas</strong>
      {alert.samplePaths.length > 0 && (
        <small className="alert-operation-targets">
          {alert.samplePaths.slice(0, 3).join(" · ")}
        </small>
      )}
      {events.length === 0 ? (
        <small>Nenhuma operação adicional encontrada no recorte deste alerta.</small>
      ) : (
        <ul className="alert-operation-list">
          {events.map((event) => (
            <li key={event.id}>
              <div className="alert-operation-head">
                <span className="pill">{event.action}</span>
                <strong>{formatDate(event.timestampUtc)}</strong>
              </div>
              <p title={event.path}>{event.path}</p>
              <small>
                {event.objectType} · {event.user} · {formatSource(event)} · {event.processName ?? "processo não informado"}
              </small>
              {event.previousPath && <small>Anterior: {event.previousPath}</small>}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function AlertRulesEditor({
  rules,
  onChanged,
  onNotify
}: {
  rules: AlertRuleConfig[];
  onChanged: () => void;
  onNotify: (notice: Notice | null) => void;
}) {
  const [drafts, setDrafts] = useState<Record<string, AlertRuleConfig>>({});
  const [savingRule, setSavingRule] = useState<string | null>(null);
  const [simulatingRule, setSimulatingRule] = useState<string | null>(null);
  const [simulation, setSimulation] = useState<AlertRuleSimulationResponse | null>(null);

  useEffect(() => {
    setDrafts(Object.fromEntries(rules.map((rule) => [rule.rule, rule])));
  }, [rules]);

  const items = rules.map((rule) => drafts[rule.rule] ?? rule);

  return (
    <div className="table-wrap">
      <table>
        <thead>
          <tr>
            <th>Regra</th>
            <th>Ativa</th>
            <th>Severidade</th>
            <th>Threshold</th>
            <th>Threshold 2</th>
            <th>Severidade 2</th>
            <th>Servidor</th>
            <th>Share</th>
            <th>Path prefix</th>
            <th>Hora início</th>
            <th>Hora fim</th>
            <th>Dias</th>
            <th>Ignorar usuários</th>
            <th>Ignorar hosts</th>
            <th>Ignorar processos</th>
            <th>Fuso</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {items.map((rule) => (
            <tr key={rule.rule}>
              <td>
                <strong>{rule.title}</strong>
                <span className="muted-id">{rule.rule}</span>
              </td>
              <td>
                <input
                  type="checkbox"
                  checked={rule.enabled}
                  onChange={(event) => setDrafts({ ...drafts, [rule.rule]: { ...rule, enabled: event.target.checked } })}
                />
              </td>
              <td>
                <select value={rule.severity} onChange={(event) => setDrafts({ ...drafts, [rule.rule]: { ...rule, severity: event.target.value } })}>
                  <option value="warning">warning</option>
                  <option value="high">high</option>
                  <option value="critical">critical</option>
                </select>
              </td>
              <td>
                <input
                  type="number"
                  min={1}
                  value={rule.threshold ?? ""}
                  onChange={(event) => setDrafts({ ...drafts, [rule.rule]: { ...rule, threshold: event.target.value ? Number(event.target.value) : null } })}
                />
              </td>
              <td>
                <input
                  type="number"
                  min={1}
                  value={rule.secondaryThreshold ?? ""}
                  onChange={(event) => setDrafts({ ...drafts, [rule.rule]: { ...rule, secondaryThreshold: event.target.value ? Number(event.target.value) : null } })}
                />
              </td>
              <td>
                <select
                  value={rule.secondarySeverity ?? ""}
                  onChange={(event) => setDrafts({ ...drafts, [rule.rule]: { ...rule, secondarySeverity: event.target.value || null } })}
                >
                  <option value="">-</option>
                  <option value="high">high</option>
                  <option value="critical">critical</option>
                </select>
              </td>
              <td>
                <input
                  value={rule.serverFilter ?? ""}
                  onChange={(event) => setDrafts({ ...drafts, [rule.rule]: { ...rule, serverFilter: event.target.value || null } })}
                  placeholder="FileServer"
                />
              </td>
              <td>
                <input
                  value={rule.shareFilter ?? ""}
                  onChange={(event) => setDrafts({ ...drafts, [rule.rule]: { ...rule, shareFilter: event.target.value || null } })}
                  placeholder="Departamentos"
                />
              </td>
              <td>
                <input
                  value={rule.pathFilter ?? ""}
                  onChange={(event) => setDrafts({ ...drafts, [rule.rule]: { ...rule, pathFilter: event.target.value || null } })}
                  placeholder="D:\\Shares\\Financeiro"
                />
              </td>
              <td>
                <input
                  type="number"
                  min={0}
                  max={23}
                  value={rule.activeFromHour ?? ""}
                  onChange={(event) => setDrafts({ ...drafts, [rule.rule]: { ...rule, activeFromHour: event.target.value ? Number(event.target.value) : null } })}
                  placeholder="19"
                />
              </td>
              <td>
                <input
                  type="number"
                  min={0}
                  max={23}
                  value={rule.activeToHour ?? ""}
                  onChange={(event) => setDrafts({ ...drafts, [rule.rule]: { ...rule, activeToHour: event.target.value ? Number(event.target.value) : null } })}
                  placeholder="7"
                />
              </td>
              <td>
                <input
                  value={rule.activeDays ?? ""}
                  onChange={(event) => setDrafts({ ...drafts, [rule.rule]: { ...rule, activeDays: event.target.value || null } })}
                  placeholder="seg,ter,qua,qui,sex"
                />
              </td>
              <td>
                <input
                  value={rule.excludedUsers ?? ""}
                  onChange={(event) => setDrafts({ ...drafts, [rule.rule]: { ...rule, excludedUsers: event.target.value || null } })}
                  placeholder="svc_backup,svc_antivirus"
                />
              </td>
              <td>
                <input
                  value={rule.excludedHosts ?? ""}
                  onChange={(event) => setDrafts({ ...drafts, [rule.rule]: { ...rule, excludedHosts: event.target.value || null } })}
                  placeholder="WKS-ADM-01,SRV-BKP-01"
                />
              </td>
              <td>
                <input
                  value={rule.excludedProcesses ?? ""}
                  onChange={(event) => setDrafts({ ...drafts, [rule.rule]: { ...rule, excludedProcesses: event.target.value || null } })}
                  placeholder="robocopy.exe,veeamagent.exe"
                />
              </td>
              <td>
                <input
                  value={rule.timeZoneId ?? ""}
                  onChange={(event) => setDrafts({ ...drafts, [rule.rule]: { ...rule, timeZoneId: event.target.value || null } })}
                  placeholder="America/Maceio"
                />
              </td>
              <td className="row-actions">
                <div className="row-button-stack">
                  <button
                    className="text-button"
                    disabled={savingRule === rule.rule}
                    onClick={() => updateAlertRule(rule, setSavingRule, onChanged, onNotify)}
                  >
                    {savingRule === rule.rule ? "Salvando..." : "Salvar"}
                  </button>
                  <button
                    className="text-button"
                    disabled={simulatingRule === rule.rule}
                    onClick={() => simulateAlertRule(rule.rule, setSimulatingRule, setSimulation, onNotify)}
                  >
                    {simulatingRule === rule.rule ? "Simulando..." : "Simular"}
                  </button>
                </div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {items.length === 0 && <EmptyState text="Nenhuma regra de alerta disponível." />}
      {simulation && (
        <div className="simulation-panel">
          <strong>{simulation.title}</strong>
          <small>
            {formatDate(simulation.fromUtc)} até {formatDate(simulation.toUtc)} · {simulation.evaluatedEvents} eventos avaliados ·{" "}
            {simulation.matchingEvents} eventos aderentes · {simulation.alertCount} alertas simulados
          </small>
          <AlertList alerts={simulation.alerts.slice(0, 5)} />
        </div>
      )}
    </div>
  );
}

function AgentsView({
  agents,
  timeline
}: {
  agents: AgentHealth[];
  timeline: TimelineMaterializationMetrics | null;
}) {
  const attentionAgents = agents.filter((agent) => agent.operationalStatus === "attention").length;
  const criticalAgents = agents.filter((agent) => agent.operationalStatus === "critical" || agent.isStale).length;
  const staleAgents = attentionAgents + criticalAgents;
  const queuedAgents = agents.filter((agent) => agent.pendingQueueEvents > 0).length;
  const cycleErrors = agents.filter((agent) => agent.hasCycleError || agent.lastCycle?.error).length;
  const lastHeartbeat = agents
    .map((agent) => agent.lastHeartbeatUtc)
    .filter(Boolean)
    .sort((left, right) => String(right).localeCompare(String(left)))[0];

  return (
    <div className="view-stack">
      <section className="executive-grid">
        <ExecutiveCard
          title="Agentes Visíveis"
          value={agents.length.toLocaleString("pt-BR")}
          detail="Heartbeat recebido no ambiente monitorado."
          tone="neutral"
        />
        <ExecutiveCard
          title="Agentes em Atenção"
          value={staleAgents.toLocaleString("pt-BR")}
          detail={`${attentionAgents} atenção · ${criticalAgents} críticos · ${queuedAgents} com fila.`}
          tone={criticalAgents > 0 ? "danger" : attentionAgents > 0 || queuedAgents > 0 ? "warning" : "neutral"}
        />
        <ExecutiveCard
          title="Erros de Ciclo"
          value={cycleErrors.toLocaleString("pt-BR")}
          detail="Última varredura com erro de coleta, correlação ou envio."
          tone={cycleErrors > 0 ? "danger" : "neutral"}
        />
        <ExecutiveCard
          title="Último Heartbeat"
          value={lastHeartbeat ? formatDate(lastHeartbeat) : "Sem registro"}
          detail="Ajuda a perceber rapidamente se a coleta está respirando."
          tone={lastHeartbeat ? "neutral" : "warning"}
        />
      </section>

      <Panel
        title="Core e timeline"
        subtitle="Estado da fila persistente que transforma eventos brutos em dados prontos para consulta."
      >
        {timeline ? (
          <div className="timeline-runtime">
            <div className="timeline-runtime-heading">
              <div>
                <span>Materialização</span>
                <strong>{timeline.provider}</strong>
              </div>
              <span className={`status ${timeline.status}`}>{formatRuntimeStatus(timeline.status)}</span>
            </div>
            <dl className="timeline-runtime-grid">
              <div>
                <dt>Jobs</dt>
                <dd>{formatNullableCount(timeline.totalJobs)}</dd>
              </div>
              <div>
                <dt>Pendentes</dt>
                <dd>{formatNullableCount(timeline.pendingJobs)}</dd>
              </div>
              <div>
                <dt>Processando</dt>
                <dd>{formatNullableCount(timeline.processingJobs)}</dd>
              </div>
              <div>
                <dt>Em repetição</dt>
                <dd className={timeline.retryingJobs ? "queue-warning" : ""}>{formatNullableCount(timeline.retryingJobs)}</dd>
              </div>
              <div>
                <dt>Maior espera</dt>
                <dd>{timeline.oldestJobAgeSeconds === null ? "Sem fila" : formatElapsedSeconds(timeline.oldestJobAgeSeconds)}</dd>
              </div>
              <div>
                <dt>Maior tentativa</dt>
                <dd>{formatNullableCount(timeline.maxAttemptCount)}</dd>
              </div>
              <div>
                <dt>Consulta</dt>
                <dd>{formatDurationMs(timeline.queryDurationMs)}</dd>
              </div>
            </dl>
            {(timeline.lastError || timeline.error) && (
              <p className="timeline-runtime-error">
                <AlertTriangle size={16} />
                <span>
                  <strong>Último erro</strong>
                  {timeline.lastError ?? timeline.error}
                </span>
              </p>
            )}
          </div>
        ) : (
          <EmptyState text="Aguardando os sinais operacionais do Core." />
        )}
      </Panel>

      <Panel title="Agentes" subtitle="Saúde do coletor, atraso de heartbeat, fila local e progresso no USN.">
        <div className="agent-grid">
          {agents.map((agent) => (
            <article className="agent-card" key={agent.agentId}>
              <div className="agent-header">
                <Server size={20} />
                <div>
                  <strong>{agent.server}</strong>
                  <span>{agent.agentId}</span>
                </div>
              </div>
              <dl>
                <div>
                  <dt>Status operacional</dt>
                  <dd className={`status ${agent.operationalStatus}`}>{formatAgentOperationalStatus(agent.operationalStatus)}</dd>
                </div>
                <div>
                  <dt>Heartbeat</dt>
                  <dd>{formatDateWithAge(agent.lastHeartbeatUtc, agent.lastHeartbeatAgeSeconds)}</dd>
                </div>
                <div>
                  <dt>Serviço</dt>
                  <dd className={`status ${agent.status}`}>{agent.isStale ? "heartbeat atrasado" : agent.status}</dd>
                </div>
                <div>
                  <dt>Último evento</dt>
                  <dd>{formatDateWithAge(agent.lastCollectedEventUtc, agent.lastCollectedEventAgeSeconds)}</dd>
                </div>
                <div>
                  <dt>Fila</dt>
                  <dd className={agent.pendingQueueEvents > 0 ? "queue-warning" : ""}>
                    {agent.pendingQueueEvents >= 0 ? agent.pendingQueueEvents : "indisponível"}
                  </dd>
                </div>
                <div>
                  <dt>Último envio</dt>
                  <dd>{formatDateWithAge(agent.lastSuccessfulSendUtc, agent.lastSuccessfulSendAgeSeconds) || "Sem envio"}</dd>
                </div>
                <div>
                  <dt>Security cursor</dt>
                  <dd>{agent.lastRecordId}</dd>
                </div>
                <div>
                  <dt>USN</dt>
                  <dd>{formatUsn(agent.lastUsnByVolume)}</dd>
                </div>
              </dl>
              {agent.lastCycle && (
                <div className="agent-cycle">
                  <strong>Última varredura</strong>
                  <div>
                    <span>Security</span>
                    <b>{agent.lastCycle.securityEventsRead.toLocaleString("pt-BR")}</b>
                  </div>
                  <div>
                    <span>USN</span>
                    <b>{agent.lastCycle.usnEventsRead.toLocaleString("pt-BR")}</b>
                  </div>
                  <div>
                    <span>Correlacionados</span>
                    <b>{agent.lastCycle.correlatedEvents.toLocaleString("pt-BR")}</b>
                  </div>
                  <div>
                    <span>Enviados</span>
                    <b>{agent.lastCycle.sentEvents.toLocaleString("pt-BR")}</b>
                  </div>
                  <div>
                    <span>Fila gerada</span>
                    <b>{agent.lastCycle.queuedEvents.toLocaleString("pt-BR")}</b>
                  </div>
                  <div>
                    <span>Duração</span>
                    <b>{formatDurationMs(agent.lastCycle.durationMs)}</b>
                  </div>
                </div>
              )}
              {(agent.operationalMessage || agent.message || agent.lastCycle?.error) && (
                <p className="agent-message">{agent.lastCycle?.error ?? agent.operationalMessage ?? agent.message}</p>
              )}
            </article>
          ))}
          {agents.length === 0 && <EmptyState text="Nenhum agente reportou heartbeat ainda." />}
        </div>
      </Panel>
    </div>
  );
}

function MonitoredPathsView({
  paths,
  canManagePaths,
  onChanged,
  onNotify
}: {
  paths: MonitoredPath[];
  canManagePaths: boolean;
  onChanged: () => void;
  onNotify: (notice: Notice | null) => void;
}) {
  const [form, setForm] = useState<MonitoredPathForm>(emptyMonitoredPathForm);
  const [saving, setSaving] = useState(false);
  const [editingPathId, setEditingPathId] = useState<string | null>(null);
  const [drafts, setDrafts] = useState<Record<string, MonitoredPath>>({});
  const activePaths = paths.filter((path) => path.status === "active").length;
  const criticalPaths = paths.filter((path) => ["high", "critical"].includes(path.priority)).length;

  useEffect(() => {
    setDrafts(Object.fromEntries(paths.map((path) => [path.id, path])));
  }, [paths]);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setSaving(true);

    try {
      const response = await fetch(`${apiBaseUrl}/api/monitored-paths`, {
        method: "POST",
        headers: buildJsonHeaders(),
        body: JSON.stringify(form)
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response, "Nao foi possivel salvar o caminho monitorado."));
      }

      setForm(emptyMonitoredPathForm);
      onChanged();
      onNotify({ tone: "success", message: "Caminho monitorado adicionado com sucesso." });
    } catch (error) {
      onNotify({ tone: "danger", message: error instanceof Error ? error.message : "Nao foi possivel salvar o caminho monitorado." });
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="view-stack">
      <section className="executive-grid">
        <ExecutiveCard
          title="Caminhos Cadastrados"
          value={paths.length.toLocaleString("pt-BR")}
          detail={`${activePaths} ativos no recorte atual.`}
          tone="neutral"
        />
        <ExecutiveCard
          title="Prioridade Elevada"
          value={criticalPaths.toLocaleString("pt-BR")}
          detail="Itens que merecem onboarding e validação com mais cuidado."
          tone={criticalPaths > 0 ? "warning" : "neutral"}
        />
        <ExecutiveCard
          title="Cobertura Atual"
          value={paths[0]?.server ?? "Sem servidor"}
          detail={paths[0] ? "Use a lista abaixo para revisar status e criticidade." : "Cadastre o primeiro share ou pasta crítica."}
          tone="neutral"
        />
      </section>

      {canManagePaths && (
        <Panel title="Novo Caminho Monitorado" subtitle="Cadastre shares e pastas críticas pensando em prioridade, dono e fase do rollout.">
          <form className="path-form" onSubmit={submit}>
            <label>
              Servidor
              <input value={form.server} onChange={(event) => setForm({ ...form, server: event.target.value })} />
            </label>
            <label>
              Share
              <input value={form.share} onChange={(event) => setForm({ ...form, share: event.target.value })} placeholder="Departamentos" />
            </label>
            <label className="wide-field">
              Caminho raiz
              <input value={form.path} onChange={(event) => setForm({ ...form, path: event.target.value })} placeholder="D:\\Shares\\Departamentos\\Financeiro" />
            </label>
            <label>
              Status
              <select value={form.status} onChange={(event) => setForm({ ...form, status: event.target.value })}>
                <option value="planned">Planejado</option>
                <option value="active">Ativo</option>
                <option value="paused">Pausado</option>
                <option value="retired">Retirado</option>
              </select>
            </label>
            <label>
              Prioridade
              <select value={form.priority} onChange={(event) => setForm({ ...form, priority: event.target.value })}>
                <option value="low">Baixa</option>
                <option value="normal">Normal</option>
                <option value="high">Alta</option>
                <option value="critical">Crítica</option>
              </select>
            </label>
            <label>
              Responsável
              <input value={form.owner} onChange={(event) => setForm({ ...form, owner: event.target.value })} placeholder="Infra / área dona" />
            </label>
            <label className="wide-field">
              Observações
              <input value={form.notes} onChange={(event) => setForm({ ...form, notes: event.target.value })} placeholder="Fase do piloto, exceções ou janela de implantação" />
            </label>
            <button className="text-button path-submit" type="submit" disabled={saving}>
              <Plus size={16} />
              {saving ? "Adicionando..." : "Adicionar"}
            </button>
          </form>
        </Panel>
      )}

      <Panel title="Caminhos Cadastrados" subtitle="Inventário operacional do que já entrou ou ainda vai entrar no monitoramento.">
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Servidor</th>
                <th>Share</th>
                <th>Status</th>
                <th>Prioridade</th>
                <th>Caminho</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {paths.map((path) => (
                <tr key={path.id}>
                  <td>{path.server}</td>
                  <td>{path.share}</td>
                  <td>
                    <select
                      disabled={!canManagePaths}
                      value={(drafts[path.id] ?? path).status}
                      onChange={(event) => setDrafts({
                        ...drafts,
                        [path.id]: { ...(drafts[path.id] ?? path), status: event.target.value }
                      })}
                    >
                      <option value="planned">Planejado</option>
                      <option value="active">Ativo</option>
                      <option value="paused">Pausado</option>
                      <option value="retired">Retirado</option>
                    </select>
                  </td>
                  <td>
                    <select
                      disabled={!canManagePaths}
                      value={(drafts[path.id] ?? path).priority}
                      onChange={(event) => setDrafts({
                        ...drafts,
                        [path.id]: { ...(drafts[path.id] ?? path), priority: event.target.value }
                      })}
                    >
                      <option value="low">Baixa</option>
                      <option value="normal">Normal</option>
                      <option value="high">Alta</option>
                      <option value="critical">Crítica</option>
                    </select>
                  </td>
                  <td className="path-cell" title={path.path}>{path.path}</td>
                  <td className="row-actions">
                    {canManagePaths && (
                      <div className="row-button-stack">
                        <button
                          className="text-button"
                          type="button"
                          disabled={editingPathId === path.id}
                          onClick={() => updateMonitoredPath(drafts[path.id] ?? path, setEditingPathId, onChanged, onNotify)}
                        >
                          {editingPathId === path.id ? "Salvando..." : "Salvar"}
                        </button>
                        <button className="icon-button subtle" onClick={() => deleteMonitoredPath(path.id, onChanged, onNotify)} title="Remover caminho">
                          <Trash2 size={16} />
                        </button>
                      </div>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {paths.length === 0 && <EmptyState text="Nenhum caminho monitorado cadastrado." />}
        </div>
      </Panel>
    </div>
  );
}

function InventoryGovernanceView({ onNotify }: { onNotify: (notice: Notice | null) => void }) {
  const [summary, setSummary] = useState<InventorySummary | null>(null);
  const [snapshots, setSnapshots] = useState<InventorySnapshot[]>([]);
  const [investigationKind, setInvestigationKind] = useState("executable");
  const [investigationItems, setInvestigationItems] = useState<InventoryItem[]>([]);
  const [loadingInvestigation, setLoadingInvestigation] = useState(false);
  const [loading, setLoading] = useState(false);
  const [requestingScan, setRequestingScan] = useState(false);
  const [activeInventoryTab, setActiveInventoryTab] = useState<"overview" | "capacity" | "cleanup" | "risk" | "activity" | "snapshots">("overview");

  async function loadInventory() {
    setLoading(true);
    try {
      const [nextSummary, nextSnapshots] = await Promise.all([
        fetchJson<InventorySummary>("/api/inventory/summary"),
        fetchJson<InventorySnapshot[]>("/api/inventory/snapshots?take=10")
      ]);
      setSummary(nextSummary);
      setSnapshots(nextSnapshots);
    } catch (error) {
      onNotify({ tone: "danger", message: error instanceof Error ? error.message : "Nao foi possivel carregar inventario." });
    } finally {
      setLoading(false);
    }
  }

  async function loadInventoryItems(kind = investigationKind) {
    setInvestigationKind(kind);
    setLoadingInvestigation(true);
    try {
      const params = new URLSearchParams({ kind, take: "100" });
      const items = await fetchJson<InventoryItem[]>(`/api/inventory/items?${params.toString()}`);
      setInvestigationItems(items);
    } catch (error) {
      onNotify({ tone: "danger", message: error instanceof Error ? error.message : "Nao foi possivel carregar achados do inventario." });
    } finally {
      setLoadingInvestigation(false);
    }
  }

  useEffect(() => {
    void loadInventory();
  }, []);

  useEffect(() => {
    if (summary?.snapshotId) {
      void loadInventoryItems("executable");
    }
  }, [summary?.snapshotId]);

  async function requestScanNow() {
    setRequestingScan(true);
    try {
      const response = await fetch(`${apiBaseUrl}/api/inventory/scan-now`, {
        method: "POST",
        headers: buildJsonHeaders()
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response, "Nao foi possivel solicitar o scan."));
      }

      onNotify({ tone: "success", message: "Scan de inventario solicitado ao agente." });
      await loadInventory();
      window.setTimeout(() => {
        void loadInventory();
      }, 75_000);
    } catch (error) {
      onNotify({ tone: "danger", message: error instanceof Error ? error.message : "Nao foi possivel solicitar o scan." });
    } finally {
      setRequestingScan(false);
    }
  }

  if (!summary || summary.status === "empty") {
    return (
      <div className="view-stack">
        <Panel title="Inventário Gerencial" subtitle={loading ? "Carregando inventário..." : "Nenhum snapshot de inventário encontrado."}>
          <EmptyState text="Ative o scan de inventário no agente para gerar a primeira visão gerencial do compartilhamento." />
          <div className="toolbar">
            <button className="text-button" type="button" onClick={requestScanNow} disabled={requestingScan || loading}>
              <RefreshCcw size={16} />
              {requestingScan ? "Solicitando..." : "Executar scan agora"}
            </button>
          </div>
        </Panel>
        <InventorySnapshotsPanel snapshots={snapshots} />
      </div>
    );
  }

  const cycleStatusLabel = summary.insight?.trend === "improved"
    ? "Melhorou"
    : summary.insight?.trend === "worsened"
      ? "Piorou"
      : "Manteve";
  const storageShareItems = summary.topFolders
    .filter((item) => item.totalBytes > 0)
    .slice(0, 5)
    .map((item) => ({
      label: item.path,
      value: formatBytes(item.totalBytes),
      detail: `${formatPercent((item.totalBytes / Math.max(summary.totalBytes, 1)) * 100)} do volume monitorado`,
      percent: (item.totalBytes / Math.max(summary.totalBytes, 1)) * 100,
      tone: "navy" as const
    }));
  const governanceRatioItems = [
    {
      label: "Volume frio +365 dias",
      value: formatBytes(summary.governance.inactive365DaysBytes),
      detail: `${summary.governance.inactive365DaysFileCount.toLocaleString("pt-BR")} arquivo(s)`,
      percent: (summary.governance.inactive365DaysBytes / Math.max(summary.totalBytes, 1)) * 100,
      tone: "amber" as const
    },
    {
      label: "Sem acesso observado",
      value: formatBytes(summary.governance.neverAccessedBytes),
      detail: `${summary.governance.neverAccessedFileCount.toLocaleString("pt-BR")} arquivo(s)`,
      percent: (summary.governance.neverAccessedBytes / Math.max(summary.totalBytes, 1)) * 100,
      tone: "blue" as const
    },
    {
      label: "Executáveis e scripts",
      value: summary.governance.executableFileCount.toLocaleString("pt-BR"),
      detail: formatBytes(summary.governance.executableFileBytes),
      percent: Math.min(100, (summary.governance.executableFileCount / Math.max(summary.fileCount, 1)) * 100),
      tone: "danger" as const
    },
    {
      label: "Arquivos acima de 1 GB",
      value: summary.governance.largeFileCount.toLocaleString("pt-BR"),
      detail: formatBytes(summary.governance.largeFileBytes),
      percent: Math.min(100, (summary.governance.largeFileCount / Math.max(summary.fileCount, 1)) * 100),
      tone: "green" as const
    }
  ];
  const largestFolder = summary.topFolders[0];
  const mostActiveFolder = summary.observedActivity.topFolders[0];
  const mostActiveUser = summary.observedActivity.topUsers[0];
  const summarySnapshot = snapshots.find((snapshot) => snapshot.id === summary.snapshotId);
  const latestCompletedSnapshot = snapshots.find((snapshot) => snapshot.status === "completed" || snapshot.status === "completed_with_errors");
  const ignoredLatestSnapshot = latestCompletedSnapshot && latestCompletedSnapshot.id !== summary.snapshotId
    ? latestCompletedSnapshot
    : null;
  const coldVolumePercent = (summary.governance.inactive365DaysBytes / Math.max(summary.totalBytes, 1)) * 100;
  const neverAccessedPercent = (summary.governance.neverAccessedBytes / Math.max(summary.totalBytes, 1)) * 100;
  const capacityHotspotPercent = largestFolder ? (largestFolder.totalBytes / Math.max(summary.totalBytes, 1)) * 100 : 0;
  const decisionLanes = [
    {
      title: "Capacidade",
      label: largestFolder ? largestFolder.path : "Sem pasta dominante",
      value: largestFolder ? formatBytes(largestFolder.totalBytes) : "0 B",
      detail: largestFolder
        ? `${formatPercent(capacityHotspotPercent)} do volume no maior ponto de concentração`
        : "Execute um scan para formar ranking de capacidade.",
      tone: capacityHotspotPercent >= 40 ? "amber" : "blue",
      progress: capacityHotspotPercent
    },
    {
      title: "Limpeza",
      label: "Arquivos frios e sem acesso",
      value: formatBytes(summary.governance.inactive365DaysBytes + summary.governance.neverAccessedBytes),
      detail: `${summary.governance.inactive365DaysFileCount.toLocaleString("pt-BR")} frio(s) + ${summary.governance.neverAccessedFileCount.toLocaleString("pt-BR")} sem acesso observado`,
      tone: coldVolumePercent + neverAccessedPercent >= 35 ? "danger" : coldVolumePercent + neverAccessedPercent > 0 ? "amber" : "green",
      progress: Math.min(100, coldVolumePercent + neverAccessedPercent)
    },
    {
      title: "Risco",
      label: "Executáveis, scripts e erros",
      value: (summary.governance.executableFileCount + summary.errorCount).toLocaleString("pt-BR"),
      detail: `${formatBytes(summary.governance.executableFileBytes)} em itens executáveis · ${summary.errorCount.toLocaleString("pt-BR")} erro(s) de scan`,
      tone: summary.governance.executableFileCount > 0 || summary.errorCount > 0 ? "danger" : "green",
      progress: Math.min(100, ((summary.governance.executableFileCount + summary.errorCount) / Math.max(summary.fileCount + summary.folderCount, 1)) * 100)
    },
    {
      title: "Uso",
      label: mostActiveFolder ? mostActiveFolder.path : "Sem atividade recente",
      value: summary.observedActivity.totalEvents.toLocaleString("pt-BR"),
      detail: mostActiveUser
        ? `${mostActiveUser.user} lidera com ${mostActiveUser.eventCount.toLocaleString("pt-BR")} evento(s)`
        : "Sem atividade correlacionada no período observado.",
      tone: summary.observedActivity.totalEvents > 0 ? "blue" : "green",
      progress: mostActiveFolder ? Math.min(100, (mostActiveFolder.eventCount / Math.max(summary.observedActivity.totalEvents, 1)) * 100) : 0
    }
  ] as const;

  return (
    <div className="inventory-workspace">
      <section className="inventory-overview" aria-label="Resumo do inventário">
        <div className="inventory-overview-main">
          <div className="inventory-overview-title">
            <span className="inventory-kicker"><HardDrive size={16} /> Compartilhamento monitorado</span>
            <h2>{summary.share ?? "Inventário de arquivos"}</h2>
            <p>{summary.rootPath ?? "Raiz não informada"}</p>
          </div>
          <div className="inventory-overview-badges">
            <span className="badge info"><Server size={14} /> {summary.server ?? "Servidor não informado"}</span>
            <span className={`badge ${summary.errorCount > 0 ? "warning" : "success"}`}>
              {summary.errorCount > 0 ? `${summary.errorCount} erro(s) no scan` : "Leitura sem erros"}
            </span>
            {ignoredLatestSnapshot && (
              <span className="badge warning" title={`Scan de ${formatDate(ignoredLatestSnapshot.startedUtc)} tinha ${ignoredLatestSnapshot.fileCount.toLocaleString("pt-BR")} arquivo(s) e ${formatBytes(ignoredLatestSnapshot.totalBytes)}.`}>
                Snapshot vazio ignorado
              </span>
            )}
            <span className={`badge ${summary.growth.totalBytesDelta > 0 ? "warning" : "success"}`}>
              {summary.growth.totalBytesDelta > 0 ? `Crescimento ${formatSignedBytes(summary.growth.totalBytesDelta)}` : `Variação ${formatSignedBytes(summary.growth.totalBytesDelta)}`}
            </span>
          </div>
          <div className="inventory-snapshot-note">
            <CheckCircle2 size={15} />
            <span>
              Inventário usando snapshot de {summary.finishedUtc ? formatDate(summary.finishedUtc) : "scan em andamento"}
              {summarySnapshot ? ` · ${summarySnapshot.fileCount.toLocaleString("pt-BR")} arquivo(s), ${summarySnapshot.folderCount.toLocaleString("pt-BR")} pasta(s)` : ""}.
              {ignoredLatestSnapshot ? ` O scan mais recente (${formatDate(ignoredLatestSnapshot.startedUtc)}) foi ignorado por não ter conteúdo suficiente para leitura gerencial.` : ""}
            </span>
          </div>
          <div className="inventory-overview-actions">
            <button className="text-button" type="button" onClick={loadInventory} disabled={loading}>
              <RefreshCcw size={16} />
              {loading ? "Atualizando..." : "Atualizar dados"}
            </button>
            <button className="primary-button" type="button" onClick={requestScanNow} disabled={requestingScan || loading}>
              <ScanLine size={16} />
              {requestingScan ? "Solicitando..." : "Executar scan"}
            </button>
          </div>
        </div>

        <div className="inventory-capacity">
          <span>Espaço catalogado</span>
          <strong>{formatBytes(summary.totalBytes)}</strong>
          <small>{summary.fileCount.toLocaleString("pt-BR")} arquivos em {summary.folderCount.toLocaleString("pt-BR")} pastas</small>
        </div>

        <dl className="inventory-scan-meta">
          <div>
            <dt>Último scan</dt>
            <dd>{summary.finishedUtc ? formatDate(summary.finishedUtc) : "Em andamento"}</dd>
          </div>
          <div>
            <dt>Snapshot atual</dt>
            <dd>{summary.snapshotId ? summary.snapshotId.slice(0, 8).toUpperCase() : "-"}</dd>
          </div>
          <div>
            <dt>Status</dt>
            <dd>{summary.status === "ready" ? "Catálogo disponível" : summary.status}</dd>
          </div>
          <div>
            <dt>Atividade observada</dt>
            <dd>{summary.observedActivity.totalEvents.toLocaleString("pt-BR")} eventos</dd>
          </div>
        </dl>
      </section>

      <div className="inventory-tabs" role="tablist" aria-label="Navegação do inventário">
        {[
          ["overview", "Visão geral"],
          ["capacity", "Capacidade"],
          ["cleanup", "Limpeza"],
          ["risk", "Risco"],
          ["activity", "Atividade"],
          ["snapshots", "Snapshots"]
        ].map(([id, label]) => (
          <button
            key={id}
            className={activeInventoryTab === id ? "active" : ""}
            type="button"
            onClick={() => setActiveInventoryTab(id as typeof activeInventoryTab)}
          >
            {label}
          </button>
        ))}
      </div>

      <div className={`inventory-tabbed-content inventory-tab-${activeInventoryTab}`}>
      <section className="inventory-section inventory-pane-overview" aria-label="Leitura executiva qbr">
        <div className="inventory-section-header">
          <div>
            <span className="inventory-section-kicker">Leitura executiva</span>
            <h3>Quadro para acompanhar evolução, concentração e governança</h3>
          </div>
          <p>Essa camada é pensada para gestão contínua: volume, risco, concentração e ação prioritária numa leitura única.</p>
        </div>

        <div className="inventory-qbr-grid">
          <article className={`inventory-hero-card ${summary.insight.tone}`}>
            <div className="inventory-hero-head">
              <span className="inventory-kicker">QBR contínuo</span>
              <span className={`status ${summary.insight.trend === "worsened" ? "critical" : summary.insight.trend === "improved" ? "ok" : "attention"}`}>{cycleStatusLabel}</span>
            </div>
            <strong>{summary.insight.score}</strong>
            <p>
              {summary.insight.score >= 85
                ? "Ambiente bem organizado, com leitura operacional saudável e pouca pressão de revisão."
                : summary.insight.score >= 65
                  ? "Ambiente sob controle, mas já com sinais claros de limpeza, revisão ou endurecimento."
                  : "Ambiente pedindo ação mais rápida para evitar crescimento desordenado ou risco operacional."}
            </p>
            <div className="inventory-hero-points">
              {summary.executiveOverview.headlines.slice(0, 3).map((headline) => (
                <div key={headline}>
                  <CheckCircle2 size={15} />
                  <span>{headline}</span>
                </div>
              ))}
            </div>
          </article>

          <Panel title="Concentração do volume" subtitle="Onde o armazenamento está mais concentrado dentro do recorte atual.">
            <InventoryRatioList items={storageShareItems} />
          </Panel>

          <Panel title="Pressão de governança" subtitle="Quanto do ambiente já sinaliza frio, baixa rastreabilidade ou itens sensíveis.">
            <InventoryRatioList items={governanceRatioItems} />
          </Panel>
        </div>
      </section>

      <section className="inventory-section inventory-pane-overview" aria-label="Matriz de decisão gerencial">
        <div className="inventory-section-header">
          <div>
            <span className="inventory-section-kicker">Matriz de decisão</span>
            <h3>O que atacar, acompanhar ou levar para o próximo ciclo</h3>
          </div>
          <p>Quatro leituras rápidas para transformar inventário em plano de ação: capacidade, limpeza, risco e uso real.</p>
        </div>

        <div className="inventory-decision-grid">
          {decisionLanes.map((item) => (
            <article key={item.title} className={`inventory-decision-card ${item.tone}`}>
              <div className="inventory-decision-head">
                <span>{item.title}</span>
                <strong>{item.value}</strong>
              </div>
              <p title={item.label}>{item.label}</p>
              <small>{item.detail}</small>
              <div className="inventory-decision-bar" aria-hidden="true">
                <i style={{ width: `${Math.max(4, Math.min(100, item.progress))}%` }} />
              </div>
            </article>
          ))}
        </div>
      </section>

      <section className="inventory-section inventory-pane-overview" aria-label="Panorama executivo">
        <div className="inventory-section-header">
          <div>
            <span className="inventory-section-kicker">Panorama executivo</span>
            <h3>Leitura rápida do ambiente monitorado</h3>
          </div>
          <p>Um retrato do tamanho, atividade e sinais de atenção do compartilhamento.</p>
        </div>
        <div className="inventory-kpi-grid">
          <InventoryKpiCard
            icon={<Database size={18} />}
            label="Dados catalogados"
            value={formatBytes(summary.totalBytes)}
            detail={`${summary.topFolders.length.toLocaleString("pt-BR")} áreas no ranking principal`}
            tone="navy"
          />
          <InventoryKpiCard
            icon={<Files size={18} />}
            label="Arquivos monitorados"
            value={summary.fileCount.toLocaleString("pt-BR")}
            detail={`${summary.folderCount.toLocaleString("pt-BR")} pastas registradas`}
            tone="blue"
          />
          <InventoryKpiCard
            icon={<BarChart3 size={18} />}
            label="Crescimento recente"
            value={formatSignedBytes(summary.growth.totalBytesDelta)}
            detail={`${formatSignedNumber(summary.growth.fileCountDelta)} arquivo(s) vs. snapshot anterior`}
            tone={summary.growth.totalBytesDelta > 0 ? "amber" : "green"}
          />
          <InventoryKpiCard
            icon={<Activity size={18} />}
            label="Atividade observada"
            value={summary.observedActivity.totalEvents.toLocaleString("pt-BR")}
            detail="Eventos reais cruzados com a timeline persistida"
            tone="green"
          />
          <InventoryKpiCard
            icon={<ClipboardList size={18} />}
            label="Recomendações abertas"
            value={summary.recommendations.length.toLocaleString("pt-BR")}
            detail="Achados prontos para revisão ou limpeza"
            tone={summary.recommendations.length > 0 ? "amber" : "green"}
          />
          <InventoryKpiCard
            icon={<ShieldAlert size={18} />}
            label="Itens sensíveis à revisão"
            value={(summary.governance.executableFileCount + summary.governance.largeFileCount).toLocaleString("pt-BR")}
            detail="Executáveis, scripts e arquivos grandes"
            tone="danger"
          />
        </div>
      </section>

      <section className="inventory-section inventory-pane-overview inventory-pane-activity" aria-label="Painel gerencial contínuo">
        <div className="inventory-section-header">
          <div>
            <span className="inventory-section-kicker">Painel gerencial contínuo</span>
            <h3>Resumo executivo pronto para acompanhamento recorrente</h3>
          </div>
          <p>Uma leitura mais próxima de QBR contínuo: onde está o volume, onde está a atividade e o que entrou na fila de decisão.</p>
        </div>

        <div className="inventory-headline-strip">
          {summary.executiveOverview.headlines.length === 0 ? (
            <EmptyState text="Sem destaques executivos suficientes no snapshot atual." />
          ) : (
            summary.executiveOverview.headlines.map((headline) => (
              <article key={headline} className="inventory-headline-pill">
                <CheckCircle2 size={16} />
                <span>{headline}</span>
              </article>
            ))
          )}
        </div>

        <div className="inventory-executive-grid">
          <Panel title="Hotspots de armazenamento" subtitle="Áreas que mais concentram dados no compartilhamento.">
            <InventoryExecutiveList
              items={summary.executiveOverview.storageHotspots}
              getKey={(item) => item.path}
              renderTitle={(item) => item.label}
              renderValue={(item) => item.primaryText}
              renderDetail={(item) => item.secondaryText}
            />
          </Panel>

          <Panel title="Hotspots de atividade" subtitle="Pastas que mais puxaram eventos reais no período recente.">
            <InventoryExecutiveList
              items={summary.executiveOverview.activityHotspots}
              getKey={(item) => item.path}
              renderTitle={(item) => item.label}
              renderValue={(item) => item.primaryText}
              renderDetail={(item) => item.secondaryText}
            />
          </Panel>

          <Panel title="Usuários mais ativos" subtitle="Atores que mais apareceram na atividade correlacionada.">
            <InventoryExecutiveList
              items={summary.executiveOverview.userHotspots}
              getKey={(item) => item.user}
              renderTitle={(item) => item.user}
              renderValue={(item) => item.primaryText}
              renderDetail={(item) => item.secondaryText}
            />
          </Panel>
        </div>

        <Panel title="Fila gerencial de decisão" subtitle="Recomendações prontas para virar plano de ação no próximo ciclo.">
          {summary.executiveOverview.priorities.length === 0 ? (
            <EmptyState text="Sem prioridades abertas no snapshot atual." />
          ) : (
            <div className="inventory-priority-list">
              {summary.executiveOverview.priorities.map((item) => (
                <article key={`${item.title}-${item.detail}`} className={`inventory-priority-item ${item.tone}`}>
                  <div className="inventory-priority-head">
                    <strong>{item.title}</strong>
                    <span className={`status ${item.severity === "warning" ? "critical" : "attention"}`}>{item.severity}</span>
                  </div>
                  <p>{item.detail}</p>
                </article>
              ))}
            </div>
          )}
        </Panel>
      </section>

      <section className="inventory-section inventory-pane-overview" aria-label="Leitura do ciclo">
        <div className="inventory-section-header">
          <div>
            <span className="inventory-section-kicker">Leitura do ciclo</span>
            <h3>Como o compartilhamento está se comportando agora</h3>
          </div>
          <p>Uma leitura curta para dizer se o ambiente melhorou, se manteve ou se pede reação mais rápida.</p>
        </div>
        <div className="inventory-cycle-grid">
          <InventorySignalCard
            title="Score de higiene"
            value={`${summary.insight?.score ?? 0}`}
            caption={summary.insight?.score && summary.insight.score >= 85 ? "ambiente bem organizado" : summary.insight?.score && summary.insight.score >= 65 ? "atenção gerenciável" : "prioridade de revisão"}
            tone={summary.insight?.tone ?? "green"}
            bullets={[
              `${formatPercent((summary.governance.inactive365DaysBytes / Math.max(summary.totalBytes, 1)) * 100)} do volume está frio há +365 dias`,
              `${formatPercent((summary.governance.neverAccessedBytes / Math.max(summary.totalBytes, 1)) * 100)} sem acesso observado`,
              `${summary.recommendations.length.toLocaleString("pt-BR")} recomendação(ões) em aberto`
            ]}
          />
          <InventorySignalCard
            title="Comparação do ciclo"
            value={cycleStatusLabel}
            caption={summary.comparison.previousStartedUtc ? `Comparado ao snapshot de ${formatDate(summary.comparison.previousStartedUtc)}` : "Sem ciclo anterior para comparar"}
            tone={summary.insight?.trend === "improved" ? "green" : summary.insight?.trend === "worsened" ? "danger" : "blue"}
            bullets={[
              summary.comparison.previousStartedUtc ? `Variação de volume: ${formatPercent(summary.comparison.totalBytesGrowthPercent)}` : "Primeiro ciclo comparável ainda não disponível",
              `Delta de arquivos: ${formatSignedNumber(summary.growth.fileCountDelta)}`,
              `Delta de pastas: ${formatSignedNumber(summary.growth.folderCountDelta)}`
            ]}
          />
          <InventorySignalCard
            title="Melhorou"
            value={`${summary.insight?.positives.length ?? 0}`}
            caption="Sinais positivos identificados"
            tone="green"
            bullets={summary.insight?.positives.length ? summary.insight.positives.slice(0, 3) : ["Sem destaque positivo neste ciclo."]}
          />
          <InventorySignalCard
            title="Manteve"
            value={`${summary.insight?.stables.length ?? 0}`}
            caption="Aspectos sem mudança brusca"
            tone="blue"
            bullets={summary.insight?.stables.length ? summary.insight.stables.slice(0, 3) : ["Nenhum sinal estável destacado neste ciclo."]}
          />
          <InventorySignalCard
            title="Piorou"
            value={`${summary.insight?.attentions.length ?? 0}`}
            caption="Pontos que pedem intervenção"
            tone="danger"
            bullets={summary.insight?.attentions.length ? summary.insight.attentions.slice(0, 3) : ["Nenhum agravamento relevante percebido neste ciclo."]}
          />
        </div>
      </section>

      <section className="inventory-section inventory-pane-cleanup inventory-pane-risk inventory-pane-activity" aria-label="Risco e uso">
        <div className="inventory-section-header">
          <div>
            <span className="inventory-section-kicker">Risco e uso</span>
            <h3>Onde o ambiente pede atenção primeiro</h3>
          </div>
          <p>Os blocos abaixo ajudam a separar volume, idade, uso real e sinais de governança.</p>
        </div>

        <section className="inventory-action-strip" aria-label="Pontos de atenção">
          <InventoryActionMetric icon={<FileClock size={19} />} label="Arquivos frios" value={summary.governance.inactive365DaysFileCount} detail={`${formatBytes(summary.governance.inactive365DaysBytes)} sem alteração há mais de um ano`} tone="warning" />
          <InventoryActionMetric icon={<Clock3 size={19} />} label="Sem registro de acesso" value={summary.governance.neverAccessedFileCount} detail={formatBytes(summary.governance.neverAccessedBytes)} tone="neutral" />
          <InventoryActionMetric icon={<Files size={19} />} label="Arquivos grandes" value={summary.governance.largeFileCount} detail={`${formatBytes(summary.governance.largeFileBytes)} acima de 1 GB`} tone="warning" />
          <InventoryActionMetric icon={<ShieldAlert size={19} />} label="Executáveis e scripts" value={summary.governance.executableFileCount} detail={formatBytes(summary.governance.executableFileBytes)} tone="danger" />
        </section>

        <div className="inventory-analysis-grid">
          <Panel title="Composição do armazenamento" subtitle="Tipos de conteúdo que mais ocupam espaço.">
            <InventoryCategoryChart items={summary.contentCategories} />
          </Panel>
          <Panel title="Ciclo de vida dos arquivos" subtitle="Distribuição pela última modificação conhecida.">
            <InventoryAgeChart buckets={summary.ageBuckets} />
          </Panel>
          <Panel title="Atividade observada" subtitle="Ações correlacionadas nos últimos 30 dias.">
            <div className="inventory-activity-total">
              <Activity size={20} />
              <strong>{summary.observedActivity.totalEvents.toLocaleString("pt-BR")}</strong>
              <span>eventos reais</span>
            </div>
            <InventoryRanking
              items={summary.observedActivity.topFolders}
              getKey={(item) => item.path}
              renderLabel={(item) => item.path}
              renderValue={(item) => `${item.eventCount.toLocaleString("pt-BR")}`}
              renderDetail={(item) => `${item.topAction} · ${formatDate(item.lastActivityUtc)}`}
              maxValue={Math.max(...summary.observedActivity.topFolders.map((item) => item.eventCount), 1)}
              getBarValue={(item) => item.eventCount}
            />
          </Panel>
        </div>
      </section>

      <section className="inventory-section inventory-pane-cleanup inventory-pane-risk" aria-label="Prioridades de revisão">
        <div className="inventory-section-header">
          <div>
            <span className="inventory-section-kicker">Prioridades de revisão</span>
            <h3>Capacidade, atividade e recomendações em contexto</h3>
          </div>
          <p>Uma leitura prática do que está ocupando espaço, quem mais movimenta a área e o que vale atacar antes.</p>
        </div>

        <div className="inventory-details-grid inventory-details-grid--priority">
          <Panel title="Recomendações para revisão" subtitle="Sinais do último snapshot para orientar limpeza, arquivamento e ajustes de acesso.">
            {summary.recommendations.length === 0 ? (
              <EmptyState text="Sem recomendação gerencial para o snapshot atual." />
            ) : (
              <div className="inventory-recommendations">
                {summary.recommendations.map((item) => (
                  <article key={`${item.title}-${item.detail}`} className={item.severity === "warning" ? "warning" : "info"}>
                    {item.severity === "warning" ? <AlertTriangle size={18} /> : <ShieldCheck size={18} />}
                    <div>
                      <strong>{item.title}</strong>
                      <p>{item.detail}</p>
                    </div>
                  </article>
                ))}
              </div>
            )}
          </Panel>

          <Panel title="Pastas com maior consumo" subtitle="Priorize as maiores áreas para revisão de capacidade.">
            <InventoryRanking
              items={summary.topFolders}
              getKey={(item) => item.path}
              renderLabel={(item) => item.path}
              renderValue={(item) => formatBytes(item.totalBytes)}
              renderDetail={(item) => `${item.fileCount.toLocaleString("pt-BR")} arquivos · ${item.folderCount.toLocaleString("pt-BR")} pastas`}
              maxValue={Math.max(...summary.topFolders.map((item) => item.totalBytes), 1)}
            />
          </Panel>

          <Panel title="Usuários mais ativos" subtitle="Atividade real observada no mesmo período.">
            <InventoryRanking
              items={summary.observedActivity.topUsers}
              getKey={(item) => item.user}
              renderLabel={(item) => item.user}
              renderValue={(item) => `${item.eventCount.toLocaleString("pt-BR")}`}
              renderDetail={(item) => `${item.topAction} · ${formatDate(item.lastActivityUtc)}`}
              maxValue={Math.max(...summary.observedActivity.topUsers.map((item) => item.eventCount), 1)}
              getBarValue={(item) => item.eventCount}
            />
          </Panel>
        </div>
      </section>

      <section className="inventory-section inventory-pane-capacity inventory-pane-cleanup inventory-pane-risk" aria-label="Exploração detalhada">
        <div className="inventory-section-header">
          <div>
            <span className="inventory-section-kicker">Exploração detalhada</span>
            <h3>Achados, crescimento e itens que merecem auditoria fina</h3>
          </div>
          <p>Essa camada é útil para a equipe aprofundar a análise sem sair do inventário.</p>
        </div>

        <Panel title="Investigar achados" subtitle="Revise itens do último snapshot sem executar uma nova varredura.">
          <div className="toolbar">
            {[
              ["executable", "Executáveis/scripts"],
              ["large", "Arquivos grandes"],
              ["inactive365", "Inativos +365d"],
              ["errors", "Erros de leitura"]
            ].map(([kind, label]) => (
              <button
                key={kind}
                className={investigationKind === kind ? "primary-button compact" : "text-button"}
                type="button"
                onClick={() => loadInventoryItems(kind)}
                disabled={loadingInvestigation}
              >
                <Search size={16} />
                {label}
              </button>
            ))}
          </div>
          {loadingInvestigation ? (
            <EmptyState text="Carregando achados do inventário..." />
          ) : investigationItems.length === 0 ? (
            <EmptyState text="Escolha um tipo de achado para listar até 100 itens do último scan." />
          ) : (
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Nome</th>
                    <th>Tipo</th>
                    <th>Tamanho</th>
                    <th>Modificado</th>
                    <th>Acessado</th>
                    <th>Status</th>
                    <th>Caminho</th>
                  </tr>
                </thead>
                <tbody>
                  {investigationItems.map((item) => (
                    <tr key={item.id}>
                      <td>{item.name}</td>
                      <td>{item.extension || item.itemType}</td>
                      <td>{formatBytes(item.sizeBytes)}</td>
                      <td>{item.modifiedUtc ? formatDate(item.modifiedUtc) : "-"}</td>
                      <td>{item.accessedUtc ? formatDate(item.accessedUtc) : "-"}</td>
                      <td>
                        <span className={`status ${item.status === "active" ? "ok" : "attention"}`}>
                          {item.status === "active" ? "ativo" : item.status}
                        </span>
                      </td>
                      <td title={item.path}>{item.path}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Panel>

        <Panel title="Pastas que mais cresceram" subtitle="Comparação entre o snapshot atual e o anterior do mesmo compartilhamento.">
          <InventoryRanking
            items={summary.growth.topGrowingFolders}
            getKey={(item) => item.path}
            renderLabel={(item) => item.path}
            renderValue={(item) => formatSignedBytes(item.totalBytesDelta)}
            renderDetail={(item) => `${formatSignedNumber(item.fileCountDelta)} arquivo(s) · ${formatSignedNumber(item.folderCountDelta)} pasta(s)`}
            maxValue={Math.max(...summary.growth.topGrowingFolders.map((item) => item.totalBytesDelta), 1)}
            getBarValue={(item) => item.totalBytesDelta}
          />
        </Panel>

        <div className="inventory-details-grid">
          <Panel title="Maiores arquivos" subtitle="Candidatos para revisão de consumo, arquivamento ou política de retenção.">
            <InventoryRanking
              items={summary.topLargeFiles}
              getKey={(item) => item.path}
              renderLabel={(item) => item.name}
              renderValue={(item) => formatBytes(item.sizeBytes)}
              renderDetail={(item) => `${item.path} · ${formatInventoryFileAge(item)}`}
              maxValue={Math.max(...summary.topLargeFiles.map((item) => item.sizeBytes), 1)}
              getBarValue={(item) => item.sizeBytes}
            />
          </Panel>

          <Panel title="Arquivos antigos" subtitle="Itens com modificação mais antiga no snapshot atual.">
            <InventoryRanking
              items={summary.topInactiveFiles}
              getKey={(item) => item.path}
              renderLabel={(item) => item.name}
              renderValue={(item) => item.ageDays === null || item.ageDays === undefined ? "sem idade" : `${item.ageDays.toLocaleString("pt-BR")} dia(s)`}
              renderDetail={(item) => `${formatBytes(item.sizeBytes)} · ${item.path}`}
              maxValue={Math.max(...summary.topInactiveFiles.map((item) => item.ageDays ?? 0), 1)}
              getBarValue={(item) => item.ageDays ?? 0}
            />
          </Panel>
        </div>

        <div className="inventory-details-grid">
          <Panel title="Executáveis e scripts encontrados" subtitle="Arquivos que merecem revisão rápida em compartilhamentos corporativos.">
            <InventoryRanking
              items={summary.topExecutableFiles}
              getKey={(item) => item.path}
              renderLabel={(item) => item.name}
              renderValue={(item) => `${item.extension ?? "(sem extensão)"} · ${formatBytes(item.sizeBytes)}`}
              renderDetail={(item) => `${item.path} · ${formatInventoryFileAge(item)}`}
              maxValue={Math.max(...summary.topExecutableFiles.map((item) => item.sizeBytes), 1)}
              getBarValue={(item) => item.sizeBytes}
            />
          </Panel>

          <Panel title="Top extensões por tamanho" subtitle="Ajuda a encontrar arquivos de mídia, backup, PST, ISO e outros consumidores.">
            <InventoryRanking
              items={summary.topExtensions}
              getKey={(item) => item.extension}
              renderLabel={(item) => item.extension}
              renderValue={(item) => formatBytes(item.totalBytes)}
              renderDetail={(item) => `${item.fileCount.toLocaleString("pt-BR")} arquivo(s)`}
              maxValue={Math.max(...summary.topExtensions.map((item) => item.totalBytes), 1)}
            />
          </Panel>
        </div>
      </section>

      <div className="inventory-pane-snapshots">
        <InventorySnapshotsPanel snapshots={snapshots} />
      </div>
      </div>
    </div>
  );
}

function InventorySnapshotsPanel({ snapshots }: { snapshots: InventorySnapshot[] }) {
  return (
    <Panel title="Histórico de scans" subtitle="Últimas varreduras do inventário usadas para acompanhar cobertura e falhas de leitura.">
      {snapshots.length === 0 ? (
        <EmptyState text="Nenhum scan registrado ainda." />
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Início</th>
                <th>Fim</th>
                <th>Status</th>
                <th>Servidor</th>
                <th>Share</th>
                <th>Arquivos</th>
                <th>Pastas</th>
                <th>Tamanho</th>
                <th>Erros</th>
                <th>Raiz</th>
              </tr>
            </thead>
            <tbody>
              {snapshots.map((snapshot) => (
                <tr key={snapshot.id}>
                  <td>{formatDate(snapshot.startedUtc)}</td>
                  <td>{snapshot.finishedUtc ? formatDate(snapshot.finishedUtc) : "Em andamento"}</td>
                  <td>
                    <span className={`status ${snapshot.status === "completed" ? "ok" : snapshot.status === "failed" ? "critical" : "attention"}`}>
                      {formatInventoryStatus(snapshot.status)}
                    </span>
                  </td>
                  <td>{snapshot.server}</td>
                  <td>{snapshot.share}</td>
                  <td>{snapshot.fileCount.toLocaleString("pt-BR")}</td>
                  <td>{snapshot.folderCount.toLocaleString("pt-BR")}</td>
                  <td>{formatBytes(snapshot.totalBytes)}</td>
                  <td>{snapshot.errorCount.toLocaleString("pt-BR")}</td>
                  <td title={snapshot.rootPath}>{snapshot.rootPath}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  );
}

function formatInventoryStatus(status: string) {
  const normalized = status.toLowerCase();
  if (normalized === "completed") {
    return "concluído";
  }

  if (normalized === "completed_with_errors") {
    return "concluído com erro";
  }

  if (normalized === "failed") {
    return "falhou";
  }

  if (normalized === "running") {
    return "em execução";
  }

  return status;
}

function InventoryActionMetric({
  icon,
  label,
  value,
  detail,
  tone
}: {
  icon: React.ReactNode;
  label: string;
  value: number;
  detail: string;
  tone: "neutral" | "warning" | "danger";
}) {
  return (
    <article className={`inventory-action-metric ${tone}`}>
      <span className="inventory-action-icon">{icon}</span>
      <div>
        <span>{label}</span>
        <strong>{value.toLocaleString("pt-BR")}</strong>
        <small>{detail}</small>
      </div>
    </article>
  );
}

function InventoryKpiCard({
  icon,
  label,
  value,
  detail,
  tone
}: {
  icon: React.ReactNode;
  label: string;
  value: string;
  detail: string;
  tone: "navy" | "blue" | "green" | "amber" | "danger";
}) {
  return (
    <article className={`inventory-kpi-card ${tone}`}>
      <div className="inventory-kpi-head">
        <span className="inventory-kpi-icon">{icon}</span>
        <span className="inventory-kpi-label">{label}</span>
      </div>
      <strong>{value}</strong>
      <small>{detail}</small>
    </article>
  );
}

function InventorySignalCard({
  title,
  value,
  caption,
  bullets,
  tone
}: {
  title: string;
  value: string;
  caption: string;
  bullets: string[];
  tone: "green" | "blue" | "amber" | "danger";
}) {
  return (
    <article className={`inventory-signal-card ${tone}`}>
      <div className="inventory-signal-head">
        <span>{title}</span>
        <strong>{value}</strong>
      </div>
      <p>{caption}</p>
      <ul>
        {bullets.map((item) => (
          <li key={`${title}-${item}`}>{item}</li>
        ))}
      </ul>
    </article>
  );
}

function InventoryExecutiveList<T>({
  items,
  getKey,
  renderTitle,
  renderValue,
  renderDetail
}: {
  items: T[];
  getKey: (item: T) => string;
  renderTitle: (item: T) => string;
  renderValue: (item: T) => string;
  renderDetail: (item: T) => string;
}) {
  if (items.length === 0) {
    return <EmptyState text="Sem dados suficientes para esse recorte." />;
  }

  return (
    <div className="inventory-executive-list">
      {items.map((item, index) => (
        <article key={getKey(item)} className="inventory-executive-item">
          <span className="inventory-executive-rank">{String(index + 1).padStart(2, "0")}</span>
          <div className="inventory-executive-copy">
            <strong title={renderTitle(item)}>{renderTitle(item)}</strong>
            <small>{renderDetail(item)}</small>
          </div>
          <span className="inventory-executive-value">{renderValue(item)}</span>
        </article>
      ))}
    </div>
  );
}

function InventoryRatioList({
  items
}: {
  items: Array<{
    label: string;
    value: string;
    detail: string;
    percent: number;
    tone: "navy" | "blue" | "green" | "amber" | "danger";
  }>;
}) {
  if (items.length === 0) {
    return <EmptyState text="Sem leitura suficiente para esse comparativo." />;
  }

  return (
    <div className="inventory-ratio-list">
      {items.map((item) => {
        const width = Math.max(4, Math.min(100, Math.round(item.percent)));
        return (
          <article key={`${item.label}-${item.value}`} className={`inventory-ratio-item ${item.tone}`}>
            <div className="inventory-ratio-head">
              <strong title={item.label}>{item.label}</strong>
              <span>{item.value}</span>
            </div>
            <div className="inventory-ratio-bar" aria-hidden="true">
              <i style={{ width: `${width}%` }} />
            </div>
            <small>{item.detail}</small>
          </article>
        );
      })}
    </div>
  );
}

function InventoryCategoryChart({ items }: { items: InventoryContentCategory[] }) {
  const colors = ["#2f6f9f", "#4d8d77", "#b87535", "#6c6db4", "#a5576b", "#6d7b8a"];
  const ranked = items.filter((item) => item.totalBytes > 0).slice(0, 6);
  const total = ranked.reduce((sum, item) => sum + item.totalBytes, 0);

  if (ranked.length === 0 || total === 0) {
    return <EmptyState text="Sem tipos de conteúdo para representar no último snapshot." />;
  }

  let cursor = 0;
  const segments = ranked.map((item, index) => {
    const start = cursor;
    cursor += (item.totalBytes / total) * 100;
    return `${colors[index]} ${start.toFixed(2)}% ${cursor.toFixed(2)}%`;
  });

  return (
    <div className="inventory-category-chart">
      <div className="inventory-donut" style={{ background: `conic-gradient(${segments.join(", ")})` }} aria-label="Distribuição por tipo de conteúdo">
        <div>
          <strong>{formatBytes(total)}</strong>
          <span>top categorias</span>
        </div>
      </div>
      <div className="inventory-category-legend">
        {ranked.map((item, index) => (
          <div key={item.category}>
            <i style={{ background: colors[index] }} />
            <span title={formatInventoryCategory(item.category)}>{formatInventoryCategory(item.category)}</span>
            <strong>{formatBytes(item.totalBytes)}</strong>
          </div>
        ))}
      </div>
    </div>
  );
}

function InventoryAgeChart({ buckets }: { buckets: InventoryAgeBucket[] }) {
  const max = Math.max(...buckets.map((bucket) => bucket.fileCount), 1);

  if (buckets.length === 0) {
    return <EmptyState text="Sem dados de idade no último snapshot." />;
  }

  return (
    <div className="inventory-age-chart" aria-label="Idade dos arquivos">
      {buckets.map((bucket) => {
        const height = Math.max(8, Math.round((bucket.fileCount / max) * 100));
        return (
          <article key={bucket.label}>
            <strong>{bucket.fileCount.toLocaleString("pt-BR")}</strong>
            <div className="inventory-age-bar"><i style={{ height: `${height}%` }} /></div>
            <span>{bucket.label}</span>
            <small>{formatBytes(bucket.totalBytes)}</small>
          </article>
        );
      })}
    </div>
  );
}

function InventoryRanking<T>({
  items,
  getKey,
  renderLabel,
  renderValue,
  renderDetail,
  maxValue,
  getBarValue
}: {
  items: T[];
  getKey: (item: T) => string;
  renderLabel: (item: T) => string;
  renderValue: (item: T) => string;
  renderDetail: (item: T) => string;
  maxValue: number;
  getBarValue?: (item: T) => number;
}) {
  if (items.length === 0) {
    return <EmptyState text="Sem dados para exibir." />;
  }

  return (
    <div className="inventory-ranking">
      {items.map((item) => {
        const value = getBarValue ? getBarValue(item) : Number((item as { totalBytes?: number }).totalBytes ?? 0);
        const width = Math.max(3, Math.round((value / maxValue) * 100));
        return (
          <article key={getKey(item)}>
            <div>
              <strong title={renderLabel(item)}>{renderLabel(item)}</strong>
              <span>{renderValue(item)}</span>
            </div>
            <div className="inventory-bar" aria-hidden="true">
              <i style={{ width: `${width}%` }} />
            </div>
            <small>{renderDetail(item)}</small>
          </article>
        );
      })}
    </div>
  );
}

function formatPercent(value: number) {
  return `${value.toLocaleString("pt-BR", { minimumFractionDigits: 1, maximumFractionDigits: 1 })}%`;
}

function DatabaseCapacityView({ onNotify }: { onNotify: (notice: Notice | null) => void }) {
  const [capacity, setCapacity] = useState<DatabaseCapacityResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const timelineWindow = capacity?.windows.find((item) => item.name === "Linha do tempo");
  const rawWindow = capacity?.windows.find((item) => item.name === "Eventos brutos");
  const recentDaily = useMemo(() => {
    if (!capacity) {
      return [];
    }

    const grouped = new Map<string, Record<string, number>>();
    for (const item of capacity.dailyCounts) {
      const bucket = grouped.get(item.date) ?? {};
      bucket[item.series] = item.count;
      grouped.set(item.date, bucket);
    }

    return Array.from(grouped.entries())
      .map(([date, values]) => ({
        date,
        raw: values["Eventos brutos"] ?? 0,
        timeline: values["Linha do tempo"] ?? 0,
        alerts: values["Alertas"] ?? 0
      }))
      .slice(0, 10);
  }, [capacity]);

  async function loadCapacity() {
    setLoading(true);
    try {
      setCapacity(await fetchJson<DatabaseCapacityResponse>("/api/database/capacity"));
    } catch (error) {
      onNotify({ tone: "danger", message: error instanceof Error ? error.message : "Nao foi possivel carregar capacidade do banco." });
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    void loadCapacity();
  }, []);

  if (!capacity) {
    return (
      <Panel title="Capacidade do Banco" subtitle={loading ? "Carregando capacidade..." : "Sem dados carregados."}>
        <EmptyState text={loading ? "Consultando SQL Server..." : "Clique em atualizar para carregar a capacidade."} />
      </Panel>
    );
  }

  return (
    <div className="view-stack">
      <section className="executive-grid">
        <ExecutiveCard
          title="Linhas Totais"
          value={capacity.totalRows.toLocaleString("pt-BR")}
          detail={`Provider ${capacity.provider}. Atualizado em ${formatDate(capacity.generatedUtc)}.`}
          tone="neutral"
        />
        <ExecutiveCard
          title="Espaço Reservado"
          value={formatMegabytes(capacity.totalReservedMb)}
          detail="Soma das tabelas principais da aplicação."
          tone={capacity.totalReservedMb > 102400 ? "warning" : "neutral"}
        />
        <ExecutiveCard
          title="Timeline Correlacionada"
          value={capacity.timelineRows.toLocaleString("pt-BR")}
          detail={timelineWindow?.fromUtc ? `${formatDate(timelineWindow.fromUtc)} até ${formatDate(timelineWindow.toUtc ?? timelineWindow.fromUtc)}` : "Sem janela registrada."}
          tone="neutral"
        />
      </section>

      {capacity.message && <div className="sync-banner">{capacity.message}</div>}

      <div className="toolbar">
        <button className="text-button" type="button" onClick={loadCapacity} disabled={loading}>
          <RefreshCcw size={16} />
          {loading ? "Atualizando..." : "Atualizar capacidade"}
        </button>
      </div>

      <Panel title="Tabelas principais" subtitle="Volume e espaço reservado para os dados que mais crescem em produção.">
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Tabela</th>
                <th>Linhas</th>
                <th>Reservado</th>
                <th>Usado</th>
                <th>Nome físico</th>
              </tr>
            </thead>
            <tbody>
              {capacity.tables.map((table) => (
                <tr key={table.physicalName}>
                  <td>{table.name}</td>
                  <td>{table.rowCount.toLocaleString("pt-BR")}</td>
                  <td>{formatMegabytes(table.reservedMb)}</td>
                  <td>{formatMegabytes(table.usedMb)}</td>
                  <td className="details-cell">{table.physicalName}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {capacity.tables.length === 0 && <EmptyState text="Sem tabelas detalhadas para este provider." />}
        </div>
      </Panel>

      <div className="split-grid">
        <Panel title="Janelas de retenção" subtitle="Menor e maior data encontrada nas tabelas de auditoria.">
          <div className="capacity-window-list">
            {capacity.windows.map((item) => (
              <article key={item.name}>
                <strong>{item.name}</strong>
                <span>{item.rowCount.toLocaleString("pt-BR")} registro(s)</span>
                <small>{item.fromUtc ? `${formatDate(item.fromUtc)} até ${formatDate(item.toUtc ?? item.fromUtc)}` : "Sem registros"}</small>
              </article>
            ))}
            {rawWindow && timelineWindow && rawWindow.rowCount > 0 && (
              <article>
                <strong>Taxa de consolidação</strong>
                <span>{Math.round((timelineWindow.rowCount / rawWindow.rowCount) * 100).toLocaleString("pt-BR")}%</span>
                <small>Relação entre timeline correlacionada e eventos brutos.</small>
              </article>
            )}
          </div>
        </Panel>

        <Panel title="Crescimento recente" subtitle="Contagem diária dos últimos 30 dias, com foco nos 10 dias mais recentes.">
          <div className="capacity-daily-list">
            {recentDaily.map((item) => (
              <div key={item.date}>
                <strong>{item.date}</strong>
                <span>Brutos {item.raw.toLocaleString("pt-BR")}</span>
                <span>Timeline {item.timeline.toLocaleString("pt-BR")}</span>
                <span>Alertas {item.alerts.toLocaleString("pt-BR")}</span>
              </div>
            ))}
            {recentDaily.length === 0 && <EmptyState text="Sem crescimento recente no recorte." />}
          </div>
        </Panel>
      </div>
    </div>
  );
}

function AdminAuditView({ entries }: { entries: AdminAuditEntry[] }) {
  const [filter, setFilter] = useState("");
  const filteredEntries = useMemo(() => {
    const value = filter.trim().toLowerCase();

    if (!value) {
      return entries;
    }

    return entries.filter((entry) =>
      [entry.action, entry.entityType, entry.entityId, entry.actor, entry.sourceIp, entry.detailsJson]
        .filter(Boolean)
        .some((item) => item!.toLowerCase().includes(value))
    );
  }, [entries, filter]);
  const uniqueActors = useMemo(() => new Set(filteredEntries.map((entry) => entry.actor).filter(Boolean)).size, [filteredEntries]);
  const latestAudit = filteredEntries[0]?.timestampUtc ?? null;

  return (
    <div className="view-stack">
      <section className="executive-grid">
        <ExecutiveCard
          title="Registros no Recorte"
          value={filteredEntries.length.toLocaleString("pt-BR")}
          detail="Ações administrativas visíveis com o filtro atual."
          tone="neutral"
        />
        <ExecutiveCard
          title="Operadores"
          value={uniqueActors.toLocaleString("pt-BR")}
          detail="Pessoas ou integrações que alteraram configuração ou cadastro."
          tone={uniqueActors > 3 ? "warning" : "neutral"}
        />
        <ExecutiveCard
          title="Última Alteração"
          value={latestAudit ? formatDate(latestAudit) : "Sem registro"}
          detail={filter.trim() ? `Filtro ativo: ${filter.trim()}` : "Sem filtro adicional aplicado."}
          tone="neutral"
        />
      </section>

      <div className="toolbar">
        <label className="search-box">
          <Search size={18} />
          <input
            value={filter}
            onChange={(event) => setFilter(event.target.value)}
            placeholder="Filtrar por ação, entidade, operador ou IP"
          />
        </label>
      </div>
      <Panel title="Auditoria Administrativa" subtitle="Trilha de mudanças operacionais para apoiar governança e troubleshooting.">
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Horário</th>
                <th>Ação</th>
                <th>Entidade</th>
                <th>Operador</th>
                <th>IP</th>
                <th>Detalhes</th>
              </tr>
            </thead>
            <tbody>
              {filteredEntries.map((entry) => (
                <tr key={entry.id}>
                  <td>{formatDate(entry.timestampUtc)}</td>
                  <td><span className="pill">{entry.action}</span></td>
                  <td>{entry.entityType}<span className="muted-id">{entry.entityId}</span></td>
                  <td>{entry.actor}</td>
                  <td>{entry.sourceIp ?? "-"}</td>
                  <td className="details-cell" title={entry.detailsJson ?? ""}>{formatDetails(entry.detailsJson)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {filteredEntries.length === 0 && <EmptyState text="Nenhum registro administrativo encontrado." />}
        </div>
      </Panel>
    </div>
  );
}

function EventTable({
  events,
  compact = false,
  pagination
}: {
  events: DisplayEvent[];
  compact?: boolean;
  pagination?: PaginationState;
}) {
  return (
    <div className="table-wrap">
      <table>
        <thead>
          <tr>
            <th>Horário</th>
            <th>Ação</th>
            <th>Usuário</th>
            {!compact && <th>Origem</th>}
            <th>Antes / Depois</th>
          </tr>
        </thead>
        <tbody>
          {events.map((event) => (
            <tr key={event.id}>
              <td>{formatDate(event.timestampUtc)}</td>
              <td>
                <div className="event-action-cell">
                  <span className="pill">{event.displayAction ?? formatAction(event.action, event)}</span>
                  {event.displayTarget && <small>{event.displayTarget}</small>}
                </div>
              </td>
              <td>{event.user}</td>
              {!compact && <td>{formatSource(event)}</td>}
              <td className="transition-cell" title={formatEventTransition(event)}>
                <div className="transition-stack">
                  {event.previousPath ? (
                    <>
                      <span className="transition-label">Antes</span>
                      <span className="path-cell" title={event.previousPath}>{event.previousPath}</span>
                      <span className="transition-label">Depois</span>
                      <span className="path-cell" title={event.path}>{event.path}</span>
                    </>
                  ) : (
                    <>
                      <span className="transition-label">Atual</span>
                      <span className="path-cell" title={event.path}>{event.path}</span>
                    </>
                  )}
                </div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {events.length === 0 && <EmptyState text="Nenhum evento encontrado." />}
      {pagination && pagination.totalItems > 0 && <PaginationFooter pagination={pagination} />}
    </div>
  );
}

function InvestigationTable({
  events,
  pagination
}: {
  events: DisplayEvent[];
  pagination?: PaginationState;
}) {
  return (
    <div className="table-wrap">
      <table>
        <thead>
          <tr>
            <th>Horário</th>
            <th>Ação</th>
            <th>Usuário</th>
            <th>Origem</th>
            <th>Processo</th>
            <th>Resultado</th>
            <th>Caminho</th>
            <th>Anterior</th>
          </tr>
        </thead>
        <tbody>
          {events.map((event) => (
            <tr key={event.id}>
              <td>{formatDate(event.timestampUtc)}</td>
              <td>
                <div className="event-action-cell">
                  <span className="pill">{event.displayAction ?? formatAction(event.action, event)}</span>
                  {event.displayTarget && <small>{event.displayTarget}</small>}
                </div>
              </td>
              <td>{event.user}</td>
              <td>{formatSource(event)}</td>
              <td>{event.processName ?? "-"}</td>
              <td>{event.result}</td>
              <td className="path-cell" title={event.path}>{event.path}</td>
              <td className="path-cell" title={event.previousPath ?? ""}>{event.previousPath ?? "-"}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {events.length === 0 && <EmptyState text="Nenhum evento encontrado para os filtros informados." />}
      {pagination && pagination.totalItems > 0 && <PaginationFooter pagination={pagination} />}
    </div>
  );
}

function PaginationFooter({ pagination }: { pagination: PaginationState }) {
  return (
    <div className="pagination-footer">
      <span className="pagination-summary">
        Mostrando {pagination.pageItems.toLocaleString("pt-BR")} de {pagination.totalItems.toLocaleString("pt-BR")} registro(s)
      </span>
      <div className="pagination-actions">
        <button className="text-button" type="button" onClick={pagination.onPrevious} disabled={pagination.page <= 1}>
          Anterior
        </button>
        <span className="pagination-page">
          Página {pagination.page.toLocaleString("pt-BR")} de {pagination.totalPages.toLocaleString("pt-BR")}
        </span>
        <button className="text-button" type="button" onClick={pagination.onNext} disabled={pagination.page >= pagination.totalPages}>
          Próxima
        </button>
      </div>
    </div>
  );
}

function formatEventTransition(event: FileAuditEvent) {
  return event.previousPath
    ? `Anterior: ${event.previousPath}\nAtual: ${event.path}`
    : event.path;
}

function AlertList({ alerts }: { alerts: FileServerAlert[] }) {
  if (alerts.length === 0) {
    return <EmptyState text="Nenhum alerta aberto." />;
  }

  return (
    <div className="alert-list">
      {alerts.map((alert) => (
        <article key={alert.id}>
          <div className="alert-list-head">
            <span className={`badge ${alert.severity}`}>{alert.severity}</span>
            <span className={`status ${alert.status}`}>{alert.status}</span>
          </div>
          <strong>{alert.title}</strong>
          <small>{alert.server} · {alert.user} · {alert.eventCount} eventos · {formatDate(alert.createdUtc)}</small>
        </article>
      ))}
    </div>
  );
}

function Metric({ icon, label, value, tone }: { icon: React.ReactNode; label: string; value: number; tone: string }) {
  return (
    <article className={`metric ${tone}`}>
      <div className="metric-icon">{icon}</div>
      <div>
        <span>{label}</span>
        <strong>{value.toLocaleString("pt-BR")}</strong>
      </div>
    </article>
  );
}

function Panel({ title, subtitle, children }: { title: string; subtitle?: string; children?: React.ReactNode }) {
  return (
    <section className="panel">
      <header>
        <div>
          <h2>{title}</h2>
          {subtitle && <p>{subtitle}</p>}
        </div>
      </header>
      {children}
    </section>
  );
}

function ExecutiveCard({
  title,
  value,
  detail,
  tone
}: {
  title: string;
  value: string;
  detail: string;
  tone: "neutral" | "warning" | "danger";
}) {
  return (
    <article className={`executive-card ${tone}`}>
      <span>{title}</span>
      <strong>{value}</strong>
      <small>{detail}</small>
    </article>
  );
}

function TabButton({
  icon,
  active,
  onClick,
  meta,
  children
}: {
  icon: React.ReactNode;
  active: boolean;
  onClick: () => void;
  meta?: string;
  children: React.ReactNode;
}) {
  return (
    <button className={active ? "active" : ""} onClick={onClick}>
      {icon}
      <span>{children}</span>
      {meta && <small>{meta}</small>}
    </button>
  );
}

function EmptyState({ text }: { text: string }) {
  return <div className="empty-state">{text}</div>;
}

function ReportGroupList({ rows, total }: { rows: Array<{ label: string; count: number }>; total: number }) {
  const safeTotal = Math.max(1, total);

  return (
    <div className="report-group-list">
      {rows.slice(0, 10).map((row) => {
        const percent = Math.round((row.count / safeTotal) * 100);
        return (
          <div key={row.label} className="report-group-row">
            <div>
              <strong>{row.label}</strong>
              <span>{row.count.toLocaleString("pt-BR")} evento(s)</span>
            </div>
            <div className="report-group-bar" aria-label={`${row.label}: ${percent}%`}>
              <span style={{ width: `${Math.max(4, percent)}%` }} />
            </div>
          </div>
        );
      })}
    </div>
  );
}

function ReportTextList({
  title,
  items,
  emptyText
}: {
  title: string;
  items: string[];
  emptyText: string;
}) {
  return (
    <article className="report-text-card">
      <strong>{title}</strong>
      {items.length === 0 ? (
        <p>{emptyText}</p>
      ) : (
        <ul>
          {items.map((item) => (
            <li key={`${title}-${item}`}>{item}</li>
          ))}
        </ul>
      )}
    </article>
  );
}

function FeedbackBanner({
  tone,
  message,
  onClose
}: {
  tone: "success" | "warning" | "danger";
  message: string;
  onClose: () => void;
}) {
  return (
    <div className={`feedback-banner ${tone}`}>
      <span>{message}</span>
      <button className="icon-button subtle" type="button" onClick={onClose} title="Fechar aviso">
        ×
      </button>
    </div>
  );
}

async function fetchJson<T>(path: string, options: { timeoutMs?: number } = {}): Promise<T> {
  const controller = new AbortController();
  const timeout = window.setTimeout(() => controller.abort(), options.timeoutMs ?? 15_000);

  const response = await fetch(`${apiBaseUrl}${path}`, {
    headers: buildHeaders(),
    signal: controller.signal
  }).finally(() => window.clearTimeout(timeout));

  if (!response.ok) {
    throw new Error(await readErrorMessage(response, `${response.status} ao chamar ${path}`));
  }

  return response.json() as Promise<T>;
}

async function readErrorMessage(response: Response, fallback: string) {
  try {
    const raw = await response.text();

    if (!raw.trim()) {
      return fallback;
    }

    try {
      const parsed = JSON.parse(raw) as { message?: string; error?: string; title?: string };
      return parsed.message || parsed.error || parsed.title || raw;
    } catch {
      return raw.length > 300 ? fallback : raw;
    }
  } catch {
    return fallback;
  }
}

function buildActivitySummaryUrl(filters: ActivitySummaryFilters) {
  const params = new URLSearchParams({ take: "8" });
  const periodHours = Number(filters.periodHours);

  if (Number.isFinite(periodHours) && periodHours > 0) {
    params.set("fromUtc", new Date(Date.now() - periodHours * 60 * 60 * 1000).toISOString());
  }

  if (filters.server.trim()) {
    params.set("server", filters.server.trim());
  }

  if (filters.share.trim()) {
    params.set("share", filters.share.trim());
  }

  if (filters.user.trim()) {
    params.set("user", filters.user.trim());
  }

  if (filters.action.trim()) {
    params.set("action", filters.action.trim());
  }

  return `/api/reports/activity-summary?${params.toString()}`;
}

function buildBaselineAnomaliesUrl(filters: ActivitySummaryFilters) {
  const params = new URLSearchParams({ take: "8" });
  const periodHours = Number(filters.periodHours);

  if (Number.isFinite(periodHours) && periodHours > 0) {
    params.set("fromUtc", new Date(Date.now() - periodHours * 60 * 60 * 1000).toISOString());
  }

  if (filters.server.trim()) {
    params.set("server", filters.server.trim());
  }

  if (filters.share.trim()) {
    params.set("share", filters.share.trim());
  }

  if (filters.user.trim()) {
    params.set("user", filters.user.trim());
  }

  if (filters.action.trim()) {
    params.set("action", filters.action.trim());
  }

  return `/api/reports/baseline-anomalies?${params.toString()}`;
}

function buildBaselineAnomaliesExportUrl(filters: ActivitySummaryFilters) {
  const params = new URLSearchParams({ take: "20" });
  const periodHours = Number(filters.periodHours);

  if (Number.isFinite(periodHours) && periodHours > 0) {
    params.set("fromUtc", new Date(Date.now() - periodHours * 60 * 60 * 1000).toISOString());
  }

  if (filters.server.trim()) {
    params.set("server", filters.server.trim());
  }

  if (filters.share.trim()) {
    params.set("share", filters.share.trim());
  }

  if (filters.user.trim()) {
    params.set("user", filters.user.trim());
  }

  if (filters.action.trim()) {
    params.set("action", filters.action.trim());
  }

  return `/api/reports/baseline-anomalies/export.csv?${params.toString()}`;
}

function buildInvestigationUrl(filters: InvestigationFilters) {
  const params = new URLSearchParams({ take: "500" });
  const periodHours = Number(filters.periodHours);

  if (filters.periodMode === "custom" && filters.fromDate) {
    const fromDate = new Date(`${filters.fromDate}T00:00:00`);
    const toDate = filters.toDate ? new Date(`${filters.toDate}T23:59:59.999`) : new Date(`${filters.fromDate}T23:59:59.999`);
    params.set("fromUtc", fromDate.toISOString());
    params.set("toUtc", toDate.toISOString());
  } else if (Number.isFinite(periodHours) && periodHours > 0) {
    params.set("fromUtc", new Date(Date.now() - periodHours * 60 * 60 * 1000).toISOString());
  }

  if (filters.server.trim()) {
    params.set("server", filters.server.trim());
  }

  if (filters.user.trim()) {
    params.set("user", filters.user.trim());
  }

  if (filters.path.trim()) {
    params.set("path", filters.path.trim());
  }

  if (filters.action.trim()) {
    params.set("action", filters.action.trim());
  }

  return `/api/events/timeline?${params.toString()}`;
}

function buildReportEventsUrl(filters: ReportFilters, take = 5000) {
  const params = buildReportQueryParams(filters, take);
  return `/api/events/timeline?${params.toString()}`;
}

function getReportEventTake(scenarioId: ReportScenarioId | null) {
  if (scenarioId === "executive-qbr" || scenarioId === "capacity-cleanup" || scenarioId === "cold-data") {
    return 3000;
  }

  return 10000;
}

async function fetchInventorySummaryForReport(filters: ReportFilters) {
  const params = new URLSearchParams();

  if (filters.server.trim()) {
    params.set("server", filters.server.trim());
  }

  if (filters.share.trim()) {
    params.set("share", filters.share.trim());
  }

  if (filters.path.trim()) {
    params.set("rootPath", filters.path.trim());
  }

  params.set("top", "5");

  return fetchJson<InventorySummary>(`/api/inventory/summary?${params.toString()}`);
}

function buildReportExportUrl(filters: ReportFilters, take = 20000) {
  const params = buildReportQueryParams(filters, take);
  return `/api/events/timeline/export.csv?${params.toString()}`;
}

function buildTimelinePageUrl(page: number, search: string) {
  const params = new URLSearchParams({
    page: String(Math.max(1, page)),
    pageSize: String(EVENTS_PAGE_SIZE),
    windowTake: "2000"
  });

  if (search.trim()) {
    params.set("search", search.trim());
  }

  return `/api/events/timeline/page?${params.toString()}`;
}

function buildAlertOperationsUrl(alert: FileServerAlert) {
  const params = new URLSearchParams({
    server: alert.server,
    user: alert.user,
    take: String(Math.max(100, Math.min(500, alert.eventCount * 4)))
  });

  params.set("fromUtc", new Date(new Date(alert.firstEventUtc).getTime() - 10 * 60 * 1000).toISOString());
  params.set("toUtc", new Date(new Date(alert.lastEventUtc).getTime() + 10 * 60 * 1000).toISOString());

  return `/api/events?${params.toString()}`;
}

async function acknowledgeAlert(id: string, onDone: () => void, onNotify: (notice: Notice | null) => void) {
  const response = await fetch(`${apiBaseUrl}/api/alerts/${id}/ack`, {
    method: "POST",
    headers: buildHeaders()
  });

  if (response.ok) {
    onDone();
    onNotify({ tone: "success", message: "Alerta reconhecido com sucesso." });
  } else {
    onNotify({ tone: "danger", message: "Nao foi possivel reconhecer o alerta." });
  }
}

async function updateAlertRule(
  rule: AlertRuleConfig,
  setSavingRule: (value: string | null) => void,
  onDone: () => void,
  onNotify: (notice: Notice | null) => void
) {
  setSavingRule(rule.rule);

  try {
    const response = await fetch(`${apiBaseUrl}/api/alert-rules/${rule.rule}`, {
      method: "PUT",
      headers: buildJsonHeaders(),
      body: JSON.stringify({
        enabled: rule.enabled,
        severity: rule.severity,
        threshold: rule.threshold,
        secondaryThreshold: rule.secondaryThreshold,
        secondarySeverity: rule.secondarySeverity,
        serverFilter: rule.serverFilter,
        shareFilter: rule.shareFilter,
        pathFilter: rule.pathFilter,
        activeFromHour: rule.activeFromHour,
        activeToHour: rule.activeToHour,
        activeDays: rule.activeDays,
        excludedUsers: rule.excludedUsers,
        excludedHosts: rule.excludedHosts,
        excludedProcesses: rule.excludedProcesses,
        timeZoneId: rule.timeZoneId
      })
    });

    if (!response.ok) {
      throw new Error(`Falha ao atualizar regra ${rule.rule}.`);
    }

    onDone();
    onNotify({ tone: "success", message: `Regra ${rule.title} atualizada.` });
  } catch (error) {
    console.error(error);
    onNotify({ tone: "danger", message: "Nao foi possivel salvar a regra de alerta." });
  } finally {
    setSavingRule(null);
  }
}

async function simulateAlertRule(
  ruleName: string,
  setSimulatingRule: (value: string | null) => void,
  setSimulation: (value: AlertRuleSimulationResponse | null) => void,
  onNotify: (notice: Notice | null) => void
) {
  setSimulatingRule(ruleName);

  try {
    const response = await fetch(`${apiBaseUrl}/api/alert-rules/${ruleName}/simulate`, {
      method: "POST",
      headers: buildJsonHeaders(),
      body: JSON.stringify({
        fromUtc: new Date(Date.now() - 24 * 60 * 60 * 1000).toISOString(),
        toUtc: new Date().toISOString(),
        take: 5000
      })
    });

    if (!response.ok) {
      throw new Error(`Falha ao simular regra ${ruleName}.`);
    }

    setSimulation((await response.json()) as AlertRuleSimulationResponse);
    onNotify({ tone: "success", message: `Simulação da regra ${ruleName} concluída.` });
  } catch (error) {
    console.error(error);
    onNotify({ tone: "danger", message: "Nao foi possivel simular a regra de alerta." });
  } finally {
    setSimulatingRule(null);
  }
}

async function deleteMonitoredPath(id: string, onDone: () => void, onNotify: (notice: Notice | null) => void) {
  const response = await fetch(`${apiBaseUrl}/api/monitored-paths/${id}`, {
    method: "DELETE",
    headers: buildHeaders()
  });

  if (response.ok) {
    onDone();
    onNotify({ tone: "success", message: "Caminho monitorado removido." });
  } else {
    onNotify({ tone: "danger", message: "Nao foi possivel remover o caminho monitorado." });
  }
}

async function updateMonitoredPath(
  path: MonitoredPath,
  setEditingPathId: (value: string | null) => void,
  onDone: () => void,
  onNotify: (notice: Notice | null) => void
) {
  setEditingPathId(path.id);

  try {
    const response = await fetch(`${apiBaseUrl}/api/monitored-paths/${path.id}`, {
      method: "PUT",
      headers: buildJsonHeaders(),
      body: JSON.stringify({
        server: path.server,
        share: path.share,
        path: path.path,
        status: path.status,
        priority: path.priority,
        owner: path.owner ?? "",
        notes: path.notes ?? ""
      })
    });

    if (!response.ok) {
      throw new Error(await readErrorMessage(response, "Nao foi possivel atualizar o caminho monitorado."));
    }

    onDone();
    onNotify({ tone: "success", message: "Caminho monitorado atualizado." });
  } catch (error) {
    console.error(error);
    onNotify({ tone: "danger", message: error instanceof Error ? error.message : "Nao foi possivel atualizar o caminho monitorado." });
  } finally {
    setEditingPathId(null);
  }
}

async function downloadAlertsCsv(alerts: FileServerAlert[], onNotify: (notice: Notice | null) => void) {
  try {
    const query = new URLSearchParams({ status: "open", take: String(Math.max(100, alerts.length || 100)) });
    await downloadCsv(`${apiBaseUrl}/api/alerts/export.csv?${query.toString()}`, `fileserver-alerts-${new Date().toISOString().slice(0, 10)}.csv`);
    onNotify({ tone: "success", message: "Exportação de alertas iniciada." });
  } catch (error) {
    console.error(error);
    onNotify({ tone: "danger", message: "Nao foi possivel exportar os alertas agora." });
  }
}

async function downloadBaselineAnomaliesCsv(filters: ActivitySummaryFilters, onNotify: (notice: Notice | null) => void) {
  try {
    await downloadCsv(
      `${apiBaseUrl}${buildBaselineAnomaliesExportUrl(filters)}`,
      `fileserver-anomalies-${new Date().toISOString().slice(0, 10)}.csv`
    );
    onNotify({ tone: "success", message: "Exportação de anomalias iniciada." });
  } catch (error) {
    console.error(error);
    onNotify({ tone: "danger", message: "Nao foi possivel exportar as anomalias agora." });
  }
}

async function downloadEventsCsv(onNotify: (notice: Notice | null) => void) {
  try {
    await downloadCsv(`${apiBaseUrl}/api/events/export.csv?take=10000`, `fileserver-events-${new Date().toISOString().slice(0, 10)}.csv`);
    onNotify({ tone: "success", message: "Exportação de eventos iniciada." });
  } catch (error) {
    console.error(error);
    onNotify({ tone: "danger", message: "Nao foi possivel exportar os eventos agora." });
  }
}

async function downloadReportCsv(filters: ReportFilters, onNotify: (notice: Notice | null) => void) {
  try {
    await downloadCsv(
      `${apiBaseUrl}${buildReportExportUrl(filters, 10000)}`,
      `fileserver-report-${new Date().toISOString().slice(0, 10)}.csv`
    );
    onNotify({ tone: "success", message: "Exportação do relatório iniciada." });
  } catch (error) {
    console.error(error);
    onNotify({ tone: "danger", message: "Nao foi possivel exportar o relatorio agora." });
  }
}

async function downloadCsv(url: string, fileName: string) {
  const response = await fetch(url, {
    headers: buildHeaders()
  });

  if (!response.ok) {
    throw new Error(`Falha ao exportar CSV em ${url}.`);
  }

  const blob = await response.blob();
  const objectUrl = URL.createObjectURL(blob);
  const link = document.createElement("a");

  link.href = objectUrl;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(objectUrl);
}

function buildHeaders() {
  const headers: Record<string, string> = {};
  const token = localStorage.getItem(authTokenStorageKey);

  if (apiKey) {
    headers["X-Api-Key"] = apiKey;
  }

  if (token) {
    headers.Authorization = `Bearer ${token}`;
  }

  if (actorName) {
    headers["X-Actor"] = actorName;
  }

  return Object.keys(headers).length > 0 ? headers : undefined;
}

function buildJsonHeaders() {
  return {
    "Content-Type": "application/json",
    ...(buildHeaders() ?? {})
  };
}

function readStoredAuthUser() {
  const raw = localStorage.getItem(authUserStorageKey);

  if (!raw) {
    return null;
  }

  try {
    return JSON.parse(raw) as AuthenticatedUser;
  } catch {
    localStorage.removeItem(authUserStorageKey);
    localStorage.removeItem(authTokenStorageKey);
    return null;
  }
}

function buildAccessPolicy(authStatus: AuthStatusResponse | null, authUser: AuthenticatedUser | null): AccessPolicy {
  const role = resolveAccessRole(authStatus, authUser);
  const canOperate = role === "admin" || role === "operator";

  return {
    role,
    canManageAlerts: canOperate,
    canManagePaths: canOperate,
    canViewAgents: canOperate,
    canViewCapacity: canOperate,
    canViewAdminAudit: role === "admin",
    canManageAuth: role === "admin"
  };
}

function resolveAccessRole(authStatus: AuthStatusResponse | null, authUser: AuthenticatedUser | null): AccessRole {
  if (!authStatus?.enabled) {
    return "admin";
  }

  const role = authUser?.role?.toLowerCase();

  if (role === "admin" || role === "operator" || role === "reader") {
    return role;
  }

  return "reader";
}

function getVisibleTabs(policy: AccessPolicy): Tab[] {
  const tabs: Tab[] = ["dashboard", "events", "investigation", "reports", "inventory"];

  if (policy.canManageAlerts) {
    tabs.push("alerts");
  }

  if (policy.canViewAgents) {
    tabs.push("agents");
  }

  if (policy.canManagePaths) {
    tabs.push("paths");
  }

  if (policy.canViewCapacity) {
    tabs.push("capacity");
  }

  if (policy.canViewAdminAudit) {
    tabs.push("audit");
  }

  if (policy.canManageAuth) {
    tabs.push("auth");
  }

  return tabs;
}

function labelForRole(role: AccessRole) {
  const labels: Record<AccessRole, string> = {
    admin: "Admin",
    operator: "Operador",
    reader: "Leitor"
  };

  return labels[role];
}

function titleForTab(tab: Tab) {
  const titles: Record<Tab, string> = {
    dashboard: "Dashboard",
    events: "Eventos",
    investigation: "Investigação",
    reports: "Relatórios",
    inventory: "Inventário Gerencial",
    alerts: "Alertas",
    agents: "Agentes",
    paths: "Caminhos Monitorados",
    capacity: "Capacidade do Banco",
    audit: "Auditoria Administrativa",
    auth: "Configuração"
  };

  return titles[tab];
}

function formatSource(event: FileAuditEvent) {
  if (event.sourceHost || event.sourceIp) {
    return [event.sourceHost, event.sourceIp].filter(Boolean).join(" · ");
  }

  const labels: Record<string, string> = {
    "windows-security-log": "Log de Segurança do Windows",
    "usn-journal": "USN Journal",
    "usn-journal+security-log": "USN + Log de Segurança",
    "manual-demo": "Carga de demonstração"
  };

  return labels[event.source] ?? event.source;
}

function formatAction(action: string, event?: FileAuditEvent) {
  if (action === "renamed" && event?.previousPath && isCrossFolderRename(event.previousPath, event.path)) {
    return "Movido";
  }

  const labels: Record<string, string> = {
    accessed: "Acessado",
    changed: "Alterado",
    created: "Criação",
    created_or_appended: "Criação",
    deleted: "Excluído",
    modified: "Alterado",
    permission_changed: "Permissão alterada",
    renamed: "Renomeado",
    renamed_new: "Renomeado",
    renamed_old: "Renomeado",
    moved: "Movido"
  };

  return labels[action] ?? action;
}

function isCrossFolderRename(previousPath: string, nextPath: string) {
  return getDisplayParentPath(previousPath).toLowerCase() !== getDisplayParentPath(nextPath).toLowerCase();
}

function getDisplayParentPath(path: string) {
  const normalized = path.trim().replaceAll("/", "\\").replace(/\\+$/, "");
  const separatorIndex = normalized.lastIndexOf("\\");

  return separatorIndex <= 0 ? normalized : normalized.slice(0, separatorIndex);
}

function getHighestAnomaly(response: BaselineAnomalyResponse | null) {
  if (!response) {
    return null;
  }

  return [...response.byAction, ...response.byShare, ...response.byUser]
    .sort((left, right) => right.deltaPercent - left.deltaPercent)[0] ?? null;
}

function formatMegabytes(value: number) {
  if (value >= 1024) {
    return `${(value / 1024).toLocaleString("pt-BR", { maximumFractionDigits: 1 })} GB`;
  }

  return `${value.toLocaleString("pt-BR", { maximumFractionDigits: 1 })} MB`;
}

function formatBytes(value: number) {
  const units = ["B", "KB", "MB", "GB", "TB", "PB"];
  let current = Math.max(0, value);
  let unitIndex = 0;

  while (current >= 1024 && unitIndex < units.length - 1) {
    current /= 1024;
    unitIndex++;
  }

  return `${current.toLocaleString("pt-BR", { maximumFractionDigits: unitIndex === 0 ? 0 : 1 })} ${units[unitIndex]}`;
}

function formatSignedBytes(value: number) {
  if (value === 0) {
    return "0 B";
  }

  return `${value > 0 ? "+" : "-"}${formatBytes(Math.abs(value))}`;
}

function formatSignedNumber(value: number) {
  if (value === 0) {
    return "0";
  }

  return `${value > 0 ? "+" : "-"}${Math.abs(value).toLocaleString("pt-BR")}`;
}

function formatInventoryFileAge(item: InventoryFileCandidate) {
  const age = item.ageDays === null || item.ageDays === undefined ? "idade indisponível" : `${item.ageDays.toLocaleString("pt-BR")} dia(s)`;

  if (item.modifiedUtc) {
    return `${age} desde última modificação`;
  }

  if (item.accessedUtc) {
    return `${age} desde último acesso`;
  }

  return age;
}

function formatInventoryCategory(category: string) {
  const labels: Record<string, string> = {
    audio: "Áudio",
    binarios: "Binários",
    compactados: "Compactados",
    dados: "Dados",
    "dados sensiveis": "Dados sensíveis",
    documentos: "Documentos",
    imagens: "Imagens",
    "imagens de disco": "Imagens de disco",
    instaladores: "Instaladores",
    outros: "Outros",
    "projetos cad": "Projetos CAD",
    scripts: "Scripts",
    videos: "Vídeos"
  };

  return labels[category] ?? category;
}

function getOperationalPosture(
  openAlerts: number,
  criticalAlerts: number,
  offlineAgents: number,
  highestAnomaly: BaselineAnomalyItem | null
) {
  if (criticalAlerts > 0 || offlineAgents > 0 || (highestAnomaly?.deltaPercent ?? 0) > 150) {
    return {
      label: "Atenção alta",
      detail: `${criticalAlerts} críticos, ${offlineAgents} agentes em atenção e maior desvio de ${Math.round(highestAnomaly?.deltaPercent ?? 0)}%.`,
      tone: "danger" as const
    };
  }

  if (openAlerts > 0 || (highestAnomaly?.deltaPercent ?? 0) > 60) {
    return {
      label: "Monitorar",
      detail: `${openAlerts} alertas abertos e desvio relevante no período.`,
      tone: "warning" as const
    };
  }

  return {
    label: "Estável",
    detail: "Sem pressão relevante no recorte atual.",
    tone: "neutral" as const
  };
}

function labelForPeriod(periodHours: string) {
  return ({
    "1": "Última hora",
    "6": "Últimas 6 horas",
    "24": "Últimas 24 horas",
    "168": "Últimos 7 dias",
    "720": "Últimos 30 dias"
  } as Record<string, string>)[periodHours] ?? "Período customizado";
}

function buildFilterSummary(filters: ActivitySummaryFilters) {
  const parts = [filters.server, filters.share, filters.user, filters.action].filter(Boolean);
  return parts.length > 0 ? parts.join(" · ") : "Sem filtro adicional";
}

function buildInvestigationSummary(filters: InvestigationFilters) {
  const periodLabel = filters.periodMode === "custom"
    ? [filters.fromDate, filters.toDate && filters.toDate !== filters.fromDate ? filters.toDate : ""].filter(Boolean).join(" até ")
    : labelForPeriod(filters.periodHours);
  const parts = [periodLabel, filters.server, filters.user, filters.path, filters.action].filter(Boolean);
  return parts.length > 0 ? parts.join(" · ") : "Sem filtro adicional";
}

function filterAlertOperations(alert: FileServerAlert, events: FileAuditEvent[]) {
  if (alert.samplePaths.length === 0) {
    return events;
  }

  return events.filter((event) =>
    alert.samplePaths.some((path) => matchesAlertPath(path, event.path) || matchesAlertPath(path, event.previousPath))
  );
}

function matchesAlertPath(referencePath: string, value?: string | null) {
  if (!value) {
    return false;
  }

  const left = referencePath.toLowerCase();
  const right = value.toLowerCase();
  return right === left || right.startsWith(left);
}

function getTopEventAction(events: FileAuditEvent[]) {
  if (events.length === 0) {
    return null;
  }

  const counts = new Map<string, number>();
  for (const event of events) {
    counts.set(event.action, (counts.get(event.action) ?? 0) + 1);
  }

  return [...counts.entries()].sort((left, right) => right[1] - left[1])[0]?.[0] ?? null;
}

function validateReportFilters(
  filters: ReportFilters,
  setError: (error: string | null) => void,
  onNotify: (notice: Notice | null) => void
) {
  if (filters.periodMode === "custom" && !filters.fromDate) {
    setError("Selecione pelo menos a data inicial para consultar um dia ou intervalo.");
    onNotify({ tone: "warning", message: "Escolha a data inicial para abrir o relatorio." });
    return false;
  }

  if (filters.periodMode === "custom" && filters.toDate && filters.toDate < filters.fromDate) {
    setError("A data final não pode ser anterior à data inicial.");
    onNotify({ tone: "warning", message: "Revise o intervalo informado antes de consultar." });
    return false;
  }

  return true;
}

function buildReportGroups(events: DisplayEvent[], groupBy: ReportGrouping) {
  const counts = new Map<string, number>();

  for (const event of events) {
    const label = getReportGroupValue(event, groupBy);
    counts.set(label, (counts.get(label) ?? 0) + 1);
  }

  return [...counts.entries()]
    .map(([label, count]) => ({ label, count }))
    .sort((left, right) => right.count - left.count || left.label.localeCompare(right.label, "pt-BR"));
}

function buildGeneratedReport(
  title: string,
  filters: ReportFilters,
  events: DisplayEvent[],
  inventorySummary: InventorySummary | null
): GeneratedReport {
  const topActions = buildReportGroups(events, "action").slice(0, 5);
  const topUsers = buildReportGroups(events, "user").slice(0, 5);
  const topPaths = buildReportGroups(events, "path").slice(0, 5);
  const deletedCount = events.filter((event) => event.action === "deleted").length;
  const renamedCount = events.filter((event) => event.action === "renamed").length;
  const movedCount = events.filter((event) => event.action === "moved").length;
  const deniedCount = events.filter((event) => (event.result ?? "").toLowerCase() === "denied").length;
  const dominantAction = topActions[0]?.label ?? "sem predominio";
  const uniqueUsers = new Set(events.map((event) => event.user).filter(Boolean)).size;
  const uniquePaths = new Set(events.map((event) => event.path).filter(Boolean)).size;
  const criticalCount = events.filter((event) => (event.severity ?? "").toLowerCase() === "critical").length;
  const warningCount = events.filter((event) => (event.severity ?? "").toLowerCase() === "warning").length;
  const topHosts = buildReportGroups(events, "sourceHost").slice(0, 3);
  const highlights: string[] = [];
  const risks: string[] = [];
  const nextSteps: string[] = [];

  if (events.length > 0) {
    highlights.push(`${events.length.toLocaleString("pt-BR")} evento(s) correlacionado(s) no recorte, com predominio de ${dominantAction.toLowerCase()}.`);
  }

  if (topUsers[0]) {
    highlights.push(`Maior concentracao de atividade em ${topUsers[0].label}, com ${topUsers[0].count.toLocaleString("pt-BR")} acao(oes).`);
  }

  if (topPaths[0]) {
    highlights.push(`Area mais impactada: ${topPaths[0].label} com ${topPaths[0].count.toLocaleString("pt-BR")} evento(s).`);
  }

  if (inventorySummary?.executiveOverview.headlines?.length) {
    highlights.push(...inventorySummary.executiveOverview.headlines.slice(0, 2));
  }

  if (deletedCount > 0) {
    risks.push(`${deletedCount.toLocaleString("pt-BR")} exclusao(oes) aparecem no periodo e merecem validacao de contexto e autoria.`);
  }

  if (renamedCount + movedCount > 0) {
    risks.push(`${(renamedCount + movedCount).toLocaleString("pt-BR")} movimento(s) de rename ou deslocamento podem indicar reorganizacao ou alteracao em massa.`);
  }

  if (deniedCount > 0) {
    risks.push(`${deniedCount.toLocaleString("pt-BR")} tentativa(s) negada(s) aparecem no recorte e pedem revisao de permissao ou origem.`);
  }

  if (inventorySummary?.recommendations?.length) {
    risks.push(...inventorySummary.recommendations.slice(0, 2).map((item) => item.detail));
  }

  if (topPaths[0]) {
    nextSteps.push(`Revisar a area ${topPaths[0].label} por concentrar o maior volume de eventos no recorte.`);
  }

  if (topUsers[0]) {
    nextSteps.push(`Validar com ${topUsers[0].label} o contexto operacional das principais acoes observadas.`);
  }

  if (inventorySummary?.executiveOverview.priorities?.length) {
    nextSteps.push(...inventorySummary.executiveOverview.priorities.slice(0, 2).map((item) => item.title));
  }

  const executiveSummaryParts = [
    `${title} cobrindo ${summarizeReportFilters(filters) || "o recorte selecionado"}.`,
    events.length > 0
      ? `Foram observados ${events.length.toLocaleString("pt-BR")} evento(s), distribuido(s) por ${uniqueUsers.toLocaleString("pt-BR")} usuario(s) e ${uniquePaths.toLocaleString("pt-BR")} caminho(s).`
      : "Nenhum evento foi encontrado para o recorte informado.",
    inventorySummary
      ? `O inventario associado aponta ${formatBytes(inventorySummary.totalBytes)} monitorados, score ${inventorySummary.insight.score} e ${inventorySummary.recommendations.length.toLocaleString("pt-BR")} recomendacao(oes) aberta(s).`
      : "O resumo de inventario nao foi anexado a esta geracao."
  ];

  const sections = [
    {
      title: "Resumo executivo",
      items: dedupeText([
        `${events.length.toLocaleString("pt-BR")} evento(s) no periodo, com ${dominantAction.toLowerCase()} como comportamento dominante.`,
        topUsers[0] ? `${topUsers[0].label} foi o principal ator do recorte com ${topUsers[0].count.toLocaleString("pt-BR")} acao(oes).` : "",
        inventorySummary ? `O compartilhamento ${inventorySummary.share ?? "monitorado"} encerra o ciclo com score ${inventorySummary.insight.score} e tom ${inventorySummary.insight.tone}.` : ""
      ]).slice(0, 4)
    },
    {
      title: "Evolucao do ciclo",
      items: dedupeText([
        inventorySummary?.comparison.previousStartedUtc
          ? `Comparado ao snapshot de ${formatDate(inventorySummary.comparison.previousStartedUtc)}, o volume variou ${formatPercent(inventorySummary.comparison.totalBytesGrowthPercent)}.`
          : "Ainda nao ha snapshot anterior para comparacao formal do ciclo.",
        inventorySummary ? `Delta de arquivos: ${formatSignedNumber(inventorySummary.growth.fileCountDelta)} e delta de pastas: ${formatSignedNumber(inventorySummary.growth.folderCountDelta)}.` : "",
        criticalCount > 0 ? `${criticalCount.toLocaleString("pt-BR")} evento(s) chegaram com severidade critica no recorte.` : "",
        warningCount > 0 ? `${warningCount.toLocaleString("pt-BR")} evento(s) chegaram com severidade de atencao.` : ""
      ]).slice(0, 4)
    },
    {
      title: "Capacidade e limpeza",
      items: dedupeText([
        inventorySummary?.topFolders[0]
          ? `Maior area por volume: ${inventorySummary.topFolders[0].path} com ${formatBytes(inventorySummary.topFolders[0].totalBytes)}.`
          : "",
        inventorySummary?.growth.topGrowingFolders[0]
          ? `Maior crescimento recente: ${inventorySummary.growth.topGrowingFolders[0].path} com ${formatSignedBytes(inventorySummary.growth.topGrowingFolders[0].totalBytesDelta)}.`
          : "",
        inventorySummary?.topInactiveFiles[0]
          ? `Arquivo mais antigo em destaque: ${inventorySummary.topInactiveFiles[0].name}, com ${formatInventoryFileAge(inventorySummary.topInactiveFiles[0])}.`
          : "",
        inventorySummary
          ? `${formatBytes(inventorySummary.governance.inactive365DaysBytes)} estao sem modificacao ha mais de 365 dias.`
          : ""
      ]).slice(0, 4)
    },
    {
      title: "Hotspots operacionais",
      items: dedupeText([
        topPaths[0] ? `O caminho ${topPaths[0].label} concentrou ${topPaths[0].count.toLocaleString("pt-BR")} evento(s).` : "",
        topActions[0] ? `A acao ${topActions[0].label.toLowerCase()} liderou a distribuicao do periodo.` : "",
        topHosts[0] ? `O host ${topHosts[0].label} aparece como principal origem com ${topHosts[0].count.toLocaleString("pt-BR")} ocorrencia(s).` : "",
        inventorySummary?.executiveOverview.storageHotspots[0]
          ? `A maior concentracao de dados segue em ${inventorySummary.executiveOverview.storageHotspots[0].label}.`
          : ""
      ]).slice(0, 4)
    },
    {
      title: "Risco e governanca",
      items: dedupeText([
        deletedCount > 0 ? `${deletedCount.toLocaleString("pt-BR")} exclusao(oes) aparecem no recorte.` : "",
        deniedCount > 0 ? `${deniedCount.toLocaleString("pt-BR")} tentativa(s) negada(s) exigem revisao de permissao ou origem.` : "",
        inventorySummary ? `${inventorySummary.governance.inactive365DaysFileCount.toLocaleString("pt-BR")} arquivo(s) estao frios ha mais de 365 dias.` : "",
        inventorySummary ? `${inventorySummary.governance.executableFileCount.toLocaleString("pt-BR")} executavel(is) ou script(s) seguem expostos no inventario.` : ""
      ]).slice(0, 4)
    },
    {
      title: "Plano de acao sugerido",
      items: dedupeText(nextSteps).slice(0, 5)
    }
  ];

  return {
    title,
    generatedAt: new Date().toISOString(),
    filtersSummary: summarizeReportFilters(filters),
    events,
    executiveSummary: executiveSummaryParts.join(" "),
    highlights: dedupeText(highlights).slice(0, 5),
    risks: dedupeText(risks).slice(0, 5),
    nextSteps: dedupeText(nextSteps).slice(0, 5),
    topActions,
    topUsers,
    topPaths,
    sections,
    inventorySummary
  };
}

function getReportGroupValue(event: DisplayEvent, groupBy: ReportGrouping) {
  switch (groupBy) {
    case "action":
      return event.displayAction ?? event.action;
    case "user":
      return event.user || "UNKNOWN";
    case "server":
      return event.server || "Sem servidor";
    case "share":
      return event.share || "Sem compartilhamento";
    case "sourceHost":
      return event.sourceHost || "Sem host";
    case "path":
      return getDirectoryName(event.path) || event.path || "Sem caminho";
    case "extension":
      return event.extension || "Sem extensao";
    case "severity":
      return event.severity || "Sem severidade";
    default:
      return "Outros";
  }
}

function labelForReportGroup(groupBy: ReportGrouping) {
  const labels: Record<ReportGrouping, string> = {
    action: "Ação",
    user: "Usuário",
    server: "Servidor",
    share: "Compartilhamento",
    sourceHost: "Host de origem",
    path: "Caminho",
    extension: "Extensão",
    severity: "Severidade"
  };

  return labels[groupBy];
}

function groupReportScenariosByCategory(scenarios: ReportScenario[]) {
  const orderedCategories = ["Executivo", "Governança", "Incidente", "Investigação"];
  const groups = new Map<string, ReportScenario[]>();

  for (const scenario of scenarios) {
    const category = getReportScenarioCategory(scenario.id);
    groups.set(category, [...(groups.get(category) ?? []), scenario]);
  }

  return orderedCategories
    .filter((category) => groups.has(category))
    .map((category) => ({ category, items: groups.get(category) ?? [] }));
}

function getReportScenarioCategory(id: ReportScenarioId) {
  if (id === "executive-qbr" || id === "capacity-cleanup" || id === "hot-folder") {
    return "Executivo";
  }

  if (id === "cold-data" || id === "permission-changes" || id === "executable-creation" || id === "after-hours-activity") {
    return "Governança";
  }

  if (id === "mass-delete" || id === "mass-rename" || id === "mass-move" || id === "recurrent-denied-access" || id === "suspicious-remote-access") {
    return "Incidente";
  }

  return "Investigação";
}

function dedupeText(items: string[]) {
  return [...new Set(items.filter(Boolean))];
}

function getDirectoryName(path: string) {
  const index = Math.max(path.lastIndexOf("\\"), path.lastIndexOf("/"));
  return index > 0 ? path.slice(0, index) : path;
}

function formatDetails(value?: string | null) {
  if (!value) {
    return "-";
  }

  try {
    const parsed = JSON.parse(value) as Record<string, unknown>;
    const preferred = ["server", "share", "path", "status", "priority", "rule", "severity"];
    const parts = preferred
      .filter((key) => parsed[key] !== undefined && parsed[key] !== null)
      .map((key) => `${key}: ${String(parsed[key])}`);

    return parts.length > 0 ? parts.join(" · ") : value;
  } catch {
    return value;
  }
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat("pt-BR", {
    dateStyle: "short",
    timeStyle: "medium"
  }).format(new Date(value));
}

function formatDateWithAge(value?: string | null, ageSeconds?: number | null) {
  if (!value) {
    return "Sem registro";
  }

  return `${formatDate(value)} · ${formatAge(ageSeconds)}`;
}

function formatAge(ageSeconds?: number | null) {
  if (ageSeconds === undefined || ageSeconds === null) {
    return "idade indisponível";
  }

  if (ageSeconds < 60) {
    return `${Math.round(ageSeconds)}s atrás`;
  }

  if (ageSeconds < 3600) {
    return `${Math.round(ageSeconds / 60)}min atrás`;
  }

  return `${Math.round(ageSeconds / 3600)}h atrás`;
}

function formatDurationMs(value: number) {
  if (value < 1000) {
    return `${Math.round(value)} ms`;
  }

  return `${(value / 1000).toLocaleString("pt-BR", { maximumFractionDigits: 1 })} s`;
}

function formatElapsedSeconds(value: number) {
  if (value < 60) {
    return `${Math.round(value)} s`;
  }

  if (value < 3600) {
    return `${Math.round(value / 60)} min`;
  }

  return `${(value / 3600).toLocaleString("pt-BR", { maximumFractionDigits: 1 })} h`;
}

function formatNullableCount(value: number | null) {
  return value === null ? "Indisponível" : value.toLocaleString("pt-BR");
}

function formatRuntimeStatus(value: string) {
  const labels: Record<string, string> = {
    healthy: "saudável",
    degraded: "atenção",
    critical: "crítico"
  };

  return labels[value] ?? value;
}

function formatAgentOperationalStatus(value: string) {
  const labels: Record<string, string> = {
    ok: "ok",
    attention: "atenção",
    critical: "crítico",
    unknown: "indefinido"
  };

  return labels[value] ?? value;
}

function formatUsn(value: Record<string, number>) {
  const entries = Object.entries(value);

  if (entries.length === 0) {
    return "Sem USN";
  }

  return entries.map(([volume, usn]) => `${volume} ${usn}`).join(", ");
}

function labelForStatus(value: string) {
  const labels: Record<string, string> = {
    planned: "planejado",
    active: "ativo",
    paused: "pausado",
    retired: "retirado"
  };

  return labels[value] ?? value;
}

function labelForPriority(value: string) {
  const labels: Record<string, string> = {
    low: "baixa",
    normal: "normal",
    high: "alta",
    critical: "crítica"
  };

  return labels[value] ?? value;
}

createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
