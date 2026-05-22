# File Server Monitor

Sistema para monitoramento de alteracoes em servidor de arquivos Windows.

## Cenario

O projeto esta sendo desenhado para um Windows Server 2022 Datacenter com mais de 8 TB de dados, milhoes de arquivos, muitos compartilhamentos SMB e aproximadamente 400 usuarios no Active Directory.

## Stack Inicial

- API: ASP.NET Core
- Agente Windows: .NET Worker Service, em etapa futura
- Automacao Windows: PowerShell
- Core: biblioteca .NET sem dependencias externas para regras e normalizacao
- Banco de desenvolvimento: SQL Server 2022 em Docker
- Banco de producao: SQL Server externo
- Frontend: React + TypeScript, em etapa futura

## Executar em Docker

Crie o arquivo `.env` a partir de `.env.example` e ajuste a senha do SQL Server.

```bash
cp .env.example .env
./scripts/start-dev.sh
```

Para validar se a stack subiu corretamente:

```bash
./scripts/check-dev.sh
```

Para encerrar:

```bash
./scripts/stop-dev.sh
```

A API ficara disponivel em:

```text
http://localhost:8180
```

O painel web ficara disponivel em:

```text
http://localhost:3300
```

Fluxo 100% Docker da stack principal:

- `scripts/start-dev.sh`
- `scripts/check-dev.sh`
- `scripts/stop-dev.sh`

## Executar em Producao

Use o Compose de producao quando a aplicacao for apontar para um SQL Server externo.

Crie o arquivo `.env.production` a partir de `.env.production.example`:

```bash
cp .env.production.example .env.production
```

Configure:

- `PUBLIC_API_BASE_URL`: URL publica do painel/API.
- `PUBLIC_WEB_ORIGIN`: origem HTTPS permitida no CORS da API.
- `PUBLIC_WEB_API_KEY`: chave administrativa usada pelo painel web.
- `PUBLIC_WEB_ACTOR_NAME`: identificador temporario do operador do painel, usado em auditoria administrativa.
- `FILESERVER_MONITOR_API_KEY`: chave forte para API, agente e automacoes.
- `FILESERVER_MONITOR_ADMIN_API_KEY`: chave forte para administracao do painel.
- `SQLSERVER_CONNECTION_STRING`: connection string do SQL Server externo.

Suba a pilha:

```bash
docker compose --env-file .env.production -f docker-compose.prod.yml up --build -d
```

O reverse proxy de producao espera certificados em:

```text
docker/certs/fullchain.pem
docker/certs/privkey.pem
```

Para preparar o banco externo, execute os scripts SQL em ordem:

```text
docker/sql/001_create_schema.sql
docker/sql/002_create_app_login.sql
```

Antes de usar `002_create_app_login.sql`, troque a senha do login de aplicacao.

Verificar saude:

```bash
curl http://localhost:8080/health
```

Enviar um evento de teste:

```bash
curl -X POST http://localhost:8080/api/events \
  -H "Content-Type: application/json" \
  -d '{
    "server": "FS01",
    "share": "Departamentos",
    "path": "\\\\FS01\\Departamentos\\Financeiro\\relatorio.xlsx",
    "objectType": "file",
    "action": "modified",
    "user": "EMPRESA\\maria.silva",
    "sourceHost": "WKS-023",
    "sourceIp": "192.168.10.23",
    "processName": "EXCEL.EXE",
    "fileSizeBytes": 248120,
    "extension": ".xlsx",
    "source": "manual-test"
  }'
```

Listar eventos:

```bash
curl "http://localhost:8080/api/events?take=20"
```

Exportar eventos em CSV:

```bash
curl "http://localhost:8080/api/events/export.csv?take=1000" -o eventos.csv
```

Exportar alertas em CSV:

```bash
curl "http://localhost:8080/api/alerts/export.csv?status=open&take=1000" -o alertas.csv
```

Investigar a linha do tempo de um usuario ou caminho:

```bash
curl "http://localhost:8080/api/events?user=maria.silva&path=Financeiro&fromUtc=2026-05-21T00:00:00Z&take=500"
```

Cadastrar uma pasta monitorada:

```bash
curl -X POST http://localhost:8080/api/monitored-paths \
  -H "Content-Type: application/json" \
  -d '{
    "server": "FS01",
    "share": "Departamentos",
    "path": "D:\\Shares\\Departamentos\\Financeiro",
    "status": "active",
    "priority": "critical",
    "owner": "Infra / Financeiro",
    "notes": "Pasta piloto com auditoria NTFS habilitada"
  }'
```

Listar caminhos monitorados:

```bash
curl "http://localhost:8080/api/monitored-paths"
```

Resumo operacional das ultimas 24 horas:

```bash
curl "http://localhost:8080/api/reports/activity-summary?take=10"
```

Resumo operacional filtrado:

```bash
curl "http://localhost:8080/api/reports/activity-summary?server=FS01&share=Departamentos&user=maria.silva&action=modified&take=10"
```

Anomalias de baseline:

```bash
curl "http://localhost:8080/api/reports/baseline-anomalies?server=FS01&take=8"
```

Exportar anomalias de baseline:

```bash
curl "http://localhost:8080/api/reports/baseline-anomalies/export.csv?server=FS01&take=20" -o anomalias.csv
```

Listar alertas abertos:

```bash
curl "http://localhost:8080/api/alerts?status=open&take=20"
```

## Banco de Dados

O Docker Compose sobe um SQL Server local para desenvolvimento. O script inicial esta em:

```text
docker/sql/001_create_schema.sql
```

Na configuracao padrao, a API usa SQL Server. O Docker Compose tambem executa o script de criacao do schema antes de iniciar a API.

Em producao, a connection string deve apontar para o SQL Server externo:

```text
ConnectionStrings__SqlServer=Server=SERVIDOR_SQL,1433;Database=FileServerMonitor;User Id=usuario;Password=senha;TrustServerCertificate=True;
```

Para rodar sem banco em uma validacao rapida, altere o provedor de armazenamento:

```text
Monitor__StorageProvider=InMemory
```

Esse modo e apenas para desenvolvimento e nao deve ser usado em producao.

## Caminhos Monitorados

O cadastro de caminhos monitorados registra quais servidores, shares e pastas raiz fazem parte do piloto ou da operacao.

Campos principais:

- `server`: nome do servidor de arquivos.
- `share`: nome do compartilhamento SMB.
- `path`: caminho fisico raiz auditado.
- `status`: `planned`, `active`, `paused` ou `retired`.
- `priority`: `low`, `normal`, `high` ou `critical`.
- `owner`: area responsavel.
- `notes`: observacoes operacionais.

Endpoints:

```text
GET    /api/monitored-paths
POST   /api/monitored-paths
PUT    /api/monitored-paths/{id}
DELETE /api/monitored-paths/{id}
```

Na configuracao padrao com SQL Server, os caminhos sao persistidos na tabela `dbo.MonitoredPaths`.

## Auditoria Administrativa

A API registra alteracoes administrativas importantes:

- reconhecimento de alertas;
- criacao de caminho monitorado;
- edicao de caminho monitorado;
- remocao de caminho monitorado.

Consultar registros:

```bash
curl "http://localhost:8080/api/admin-audit?take=50"
```

Filtros:

```text
GET /api/admin-audit?action=monitored_path.create
GET /api/admin-audit?entityType=alert
```

Quando houver um proxy ou autenticacao corporativa, envie `X-Actor` para registrar o operador real. Na configuracao padrao com SQL Server, os registros sao persistidos na tabela `dbo.AdminAuditLog`.

O painel web tambem possui a aba `Auditoria`, com busca por acao, entidade, operador ou IP.

## Relatorios Operacionais

O endpoint de resumo operacional agrega atividade recente sem retornar a lista completa de eventos.

```text
GET /api/reports/activity-summary
```

Filtros:

- `fromUtc`: inicio da janela em UTC.
- `toUtc`: fim da janela em UTC.
- `server`: servidor de arquivos.
- `share`: compartilhamento.
- `user`: usuario ou parte do nome.
- `action`: acao exata, como `created`, `modified`, `deleted`, `renamed`, `moved` ou `permission_changed`.
- `take`: quantidade de itens por ranking, de 1 a 50.

O endpoint de baseline compara o período atual com a média dos 7 períodos anteriores do mesmo tamanho.

```text
GET /api/reports/baseline-anomalies
GET /api/reports/baseline-anomalies/export.csv
```

Sem filtros, a API resume as ultimas 24 horas e retorna:

- Total de eventos.
- Ranking por acao.
- Ranking por compartilhamento.
- Ranking por usuario.

## Autenticacao Inicial

A API suporta uma protecao inicial por API key. Em desenvolvimento ela vem desligada.

Variaveis:

```text
AUTH_ENABLED=true
FILESERVER_MONITOR_API_KEY=uma-chave-forte
FILESERVER_MONITOR_ADMIN_API_KEY=uma-chave-admin-forte
```

Quando habilitada, a API exige um destes formatos:

```text
X-Api-Key: uma-chave-forte
Authorization: Bearer uma-chave-forte
```

O agente pode enviar a chave pelo campo `apiKey` em `appsettings.agent.json`.

O frontend deve usar a chave administrativa quando for administrar caminhos, reconhecer alertas ou consultar auditoria:

```text
VITE_API_KEY=uma-chave-admin-forte
VITE_ACTOR_NAME=operador.infra
```

Se `FILESERVER_MONITOR_ADMIN_API_KEY` nao for configurada, a API usa `FILESERVER_MONITOR_API_KEY` tambem para administracao. Em producao, prefira separar as duas.

Essa camada e uma protecao inicial para ambiente interno. A etapa de producao deve evoluir para Active Directory ou Entra ID com controle de acesso por perfil.

## CORS

A API permite somente origens configuradas para chamadas feitas pelo navegador.

Em desenvolvimento, o Compose libera localhost nas portas comuns do painel. Em producao, configure:

```text
PUBLIC_WEB_ORIGIN=https://fileserver-monitor.seudominio.local
```

O valor deve ser a origem do painel web, sem barra final.

## Auditoria Windows

Antes de auditar grandes volumes, comece com pastas piloto. O script inicial esta em:

```text
scripts/windows/Configure-FileServerAudit.ps1
```

Exemplo no Windows Server:

```powershell
.\Configure-FileServerAudit.ps1 -Paths "D:\Shares\Financeiro" -Identity "Everyone" -WhatIf
```

Depois de validar impacto e eventos gerados, rode sem `-WhatIf`.

Tambem e possivel sincronizar a auditoria a partir dos caminhos ativos cadastrados na API:

```powershell
.\Sync-FileServerAuditFromApi.ps1 `
  -ApiBaseUrl "https://fileserver-monitor.seudominio.local" `
  -ApiKey "sua-chave" `
  -ServerName "FS01" `
  -WhatIf
```

Depois de revisar os caminhos retornados, rode sem `-WhatIf`.

## Agente Windows

O agente fica em:

```text
src/FileServerMonitor.Agent
```

Ele executa em loop, chama o script PowerShell de coleta, envia eventos para `/api/events/batch` e envia heartbeat para `/api/agents/heartbeat`.

Configuracao principal:

```text
src/FileServerMonitor.Agent/appsettings.agent.json
```

Campos importantes:

- `agentId`: identificador unico do agente.
- `server`: nome do Windows Server.
- `apiBaseUrl`: URL da API central.
- `pollIntervalSeconds`: intervalo de coleta.
- `batchSize`: quantidade maxima de eventos por lote.
- `enableSecurityLogCollector`: ativa coleta do Windows Security Log.
- `enableUsnJournalCollector`: ativa coleta do USN Journal.
- `enableCorrelation`: ativa enriquecimento de eventos USN com dados do Security Log.
- `correlationWindowSeconds`: janela maxima de tempo para aproximar eventos das duas fontes.
- `sendSecurityLogEvents`: envia tambem os eventos brutos do Security Log para comparacao e auditoria.
- `usnVolumes`: volumes NTFS monitorados pelo USN Journal, como `D:`.
- `stateFile`: arquivo local com o ultimo RecordId processado.
- `queueFile`: fila local para eventos pendentes quando a API estiver indisponivel.
- `eventIds`: eventos do Windows Security Log coletados.

Eventos iniciais monitorados:

- `4663`: tentativa de acesso a objeto, usado para alteracoes em arquivos e pastas.
- `4660`: objeto excluido.
- `4670`: permissao alterada.

Coleta avancada:

- Security Log identifica melhor quem fez a acao.
- USN Journal detecta mudancas no volume NTFS com mais eficiencia em ambientes grandes.
- O agente mantem `LastRecordId` para Security Log e `LastUsnByVolume` para cada volume.
- No primeiro piloto, mantenha `enableUsnJournalCollector` como `false`, valide Security Log e depois habilite USN em um volume controlado.

Correlacao:

- O agente cruza eventos USN com eventos do Security Log por caminho e proximidade de horario.
- Quando encontra uma correspondencia, o evento USN recebe usuario, SID, host, IP e processo quando esses dados existirem no Security Log.
- A fonte do evento correlacionado passa a ser `usn-journal+security-log`.
- Essa correlacao melhora a investigacao operacional, mas a evidencia primaria de usuario continua sendo o Windows Security Log.

## Alertas

A API gera alertas iniciais quando recebe eventos pelos endpoints de ingestao.

Regras atuais:

- `mass-delete`: muitas exclusoes pelo mesmo usuario no mesmo servidor em uma janela curta.
- `mass-rename`: muitas renomeacoes pelo mesmo usuario no mesmo servidor.
- `permission-change`: alteracao de permissao em arquivo ou pasta.
- `possible-ransomware`: grande volume de alteracoes ou extensoes suspeitas em curto periodo.

Configuracao em `appsettings.json`:

```json
"Alerts": {
  "WindowMinutes": 5,
  "DedupMinutes": 10,
  "MassDeleteThreshold": 50,
  "MassRenameThreshold": 100,
  "RansomwareActivityThreshold": 250,
  "SuspiciousExtensionThreshold": 10
}
```

Endpoints:

```text
GET  /api/alerts
GET  /api/alerts/export.csv
POST /api/alerts/{id}/ack
GET  /api/alert-rules
PUT  /api/alert-rules/{rule}
POST /api/alert-rules/{rule}/simulate
```

Na configuracao padrao com SQL Server, os alertas sao persistidos na tabela `dbo.FileServerAlerts`. O modo em memoria fica apenas como fallback de desenvolvimento quando `Monitor__StorageProvider=InMemory`.

As regras tambem podem ser ajustadas operacionalmente pela API e pela aba `Alertas` do painel, sem editar arquivo de configuracao. Exemplos:

```bash
curl "http://localhost:8080/api/alert-rules"
curl -X PUT "http://localhost:8080/api/alert-rules/mass-delete" \
  -H "Content-Type: application/json" \
  -d '{
    "enabled": true,
    "severity": "critical",
    "threshold": 30
  }'
curl -X POST "http://localhost:8080/api/alert-rules/mass-delete/simulate" \
  -H "Content-Type: application/json" \
  -d '{
    "fromUtc": "2026-05-21T00:00:00Z",
    "toUtc": "2026-05-22T00:00:00Z",
    "take": 5000
  }'
```

Tambem e possivel restringir uma regra a um escopo operacional:

```bash
curl -X PUT "http://localhost:8080/api/alert-rules/permission-change" \
  -H "Content-Type: application/json" \
  -d '{
    "enabled": true,
    "severity": "critical",
    "serverFilter": "FS01",
    "shareFilter": "Departamentos",
    "pathFilter": "D:\\Shares\\Departamentos\\Financeiro"
  }'
```

Filtros de escopo:

- `serverFilter`: servidor exato.
- `shareFilter`: share exata.
- `pathFilter`: prefixo do caminho fisico.

Janela opcional de horario:

- `activeFromHour`: hora inicial de 0 a 23.
- `activeToHour`: hora final de 0 a 23.
- `activeDays`: dias da semana separados por virgula, por exemplo `seg,ter,qua,qui,sex`.
- `excludedUsers`: usuarios ignorados pela regra.
- `excludedHosts`: hosts ignorados pela regra.
- `excludedProcesses`: processos ignorados pela regra.
- `timeZoneId`: fuso da janela, por exemplo `America/Maceio`.

Exemplo para monitorar fora do expediente:

```bash
curl -X PUT "http://localhost:8080/api/alert-rules/mass-delete" \
  -H "Content-Type: application/json" \
  -d '{
    "enabled": true,
    "severity": "critical",
    "threshold": 20,
    "activeFromHour": 19,
    "activeToHour": 7,
    "activeDays": "seg,ter,qua,qui,sex",
    "excludedUsers": "svc_backup,svc_antivirus",
    "excludedProcesses": "robocopy.exe,veeamagent.exe",
    "timeZoneId": "America/Maceio"
  }'
```

## Notificacoes

A API pode enviar alertas novos para um webhook externo.

Variaveis:

```text
NOTIFICATIONS_ENABLED=true
NOTIFICATIONS_WEBHOOK_URL=https://webhook.exemplo.local/alertas
NOTIFICATIONS_FORMAT=generic
NOTIFICATIONS_MINIMUM_SEVERITY=critical
```

Formatos:

- `generic`: payload JSON com todos os campos principais do alerta.
- `teams`: payload no formato MessageCard para Microsoft Teams Incoming Webhook.

Exemplo para Teams:

```text
NOTIFICATIONS_ENABLED=true
NOTIFICATIONS_WEBHOOK_URL=https://outlook.office.com/webhook/...
NOTIFICATIONS_FORMAT=teams
NOTIFICATIONS_MINIMUM_SEVERITY=high
```

As notificacoes sao enviadas apenas para alertas novos e respeitam a deduplicacao configurada em `Alerts:DedupMinutes`.

## Retencao de Dados

Em ambientes com muitos compartilhamentos e milhoes de arquivos, a retencao precisa ser definida antes do piloto crescer.

Configuracoes principais:

```text
RETENTION_ENABLED=true
RETENTION_EVENTS_DAYS=180
RETENTION_ALERTS_DAYS=365
RETENTION_INTERVAL_HOURS=24
RETENTION_PURGE_BATCH_SIZE=10000
```

Quando habilitada, a API remove eventos e alertas antigos em lotes para reduzir impacto no SQL Server. Ajuste os dias conforme politica interna, LGPD, auditoria e capacidade do banco.

## Interface Web

O frontend fica em:

```text
src/FileServerMonitor.Web
```

Stack:

- React.
- TypeScript.
- Vite.
- Nginx no container final.

Telas iniciais:

- Dashboard.
- Eventos.
- Investigacao.
- Alertas.
- Agentes.

Variavel opcional:

```text
VITE_API_BASE_URL=http://localhost:8080
VITE_API_KEY=uma-chave-forte
VITE_ACTOR_NAME=operador.infra
```

Publicar o agente:

```bash
dotnet publish src/FileServerMonitor.Agent/FileServerMonitor.Agent.csproj -c Release -r win-x64 --self-contained false
```

Instalar no Windows Server como servico:

```powershell
.\scripts\windows\Install-FileServerMonitorAgent.ps1 -AgentDirectory "C:\FileServerMonitor\Agent" -WhatIf
```

Depois de validar, rode sem `-WhatIf`.

Saude dos agentes:

```bash
curl "http://localhost:8080/api/agents/health"
```

Na configuracao padrao com SQL Server, cada heartbeat e persistido na tabela `dbo.AgentHeartbeats`, incluindo status, versao, ultimo `RecordId`, ultimo USN por volume, quantidade de eventos pendentes na fila local e ultimo envio bem-sucedido.

A API marca um agente como `stale` quando o ultimo heartbeat passa do limite configurado:

```text
AGENTS_STALE_MINUTES=10
AGENTS_BACKLOG_WARNING_THRESHOLD=1000
```

Esse status e calculado na leitura de `/api/agents/health`; o heartbeat original continua persistido para auditoria.

Se a API ficar indisponivel, o agente grava eventos em `queueFile`. O painel mostra o tamanho dessa fila para indicar backlog de envio. Quando a fila passa de `AGENTS_BACKLOG_WARNING_THRESHOLD`, a API marca o agente como `backlog`.

Configuracao remota do agente:

```bash
curl "http://localhost:8080/api/agents/config?server=FS01"
```

Esse endpoint retorna os caminhos ativos cadastrados para o servidor, o compartilhamento padrao e os volumes NTFS derivados dos caminhos, como `D:`. Quando `enableRemoteConfig=true`, o agente busca essa configuracao periodicamente e pode filtrar eventos fora dos caminhos ativos com `filterToConfiguredPaths=true`.

## Roadmap

As etapas planejadas estao documentadas em:

```text
docs/etapas.md
```

## Piloto Operacional

Use estes documentos para executar o primeiro piloto no Windows Server 2022:

```text
docs/piloto-windows-server-2022.md
docs/checklist-piloto.md
```

Script de diagnostico:

```text
scripts/windows/Test-FileServerMonitorPrereqs.ps1
```

## Testes

Os testes basicos ficam em:

```text
tests/FileServerMonitor.Core.Tests
```

Eles validam normalizacao de eventos, regras iniciais de alerta e correlacao Security Log + USN Journal sem depender de pacotes externos.

A API reutiliza essa biblioteca para normalizar eventos recebidos e gerar alertas. O agente reutiliza a mesma biblioteca para correlacionar eventos Security Log + USN Journal, evitando divergencia entre codigo testado e codigo de producao.

Executar:

```bash
./scripts/run-tests.sh
```

Validacao completa local:

```bash
./scripts/validate.sh
```

Essa validacao checa Compose, testes, build do agente e sintaxe PowerShell. A API depende do pacote `Microsoft.Data.SqlClient`; em ambientes sem acesso ao NuGet, essa etapa aparece como aviso conhecido.

Por padrao, a API compila em modo local sem SQL Server externo:

```bash
dotnet build src/FileServerMonitor.Api/FileServerMonitor.Api.csproj /p:EnableSqlServer=false
```

Para validar o build com SQL Server, use um ambiente com acesso ao NuGet:

```bash
VALIDATE_SQLSERVER=true ./scripts/validate.sh
```

## Demo Local

Para subir a API em memoria, sem SQL Server:

```bash
./scripts/run-api-local.sh
```

Em outro terminal, popular dados de demonstracao:

```bash
./scripts/seed-demo.sh
```

Com API key:

```bash
API_KEY=uma-chave ./scripts/seed-demo.sh
```

O seed cria heartbeat de agente, eventos variados e alertas de exemplo.

Em alguns sandboxes locais, conexoes para `localhost` podem ser bloqueadas. Nesse caso, valide a demo em Docker Desktop, no Windows Server de homologacao ou em uma maquina de desenvolvimento sem essa restricao.

## Preview Local

Para abrir a interface em modo de revisao visual, sem depender de SQL Server externo:

```bash
./scripts/start-preview.sh
```

Se quiser usar outro arquivo de ambiente:

```bash
ENV_FILE=.env.preview ./scripts/start-preview.sh
```

Depois de subir, popule dados de demonstracao:

```bash
API_BASE_URL=http://localhost:8081 ./scripts/seed-demo.sh
```

Ou faça a checagem completa do preview, já validando web, API e dados básicos:

```bash
./scripts/check-preview.sh
```

Para validar sem repopular a demo:

```bash
SEED_DEMO=false ./scripts/check-preview.sh
```

Para encerrar o preview:

```bash
./scripts/stop-preview.sh
```

Arquivos de apoio:

- `docker-compose.preview.yml`
- `.env.preview.example`
- `scripts/start-preview.sh`
- `scripts/check-preview.sh`
- `scripts/stop-preview.sh`

## Ajustes Recentes da Interface

- Dashboard com cards executivos para postura atual, maior desvio e janela analisada.
- Paineis com subtitulos operacionais para leitura mais rapida do contexto.
- Lista de alertas recentes com severidade, status e resumo visual mais direto.
- Telas de Alertas, Investigacao, Agentes, Caminhos e Auditoria com resumos executivos para leitura consistente entre as areas do painel.
- Interface com avisos de sucesso/erro no proprio painel, indicacao de atualizacao em andamento e retorno visual melhor para acoes operacionais.
- Navegacao lateral com contadores por area, resumo rapido no topo e tabelas com leitura mais confortavel para uso continuo.
- Preview local com script de checagem para confirmar subida da web, API e dados de demonstracao antes da revisao visual.
- Stack principal de desenvolvimento com scripts dedicados para subir, validar e encerrar tudo em Docker.
- Alertas agora permitem abrir a lista de operacoes relacionadas ao arquivo ou pasta afetada sem sair da propria tela.
