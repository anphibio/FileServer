# Seguranca e idempotencia da ingestao

## Objetivo

Separar identidade humana de identidade de maquina e garantir que uma repeticao
de envio nao produza eventos, alertas ou materializacoes duplicadas.

## Fronteiras de autenticacao

| Ator | Credencial | Uso |
| --- | --- | --- |
| Usuario | Sessao LDAP/AD assinada | Consultas e operacoes conforme perfil |
| Administracao tecnica | `Auth:AdminApiKey` | Contingencia administrativa controlada |
| Agente | `Auth:AgentApiKey` + `X-Agent-Id` | Coleta, heartbeat e inventario |

A chave do agente e recusada nas rotas humanas. As chaves humana e administrativa
sao recusadas nas rotas de maquina. O bundle React contem apenas a URL da API.

## Identidade do evento

O agente envia os identificadores nativos do Windows. O Core normaliza esses
campos e calcula `SourceEventId` com SHA-256 sobre:

1. agente;
2. tipo de cursor;
3. horario UTC original;
4. RecordId ou USN;
5. volume;
6. FileReferenceId.

O mesmo evento produz sempre a mesma identidade. Um cursor reutilizado depois de
reset nao colide porque o horario original tambem participa do hash.

## Persistencia

A tabela `dbo.FileAuditEvents` preserva o evento bruto e os campos de evidencia.
O indice unico filtrado por `(AgentId, SourceEventId)` impede duplicidade no banco.
Somente eventos aceitos:

- alimentam o motor de alertas;
- abrem uma janela na fila de materializacao;
- contam como novos na resposta da API.

A resposta de lote informa `acceptedEvents` e `duplicateEvents`. Para o agente,
uma repeticao reconhecida e sucesso: o evento ja esta duravelmente persistido.

## Implantacao

1. Execute `docker/sql/001_create_schema.sql` com uma conta autorizada a alterar schema.
2. Configure `FILESERVER_MONITOR_AGENT_API_KEY` na API.
3. Configure o mesmo segredo apenas no `appsettings.agent.json` do servidor.
4. Configure `FILESERVER_MONITOR_SESSION_SIGNING_KEY` separado das API keys.
5. Publique o agente antes de remover qualquer compatibilidade temporaria.
6. Valide heartbeat, fila local e os campos de evidencia no SQL Server.

Nunca exponha essas chaves em `VITE_*`, HTML, JavaScript ou headers definidos pelo navegador.
