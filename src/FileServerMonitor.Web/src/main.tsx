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
  Plus,
  RefreshCcw,
  Search,
  Server,
  ShieldAlert,
  Trash2
} from "lucide-react";
import "./styles.css";

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
  backlogWarningThreshold: number;
  isStale: boolean;
  staleAfterMinutes: number;
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

type Notice = {
  tone: "success" | "warning" | "danger";
  message: string;
};

type Tab = "dashboard" | "events" | "investigation" | "alerts" | "agents" | "paths" | "audit";

const emptyMonitoredPathForm: MonitoredPathForm = {
  server: "FS01",
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

function App() {
  const [activeTab, setActiveTab] = useState<Tab>("dashboard");
  const [health, setHealth] = useState<HealthResponse | null>(null);
  const [events, setEvents] = useState<FileAuditEvent[]>([]);
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
  const [summaryFilters, setSummaryFilters] = useState<ActivitySummaryFilters>(defaultSummaryFilters);

  async function loadData() {
    setLoading(true);
    setError(null);

    try {
      const [healthResult, eventsResult, alertsResult, alertRulesResult, agentsResult, pathsResult, summaryResult, anomaliesResult, auditResult] = await Promise.all([
        fetchJson<HealthResponse>("/health"),
        fetchJson<FileAuditEvent[]>("/api/events?take=100"),
        fetchJson<FileServerAlert[]>("/api/alerts?take=100"),
        fetchJson<AlertRuleConfig[]>("/api/alert-rules"),
        fetchJson<AgentHealth[]>("/api/agents/health"),
        fetchJson<MonitoredPath[]>("/api/monitored-paths"),
        fetchJson<ActivitySummary>(buildActivitySummaryUrl(summaryFilters)),
        fetchJson<BaselineAnomalyResponse>(buildBaselineAnomaliesUrl(summaryFilters)),
        fetchJson<AdminAuditEntry[]>("/api/admin-audit?take=100")
      ]);

      setHealth(healthResult);
      setEvents(eventsResult);
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
    loadData();
    const timer = window.setInterval(loadData, 30000);
    return () => window.clearInterval(timer);
  }, [summaryFilters]);

  useEffect(() => {
    if (!notice) {
      return undefined;
    }

    const timer = window.setTimeout(() => setNotice(null), 4500);
    return () => window.clearTimeout(timer);
  }, [notice]);

  const openAlerts = alerts.filter((alert) => alert.status === "open");
  const criticalAlerts = openAlerts.filter((alert) => alert.severity === "critical");
  const offlineAgents = agents.filter((agent) => agent.isStale || agent.status !== "running" || agent.pendingQueueEvents >= agent.backlogWarningThreshold);

  const filteredEvents = useMemo(() => {
    const filter = eventFilter.trim().toLowerCase();

    if (!filter) {
      return events;
    }

    return events.filter((event) =>
      [event.server, event.share, event.path, event.user, event.action, event.source]
        .filter(Boolean)
        .some((value) => value.toLowerCase().includes(filter))
    );
  }, [eventFilter, events]);

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
          <TabButton icon={<FileClock size={18} />} active={activeTab === "events"} onClick={() => setActiveTab("events")} meta={filteredEvents.length.toLocaleString("pt-BR")}>
            Eventos
          </TabButton>
          <TabButton icon={<Search size={18} />} active={activeTab === "investigation"} onClick={() => setActiveTab("investigation")} meta="até 500">
            Investigação
          </TabButton>
          <TabButton icon={<Bell size={18} />} active={activeTab === "alerts"} onClick={() => setActiveTab("alerts")} meta={openAlerts.length.toLocaleString("pt-BR")}>
            Alertas
          </TabButton>
          <TabButton icon={<Server size={18} />} active={activeTab === "agents"} onClick={() => setActiveTab("agents")} meta={offlineAgents.length.toLocaleString("pt-BR")}>
            Agentes
          </TabButton>
          <TabButton icon={<FolderTree size={18} />} active={activeTab === "paths"} onClick={() => setActiveTab("paths")} meta={monitoredPaths.length.toLocaleString("pt-BR")}>
            Caminhos
          </TabButton>
          <TabButton icon={<ClipboardList size={18} />} active={activeTab === "audit"} onClick={() => setActiveTab("audit")} meta={adminAudit.length.toLocaleString("pt-BR")}>
            Auditoria
          </TabButton>
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
              <span className="pill">eventos {events.length.toLocaleString("pt-BR")}</span>
              <span className={`badge ${criticalAlerts.length > 0 ? "critical" : openAlerts.length > 0 ? "warning" : "low"}`}>
                alertas {openAlerts.length.toLocaleString("pt-BR")}
              </span>
              <span className={`status ${offlineAgents.length > 0 ? "degraded" : "running"}`}>
                agentes {offlineAgents.length > 0 ? `${offlineAgents.length} atenção` : "estáveis"}
              </span>
            </div>
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
          <EventsView events={filteredEvents} filter={eventFilter} onFilterChange={setEventFilter} onNotify={setNotice} />
        )}

        {activeTab === "investigation" && <InvestigationView onNotify={setNotice} />}

        {activeTab === "alerts" && <AlertsView alerts={alerts} rules={alertRules} onChanged={loadData} onAcknowledge={loadData} onNotify={setNotice} />}

        {activeTab === "agents" && <AgentsView agents={agents} />}

        {activeTab === "paths" && <MonitoredPathsView paths={monitoredPaths} onChanged={loadData} onNotify={setNotice} />}

        {activeTab === "audit" && <AdminAuditView entries={adminAudit} />}
      </section>
    </main>
  );
}

function Dashboard({
  health,
  events,
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
  openAlerts: FileServerAlert[];
  criticalAlerts: FileServerAlert[];
  offlineAgents: AgentHealth[];
  activitySummary: ActivitySummary | null;
  baselineAnomalies: BaselineAnomalyResponse | null;
  summaryFilters: ActivitySummaryFilters;
  onSummaryFiltersChange: (filters: ActivitySummaryFilters) => void;
  onNotify: (notice: Notice | null) => void;
}) {
  const latestEvents = events.slice(0, 8);
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
            <input value={summaryFilters.server} onChange={(event) => updateFilter("server", event.target.value)} placeholder="FS01" />
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
  onNotify
}: {
  events: FileAuditEvent[];
  filter: string;
  onFilterChange: (value: string) => void;
  onNotify: (notice: Notice | null) => void;
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
        <EventTable events={events} />
      </Panel>
    </div>
  );
}

function InvestigationView({ onNotify }: { onNotify: (notice: Notice | null) => void }) {
  const [filters, setFilters] = useState<InvestigationFilters>(defaultInvestigationFilters);
  const [events, setEvents] = useState<FileAuditEvent[]>([]);
  const [searched, setSearched] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const uniqueUsers = useMemo(() => new Set(events.map((event) => event.user).filter(Boolean)).size, [events]);
  const dominantAction = useMemo(() => getTopEventAction(events), [events]);

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
      const result = await fetchJson<FileAuditEvent[]>(buildInvestigationUrl(filters));
      setEvents(result);
      setSearched(true);
      onNotify({
        tone: result.length > 0 ? "success" : "warning",
        message: result.length > 0 ? `Investigação atualizada com ${result.length.toLocaleString("pt-BR")} evento(s).` : "Nenhum evento encontrado no recorte consultado."
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
          value={searched ? events.length.toLocaleString("pt-BR") : "-"}
          detail={searched ? "Resultado do último recorte consultado." : "Faça a primeira busca para abrir a linha investigativa."}
          tone={events.length > 0 ? "warning" : "neutral"}
        />
        <ExecutiveCard
          title="Usuários no Recorte"
          value={searched ? uniqueUsers.toLocaleString("pt-BR") : "-"}
          detail={dominantAction ? `Ação dominante: ${dominantAction}.` : "Sem ação dominante até o momento."}
          tone={events.length > 100 ? "danger" : "neutral"}
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
            <input value={filters.server} onChange={(event) => setFilters({ ...filters, server: event.target.value })} placeholder="FS01" />
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
        {searched ? <InvestigationTable events={events} /> : <EmptyState text="Preencha os filtros e consulte para iniciar a investigação." />}
      </Panel>
    </div>
  );
}

function AlertsView({
  alerts,
  rules,
  onChanged,
  onAcknowledge,
  onNotify
}: {
  alerts: FileServerAlert[];
  rules: AlertRuleConfig[];
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

      <Panel title="Regras de Alerta" subtitle="Ajuste thresholds, escopo, exceções e janelas operacionais sem sair da tela.">
        <p className="inline-note">Os tipos de alerta já vêm prontos. Aqui você ajusta as regras existentes e o comportamento de cada uma.</p>
        <AlertRulesEditor rules={rules} onChanged={onChanged} onNotify={onNotify} />
      </Panel>
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
                {alert.status === "open" && (
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
                  placeholder="FS01"
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
  const staleAgents = agents.filter((agent) => agent.isStale || ["stale", "backlog", "degraded"].includes(agent.status)).length;
  const queuedAgents = agents.filter((agent) => agent.pendingQueueEvents > 0).length;
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
          detail={`${queuedAgents} com fila pendente ou pressão de envio.`}
          tone={staleAgents > 0 ? "danger" : queuedAgents > 0 ? "warning" : "neutral"}
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
                  <dt>Status</dt>
                  <dd className={`status ${agent.status}`}>{agent.isStale ? "heartbeat atrasado" : agent.status}</dd>
                </div>
                <div>
                  <dt>Heartbeat</dt>
                  <dd>{agent.lastHeartbeatUtc ? formatDate(agent.lastHeartbeatUtc) : "Sem registro"}</dd>
                </div>
                <div>
                  <dt>Limite</dt>
                  <dd>{agent.staleAfterMinutes} min</dd>
                </div>
                <div>
                  <dt>RecordId</dt>
                  <dd>{agent.lastRecordId}</dd>
                </div>
                <div>
                  <dt>Fila</dt>
                  <dd className={agent.pendingQueueEvents > 0 ? "queue-warning" : ""}>
                    {agent.pendingQueueEvents >= 0 ? agent.pendingQueueEvents : "indisponível"}
                  </dd>
                </div>
                <div>
                  <dt>Limite fila</dt>
                  <dd>{agent.backlogWarningThreshold}</dd>
                </div>
                <div>
                  <dt>Último envio</dt>
                  <dd>{agent.lastSuccessfulSendUtc ? formatDate(agent.lastSuccessfulSendUtc) : "Sem envio"}</dd>
                </div>
                <div>
                  <dt>USN</dt>
                  <dd>{formatUsn(agent.lastUsnByVolume)}</dd>
                </div>
              </dl>
              {agent.message && <p className="agent-message">{agent.message}</p>}
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
  onChanged,
  onNotify
}: {
  paths: MonitoredPath[];
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

function EventTable({ events, compact = false }: { events: FileAuditEvent[]; compact?: boolean }) {
  const displayEvents = useMemo(() => buildDisplayEvents(events), [events]);

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
          {displayEvents.map((event) => (
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
      {displayEvents.length === 0 && <EmptyState text="Nenhum evento encontrado." />}
    </div>
  );
}

function InvestigationTable({ events }: { events: FileAuditEvent[] }) {
  const displayEvents = useMemo(() => buildDisplayEvents(events), [events]);

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
          {displayEvents.map((event) => (
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
      {displayEvents.length === 0 && <EmptyState text="Nenhum evento encontrado para os filtros informados." />}
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

function Panel({ title, subtitle, children }: { title: string; subtitle?: string; children: React.ReactNode }) {
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
      return raw;
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

  return `/api/events?${params.toString()}`;
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
    await downloadCsv(`${apiBaseUrl}/api/events/export.csv?take=1000`, `fileserver-events-${new Date().toISOString().slice(0, 10)}.csv`);
    onNotify({ tone: "success", message: "Exportação de eventos iniciada." });
  } catch (error) {
    console.error(error);
    onNotify({ tone: "danger", message: "Nao foi possivel exportar os eventos agora." });
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

  if (apiKey) {
    headers["X-Api-Key"] = apiKey;
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

function titleForTab(tab: Tab) {
  const titles: Record<Tab, string> = {
    dashboard: "Dashboard",
    events: "Eventos",
    investigation: "Investigação",
    alerts: "Alertas",
    agents: "Agentes",
    paths: "Caminhos Monitorados",
    audit: "Auditoria Administrativa"
  };

  return titles[tab];
}

function buildDisplayEvents(events: FileAuditEvent[]) {
  const ordered = deduplicateRawEvents(events).sort((left, right) => new Date(right.timestampUtc).getTime() - new Date(left.timestampUtc).getTime());
  const consumed = new Set<number>();
  const display: DisplayEvent[] = [];
  const emittedSemanticKeys = new Set<string>();
  const correlationWindowMs = 15_000;

  for (let index = 0; index < ordered.length; index++) {
    if (consumed.has(index)) {
      continue;
    }

    const current = ordered[index];
    const cluster = ordered
      .map((event, clusterIndex) => ({ event, clusterIndex }))
      .filter(({ clusterIndex }) => !consumed.has(clusterIndex))
      .filter(({ event }) => event.server === current.server && event.share === current.share)
      .filter(({ event }) => Math.abs(new Date(event.timestampUtc).getTime() - new Date(current.timestampUtc).getTime()) <= correlationWindowMs);

    if (isOperationalNoise(current)) {
      consumed.add(index);
      continue;
    }

    if (isTransientRenameNoise(current, cluster)) {
      consumed.add(index);
      continue;
    }

    const explicitTransition = tryBuildExplicitTransition(
      current,
      cluster.filter(({ event }) => !isOperationalNoise(event))
    );
    if (explicitTransition) {
      explicitTransition.consumedIndexes.forEach((clusterIndex) => consumed.add(clusterIndex));
      emittedSemanticKeys.add(getSemanticEventKey(explicitTransition.event));
      display.push(explicitTransition.event);
      continue;
    }

    if (current.source === "windows-security-log" && current.action === "deleted" && isFileLikePath(current.path)) {
      const condensedTransition = tryBuildFileTransition(current, cluster);
      if (condensedTransition) {
        condensedTransition.consumedIndexes.forEach((clusterIndex) => consumed.add(clusterIndex));
        emittedSemanticKeys.add(getSemanticEventKey(condensedTransition.event));
        display.push(condensedTransition.event);
        continue;
      }
    }

    if (isProvisionalDocumentNoise(current, ordered)) {
      consumed.add(index);
      continue;
    }

    if (isRedundantRenameAfterCreation(current, ordered)) {
      consumed.add(index);
      continue;
    }

    if (isRedundantDeletedNoise(current, cluster) || isRedundantCreationNoise(current, cluster)) {
      consumed.add(index);
      continue;
    }

    if (isRootOnlyNoise(current, cluster)) {
      consumed.add(index);
      continue;
    }

    if (current.source.includes("usn-journal") && isRenameLikeAction(current.action)) {
      const clusterByUser = cluster
        .filter(({ event }) => event.user === current.user || event.user === "UNKNOWN");

      const deletedMatch = clusterByUser.find(({ event }) =>
        event.source === "windows-security-log"
        && event.action === "deleted"
        && event.path !== current.path
        && isFileLikePath(event.path)
      );
      const usnFileCandidates = clusterByUser
        .filter(({ event }) =>
          event.source.includes("usn-journal")
          && isRenameLikeAction(event.action)
          && isFileLikePath(event.path))
        .map(({ event }) => event.path)
        .filter(Boolean)
        .filter((path, pathIndex, paths) => paths.findIndex((candidate) => candidate === path) === pathIndex);

      if (deletedMatch && usnFileCandidates.length >= 2) {
        const previousPath = deletedMatch.event.path;
        const nextPath = usnFileCandidates.find((path) => path !== previousPath) ?? current.path;
        const isProvisionalOrigin = isProvisionalDocumentName(previousPath) || isProvisionalFolderName(previousPath);
        const consumedIndexes = clusterByUser
          .filter(({ event }) =>
            (event.source.includes("usn-journal") && usnFileCandidates.includes(event.path))
            || (event.source === "windows-security-log" && event.action === "deleted" && event.path === previousPath)
            || (event.source === "windows-security-log" && event.action === "created_or_appended" && event.path === getParentPath(previousPath))
          )
          .map(({ clusterIndex }) => clusterIndex);

        consumedIndexes.forEach((clusterIndex) => consumed.add(clusterIndex));
        display.push({
          ...current,
          id: `${current.id}-renamed`,
          action: isProvisionalOrigin ? "created" : isMove(previousPath, nextPath) ? "moved" : "renamed",
          previousPath: isProvisionalOrigin ? null : previousPath,
          path: nextPath,
          source: "usn-journal+security-log",
          displayAction: isProvisionalOrigin ? "Criação" : isMove(previousPath, nextPath) ? "Movido" : "Renomeado",
          displayTarget: getLeafName(nextPath)
        });
        continue;
      }
    }

    if (isRedundantParentCreate(current, cluster)) {
      consumed.add(index);
      continue;
    }

    if (isUnknownUsnNoise(current, cluster)) {
      consumed.add(index);
      continue;
    }

    if (isRedundantChangedNoise(current, cluster)) {
      consumed.add(index);
      continue;
    }

    const displayEvent = {
      ...current,
      displayAction: formatAction(current.action, current),
      displayTarget: shouldShowActionTarget(current) ? getLeafName(current.path) : undefined
    } satisfies DisplayEvent;
    const semanticKey = getSemanticEventKey(displayEvent);

    if (emittedSemanticKeys.has(semanticKey)) {
      consumed.add(index);
      continue;
    }

    emittedSemanticKeys.add(semanticKey);
    display.push(displayEvent);
  }

  return refineDisplayEvents(display);
}

function refineDisplayEvents(events: DisplayEvent[]) {
  const normalized = events.map((event) => normalizeTransitionDirection(event, events));
  const inferred = inferDisplayTransitions(normalized);
  const withInferredRenames = inferMissingRenamesBeforeMoves(inferred);

  return withInferredRenames.filter((event, _, allEvents) =>
    !isTransientDisplayNoise(event)
    && !isRedundantDisplayDeleted(event, allEvents)
    && !isSuspiciousMoveEcho(event, allEvents)
    && !isRedundantDisplayFolderChangedEcho(event, allEvents)
    && !isRedundantDisplayProvisionalCreate(event, allEvents)
    && !isRedundantDisplayRenameAfterCreate(event, allEvents)
    && !isRedundantDisplayCreateEcho(event, allEvents)
    && !isRedundantDisplayChangedEcho(event, allEvents)
  );
}

function inferMissingRenamesBeforeMoves(events: DisplayEvent[]) {
  const synthetic: DisplayEvent[] = [];
  const renameWindowMs = 120_000;

  for (const moveEvent of events) {
    const movePreviousPath = moveEvent.previousPath ?? "";
    if (moveEvent.action !== "moved" || !movePreviousPath || !isFileLikePath(movePreviousPath)) {
      continue;
    }

    const moveTime = new Date(moveEvent.timestampUtc).getTime();
    const alreadyHasRename = events.some((event) =>
      event.id !== moveEvent.id
      && event.action === "renamed"
      && normalizePath(event.path) === normalizePath(movePreviousPath)
      && Math.abs(new Date(event.timestampUtc).getTime() - moveTime) <= renameWindowMs);

    if (alreadyHasRename) {
      continue;
    }

    const createdBeforeMove = events
      .filter((event) =>
        (event.action === "created" || event.action === "created_or_appended")
        && isFileLikePath(event.path)
        && normalizePath(event.path) !== normalizePath(movePreviousPath)
        && normalizePath(getParentPath(event.path)) === normalizePath(getParentPath(movePreviousPath))
        && getExtension(getLeafName(event.path)) === getExtension(getLeafName(movePreviousPath))
        && new Date(event.timestampUtc).getTime() <= moveTime
        && moveTime - new Date(event.timestampUtc).getTime() <= renameWindowMs)
      .sort((left, right) => new Date(right.timestampUtc).getTime() - new Date(left.timestampUtc).getTime())[0];

    if (!createdBeforeMove) {
      continue;
    }

    synthetic.push({
      ...moveEvent,
      id: `${moveEvent.id}-inferred-rename-before-move`,
      action: "renamed",
      previousPath: createdBeforeMove.path,
      path: movePreviousPath,
      timestampUtc: new Date(Math.max(new Date(createdBeforeMove.timestampUtc).getTime() + 1_000, moveTime - 1_000)).toISOString(),
      source: moveEvent.source,
      displayAction: "Renomeado",
      displayTarget: getLeafName(movePreviousPath)
    });
  }

  return [...events, ...synthetic]
    .sort((left, right) => new Date(right.timestampUtc).getTime() - new Date(left.timestampUtc).getTime());
}

function isTransientDisplayNoise(event: DisplayEvent) {
  if (isTransientArtifactPath(event.path) || isTransientArtifactPath(event.previousPath ?? "")) {
    return true;
  }

  if ((event.action === "moved" || event.action === "renamed") && event.previousPath) {
    const currentParentSegments = normalizePath(getParentPath(event.path)).split("\\").filter(Boolean);
    const previousParentSegments = normalizePath(getParentPath(event.previousPath)).split("\\").filter(Boolean);

    if (currentParentSegments.some(isTransientContainerSegment) || previousParentSegments.some(isTransientContainerSegment)) {
      return true;
    }
  }

  return false;
}

function normalizeTransitionDirection(event: DisplayEvent, events: DisplayEvent[]) {
  if ((event.action !== "moved" && event.action !== "renamed") || !event.previousPath) {
    return event;
  }

  const previousPath = event.previousPath ?? "";
  const nearbyDeletedOnCurrentPath = events.some((candidate) =>
    candidate.id !== event.id
    && candidate.action === "deleted"
    && candidate.user === event.user
    && Math.abs(new Date(candidate.timestampUtc).getTime() - new Date(event.timestampUtc).getTime()) <= 15_000
    && normalizePath(candidate.path) === normalizePath(event.path));

  const nearbyDeletedOnPreviousPath = events.some((candidate) =>
    candidate.id !== event.id
    && candidate.action === "deleted"
    && candidate.user === event.user
    && Math.abs(new Date(candidate.timestampUtc).getTime() - new Date(event.timestampUtc).getTime()) <= 15_000
    && normalizePath(candidate.path) === normalizePath(previousPath));

  if (nearbyDeletedOnCurrentPath && !nearbyDeletedOnPreviousPath) {
    const swappedPrevious = event.path;
    const swappedCurrent = previousPath;
    return {
      ...event,
      action: isMove(swappedPrevious, swappedCurrent) ? "moved" : "renamed",
      previousPath: swappedPrevious,
      path: swappedCurrent,
      displayAction: isMove(swappedPrevious, swappedCurrent) ? "Movido" : "Renomeado",
      displayTarget: getLeafName(swappedCurrent)
    };
  }

  return event;
}

function inferDisplayTransitions(events: DisplayEvent[]) {
  const ordered = [...events].sort((left, right) => new Date(right.timestampUtc).getTime() - new Date(left.timestampUtc).getTime());
  const suppressedIds = new Set<string>();
  const synthetic: DisplayEvent[] = [];
  const transitionWindowMs = 30_000;

  const folderTransitions = ordered.filter((event) =>
    (event.action === "created"
      || event.action === "created_or_appended"
      || event.action === "changed"
      || event.action === "modified")
    && isLikelyFolderPath(event.path));
  const deletedFiles = ordered.filter((event) =>
    event.action === "deleted"
    && isFileLikePath(event.path));

  for (const folder of folderTransitions) {
    const folderParent = normalizePath(getParentPath(folder.path));
    const folderTime = new Date(folder.timestampUtc).getTime();
    const movedFrom = deletedFiles
      .filter((deletedEvent) =>
        !suppressedIds.has(deletedEvent.id)
        && normalizePath(getParentPath(deletedEvent.path)) === folderParent
        && Math.abs(new Date(deletedEvent.timestampUtc).getTime() - folderTime) <= transitionWindowMs)
      .sort((left, right) =>
        Math.abs(new Date(left.timestampUtc).getTime() - folderTime)
        - Math.abs(new Date(right.timestampUtc).getTime() - folderTime))[0];

    if (!movedFrom) {
      continue;
    }

    const nextPath = `${folder.path}\\${getLeafName(movedFrom.path)}`;
    suppressedIds.add(movedFrom.id);
    synthetic.push({
      ...movedFrom,
      id: `${movedFrom.id}-moved-to-folder`,
      action: "moved",
      previousPath: movedFrom.path,
      path: nextPath,
      source: "usn-journal+security-log",
      displayAction: "Movido",
      displayTarget: getLeafName(nextPath)
    });
  }

  const transitionTargets = [...synthetic, ...ordered.filter((event) => event.action === "renamed" || event.action === "moved")];
  for (const transition of transitionTargets) {
    if (!transition.previousPath || !isFileLikePath(transition.previousPath)) {
      continue;
    }

    const transitionPreviousPath = transition.previousPath;
    const transitionPrevious = normalizePath(transitionPreviousPath);
    const transitionParent = normalizePath(getParentPath(transitionPreviousPath));
    const transitionTime = new Date(transition.timestampUtc).getTime();
    const renameFrom = deletedFiles
      .filter((deletedEvent) =>
        !suppressedIds.has(deletedEvent.id)
        && normalizePath(deletedEvent.path) !== transitionPrevious
        && normalizePath(getParentPath(deletedEvent.path)) === transitionParent
        && getExtension(getLeafName(deletedEvent.path)) === getExtension(getLeafName(transitionPreviousPath))
        && Math.abs(new Date(deletedEvent.timestampUtc).getTime() - transitionTime) <= transitionWindowMs)
      .sort((left, right) =>
        Math.abs(new Date(left.timestampUtc).getTime() - transitionTime)
        - Math.abs(new Date(right.timestampUtc).getTime() - transitionTime))[0];

    if (!renameFrom) {
      continue;
    }

    suppressedIds.add(renameFrom.id);
    synthetic.push({
      ...transition,
      id: `${transition.id}-renamed-before-transition`,
      action: "renamed",
      previousPath: renameFrom.path,
      path: transitionPreviousPath,
      timestampUtc: renameFrom.timestampUtc,
      user: renameFrom.user !== "UNKNOWN" ? renameFrom.user : transition.user,
      source: "usn-journal+security-log",
      displayAction: "Renomeado",
      displayTarget: getLeafName(transitionPreviousPath)
    });
  }

  for (const deletedEvent of ordered) {
    if (suppressedIds.has(deletedEvent.id) || deletedEvent.action !== "deleted" || !isFileLikePath(deletedEvent.path)) {
      continue;
    }

    const deletedTime = new Date(deletedEvent.timestampUtc).getTime();
    const renameTo = deletedFiles
      .filter((candidate) =>
        candidate.id !== deletedEvent.id
        && !suppressedIds.has(candidate.id)
        && normalizePath(candidate.path) !== normalizePath(deletedEvent.path)
        && normalizePath(getParentPath(candidate.path)) === normalizePath(getParentPath(deletedEvent.path))
        && getExtension(getLeafName(candidate.path)) === getExtension(getLeafName(deletedEvent.path))
        && Math.abs(new Date(candidate.timestampUtc).getTime() - deletedTime) <= transitionWindowMs)
      .sort((left, right) =>
        Math.abs(new Date(left.timestampUtc).getTime() - deletedTime)
        - Math.abs(new Date(right.timestampUtc).getTime() - deletedTime))[0];

    if (renameTo) {
      suppressedIds.add(deletedEvent.id);
      suppressedIds.add(renameTo.id);
      synthetic.push({
        ...renameTo,
        id: `${renameTo.id}-renamed-from-delete-pair`,
        action: "renamed",
        previousPath: deletedEvent.path,
        path: renameTo.path,
        timestampUtc: deletedEvent.timestampUtc,
        source: "usn-journal+security-log",
        displayAction: "Renomeado",
        displayTarget: getLeafName(renameTo.path)
      });
    }
  }

  return [...ordered.filter((event) => !suppressedIds.has(event.id)), ...synthetic]
    .sort((left, right) => new Date(right.timestampUtc).getTime() - new Date(left.timestampUtc).getTime());
}

function isRedundantDisplayDeleted(event: DisplayEvent, allEvents: DisplayEvent[]) {
  if (event.action !== "deleted") {
    return false;
  }

  return allEvents.some((candidate) =>
    candidate.id !== event.id
    && (candidate.action === "renamed" || candidate.action === "moved")
    && Math.abs(new Date(candidate.timestampUtc).getTime() - new Date(event.timestampUtc).getTime()) <= 15_000
    && normalizePath(candidate.previousPath) === normalizePath(event.path));
}

function isSuspiciousMoveEcho(event: DisplayEvent, allEvents: DisplayEvent[]) {
  if (event.action !== "moved" || !event.previousPath) {
    return false;
  }

  const previousPath = event.previousPath;
  const currentParent = normalizePath(getParentPath(event.path));
  const previousParent = normalizePath(getParentPath(previousPath));
  if (!previousParent.startsWith(`${currentParent}\\`)) {
    return false;
  }

  const eventTime = new Date(event.timestampUtc).getTime();
  const deletedPrevious = allEvents.some((candidate) =>
    candidate.id !== event.id
    && candidate.action === "deleted"
    && Math.abs(new Date(candidate.timestampUtc).getTime() - eventTime) <= 30_000
    && normalizePath(candidate.path) === normalizePath(previousPath));

  const destinationSignalWindowMs = 8_000;
  const destinationSignal = allEvents.some((candidate) =>
    candidate.id !== event.id
    && candidate.action !== "deleted"
    && Math.abs(new Date(candidate.timestampUtc).getTime() - eventTime) <= destinationSignalWindowMs
    && normalizePath(candidate.path) === normalizePath(event.path));

  return deletedPrevious && !destinationSignal;
}

function isRedundantDisplayFolderChangedEcho(event: DisplayEvent, allEvents: DisplayEvent[]) {
  if (!isLikelyFolderPath(event.path)) {
    return false;
  }

  if (event.action !== "changed" && event.action !== "modified") {
    return false;
  }

  const folderPath = normalizePath(event.path);
  const eventTime = new Date(event.timestampUtc).getTime();
  return allEvents.some((candidate) =>
    candidate.id !== event.id
    && candidate.action === "moved"
    && Math.abs(new Date(candidate.timestampUtc).getTime() - eventTime) <= 30_000
    && normalizePath(getParentPath(candidate.path)) === folderPath);
}

function isRedundantDisplayProvisionalCreate(event: DisplayEvent, allEvents: DisplayEvent[]) {
  if (event.action !== "created" && event.action !== "created_or_appended") {
    return false;
  }

  if (!isProvisionalDocumentName(event.path) && !isProvisionalFolderName(event.path)) {
    return false;
  }

  const parentPath = normalizePath(getParentPath(event.path));
  return allEvents.some((candidate) =>
    candidate.id !== event.id
    && Math.abs(new Date(candidate.timestampUtc).getTime() - new Date(event.timestampUtc).getTime()) <= 15_000
    && normalizePath(getParentPath(candidate.path)) === parentPath
    && !isProvisionalDocumentName(candidate.path)
    && !isProvisionalFolderName(candidate.path)
    && (candidate.action === "created" || candidate.action === "renamed" || candidate.action === "moved"));
}

function isRedundantDisplayRenameAfterCreate(event: DisplayEvent, allEvents: DisplayEvent[]) {
  if (event.action !== "renamed" && event.action !== "moved") {
    return false;
  }

  if (!event.previousPath) {
    return false;
  }

  if (!isProvisionalDocumentName(event.previousPath) && !isProvisionalFolderName(event.previousPath)) {
    return false;
  }

  return allEvents.some((candidate) =>
    candidate.id !== event.id
    && (candidate.action === "created" || candidate.action === "created_or_appended")
    && Math.abs(new Date(candidate.timestampUtc).getTime() - new Date(event.timestampUtc).getTime()) <= 15_000
    && normalizePath(candidate.path) === normalizePath(event.path));
}

function isRedundantDisplayCreateEcho(event: DisplayEvent, allEvents: DisplayEvent[]) {
  if (event.action !== "created" && event.action !== "created_or_appended") {
    return false;
  }

  return allEvents.some((candidate) =>
    candidate.id !== event.id
    && (candidate.action === "renamed" || candidate.action === "moved")
    && Math.abs(new Date(candidate.timestampUtc).getTime() - new Date(event.timestampUtc).getTime()) <= 15_000
    && normalizePath(candidate.path) === normalizePath(event.path));
}

function isRedundantDisplayChangedEcho(event: DisplayEvent, allEvents: DisplayEvent[]) {
  if (event.action !== "changed" && event.action !== "modified") {
    return false;
  }

  return allEvents.some((candidate) =>
    candidate.id !== event.id
    && Math.abs(new Date(candidate.timestampUtc).getTime() - new Date(event.timestampUtc).getTime()) <= 15_000
    && (
      normalizePath(candidate.path) === normalizePath(event.path)
      || normalizePath(candidate.previousPath) === normalizePath(event.path)
    )
    && (candidate.action === "renamed"
      || candidate.action === "moved"
      || candidate.action === "created"
      || candidate.action === "created_or_appended"
      || candidate.action === "deleted"));
}

function deduplicateRawEvents(events: FileAuditEvent[]) {
  const grouped = new Map<string, FileAuditEvent>();

  for (const event of events) {
    const timestamp = new Date(event.timestampUtc);
    timestamp.setMilliseconds(0);
    const key = [
      event.user,
      event.source,
      event.action,
      event.path,
      event.previousPath ?? "",
      timestamp.toISOString()
    ].join("|");

    const existing = grouped.get(key);

    if (!existing || getEventWeight(event) > getEventWeight(existing)) {
      grouped.set(key, event);
    }
  }

  return [...grouped.values()];
}

function getEventWeight(event: FileAuditEvent) {
  const sourceWeight = event.source.includes("usn-journal+security-log")
    ? 30
    : event.source.includes("usn-journal")
      ? 20
      : 10;
  const actionWeight = event.action === "moved" || event.action === "renamed"
    ? 30
    : event.action === "deleted"
      ? 20
      : event.action === "created_or_appended" || event.action === "created"
        ? 15
        : 5;

  return sourceWeight + actionWeight;
}

function getSemanticEventKey(event: FileAuditEvent) {
  const timestamp = new Date(event.timestampUtc);
  timestamp.setMilliseconds(0);

  const effectiveAction =
    event.action === "changed" || event.action === "modified"
      ? "changed"
      : event.action;

  return [
    event.user,
    event.source.includes("usn-journal") ? "usn" : event.source,
    effectiveAction,
    normalizePath(event.path),
    normalizePath(event.previousPath),
    timestamp.toISOString()
  ].join("|");
}

function getParentPath(path: string) {
  const segments = path.split("\\");
  return segments.length > 1 ? segments.slice(0, -1).join("\\") : path;
}

function isRenameLikeAction(action: string) {
  return action === "renamed" || action === "changed" || action === "modified";
}

function isMove(previousPath: string, nextPath: string) {
  return getParentPath(previousPath).toLowerCase() !== getParentPath(nextPath).toLowerCase();
}

function isOperationalNoise(event: FileAuditEvent) {
  const path = event.path.toLowerCase();
  const previousPath = (event.previousPath ?? "").toLowerCase();

  return path.endsWith("\\appsettings.agent.json")
    || path.endsWith("\\agent-state.json")
    || path.endsWith("\\pending-events.ndjson")
    || path.includes("\\logs\\")
    || path.endsWith("\\logs")
    || isTransientArtifactPath(event.path)
    || isTransientArtifactPath(previousPath);
}

function isLikelyFolderPath(path: string) {
  return !getLeafName(path).includes(".");
}

function isFileLikePath(path: string) {
  return getLeafName(path).includes(".");
}

function isRootOnlyNoise(
  event: FileAuditEvent,
  cluster: Array<{ event: FileAuditEvent; clusterIndex: number }>
) {
  if (!isLikelyFolderPath(event.path)) {
    return false;
  }

  if (isShareRootPath(event)) {
    return true;
  }

  return cluster.some(({ event: candidate }) =>
    candidate.id !== event.id
    && isFileLikePath(candidate.path)
    && normalizePath(getParentPath(candidate.path)) === normalizePath(event.path)
    && Math.abs(new Date(candidate.timestampUtc).getTime() - new Date(event.timestampUtc).getTime()) <= 15_000);
}

function isShareRootPath(event: FileAuditEvent) {
  const shareName = event.share.trim().toLowerCase();
  if (!shareName) {
    return false;
  }

  const leafName = getLeafName(event.path).toLowerCase();
  if (leafName !== shareName) {
    return false;
  }

  const parentPath = normalizePath(getParentPath(event.path));
  return /^[a-z]:$/i.test(parentPath)
    || /^\\\\[^\\]+$/.test(parentPath);
}

function isRedundantParentCreate(
  current: FileAuditEvent,
  cluster: Array<{ event: FileAuditEvent; clusterIndex: number }>
) {
  return current.source === "windows-security-log"
    && current.action === "created_or_appended"
    && cluster.some(({ event }) =>
      event.id !== current.id
      && event.server === current.server
      && event.share === current.share
      && event.user === current.user
      && (event.action === "deleted" || isRenameLikeAction(event.action))
      && getParentPath(event.path) === current.path);
}

function isUnknownUsnNoise(
  current: FileAuditEvent,
  cluster: Array<{ event: FileAuditEvent; clusterIndex: number }>
) {
  return current.source === "usn-journal"
    && current.user === "UNKNOWN"
    && (current.action === "changed" || current.action === "modified")
    && cluster.some(({ event }) =>
      event.id !== current.id
      && event.path === current.path
      && event.source !== "usn-journal"
      && event.action !== "changed"
      && event.action !== "modified");
}

function isProvisionalDocumentNoise(
  current: FileAuditEvent,
  ordered: FileAuditEvent[]
) {
  const isProvisionalName = isProvisionalDocumentName(current.path) || isProvisionalFolderName(current.path);
  if (!isProvisionalName) {
    return false;
  }

  if (current.action === "created" || current.action === "created_or_appended") {
    return true;
  }

  const currentParent = getParentPath(current.path);
  const currentTime = new Date(current.timestampUtc).getTime();

  return ordered.some((event) => {
    if (event.id === current.id) {
      return false;
    }

    if (Math.abs(new Date(event.timestampUtc).getTime() - currentTime) > 15_000) {
      return false;
    }

    if (!isFileLikePath(event.path) && !isLikelyFolderPath(event.path)) {
      return false;
    }

    if (getParentPath(event.path) !== currentParent) {
      return false;
    }

    if (isProvisionalDocumentName(event.path)) {
      return false;
    }

    if (isProvisionalFolderName(event.path)) {
      return false;
    }

    return event.action === "renamed"
      || event.action === "moved"
      || event.action === "deleted"
      || event.action === "created"
      || event.action === "created_or_appended"
      || event.action === "changed"
      || event.action === "modified";
  });
}

function isRedundantRenameAfterCreation(
  current: FileAuditEvent,
  ordered: FileAuditEvent[]
) {
  if (current.action !== "renamed") {
    return false;
  }

  if (!current.previousPath) {
    return false;
  }

  const provisionalOrigin = isProvisionalDocumentName(current.previousPath) || isProvisionalFolderName(current.previousPath);
  if (!provisionalOrigin) {
    return false;
  }

  const currentPath = normalizePath(current.path);
  const currentTime = new Date(current.timestampUtc).getTime();

  return ordered.some((event) =>
    event.id !== current.id
    && Math.abs(new Date(event.timestampUtc).getTime() - currentTime) <= 15_000
    && (event.action === "created" || event.action === "created_or_appended")
    && normalizePath(event.path) === currentPath);
}

function isRedundantDeletedNoise(
  current: FileAuditEvent,
  cluster: Array<{ event: FileAuditEvent; clusterIndex: number }>
) {
  if (current.action !== "deleted") {
    return false;
  }

  const currentPath = normalizePath(current.path);
  return cluster.some(({ event }) => {
    if (event.id === current.id) {
      return false;
    }

    if (event.action !== "renamed" && event.action !== "moved" && event.action !== "created") {
      return false;
    }

    return normalizePath(event.previousPath) === currentPath
      || normalizePath(event.path) === currentPath;
  });
}

function isRedundantCreationNoise(
  current: FileAuditEvent,
  cluster: Array<{ event: FileAuditEvent; clusterIndex: number }>
) {
  if (current.action !== "created_or_appended" && current.action !== "created") {
    return false;
  }

  const currentPath = normalizePath(current.path);
  return cluster.some(({ event }) => {
    if (event.id === current.id) {
      return false;
    }

    if (event.action !== "renamed" && event.action !== "moved" && event.action !== "created") {
      return false;
    }

    return normalizePath(event.path) === currentPath
      || normalizePath(event.previousPath) === currentPath;
  });
}

function isTransientRenameNoise(
  current: FileAuditEvent,
  cluster: Array<{ event: FileAuditEvent; clusterIndex: number }>
) {
  if (current.action !== "renamed" && current.action !== "moved") {
    return false;
  }

  const touchesTransientArtifact =
    isTransientArtifactPath(current.path)
    || isTransientArtifactPath(current.previousPath ?? "");

  if (!touchesTransientArtifact) {
    return false;
  }

  return cluster.some(({ event }) =>
    event.id !== current.id
    && event.action === "deleted"
    && isFileLikePath(event.path)
    && (
      normalizePath(event.path).startsWith(normalizePath(current.previousPath))
      || normalizePath(event.path).startsWith(normalizePath(current.path))
      || normalizePath(getParentPath(event.path)) === normalizePath(current.previousPath)
    ));
}

function isRedundantChangedNoise(
  current: FileAuditEvent,
  cluster: Array<{ event: FileAuditEvent; clusterIndex: number }>
) {
  if (!current.source.includes("usn-journal")) {
    return false;
  }

  if (current.action !== "changed" && current.action !== "modified") {
    return false;
  }

  const currentPath = normalizePath(current.path);
  const currentTimestamp = new Date(current.timestampUtc).getTime();
  const strongerEvent = cluster.find(({ event }) => {
    if (event.id === current.id) {
      return false;
    }

    const touchesSamePath =
      normalizePath(event.path) === currentPath
      || normalizePath(event.previousPath) === currentPath;

    if (!touchesSamePath) {
      return false;
    }

    if (event.action === "renamed" || event.action === "moved" || event.action === "deleted") {
      return true;
    }

    if ((event.action === "created" || event.action === "created_or_appended")
      && isFileLikePath(event.path)) {
      return true;
    }

    if (!event.source.includes("usn-journal")) {
      return false;
    }

    if (event.action !== "changed" && event.action !== "modified") {
      return false;
    }

    return new Date(event.timestampUtc).getTime() > currentTimestamp;
  });

  return Boolean(strongerEvent);
}

function tryBuildFileTransition(
  current: FileAuditEvent,
  cluster: Array<{ event: FileAuditEvent; clusterIndex: number }>
) {
  const relevant = cluster.filter(({ event }) => !isOperationalNoise(event));
  const explicitTransition = tryBuildExplicitTransition(current, relevant);
  if (explicitTransition) {
    return explicitTransition;
  }

  const securityDeleted = relevant
    .filter(({ event }) =>
      event.source === "windows-security-log"
      && event.action === "deleted"
      && isFileLikePath(event.path))
    .map(({ event, clusterIndex }) => ({ event, clusterIndex }));
  const usnCandidates = relevant
    .filter(({ event }) =>
      event.source.includes("usn-journal")
      && (event.action === "changed" || event.action === "modified" || event.action === "renamed" || event.action === "moved")
      && isFileLikePath(event.path)
      && !isTransientArtifactPath(event.path))
    .map(({ event, clusterIndex }) => ({ event, clusterIndex }));

  for (const deleted of securityDeleted) {
    const match = usnCandidates
      .filter(({ event }) => normalizePath(event.path) !== normalizePath(deleted.event.path))
      .map(({ event, clusterIndex }) => ({
        event,
        clusterIndex,
        score: getTransitionScore(deleted.event.path, event.path)
      }))
      .filter((candidate) => candidate.score > 0)
      .sort((left, right) => right.score - left.score)[0];

    if (!match) {
      continue;
    }

    const previousPath = deleted.event.path;
    const nextPath = match.event.path;
    const isProvisionalOrigin = isProvisionalDocumentName(previousPath) || isProvisionalFolderName(previousPath);
    const action = isMove(previousPath, nextPath) ? "moved" : "renamed";
    const displayAction = isProvisionalOrigin
      ? "Criação"
      : action === "moved"
        ? "Movido"
        : "Renomeado";
    const consumedIndexes = relevant
      .filter(({ event }) => shouldConsumeTransitionEvent(event, previousPath, nextPath))
      .map(({ clusterIndex }) => clusterIndex);

    return {
      consumedIndexes,
      event: {
        ...match.event,
        id: `${match.event.id}-${action}`,
        action: isProvisionalOrigin ? "created" : action,
        previousPath: isProvisionalOrigin ? null : previousPath,
        path: nextPath,
        user: deleted.event.user !== "UNKNOWN" ? deleted.event.user : match.event.user,
        source: match.event.source.includes("security-log") ? match.event.source : "usn-journal+security-log",
        displayAction,
        displayTarget: getLeafName(nextPath)
      } satisfies DisplayEvent
    };
  }

  return null;
}

function tryBuildExplicitTransition(
  current: FileAuditEvent,
  relevant: Array<{ event: FileAuditEvent; clusterIndex: number }>
) {
  if (!current.previousPath) {
    return null;
  }

  if (current.action !== "renamed" && current.action !== "moved") {
    return null;
  }

  const previousPath = current.previousPath ?? "";
  const isFileTransition = isFileLikePath(current.path) && isFileLikePath(previousPath);
  const isFolderTransition = isLikelyFolderPath(current.path) && isLikelyFolderPath(previousPath);

  if (!isFileTransition && !isFolderTransition) {
    return null;
  }

  const nextPath = current.path;
  const isProvisionalOrigin = isProvisionalDocumentName(previousPath) || isProvisionalFolderName(previousPath);
  const action = isMove(previousPath, nextPath) ? "moved" : "renamed";
  const displayAction = isProvisionalOrigin
    ? "Criação"
    : action === "moved"
      ? "Movido"
      : "Renomeado";
  const consumedIndexes = relevant
    .filter(({ event }) => shouldConsumeTransitionEvent(event, previousPath, nextPath))
    .map(({ clusterIndex }) => clusterIndex);

  return {
    consumedIndexes,
      event: {
        ...current,
        id: `${current.id}-${action}-explicit`,
        action: isProvisionalOrigin ? "created" : action,
        previousPath: isProvisionalOrigin ? null : previousPath,
        path: nextPath,
        displayAction,
        displayTarget: getLeafName(nextPath)
      } satisfies DisplayEvent
  };
}

function shouldConsumeTransitionEvent(
  event: FileAuditEvent,
  previousPath: string,
  nextPath: string
) {
  const normalizedPath = normalizePath(event.path);
  const normalizedPrevious = normalizePath(event.previousPath);
  const normalizedTransitionPrevious = normalizePath(previousPath);
  const normalizedTransitionNext = normalizePath(nextPath);

  if (event.action === "renamed" || event.action === "moved") {
    return normalizedPath === normalizedTransitionNext
      && normalizedPrevious === normalizedTransitionPrevious;
  }

  return normalizedPath === normalizedTransitionPrevious
    || normalizedPath === normalizedTransitionNext
    || normalizedPrevious === normalizedTransitionPrevious
    || normalizedPrevious === normalizedTransitionNext
    || (event.action === "created_or_appended" && normalizedPath === normalizedTransitionNext);
}

function getTransitionScore(previousPath: string, nextPath: string) {
  const previousLeaf = getLeafName(previousPath).toLowerCase();
  const nextLeaf = getLeafName(nextPath).toLowerCase();

  if (previousLeaf === nextLeaf && previousLeaf !== "") {
    return 100;
  }

  const previousExtension = getExtension(previousLeaf);
  const nextExtension = getExtension(nextLeaf);
  const previousParent = getParentPath(previousPath).toLowerCase();
  const nextParent = getParentPath(nextPath).toLowerCase();

  if (previousExtension && previousExtension === nextExtension && previousParent === nextParent) {
    return 80;
  }

  if (previousExtension && previousExtension === nextExtension) {
    return 60;
  }

  return 0;
}

function getExtension(value: string) {
  const index = value.lastIndexOf(".");
  return index >= 0 ? value.slice(index) : "";
}

function isTransientArtifactPath(path: string) {
  const normalized = normalizePath(path);
  const leaf = getLeafName(path).toLowerCase();
  const segments = normalized.split("\\").filter(Boolean);
  const hasTransientContainer = segments.some(isTransientContainerSegment);

  return hasTransientContainer
    || leaf.startsWith("$")
    || leaf.startsWith("~$")
    || leaf === "thumbs.db"
    || leaf === "desktop.ini"
    || leaf.endsWith(".tmp")
    || leaf === "volumejoblock.bin"
    || leaf.startsWith("optimizationstate.xml")
    || leaf.startsWith("chunkstorestatistics.xml")
    || leaf.startsWith("changes.optimization.");
}

function isTransientContainerSegment(segment: string) {
  return segment === "$recycle.bin"
    || /^\$i[a-z0-9]{5,}$/i.test(segment)
    || /^\$r[a-z0-9]{5,}$/i.test(segment)
    || /^\$[a-z0-9]{7,}$/i.test(segment);
}

function isProvisionalDocumentName(path: string) {
  const leaf = getLeafName(path).toLowerCase();
  return leaf === "novo documento de texto.txt"
    || leaf === "new text document.txt";
}

function isProvisionalFolderName(path: string) {
  const leaf = getLeafName(path).toLowerCase();
  return leaf === "nova pasta"
    || leaf === "new folder";
}

function normalizePath(path?: string | null) {
  return (path ?? "").trim().replaceAll("/", "\\").replace(/\\+$/, "").toLowerCase();
}

function getLeafName(path: string) {
  const segments = path.split("\\").filter(Boolean);
  return segments[segments.length - 1] ?? path;
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
  if (action === "renamed" && event?.previousPath && isMove(event.previousPath, event.path)) {
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

function shouldShowActionTarget(event: FileAuditEvent) {
  return event.action !== "accessed";
}

function getHighestAnomaly(response: BaselineAnomalyResponse | null) {
  if (!response) {
    return null;
  }

  return [...response.byAction, ...response.byShare, ...response.byUser]
    .sort((left, right) => right.deltaPercent - left.deltaPercent)[0] ?? null;
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
