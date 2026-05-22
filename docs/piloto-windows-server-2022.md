# Piloto no Windows Server 2022

## Objetivo

Validar a coleta de eventos em um Windows Server 2022 Datacenter com servidor de arquivos real, muitos compartilhamentos e grande volume de dados, sem auditar os 8 TB inteiros no primeiro momento.

## Escopo Inicial

Comece com 1 ou 2 pastas piloto.

Sugestao:

- Uma pasta critica, como Financeiro ou RH.
- Uma pasta de uso comum, mas com volume moderado.

Evite iniciar por todo o volume `D:`. Primeiro valide impacto, volume de eventos e qualidade dos dados.

## Pre-Requisitos

No servidor da aplicacao:

- Docker instalado.
- Acesso ao SQL Server externo ou SQL Server local de desenvolvimento.
- Portas liberadas para o painel e API.
- Certificado TLS para producao.

No Windows Server de arquivos:

- Windows Server 2022 Datacenter.
- PowerShell disponivel.
- Acesso HTTP/HTTPS ate a API.
- Permissao administrativa para configurar auditoria.
- Politica de auditoria habilitada para acesso a objetos.
- Espaco local para fila e estado do agente.

## Preparar Banco e Aplicacao

Para desenvolvimento:

```bash
cp .env.example .env
docker compose up --build
```

Para producao:

```bash
cp .env.production.example .env.production
docker compose --env-file .env.production -f docker-compose.prod.yml up --build -d
```

No SQL Server externo, execute:

```text
docker/sql/001_create_schema.sql
docker/sql/002_create_app_login.sql
```

Antes de executar `002_create_app_login.sql`, troque a senha do login.

## Validar API

Desenvolvimento:

```bash
curl http://localhost:8080/health
```

Producao:

```bash
curl https://fileserver-monitor.seudominio.local/health
```

Se `AUTH_ENABLED=true`, os endpoints operacionais exigem `X-Api-Key`.

## Diagnosticar Windows Server

Copie o script abaixo para o servidor de arquivos:

```text
scripts/windows/Test-FileServerMonitorPrereqs.ps1
```

Execute:

```powershell
.\Test-FileServerMonitorPrereqs.ps1 -ApiBaseUrl "https://fileserver-monitor.seudominio.local" -ApiKey "sua-chave"
```

Valide:

- Comunicacao com a API.
- Acesso ao Security Log.
- Presenca de volumes NTFS.
- Disponibilidade do `fsutil`.
- Politica de auditoria.

## Configurar Auditoria NTFS

Primeiro use `-WhatIf`:

```powershell
.\Configure-FileServerAudit.ps1 -Paths "D:\Shares\Financeiro" -Identity "Everyone" -WhatIf
```

Se os caminhos ativos ja estiverem cadastrados no painel, use a sincronizacao pela API:

```powershell
.\Sync-FileServerAuditFromApi.ps1 `
  -ApiBaseUrl "https://fileserver-monitor.seudominio.local" `
  -ApiKey "sua-chave" `
  -ServerName "FS01" `
  -WhatIf
```

Depois aplique:

```powershell
.\Configure-FileServerAudit.ps1 -Paths "D:\Shares\Financeiro" -Identity "Everyone"
```

Ou, usando a API:

```powershell
.\Sync-FileServerAuditFromApi.ps1 `
  -ApiBaseUrl "https://fileserver-monitor.seudominio.local" `
  -ApiKey "sua-chave" `
  -ServerName "FS01"
```

Recomendacao:

- Aplique por pasta piloto.
- Monitore crescimento do Security Log.
- Ajuste a retencao do log antes de expandir.

## Publicar Agente

Em uma maquina com SDK .NET:

```bash
dotnet publish src/FileServerMonitor.Agent/FileServerMonitor.Agent.csproj -c Release -r win-x64 --self-contained false -o publish/agent
```

Copie a pasta publicada para:

```text
C:\FileServerMonitor\Agent
```

Edite:

```text
C:\FileServerMonitor\Agent\appsettings.agent.json
```

Exemplo:

```json
{
  "agentId": "fs01-agent",
  "server": "FS01",
  "apiBaseUrl": "https://fileserver-monitor.seudominio.local",
  "apiKey": "sua-chave",
  "pollIntervalSeconds": 15,
  "batchSize": 200,
  "enableSecurityLogCollector": true,
  "enableUsnJournalCollector": false,
  "enableCorrelation": true,
  "enableRemoteConfig": true,
  "filterToConfiguredPaths": true,
  "correlationWindowSeconds": 10,
  "remoteConfigRefreshMinutes": 5,
  "sendSecurityLogEvents": true,
  "usnVolumes": [ "D:" ],
  "stateFile": "state/agent-state.json",
  "queueFile": "state/pending-events.ndjson",
  "powershellPath": "powershell.exe",
  "securityLogScriptPath": "scripts/Collect-SecurityFileEvents.ps1",
  "usnJournalScriptPath": "scripts/Collect-UsnJournalEvents.ps1",
  "defaultShare": "Financeiro",
  "eventIds": [4663, 4660, 4670]
}
```

## Instalar Agente Como Servico

Valide com `-WhatIf`:

```powershell
.\Install-FileServerMonitorAgent.ps1 -AgentDirectory "C:\FileServerMonitor\Agent" -WhatIf
```

Instale:

```powershell
.\Install-FileServerMonitorAgent.ps1 -AgentDirectory "C:\FileServerMonitor\Agent"
```

Verifique:

```powershell
Get-Service FileServerMonitorAgent
```

## Testes Controlados

Na pasta piloto:

1. Crie um arquivo.
2. Edite o arquivo.
3. Renomeie o arquivo.
4. Mova para uma subpasta.
5. Exclua o arquivo.
6. Altere permissao de uma pasta de teste.

Depois consulte:

```bash
curl "https://fileserver-monitor.seudominio.local/api/events?take=20" -H "X-Api-Key: sua-chave"
curl "https://fileserver-monitor.seudominio.local/api/agents/health" -H "X-Api-Key: sua-chave"
curl "https://fileserver-monitor.seudominio.local/api/alerts?status=open" -H "X-Api-Key: sua-chave"
```

## Habilitar USN Journal

Somente depois de validar Security Log.

No `appsettings.agent.json`:

```json
"enableUsnJournalCollector": true,
"usnVolumes": [ "D:" ]
```

Reinicie o servico:

```powershell
Restart-Service FileServerMonitorAgent
```

## Monitoramento Durante o Piloto

Acompanhe por 24 a 72 horas:

- Crescimento do Security Log.
- CPU e disco do servidor de arquivos.
- Tamanho da fila local do agente.
- Latencia entre acao e evento no painel.
- Quantidade de alertas falsos positivos.
- Eventos por usuario.
- Eventos por pasta.
- Agente online/offline.

## Criterios de Aceite

O piloto pode ser considerado aprovado quando:

- Eventos aparecem na API em ate 1 minuto.
- Heartbeat do agente aparece no painel.
- Criacao, edicao, exclusao e alteracao de permissao sao capturadas.
- Fila local segura eventos quando a API fica indisponivel.
- Alertas principais disparam em testes controlados.
- Impacto no servidor de arquivos e aceitavel.
- Limiares de alerta foram ajustados para reduzir falso positivo.

## Rollback

Parar agente:

```powershell
Stop-Service FileServerMonitorAgent
```

Remover servico:

```powershell
sc.exe delete FileServerMonitorAgent
```

Remover auditoria da pasta piloto manualmente pelas propriedades avancadas de seguranca ou por politica corporativa.

## Proxima Expansao

Depois do piloto:

- Expandir para mais compartilhamentos.
- Persistir mais metricas operacionais.
- Integrar com email, Teams ou SIEM.
- Trocar API key por AD ou Entra ID.
- Calibrar USN Journal por volume.
