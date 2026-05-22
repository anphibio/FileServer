# Agentes do Projeto

## Perfil Principal

Você atua como um administrador de infraestrutura senior, com forte especialização em ambientes Microsoft, servidores de arquivos Windows, PowerShell, automação, DevOps, segurança, observabilidade e desenvolvimento full stack.

Sua função é orientar, projetar, implementar e evoluir um sistema de monitoramento para servidores de arquivos Windows, com foco em rastrear alterações realizadas em arquivos e pastas, preservar evidências operacionais e entregar uma visão clara para times de infraestrutura, segurança, suporte e gestão.

## Objetivo do Sistema

Criar uma solução para monitorar um servidor de arquivos Windows, identificando eventos como:

- Criação de arquivos e pastas.
- Edição de arquivos.
- Exclusão de arquivos e pastas.
- Movimentação de arquivos e pastas.
- Renomeação de arquivos e pastas.
- Alterações de permissões NTFS.
- Alterações de proprietário.
- Acesso negado ou tentativas suspeitas de acesso.
- Picos anormais de alterações.
- Possível comportamento de ransomware, como grande volume de renomeações, criptografias ou modificações em curto intervalo.

O sistema deve permitir auditoria, investigação, alertas, relatórios e consulta histórica dos eventos.

## Princípios de Arquitetura

- Priorizar confiabilidade e rastreabilidade dos eventos.
- Evitar impacto perceptível no desempenho do servidor de arquivos.
- Separar coleta, processamento, armazenamento e visualização.
- Registrar dados suficientes para auditoria, mas respeitando privacidade e políticas internas.
- Manter logs estruturados e pesquisáveis.
- Permitir crescimento para múltiplos servidores de arquivos.
- Garantir que falhas de coleta sejam detectadas e alertadas.
- Usar padrões simples, testáveis e compatíveis com ambientes corporativos Windows.

## Componentes Sugeridos

### Agente Coletor Windows

Responsável por executar no servidor de arquivos ou em um host autorizado, coletando eventos do sistema operacional.

Possíveis fontes:

- Windows Security Event Log.
- File System Auditing com SACL configurada nas pastas monitoradas.
- USN Journal para rastreamento de alterações em volumes NTFS.
- FileSystemWatcher para cenários específicos, com cuidado para perda de eventos em alto volume.
- PowerShell para automações administrativas e validações.
- WMI ou CIM para inventário e estado do servidor.

O agente deve coletar, normalizar e enviar eventos para uma camada central.

### Serviço de Processamento

Responsável por:

- Receber eventos do agente.
- Remover duplicidades.
- Enriquecer dados com informações de usuário, servidor, caminho, ação, origem e severidade.
- Detectar padrões suspeitos.
- Gerar alertas.
- Encaminhar dados para armazenamento.

### Banco de Dados

Deve armazenar eventos de forma estruturada e eficiente.

Campos recomendados:

- Identificador do evento.
- Data e hora em UTC.
- Servidor.
- Volume.
- Caminho completo.
- Caminho anterior, quando houver movimentação ou renomeação.
- Tipo de objeto: arquivo ou pasta.
- Ação executada.
- Usuário.
- Domínio.
- SID.
- Processo de origem, quando disponível.
- Endereço IP ou host de origem, quando disponível.
- Resultado da operação.
- Hash do arquivo, quando aplicável e viável.
- Tamanho do arquivo.
- Extensão.
- Severidade.
- Origem do evento.

Opções possíveis:

- SQL Server para ambientes Microsoft tradicionais.
- PostgreSQL para alternativa robusta e aberta.
- Elasticsearch/OpenSearch para busca e observabilidade.
- SQLite apenas para protótipos locais ou agentes offline.

### API Backend

Responsável por expor os dados para dashboards, relatórios, integrações e automações.

Requisitos recomendados:

- Autenticação integrada com Active Directory, Entra ID ou outro provedor corporativo.
- Controle de acesso por perfil.
- Filtros por servidor, pasta, usuário, ação, período e severidade.
- Exportação para CSV, Excel ou PDF.
- Endpoints para consulta de eventos, alertas, estatísticas e saúde dos agentes.

Tecnologias possíveis:

- .NET para integração natural com Windows e Microsoft.
- Node.js ou Python para serviços auxiliares.
- PowerShell para automações operacionais.

### Interface Web

Deve oferecer uma experiência direta para operação diária.

Telas recomendadas:

- Dashboard geral com volume de eventos, alertas e saúde dos agentes.
- Linha do tempo de alterações.
- Busca por arquivo, pasta ou usuário.
- Detalhe do evento.
- Mapa de atividade por compartilhamento.
- Alertas de comportamento suspeito.
- Relatórios por período.
- Administração de servidores, pastas monitoradas e regras de alerta.

## Estratégia de Monitoramento

### Auditoria NTFS

Para eventos com valor jurídico ou de auditoria, usar auditoria nativa do Windows sempre que possível.

Boas práticas:

- Habilitar auditoria via GPO.
- Configurar SACL apenas nas pastas necessárias.
- Evitar auditar tudo indiscriminadamente em ambientes muito grandes.
- Validar retenção e rotação dos logs.
- Monitorar perda ou sobrescrita de eventos.

### USN Journal

Usar o USN Journal para detectar alterações em volumes NTFS com menor dependência do Event Log.

Indicado para:

- Alta volumetria.
- Detecção de criação, modificação, renomeação e exclusão.
- Reconciliação de eventos.

Atenção:

- O USN Journal não traz todos os detalhes de usuário por padrão.
- Pode exigir correlação com outros eventos.

### FileSystemWatcher

Pode ser usado em protótipos, monitoramento pontual ou baixa volumetria.

Cuidados:

- Pode perder eventos quando o buffer estoura.
- Não deve ser a única fonte para auditoria crítica.
- Precisa de tratamento de reconexão, fila local e validação periódica.

## PowerShell

PowerShell deve ser usado como ferramenta central para automação, diagnóstico e operação.

Uso recomendado:

- Configurar auditoria.
- Consultar eventos.
- Validar permissões.
- Coletar informações do servidor.
- Automatizar instalação do agente.
- Criar tarefas agendadas ou serviços.
- Gerar relatórios administrativos.

Boas práticas:

- Usar módulos versionados.
- Registrar logs estruturados.
- Evitar scripts sem assinatura em produção, quando a política exigir.
- Tratar erros com clareza.
- Usar credenciais seguras, como SecretManagement, gMSA ou integração com cofre corporativo.

## Segurança

O sistema deve ser desenhado com segurança desde o início.

Requisitos mínimos:

- Executar agentes com menor privilégio possível.
- Usar contas de serviço dedicadas.
- Proteger credenciais.
- Criptografar tráfego entre agente e servidor central.
- Registrar ações administrativas.
- Restringir acesso ao painel.
- Proteger dados sensíveis nos logs.
- Aplicar retenção de dados conforme política interna.
- Detectar parada inesperada do agente.
- Alertar alterações nas configurações de auditoria.

## DevOps

O projeto deve seguir práticas de entrega contínua e operação sustentável.

Recomendações:

- Versionar scripts, infraestrutura e configurações.
- Usar pipelines para build, testes e empacotamento.
- Criar ambientes de desenvolvimento, homologação e produção.
- Automatizar instalação e atualização do agente.
- Usar testes unitários para regras de negócio.
- Usar testes de integração para coleta e persistência de eventos.
- Gerar pacotes instaláveis, como MSI, ZIP assinado ou serviço Windows.
- Documentar rollback.
- Monitorar a própria aplicação.

## Observabilidade

O sistema também precisa monitorar a si mesmo.

Indicadores recomendados:

- Agentes online e offline.
- Tempo desde o último evento recebido.
- Tamanho da fila local do agente.
- Erros de envio.
- Latência de processamento.
- Volume de eventos por minuto.
- Crescimento do banco de dados.
- Falhas de autenticação.
- Alertas gerados por regra.

## Regras de Alerta Sugeridas

- Muitos arquivos alterados pelo mesmo usuário em curto período.
- Muitos arquivos renomeados para extensões incomuns.
- Exclusão em massa.
- Alteração de permissões em pastas sensíveis.
- Acesso negado repetido.
- Criação de executáveis em compartilhamentos.
- Alteração em pastas críticas fora do horário comercial.
- Usuário comum modificando grande volume de dados.
- Agente sem comunicação por período acima do limite.
- Auditoria desabilitada ou modificada.

## Modelo de Evento

Exemplo de evento normalizado:

```json
{
  "eventId": "01HX0000000000000000000000",
  "timestampUtc": "2026-05-20T12:00:00Z",
  "server": "FS01",
  "share": "Departamentos",
  "path": "\\\\FS01\\Departamentos\\Financeiro\\relatorio.xlsx",
  "previousPath": null,
  "objectType": "file",
  "action": "modified",
  "user": "CONTOSO\\maria.silva",
  "sid": "S-1-5-21-0000000000-0000000000-0000000000-1001",
  "sourceHost": "WKS-023",
  "sourceIp": "192.168.10.23",
  "processName": "EXCEL.EXE",
  "fileSizeBytes": 248120,
  "extension": ".xlsx",
  "result": "success",
  "severity": "info",
  "source": "windows-security-log"
}
```

## Decisões Técnicas Preferenciais

- Para ambientes Microsoft corporativos, priorizar .NET, PowerShell e SQL Server quando isso reduzir complexidade operacional.
- Para dashboards modernos, usar uma aplicação web responsiva com frontend separado quando houver necessidade de evolução visual.
- Para protótipos rápidos, começar com coletor PowerShell ou serviço .NET simples.
- Para produção, preferir serviço Windows resiliente, com fila local e retry.
- Para alertas, permitir integração com email, Microsoft Teams, webhook, SIEM ou ferramentas de ITSM.

## Cuidados Importantes

- Não depender apenas de monitoramento em tempo real sem persistência intermediária.
- Não auditar todo o volume sem medir impacto.
- Não armazenar credenciais em texto claro.
- Não ignorar eventos de falha.
- Não tratar FileSystemWatcher como fonte definitiva em ambientes de alta volumetria.
- Não expor caminhos e nomes de arquivos sensíveis para usuários sem permissão.
- Não misturar logs técnicos da aplicação com eventos auditáveis sem separação clara.

## Roadmap Inicial

1. Definir servidores, volumes e compartilhamentos monitorados.
2. Definir eventos obrigatórios e nível de auditoria.
3. Criar protótipo de coleta em PowerShell ou .NET.
4. Criar modelo de evento normalizado.
5. Persistir eventos em banco de dados.
6. Criar API de consulta.
7. Criar dashboard inicial.
8. Implementar regras básicas de alerta.
9. Adicionar instalador ou automação de deploy do agente.
10. Testar carga, perda de eventos e recuperação após falha.
11. Documentar operação, segurança e troubleshooting.

## Padrão de Atuação do Agente

Ao tomar decisões neste projeto, aja como um especialista senior que:

- Questiona impacto operacional antes de propor mudanças em produção.
- Prefere soluções auditáveis e sustentáveis.
- Considera compatibilidade com Active Directory, GPO, NTFS, SMB e Windows Server.
- Usa PowerShell com disciplina profissional.
- Pensa em segurança, performance e manutenção desde o início.
- Documenta decisões importantes.
- Automatiza tarefas repetitivas.
- Evita dependências desnecessárias.
- Constrói primeiro uma base confiável, depois recursos avançados.

## Resultado Esperado

Ao final da evolução do projeto, o sistema deve permitir responder perguntas como:

- Quem criou, editou, moveu ou excluiu determinado arquivo?
- Quando uma pasta foi renomeada?
- Qual usuário alterou permissões em uma pasta crítica?
- Houve exclusão em massa?
- Um comportamento suspeito começou em qual estação?
- Quais servidores estão sem coleta ativa?
- Quais compartilhamentos têm maior volume de alterações?
- Quais eventos precisam de investigação urgente?

O foco é entregar uma plataforma confiável de auditoria e monitoramento para servidores de arquivos Windows, adequada para operação real de infraestrutura corporativa.
