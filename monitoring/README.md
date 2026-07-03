# Monitoramento FileServer Monitor

Este pacote adiciona observabilidade para a API, banco e agentes.

## API

A API expõe:

- `GET /health`: checagem simples de disponibilidade.
- `GET /metrics`: retrato completo do ambiente em JSON para Zabbix/Grafana.

O `/metrics` retorna status geral, status do banco, idade do último evento, total de eventos, capacidade das tabelas principais, resumo dos agentes, fila local dos agentes, batimentos, atraso do último evento coletado e contadores do último ciclo de coleta.

Principais sinais de banco e capacidade:

- `database.status`: saúde da consulta básica do banco.
- `database.queryDurationMs`: tempo da consulta básica de saúde.
- `capacity.status`: saúde da coleta de capacidade.
- `capacity.totalRows`: total de linhas nas tabelas principais.
- `capacity.totalReservedMb`: espaço reservado pelas tabelas principais.
- `capacity.rawEventRows`: linhas de eventos brutos.
- `capacity.timelineRows`: linhas da timeline correlacionada.
- `capacity.alertRows`: linhas de alertas.
- `capacity.timelineNewestAgeSeconds`: idade do evento mais novo da timeline.

Principais sinais de agente:

- `operationalStatus`: `ok`, `attention` ou `critical`.
- `lastCycleSecurityEventsRead`: eventos lidos no Security Log no último ciclo.
- `lastCycleUsnEventsRead`: eventos lidos no USN Journal no último ciclo.
- `lastCycleCorrelatedEvents`: eventos após correlação no último ciclo.
- `lastCycleSentEvents`: eventos enviados para a API no último ciclo.
- `lastCycleQueuedEvents`: eventos que precisaram ficar na fila local.
- `maxCollectedEventAgeSeconds`: maior atraso desde o último evento coletado por um agente.
- `cycleErrors`: quantidade de agentes cujo último ciclo reportou erro.

## Zabbix

Importe o arquivo:

`monitoring/zabbix/fileserver-monitor-template.yaml`

Depois crie ou selecione um host no Zabbix para representar a aplicação e vincule o template `Template App FileServer Monitor`.

Macros principais:

- `{$FILESERVER_MONITOR_URL}`: URL base da API. Exemplo: `http://192.168.2.170:8180`
- `{$FILESERVER_MONITOR_QUEUE_WARN}`: alerta quando a fila local de algum agente chegar nesse valor.
- `{$FILESERVER_MONITOR_LAST_EVENT_MAX_AGE}`: alerta quando nenhum evento novo chegar por esse tempo, em segundos.
- `{$FILESERVER_MONITOR_DB_RESERVED_WARN_MB}`: alerta quando as tabelas principais reservarem mais espaço que esse limite, em MB.

O template usa item HTTP agent no endpoint `/metrics` e cria itens dependentes com JSONPath.

## Grafana

Importe o arquivo:

`monitoring/grafana/fileserver-monitor-dashboard.json`

O dashboard espera o datasource do plugin Zabbix (`alexanderzobnin-zabbix-datasource`). Na importação, selecione o datasource e o host Zabbix onde o template foi vinculado.

Depois da importação, use as variáveis no topo do dashboard:

- `Host group`: grupo de hosts vindo do Zabbix.
- `Host`: hosts filtrados pelo grupo selecionado.

Observação para manutenção do dashboard: itens textuais do Zabbix, como `overall status` e `database status`, devem usar Query type `Text` no Grafana, mantendo `Group`, `Host`, `Application` e `Item` preenchidos. No JSON exportado pelo plugin, esse modo aparece como `queryType: "2"`. Contadores, tempos e idades usam Query type `Metrics`.
