import { readFileSync } from "node:fs";

const [eventsPath, expectedPath, rootName] = process.argv.slice(2);

if (!eventsPath || !expectedPath || !rootName) {
  console.error("usage: validate-load-action-timeline.mjs <events.json> <expected.json> <root-name>");
  process.exit(2);
}

const events = JSON.parse(readFileSync(eventsPath, "utf8"));
const expected = JSON.parse(readFileSync(expectedPath, "utf8"));
const display = events
  .filter((event) => event.path.includes(rootName) || event.previousPath?.includes(rootName));
const expectedActions = new Set(["created", "deleted", "accessed", "renamed", "moved"]);

const keyForTransition = (item) => `${item.from} -> ${item.to}`;
const unique = (items) => [...new Set(items)].sort();

function comparePaths(displayAction, expectedPaths) {
  const actual = display
    .filter((event) => event.action === displayAction)
    .map((event) => event.path);
  const actualUnique = unique(actual);
  const expectedSorted = [...expectedPaths].sort();
  return {
    expected: expectedSorted.length,
    actual: actual.length,
    unique: actualUnique.length,
    missing: expectedSorted.filter((path) => !actualUnique.includes(path)),
    extras: actual.filter((path) => !expectedSorted.includes(path)),
    duplicates: unique(actual.filter((path, index) => actual.indexOf(path) !== index))
  };
}

function compareTransitions(displayAction, expectedTransitions) {
  const actual = display
    .filter((event) => event.action === displayAction)
    .map((event) => ({
      from: event.previousPath,
      to: event.path
    }));
  const actualKeys = actual.map(keyForTransition);
  const actualUnique = unique(actualKeys);
  const expectedKeys = expectedTransitions.map(keyForTransition).sort();
  return {
    expected: expectedKeys.length,
    actual: actual.length,
    unique: actualUnique.length,
    missing: expectedTransitions.filter((item) => !actualUnique.includes(keyForTransition(item))),
    extras: actual.filter((item) => !expectedKeys.includes(keyForTransition(item))),
    duplicates: unique(actualKeys.filter((key, index) => actualKeys.indexOf(key) !== index))
  };
}

const report = {
  rootName,
  rawEvents: events.filter((event) => event.path.includes(rootName) || event.previousPath?.includes(rootName)).length,
  displayEvents: display.length,
  unexpectedActions: display
    .filter((event) => !expectedActions.has(event.action))
    .map((event) => ({ action: event.action, path: event.path, timestampUtc: event.timestampUtc })),
  userMismatches: display
    .filter((event) => event.user !== expected.user)
    .map((event) => ({ action: event.action, path: event.path, user: event.user })),
  created: comparePaths("created", expected.created),
  deleted: comparePaths("deleted", expected.deleted),
  accessed: comparePaths("accessed", expected.accessed),
  renamed: compareTransitions("renamed", expected.renamed),
  moved: compareTransitions("moved", expected.moved)
};

report.summary = Object.fromEntries(
  Object.entries(report)
    .filter(([, value]) => value && typeof value === "object" && "expected" in value)
    .map(([key, value]) => [
      key,
      {
        expected: value.expected,
        unique: value.unique,
        missing: value.missing.length,
        extras: value.extras.length,
        duplicates: value.duplicates.length
      }
    ])
);

console.log(JSON.stringify(report, null, 2));

const strict = process.env.STRICT_LOAD_VALIDATION === "1";
const failed = Object.values(report.summary).some((section) =>
  section.missing > 0 || section.extras > 0 || section.duplicates > 0);

if (strict && failed) {
  process.exit(1);
}
