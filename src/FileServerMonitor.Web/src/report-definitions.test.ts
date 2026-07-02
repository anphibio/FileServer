import test from "node:test";
import assert from "node:assert/strict";

import {
  buildReportQueryParams,
  createFiltersForScenario,
  executableExtensions,
  reportScenarios,
  summarizeReportFilters
} from "./report-definitions.ts";

test("defines the guided report scenarios expected by operations", () => {
  assert.equal(reportScenarios.length, 11);
  assert.deepEqual(reportScenarios.map((scenario) => scenario.id), [
    "folder-activity",
    "user-activity",
    "server-activity",
    "read-without-change",
    "mass-delete",
    "recurrent-denied-access",
    "permission-changes",
    "mass-rename",
    "executable-creation",
    "source-host-activity",
    "suspicious-remote-access"
  ]);
});

test("uses narrow presets for high-volume incident reports", () => {
  assert.equal(createFiltersForScenario("mass-delete").action, "deleted");
  assert.equal(createFiltersForScenario("mass-rename").action, "renamed");
  assert.equal(createFiltersForScenario("read-without-change").action, "accessed");
});

test("tracks executable and script creation with a multi-extension filter", () => {
  const filters = createFiltersForScenario("executable-creation");

  assert.equal(filters.action, "created");
  assert.equal(filters.extension, executableExtensions);
  assert.equal(filters.groupBy, "extension");
});

test("builds report query parameters from the same filter object used by the UI", () => {
  const filters = {
    ...createFiltersForScenario("mass-delete"),
    periodHours: "6",
    server: "FS01",
    share: "Corporativo",
    user: "DOMINIO\\maria",
    path: "C:\\Corporativo\\RH"
  };
  const params = buildReportQueryParams(filters, 500);

  assert.equal(params.get("take"), "500");
  assert.equal(params.get("server"), "FS01");
  assert.equal(params.get("share"), "Corporativo");
  assert.equal(params.get("user"), "DOMINIO\\maria");
  assert.equal(params.get("path"), "C:\\Corporativo\\RH");
  assert.equal(params.get("action"), "deleted");
  assert.ok(params.get("fromUtc"));
});

test("summarizes active filters for report headers", () => {
  const filters = {
    ...createFiltersForScenario("source-host-activity"),
    sourceHost: "NOTE-01",
    path: "C:\\Corporativo"
  };

  assert.match(summarizeReportFilters(filters), /host NOTE-01/);
  assert.match(summarizeReportFilters(filters), /caminho C:\\Corporativo/);
});
