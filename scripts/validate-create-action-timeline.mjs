import { readFileSync } from "node:fs";
import { buildDisplayEvents } from "../src/FileServerMonitor.Web/src/event-timeline.ts";

const [eventsPath, expectedPath, rootName] = process.argv.slice(2);

if (!eventsPath || !expectedPath || !rootName) {
  console.error("usage: validate-create-action-timeline.mjs <events.json> <expected.json> <root-name>");
  process.exit(2);
}

const events = JSON.parse(readFileSync(eventsPath, "utf8"));
const expected = JSON.parse(readFileSync(expectedPath, "utf8"));
const display = buildDisplayEvents(events);
const created = display
  .filter((event) => event.displayAction === "Criação")
  .filter((event) => event.path.includes(rootName))
  .map((event) => event.path);

const uniqueCreated = [...new Set(created)].sort();
const missing = expected.filter((path) => !uniqueCreated.includes(path));
const duplicates = created.filter((path, index) => created.indexOf(path) !== index);

const report = {
  displayCreatedCount: created.length,
  displayUniqueCreatedCount: uniqueCreated.length,
  expectedCount: expected.length,
  missing,
  duplicates: [...new Set(duplicates)].sort(),
  created: uniqueCreated
};

console.log(JSON.stringify(report, null, 2));

if (missing.length > 0 || duplicates.length > 0) {
  process.exit(1);
}
