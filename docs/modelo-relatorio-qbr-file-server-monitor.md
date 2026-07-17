# Modelo de Relatorio QBR para o File Server Monitor

Este modelo serve como base para um relatorio mensal ou trimestral apresentado para areas gestoras, seguranca, infraestrutura e dono do compartilhamento.

## Principios do QBR

O relatorio nao deve ser apenas uma exportacao de eventos. Ele precisa responder quatro perguntas:

1. o ambiente cresceu ou encolheu;
2. o risco aumentou ou reduziu;
3. quem mais movimentou o ambiente e onde;
4. o que precisa ser tratado antes do proximo ciclo.

## Estrutura sugerida do relatorio

O formato abaixo foi pensado para refletir o que o QBR da Varonis faz bem, mas usando o que o File Server Monitor consegue sustentar com honestidade.

## 1. Resumo Executivo

- periodo analisado;
- compartilhamentos avaliados;
- variacao do volume armazenado;
- variacao da atividade observada;
- principais riscos identificados;
- principais melhorias desde o ultimo ciclo.

### Exemplo de sintese

No periodo analisado, o ambiente monitorado apresentou crescimento de armazenamento, concentrado principalmente nas areas Financeiro e DTI. A atividade operacional permaneceu mais intensa em criacao, alteracao e movimentacao de arquivos, com destaque para os usuarios mais ativos e para a concentracao de eventos em poucas areas. Foram identificadas oportunidades de limpeza de dados frios, revisao de permissoes amplas e reducao de areas com grande volume e baixa utilizacao.

## 2. Panorama do Ambiente

- total de dados monitorados;
- total de arquivos;
- total de pastas;
- crescimento no periodo;
- distribuicao por compartilhamento;
- distribuicao por categoria de conteudo;
- maiores areas por tamanho.

### Visual recomendado

- cards executivos;
- grafico de crescimento;
- ranking de top pastas por tamanho;
- ranking de top extensoes.
- comparativo com snapshot anterior.

## 3. Atividade Operacional

- total de eventos correlacionados no periodo;
- distribuicao por acao;
- top usuarios;
- top pastas por atividade;
- top hosts de origem;
- picos fora do horario;
- comparacao com periodo anterior.

### Visual recomendado

- barras por acao;
- linha de tendencia semanal;
- ranking por usuario;
- ranking por pasta;
- heatmap por horario e dia da semana.
- comparativo com periodo anterior.

## 4. Risco e Governanca

- arquivos sem acesso ha 90/180/365 dias;
- arquivos nunca acessados;
- executaveis e scripts em areas de negocio;
- acessos negados recorrentes;
- areas com permissao ampla;
- areas com heranca quebrada;
- areas com crescimento alto e baixa atividade.

### Visual recomendado

- cards de risco;
- tabela de achados;
- ranking de areas prioritarias;
- semaforo por severidade.
- score de higiene por compartilhamento ou pasta raiz.

## 5. Incidentes e Anomalias

- exclusoes em massa;
- renomeacoes em massa;
- movimentacoes em massa;
- leituras em volume sem alteracao;
- maiores desvios da baseline;
- alertas abertos e fechados no periodo.

### Visual recomendado

- linha do tempo resumida;
- cards de anomalia;
- comparativo com media historica.
- top caminhos e top usuarios por tipo de incidente.

## 6. Evolucao desde o ultimo ciclo

- volume cresceu ou caiu;
- areas de risco reduziram ou aumentaram;
- acessos negados reduziram ou aumentaram;
- dados frios aumentaram ou reduziram;
- recomendacoes tratadas;
- pendencias mantidas.

### Estrutura recomendada

- `Mantido`
- `Melhorou`
- `Piorou`

## 7. Recomendacoes Prioritarias

Separar sempre por impacto e esforco.

### Alta prioridade

- revisar areas com permissao ampla;
- tratar exclusoes, renomeacoes ou movimentacoes atipicas;
- revisar scripts e executaveis fora da area tecnica.

### Media prioridade

- identificar arquivos frios e areas de arquivamento;
- revisar crescimento de pastas com pouca atividade;
- reduzir concentracao de dados em areas sem dono claro.

### Baixa prioridade

- padronizar nomenclatura;
- revisar estrutura profunda de pastas;
- melhorar ownership e catalogo de dados.

## 8. Plano de Acao para o proximo ciclo

- acao;
- responsavel;
- prazo;
- indicador de sucesso.

### Exemplo

| Acao | Responsavel | Prazo | Indicador |
| --- | --- | --- | --- |
| Revisar permissoes da area Financeiro | Infra + dono da area | 30 dias | reducao de pastas com acesso amplo |
| Avaliar arquivos sem uso ha 365 dias | Governanca | 45 dias | volume frio reduzido |
| Revisar scripts fora da DTI | Seguranca | 15 dias | zero scripts fora da area tecnica sem justificativa |

## 9. Como esse relatorio deve nascer no produto

O relatorio QBR nativo do File Server Monitor deve combinar:

- inventario gerencial persistido por snapshot;
- atividade correlacionada persistida na timeline;
- anomalias operacionais;
- recomendacoes automativas calculadas pela API;
- comparativos entre ciclos salvos.

### Fontes internas do produto

- `inventario`: visao de capacidade, idade e composicao;
- `eventos correlacionados`: atividade real e rankings;
- `alertas`: anomalias e risco operacional;
- `baseline`: comparacao entre periodos.

## 10. Versao minima recomendada

Uma primeira versao do QBR nativo do produto pode sair mesmo antes de classificacao sensivel, desde que contenha:

- crescimento do ambiente;
- top areas por volume;
- top usuarios por atividade;
- top acoes;
- dados frios;
- acessos negados;
- anomalias relevantes;
- recomendacoes priorizadas.

## 11. Extensoes que agregam muito valor no futuro

Quando a base atual estiver consolidada, o QBR pode subir de patamar com:

- ACL e heranca quebrada;
- dono da area;
- score de higiene;
- dados sensiveis por extensao, nome ou motor dedicado;
- recomendacoes automaticas tratadas x pendentes;
- tendencia trimestral por compartilhamento;
- benchmark interno entre areas.
