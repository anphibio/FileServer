import { readFileSync } from "node:fs";
import { buildDisplayEvents } from "../src/FileServerMonitor.Web/src/event-timeline.ts";

const [eventsPath, expectedPath, rootName] = process.argv.slice(2);

if (!eventsPath || !expectedPath || !rootName) {
  console.error("usage: validate-access-action-timeline.mjs <events.json> <expected.json> <root-name>");
  process.exit(2);
}

const events = JSON.parse(readFileSync(eventsPath, "utf8"));
const expected = JSON.parse(readFileSync(expectedPath, "utf8"));
const display = buildDisplayEvents(events);
const accessed = display
  .filter((event) => event.displayAction === "Acessado")
  .filter((event) => event.path.includes(rootName))
  .map((event) => event.path);

const uniqueAccessed = [...new Set(accessed)].sort();
const expectedPaths = [...expected].sort();
const missing = expected.filter((path) => !uniqueAccessed.includes(path));
const extras = accessed.filter((path) => !expectedPaths.includes(path));
const duplicates = accessed.filter((path, index) => accessed.indexOf(path) !== index);

const report = {
  displayAccessedCount: accessed.length,
  displayUniqueAccessedCount: uniqueAccessed.length,
  expectedCount: expected.length,
  missing,
  extras,
  duplicates: [...new Set(duplicates)].sort(),
  accessed: uniqueAccessed
};

console.log(JSON.stringify(report, null, 2));

if (missing.length > 0 || extras.length > 0 || duplicates.length > 0) {
  process.exit(1);
}
