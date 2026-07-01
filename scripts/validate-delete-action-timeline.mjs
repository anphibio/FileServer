import { readFileSync } from "node:fs";
import { buildDisplayEvents } from "../src/FileServerMonitor.Web/src/event-timeline.ts";

const [eventsPath, expectedPath, rootName] = process.argv.slice(2);

if (!eventsPath || !expectedPath || !rootName) {
  console.error("usage: validate-delete-action-timeline.mjs <events.json> <expected.json> <root-name>");
  process.exit(2);
}

const events = JSON.parse(readFileSync(eventsPath, "utf8"));
const expected = JSON.parse(readFileSync(expectedPath, "utf8"));
const display = buildDisplayEvents(events);
const deleted = display
  .filter((event) => event.displayAction === "Excluído")
  .filter((event) => event.path.includes(rootName))
  .map((event) => event.path);

const uniqueDeleted = [...new Set(deleted)].sort();
const missing = expected.filter((path) => !uniqueDeleted.includes(path));
const duplicates = deleted.filter((path, index) => deleted.indexOf(path) !== index);

const report = {
  displayDeletedCount: deleted.length,
  displayUniqueDeletedCount: uniqueDeleted.length,
  expectedCount: expected.length,
  missing,
  duplicates: [...new Set(duplicates)].sort(),
  deleted: uniqueDeleted
};

console.log(JSON.stringify(report, null, 2));

if (missing.length > 0 || duplicates.length > 0) {
  process.exit(1);
}
