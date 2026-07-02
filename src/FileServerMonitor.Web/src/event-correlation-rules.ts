import { isTransientArtifactPath } from "./event-noise.ts";

type EventLike = {
  id: string;
  timestampUtc: string;
  path: string;
  previousPath?: string | null;
  action: string;
  source: string;
  displayAction?: string;
  displayTarget?: string;
};

export function shouldSuppressProvisionalCreate(event: EventLike, allEvents: EventLike[]) {
  if (event.action !== "created" && event.action !== "created_or_appended") {
    return false;
  }

  if (!isProvisionalDocumentName(event.path) && !isProvisionalFolderName(event.path)) {
    return false;
  }

  const eventTime = new Date(event.timestampUtc).getTime();
  const path = normalizePath(event.path);

  return allEvents.some((candidate) =>
    candidate.id !== event.id
    && Math.abs(new Date(candidate.timestampUtc).getTime() - eventTime) <= 45_000
    && (candidate.action === "renamed" || candidate.action === "moved")
    && normalizePath(candidate.previousPath) === path
    && !isTransientArtifactPath(candidate.path));
}

export function promoteLikelyInitialCreations<T extends EventLike>(events: T[]) {
  return events.map((event) => {
    if (!isLikelyInitialCreationEvent(event, events)) {
      return event;
    }

    return {
      ...event,
      action: "created",
      displayAction: "Criação",
      displayTarget: getLeafName(event.path)
    } satisfies T;
  });
}

export function isLikelyInitialCreationEvent<T extends EventLike>(event: T, events: T[]) {
  if ((event.action !== "changed" && event.action !== "modified") || !event.source.includes("usn-journal")) {
    return false;
  }

  if (!isFileLikePath(event.path)) {
    return false;
  }

  const eventTime = new Date(event.timestampUtc).getTime();
  const path = normalizePath(event.path);
  const earlierRelatedHistory = events.filter((candidate) =>
    candidate.id !== event.id
    && new Date(candidate.timestampUtc).getTime() < eventTime
    && (normalizePath(candidate.path) === path || normalizePath(candidate.previousPath) === path));

  const hasEarlierStrongHistory = earlierRelatedHistory.some((candidate) =>
    isStrongLifecycleAction(candidate.action)
    && !isIgnorableEarlierLifecycle(candidate, path));
  if (hasEarlierStrongHistory) {
    return false;
  }

  return events.some((candidate) =>
    candidate.id !== event.id
    && new Date(candidate.timestampUtc).getTime() >= eventTime
    && new Date(candidate.timestampUtc).getTime() - eventTime <= 300_000
    && (
      normalizePath(candidate.path) === path
      || normalizePath(candidate.previousPath) === path
    )
    && (
      candidate.action === "deleted"
      || candidate.action === "renamed"
      || candidate.action === "moved"
    ));
}

function isIgnorableEarlierLifecycle(event: EventLike, targetPath: string) {
  const normalizedPath = normalizePath(event.path);
  const normalizedPreviousPath = normalizePath(event.previousPath);
  const touchesTransientOrigin =
    isTransientArtifactPath(event.path)
    || isTransientArtifactPath(event.previousPath ?? "")
    || isProvisionalDocumentName(event.path)
    || isProvisionalDocumentName(event.previousPath ?? "")
    || isProvisionalFolderName(event.path)
    || isProvisionalFolderName(event.previousPath ?? "");

  if (!touchesTransientOrigin) {
    return false;
  }

  return normalizedPath === targetPath || normalizedPreviousPath === targetPath;
}

function isStrongLifecycleAction(action: string) {
  return action === "created"
    || action === "created_or_appended"
    || action === "deleted"
    || action === "renamed"
    || action === "moved";
}

function normalizePath(path?: string | null) {
  return (path ?? "").trim().replaceAll("/", "\\").replace(/\\+$/, "").toLowerCase();
}

function isFileLikePath(path: string) {
  return getLeafName(path).includes(".");
}

function getLeafName(path: string) {
  const segments = path.split("\\").filter(Boolean);
  return segments[segments.length - 1] ?? path;
}

function isProvisionalDocumentName(path: string) {
  return getProvisionalDocumentKind(path) !== null;
}

function getProvisionalDocumentKind(path: string) {
  const baseLeaf = getLeafName(path)
    .toLowerCase()
    .replace(/ \(\d+\)(?=\.[^.]+$)/, "");

  const provisionalKinds: Array<[string, RegExp[]]> = [
    ["text", [
      /^novo documento de texto\.txt$/,
      /^new text document\.txt$/
    ]],
    ["excel", [
      /^novo\(a\) planilha do microsoft excel.*\.xlsx$/,
      /^new microsoft excel worksheet.*\.xlsx$/
    ]],
    ["word", [
      /^novo\(a\) documento do microsoft word.*\.docx$/,
      /^new microsoft word document.*\.docx$/
    ]],
    ["powerpoint", [
      /^novo\(a\) apresenta.*microsoft powerpoint.*\.pptx$/,
      /^new microsoft powerpoint presentation.*\.pptx$/
    ]],
    ["publisher", [
      /^novo\(a\).*microsoft publisher document.*\.pub$/,
      /^new microsoft publisher document.*\.pub$/
    ]],
    ["bitmap", [
      /^nova imagem de bitmap.*\.bmp$/,
      /^new bitmap image.*\.bmp$/
    ]]
  ];

  return provisionalKinds.find(([, patterns]) =>
    patterns.some((pattern) => pattern.test(baseLeaf)))?.[0] ?? null;
}

function isProvisionalFolderName(path: string) {
  const leaf = getLeafName(path).toLowerCase();
  return leaf === "nova pasta"
    || leaf === "new folder";
}
