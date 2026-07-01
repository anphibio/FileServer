import test from "node:test";
import assert from "node:assert/strict";

import { promoteLikelyInitialCreations, shouldSuppressProvisionalCreate } from "./event-correlation-rules.ts";

test("keeps a provisional text document when it was not renamed", () => {
  const events = [
    {
      id: "1",
      timestampUtc: "2026-05-24T21:00:00.000Z",
      path: "E:\\Corporativo\\Novo Documento de Texto.txt",
      action: "created",
      source: "windows-security-log"
    }
  ];

  assert.equal(shouldSuppressProvisionalCreate(events[0], events), false);
});

test("suppresses a provisional create only when a later transition starts from that exact path", () => {
  const events = [
    {
      id: "1",
      timestampUtc: "2026-05-24T21:00:00.000Z",
      path: "E:\\Corporativo\\New Bitmap Image.bmp",
      action: "created",
      source: "windows-security-log"
    },
    {
      id: "2",
      timestampUtc: "2026-05-24T21:00:03.000Z",
      path: "E:\\Corporativo\\teste-bmp-01.bmp",
      previousPath: "E:\\Corporativo\\New Bitmap Image.bmp",
      action: "renamed",
      source: "usn-journal+security-log"
    }
  ];

  assert.equal(shouldSuppressProvisionalCreate(events[0], events), true);
});

test("promotes first changed event into creation when a later lifecycle event confirms it", () => {
  const promoted = promoteLikelyInitialCreations([
    {
      id: "1",
      timestampUtc: "2026-05-24T21:00:00.000Z",
      path: "E:\\Corporativo\\teste-xlsx-01.xlsx",
      action: "changed",
      source: "usn-journal"
    },
    {
      id: "2",
      timestampUtc: "2026-05-24T21:00:20.000Z",
      path: "E:\\Corporativo\\teste-xlsx-01.xlsx",
      action: "deleted",
      source: "windows-security-log"
    }
  ]);

  assert.equal(promoted[0]?.action, "created");
  assert.equal(promoted[0]?.displayAction, "Criação");
});

test("does not promote a changed event when it already has earlier history", () => {
  const promoted = promoteLikelyInitialCreations([
    {
      id: "1",
      timestampUtc: "2026-05-24T20:59:00.000Z",
      path: "E:\\Corporativo\\teste-xlsx-01.xlsx",
      action: "created",
      source: "windows-security-log"
    },
    {
      id: "2",
      timestampUtc: "2026-05-24T21:00:00.000Z",
      path: "E:\\Corporativo\\teste-xlsx-01.xlsx",
      action: "changed",
      source: "usn-journal"
    }
  ]);

  assert.equal(promoted[1]?.action, "changed");
});

test("still promotes when earlier history is only weak noise on the same path", () => {
  const promoted = promoteLikelyInitialCreations([
    {
      id: "weak-1",
      timestampUtc: "2026-05-24T22:54:30.000Z",
      path: "E:\\Corporativo\\teste-txt-01.txt",
      action: "accessed",
      source: "windows-security-log"
    },
    {
      id: "weak-2",
      timestampUtc: "2026-05-24T22:54:35.000Z",
      path: "E:\\Corporativo\\teste-txt-01.txt",
      action: "modified",
      source: "usn-journal"
    },
    {
      id: "later-move",
      timestampUtc: "2026-05-24T22:55:03.000Z",
      path: "E:\\Corporativo\\RH\\teste-txt-01.txt",
      previousPath: "E:\\Corporativo\\teste-txt-01.txt",
      action: "moved",
      source: "usn-journal+security-log"
    }
  ]);

  assert.equal(promoted[1]?.action, "created");
  assert.equal(promoted[1]?.displayAction, "Criação");
});

test("promotes direct final-name creations when later move or delete confirms the lifecycle", () => {
  const promoted = promoteLikelyInitialCreations([
    {
      id: "txt-1",
      timestampUtc: "2026-05-24T22:54:35.000Z",
      path: "E:\\Corporativo\\teste-txt-01.txt",
      action: "modified",
      source: "usn-journal"
    },
    {
      id: "bmp-1",
      timestampUtc: "2026-05-24T22:54:45.000Z",
      path: "E:\\Corporativo\\teste-bmp-02.bmp",
      action: "modified",
      source: "usn-journal+security-log"
    },
    {
      id: "ppt-1",
      timestampUtc: "2026-05-24T22:54:48.000Z",
      path: "E:\\Corporativo\\teste-pptx-01.pptx",
      action: "modified",
      source: "usn-journal+security-log"
    },
    {
      id: "xlsx-1",
      timestampUtc: "2026-05-24T22:54:57.000Z",
      path: "E:\\Corporativo\\teste-xlsx-01.xlsx",
      action: "modified",
      source: "usn-journal+security-log"
    },
    {
      id: "txt-move",
      timestampUtc: "2026-05-24T22:55:03.000Z",
      path: "E:\\Corporativo\\RH\\teste-txt-01.txt",
      previousPath: "E:\\Corporativo\\teste-txt-01.txt",
      action: "moved",
      source: "usn-journal+security-log"
    },
    {
      id: "bmp-move",
      timestampUtc: "2026-05-24T22:55:06.000Z",
      path: "E:\\Corporativo\\RH\\teste-bmp-02.bmp",
      previousPath: "E:\\Corporativo\\teste-bmp-02.bmp",
      action: "moved",
      source: "usn-journal+security-log"
    },
    {
      id: "ppt-delete",
      timestampUtc: "2026-05-24T22:55:18.000Z",
      path: "E:\\Corporativo\\teste-pptx-01.pptx",
      action: "deleted",
      source: "windows-security-log"
    },
    {
      id: "xlsx-delete",
      timestampUtc: "2026-05-24T22:55:18.000Z",
      path: "E:\\Corporativo\\teste-xlsx-01.xlsx",
      action: "deleted",
      source: "windows-security-log"
    }
  ]);

  assert.equal(promoted[0]?.action, "created");
  assert.equal(promoted[1]?.action, "created");
  assert.equal(promoted[2]?.action, "created");
  assert.equal(promoted[3]?.action, "created");
});

test("still promotes when earlier strong history only comes from a transient rename into the same file", () => {
  const promoted = promoteLikelyInitialCreations([
    {
      id: "temp-rename",
      timestampUtc: "2026-05-24T22:54:32.000Z",
      path: "E:\\Corporativo\\teste-xlsx-01.xlsx",
      previousPath: "E:\\Corporativo\\~$temp-xlsx-01.tmp",
      action: "renamed",
      source: "usn-journal"
    },
    {
      id: "xlsx-1",
      timestampUtc: "2026-05-24T22:54:57.000Z",
      path: "E:\\Corporativo\\teste-xlsx-01.xlsx",
      action: "modified",
      source: "usn-journal+security-log"
    },
    {
      id: "xlsx-delete",
      timestampUtc: "2026-05-24T22:55:18.000Z",
      path: "E:\\Corporativo\\teste-xlsx-01.xlsx",
      action: "deleted",
      source: "windows-security-log"
    }
  ]);

  assert.equal(promoted[1]?.action, "created");
  assert.equal(promoted[1]?.displayAction, "Criação");
});
