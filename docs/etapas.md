# Etapas do Projeto

## Cenario Inicial

- 1 servidor de arquivos.
- Windows Server 2022 Datacenter.
- Mais de 8 TB de dados.
- Milhoes de arquivos.
- Muitos compartilhamentos SMB.
- Aproximadamente 400 usuarios no Active Directory.
- Execucao da plataforma central em Docker.
- Banco local em SQL Server container para desenvolvimento.
- Banco externo SQL Server para producao.

## Etapa 1: Fundacao da Plataforma

Objetivo: criar a base executavel do sistema.

Entregas:

- API HTTP para receber eventos.
- Endpoint de saude.
- Endpoint de consulta de eventos.
- Dockerfile da API.
- Docker Compose com API e SQL Server local.
- Script SQL inicial para tabelas e indices.
- Script PowerShell inicial para configurar auditoria NTFS em pastas piloto.

Status: iniciada.

## Etapa 2: Persistencia SQL Server

Objetivo: substituir armazenamento temporario em memoria por SQL Server.

Entregas:

- Repositorio SQL Server.
- Insercao em lote.
- Consulta filtrada por servidor, usuario, acao, caminho e periodo.
- Busca por identificador do evento.
- Endpoint de saude mostrando provedor de armazenamento.
- Indices iniciais para data, servidor, acao, usuario e caminho.
- Configuracao por connection string.
- Preparacao para SQL Server externo em producao.

Status: implementada em codigo. Validacao completa depende da restauracao do pacote `Microsoft.Data.SqlClient`.

## Etapa 3: Agente Windows

Objetivo: criar o coletor que roda no Windows Server 2022.

Entregas:

- Servico Windows em .NET.
- Leitura incremental do Windows Security Event Log.
- Controle de ultimo evento processado.
- Fila local em disco.
- Retry de envio para a API.
- Heartbeat do agente.
- Instalacao automatizada via PowerShell.
- Persistencia SQL do heartbeat.

Status: base implementada com heartbeat persistido.

Observacoes:

- O agente usa um processo PowerShell para coletar eventos do Security Log.
- O estado local so avanca apos envio bem-sucedido para a API.
- Se a API estiver indisponivel, eventos coletados sao salvos em fila local.
- A instalacao como servico Windows esta preparada por script.
- A saude dos agentes e persistida em `dbo.AgentHeartbeats` quando a API usa SQL Server.
- A validacao funcional completa deve ser feita no Windows Server 2022 com auditoria NTFS ativa.

## Etapa 4: Coleta Avancada

Objetivo: melhorar precisao e resiliencia da coleta.

Entregas:

- Coletor opcional do USN Journal.
- Cursores separados para Security Log e USN Journal.
- Estado por volume NTFS em `LastUsnByVolume`.
- Eventos USN enviados no mesmo contrato da API.
- Correlacao inicial por caminho e janela de tempo entre Security Log e USN Journal.
- Enriquecimento de eventos USN com usuario, SID, host, IP e processo quando houver correspondencia.
- Marcacao de eventos correlacionados com fonte `usn-journal+security-log`.
- Tratamento inicial de picos via lote, fila local e cursor incremental.

Status: base implementada com correlacao inicial.

Observacoes:

- O USN Journal e eficiente para detectar mudancas em volumes grandes, mas nao identifica usuario sozinho.
- A correlacao entre Security Log e USN Journal melhora investigacao operacional, mas a evidencia primaria de usuario continua sendo o Security Log.
- A janela de correlacao deve ser calibrada no Windows Server 2022 com eventos reais.
- A versao atual usa `fsutil usn readjournal` como primeiro passo operacional. Uma etapa futura pode trocar para leitura nativa via API Windows para reduzir dependencia de parsing de texto.

## Etapa 5: Alertas

Objetivo: transformar eventos em sinais operacionais.

Entregas:

- Exclusao em massa.
- Renomeacao em massa.
- Alteracao de permissao em pasta sensivel.
- Muitos acessos negados.
- Atividade fora do horario.
- Agente offline.
- Possivel ransomware.

Status: base implementada na API com persistencia SQL.

Regras implementadas:

- `mass-delete`.
- `mass-rename`.
- `permission-change`.
- `possible-ransomware`.

Observacoes:

- Os alertas sao persistidos em SQL Server quando `Monitor:StorageProvider` esta como `SqlServer`.
- O modo em memoria permanece como fallback de desenvolvimento.
- Os limiares sao configuraveis em `appsettings.json`.

## Etapa 6: Interface Web

Objetivo: entregar uma visao de operacao diaria.

Entregas:

- Dashboard.
- Busca por usuario.
- Busca por arquivo ou pasta.
- Linha do tempo.
- Alertas.
- Saude dos agentes.
- Administracao de pastas monitoradas.

Status: base implementada.

Entregue nesta etapa:

- Projeto React + TypeScript com Vite.
- Dockerfile com build estatico e Nginx.
- Dashboard com indicadores principais.
- Tela de eventos com filtro.
- Tela de alertas com reconhecimento.
- Tela de agentes com heartbeat, RecordId e USN por volume.
- Compose expondo painel em `http://localhost:3000`.

## Etapa 7: Hardening de Producao

Objetivo: preparar para operacao real.

Entregas:

- Autenticacao inicial por API key.
- Preparacao para autenticacao integrada com AD ou Entra ID.
- Controle de acesso por perfil.
- TLS.
- Retencao de dados.
- Backup.
- Logs estruturados.
- Monitoramento da aplicacao.
- Documentacao de troubleshooting.
- Compose de producao com SQL Server externo.
- Reverse proxy Nginx com TLS.
- Exemplo de `.env.production`.
- Runbook do piloto no Windows Server 2022.
- Checklist de aceite do piloto.
- Script de diagnostico de pre-requisitos no servidor de arquivos.
- Notificacoes por webhook generico ou Microsoft Teams.
- Biblioteca Core para regras e normalizacao.
- Testes automatizados basicos sem dependencia externa.
- API integrada ao Core para normalizacao e regras de alerta.
- Agente integrado ao Core para correlacao Security Log + USN Journal.
- Script unico de validacao local.
- Workflow inicial de CI.
- Build local da API sem dependencia obrigatoria do SQL Client.
- Scripts de demo local com API em memoria e seed de dados.
- Retencao automatica configuravel para eventos e alertas antigos.
- Cadastro de caminhos monitorados com API, tabela SQL e tela no painel.
- Relatorio operacional agregado por acao, compartilhamento e usuario.
- Deteccao de heartbeat atrasado para agentes sem comunicacao recente.
- Configuracao remota do agente a partir dos caminhos ativos cadastrados.
- Sincronizacao da auditoria NTFS no Windows Server a partir da API.
- Auditoria administrativa para alteracoes em caminhos monitorados e reconhecimento de alertas.

Status: autenticacao inicial e base de producao implementadas.

Observacoes:

- `Auth:Enabled=false` por padrao para facilitar desenvolvimento.
- Quando habilitada, a API exige `X-Api-Key` ou `Authorization: Bearer`.
- Agente e frontend ja suportam envio da chave.
- AD/Entra ID permanece como evolucao recomendada antes de producao ampla.
- O Compose de producao nao sobe SQL Server local; ele usa `SQLSERVER_CONNECTION_STRING`.
- Certificados TLS devem ser montados em `docker/certs`.
- O piloto deve iniciar por pastas controladas antes de expandir para todos os compartilhamentos.
- Notificacoes ficam desativadas por padrao e podem ser habilitadas por variaveis `NOTIFICATIONS_*`.
- Testes iniciais rodam com `./scripts/run-tests.sh`.
- A API consome o Core para evitar duplicidade de logica nas regras testadas.
- O agente consome o Core para evitar duplicidade na correlacao de eventos.
- Validacao local roda com `./scripts/validate.sh`.
- Build SQL Server pode ser habilitado com `EnableSqlServer=true` ou `VALIDATE_SQLSERVER=true`.
- Demo local pode ser iniciada com `./scripts/run-api-local.sh` e populada com `./scripts/seed-demo.sh`.
- Retencao fica desligada por padrao no desenvolvimento e ligada no exemplo de producao.
- Caminhos monitorados podem ser administrados em `/api/monitored-paths` e na aba Caminhos.
- Dashboard consome `/api/reports/activity-summary` para rankings das ultimas 24 horas.
- API marca agente como `stale` quando ultrapassa `AGENTS_STALE_MINUTES` sem heartbeat.
- Agente pode consultar `/api/agents/config?server=...` e filtrar eventos fora dos caminhos ativos.
- Script `Sync-FileServerAuditFromApi.ps1` aplica auditoria NTFS nos caminhos ativos retornados pela API.
- Endpoint `/api/admin-audit` lista alteracoes administrativas registradas.
- Painel web possui aba Auditoria para consultar o log administrativo.
- Frontend envia `X-Actor` quando `VITE_ACTOR_NAME` ou `PUBLIC_WEB_ACTOR_NAME` esta configurado.
- API diferencia chave operacional e chave administrativa para acoes sensiveis.
- CORS da API passa a ser configuravel por ambiente com `PUBLIC_WEB_ORIGIN`.
- Heartbeat do agente informa fila local pendente e ultimo envio bem-sucedido.
- API marca agente como `backlog` quando fila local passa de `AGENTS_BACKLOG_WARNING_THRESHOLD`.
- Eventos podem ser exportados em CSV pela API e pela aba Eventos do painel web.
- Relatorio operacional aceita filtros por periodo, servidor, compartilhamento, usuario e acao.
- Painel web possui aba Investigacao para consultar linha do tempo por usuario, caminho, servidor, acao e periodo.
- Regras de alerta podem ser consultadas e ajustadas por API e pela aba Alertas, com persistencia em SQL Server.
- Regras de alerta aceitam escopo fino por servidor, share e prefixo de caminho para pastas criticas.
- Regras de alerta aceitam janela de horario com fuso opcional para expediente e fora de expediente.
- Regras de alerta aceitam dias da semana para diferenciar dias uteis, sabado e domingo.
- Regras de alerta aceitam excecoes por usuario, host e processo para reduzir ruido operacional.
- Regras de alerta podem ser simuladas contra eventos recentes antes de entrar em producao.
- Dashboard destaca anomalias por acao, share e usuario comparando o periodo atual com a media historica recente.
- Dashboard e API exportam alertas e anomalias em CSV para acompanhamento executivo.
- Dashboard recebeu acabamento visual com cards executivos, subtitulos contextuais e leitura mais rapida dos alertas recentes.
- Telas de Alertas, Investigacao, Agentes, Caminhos e Auditoria receberam resumos executivos e subtitulos para leitura operacional mais consistente.
- Painel web passou a exibir avisos de sucesso/erro, estado de atualizacao e feedback visual melhor para exportacoes, simulacoes, investigacoes e cadastro de caminhos.
- Navegacao lateral passou a exibir contadores por area, topo ganhou resumo rapido do ambiente e tabelas receberam leitura visual mais confortavel.
- Projeto ganhou compose de preview em memoria, arquivo de ambiente e scripts para subir, popular e encerrar a revisao visual local.
- Preview local ganhou script de checagem para aguardar subida, validar endpoints e opcionalmente popular a demo antes da revisao visual.
- Stack principal ganhou scripts de start, check e stop em Docker, com fallback entre `docker compose` e `docker-compose`.
- Aba de Alertas ganhou link para abrir a lista de operacoes relacionadas ao arquivo ou pasta impactada pelo alerta.
