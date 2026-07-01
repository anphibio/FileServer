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

test("does not let a near creation access echo replace the creation", () => {
  const path = "C:\\Corporativo\\codex-create-action\\01-single-file.txt";
  const display = buildDisplayEvents([
    buildEvent({
      id: "created",
      timestampUtc: "2026-07-01T02:04:50.000Z",
      path,
      action: "created",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "accessed-echo",
      timestampUtc: "2026-07-01T02:04:50.640Z",
      path,
      action: "accessed",
      source: "windows-security-log"
    })
  ]);

  assert.deepEqual(display.map((event) => event.displayAction), ["Criação"]);
});

test("suppresses access and mojibake security creation echoes near a usn creation", () => {
  const correctPath = "C:\\Corporativo\\Anderson Fábio Costa Bandeira - Sobreaviso XX-2026.xlsx";
  const mojibakePath = "C:\\Corporativo\\Anderson F�bio Costa Bandeira - Sobreaviso XX-2026.xlsx";
  const display = buildDisplayEvents([
    buildEvent({
      id: "accessed-echo",
      timestampUtc: "2026-07-01T02:12:03.923Z",
      path: mojibakePath,
      action: "accessed",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "security-created-echo",
      timestampUtc: "2026-07-01T02:12:02.903Z",
      path: mojibakePath,
      action: "created_or_appended",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "usn-created",
      timestampUtc: "2026-07-01T02:12:02.000Z",
      path: correctPath,
      action: "created",
      source: "usn-journal+security-log"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.path, event.displayAction]), [[correctPath, "Criação"]]);
});

test("promotes a security modified event to creation when it is part of a creation batch", () => {
  const targetPath = "C:\\Corporativo\\codex-client-check-02.md";
  const siblingPath = "C:\\Corporativo\\codex-client-check-01.txt";
  const display = buildDisplayEvents([
    buildEvent({
      id: "target-accessed-later",
      timestampUtc: "2026-07-01T02:20:13.003Z",
      path: targetPath,
      action: "accessed",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "target-accessed",
      timestampUtc: "2026-07-01T02:20:11.997Z",
      path: targetPath,
      action: "accessed",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "target-modified",
      timestampUtc: "2026-07-01T02:20:11.993Z",
      path: targetPath,
      action: "modified",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "sibling-created",
      timestampUtc: "2026-07-01T02:20:11.000Z",
      path: siblingPath,
      action: "created",
      source: "usn-journal"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.path, event.displayAction]).sort(), [
    [siblingPath, "Criação"],
    [targetPath, "Criação"]
  ].sort());
});

test("promotes a modified folder to creation when its children are created in the same batch", () => {
  const folderPath = "C:\\Corporativo\\teste";
  const childPath = "C:\\Corporativo\\teste\\Example txt file.txt";
  const display = buildDisplayEvents([
    buildEvent({
      id: "folder-accessed",
      timestampUtc: "2026-07-01T02:20:12.117Z",
      path: folderPath,
      objectType: "file",
      action: "accessed",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "folder-modified-usn",
      timestampUtc: "2026-07-01T02:20:12.000Z",
      path: folderPath,
      objectType: "folder",
      action: "modified",
      source: "usn-journal"
    }),
    buildEvent({
      id: "child-created",
      timestampUtc: "2026-07-01T02:20:11.940Z",
      path: childPath,
      action: "created_or_appended",
      source: "windows-security-log"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.path, event.displayAction]).sort(), [
    [childPath, "Criação"],
    [folderPath, "Criação"]
  ].sort());
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

test("collapses folder delete duplicates when security and usn timestamps differ within the same second", () => {
  const path = "C:\\Corporativo\\Example folder";
  const display = buildDisplayEvents([
    buildEvent({
      id: "example-folder-security-delete",
      timestampUtc: "2026-07-01T01:09:08.497Z",
      path,
      objectType: "file",
      action: "deleted",
      source: "windows-security-log",
      user: "FILESERVER\\AnphibiO"
    }),
    buildEvent({
      id: "example-folder-usn-delete",
      timestampUtc: "2026-07-01T01:09:08.000Z",
      path,
      objectType: "folder",
      action: "deleted",
      source: "usn-journal",
      user: "UNKNOWN"
    })
  ]);

  assert.deepEqual(display.map((event) => event.displayAction), ["Excluído"]);
  assert.equal(display[0]?.user, "FILESERVER\\AnphibiO");
});

test("keeps deletion of an empty Windows default-named folder", () => {
  const path = "C:\\Corporativo\\Nova pasta";
  const display = buildDisplayEvents([
    buildEvent({
      id: "nova-pasta-security-accessed",
      timestampUtc: "2026-07-01T01:29:06.723Z",
      path,
      objectType: "file",
      action: "accessed",
      source: "windows-security-log",
      user: "FILESERVER\\AnphibiO"
    }),
    buildEvent({
      id: "nova-pasta-security-delete",
      timestampUtc: "2026-07-01T01:29:06.810Z",
      path,
      objectType: "file",
      action: "deleted",
      source: "windows-security-log",
      user: "FILESERVER\\AnphibiO"
    }),
    buildEvent({
      id: "nova-pasta-usn-delete",
      timestampUtc: "2026-07-01T01:29:06.000Z",
      path,
      objectType: "folder",
      action: "deleted",
      source: "usn-journal+security-log",
      user: "FILESERVER\\AnphibiO"
    })
  ]);

  assert.deepEqual(display.map((event) => event.displayAction), ["Excluído"]);
  assert.equal(display[0]?.path, path);
});

test("keeps folder creation when files are created inside it in the same batch", () => {
  const folder = "C:\\Corporativo\\codex-create-action\\02-many-files";
  const display = buildDisplayEvents([
    buildEvent({
      id: "folder-created",
      timestampUtc: "2026-07-01T01:44:29.000Z",
      path: folder,
      objectType: "folder",
      action: "created",
      source: "usn-journal+security-log",
      user: "FILESERVER\\Administrator"
    }),
    buildEvent({
      id: "child-created",
      timestampUtc: "2026-07-01T01:44:29.000Z",
      path: `${folder}\\a.txt`,
      objectType: "file",
      action: "created",
      source: "usn-journal+security-log",
      user: "FILESERVER\\Administrator"
    })
  ]);

  assert.deepEqual(
    display.map((event) => [event.path, event.displayAction]).sort(),
    [
      [`${folder}\\a.txt`, "Criação"],
      [folder, "Criação"]
    ].sort()
  );
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
