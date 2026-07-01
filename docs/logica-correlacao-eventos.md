# Logica de Correlacao de Eventos

Este documento resume a logica atual de correlacao de eventos do FileServerMonitor, o que ja esta maduro e o que ainda falta consolidar.

## Objetivo

A correlacao transforma eventos brutos do USN Journal e do Log de Seguranca do Windows em uma linha do tempo mais legivel para auditoria.

O motor precisa lidar com eventos ruidosos, fora de ordem e parcialmente duplicados, principalmente quando o Windows ou o Office criam arquivos temporarios, renomeiam nomes padrao e depois gravam o arquivo final.

## Arquivos principais

- `/Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Core/EventCorrelation.cs`
- `/Users/andersonbandeira/Projetos/FileServer/tests/FileServerMonitor.Core.Tests/Program.cs`
- `/Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Web/src/event-timeline.ts`
- `/Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Web/src/event-correlation-rules.ts`
- `/Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Web/src/event-noise.ts`
- `/Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Web/src/event-timeline.test.ts`
- `/Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Web/src/event-correlation-rules.test.ts`
- `/Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Web/src/event-noise.test.ts`
- `/Users/andersonbandeira/Projetos/FileServer/scripts/windows/Run-FileServerMonitorTestScenario.ps1`
- `/Users/andersonbandeira/Projetos/FileServer/scripts/windows/Export-FileServerMonitorRawEvents.ps1`

## Fluxo atual

1. O agente coleta eventos do USN Journal e do Log de Seguranca do Windows.
2. O backend C# correlaciona eventos por caminho, janela de tempo, servidor, compartilhamento, volume e identificadores do arquivo.
3. O backend tenta enriquecer eventos do USN com usuario, SID, host, IP e processo vindos do Log de Seguranca.
4. A API entrega os eventos para o frontend.
5. O frontend ainda faz uma camada de limpeza de apresentacao em `event-timeline.ts`, removendo ruido visual, agrupando ecos e ajustando a linha do tempo.

## O que ja esta maduro

### Backend C#

O arquivo `/Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Core/EventCorrelation.cs` ja esta fazendo bem a parte estrutural da correlacao.

Pontos maduros:

- correlacao entre USN Journal e Log de Seguranca por caminho e janela de tempo;
- enriquecimento de eventos USN com usuario, SID, host, IP e processo do evento de seguranca correspondente;
- marcacao da origem correlacionada como `usn-journal+security-log`;
- agrupamento de pares de renomeacao do USN (`renamed_old` e `renamed_new`);
- inferencia de renomeacao usando o mesmo identificador de arquivo quando o USN gera eventos intermediarios;
- classificacao entre `renomeado` e `movido`;
- suporte a criacoes com nomes provisiorios do Windows e do Office;
- colapso de cadeias como nome padrao -> nome intermediario -> nome final;
- protecao contra correlacionar arquivos parecidos de execucoes diferentes usando identificadores, volume, servidor e compartilhamento;
- preservacao de acesso quando o Log de Seguranca confirma leitura, mesmo se o USN aparecer como alteracao.

Os testes de Core estavam passando com 21 cenarios cobertos em `/Users/andersonbandeira/Projetos/FileServer/tests/FileServerMonitor.Core.Tests/Program.cs`.

### Frontend TypeScript

O arquivo `/Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Web/src/main.tsx` foi simplificado para parar de carregar uma segunda copia grande da logica de correlacao.

A logica de apresentacao ficou concentrada em:

- `/Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Web/src/event-timeline.ts`
- `/Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Web/src/event-correlation-rules.ts`
- `/Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Web/src/event-noise.ts`

Pontos maduros nessa camada:

- deduplicacao de eventos para exibicao;
- remocao de ruido operacional e transitorio;
- supressao de ecos `changed`/`modified` quando ja existe evento mais expressivo;
- resolucao visual de usuario `UNKNOWN` quando ha evento correlato com usuario conhecido;
- normalizacao de criacoes vindas de nomes padrao do Windows e Office;
- sintese de criacao quando a linha do tempo precisa mostrar a origem de um renomeio normal;
- preservacao de eventos de acesso;
- preservacao da delecao de pasta, como `E:\Corporativo\RH`;
- testes automatizados para a timeline e regras de correlacao.

Os testes de frontend estavam passando com 15 cenarios cobrindo regras de timeline e correlacao.

## O que faltava e foi ajustado

### Duplicacao no `main.tsx`

Havia uma copia antiga de logica de correlacao no fim de `/Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Web/src/main.tsx`.

Isso criava o problema de "dois cerebros": uma regra podia estar correta em `event-timeline.ts`, mas diferente ou obsoleta dentro do proprio componente React.

Estado atual: essa duplicacao foi removida. O `main.tsx` ficou responsavel por buscar dados e renderizar a tela.

### Delecao da pasta `RH`

A delecao da pasta estava sendo tratada como ruido de pasta ou como evento raiz, e por isso sumia da timeline.

Estado atual: a delecao da pasta deve aparecer como evento `Excluido` para `E:\Corporativo\RH`.

### Criacao do arquivo de origem em renomeacao normal

O arquivo `E:\Corporativo\teste-rename-origem.txt` nao aparecia como criacao quando depois era renomeado para `teste-renomeado-final.txt`.

Estado atual: a timeline sintetiza a criacao da origem em renomeacoes normais, sem confundir isso com arquivos provisiorios do Windows ou Office.

### Evento de acesso

O acesso ao arquivo podia aparecer como `Alterado` porque o USN Journal registrava mudanca no arquivo e esse evento vencida a interpretacao do Log de Seguranca.

Estado atual: o Core preserva `accessed` quando o Log de Seguranca confirma leitura. O script de teste tambem passou a ler bytes do arquivo para forcar auditoria real.

### Renomeacoes normais de `.bmp` e `.pptx`

As criacoes usando nomes padrao do Windows/Office continuam sendo colapsadas de proposito para reduzir ruido.

Para validar renomeacoes normais sem desfazer essa limpeza, o roteiro passou a incluir casos explicitos com nomes nao provisiorios, como:

- `teste-bmp-rename-origem.bmp` -> `teste-bmp-renomeado-final.bmp`
- `teste-pptx-rename-origem.pptx` -> `teste-pptx-renomeado-final.pptx`

## O que ainda falta consolidar

### Migrar heuristicas do frontend para o Core

O caminho ideal e deixar o backend entregar uma timeline pronta para consumo por tela, CSV e relatorios.

Ainda devem migrar gradualmente de TypeScript para C#:

- sintese de criacao;
- supressao final de ruido visual;
- resolucao de usuario `UNKNOWN`;
- supressao de ecos entre arquivo e pasta;
- contrato final de evento pronto para timeline.

Essa migracao deve ser feita em fatias pequenas, com teste antes e depois, para evitar voltar ao problema de "dois cerebros".

### Definir comportamento de nomes provisiorios

Hoje a timeline limpa nomes padrao como `Novo Documento`, `Nova Imagem de Bitmap` e equivalentes do Office, mostrando preferencialmente o arquivo final.

Isso e bom para uma timeline operacional.

Mas se o produto precisar de auditoria forense completa, sera necessario ter duas visoes:

- timeline limpa, para leitura humana;
- eventos brutos, para investigacao detalhada.

### Fortalecer fixtures pequenos

O fixture grande ajuda a reproduzir o mundo real, mas consome muita atencao e dificulta manutencao.

Recomendacao:

- manter fixtures pequenos para renomeacao normal;
- manter fixtures pequenos para Office/Windows com nome provisiorio;
- manter fixture pequeno para acesso;
- manter fixture pequeno para delecao de pasta;
- usar exportacoes grandes apenas como evidencia de integracao.

### Validar auditoria de acesso no Windows

Eventos de acesso dependem da configuracao de auditoria/SACL no Windows.

Mesmo com a aplicacao correta, se o Windows nao estiver auditando leitura, ou se o script apenas abrir e fechar sem ler conteudo, o evento pode nao aparecer como esperado.

## Criterio de aceite atual

Para considerar a correlacao saudavel no roteiro de teste atual, a timeline deve mostrar:

- criacao de `Novo Documento de Texto.txt`;
- criacao de `Novo Documento.txt`;
- acesso a `Novo Documento.txt`;
- criacao de `teste-txt-01.txt`;
- criacao de `teste-rename-origem.txt`;
- renomeacao de `teste-rename-origem.txt` para `teste-renomeado-final.txt`;
- criacao/renomeacao normal dos casos `.bmp` e `.pptx` de origem explicita;
- criacoes finais limpas para casos com nomes padrao do Windows/Office;
- movimentos para a pasta `RH`;
- delecao dos arquivos criados;
- delecao da pasta `RH`.

## Comandos de validacao

Core:

```bash
dotnet run --project /Users/andersonbandeira/Projetos/FileServer/tests/FileServerMonitor.Core.Tests/FileServerMonitor.Core.Tests.csproj
```

Frontend:

```bash
cd /Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Web
npm test
npm run build
```

Roteiro Windows:

```powershell
E:\caminho\para\Run-FileServerMonitorTestScenario.ps1
```

O caminho acima deve ser ajustado para o local onde o script estiver no Windows. No repositorio, o script fica em:

`/Users/andersonbandeira/Projetos/FileServer/scripts/windows/Run-FileServerMonitorTestScenario.ps1`

## Recomendacao para o proximo agente

Nao substituir o `EventCorrelation.cs` inteiro de uma vez.

O melhor caminho e:

1. manter os testes atuais verdes;
2. escolher uma heuristica pequena do `event-timeline.ts`;
3. escrever ou mover o teste equivalente para o Core;
4. implementar em C#;
5. remover a regra correspondente do frontend;
6. validar com o roteiro real no Windows.

Assim a aplicacao sai gradualmente do modelo com dois cerebros e chega a uma arquitetura em que o frontend apenas renderiza eventos ja correlacionados pelo Core.
