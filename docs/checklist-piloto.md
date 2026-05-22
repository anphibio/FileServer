# Checklist do Piloto

## Antes de Comecar

- [ ] SQL Server definido.
- [ ] Scripts `001_create_schema.sql` e `002_create_app_login.sql` executados.
- [ ] API respondendo `/health`.
- [ ] Painel web acessivel.
- [ ] API key definida, se autenticacao estiver ativa.
- [ ] Pasta piloto escolhida.
- [ ] Janela de teste aprovada com o time.
- [ ] Backup e politica de retencao conhecidos.

## Windows Server

- [ ] PowerShell disponivel.
- [ ] Acesso de rede ate a API.
- [ ] `Test-FileServerMonitorPrereqs.ps1` executado.
- [ ] Security Log acessivel.
- [ ] Auditoria de acesso a objetos habilitada.
- [ ] Auditoria NTFS aplicada com `-WhatIf`.
- [ ] Auditoria NTFS aplicada sem `-WhatIf`.
- [ ] Sincronizacao por API testada com `Sync-FileServerAuditFromApi.ps1`.

## Agente

- [ ] Agente publicado para `win-x64`.
- [ ] Arquivos copiados para `C:\FileServerMonitor\Agent`.
- [ ] `appsettings.agent.json` ajustado.
- [ ] `apiBaseUrl` correto.
- [ ] `apiKey` configurada, se necessario.
- [ ] `enableSecurityLogCollector=true`.
- [ ] `enableUsnJournalCollector=false` no primeiro teste.
- [ ] Servico instalado.
- [ ] Servico iniciado.
- [ ] Heartbeat visivel em `/api/agents/health`.

## Testes Funcionais

- [ ] Criacao de arquivo capturada.
- [ ] Edicao de arquivo capturada.
- [ ] Exclusao de arquivo capturada.
- [ ] Renomeacao capturada ou aproximada.
- [ ] Alteracao de permissao capturada.
- [ ] Usuario aparece corretamente.
- [ ] Processo aparece quando disponivel.
- [ ] Eventos aparecem no painel.
- [ ] Alertas aparecem no painel.
- [ ] Reconhecimento de alerta funciona.

## Resiliencia

- [ ] API parada por alguns minutos.
- [ ] Agente manteve fila local.
- [ ] API religada.
- [ ] Fila foi enviada.
- [ ] Cursor `LastRecordId` avancou corretamente.

## USN Journal

- [ ] Security Log validado primeiro.
- [ ] `enableUsnJournalCollector=true` testado em volume piloto.
- [ ] `LastUsnByVolume` aparece no heartbeat.
- [ ] Eventos USN aparecem com fonte `usn-journal`.
- [ ] Correlacao aparece como `usn-journal+security-log` quando aplicavel.

## Aceite

- [ ] Latencia media ate 1 minuto.
- [ ] Impacto aceitavel em CPU.
- [ ] Impacto aceitavel em disco.
- [ ] Sem crescimento perigoso do Security Log.
- [ ] Sem fila local acumulando continuamente.
- [ ] Alertas calibrados.
- [ ] Plano de expansao aprovado.
