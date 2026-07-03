import React, { useEffect, useMemo, useState } from "react";
import { createRoot } from "react-dom/client";
import {
  Activity,
  AlertTriangle,
  BarChart3,
  Bell,
  CheckCircle2,
  ClipboardList,
  Database,
  Download,
  FileClock,
  FolderTree,
  KeyRound,
  LogOut,
  LockKeyhole,
  Plus,
  RefreshCcw,
  Search,
  Server,
  ShieldCheck,
  ShieldAlert,
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

type Notice = {
  tone: "success" | "warning" | "danger";
  message: string;
};

type GeneratedReport = {
  title: string;
  generatedAt: string;
  filtersSummary: string;
  events: DisplayEvent[];
};

type Tab = "dashboard" | "events" | "investigation" | "reports" | "alerts" | "agents" | "paths" | "capacity" | "audit" | "auth";
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

  async function loadData() {
    setLoading(true);
    setError(null);

    try {
      const [healthResult, eventsResult, alertsResult, alertRulesResult, agentsResult, pathsResult, summaryResult, anomaliesResult, auditResult] = await Promise.all([
        fetchJson<HealthResponse>("/health"),
        fetchJson<TimelinePageResponse>(buildTimelinePageUrl(eventsPage, eventFilter)),
        fetchJson<FileServerAlert[]>("/api/alerts?take=100"),
        fetchJson<AlertRuleConfig[]>("/api/alert-rules"),
        fetchJson<AgentHealth[]>("/api/agents/health"),
        fetchJson<MonitoredPath[]>("/api/monitored-paths"),
        fetchJson<ActivitySummary>(buildActivitySummaryUrl(summaryFilters)),
        fetchJson<BaselineAnomalyResponse>(buildBaselineAnomaliesUrl(summaryFilters)).catch((anomalyError) => {
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
      setLoading(false);
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
    const timer = window.setInterval(loadData, 30000);
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
            <button className="icon-button" onClick={loadData} disabled={loading} title="Atualizar dados">
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

        {activeTab === "agents" && <AgentsView agents={agents} />}

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

      <RetentionConfigPanel onNotify={onNotify} />
    </div>
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
    setError(null);
  }

  function startCustomReport() {
    setMode("custom");
    setFilters(createDefaultReportFilters());
    setGeneratedReport(null);
    setError(null);
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
      const result = await fetchJson<DisplayEvent[]>(buildReportEventsUrl(filters, 20000));
      setEvents(result);
      setPage(1);
      setSearched(true);
      onNotify({
        tone: result.length > 0 ? "success" : "warning",
        message: result.length > 0
          ? `Relatorio atualizado com ${result.length.toLocaleString("pt-BR")} evento(s).`
          : "Nenhum evento encontrado para este recorte."
      });
    } catch (searchError) {
      setError(searchError instanceof Error ? searchError.message : "Falha ao consultar relatorio.");
    } finally {
      setLoading(false);
    }
  }

  function generateReport() {
    if (!searched) {
      onNotify({ tone: "warning", message: "Consulte o recorte antes de gerar o relatorio." });
      return;
    }

    setGeneratedReport({
      title: reportTitle,
      generatedAt: new Date().toISOString(),
      filtersSummary: summarizeReportFilters(filters),
      events
    });
    onNotify({ tone: "success", message: "Previa do relatorio gerada com o mesmo recorte da investigacao." });
  }

  return (
    <div className="view-stack reports-view">
      <section className="executive-grid">
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

      <Panel title="Relatorios guiados" subtitle="Use cenarios prontos para montar rapidamente um recorte comum de investigacao.">
        <div className="report-mode-bar">
          <button className={mode === "guided" ? "active" : ""} type="button" onClick={() => setMode("guided")}>
            Guiados
          </button>
          <button className={mode === "custom" ? "active" : ""} type="button" onClick={startCustomReport}>
            Personalizado
          </button>
        </div>

        {mode === "guided" ? (
          <div className="report-card-grid">
            {reportScenarios.map((item) => (
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
        ) : (
          <div className="custom-report-note">
            <strong>Relatorio personalizado</strong>
            <span>Monte livremente o periodo, escopo, acao, origem e agrupamento antes de investigar.</span>
          </div>
        )}
      </Panel>

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
          <div className="report-actions">
            <button className="text-button" type="submit" disabled={loading}>
              <Search size={16} />
              Investigar
            </button>
            <button className="text-button" type="button" onClick={() => downloadReportCsv(filters, onNotify)}>
              <Download size={16} />
              Exportar CSV
            </button>
            <button className="text-button" type="button" onClick={generateReport}>
              <ClipboardList size={16} />
              Gerar relatorio
            </button>
          </div>
        </form>
      </Panel>

      {error && <div className="error-banner">{error}</div>}

      <section className="report-results-stack">
        <Panel title="Resumo por agrupamento" subtitle={`Top ${labelForReportGroup(filters.groupBy).toLowerCase()} no recorte investigado.`}>
          {groupedRows.length > 0 ? <ReportGroupList rows={groupedRows} total={events.length} /> : <EmptyState text="Consulte um recorte para gerar o resumo." />}
        </Panel>
        <Panel title="Linha do Tempo do Relatorio" subtitle="Eventos correlacionados pelo Core, prontos para validar antes da geracao.">
          {searched ? (
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
            <EmptyState text="Escolha um relatorio, ajuste os filtros e clique em Investigar." />
          )}
        </Panel>
      </section>

      {generatedReport && (
        <Panel title="Previa do relatorio" subtitle="Texto pronto para revisao, impressao ou salvamento em PDF pelo navegador.">
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
              <span>{uniqueUsers.toLocaleString("pt-BR")} usuario(s)</span>
              <span>{affectedPaths.toLocaleString("pt-BR")} caminho(s)</span>
            </div>
            <ol className="report-preview-events">
              {generatedReport.events.slice(0, 20).map((item) => (
                <li key={item.id}>
                  <strong>{item.displayAction ?? item.action}</strong>
                  <span>{formatDate(item.timestampUtc)} · {item.user} · {item.path}</span>
                </li>
              ))}
            </ol>
            {generatedReport.events.length > 20 && <small>Mostrando os 20 eventos mais recentes na previa. Use o CSV para a lista completa.</small>}
          </div>
        </Panel>
      )}
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

function AgentsView({ agents }: { agents: AgentHealth[] }) {
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

async function fetchJson<T>(path: string): Promise<T> {
  const response = await fetch(`${apiBaseUrl}${path}`, {
    headers: buildHeaders()
  });

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
  const tabs: Tab[] = ["dashboard", "events", "investigation", "reports"];

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
