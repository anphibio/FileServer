# Relatorios guiados e personalizados

## Objetivo

Adicionar uma area de relatorios que ajude o operador a investigar primeiro na tela e, quando o recorte estiver correto, gerar ou exportar o relatorio com os mesmos filtros.

## Decisao de produto

- A tela de relatorios deve ser operacional, direta e orientada a investigacao.
- Os relatorios guiados entram como atalhos para cenarios comuns.
- O relatorio personalizado permite montar o recorte livremente.
- O mesmo recorte deve alimentar:
  - investigacao em tela;
  - exportacao CSV;
  - pre-visualizacao do relatorio.

## Relatorios guiados aprovados

- Atividade por pasta.
- Atividade por usuario.
- Atividade por servidor.
- Leitura sem alteracao.
- Exclusao em massa.
- Acesso negado recorrente.
- Mudancas de permissao.
- Renomeacao em massa.
- Criacao de executaveis.
- Atividade por host de origem.
- Acesso remoto suspeito.

## Decisoes tecnicas

- A correlacao de eventos continua centralizada no Core.
- A API expoe os eventos projetados pela timeline para a tela de relatorios.
- O frontend nao recria heuristica de correlacao; ele apenas monta filtros, exibe agregados e chama os endpoints.
- A exportacao de relatorio deve usar endpoint proprio com os eventos ja projetados, evitando divergencia entre tela e CSV.
- O primeiro formato de relatorio sera texto/HTML imprimivel pelo navegador, permitindo salvar em PDF sem introduzir uma dependencia nova de geracao de PDF no backend.

## Campos de filtro necessarios

- periodo rapido ou intervalo manual;
- servidor;
- compartilhamento;
- usuario;
- caminho;
- acao;
- host de origem;
- IP de origem;
- extensao;
- resultado;
- severidade;
- agrupamento principal.

## Criterios de aceite

- Existe uma aba "Relatorios" no menu principal.
- O usuario consegue escolher um cenario guiado e consultar a linha do tempo do recorte.
- O usuario consegue alternar para relatorio personalizado.
- Os filtros aplicados aparecem em resumo claro.
- O usuario consegue exportar CSV do mesmo recorte.
- O usuario consegue gerar uma pre-visualizacao textual do relatorio com resumo e eventos.
- A API suporta filtros usados pelos relatorios sem quebrar os endpoints existentes.
- Build do backend e frontend continua verde.
