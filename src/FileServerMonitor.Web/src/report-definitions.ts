export type ReportScenarioId =
  | "folder-activity"
  | "user-activity"
  | "server-activity"
  | "read-without-change"
  | "mass-delete"
  | "recurrent-denied-access"
  | "permission-changes"
  | "mass-rename"
  | "executable-creation"
  | "source-host-activity"
  | "suspicious-remote-access";

export type ReportGrouping = "action" | "user" | "server" | "share" | "sourceHost" | "path" | "extension" | "severity";

export type ReportFilters = {
  server: string;
  share: string;
  user: string;
  path: string;
  action: string;
  sourceHost: string;
  sourceIp: string;
  extension: string;
  result: string;
  severity: string;
  periodHours: string;
  periodMode: "preset" | "custom";
  fromDate: string;
  toDate: string;
  groupBy: ReportGrouping;
};

export type ReportScenario = {
  id: ReportScenarioId;
  title: string;
  description: string;
  focus: string;
  groupBy: ReportGrouping;
  preset: Partial<ReportFilters>;
};

export const executableExtensions = ".exe,.msi,.bat,.cmd,.ps1,.psm1,.vbs,.js,.jar,.dll,.scr";

export const reportScenarios: ReportScenario[] = [
  {
    id: "folder-activity",
    title: "Atividade por pasta",
    description: "Levanta tudo que aconteceu em uma pasta e suas subpastas.",
    focus: "Informe o caminho da pasta para revisar a linha do tempo consolidada.",
    groupBy: "action",
    preset: { groupBy: "action" }
  },
  {
    id: "user-activity",
    title: "Atividade por usuario",
    description: "Investiga acoes executadas por um usuario em arquivos monitorados.",
    focus: "Informe o usuario para acompanhar criacoes, alteracoes, exclusoes, renomes e movimentos.",
    groupBy: "action",
    preset: { groupBy: "action" }
  },
  {
    id: "server-activity",
    title: "Atividade por servidor",
    description: "Revisa a atividade observada em um servidor monitorado.",
    focus: "Informe o servidor ou deixe em branco para comparar todo o ambiente.",
    groupBy: "share",
    preset: { groupBy: "share" }
  },
  {
    id: "read-without-change",
    title: "Leitura sem alteracao",
    description: "Foca aberturas em uma pasta, servidor ou compartilhamento sem modificacao.",
    focus: "Use para revisar acessos a documentos sem criacao, alteracao ou exclusao.",
    groupBy: "user",
    preset: { action: "accessed", groupBy: "user" }
  },
  {
    id: "mass-delete",
    title: "Exclusao em massa",
    description: "Foca exclusoes em uma pasta, servidor ou compartilhamento para resposta a incidente.",
    focus: "Use para validar volume, autoria e escopo de exclusoes.",
    groupBy: "user",
    preset: { action: "deleted", groupBy: "user" }
  },
  {
    id: "recurrent-denied-access",
    title: "Acesso negado recorrente",
    description: "Foca tentativas negadas repetidas para acelerar investigacao de falha de acesso.",
    focus: "Use para identificar usuarios, hosts e caminhos com negacoes frequentes.",
    groupBy: "user",
    preset: { result: "denied", groupBy: "user" }
  },
  {
    id: "permission-changes",
    title: "Mudancas de permissao",
    description: "Foca alteracoes de permissao em pasta, servidor ou compartilhamento monitorado.",
    focus: "Use para revisar quem alterou permissoes e onde a mudanca ocorreu.",
    groupBy: "path",
    preset: { action: "permission_changed", groupBy: "path" }
  },
  {
    id: "mass-rename",
    title: "Renomeacao em massa",
    description: "Foca renomeacoes para acelerar investigacao de alteracoes em volume.",
    focus: "Use para listar antes/depois e entender padroes de renomeacao.",
    groupBy: "user",
    preset: { action: "renamed", groupBy: "user" }
  },
  {
    id: "executable-creation",
    title: "Criacao de executaveis",
    description: "Foca criacao de executaveis e scripts em caminhos monitorados.",
    focus: "Use para revisao rapida de arquivos potencialmente suspeitos.",
    groupBy: "extension",
    preset: { action: "created", extension: executableExtensions, groupBy: "extension" }
  },
  {
    id: "source-host-activity",
    title: "Atividade por host de origem",
    description: "Investiga a atividade operacional disparada a partir de uma estacao ou host especifico.",
    focus: "Informe o host de origem para isolar o comportamento de uma maquina.",
    groupBy: "action",
    preset: { groupBy: "action" }
  },
  {
    id: "suspicious-remote-access",
    title: "Acesso remoto suspeito",
    description: "Investiga acessos remotos por IP de origem com apoio de usuario, servidor, acao e severidade.",
    focus: "Informe IP ou host de origem para revisar acessos fora do padrao.",
    groupBy: "sourceHost",
    preset: { groupBy: "sourceHost" }
  }
];

export function createDefaultReportFilters(): ReportFilters {
  return {
    server: "",
    share: "",
    user: "",
    path: "",
    action: "",
    sourceHost: "",
    sourceIp: "",
    extension: "",
    result: "",
    severity: "",
    periodHours: "24",
    periodMode: "preset",
    fromDate: "",
    toDate: "",
    groupBy: "action"
  };
}

export function createFiltersForScenario(id: ReportScenarioId): ReportFilters {
  const scenario = reportScenarios.find((item) => item.id === id);
  return {
    ...createDefaultReportFilters(),
    groupBy: scenario?.groupBy ?? "action",
    ...(scenario?.preset ?? {})
  };
}

export function getReportScenario(id: ReportScenarioId): ReportScenario {
  const scenario = reportScenarios.find((item) => item.id === id);
  if (!scenario) {
    throw new Error(`Relatorio guiado desconhecido: ${id}`);
  }

  return scenario;
}

export function buildReportQueryParams(filters: ReportFilters, take = 1000): URLSearchParams {
  const params = new URLSearchParams({ take: String(take) });
  const periodHours = Number(filters.periodHours);

  if (filters.periodMode === "custom" && filters.fromDate) {
    const fromDate = new Date(`${filters.fromDate}T00:00:00`);
    const toDate = filters.toDate ? new Date(`${filters.toDate}T23:59:59.999`) : new Date(`${filters.fromDate}T23:59:59.999`);
    params.set("fromUtc", fromDate.toISOString());
    params.set("toUtc", toDate.toISOString());
  } else if (Number.isFinite(periodHours) && periodHours > 0) {
    params.set("fromUtc", new Date(Date.now() - periodHours * 60 * 60 * 1000).toISOString());
  }

  appendParam(params, "server", filters.server);
  appendParam(params, "share", filters.share);
  appendParam(params, "user", filters.user);
  appendParam(params, "path", filters.path);
  appendParam(params, "action", filters.action);
  appendParam(params, "sourceHost", filters.sourceHost);
  appendParam(params, "sourceIp", filters.sourceIp);
  appendParam(params, "extension", filters.extension);
  appendParam(params, "result", filters.result);
  appendParam(params, "severity", filters.severity);

  return params;
}

export function summarizeReportFilters(filters: ReportFilters): string {
  const parts = [
    filters.periodMode === "custom"
      ? filters.toDate
        ? `${filters.fromDate} a ${filters.toDate}`
        : filters.fromDate || "periodo manual"
      : labelForReportPeriod(filters.periodHours),
    filters.server ? `servidor ${filters.server}` : "",
    filters.share ? `share ${filters.share}` : "",
    filters.user ? `usuario ${filters.user}` : "",
    filters.path ? `caminho ${filters.path}` : "",
    filters.action ? `acao ${filters.action}` : "",
    filters.sourceHost ? `host ${filters.sourceHost}` : "",
    filters.sourceIp ? `IP ${filters.sourceIp}` : "",
    filters.extension ? `extensao ${filters.extension}` : "",
    filters.result ? `resultado ${filters.result}` : "",
    filters.severity ? `severidade ${filters.severity}` : ""
  ].filter(Boolean);

  return parts.join(" · ");
}

export function labelForReportPeriod(periodHours: string): string {
  switch (periodHours) {
    case "1":
      return "ultima hora";
    case "6":
      return "ultimas 6 horas";
    case "24":
      return "ultimas 24 horas";
    case "168":
      return "ultimos 7 dias";
    case "720":
      return "ultimos 30 dias";
    default:
      return `${periodHours} horas`;
  }
}

function appendParam(params: URLSearchParams, name: string, value: string) {
  const normalized = value.trim();
  if (normalized) {
    params.set(name, normalized);
  }
}
