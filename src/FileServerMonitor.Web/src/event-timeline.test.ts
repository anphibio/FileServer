import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

import { buildDisplayEvents, type FileAuditEvent } from "./event-timeline.ts";

function buildEvent(overrides: Partial<FileAuditEvent> & Pick<FileAuditEvent, "id" | "timestampUtc" | "path" | "action" | "source">): FileAuditEvent {
  return {
    server: "FS01",
    share: "Corporativo",
    objectType: overrides.path.includes(".") ? "file" : "directory",
    user: "TCE-AL\\anderson.bandeira",
    result: "success",
    severity: "info",
    previousPath: null,
    ...overrides
  };
}

test("promotes direct final-name creations in the full timeline pipeline", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "txt-changed",
      timestampUtc: "2026-05-24T22:54:35.000Z",
      path: "E:\\Corporativo\\teste-txt-01.txt",
      action: "modified",
      source: "usn-journal"
    }),
    buildEvent({
      id: "bmp-created",
      timestampUtc: "2026-05-24T22:54:42.000Z",
      path: "E:\\Corporativo\\teste-bmp-01.bmp",
      action: "created",
      source: "usn-journal"
    }),
    buildEvent({
      id: "bmp-changed",
      timestampUtc: "2026-05-24T22:54:45.000Z",
      path: "E:\\Corporativo\\teste-bmp-02.bmp",
      action: "modified",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "ppt-changed",
      timestampUtc: "2026-05-24T22:54:48.000Z",
      path: "E:\\Corporativo\\teste-pptx-01.pptx",
      action: "modified",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "ppt-created",
      timestampUtc: "2026-05-24T22:54:54.000Z",
      path: "E:\\Corporativo\\teste-pptx-02.pptx",
      action: "created",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "xlsx-changed",
      timestampUtc: "2026-05-24T22:54:57.000Z",
      path: "E:\\Corporativo\\teste-xlsx-01.xlsx",
      action: "modified",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "txt-move",
      timestampUtc: "2026-05-24T22:55:03.000Z",
      path: "E:\\Corporativo\\RH\\teste-txt-01.txt",
      previousPath: "E:\\Corporativo\\teste-txt-01.txt",
      action: "moved",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "bmp-move",
      timestampUtc: "2026-05-24T22:55:06.000Z",
      path: "E:\\Corporativo\\RH\\teste-bmp-02.bmp",
      previousPath: "E:\\Corporativo\\teste-bmp-02.bmp",
      action: "moved",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "xlsx-move",
      timestampUtc: "2026-05-24T22:55:15.000Z",
      path: "E:\\Corporativo\\RH\\teste-xlsx-02.xlsx",
      previousPath: "E:\\Corporativo\\teste-xlsx-02.xlsx",
      action: "moved",
      source: "usn-journal"
    }),
    buildEvent({
      id: "bmp-delete",
      timestampUtc: "2026-05-24T22:55:18.000Z",
      path: "E:\\Corporativo\\teste-bmp-01.bmp",
      action: "deleted",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "ppt-delete-1",
      timestampUtc: "2026-05-24T22:55:18.000Z",
      path: "E:\\Corporativo\\teste-pptx-01.pptx",
      action: "deleted",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "ppt-delete-2",
      timestampUtc: "2026-05-24T22:55:18.000Z",
      path: "E:\\Corporativo\\teste-pptx-02.pptx",
      action: "deleted",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "xlsx-delete",
      timestampUtc: "2026-05-24T22:55:18.000Z",
      path: "E:\\Corporativo\\teste-xlsx-01.xlsx",
      action: "deleted",
      source: "windows-security-log"
    })
  ]);

  const findEvents = (path: string) => display.filter((event) => event.path === path);

  assert.deepEqual(findEvents("E:\\Corporativo\\teste-txt-01.txt").map((event) => event.displayAction), ["Criação"]);
  assert.deepEqual(findEvents("E:\\Corporativo\\teste-bmp-02.bmp").map((event) => event.displayAction), ["Criação"]);
  assert.deepEqual(findEvents("E:\\Corporativo\\teste-pptx-01.pptx").map((event) => event.displayAction), ["Excluído", "Criação"]);
  assert.deepEqual(findEvents("E:\\Corporativo\\teste-xlsx-01.xlsx").map((event) => event.displayAction), ["Excluído", "Criação"]);
});

test("keeps accessed events with the file name visible", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "accessed-1",
      timestampUtc: "2026-05-24T23:10:00.000Z",
      path: "E:\\Corporativo\\Novo Documento.txt",
      action: "accessed",
      source: "windows-security-log"
    })
  ]);

  assert.equal(display.length, 1);
  assert.equal(display[0]?.displayAction, "Acessado");
  assert.equal(display[0]?.displayTarget, "Novo Documento.txt");
});

test("keeps a folder deletion when recursive cleanup also deletes its children", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "child-delete",
      timestampUtc: "2026-05-24T23:10:00.000Z",
      path: "E:\\Corporativo\\RH\\teste-txt-01.txt",
      action: "deleted",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "folder-delete",
      timestampUtc: "2026-05-24T23:10:00.100Z",
      path: "E:\\Corporativo\\RH",
      objectType: "directory",
      action: "deleted",
      source: "windows-security-log"
    })
  ]);

  assert.equal(display.some((event) => event.path === "E:\\Corporativo\\RH" && event.displayAction === "Excluído"), true);
});

test("collapses duplicate security and usn delete events for the same path", () => {
  const path = "E:\\Corporativo\\delete-suite\\arquivo-a.txt";
  const display = buildDisplayEvents([
    buildEvent({
      id: "delete-security",
      timestampUtc: "2026-07-01T00:48:31.453Z",
      path,
      action: "deleted",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "delete-correlated",
      timestampUtc: "2026-07-01T00:48:31.453Z",
      path,
      action: "deleted",
      source: "usn-journal+security-log"
    })
  ]);

  assert.deepEqual(display.map((event) => event.displayAction), ["Excluído"]);
  assert.equal(display[0]?.source, "usn-journal+security-log");
});

test("keeps initial creation before a normal rename", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "rename-source-created",
      timestampUtc: "2026-05-24T23:10:00.000Z",
      path: "E:\\Corporativo\\teste-rename-origem.txt",
      action: "created",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "rename-final",
      timestampUtc: "2026-05-24T23:10:03.000Z",
      path: "E:\\Corporativo\\teste-renomeado-final.txt",
      previousPath: "E:\\Corporativo\\teste-rename-origem.txt",
      action: "renamed",
      source: "usn-journal+security-log"
    })
  ]);

  assert.deepEqual(display.map((event) => event.displayAction), ["Renomeado", "Criação"]);
});

test("synthesizes initial creation from a normal rename previous path", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "rename-final",
      timestampUtc: "2026-05-24T23:10:03.000Z",
      path: "E:\\Corporativo\\teste-renomeado-final.txt",
      previousPath: "E:\\Corporativo\\teste-rename-origem.txt",
      action: "renamed",
      source: "usn-journal+security-log"
    })
  ]);

  assert.deepEqual(display.map((event) => event.displayAction), ["Renomeado", "Criação"]);
  assert.equal(display[1]?.path, "E:\\Corporativo\\teste-rename-origem.txt");
});

test("keeps predictable scenario lifecycle without permission or intermediate rename noise", () => {
  const base = "E:\\Corporativo\\Cenario";
  const copied = `${base}\\03-Movimentacao\\arquivo-copiado.txt`;
  const deletePath = `${base}\\02-Alteracoes\\arquivo-excluir.txt`;
  const origin = `${base}\\02-Alteracoes\\arquivo-base.txt`;
  const renamed = `${base}\\02-Alteracoes\\arquivo-renomeado.txt`;
  const moved = `${base}\\03-Movimentacao\\arquivo-movido.txt`;

  const display = buildDisplayEvents([
    buildEvent({
      id: "permission-security-1",
      timestampUtc: "2026-06-30T23:35:30.120Z",
      path: copied,
      action: "permission_changed",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "permission-correlated",
      timestampUtc: "2026-06-30T23:35:30.000Z",
      path: copied,
      action: "permission_changed",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "permission-security-2",
      timestampUtc: "2026-06-30T23:35:29.920Z",
      path: copied,
      action: "permission_changed",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "delete-created",
      timestampUtc: "2026-06-30T23:35:29.000Z",
      path: deletePath,
      action: "created",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "delete-final",
      timestampUtc: "2026-06-30T23:35:30.000Z",
      path: deletePath,
      action: "deleted",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "origin-created",
      timestampUtc: "2026-06-30T23:35:29.000Z",
      path: origin,
      action: "created",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "renamed",
      timestampUtc: "2026-06-30T23:35:29.000Z",
      path: renamed,
      previousPath: origin,
      action: "renamed",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "moved",
      timestampUtc: "2026-06-30T23:35:29.000Z",
      path: moved,
      previousPath: renamed,
      action: "moved",
      source: "usn-journal+security-log"
    })
  ]);

  const actionsByPath = new Map<string, string[]>();
  for (const event of display) {
    actionsByPath.set(event.path, [...(actionsByPath.get(event.path) ?? []), event.displayAction ?? event.action]);
  }

  assert.deepEqual(actionsByPath.get(copied), ["Permissão alterada"]);
  assert.deepEqual(actionsByPath.get(deletePath), ["Excluído", "Criação"]);
  assert.deepEqual(actionsByPath.get(renamed), ["Renomeado"]);
  assert.deepEqual(actionsByPath.get(moved), ["Movido"]);
});

test("keeps a provisional file that was not renamed even when another sibling file appears later", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "default-txt",
      timestampUtc: "2026-05-24T22:54:30.000Z",
      path: "E:\\Corporativo\\Novo Documento de Texto.txt",
      action: "created",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "named-txt",
      timestampUtc: "2026-05-24T22:54:35.000Z",
      path: "E:\\Corporativo\\teste-txt-01.txt",
      action: "modified",
      source: "usn-journal"
    }),
    buildEvent({
      id: "named-txt-delete",
      timestampUtc: "2026-05-24T22:55:18.000Z",
      path: "E:\\Corporativo\\teste-txt-01.txt",
      action: "deleted",
      source: "windows-security-log"
    })
  ]);

  const byPath = new Map(display.map((event) => [event.path, event]));

  assert.equal(byPath.get("E:\\Corporativo\\Novo Documento de Texto.txt")?.displayAction, "Criação");
  assert.equal(byPath.get("E:\\Corporativo\\teste-txt-01.txt")?.displayAction, "Criação");
});

test("matches the scripted scenario without leaving altered echoes for promoted creations", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "default-txt",
      timestampUtc: "2026-05-24T22:54:30.000Z",
      path: "E:\\Corporativo\\Novo Documento de Texto.txt",
      action: "created",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "named-txt",
      timestampUtc: "2026-05-24T22:54:35.000Z",
      path: "E:\\Corporativo\\teste-txt-01.txt",
      action: "modified",
      source: "usn-journal"
    }),
    buildEvent({
      id: "bmp-01-created",
      timestampUtc: "2026-05-24T22:54:42.000Z",
      path: "E:\\Corporativo\\teste-bmp-01.bmp",
      action: "created",
      source: "usn-journal"
    }),
    buildEvent({
      id: "bmp-02-changed",
      timestampUtc: "2026-05-24T22:54:45.000Z",
      path: "E:\\Corporativo\\teste-bmp-02.bmp",
      action: "modified",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "pptx-01-changed",
      timestampUtc: "2026-05-24T22:54:48.000Z",
      path: "E:\\Corporativo\\teste-pptx-01.pptx",
      action: "modified",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "pptx-02-created",
      timestampUtc: "2026-05-24T22:54:54.000Z",
      path: "E:\\Corporativo\\teste-pptx-02.pptx",
      action: "created",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "xlsx-01-changed",
      timestampUtc: "2026-05-24T22:54:57.000Z",
      path: "E:\\Corporativo\\teste-xlsx-01.xlsx",
      action: "modified",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "rh-created",
      timestampUtc: "2026-05-24T22:55:00.000Z",
      path: "E:\\Corporativo\\RH",
      action: "created",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "txt-move",
      timestampUtc: "2026-05-24T22:55:03.000Z",
      path: "E:\\Corporativo\\RH\\teste-txt-01.txt",
      previousPath: "E:\\Corporativo\\teste-txt-01.txt",
      action: "moved",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "bmp-move",
      timestampUtc: "2026-05-24T22:55:06.000Z",
      path: "E:\\Corporativo\\RH\\teste-bmp-02.bmp",
      previousPath: "E:\\Corporativo\\teste-bmp-02.bmp",
      action: "moved",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "xlsx-02-created",
      timestampUtc: "2026-05-24T22:55:12.000Z",
      path: "E:\\Corporativo\\teste-xlsx-02.xlsx",
      action: "created",
      source: "usn-journal"
    }),
    buildEvent({
      id: "xlsx-move",
      timestampUtc: "2026-05-24T22:55:15.000Z",
      path: "E:\\Corporativo\\RH\\teste-xlsx-02.xlsx",
      previousPath: "E:\\Corporativo\\teste-xlsx-02.xlsx",
      action: "moved",
      source: "usn-journal"
    }),
    buildEvent({
      id: "txt-delete",
      timestampUtc: "2026-05-24T22:55:18.000Z",
      path: "E:\\Corporativo\\RH\\teste-txt-01.txt",
      action: "deleted",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "bmp-01-delete",
      timestampUtc: "2026-05-24T22:55:18.000Z",
      path: "E:\\Corporativo\\teste-bmp-01.bmp",
      action: "deleted",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "pptx-01-delete",
      timestampUtc: "2026-05-24T22:55:18.000Z",
      path: "E:\\Corporativo\\teste-pptx-01.pptx",
      action: "deleted",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "pptx-02-delete",
      timestampUtc: "2026-05-24T22:55:18.000Z",
      path: "E:\\Corporativo\\teste-pptx-02.pptx",
      action: "deleted",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "xlsx-01-delete",
      timestampUtc: "2026-05-24T22:55:18.000Z",
      path: "E:\\Corporativo\\teste-xlsx-01.xlsx",
      action: "deleted",
      source: "windows-security-log"
    })
  ]);

  const historyFor = (path: string) =>
    display.filter((event) => event.path === path).map((event) => event.displayAction);

  assert.deepEqual(historyFor("E:\\Corporativo\\teste-txt-01.txt"), ["Criação"]);
  assert.deepEqual(historyFor("E:\\Corporativo\\teste-bmp-01.bmp"), ["Excluído", "Criação"]);
  assert.deepEqual(historyFor("E:\\Corporativo\\teste-bmp-02.bmp"), ["Criação"]);
  assert.deepEqual(historyFor("E:\\Corporativo\\teste-pptx-01.pptx"), ["Excluído", "Criação"]);
  assert.deepEqual(historyFor("E:\\Corporativo\\teste-pptx-02.pptx"), ["Excluído", "Criação"]);
  assert.deepEqual(historyFor("E:\\Corporativo\\teste-xlsx-01.xlsx"), ["Excluído", "Criação"]);
  assert.ok(display.every((event) => event.path !== "E:\\Corporativo\\teste-txt-01.txt" || event.displayAction !== "Alterado"));
  assert.ok(display.every((event) => event.path !== "E:\\Corporativo\\teste-bmp-02.bmp" || event.displayAction !== "Alterado"));
  assert.ok(display.every((event) => event.path !== "E:\\Corporativo\\teste-pptx-01.pptx" || event.displayAction !== "Alterado"));
  assert.ok(display.every((event) => event.path !== "E:\\Corporativo\\teste-xlsx-01.xlsx" || event.displayAction !== "Alterado"));
});

test("correlates the real exported scenario without surfacing provisional office noise", () => {
  const fixturePath = resolve(process.cwd(), "src/FileServerMonitor.Web/src/test-fixtures/raw-events-final.json");
  const exported = JSON.parse(readFileSync(fixturePath, "utf8")) as { events: FileAuditEvent[] };
  const display = buildDisplayEvents(exported.events);

  const actionsFor = (path: string) =>
    display
      .filter((event) => event.path === path)
      .map((event) => event.displayAction);

  assert.deepEqual(actionsFor("E:\\Corporativo\\Novo Documento de Texto.txt"), ["Excluído", "Criação"]);
  assert.deepEqual(actionsFor("E:\\Corporativo\\teste-txt-01.txt"), ["Criação"]);
  assert.deepEqual(actionsFor("E:\\Corporativo\\teste-pptx-01.pptx"), ["Excluído", "Criação"]);
  assert.deepEqual(actionsFor("E:\\Corporativo\\teste-xlsx-01.xlsx"), ["Excluído", "Criação"]);

  assert.ok(display.every((event) => event.path !== "E:\\Corporativo\\Novo(a) Apresentacao do Microsoft PowerPoint.pptx"));
  assert.ok(display.every((event) => event.path !== "E:\\Corporativo\\Novo(a) Planilha do Microsoft Excel.xlsx"));

  assert.ok(display.every((event) => event.path !== "E:\\Corporativo\\Novo Documento de Texto.txt" || event.displayAction !== "Alterado"));
  assert.ok(display.every((event) => event.path !== "E:\\Corporativo\\teste-txt-01.txt" || event.displayAction !== "Alterado"));
  assert.ok(display.every((event) => event.path !== "E:\\Corporativo\\teste-pptx-01.pptx" || event.displayAction !== "Alterado"));
  assert.ok(display.every((event) => event.path !== "E:\\Corporativo\\teste-xlsx-01.xlsx" || event.displayAction !== "Alterado"));
});
