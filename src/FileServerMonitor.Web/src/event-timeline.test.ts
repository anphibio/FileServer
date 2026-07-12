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

function getTestParentPath(path: string) {
  return path.split("\\").slice(0, -1).join("\\");
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

test("collapses repeated access noise for the same file within a few seconds", () => {
  const path = "C:\\Corporativo\\Novo(a) Documento de Texto - Copia (4).txt";
  const display = buildDisplayEvents([
    buildEvent({
      id: "accessed-1",
      timestampUtc: "2026-07-02T06:44:05.000Z",
      path,
      action: "accessed",
      source: "windows-security-log",
      user: "FILESERVER\\AnphibiO"
    }),
    buildEvent({
      id: "accessed-2",
      timestampUtc: "2026-07-02T06:44:06.000Z",
      path,
      action: "accessed",
      source: "windows-security-log",
      user: "FILESERVER\\AnphibiO"
    }),
    buildEvent({
      id: "accessed-3",
      timestampUtc: "2026-07-02T06:44:07.000Z",
      path,
      action: "accessed",
      source: "windows-security-log",
      user: "FILESERVER\\AnphibiO"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.displayAction, event.path, event.timestampUtc]), [
    ["Acessado", path, "2026-07-02T06:44:07.000Z"]
  ]);
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

test("keeps a real access several seconds after creation", () => {
  const path = "C:\\Corporativo\\codex-access-action\\access-root.txt";
  const display = buildDisplayEvents([
    buildEvent({
      id: "created",
      timestampUtc: "2026-07-02T02:52:12.000Z",
      path,
      action: "created",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "accessed",
      timestampUtc: "2026-07-02T02:52:20.000Z",
      path,
      action: "accessed",
      source: "windows-security-log"
    })
  ]);

  assert.deepEqual(display.map((event) => event.displayAction), ["Acessado", "Criação"]);
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

test("reconstructs a security-log-only folder rename with a default nested folder name", () => {
  const parentPath = "C:\\Corporativo\\Nova pasta";
  const previousPath = `${parentPath}\\Nova pasta`;
  const nextPath = `${parentPath}\\Nova pasta - 10`;
  const display = buildDisplayEvents([
    buildEvent({
      id: "target-accessed-1",
      timestampUtc: "2026-07-01T23:01:16.153Z",
      path: nextPath,
      action: "accessed",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "target-accessed-2",
      timestampUtc: "2026-07-01T23:01:16.140Z",
      path: nextPath,
      action: "accessed",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "parent-touch",
      timestampUtc: "2026-07-01T23:01:16.140Z",
      path: parentPath,
      action: "created_or_appended",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "source-deleted",
      timestampUtc: "2026-07-01T23:01:16.140Z",
      path: previousPath,
      action: "deleted",
      source: "windows-security-log"
    })
  ]);

  assert.deepEqual(display.map((event) => ({
    action: event.displayAction,
    path: event.path,
    previousPath: event.previousPath
  })), [{
    action: "Renomeado",
    path: nextPath,
    previousPath
  }]);
});

test("keeps default Excel worksheet creation despite internal Office temp renames", () => {
  const finalPath = "C:\\Corporativo\\Novo(a) Planilha do Microsoft Excel.xlsx";
  const tempOriginPath = "C:\\Corporativo\\~ovo(a) Planilha do Microsoft Excel.tmp";
  const tempBackupPath = "C:\\Corporativo\\Novo(a) Planilha do Microsoft Excel.xlsx~RF28b07d27.TMP";
  const display = buildDisplayEvents([
    buildEvent({
      id: "final-created",
      timestampUtc: "2026-07-02T01:41:31.000Z",
      path: finalPath,
      action: "created",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "temp-to-final",
      timestampUtc: "2026-07-02T01:41:31.000Z",
      path: finalPath,
      previousPath: tempOriginPath,
      action: "renamed",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "final-to-temp-backup",
      timestampUtc: "2026-07-02T01:41:31.000Z",
      path: tempBackupPath,
      previousPath: finalPath,
      action: "renamed",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "temp-backup-deleted",
      timestampUtc: "2026-07-02T01:41:31.000Z",
      path: tempBackupPath,
      action: "deleted",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "accessed-echo",
      timestampUtc: "2026-07-02T01:41:32.060Z",
      path: finalPath,
      action: "accessed",
      source: "windows-security-log"
    })
  ]);

  assert.deepEqual(display.map((event) => ({
    action: event.displayAction,
    path: event.path,
    previousPath: event.previousPath
  })), [{
    action: "Criação",
    path: finalPath,
    previousPath: null
  }]);
});

test("keeps explicit delete before recreating a default Excel worksheet", () => {
  const finalPath = "C:\\Corporativo\\Novo(a) Planilha do Microsoft Excel.xlsx";
  const tempOriginPath = "C:\\Corporativo\\~ovo(a) Planilha do Microsoft Excel.tmp";
  const tempBackupPath = "C:\\Corporativo\\Novo(a) Planilha do Microsoft Excel.xlsx~RF28cb7cc6.TMP";
  const display = buildDisplayEvents([
    buildEvent({
      id: "final-deleted",
      timestampUtc: "2026-07-02T02:10:54.000Z",
      path: finalPath,
      action: "deleted",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "final-created",
      timestampUtc: "2026-07-02T02:11:00.000Z",
      path: finalPath,
      action: "created",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "temp-to-final",
      timestampUtc: "2026-07-02T02:11:00.000Z",
      path: finalPath,
      previousPath: tempOriginPath,
      action: "renamed",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "final-to-temp-backup",
      timestampUtc: "2026-07-02T02:11:00.000Z",
      path: tempBackupPath,
      previousPath: finalPath,
      action: "renamed",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "temp-backup-deleted",
      timestampUtc: "2026-07-02T02:11:00.000Z",
      path: tempBackupPath,
      action: "deleted",
      source: "usn-journal+security-log"
    })
  ]);

  assert.deepEqual(display.map((event) => ({
    action: event.displayAction,
    path: event.path,
    previousPath: event.previousPath
  })), [
    {
      action: "Criação",
      path: finalPath,
      previousPath: null
    },
    {
      action: "Excluído",
      path: finalPath,
      previousPath: null
    }
  ]);
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

test("keeps a real modification before a later access echo", () => {
  const targetPath = "C:\\Corporativo\\RH\\Novo(a) Documento de Texto - Copia (3).txt";
  const siblingPath = "C:\\Corporativo\\RH\\codex-created-nearby.txt";
  const display = buildDisplayEvents([
    buildEvent({
      id: "target-modified",
      timestampUtc: "2026-07-02T10:40:47.000Z",
      path: targetPath,
      action: "modified",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "sibling-created",
      timestampUtc: "2026-07-02T10:40:48.000Z",
      path: siblingPath,
      action: "created",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "target-accessed",
      timestampUtc: "2026-07-02T10:40:55.000Z",
      path: targetPath,
      action: "accessed",
      source: "usn-journal+security-log"
    })
  ]);

  assert.deepEqual(display.filter((event) => event.path === targetPath).map((event) => event.displayAction), ["Alterado"]);
});

test("treats security log text append creation as modification", () => {
  const targetPath = "C:\\Corporativo\\RH\\Novo(a) Documento de Texto - Copia (2).txt";
  const display = buildDisplayEvents([
    buildEvent({
      id: "target-created",
      timestampUtc: "2026-07-02T10:50:12.000Z",
      path: targetPath,
      action: "created",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "target-accessed",
      timestampUtc: "2026-07-02T10:50:18.000Z",
      path: targetPath,
      action: "accessed",
      source: "usn-journal+security-log"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.path, event.displayAction]), [[targetPath, "Alterado"]]);
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

test("synthesizes descendant deletions when Windows only reports recursive folder deletes", () => {
  const paths = [
    "C:\\Corporativo\\Anderson Fábio Costa Bandeira - Sobreaviso XX-2026.xlsx",
    "C:\\Corporativo\\codex-client-check-01.txt",
    "C:\\Corporativo\\codex-client-check-02.md",
    "C:\\Corporativo\\codex-client-check-03.yml",
    "C:\\Corporativo\\Modelo Relatório Tecnico.docx",
    "C:\\Corporativo\\Novo(a) Planilha do Microsoft Excel.xlsx",
    "C:\\Corporativo\\Example folder",
    "C:\\Corporativo\\Example folder\\Another example txt file.txt",
    "C:\\Corporativo\\Example folder\\Example txt file.txt",
    "C:\\Corporativo\\Nova pasta",
    "C:\\Corporativo\\teste",
    "C:\\Corporativo\\teste\\Another example txt file.txt",
    "C:\\Corporativo\\teste\\Atesto_Datacom_XXX-2026.docx",
    "C:\\Corporativo\\teste\\Atesto_Wenet_XXX2025.docx",
    "C:\\Corporativo\\teste\\Example txt file.txt"
  ];
  const rootFiles = paths.filter((path) => getTestParentPath(path) === "C:\\Corporativo" && path.includes("."));
  const deletedFolders = [
    "C:\\Corporativo\\Example folder",
    "C:\\Corporativo\\Nova pasta",
    "C:\\Corporativo\\teste"
  ];
  const display = buildDisplayEvents([
    ...paths.map((path, index) => buildEvent({
      id: `created-${index}`,
      timestampUtc: "2026-07-01T02:20:12.000Z",
      path,
      objectType: path.includes(".") ? "file" : "folder",
      action: "created",
      source: "usn-journal"
    })),
    ...rootFiles.map((path, index) => buildEvent({
      id: `root-file-deleted-${index}`,
      timestampUtc: "2026-07-01T02:31:32.000Z",
      path,
      objectType: "file",
      action: "deleted",
      source: "usn-journal"
    })),
    ...deletedFolders.map((path, index) => buildEvent({
      id: `folder-deleted-${index}`,
      timestampUtc: "2026-07-01T02:31:32.000Z",
      path,
      objectType: "folder",
      action: "deleted",
      source: "usn-journal+security-log"
    }))
  ]);

  const deleted = display
    .filter((event) => event.displayAction === "Excluído")
    .map((event) => event.path)
    .sort();

  assert.deepEqual(deleted, paths.sort());
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

test("suppresses access echoes immediately after a normal rename", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "rename-access-later",
      timestampUtc: "2026-07-01T03:37:06.887Z",
      path: "C:\\Corporativo\\codex-client-check-010.txt",
      action: "accessed",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "rename-access",
      timestampUtc: "2026-07-01T03:37:05.873Z",
      path: "C:\\Corporativo\\codex-client-check-010.txt",
      action: "accessed",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "rename-final",
      timestampUtc: "2026-07-01T03:37:05.000Z",
      path: "C:\\Corporativo\\codex-client-check-010.txt",
      previousPath: "C:\\Corporativo\\codex-client-check-01.txt",
      action: "renamed",
      source: "usn-journal+security-log"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.path, event.displayAction]), [
    ["C:\\Corporativo\\codex-client-check-010.txt", "Renomeado"],
    ["C:\\Corporativo\\codex-client-check-01.txt", "Criação"]
  ]);
});

test("suppresses folder navigation echoes shortly before descendant renames", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "folder-access-kept",
      timestampUtc: "2026-07-02T02:44:00.763Z",
      path: "C:\\Corporativo\\Nova pasta",
      action: "accessed",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "folder-access-echo",
      timestampUtc: "2026-07-02T02:44:05.690Z",
      path: "C:\\Corporativo\\Nova pasta",
      action: "accessed",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "child-folder-access-echo",
      timestampUtc: "2026-07-02T02:44:05.663Z",
      path: "C:\\Corporativo\\Nova pasta\\Nova pasta",
      action: "accessed",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "nested-folder-renamed",
      timestampUtc: "2026-07-02T02:44:12.510Z",
      path: "C:\\Corporativo\\Nova pasta\\Nova pasta - 10",
      previousPath: "C:\\Corporativo\\Nova pasta\\Nova pasta",
      action: "renamed",
      source: "windows-security-log"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.displayAction, event.path]), [
    ["Renomeado", "C:\\Corporativo\\Nova pasta\\Nova pasta - 10"],
    ["Acessado", "C:\\Corporativo\\Nova pasta"]
  ]);
});

test("keeps default nested folder creation before it is renamed", () => {
  const folderPath = "C:\\Corporativo\\Nova pasta -10\\Nova pasta";
  const renamedPath = "C:\\Corporativo\\Nova pasta -10\\Nova pasta - 20";
  const display = buildDisplayEvents([
    buildEvent({
      id: "nested-folder-created",
      timestampUtc: "2026-07-02T04:13:17.000Z",
      path: folderPath,
      action: "created",
      objectType: "folder",
      source: "usn-journal"
    }),
    buildEvent({
      id: "nested-folder-changed",
      timestampUtc: "2026-07-02T04:13:18.000Z",
      path: folderPath,
      action: "changed",
      objectType: "folder",
      source: "usn-journal"
    }),
    buildEvent({
      id: "nested-folder-renamed",
      timestampUtc: "2026-07-02T04:13:24.000Z",
      path: renamedPath,
      previousPath: folderPath,
      action: "renamed",
      objectType: "folder",
      source: "usn-journal"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.displayAction, event.path]), [
    ["Renomeado", renamedPath],
    ["Criação", folderPath]
  ]);
});

test("suppresses security delete echoes after a rename with mojibake accents", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "rename-delete-echo",
      timestampUtc: "2026-07-01T03:46:02.947Z",
      path: "C:\\Corporativo\\Modelo Relat�rio Tecnico.docx",
      action: "deleted",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "rename-final",
      timestampUtc: "2026-07-01T03:46:02.000Z",
      path: "C:\\Corporativo\\Modelo Relatório Tecnico20.docx",
      previousPath: "C:\\Corporativo\\Modelo Relatório Tecnico.docx",
      action: "renamed",
      source: "usn-journal+security-log"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.path, event.displayAction]), [
    ["C:\\Corporativo\\Modelo Relatório Tecnico20.docx", "Renomeado"]
  ]);
});

test("does not synthesize creation for usn-only rename origins", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "rename-usn-only",
      timestampUtc: "2026-07-01T03:54:14.000Z",
      path: "C:\\Corporativo\\codex-client-check-30.txt",
      previousPath: "C:\\Corporativo\\codex-client-check-01020.txt",
      action: "renamed",
      source: "usn-journal",
      user: "UNKNOWN"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.path, event.displayAction]), [
    ["C:\\Corporativo\\codex-client-check-30.txt", "Renomeado"]
  ]);
});

test("does not synthesize creation when a rename origin has a nearby delete echo", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "rename-origin-delete-echo",
      timestampUtc: "2026-07-01T03:54:06.683Z",
      path: "C:\\Corporativo\\codex-client-check-02.md",
      action: "deleted",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "rename-final",
      timestampUtc: "2026-07-01T03:54:06.000Z",
      path: "C:\\Corporativo\\codex-client-check-30.md",
      previousPath: "C:\\Corporativo\\codex-client-check-02.md",
      action: "renamed",
      source: "usn-journal+security-log"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.path, event.displayAction]), [
    ["C:\\Corporativo\\codex-client-check-30.md", "Renomeado"]
  ]);
});

test("keeps a default-named folder rename as rename", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "folder-rename-access",
      timestampUtc: "2026-07-01T04:02:42.840Z",
      path: "C:\\Corporativo\\Nova pasta - 10",
      action: "accessed",
      source: "windows-security-log",
      objectType: "folder"
    }),
    buildEvent({
      id: "folder-rename-delete-echo",
      timestampUtc: "2026-07-01T04:02:42.840Z",
      path: "C:\\Corporativo\\Nova pasta",
      action: "deleted",
      source: "windows-security-log",
      objectType: "folder"
    }),
    buildEvent({
      id: "folder-rename",
      timestampUtc: "2026-07-01T04:02:42.000Z",
      path: "C:\\Corporativo\\Nova pasta - 10",
      previousPath: "C:\\Corporativo\\Nova pasta",
      action: "renamed",
      source: "usn-journal",
      objectType: "folder",
      user: "UNKNOWN"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.path, event.previousPath, event.displayAction]), [
    ["C:\\Corporativo\\Nova pasta - 10", "C:\\Corporativo\\Nova pasta", "Renomeado"]
  ]);
});

test("reconstructs a nested file rename from security delete and access signals", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "nested-rename-access-later",
      timestampUtc: "2026-07-01T04:11:05.397Z",
      path: "C:\\Corporativo\\Example folder\\Another example txt file - 20.txt",
      action: "accessed",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "nested-rename-parent-touch",
      timestampUtc: "2026-07-01T04:11:04.390Z",
      path: "C:\\Corporativo\\Example folder",
      action: "created_or_appended",
      source: "windows-security-log",
      objectType: "folder"
    }),
    buildEvent({
      id: "nested-rename-delete",
      timestampUtc: "2026-07-01T04:11:04.390Z",
      path: "C:\\Corporativo\\Example folder\\Another example txt file.txt",
      action: "deleted",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "nested-rename-access",
      timestampUtc: "2026-07-01T04:11:04.390Z",
      path: "C:\\Corporativo\\Example folder\\Another example txt file - 20.txt",
      action: "accessed",
      source: "windows-security-log"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.path, event.previousPath, event.displayAction]), [
    [
      "C:\\Corporativo\\Example folder\\Another example txt file - 20.txt",
      "C:\\Corporativo\\Example folder\\Another example txt file.txt",
      "Renomeado"
    ]
  ]);
});

test("suppresses access echoes immediately before a folder deletion", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "delete-access-echo",
      timestampUtc: "2026-07-01T04:24:31.843Z",
      path: "C:\\Corporativo\\Nova pasta -30",
      action: "accessed",
      source: "windows-security-log",
      objectType: "folder"
    }),
    buildEvent({
      id: "delete-final",
      timestampUtc: "2026-07-01T04:24:31.000Z",
      path: "C:\\Corporativo\\Nova pasta -30",
      action: "deleted",
      source: "usn-journal+security-log",
      objectType: "folder"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.path, event.displayAction]), [
    ["C:\\Corporativo\\Nova pasta -30", "Excluído"]
  ]);
});

test("suppresses destination folder create echoes when moving items into it", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "destination-create-echo",
      timestampUtc: "2026-07-01T04:44:36.763Z",
      path: "C:\\Corporativo\\Example folder",
      action: "created_or_appended",
      source: "windows-security-log",
      objectType: "folder"
    }),
    buildEvent({
      id: "file-moved",
      timestampUtc: "2026-07-01T04:44:36.000Z",
      path: "C:\\Corporativo\\Example folder\\Modelo Relatório Tecnico.docx",
      previousPath: "C:\\Corporativo\\Modelo Relatório Tecnico.docx",
      action: "moved",
      source: "usn-journal+security-log"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.path, event.displayAction]), [
    ["C:\\Corporativo\\Example folder\\Modelo Relatório Tecnico.docx", "Movido"]
  ]);
});

test("synthesizes descendant deletions after a folder with known children is moved", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "folder-created",
      timestampUtc: "2026-07-01T04:43:13.000Z",
      path: "C:\\Corporativo\\teste",
      action: "created",
      source: "usn-journal+security-log",
      objectType: "folder"
    }),
    buildEvent({
      id: "child-created",
      timestampUtc: "2026-07-01T04:43:13.000Z",
      path: "C:\\Corporativo\\teste\\Example txt file.txt",
      action: "created",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "folder-moved",
      timestampUtc: "2026-07-01T04:44:47.000Z",
      path: "C:\\Corporativo\\Example folder\\teste",
      previousPath: "C:\\Corporativo\\teste",
      action: "moved",
      source: "usn-journal+security-log",
      objectType: "folder"
    }),
    buildEvent({
      id: "folder-deleted",
      timestampUtc: "2026-07-01T05:22:49.000Z",
      path: "C:\\Corporativo\\Example folder\\teste",
      action: "deleted",
      source: "windows-security-log",
      objectType: "folder"
    })
  ]);

  const deleted = display
    .filter((event) => event.displayAction === "Excluído")
    .map((event) => event.path)
    .sort();

  assert.deepEqual(deleted, [
    "C:\\Corporativo\\Example folder\\teste",
    "C:\\Corporativo\\Example folder\\teste\\Example txt file.txt"
  ]);
});

test("does not revive descendants from an earlier deleted folder with the same name", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "old-folder-created",
      timestampUtc: "2026-07-01T04:00:00.000Z",
      path: "C:\\Corporativo\\Example folder",
      action: "created",
      source: "usn-journal+security-log",
      objectType: "folder"
    }),
    buildEvent({
      id: "old-child-created",
      timestampUtc: "2026-07-01T04:00:01.000Z",
      path: "C:\\Corporativo\\Example folder\\old-child.txt",
      action: "created",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "old-folder-deleted",
      timestampUtc: "2026-07-01T04:10:00.000Z",
      path: "C:\\Corporativo\\Example folder",
      action: "deleted",
      source: "usn-journal+security-log",
      objectType: "folder"
    }),
    buildEvent({
      id: "new-folder-created",
      timestampUtc: "2026-07-01T04:20:00.000Z",
      path: "C:\\Corporativo\\Example folder",
      action: "created",
      source: "usn-journal+security-log",
      objectType: "folder"
    }),
    buildEvent({
      id: "new-folder-deleted",
      timestampUtc: "2026-07-01T04:30:00.000Z",
      path: "C:\\Corporativo\\Example folder",
      action: "deleted",
      source: "usn-journal+security-log",
      objectType: "folder"
    })
  ]);

  const latestDeleted = display
    .filter((event) => event.displayAction === "Excluído")
    .filter((event) => event.timestampUtc === "2026-07-01T04:30:00.000Z")
    .map((event) => event.path)
    .sort();

  assert.deepEqual(latestDeleted, ["C:\\Corporativo\\Example folder"]);
});

test("reconstructs a folder move from security log signals and keeps descendants live", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "folder-created",
      timestampUtc: "2026-07-01T04:43:13.000Z",
      path: "C:\\Corporativo\\teste",
      action: "created",
      source: "usn-journal+security-log",
      objectType: "folder"
    }),
    buildEvent({
      id: "child-created",
      timestampUtc: "2026-07-01T04:43:13.000Z",
      path: "C:\\Corporativo\\teste\\Atesto.docx",
      action: "created",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "source-delete",
      timestampUtc: "2026-07-01T05:06:56.330Z",
      path: "C:\\Corporativo\\teste",
      action: "deleted",
      source: "windows-security-log",
      objectType: "folder"
    }),
    buildEvent({
      id: "target-folder-touch",
      timestampUtc: "2026-07-01T05:06:56.330Z",
      path: "C:\\Corporativo\\Example folder",
      action: "created_or_appended",
      source: "windows-security-log",
      objectType: "folder"
    }),
    buildEvent({
      id: "target-access",
      timestampUtc: "2026-07-01T05:06:56.347Z",
      path: "C:\\Corporativo\\Example folder\\teste",
      action: "accessed",
      source: "windows-security-log",
      objectType: "folder"
    }),
    buildEvent({
      id: "ambiguous-usn-rename",
      timestampUtc: "2026-07-01T05:06:56.000Z",
      path: "C:\\Corporativo\\teste",
      previousPath: "C:\\Corporativo\\teste",
      action: "renamed",
      source: "usn-journal",
      objectType: "folder"
    }),
    buildEvent({
      id: "target-delete",
      timestampUtc: "2026-07-01T05:22:49.000Z",
      path: "C:\\Corporativo\\Example folder\\teste",
      action: "deleted",
      source: "windows-security-log",
      objectType: "folder"
    })
  ]);

  const moved = display
    .filter((event) => event.displayAction === "Movido")
    .map((event) => [event.previousPath, event.path]);
  const deleted = display
    .filter((event) => event.displayAction === "Excluído")
    .map((event) => event.path)
    .sort();

  assert.deepEqual(moved, [["C:\\Corporativo\\teste", "C:\\Corporativo\\Example folder\\teste"]]);
  assert.deepEqual(deleted, [
    "C:\\Corporativo\\Example folder\\teste",
    "C:\\Corporativo\\Example folder\\teste\\Atesto.docx"
  ]);
});

test("keeps moved provisional office document as moved", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "office-moved",
      timestampUtc: "2026-07-01T04:44:43.000Z",
      path: "C:\\Corporativo\\Example folder\\Novo(a) Planilha do Microsoft Excel.xlsx",
      previousPath: "C:\\Corporativo\\Novo(a) Planilha do Microsoft Excel.xlsx",
      action: "moved",
      source: "usn-journal+security-log"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.path, event.previousPath, event.displayAction]), [
    [
      "C:\\Corporativo\\Example folder\\Novo(a) Planilha do Microsoft Excel.xlsx",
      "C:\\Corporativo\\Novo(a) Planilha do Microsoft Excel.xlsx",
      "Movido"
    ]
  ]);
});

test("suppresses parent and origin access echoes around a move", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "source-access",
      timestampUtc: "2026-07-01T05:40:53.357Z",
      path: "C:\\Corporativo\\codex-client-check-02 - rename.md",
      action: "accessed",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "target-access",
      timestampUtc: "2026-07-01T05:40:53.363Z",
      path: "C:\\Corporativo\\Example folder\\codex-client-check-02 - rename.md",
      action: "accessed",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "parent-access",
      timestampUtc: "2026-07-01T05:40:53.370Z",
      path: "C:\\Corporativo\\Example folder",
      action: "accessed",
      source: "windows-security-log",
      objectType: "folder"
    }),
    buildEvent({
      id: "file-move",
      timestampUtc: "2026-07-01T05:40:53.000Z",
      path: "C:\\Corporativo\\Example folder\\codex-client-check-02 - rename.md",
      previousPath: "C:\\Corporativo\\codex-client-check-02 - rename.md",
      action: "moved",
      source: "usn-journal"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.displayAction, event.path]), [
    ["Movido", "C:\\Corporativo\\Example folder\\codex-client-check-02 - rename.md"]
  ]);
});

test("keeps rename after moving a provisional office document as rename", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "office-move",
      timestampUtc: "2026-07-01T05:40:27.000Z",
      path: "C:\\Corporativo\\Example folder\\Novo(a) Planilha do Microsoft Excel.xlsx",
      previousPath: "C:\\Corporativo\\Novo(a) Planilha do Microsoft Excel.xlsx",
      action: "moved",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "office-rename",
      timestampUtc: "2026-07-01T05:40:37.000Z",
      path: "C:\\Corporativo\\Example folder\\Novo(a) Planilha do Microsoft Excel- rename.xlsx",
      previousPath: "C:\\Corporativo\\Example folder\\Novo(a) Planilha do Microsoft Excel.xlsx",
      action: "renamed",
      source: "usn-journal+security-log"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.displayAction, event.previousPath, event.path]), [
    [
      "Renomeado",
      "C:\\Corporativo\\Example folder\\Novo(a) Planilha do Microsoft Excel.xlsx",
      "C:\\Corporativo\\Example folder\\Novo(a) Planilha do Microsoft Excel- rename.xlsx"
    ],
    [
      "Movido",
      "C:\\Corporativo\\Novo(a) Planilha do Microsoft Excel.xlsx",
      "C:\\Corporativo\\Example folder\\Novo(a) Planilha do Microsoft Excel.xlsx"
    ]
  ]);
});

test("reconstructs a file move from security delete and destination access", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "source-delete",
      timestampUtc: "2026-07-01T20:25:22.930Z",
      path: "C:\\Corporativo\\codex-client-check-01.txt",
      action: "deleted",
      source: "windows-security-log"
    }),
    buildEvent({
      id: "target-parent-touch",
      timestampUtc: "2026-07-01T20:25:22.930Z",
      path: "C:\\Corporativo\\Nova pasta - 40",
      action: "created_or_appended",
      source: "windows-security-log",
      objectType: "folder"
    }),
    buildEvent({
      id: "target-access",
      timestampUtc: "2026-07-01T20:25:22.933Z",
      path: "C:\\Corporativo\\Nova pasta - 40\\codex-client-check-01.txt",
      action: "accessed",
      source: "windows-security-log"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.displayAction, event.previousPath, event.path]), [
    [
      "Movido",
      "C:\\Corporativo\\codex-client-check-01.txt",
      "C:\\Corporativo\\Nova pasta - 40\\codex-client-check-01.txt"
    ]
  ]);
});

test("does not turn parallel same-name deletes into moves", () => {
  const left = "C:\\Corporativo\\load\\worker-02\\delete-tree";
  const right = "C:\\Corporativo\\load\\worker-04\\delete-tree";
  const display = buildDisplayEvents([
    buildEvent({
      id: "right-deleted",
      timestampUtc: "2026-07-02T04:58:26.370Z",
      path: right,
      action: "deleted",
      source: "windows-security-log",
      user: "FILESERVER\\Administrator",
      objectType: "folder"
    }),
    buildEvent({
      id: "left-accessed",
      timestampUtc: "2026-07-02T04:58:26.370Z",
      path: left,
      action: "accessed",
      source: "windows-security-log",
      user: "FILESERVER\\Administrator",
      objectType: "folder"
    }),
    buildEvent({
      id: "left-parent-touched",
      timestampUtc: "2026-07-02T04:58:26.370Z",
      path: "C:\\Corporativo\\load\\worker-02",
      action: "modified",
      source: "windows-security-log",
      user: "FILESERVER\\Administrator",
      objectType: "folder"
    }),
    buildEvent({
      id: "left-deleted",
      timestampUtc: "2026-07-02T04:58:26.373Z",
      path: left,
      action: "deleted",
      source: "windows-security-log",
      user: "FILESERVER\\Administrator",
      objectType: "folder"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.displayAction, event.path, event.previousPath]), [
    ["Excluído", left, null],
    ["Excluído", right, null]
  ]);
});

test("does not treat a newly created sibling path as a move destination", () => {
  const source = "C:\\Corporativo\\load\\worker-03\\move-04.txt";
  const unrelatedNewFile = "C:\\Corporativo\\load\\worker-04\\move-04.txt";
  const display = buildDisplayEvents([
    buildEvent({
      id: "source-deleted",
      timestampUtc: "2026-07-02T05:08:53.670Z",
      path: source,
      action: "deleted",
      source: "windows-security-log",
      user: "FILESERVER\\Administrator"
    }),
    buildEvent({
      id: "target-created",
      timestampUtc: "2026-07-02T05:08:53.390Z",
      path: unrelatedNewFile,
      action: "created_or_appended",
      source: "windows-security-log",
      user: "FILESERVER\\Administrator"
    }),
    buildEvent({
      id: "target-accessed",
      timestampUtc: "2026-07-02T05:08:53.390Z",
      path: unrelatedNewFile,
      action: "accessed",
      source: "windows-security-log",
      user: "FILESERVER\\Administrator"
    }),
    buildEvent({
      id: "target-deleted",
      timestampUtc: "2026-07-02T05:08:53.643Z",
      path: unrelatedNewFile,
      action: "deleted",
      source: "windows-security-log",
      user: "FILESERVER\\Administrator"
    }),
    buildEvent({
      id: "target-parent-touched",
      timestampUtc: "2026-07-02T05:08:53.390Z",
      path: "C:\\Corporativo\\load\\worker-04",
      action: "modified",
      source: "windows-security-log",
      user: "FILESERVER\\Administrator",
      objectType: "folder"
    })
  ]);

  assert.equal(display.some((event) => event.displayAction === "Movido"), false);
  assert.equal(display.some((event) => event.displayAction === "Excluído" && event.path === source), true);
  assert.equal(display.some((event) => event.displayAction === "Criação" && event.path === unrelatedNewFile), true);
});

test("reconstructs a folder move from security delete and destination access despite same-path usn rename", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "source-delete",
      timestampUtc: "2026-07-01T20:25:29.640Z",
      path: "C:\\Corporativo\\teste",
      action: "deleted",
      source: "windows-security-log",
      objectType: "folder"
    }),
    buildEvent({
      id: "target-parent-touch",
      timestampUtc: "2026-07-01T20:25:29.643Z",
      path: "C:\\Corporativo\\Nova pasta - 40",
      action: "created_or_appended",
      source: "windows-security-log",
      objectType: "folder"
    }),
    buildEvent({
      id: "target-access",
      timestampUtc: "2026-07-01T20:25:29.643Z",
      path: "C:\\Corporativo\\Nova pasta - 40\\teste",
      action: "accessed",
      source: "windows-security-log",
      objectType: "folder"
    }),
    buildEvent({
      id: "ambiguous-usn",
      timestampUtc: "2026-07-01T20:25:29.000Z",
      path: "C:\\Corporativo\\teste",
      previousPath: "C:\\Corporativo\\teste",
      action: "renamed",
      source: "usn-journal+security-log",
      objectType: "folder"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.displayAction, event.previousPath, event.path]), [
    [
      "Movido",
      "C:\\Corporativo\\teste",
      "C:\\Corporativo\\Nova pasta - 40\\teste"
    ]
  ]);
});

test("reconstructs a folder move from same-path usn rename and destination access", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "target-parent-touch",
      timestampUtc: "2026-07-01T20:25:29.643Z",
      path: "C:\\Corporativo\\Nova pasta - 40",
      action: "created_or_appended",
      source: "windows-security-log",
      objectType: "folder"
    }),
    buildEvent({
      id: "target-access",
      timestampUtc: "2026-07-01T20:25:29.643Z",
      path: "C:\\Corporativo\\Nova pasta - 40\\teste",
      action: "accessed",
      source: "windows-security-log",
      objectType: "folder"
    }),
    buildEvent({
      id: "ambiguous-usn",
      timestampUtc: "2026-07-01T20:25:29.000Z",
      path: "C:\\Corporativo\\teste",
      previousPath: "C:\\Corporativo\\teste",
      action: "renamed",
      source: "usn-journal+security-log",
      objectType: "folder"
    })
  ]);

  assert.deepEqual(display.map((event) => [event.displayAction, event.previousPath, event.path]), [
    [
      "Movido",
      "C:\\Corporativo\\teste",
      "C:\\Corporativo\\Nova pasta - 40\\teste"
    ]
  ]);
});

test("suppresses standalone folder modified echoes", () => {
  const display = buildDisplayEvents([
    buildEvent({
      id: "folder-modified",
      timestampUtc: "2026-07-01T20:22:31.000Z",
      path: "C:\\Corporativo\\Example folder",
      action: "modified",
      source: "usn-journal+security-log",
      objectType: "folder"
    })
  ]);

  assert.deepEqual(display, []);
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

test("suppresses technical folder access emitted alongside permission changes", () => {
  const path = "C:\\Corporativo\\codex-mixed-live-01\\Acl";
  const display = buildDisplayEvents([
    buildEvent({
      id: "permission-folder",
      timestampUtc: "2026-07-12T04:32:12.000Z",
      path,
      objectType: "folder",
      action: "permission_changed",
      user: "FILESERVER\\Administrator",
      source: "usn-journal+security-log"
    }),
    buildEvent({
      id: "access-folder",
      timestampUtc: "2026-07-12T04:32:12.867Z",
      path,
      objectType: "folder",
      action: "accessed",
      user: "FILESERVER\\Administrator",
      source: "windows-security-log"
    })
  ]);

  assert.equal(display.length, 1);
  assert.equal(display[0]?.action, "permission_changed");
});
