import { isTransientArtifactPath, isTransientContainerSegment } from "./event-noise.ts";
import { isLikelyInitialCreationEvent, promoteLikelyInitialCreations, shouldSuppressProvisionalCreate } from "./event-correlation-rules.ts";

export type FileAuditEvent = {
  id: string;
  timestampUtc: string;
  server: string;
  share: string;
  path: string;
  previousPath?: string | null;
  objectType: string;
  action: string;
  user: string;
  sid?: string | null;
  sourceHost?: string | null;
  sourceIp?: string | null;
  processName?: string | null;
  fileSizeBytes?: number | null;
  extension?: string | null;
  result: string;
  severity: string;
  source: string;
};

export type DisplayEvent = FileAuditEvent & {
  displayAction?: string;
  displayTarget?: string;
};

export function buildDisplayEvents(events: FileAuditEvent[]) {
  const ordered = deduplicateRawEvents(events).sort((left, right) => new Date(right.timestampUtc).getTime() - new Date(left.timestampUtc).getTime());
  const consumed = new Set<number>();
  const display: DisplayEvent[] = [];
  const emittedSemanticKeys = new Set<string>();
  const correlationWindowMs = 15_000;

  for (let index = 0; index < ordered.length; index++) {
    if (consumed.has(index)) {
      continue;
    }

    const current = ordered[index];
    const cluster = ordered
      .map((event, clusterIndex) => ({ event, clusterIndex }))
      .filter(({ clusterIndex }) => !consumed.has(clusterIndex))
      .filter(({ event }) => event.server === current.server && event.share === current.share)
      .filter(({ event }) => Math.abs(new Date(event.timestampUtc).getTime() - new Date(current.timestampUtc).getTime()) <= correlationWindowMs);

    if (isOperationalNoise(current)) {
      consumed.add(index);
      continue;
    }

    if (isTransientRenameNoise(current, cluster)) {
      consumed.add(index);
      continue;
    }

    const explicitTransition = tryBuildExplicitTransition(
      current,
      cluster.filter(({ event }) => !isOperationalNoise(event))
    );
    if (explicitTransition) {
      explicitTransition.consumedIndexes.forEach((clusterIndex) => consumed.add(clusterIndex));
      emittedSemanticKeys.add(getSemanticEventKey(explicitTransition.event));
      display.push(explicitTransition.event);
      continue;
    }

    if (isProvisionalDocumentNoise(current, ordered)) {
      consumed.add(index);
      continue;
    }

    if (isRedundantRenameAfterCreation(current, ordered)) {
      consumed.add(index);
      continue;
    }

    if (isRedundantDeletedNoise(current, cluster) || isRedundantCreationNoise(current, cluster)) {
      consumed.add(index);
      continue;
    }

    if (isRootOnlyNoise(current, cluster)) {
      consumed.add(index);
      continue;
    }

    if (isRedundantParentCreate(current, cluster)) {
      consumed.add(index);
      continue;
    }

    if (isUnknownUsnNoise(current, cluster)) {
      consumed.add(index);
      continue;
    }

    if (isRedundantChangedNoise(current, cluster)) {
      consumed.add(index);
      continue;
    }

    const displayEvent = {
      ...current,
      displayAction: formatAction(current.action, current),
      displayTarget: shouldShowActionTarget(current) ? getLeafName(current.path) : undefined
    } satisfies DisplayEvent;
    const semanticKey = getSemanticEventKey(displayEvent);

    if (emittedSemanticKeys.has(semanticKey)) {
      consumed.add(index);
      continue;
    }

    emittedSemanticKeys.add(semanticKey);
    display.push(displayEvent);
  }

  return refineDisplayEvents(display, ordered);
}

function refineDisplayEvents(events: DisplayEvent[], rawEvents: FileAuditEvent[]) {
  const withProvisionalCreates = normalizeProvisionalCreateTransitions(events);
  const withResolvedUsers = resolveUnknownDisplayUsers(withProvisionalCreates);
  const withSyntheticCreations = synthesizeLikelyCreations(withResolvedUsers, rawEvents);
  const withPromotedCreations = promoteLikelyInitialCreations(withSyntheticCreations);

  return withPromotedCreations.filter((event, _, allEvents) =>
    !isTransientDisplayNoise(event)
    && !isRedundantDisplayPermissionEcho(event, allEvents)
    && !isRedundantDisplayDeleted(event, allEvents)
    && !isRedundantDisplayProvisionalDelete(event, allEvents)
    && !isSuspiciousMoveEcho(event, allEvents)
    && !isRedundantDisplayFolderChangedEcho(event, allEvents)
    && !isRedundantDisplayProvisionalCreate(event, allEvents)
    && !isRedundantDisplayRenameAfterCreate(event, allEvents)
    && !isRedundantDisplayCreateEcho(event, allEvents)
    && !isRedundantDisplayChangedEcho(event, allEvents)
  );
}

function synthesizeLikelyCreations(events: DisplayEvent[], rawEvents: FileAuditEvent[]) {
  const synthetic = new Map<string, DisplayEvent>();
  const orderedRaw = [...rawEvents].sort((left, right) => new Date(left.timestampUtc).getTime() - new Date(right.timestampUtc).getTime());

  for (const rawEvent of orderedRaw) {
    const candidate = getSyntheticCreationCandidate(rawEvent, events, rawEvents);
    if (!candidate) {
      continue;
    }

    const key = [normalizePath(candidate.path), candidate.action, new Date(candidate.timestampUtc).toISOString()].join("|");
    if (!synthetic.has(key)) {
      synthetic.set(key, candidate);
    }
  }

  return [...events, ...synthetic.values()].sort((left, right) => new Date(right.timestampUtc).getTime() - new Date(left.timestampUtc).getTime());
}

function getSyntheticCreationCandidate(
  rawEvent: FileAuditEvent,
  displayEvents: DisplayEvent[],
  rawEvents: FileAuditEvent[]
) {
  if ((rawEvent.action === "moved" || rawEvent.action === "renamed")
    && rawEvent.previousPath
    && isFileLikePath(rawEvent.previousPath)
    && !isProvisionalDocumentName(rawEvent.previousPath)
    && !isProvisionalFolderName(rawEvent.previousPath)) {
    const originPath = normalizePath(rawEvent.previousPath);
    if (!hasDisplayCreation(displayEvents, originPath)
      && !hasLikelyInitialDisplayCreation(displayEvents, originPath)
      && !hasDisplayTransitionDestination(displayEvents, originPath, rawEvent.timestampUtc)
      && !hasEarlierStrongRawHistory(originPath, rawEvent.timestampUtc, rawEvents)) {
      return buildSyntheticCreationEvent(rawEvent, rawEvent.previousPath, -1_000);
    }
  }

  if ((rawEvent.action === "changed" || rawEvent.action === "modified") && rawEvent.source.includes("usn-journal") && isFileLikePath(rawEvent.path)) {
    const path = normalizePath(rawEvent.path);
    if (!hasDisplayEvent(displayEvents, rawEvent.id)
      && !hasDisplayCreation(displayEvents, path)
      && !hasEarlierStrongRawHistory(path, rawEvent.timestampUtc, rawEvents)
      && !hasEarlierRawChange(path, rawEvent.timestampUtc, rawEvents)
      && hasLaterLifecycleSignal(path, rawEvent.timestampUtc, rawEvents)) {
      return buildSyntheticCreationEvent(rawEvent, rawEvent.path);
    }
  }

  return null;
}

function buildSyntheticCreationEvent(event: FileAuditEvent, path: string, timestampOffsetMs = 0) {
  const timestamp = new Date(new Date(event.timestampUtc).getTime() + timestampOffsetMs).toISOString();

  return {
    ...event,
    id: `${event.id}-synthetic-created-${normalizePath(path)}`,
    timestampUtc: timestamp,
    path,
    previousPath: null,
    action: "created",
    displayAction: "Criação",
    displayTarget: getLeafName(path)
  } satisfies DisplayEvent;
}

function hasDisplayCreation(events: DisplayEvent[], path: string) {
  return events.some((event) =>
    normalizePath(event.path) === path
    && (event.action === "created" || event.action === "created_or_appended"));
}

function hasDisplayEvent(events: DisplayEvent[], id: string) {
  return events.some((event) => event.id === id);
}

function hasDisplayTransitionDestination(events: DisplayEvent[], path: string, beforeUtc: string) {
  const normalizedPath = normalizePath(path);
  const beforeTime = new Date(beforeUtc).getTime();

  return events.some((event) =>
    (event.action === "renamed" || event.action === "moved")
    && normalizePath(event.path) === normalizedPath
    && new Date(event.timestampUtc).getTime() <= beforeTime);
}

function hasLikelyInitialDisplayCreation(events: DisplayEvent[], path: string) {
  return events.some((event) =>
    normalizePath(event.path) === path
    && isLikelyInitialCreationEvent(event, events));
}

function hasEarlierRawChange(path: string, timestampUtc: string, rawEvents: FileAuditEvent[]) {
  const eventTime = new Date(timestampUtc).getTime();

  return rawEvents.some((candidate) =>
    new Date(candidate.timestampUtc).getTime() < eventTime
    && normalizePath(candidate.path) === path
    && (candidate.action === "changed" || candidate.action === "modified"));
}

function hasEarlierStrongRawHistory(path: string, timestampUtc: string, rawEvents: FileAuditEvent[]) {
  const eventTime = new Date(timestampUtc).getTime();

  return rawEvents.some((candidate) => {
    if (new Date(candidate.timestampUtc).getTime() >= eventTime) {
      return false;
    }

    const touchesPath = normalizePath(candidate.path) === path || normalizePath(candidate.previousPath) === path;
    if (!touchesPath) {
      return false;
    }

    if (!isStrongLifecycleAction(candidate.action)) {
      return false;
    }

    return !isIgnorableEarlierRawLifecycle(candidate, path);
  });
}

function hasLaterLifecycleSignal(path: string, timestampUtc: string, rawEvents: FileAuditEvent[]) {
  const eventTime = new Date(timestampUtc).getTime();

  return rawEvents.some((candidate) => {
    const candidateTime = new Date(candidate.timestampUtc).getTime();
    if (candidateTime < eventTime || candidateTime - eventTime > 300_000) {
      return false;
    }

    const touchesPath = normalizePath(candidate.path) === path || normalizePath(candidate.previousPath) === path;
    if (!touchesPath) {
      return false;
    }

    return candidate.action === "deleted"
      || candidate.action === "renamed"
      || candidate.action === "moved";
  });
}

function isIgnorableEarlierRawLifecycle(event: FileAuditEvent, targetPath: string) {
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

function normalizeProvisionalCreateTransitions(events: DisplayEvent[]) {
  return events.map((event) => {
    if ((event.action !== "renamed" && event.action !== "moved") || !event.previousPath) {
      return event;
    }

    const provisionalOrigin = isProvisionalDocumentName(event.previousPath) || isProvisionalFolderName(event.previousPath);
    if (!provisionalOrigin) {
      return event;
    }

    return {
      ...event,
      action: "created",
      previousPath: null,
      displayAction: "Criação",
      displayTarget: getLeafName(event.path)
    } satisfies DisplayEvent;
  });
}

function resolveUnknownDisplayUsers(events: DisplayEvent[]) {
  const userWindowMs = 60_000;

  return events.map((event) => {
    if (event.user !== "UNKNOWN") {
      return event;
    }

    const eventTime = new Date(event.timestampUtc).getTime();
    const userCandidate = events
      .filter((candidate) =>
        candidate.id !== event.id
        && candidate.server === event.server
        && candidate.share === event.share
        && candidate.user !== "UNKNOWN"
        && Math.abs(new Date(candidate.timestampUtc).getTime() - eventTime) <= userWindowMs
        && hasRelatedDisplayPath(event, candidate))
      .sort((left, right) =>
        Math.abs(new Date(left.timestampUtc).getTime() - eventTime)
        - Math.abs(new Date(right.timestampUtc).getTime() - eventTime))[0];

    return userCandidate ? { ...event, user: userCandidate.user } : event;
  });
}

function hasRelatedDisplayPath(left: FileAuditEvent, right: FileAuditEvent) {
  const leftPaths = [left.path, left.previousPath].filter(isPresentString).map((path) => normalizePath(path));
  const rightPaths = [right.path, right.previousPath].filter(isPresentString).map((path) => normalizePath(path));

  return leftPaths.some((leftPath) =>
    rightPaths.some((rightPath) =>
      leftPath === rightPath
      || rightPath.startsWith(`${leftPath}\\`)
      || leftPath.startsWith(`${rightPath}\\`)
      || normalizePath(getParentPath(leftPath)) === normalizePath(getParentPath(rightPath))));
}

function isPresentString(value: string | null | undefined): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

function isTransientDisplayNoise(event: DisplayEvent) {
  if (isTransientArtifactPath(event.path) || isTransientArtifactPath(event.previousPath ?? "")) {
    return true;
  }

  if ((event.action === "moved" || event.action === "renamed") && event.previousPath) {
    const currentParentSegments = normalizePath(getParentPath(event.path)).split("\\").filter(Boolean);
    const previousParentSegments = normalizePath(getParentPath(event.previousPath)).split("\\").filter(Boolean);

    if (currentParentSegments.some(isTransientContainerSegment) || previousParentSegments.some(isTransientContainerSegment)) {
      return true;
    }
  }

  return false;
}

function isRedundantDisplayPermissionEcho(event: DisplayEvent, allEvents: DisplayEvent[]) {
  if (event.action !== "permission_changed") {
    return false;
  }

  const eventTime = new Date(event.timestampUtc).getTime();
  const eventPriority = getDisplaySourcePriority(event);

  return allEvents.some((candidate) => {
    if (candidate.id === event.id || candidate.action !== "permission_changed") {
      return false;
    }

    if (normalizePath(candidate.path) !== normalizePath(event.path)
      || normalizeUser(candidate.user) !== normalizeUser(event.user)) {
      return false;
    }

    const candidateTime = new Date(candidate.timestampUtc).getTime();
    if (Math.abs(candidateTime - eventTime) > 2_000) {
      return false;
    }

    const candidatePriority = getDisplaySourcePriority(candidate);
    return candidatePriority > eventPriority
      || (candidatePriority === eventPriority
        && (candidateTime > eventTime
          || (candidateTime === eventTime && candidate.id.localeCompare(event.id) < 0)));
  });
}

function getDisplaySourcePriority(event: DisplayEvent) {
  if (event.source.includes("usn-journal+security-log")) {
    return 3;
  }

  if (event.source.includes("windows-security-log")) {
    return 2;
  }

  if (event.source.includes("usn-journal")) {
    return 1;
  }

  return 0;
}

function normalizeUser(user?: string | null) {
  return (user ?? "").trim().toLowerCase();
}

function isRedundantDisplayDeleted(event: DisplayEvent, allEvents: DisplayEvent[]) {
  if (event.action !== "deleted") {
    return false;
  }

  return allEvents.some((candidate) =>
    candidate.id !== event.id
    && (candidate.action === "renamed" || candidate.action === "moved")
    && Math.abs(new Date(candidate.timestampUtc).getTime() - new Date(event.timestampUtc).getTime()) <= 15_000
    && normalizePath(candidate.previousPath) === normalizePath(event.path));
}

function isRedundantDisplayProvisionalDelete(event: DisplayEvent, allEvents: DisplayEvent[]) {
  if (event.action !== "deleted") {
    return false;
  }

  const provisionalKind = getProvisionalDocumentKind(event.path);
  if (!provisionalKind || provisionalKind === "text") {
    return false;
  }

  const eventTime = new Date(event.timestampUtc).getTime();
  const eventParent = normalizePath(getParentPath(event.path));
  const eventExtension = getPathExtension(event.path);

  return allEvents.some((candidate) => {
    if (candidate.id === event.id) {
      return false;
    }

    const candidateTime = new Date(candidate.timestampUtc).getTime();
    if (Math.abs(candidateTime - eventTime) > 45_000) {
      return false;
    }

    if (candidate.action === "renamed" || candidate.action === "moved") {
      return normalizePath(candidate.previousPath) === normalizePath(event.path);
    }

    if (candidate.action !== "created" && candidate.action !== "created_or_appended") {
      return false;
    }

    if (isProvisionalDocumentName(candidate.path)) {
      return false;
    }

    return normalizePath(getParentPath(candidate.path)) === eventParent
      && getPathExtension(candidate.path) === eventExtension;
  });
}

function isSuspiciousMoveEcho(event: DisplayEvent, allEvents: DisplayEvent[]) {
  if (event.action !== "moved" || !event.previousPath) {
    return false;
  }

  const previousPath = event.previousPath;
  const currentParent = normalizePath(getParentPath(event.path));
  const previousParent = normalizePath(getParentPath(previousPath));
  if (!previousParent.startsWith(`${currentParent}\\`)) {
    return false;
  }

  const eventTime = new Date(event.timestampUtc).getTime();
  const deletedPrevious = allEvents.some((candidate) =>
    candidate.id !== event.id
    && candidate.action === "deleted"
    && Math.abs(new Date(candidate.timestampUtc).getTime() - eventTime) <= 30_000
    && normalizePath(candidate.path) === normalizePath(previousPath));

  const destinationSignalWindowMs = 8_000;
  const destinationSignal = allEvents.some((candidate) =>
    candidate.id !== event.id
    && candidate.action !== "deleted"
    && Math.abs(new Date(candidate.timestampUtc).getTime() - eventTime) <= destinationSignalWindowMs
    && normalizePath(candidate.path) === normalizePath(event.path));

  return deletedPrevious && !destinationSignal;
}

function isRedundantDisplayFolderChangedEcho(event: DisplayEvent, allEvents: DisplayEvent[]) {
  if (!isLikelyFolderPath(event.path)) {
    return false;
  }

  if (event.action !== "changed" && event.action !== "modified") {
    return false;
  }

  const folderPath = normalizePath(event.path);
  const eventTime = new Date(event.timestampUtc).getTime();
  return allEvents.some((candidate) =>
    candidate.id !== event.id
    && candidate.action === "moved"
    && Math.abs(new Date(candidate.timestampUtc).getTime() - eventTime) <= 30_000
    && normalizePath(getParentPath(candidate.path)) === folderPath);
}

function isRedundantDisplayProvisionalCreate(event: DisplayEvent, allEvents: DisplayEvent[]) {
  return shouldSuppressProvisionalCreate(event, allEvents);
}

function isRedundantDisplayRenameAfterCreate(event: DisplayEvent, allEvents: DisplayEvent[]) {
  if (event.action !== "renamed" && event.action !== "moved") {
    return false;
  }

  if (!event.previousPath) {
    return false;
  }

  if (!isProvisionalDocumentName(event.previousPath) && !isProvisionalFolderName(event.previousPath)) {
    return false;
  }

  return allEvents.some((candidate) =>
    candidate.id !== event.id
    && (candidate.action === "created" || candidate.action === "created_or_appended")
    && Math.abs(new Date(candidate.timestampUtc).getTime() - new Date(event.timestampUtc).getTime()) <= 15_000
    && normalizePath(candidate.path) === normalizePath(event.path));
}

function isRedundantDisplayCreateEcho(event: DisplayEvent, allEvents: DisplayEvent[]) {
  if (event.action !== "created" && event.action !== "created_or_appended") {
    return false;
  }

  const provisionalOrigin = isProvisionalDocumentName(event.path) || isProvisionalFolderName(event.path);
  if (!provisionalOrigin) {
    return false;
  }

  return allEvents.some((candidate) =>
    candidate.id !== event.id
    && (candidate.action === "renamed" || candidate.action === "moved")
    && Math.abs(new Date(candidate.timestampUtc).getTime() - new Date(event.timestampUtc).getTime()) <= 15_000
    && normalizePath(candidate.path) === normalizePath(event.path));
}

function isRedundantDisplayChangedEcho(event: DisplayEvent, allEvents: DisplayEvent[]) {
  if (event.action !== "changed" && event.action !== "modified") {
    return false;
  }

  return allEvents.some((candidate) =>
    candidate.id !== event.id
    && Math.abs(new Date(candidate.timestampUtc).getTime() - new Date(event.timestampUtc).getTime()) <= 15_000
    && (
      normalizePath(candidate.path) === normalizePath(event.path)
      || normalizePath(candidate.previousPath) === normalizePath(event.path)
    )
    && (candidate.action === "renamed"
      || candidate.action === "moved"
      || candidate.action === "created"
      || candidate.action === "created_or_appended"
      || candidate.action === "deleted"));
}

function deduplicateRawEvents(events: FileAuditEvent[]) {
  const grouped = new Map<string, FileAuditEvent>();

  for (const event of events) {
    const timestamp = new Date(event.timestampUtc);
    timestamp.setMilliseconds(0);
    const effectiveUser = event.action === "deleted" ? "" : event.user;
    const key = [
      effectiveUser,
      event.action,
      normalizePath(event.path),
      normalizePath(event.previousPath),
      timestamp.toISOString()
    ].join("|");

    const existing = grouped.get(key);

    if (existing) {
      grouped.set(key, mergeDuplicateEvent(existing, event));
    } else {
      grouped.set(key, event);
    }
  }

  return [...grouped.values()];
}

function mergeDuplicateEvent(left: FileAuditEvent, right: FileAuditEvent): FileAuditEvent {
  const winner = getEventWeight(right) > getEventWeight(left) ? right : left;
  const other = winner === right ? left : right;

  return {
    ...winner,
    user: isUnknownUser(winner.user) && !isUnknownUser(other.user) ? other.user : winner.user,
    sid: winner.sid ?? other.sid,
    sourceHost: winner.sourceHost ?? other.sourceHost,
    sourceIp: winner.sourceIp ?? other.sourceIp,
    processName: winner.processName ?? other.processName,
    source: mergeEventSources(winner.source, other.source)
  };
}

function isUnknownUser(user?: string | null) {
  return !user || user.trim().toUpperCase() === "UNKNOWN";
}

function mergeEventSources(left: string, right: string) {
  const hasUsn = left.includes("usn-journal") || right.includes("usn-journal");
  const hasSecurity = left.includes("security-log") || right.includes("security-log");

  if (hasUsn && hasSecurity) {
    return "usn-journal+security-log";
  }

  return left;
}

function getEventWeight(event: FileAuditEvent) {
  const sourceWeight = event.source.includes("usn-journal+security-log")
    ? 30
    : event.source.includes("usn-journal")
      ? 20
      : 10;
  const actionWeight = event.action === "moved" || event.action === "renamed"
    ? 30
    : event.action === "deleted"
      ? 20
      : event.action === "created_or_appended" || event.action === "created"
        ? 15
        : 5;

  return sourceWeight + actionWeight;
}

function getSemanticEventKey(event: FileAuditEvent) {
  const timestamp = new Date(event.timestampUtc);
  timestamp.setMilliseconds(0);

  const effectiveAction =
    event.action === "changed" || event.action === "modified"
      ? "changed"
      : event.action;
  const effectiveUser = event.action === "deleted" ? "" : event.user;

  return [
    effectiveUser,
    effectiveAction,
    normalizePath(event.path),
    normalizePath(event.previousPath),
    timestamp.toISOString()
  ].join("|");
}

function getParentPath(path: string) {
  const segments = path.split("\\");
  return segments.length > 1 ? segments.slice(0, -1).join("\\") : path;
}

function isRenameLikeAction(action: string) {
  return action === "renamed" || action === "changed" || action === "modified";
}

function isMove(previousPath: string, nextPath: string) {
  return getParentPath(previousPath).toLowerCase() !== getParentPath(nextPath).toLowerCase();
}

function isOperationalNoise(event: FileAuditEvent) {
  const path = event.path.toLowerCase();
  const previousPath = (event.previousPath ?? "").toLowerCase();

  return path.endsWith("\\appsettings.agent.json")
    || path.endsWith("\\agent-state.json")
    || path.endsWith("\\pending-events.ndjson")
    || path.includes("\\logs\\")
    || path.endsWith("\\logs")
    || isTransientArtifactPath(event.path)
    || isTransientArtifactPath(previousPath);
}

function isLikelyFolderPath(path: string) {
  return !getLeafName(path).includes(".");
}

function isFileLikePath(path: string) {
  return getLeafName(path).includes(".");
}

function getPathExtension(path: string) {
  const leaf = getLeafName(path);
  const index = leaf.lastIndexOf(".");
  return index >= 0 ? leaf.slice(index).toLowerCase() : "";
}

function isRootOnlyNoise(
  event: FileAuditEvent,
  cluster: Array<{ event: FileAuditEvent; clusterIndex: number }>
) {
  if (!isLikelyFolderPath(event.path)) {
    return false;
  }

  if (event.action === "deleted") {
    return false;
  }

  if (event.action === "created" || event.action === "created_or_appended") {
    return false;
  }

  if (isShareRootPath(event)) {
    return true;
  }

  return cluster.some(({ event: candidate }) =>
    candidate.id !== event.id
    && isFileLikePath(candidate.path)
    && normalizePath(getParentPath(candidate.path)) === normalizePath(event.path)
    && Math.abs(new Date(candidate.timestampUtc).getTime() - new Date(event.timestampUtc).getTime()) <= 15_000);
}

function isShareRootPath(event: FileAuditEvent) {
  const shareName = event.share.trim().toLowerCase();
  if (!shareName) {
    return false;
  }

  const leafName = getLeafName(event.path).toLowerCase();
  if (leafName !== shareName) {
    return false;
  }

  const parentPath = normalizePath(getParentPath(event.path));
  return /^[a-z]:$/i.test(parentPath)
    || /^\\\\[^\\]+$/.test(parentPath);
}

function isRedundantParentCreate(
  current: FileAuditEvent,
  cluster: Array<{ event: FileAuditEvent; clusterIndex: number }>
) {
  return current.source === "windows-security-log"
    && current.action === "created_or_appended"
    && cluster.some(({ event }) =>
      event.id !== current.id
      && event.server === current.server
      && event.share === current.share
      && event.user === current.user
      && (event.action === "deleted" || isRenameLikeAction(event.action))
      && getParentPath(event.path) === current.path);
}

function isUnknownUsnNoise(
  current: FileAuditEvent,
  cluster: Array<{ event: FileAuditEvent; clusterIndex: number }>
) {
  return current.source === "usn-journal"
    && current.user === "UNKNOWN"
    && (current.action === "changed" || current.action === "modified")
    && cluster.some(({ event }) =>
      event.id !== current.id
      && event.path === current.path
      && event.source !== "usn-journal"
      && event.action !== "changed"
      && event.action !== "modified");
}

function isProvisionalDocumentNoise(
  current: FileAuditEvent,
  ordered: FileAuditEvent[]
) {
  const provisionalKind = getProvisionalDocumentKind(current.path);
  const isProvisionalName = provisionalKind !== null || isProvisionalFolderName(current.path);
  if (!isProvisionalName) {
    return false;
  }

  if (current.action === "created" || current.action === "created_or_appended") {
    return shouldSuppressProvisionalCreate(current, ordered);
  }

  if (current.action === "deleted") {
    return false;
  }

  const currentPath = normalizePath(current.path);
  const currentTime = new Date(current.timestampUtc).getTime();

  return ordered.some((event) => {
    if (event.id === current.id) {
      return false;
    }

    if (Math.abs(new Date(event.timestampUtc).getTime() - currentTime) > 15_000) {
      return false;
    }

    if (!isFileLikePath(event.path) && !isLikelyFolderPath(event.path)) {
      return false;
    }

    return normalizePath(event.previousPath) === currentPath
      || normalizePath(event.path) === currentPath;
  });
}

function isRedundantRenameAfterCreation(
  current: FileAuditEvent,
  ordered: FileAuditEvent[]
) {
  if (current.action !== "renamed") {
    return false;
  }

  if (!current.previousPath) {
    return false;
  }

  const provisionalOrigin = isProvisionalDocumentName(current.previousPath) || isProvisionalFolderName(current.previousPath);
  if (!provisionalOrigin) {
    return false;
  }

  const currentPath = normalizePath(current.path);
  const currentTime = new Date(current.timestampUtc).getTime();

  return ordered.some((event) =>
    event.id !== current.id
    && Math.abs(new Date(event.timestampUtc).getTime() - currentTime) <= 15_000
    && (event.action === "created" || event.action === "created_or_appended")
    && normalizePath(event.path) === currentPath);
}

function isRedundantDeletedNoise(
  current: FileAuditEvent,
  cluster: Array<{ event: FileAuditEvent; clusterIndex: number }>
) {
  if (current.action !== "deleted") {
    return false;
  }

  const currentPath = normalizePath(current.path);
  return cluster.some(({ event }) => {
    if (event.id === current.id) {
      return false;
    }

    if (event.action !== "renamed" && event.action !== "moved") {
      return false;
    }

    return normalizePath(event.previousPath) === currentPath
      || normalizePath(event.path) === currentPath;
  });
}

function isRedundantCreationNoise(
  current: FileAuditEvent,
  cluster: Array<{ event: FileAuditEvent; clusterIndex: number }>
) {
  if (current.action !== "created_or_appended" && current.action !== "created") {
    return false;
  }

  const currentPath = normalizePath(current.path);
  return cluster.some(({ event }) => {
    if (event.id === current.id) {
      return false;
    }

    if (event.action !== "renamed" && event.action !== "moved" && event.action !== "created") {
      return false;
    }

    if ((event.action === "renamed" || event.action === "moved")
      && normalizePath(event.previousPath) === currentPath
      && !isProvisionalDocumentName(current.path)
      && !isProvisionalFolderName(current.path)) {
      return false;
    }

    return normalizePath(event.path) === currentPath
      || normalizePath(event.previousPath) === currentPath;
  });
}

function isTransientRenameNoise(
  current: FileAuditEvent,
  cluster: Array<{ event: FileAuditEvent; clusterIndex: number }>
) {
  if (current.action !== "renamed" && current.action !== "moved") {
    return false;
  }

  const touchesTransientArtifact =
    isTransientArtifactPath(current.path)
    || isTransientArtifactPath(current.previousPath ?? "");

  if (!touchesTransientArtifact) {
    return false;
  }

  return cluster.some(({ event }) =>
    event.id !== current.id
    && event.action === "deleted"
    && isFileLikePath(event.path)
    && (
      normalizePath(event.path).startsWith(normalizePath(current.previousPath))
      || normalizePath(event.path).startsWith(normalizePath(current.path))
      || normalizePath(getParentPath(event.path)) === normalizePath(current.previousPath)
    ));
}

function isRedundantChangedNoise(
  current: FileAuditEvent,
  cluster: Array<{ event: FileAuditEvent; clusterIndex: number }>
) {
  if (!current.source.includes("usn-journal")) {
    return false;
  }

  if (current.action !== "changed" && current.action !== "modified") {
    return false;
  }

  const currentPath = normalizePath(current.path);
  const currentTimestamp = new Date(current.timestampUtc).getTime();
  const strongerEvent = cluster.find(({ event }) => {
    if (event.id === current.id) {
      return false;
    }

    const touchesSamePath =
      normalizePath(event.path) === currentPath
      || normalizePath(event.previousPath) === currentPath;

    if (!touchesSamePath) {
      return false;
    }

    if (event.action === "renamed" || event.action === "moved" || event.action === "deleted") {
      return true;
    }

    if ((event.action === "created" || event.action === "created_or_appended")
      && isFileLikePath(event.path)) {
      return true;
    }

    if (!event.source.includes("usn-journal")) {
      return false;
    }

    if (event.action !== "changed" && event.action !== "modified") {
      return false;
    }

    return new Date(event.timestampUtc).getTime() > currentTimestamp;
  });

  return Boolean(strongerEvent);
}

function tryBuildExplicitTransition(
  current: FileAuditEvent,
  relevant: Array<{ event: FileAuditEvent; clusterIndex: number }>
) {
  if (!current.previousPath) {
    return null;
  }

  if (current.action !== "renamed" && current.action !== "moved") {
    return null;
  }

  const previousPath = current.previousPath ?? "";
  const isFileTransition = isFileLikePath(current.path) && isFileLikePath(previousPath);
  const isFolderTransition = isLikelyFolderPath(current.path) && isLikelyFolderPath(previousPath);

  if (!isFileTransition && !isFolderTransition) {
    return null;
  }

  const nextPath = current.path;
  const isProvisionalOrigin = isProvisionalDocumentName(previousPath) || isProvisionalFolderName(previousPath);
  const action = isMove(previousPath, nextPath) ? "moved" : "renamed";
  const displayAction = isProvisionalOrigin
    ? "Criação"
    : action === "moved"
      ? "Movido"
      : "Renomeado";
  const consumedIndexes = relevant
    .filter(({ event }) => shouldConsumeTransitionEvent(event, previousPath, nextPath, isProvisionalOrigin))
    .map(({ clusterIndex }) => clusterIndex);

  return {
    consumedIndexes,
    event: {
      ...current,
      id: `${current.id}-${action}-explicit`,
      action: isProvisionalOrigin ? "created" : action,
      previousPath: isProvisionalOrigin ? null : previousPath,
      path: nextPath,
      displayAction,
      displayTarget: getLeafName(nextPath)
    } satisfies DisplayEvent
  };
}

function shouldConsumeTransitionEvent(
  event: FileAuditEvent,
  previousPath: string,
  nextPath: string,
  isProvisionalOrigin: boolean
) {
  const normalizedPath = normalizePath(event.path);
  const normalizedPrevious = normalizePath(event.previousPath);
  const normalizedTransitionPrevious = normalizePath(previousPath);
  const normalizedTransitionNext = normalizePath(nextPath);

  if (event.action === "renamed" || event.action === "moved") {
    return normalizedPath === normalizedTransitionNext
      && normalizedPrevious === normalizedTransitionPrevious;
  }

  if (!isProvisionalOrigin
    && (event.action === "created" || event.action === "created_or_appended")
    && normalizedPath === normalizedTransitionPrevious) {
    return false;
  }

  return normalizedPath === normalizedTransitionPrevious
    || normalizedPath === normalizedTransitionNext
    || normalizedPrevious === normalizedTransitionPrevious
    || normalizedPrevious === normalizedTransitionNext
    || (event.action === "created_or_appended" && normalizedPath === normalizedTransitionNext);
}

function getExtension(value: string) {
  const index = value.lastIndexOf(".");
  return index >= 0 ? value.slice(index) : "";
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

function normalizePath(path?: string | null) {
  return (path ?? "").trim().replaceAll("/", "\\").replace(/\\+$/, "").toLowerCase();
}

function getLeafName(path: string) {
  const segments = path.split("\\").filter(Boolean);
  return segments[segments.length - 1] ?? path;
}

function formatAction(action: string, event?: FileAuditEvent) {
  if (action === "renamed" && event?.previousPath && isMove(event.previousPath, event.path)) {
    return "Movido";
  }

  const labels: Record<string, string> = {
    accessed: "Acessado",
    changed: "Alterado",
    created: "Criação",
    created_or_appended: "Criação",
    deleted: "Excluído",
    modified: "Alterado",
    permission_changed: "Permissão alterada",
    renamed: "Renomeado",
    renamed_new: "Renomeado",
    renamed_old: "Renomeado",
    moved: "Movido"
  };

  return labels[action] ?? action;
}

function shouldShowActionTarget(_event: FileAuditEvent) {
  return true;
}
