# Blueprint Inspirado no Varonis para o File Server Monitor

Este documento consolida os pontos mais aproveitaveis do material `Varonis Quarterly Business Review - TCE-AL (Q2 2023)` para evoluir o File Server Monitor sem tentar copiar a ferramenta original. A proposta aqui e aproveitar a logica de valor entregue ao gestor e ao time operacional, adaptando para o que o produto ja tem: coleta de eventos, correlacao no Core, alertas, inventario por snapshot e relatorios.

## O que o QBR do Varonis realmente mostra

Ao destrinchar o deck, a parte aproveitavel para File Server se organiza em quatro blocos bem claros:

1. crescimento e forma do ambiente;
2. exposicao e governanca;
3. comportamento anomalo e investigacao;
4. maturidade e evolucao ao longo do tempo.

Os slides mais uteis para inspirar o produto foram:

- `slide 8`: crescimento do numero de arquivos, pastas e volume;
- `slide 9`: blast radius, risco de leitura ampla e modificacao ampla;
- `slide 10`: open access, broken permissions e stale data;
- `slides 12 a 15`: dados sensiveis, stale sensitive files e exposicao por regra;
- `slide 16`: alertas, investigacoes e plataformas mais impactadas;
- `slides 42 a 47`: plano operacional, dashboards recorrentes e revisao continua.

O mais importante aqui nao e o visual da apresentacao. E a forma como ela transforma dados tecnicos em perguntas gerenciais que a lideranca consegue responder em poucos minutos.

## O que o produto ja tem hoje e pode reaproveitar

O File Server Monitor ja tem uma base melhor do que parece para seguir nessa direcao:

- timeline correlacionada no Core;
- monitoramento de agentes, API e banco;
- alertas operacionais;
- inventario gerencial com snapshot;
- relatorios guiados e personalizados;
- persistencia de timeline para consulta posterior.

Isso significa que o salto agora nao e "inventar um modulo". O salto e encaixar a leitura gerencial por cima do que ja existe.

## Mapeamento Varonis -> File Server Monitor

### 1. Data Growth -> Inventario Executivo

No Varonis, o slide de crescimento e simples e poderoso: total de arquivos, total de pastas, total de dados e delta do periodo.

No File Server Monitor, isso deve virar o primeiro bloco do painel de inventario.

#### KPIs recomendados

- total de bytes monitorados;
- total de arquivos;
- total de pastas;
- crescimento em bytes no periodo;
- crescimento de arquivos e pastas;
- ranking de compartilhamentos por tamanho;
- ranking de pastas por tamanho;
- ranking de pastas por quantidade de arquivos.

#### Visuais recomendados

- cards com delta em relacao ao snapshot anterior;
- serie temporal de crescimento por snapshot;
- barras de top areas por tamanho;
- participacao por extensao;
- distribuicao por idade.

### 2. Blast Radius / Open Access -> Governanca e Exposicao

O conceito mais valioso do Varonis para File Server e o de `blast radius`: quanto do ambiente esta aberto demais, exposto demais ou com chance alta de propagacao de dano.

No File Server Monitor isso deve ser traduzido para uma camada de governanca.

#### Achados recomendados

- pastas com permissao ampla;
- pastas com heranca quebrada;
- pastas sem dono claro;
- pastas que concentram muitos arquivos e muitos usuarios;
- areas com dados frios e alta ocupacao;
- areas com executaveis e scripts fora da area tecnica;
- caminhos com muito acesso negado;
- caminhos com muita mudanca em curto periodo.

#### Perguntas que o painel deve responder

- "Se um usuario de negocio for comprometido, qual area tem maior raio de impacto?"
- "Quais pastas estao grandes, frias e caras para manter?"
- "Onde temos acumulacao de dados sem leitura observada?"
- "Onde scripts e executaveis estao aparecendo fora do lugar esperado?"

### 3. Threat Detection & Response -> Alertas e Investigacao

O slide de alertas do Varonis reforca algo que faz muito sentido para o File Server Monitor: nao basta mostrar evento, e preciso mostrar padrao.

Casos com aderencia direta:

- exclusao em massa;
- renomeacao em massa;
- movimentacao em massa;
- leitura em massa sem alteracao;
- atividade fora do horario;
- acessos negados recorrentes;
- picos por usuario;
- picos por host de origem;
- picos por pasta;
- combinacao de rename + move + delete em janela curta.

#### Saidas recomendadas

- cards de anomalia no dashboard;
- alertas priorizados por severidade;
- relatorios guiados focados em investigacao;
- comparativo do comportamento atual contra baseline recente.

### 4. Operational Plan / QBR -> Evolucao e valor entregue

O material do Varonis e forte porque mostra linha de evolucao, nao so fotografia.

No File Server Monitor isso deve aparecer em duas frentes:

- dashboard executivo com comparacao entre periodos;
- relatorio mensal/trimestral com progresso, piora e pendencias.

#### Comparacoes recomendadas

- crescimento de volume;
- crescimento de areas frias;
- reducao ou aumento de acessos negados;
- reducao ou aumento de alertas de massa;
- mudanca nos usuarios mais ativos;
- mudanca nas areas mais ativas;
- itens tratados vs itens ainda pendentes.

## Dashboards recomendados

### Painel 1. Operacional

Foco em resposta rapida e respiracao do ambiente.

- saude da API, banco e agente;
- heartbeat e backlog;
- eventos recentes;
- alertas abertos;
- top usuarios do dia;
- top acoes do dia;
- maiores desvios da baseline.

### Painel 2. Inventario Gerencial

Foco em capacidade, ciclo de vida e uso.

- total de dados monitorados;
- crescimento por snapshot;
- distribuicao por extensao;
- distribuicao por idade;
- top pastas por tamanho;
- top pastas por quantidade de arquivos;
- top areas por atividade observada;
- top usuarios por atividade observada.

### Painel 3. Risco e Governanca

Foco em limpeza, exposicao e racionalizacao.

- areas com acesso amplo;
- areas com heranca quebrada;
- dados frios por faixa;
- arquivos nunca acessados;
- executaveis e scripts em area de negocio;
- acessos negados recorrentes;
- atividades fora do horario;
- areas com crescimento alto e baixa leitura.

### Painel 4. Evolucao Executiva

Foco em reuniao mensal ou trimestral.

- crescimento do ambiente;
- total de eventos correlacionados por periodo;
- total de alertas por periodo;
- principais anomalias;
- areas mais afetadas;
- tendencia de risco;
- pendencias de governanca;
- recomendacoes priorizadas;
- comparativo com o ciclo anterior.

## Relatorios guiados novos sugeridos

Os relatorios atuais cobrem bem investigacao operacional. O QBR inspira relatorios guiados mais gerenciais e recorrentes.

### Sugestoes

1. `Crescimento por compartilhamento`
   - compara snapshots e mostra deltas por share, pasta e extensao.

2. `Dados frios e sem acesso`
   - lista areas com arquivos sem uso ha 90, 180 e 365 dias.

3. `Pastas mais ativas`
   - top caminhos por volume de evento no periodo.

4. `Usuarios mais ativos`
   - top usuarios por acoes, area impactada e horario.

5. `Movimentacao em massa`
   - recorte gerencial de move com top caminhos e top usuarios.

6. `Atividade fora do horario`
   - foca em criacao, exclusao, rename e move fora da janela esperada.

7. `Executaveis e scripts em area de negocio`
   - cruza inventario, criacao e alteracao de extensoes sensiveis.

8. `Acesso negado por area`
   - agrupa negacoes por caminho, usuario, host e horario.

9. `Pasta quente`
   - combina tamanho, numero de arquivos e atividade recente para achar areas problematica de uso intenso.

10. `Capacidade e limpeza`
   - mistura top pastas por tamanho com arquivos frios e recomendacao de arquivamento.

## Ideias que valem muito para a nossa realidade

### 1. Semaforo gerencial por compartilhamento

Cada compartilhamento recebe um resumo:

- `verde`: crescimento controlado, sem exposicao relevante e sem anomalia importante;
- `amarelo`: crescimento acelerado, dados frios altos ou acessos negados recorrentes;
- `vermelho`: exposicao ampla, picos de exclusao/rename/move, backlog ou erro de coleta.

### 2. Score de higiene da area

Um score simples de 0 a 100 por compartilhamento ou pasta raiz, formado por:

- exposicao;
- quantidade de dados frios;
- arquivos suspeitos;
- acessos negados;
- eventos de massa;
- tendencia de crescimento desorganizado.

Isso ajuda muito em reuniao com area de negocio porque evita cair no detalhe tecnico cedo demais.

### 3. Recomendacoes automaticas de alto valor

Exemplos:

- "Financeiro concentra 312 GB sem acesso ha 365 dias."
- "RH tem 48 arquivos `.ps1` fora da area tecnica."
- "Compartilhamento Corporativo cresceu 18% no mes, puxado por 3 subpastas."
- "DTI teve aumento de 240% em renomeacoes no periodo."
- "Area X tem heranca quebrada em 27 subpastas e sem dono definido."

### 4. Linha historica de maturidade

Nao precisa ser um score de marketing. Pode ser um historico simples com:

- volume total;
- percentual frio;
- total de alertas;
- total de negacoes;
- total de areas criticas;
- recomendacoes abertas;
- recomendacoes resolvidas.

## O que nao vale reproduzir agora

Alguns pontos do deck nao devem virar backlog imediato:

- classificacao profunda de conteudo sem motor dedicado;
- metricas puramente de AD sem impacto no file server;
- camadas comerciais e servicos do fabricante;
- dashboards bonitos demais antes de amadurecer o dado base;
- automacao pesada de remediacao antes de consolidar leitura e recomendacao.

## Ordem recomendada de implementacao

### Fase 1. Consolidar base operacional

- continuar a exaustao de correlacao;
- manter timeline persistida como fonte oficial de relatorios;
- estabilizar fila, backlog e saude do agente em carga mais alta;
- garantir consistencia entre API, relatorio e frontend.

### Fase 2. Subir o nivel do inventario

- historico de snapshots mais exploravel;
- crescimento por compartilhamento e por pasta;
- ranking de top areas por tamanho e crescimento;
- dados frios por faixa;
- recomendacoes automaticas de limpeza.

### Fase 3. Subir o nivel de governanca

- leitura opcional de ACL;
- deteccao de permissao ampla;
- heranca quebrada;
- dono da area;
- score de higiene.

### Fase 4. Subir o nivel executivo

- dashboard de evolucao mensal/trimestral;
- recortes prontos para QBR;
- exportacao executiva em texto/PDF;
- comparacao entre ciclos com `melhorou`, `manteve`, `piorou`.

## Decisao de produto

O File Server Monitor nao deve tentar "virar Varonis". O melhor caminho e:

- manter profundidade em auditoria e timeline;
- crescer em inventario e governanca de forma pragmatica;
- apresentar evolucao historica;
- transformar sinais tecnicos em visao gerencial acionavel.

Esse e o equilibrio mais forte entre valor entregue, custo de implementacao e clareza para o usuario final.
