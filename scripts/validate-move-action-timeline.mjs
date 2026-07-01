import { readFileSync } from "node:fs";
import { buildDisplayEvents } from "../src/FileServerMonitor.Web/src/event-timeline.ts";

const [eventsPath, expectedPath, rootName] = process.argv.slice(2);

if (!eventsPath || !expectedPath || !rootName) {
  console.error("usage: validate-move-action-timeline.mjs <events.json> <expected.json> <root-name>");
  process.exit(2);
}

const events = JSON.parse(readFileSync(eventsPath, "utf8"));
const expected = JSON.parse(readFileSync(expectedPath, "utf8"));
const display = buildDisplayEvents(events);
const moved = display
  .filter((event) => event.displayAction === "Movido")
  .filter((event) => event.path.includes(rootName) || event.previousPath?.includes(rootName))
  .map((event) => ({
    from: event.previousPath,
    to: event.path
  }));

const keyFor = (item) => `${item.from} -> ${item.to}`;
const movedKeys = moved.map(keyFor);
const uniqueMovedKeys = [...new Set(movedKeys)].sort();
const expectedKeys = expected.map(keyFor).sort();
const missing = expected.filter((item) => !uniqueMovedKeys.includes(keyFor(item)));
const extras = moved.filter((item) => !expectedKeys.includes(keyFor(item)));
const duplicates = movedKeys.filter((key, index) => movedKeys.indexOf(key) !== index);

const report = {
  displayMovedCount: moved.length,
  displayUniqueMovedCount: uniqueMovedKeys.length,
  expectedCount: expected.length,
  missing,
  extras,
  duplicates: [...new Set(duplicates)].sort(),
  moved: uniqueMovedKeys
};

console.log(JSON.stringify(report, null, 2));

if (missing.length > 0 || extras.length > 0 || duplicates.length > 0) {
  process.exit(1);
}
