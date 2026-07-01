import { readFileSync } from "node:fs";
import { buildDisplayEvents } from "../src/FileServerMonitor.Web/src/event-timeline.ts";

const [eventsPath, expectedPath, rootName] = process.argv.slice(2);

if (!eventsPath || !expectedPath || !rootName) {
  console.error("usage: validate-rename-action-timeline.mjs <events.json> <expected.json> <root-name>");
  process.exit(2);
}

const events = JSON.parse(readFileSync(eventsPath, "utf8"));
const expected = JSON.parse(readFileSync(expectedPath, "utf8"));
const display = buildDisplayEvents(events);
const renamed = display
  .filter((event) => event.displayAction === "Renomeado")
  .filter((event) => event.path.includes(rootName) || event.previousPath?.includes(rootName))
  .map((event) => ({
    from: event.previousPath,
    to: event.path
  }));

const keyFor = (item) => `${item.from} -> ${item.to}`;
const renamedKeys = renamed.map(keyFor);
const uniqueRenamedKeys = [...new Set(renamedKeys)].sort();
const expectedKeys = expected.map(keyFor).sort();
const missing = expected.filter((item) => !uniqueRenamedKeys.includes(keyFor(item)));
const extras = renamed.filter((item) => !expectedKeys.includes(keyFor(item)));
const duplicates = renamedKeys.filter((key, index) => renamedKeys.indexOf(key) !== index);

const report = {
  displayRenamedCount: renamed.length,
  displayUniqueRenamedCount: uniqueRenamedKeys.length,
  expectedCount: expected.length,
  missing,
  extras,
  duplicates: [...new Set(duplicates)].sort(),
  renamed: uniqueRenamedKeys
};

console.log(JSON.stringify(report, null, 2));

if (missing.length > 0 || extras.length > 0 || duplicates.length > 0) {
  process.exit(1);
}
