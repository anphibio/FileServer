# Blueprint Inspirado no Varonis para o File Server Monitor

Este documento consolida os pontos mais aproveitaveis do material `Varonis Quarterly Business Review - TCE-AL (Q2 2023)` para evoluir o File Server Monitor sem copiar a ferramenta original. A ideia e aproveitar a logica de valor entregue ao gestor e ao time operacional, adaptando para o que ja existe no produto: coleta de eventos, correlacao no Core, alertas, inventario gerencial e relatorios.

## Leitura do material

O material do Varonis mistura quatro camadas:

1. inventario e crescimento do ambiente;
2. exposicao e risco de dados;
3. deteccao de comportamento suspeito;
4. acompanhamento de maturidade e valor ao longo do tempo.

Para o File Server Monitor, as duas primeiras fases ja estao parcialmente preparadas:

- eventos e timeline correlacionada;
- monitoramento de agentes, API e banco;
- relatorios guiados e personalizados;
- inventario gerencial com snapshot.

O maior ganho agora e transformar esses blocos em visao historica e gerencial.

## O que vale reaproveitar

### 1. Inventario executivo do ambiente

Os slides de visibilidade e crescimento mostram que o gestor entende rapidamente:

- total de dados monitorados;
- quantidade de arquivos;
- quantidade de pastas;
- crescimento no periodo;
- tendencia de expansao.

Isso deve existir no File Server Monitor como painel fixo de inventario executivo.

#### Indicadores recomendados

- total de bytes por compartilhamento;
- total de arquivos e pastas;
- crescimento diario, semanal e mensal;
- top pastas por tamanho;
- top pastas por quantidade de arquivos;
- distribuicao por extensao;
- distribuicao por faixa de idade;
- variacao entre snapshots.

### 2. Governanca e risco da estrutura

Os slides de `Data Protection` do Varonis tem forte aderencia com a realidade de file server. Mesmo sem classificar conteudo ainda, da para entregar muito valor com governanca basica.

#### Indicadores recomendados

- pastas com acesso amplo;
- pastas com heranca quebrada;
- areas sem dono claro;
- pastas com crescimento alto e pouca atividade;
- arquivos frios sem acesso ha 90/180/365 dias;
- arquivos nunca acessados;
- executaveis e scripts em areas de negocio;
- concentracao de dados por area.

#### Recomendacoes automaticas sugeridas

- "Area X concentra Y GB sem acesso ha 365 dias."
- "Area Y possui heranca quebrada em Z subpastas."
- "Area Z concentra scripts/executaveis fora da area tecnica."
- "Compartilhamento A cresceu B% no periodo com pouca leitura observada."

### 3. Comportamento e ameacas operacionais

O slide de `Threat Detection & Response` mostra uma direcao boa para o File Server Monitor: nao mostrar so eventos, mas mostrar anomalias e padroes de risco.

#### Casos de uso recomendados

- exclusao em massa;
- renomeacao em massa;
- movimentacao em massa;
- leitura sem alteracao em volume alto;
- acessos negados recorrentes;
- atividade fora do horario;
- atividade intensa por host de origem;
- picos por usuario, pasta ou compartilhamento;
- processos com maior volume de eventos.

Esses casos se encaixam em duas areas do produto:

- alertas operacionais;
- relatorios gerenciais recorrentes.

### 4. Evolucao e valor entregue

O diferencial do material do Varonis nao e so a fotografia do ambiente, mas a comparacao entre periodos.

O File Server Monitor deve fazer o mesmo em relatorios mensais e trimestrais:

- risco subiu ou caiu;
- volume cresceu ou estabilizou;
- areas mais ativas mudaram;
- usuarios mais ativos mudaram;
- anomalias reduziram ou pioraram;
- recomendacoes abertas vs tratadas.

## Painel recomendado para o produto

### Painel 1. Operacional

Voltado para quem responde rapido:

- saude da API, banco e agente;
- fila e backlog;
- eventos recentes;
- alertas abertos;
- top usuarios do dia;
- top acoes do dia;
- maiores desvios da baseline.

### Painel 2. Inventario Gerencial

Voltado para capacidade e organizacao:

- total de dados monitorados;
- crescimento por periodo;
- composicao do armazenamento;
- ciclo de vida dos arquivos;
- top pastas por tamanho;
- top pastas por volume de arquivos;
- top areas por atividade observada;
- top usuarios por atividade observada.

### Painel 3. Risco e Governanca

Voltado para revisao de exposicao:

- areas com acesso amplo;
- areas com permissao quebrada;
- dados frios;
- arquivos nunca acessados;
- executaveis e scripts em areas sensiveis;
- acessos negados recorrentes;
- areas com atividade fora do horario.

### Painel 4. Evolucao executiva

Voltado para reuniao mensal ou trimestral:

- crescimento do ambiente;
- total de eventos por periodo;
- total de alertas por periodo;
- principais anomalias;
- areas mais afetadas;
- tendencia de risco;
- pendencias de governanca;
- recomendacoes priorizadas.

## Relatorios guiados novos sugeridos

Os relatorios atuais cobrem investigacao operacional. O material do Varonis inspira alguns guiados novos, mais gerenciais.

### Sugestoes para a aba Relatorios

1. `Crescimento por compartilhamento`
   - foca em inventario e crescimento entre snapshots.

2. `Dados frios e sem acesso`
   - lista areas com arquivos sem uso ha 90/180/365 dias.

3. `Pastas mais ativas`
   - mostra top caminhos por evento no periodo.

4. `Usuarios mais ativos`
   - mostra top usuarios por quantidade de acoes e area impactada.

5. `Movimentacao em massa`
   - semelhante ao de exclusao e renomeacao, mas focado em move.

6. `Atividade fora do horario`
   - focado em criacoes, exclusoes, renomes e moves fora da janela definida.

7. `Executaveis e scripts em area de negocio`
   - combina criacao, alteracao e movimentacao de extensoes sensiveis.

8. `Acesso negado por area`
   - agrupa negacoes por caminho, usuario e host.

## O que nao vale reproduzir agora

Alguns pontos do Varonis nao devem virar backlog imediato:

- classificacao profunda de conteudo sem motor dedicado;
- metricas muito especificas de AD sem beneficio direto ao file server;
- camadas comerciais e de servicos;
- dashboards amplos demais antes de amadurecer os dados-base.

## Roadmap recomendado

### Fase 1. Consolidar o que ja existe

- estabilizar coleta e correlacao em cenarios mistos;
- finalizar inventario gerencial atual;
- consolidar historico por snapshot;
- manter relatorios usando timeline persistida.

### Fase 2. Subir o nivel gerencial

- crescimento por compartilhamento;
- top areas por atividade;
- usuarios mais ativos;
- dados frios;
- recomendacoes automaticas de limpeza e revisao.

### Fase 3. Subir o nivel de risco

- permissoes amplas;
- heranca quebrada;
- arquivos sensiveis por extensao/area;
- atividade fora do horario;
- combinacao de alertas com inventario.

### Fase 4. Fechar o ciclo executivo

- dashboard trimestral;
- comparacao entre snapshots;
- tendencia de risco;
- relatorio QBR nativo do produto;
- exportacao pronta para reunioes gerenciais.

## Decisao de produto

O File Server Monitor nao deve tentar "virar Varonis". O caminho mais forte e:

- manter profundidade em auditoria e timeline;
- ganhar maturidade em inventario e governanca;
- apresentar evolucao historica;
- transformar eventos tecnicos em sinais gerenciais.

Esse e o melhor equilibrio entre valor entregue, custo de implementacao e clareza para o usuario final.
