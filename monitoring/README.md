# Monitoramento FileServer Monitor

Este pacote adiciona observabilidade para a API, banco e agentes.

## API

A API expõe:

- `GET /health`: checagem simples de disponibilidade.
- `GET /metrics`: retrato completo do ambiente em JSON para Zabbix/Grafana.

O `/metrics` retorna status geral, status do banco, idade do último evento, total de eventos, resumo dos agentes, fila local dos agentes, batimentos e configuração de retenção.

## Zabbix

Importe o arquivo:

`monitoring/zabbix/fileserver-monitor-template.yaml`

Depois crie ou selecione um host no Zabbix para representar a aplicação e vincule o template `Template App FileServer Monitor`.

Macros principais:

- `{$FILESERVER_MONITOR_URL}`: URL base da API. Exemplo: `http://192.168.2.170:8180`
- `{$FILESERVER_MONITOR_QUEUE_WARN}`: alerta quando a fila local de algum agente chegar nesse valor.
- `{$FILESERVER_MONITOR_LAST_EVENT_MAX_AGE}`: alerta quando nenhum evento novo chegar por esse tempo, em segundos.

O template usa item HTTP agent no endpoint `/metrics` e cria itens dependentes com JSONPath.

## Grafana

Importe o arquivo:

`monitoring/grafana/fileserver-monitor-dashboard.json`

O dashboard espera o datasource do plugin Zabbix (`alexanderzobnin-zabbix-datasource`). Na importação, selecione o datasource e o host Zabbix onde o template foi vinculado.

