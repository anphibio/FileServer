using System.Text.RegularExpressions;

namespace FileServerMonitor.Core;

public sealed record FileAuditDisplayEvent(
    Guid Id,
    DateTimeOffset TimestampUtc,
    string Server,
    string Share,
    string Path,
    string? PreviousPath,
    string ObjectType,
    string Action,
    string User,
    string? Sid,
    string? SourceHost,
    string? SourceIp,
    string? ProcessName,
    long? FileSizeBytes,
    string? Extension,
    string Result,
    string Severity,
    string Source,
    string DisplayAction,
    string DisplayTarget);

public sealed class EventTimelineProjector
{
    private static readonly TimeSpan CorrelationWindow = TimeSpan.FromSeconds(15);

    public static bool IsProvisionalDocumentPath(string path)
    {
        return IsProvisionalDocumentName(path);
    }

    public IReadOnlyCollection<FileAuditDisplayEvent> BuildDisplayEvents(IReadOnlyCollection<FileAuditEvent> events)
    {
        var ordered = DeduplicateRawEvents(events)
            .OrderByDescending(item => item.TimestampUtc)
            .ToArray();
        var consumed = new HashSet<int>();
        var emittedSemanticKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var display = new List<FileAuditDisplayEvent>();

        for (var index = 0; index < ordered.Length; index++)
        {
            if (consumed.Contains(index))
            {
                continue;
            }

            var current = ordered[index];
            var clusterAll = ordered
                .Select((Event, Index) => new ClusterItem(Event, Index))
                .Where(item => string.Equals(item.Event.Server, current.Server, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.Event.Share, current.Share, StringComparison.OrdinalIgnoreCase))
                .Where(item => (item.Event.TimestampUtc - current.TimestampUtc).Duration() <= CorrelationWindow)
                .ToArray();
            var cluster = clusterAll
                .Where(item => !consumed.Contains(item.Index))
                .ToArray();

            if (IsOperationalNoise(current)
                || IsTransientRenameNoise(current, cluster)
                || IsProvisionalDocumentNoise(current, ordered)
                || IsRedundantRenameAfterCreation(current, ordered)
                || IsRedundantDeletedNoise(current, cluster)
                || IsRedundantCreationNoise(current, cluster)
                || IsRootOnlyNoise(current, cluster)
                || IsRedundantParentCreate(current, cluster)
                || IsUnknownUsnNoise(current, cluster, clusterAll)
                || IsRedundantChangedNoise(current, cluster, clusterAll))
            {
                consumed.Add(index);
                continue;
            }

            var transition = TryBuildExplicitTransition(
                current,
                cluster.Where(item => !IsOperationalNoise(item.Event)).ToArray(),
                clusterAll.Where(item => !IsOperationalNoise(item.Event)).ToArray(),
                ordered)
                ?? TryBuildSecurityLogRenameTransition(current, cluster);
            if (transition is not null)
            {
                foreach (var consumedIndex in transition.ConsumedIndexes)
                {
                    consumed.Add(consumedIndex);
                }

                var semanticKey = GetSemanticEventKey(transition.Event);
                if (emittedSemanticKeys.Add(semanticKey))
                {
                    display.Add(transition.Event);
                }

                continue;
            }

            var displayEvent = ToDisplayEvent(current);
            var key = GetSemanticEventKey(displayEvent);
            if (!emittedSemanticKeys.Add(key))
            {
                consumed.Add(index);
                continue;
            }

            display.Add(displayEvent);
        }

        return RefineDisplayEvents(display, ordered);
    }

    private static IReadOnlyCollection<FileAuditDisplayEvent> RefineDisplayEvents(
        IReadOnlyCollection<FileAuditDisplayEvent> events,
        IReadOnlyCollection<FileAuditEvent> rawEvents)
    {
        var normalized = NormalizeProvisionalCreateTransitions(events).ToArray();
        var resolvedUsers = ResolveUnknownDisplayUsers(normalized).ToArray();
        var normalizedSecurityTextAppends = NormalizeSecurityTextAppendCreates(resolvedUsers).ToArray();
        var syntheticCreations = SynthesizeLikelyCreations(normalizedSecurityTextAppends, rawEvents).ToArray();
        var syntheticDescendantMoves = SynthesizeLikelyDescendantMoves(syntheticCreations).ToArray();
        var syntheticDeletes = SynthesizeLikelyDescendantDeletions(syntheticDescendantMoves).ToArray();
        var promotedCreations = PromoteLikelyInitialCreations(syntheticDeletes).ToArray();

        var filtered = promotedCreations
            .Where(item =>
                !IsTransientDisplayNoise(item)
                && !IsRedundantDisplayPermissionEcho(item, promotedCreations)
                && !IsPermissionEchoDuringDelete(item, promotedCreations)
                && !IsRedundantDisplayDeleted(item, promotedCreations)
                && !IsRedundantDisplayDeletedDuplicate(item, promotedCreations)
                && !IsRedundantDisplayProvisionalDelete(item, promotedCreations)
                && !IsSuspiciousMoveEcho(item, promotedCreations)
                && !IsRedundantDisplayFolderChangedEcho(item, promotedCreations)
                && !ShouldSuppressProvisionalCreate(item, promotedCreations)
                && !IsRedundantDisplayRenameAfterCreate(item, promotedCreations)
                && !IsRedundantDisplayCreateEcho(item, promotedCreations)
                && !IsRedundantDisplayCreatedDuplicate(item, promotedCreations)
                && !IsRedundantDisplayAccessedEcho(item, promotedCreations)
                && !IsRedundantDisplayChangedEcho(item, promotedCreations));

        return CollapseSemanticDisplayDuplicates(filtered)
            .OrderByDescending(item => item.TimestampUtc)
            .ToArray();
    }

    private static IEnumerable<FileAuditDisplayEvent> CollapseSemanticDisplayDuplicates(IEnumerable<FileAuditDisplayEvent> events)
    {
        return events
            .GroupBy(GetSemanticEventKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(GetEventWeight)
                .ThenByDescending(item => item.TimestampUtc)
                .First());
    }

    private static IEnumerable<FileAuditDisplayEvent> SynthesizeLikelyCreations(
        IReadOnlyCollection<FileAuditDisplayEvent> events,
        IReadOnlyCollection<FileAuditEvent> rawEvents)
    {
        var synthetic = new Dictionary<string, FileAuditDisplayEvent>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawEvent in rawEvents.OrderBy(item => item.TimestampUtc))
        {
            var candidate = GetSyntheticCreationCandidate(rawEvent, events, rawEvents);
            if (candidate is null)
            {
                continue;
            }

            var key = string.Join("|", NormalizePath(candidate.Path), candidate.Action, candidate.TimestampUtc.ToString("O"));
            synthetic.TryAdd(key, candidate);
        }

        return events.Concat(synthetic.Values).OrderByDescending(item => item.TimestampUtc);
    }

    private static FileAuditDisplayEvent? GetSyntheticCreationCandidate(
        FileAuditEvent rawEvent,
        IReadOnlyCollection<FileAuditDisplayEvent> displayEvents,
        IReadOnlyCollection<FileAuditEvent> rawEvents)
    {
        if (rawEvent.Action == "renamed"
            && !string.IsNullOrWhiteSpace(rawEvent.PreviousPath)
            && rawEvent.Source.Contains("security-log", StringComparison.OrdinalIgnoreCase)
            && IsFileLikePath(rawEvent.PreviousPath)
            && !IsProvisionalDocumentName(rawEvent.PreviousPath)
            && !IsProvisionalFolderName(rawEvent.PreviousPath))
        {
            var originPath = NormalizePath(rawEvent.PreviousPath);
            if (!HasDisplayCreation(displayEvents, originPath)
                && !HasLikelyInitialDisplayCreation(displayEvents, originPath)
                && !HasDisplayTransitionDestination(displayEvents, originPath, rawEvent.TimestampUtc)
                && !HasEarlierStrongRawHistory(originPath, rawEvent.TimestampUtc, rawEvents)
                && !HasNearbyRawDelete(originPath, rawEvent.TimestampUtc, rawEvents))
            {
                return BuildSyntheticCreationEvent(rawEvent, rawEvent.PreviousPath, TimeSpan.FromSeconds(-1));
            }
        }

        if ((rawEvent.Action == "changed" || rawEvent.Action == "modified")
            && rawEvent.Source.Contains("usn-journal", StringComparison.OrdinalIgnoreCase)
            && IsFileLikePath(rawEvent.Path))
        {
            var path = NormalizePath(rawEvent.Path);
            if (!displayEvents.Any(item => item.Id == rawEvent.Id)
                && !HasDisplayCreation(displayEvents, path)
                && !HasNearbyDisplayTransition(displayEvents, path, rawEvent.TimestampUtc)
                && !HasEarlierStrongRawHistory(path, rawEvent.TimestampUtc, rawEvents)
                && !HasEarlierRawChange(path, rawEvent.TimestampUtc, rawEvents)
                && !HasNearbyRawTransition(path, rawEvent.TimestampUtc, rawEvents)
                && (rawEvent.Source.Equals("usn-journal", StringComparison.OrdinalIgnoreCase)
                    || HasNearbySiblingCreationSignal(rawEvent, rawEvents))
                && HasLaterLifecycleSignal(path, rawEvent.TimestampUtc, rawEvents))
            {
                return BuildSyntheticCreationEvent(rawEvent, rawEvent.Path);
            }
        }

        if ((rawEvent.Action == "changed" || rawEvent.Action == "modified")
            && (rawEvent.ObjectType is "folder" or "directory" || IsLikelyFolderPath(rawEvent.Path)))
        {
            var path = NormalizePath(rawEvent.Path);
            if (!HasDisplayCreation(displayEvents, path)
                && !HasEarlierNonDeletedStrongRawHistory(path, rawEvent.TimestampUtc, rawEvents)
                && HasNearbyChildCreationSignal(rawEvent, rawEvents))
            {
                return BuildSyntheticCreationEvent(rawEvent, rawEvent.Path);
            }
        }

        if (rawEvent.Action == "modified"
            && rawEvent.Source == "windows-security-log"
            && IsFileLikePath(rawEvent.Path))
        {
            var path = NormalizePath(rawEvent.Path);
            if (!HasDisplayCreation(displayEvents, path)
                && !HasNearbyDisplayTransition(displayEvents, path, rawEvent.TimestampUtc)
                && !HasEarlierStrongRawHistory(path, rawEvent.TimestampUtc, rawEvents)
                && !HasEarlierRawChange(path, rawEvent.TimestampUtc, rawEvents)
                && !HasNearbyRawTransition(path, rawEvent.TimestampUtc, rawEvents)
                && !HasLaterLifecycleSignal(path, rawEvent.TimestampUtc, rawEvents)
                && !HasLaterAccessEchoSignal(path, rawEvent.TimestampUtc, rawEvents)
                && HasNearbySiblingCreationSignal(rawEvent, rawEvents))
            {
                return BuildSyntheticCreationEvent(rawEvent, rawEvent.Path);
            }
        }

        return null;
    }

    private static FileAuditDisplayEvent BuildSyntheticCreationEvent(
        FileAuditEvent source,
        string path,
        TimeSpan? timestampOffset = null)
    {
        return ToDisplayEvent(source with
        {
            Id = StableSyntheticGuid(source.Id, $"created:{NormalizePath(path)}"),
            TimestampUtc = source.TimestampUtc + (timestampOffset ?? TimeSpan.Zero),
            Path = path,
            PreviousPath = null,
            Action = "created"
        });
    }

    private static IEnumerable<FileAuditDisplayEvent> SynthesizeLikelyDescendantDeletions(IReadOnlyCollection<FileAuditDisplayEvent> events)
    {
        var synthetic = new Dictionary<string, FileAuditDisplayEvent>(StringComparer.OrdinalIgnoreCase);

        foreach (var deleteEvent in events.Where(item => item.Action == "deleted" && IsFolderEvent(item)))
        {
            var deleteTime = deleteEvent.TimestampUtc;
            foreach (var descendantPath in GetKnownLiveDescendantsBeforeDelete(deleteEvent, events))
            {
                if (HasExplicitDescendantDelete(descendantPath, deleteTime, events))
                {
                    continue;
                }

                var candidate = ToDisplayEvent(ToAuditEvent(deleteEvent) with
                {
                    Id = StableSyntheticGuid(deleteEvent.Id, $"deleted:{NormalizePath(descendantPath)}"),
                    Path = descendantPath,
                    PreviousPath = null,
                    ObjectType = IsFileLikePath(descendantPath) ? "file" : "folder",
                    Action = "deleted"
                });
                synthetic[GetSyntheticDeletionKey(candidate)] = candidate;
            }
        }

        return events.Concat(synthetic.Values).OrderByDescending(item => item.TimestampUtc);
    }

    private static IEnumerable<FileAuditDisplayEvent> SynthesizeLikelyDescendantMoves(IReadOnlyCollection<FileAuditDisplayEvent> events)
    {
        var synthetic = new Dictionary<string, FileAuditDisplayEvent>(StringComparer.OrdinalIgnoreCase);

        foreach (var moveEvent in events.Where(item =>
                     item.Action is "moved" or "renamed"
                     && IsFolderEvent(item)
                     && !string.IsNullOrWhiteSpace(item.PreviousPath)))
        {
            var previousRoot = moveEvent.PreviousPath!;
            foreach (var descendantPath in GetKnownLiveDescendantsBeforeTimestamp(previousRoot, moveEvent.TimestampUtc, events, moveEvent.Id))
            {
                var relocatedPath = TryRelocatePath(descendantPath, previousRoot, moveEvent.Path);
                var derivedAction = IsMove(descendantPath, relocatedPath) ? "moved" : "renamed";
                if (NormalizePath(relocatedPath) == NormalizePath(descendantPath))
                {
                    continue;
                }

                if (HasExplicitDescendantTransition(descendantPath, relocatedPath, moveEvent.TimestampUtc, events))
                {
                    continue;
                }

                var candidate = ToDisplayEvent(ToAuditEvent(moveEvent) with
                {
                    Id = StableSyntheticGuid(moveEvent.Id, $"{derivedAction}:{NormalizePath(descendantPath)}->{NormalizePath(relocatedPath)}"),
                    Path = relocatedPath,
                    PreviousPath = descendantPath,
                    ObjectType = IsFileLikePath(relocatedPath) ? "file" : "folder",
                    Action = derivedAction
                });
                synthetic[GetSyntheticTransitionKey(candidate)] = candidate;
            }
        }

        return events.Concat(synthetic.Values).OrderByDescending(item => item.TimestampUtc);
    }

    private static IEnumerable<string> GetKnownLiveDescendantsBeforeDelete(
        FileAuditDisplayEvent folderDelete,
        IReadOnlyCollection<FileAuditDisplayEvent> events)
    {
        return GetKnownLiveDescendantsBeforeTimestamp(folderDelete.Path, folderDelete.TimestampUtc, events, folderDelete.Id);
    }

    private static IEnumerable<string> GetKnownLiveDescendantsBeforeTimestamp(
        string folderPath,
        DateTimeOffset timestampUtc,
        IReadOnlyCollection<FileAuditDisplayEvent> events,
        Guid excludedEventId)
    {
        var normalizedFolderPath = NormalizePath(folderPath);
        var livePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ordered = events
            .Where(item => item.Id != excludedEventId)
            .Where(item => item.TimestampUtc < timestampUtc
                || item.TimestampUtc == timestampUtc)
            .OrderBy(item => item.TimestampUtc)
            .ThenBy(GetLifecycleOrderingForLivePathTracking);

        foreach (var item in ordered)
        {
            if (item.Action is "renamed" or "moved")
            {
                if (!string.IsNullOrWhiteSpace(item.PreviousPath))
                {
                    RelocateKnownLiveDescendants(livePaths, item.PreviousPath, item.Path);
                    livePaths.Remove(NormalizePath(item.PreviousPath));
                }

                livePaths[NormalizePath(item.Path)] = item.Path;
                continue;
            }

            if (item.Action is "created" or "created_or_appended")
            {
                livePaths[NormalizePath(item.Path)] = item.Path;
                continue;
            }

            if (item.Action == "deleted")
            {
                if (IsFolderEvent(item))
                {
                    RemoveKnownLivePathTree(livePaths, item.Path);
                }
                else
                {
                    livePaths.Remove(NormalizePath(item.Path));
                }
            }
        }

        return livePaths.Values.Where(path => IsDescendantPath(path, normalizedFolderPath)).ToArray();
    }

    private static int GetLifecycleOrderingForLivePathTracking(FileAuditDisplayEvent item)
    {
        return item.Action switch
        {
            "created" or "created_or_appended" => 0,
            "changed" or "modified" or "permission_changed" or "accessed" => 1,
            "renamed" or "moved" when !IsFolderEvent(item) => 2,
            "renamed" when IsFolderEvent(item) => 3,
            "moved" when IsFolderEvent(item) => 4,
            "deleted" => 5,
            _ => 6
        };
    }

    private static void RemoveKnownLivePathTree(Dictionary<string, string> livePaths, string deletedPath)
    {
        var deletedRoot = NormalizePath(deletedPath);
        foreach (var item in livePaths.ToArray())
        {
            if (item.Key == deletedRoot || IsDescendantPath(item.Value, deletedRoot))
            {
                livePaths.Remove(item.Key);
            }
        }
    }

    private static void RelocateKnownLiveDescendants(Dictionary<string, string> livePaths, string previousPath, string nextPath)
    {
        var previousRoot = NormalizePath(previousPath);
        var nextRoot = NormalizePath(nextPath);
        var moved = new List<(string OldKey, string NewPath)>();

        foreach (var item in livePaths)
        {
            if (IsDescendantPath(item.Value, previousRoot))
            {
                var suffix = item.Value.Length >= previousPath.Length ? item.Value[previousPath.Length..] : "";
                moved.Add((item.Key, $"{nextPath}{suffix}"));
            }
        }

        foreach (var item in moved)
        {
            livePaths.Remove(item.OldKey);
            livePaths[NormalizePath(item.NewPath)] = item.NewPath;
        }

        if (livePaths.Remove(previousRoot))
        {
            livePaths[nextRoot] = nextPath;
        }
    }

    private static bool HasExplicitDescendantDelete(string path, DateTimeOffset deleteTime, IReadOnlyCollection<FileAuditDisplayEvent> events)
    {
        return events.Any(item =>
            item.Action == "deleted"
            && !IsSyntheticDelete(item)
            && NormalizePath(item.Path) == NormalizePath(path)
            && (item.TimestampUtc - deleteTime).Duration() <= TimeSpan.FromSeconds(60));
    }

    private static string GetSyntheticDeletionKey(FileAuditDisplayEvent item)
    {
        return string.Join("|", "synthetic-deleted", NormalizePath(item.Path), TrimToSecond(item.TimestampUtc).ToString("O"));
    }

    private static bool HasExplicitDescendantTransition(
        string previousPath,
        string nextPath,
        DateTimeOffset moveTime,
        IReadOnlyCollection<FileAuditDisplayEvent> events)
    {
        return events.Any(item =>
            item.Action is "moved" or "renamed"
            && NormalizePath(item.Path) == NormalizePath(nextPath)
            && NormalizePath(item.PreviousPath) == NormalizePath(previousPath)
            && (item.TimestampUtc - moveTime).Duration() <= TimeSpan.FromSeconds(60));
    }

    private static string GetSyntheticTransitionKey(FileAuditDisplayEvent item)
    {
        return string.Join("|", $"synthetic-{item.Action}", NormalizePath(item.PreviousPath), NormalizePath(item.Path), TrimToSecond(item.TimestampUtc).ToString("O"));
    }

    private static IEnumerable<FileAuditDisplayEvent> NormalizeProvisionalCreateTransitions(IEnumerable<FileAuditDisplayEvent> events)
    {
        var all = events.ToArray();
        return all.Select(item =>
        {
            if (item.Action != "renamed"
                || string.IsNullOrWhiteSpace(item.PreviousPath)
                || !IsMaterializedProvisionalDocumentRename(item.PreviousPath, item.Path))
            {
                return item;
            }

            if (HasDisplayTransitionDestination(all, NormalizePath(item.PreviousPath), item.TimestampUtc)
                || HasDisplayCreation(all, NormalizePath(item.PreviousPath))
                || HasEarlierDisplayCreation(all, NormalizePath(item.PreviousPath), item.TimestampUtc))
            {
                return item;
            }

            return item with
            {
                Action = "created",
                PreviousPath = null,
                DisplayAction = "Criação",
                DisplayTarget = GetLeafName(item.Path)
            };
        });
    }

    private static IEnumerable<FileAuditDisplayEvent> NormalizeSecurityTextAppendCreates(IEnumerable<FileAuditDisplayEvent> events)
    {
        var all = events.ToArray();
        return all.Select(item =>
        {
            if (item.Action is not ("created" or "created_or_appended")
                || !item.Source.Equals("windows-security-log", StringComparison.OrdinalIgnoreCase)
                || !IsSecurityTextAppendCandidate(item.Path)
                || HasNearbyUsnCreation(item, all)
                || (!HasLaterDisplayAccessEcho(item, all)
                    && !HasNearbyDisplayModification(item, all)
                    && !HasNearbyDisplayTransition(item, all)))
            {
                return item;
            }

            return item with
            {
                Action = "modified",
                DisplayAction = "Alterado",
                DisplayTarget = GetLeafName(item.Path)
            };
        });
    }

    private static bool IsSecurityTextAppendCandidate(string path)
    {
        return GetProvisionalDocumentKind(path) == "text"
            || string.Equals(GetPathExtension(path), ".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasNearbyDisplayModification(FileAuditDisplayEvent item, IReadOnlyCollection<FileAuditDisplayEvent> all)
    {
        return all.Any(candidate =>
            candidate.Id != item.Id
            && candidate.Action is "changed" or "modified"
            && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(10)
            && PathsReferToSameItem(candidate.Path, item.Path));
    }

    private static bool HasNearbyDisplayTransition(FileAuditDisplayEvent item, IReadOnlyCollection<FileAuditDisplayEvent> all)
    {
        var path = NormalizePath(item.Path);
        return all.Any(candidate =>
            candidate.Id != item.Id
            && candidate.Action is "renamed" or "moved"
            && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(15)
            && (NormalizePath(candidate.Path) == path || NormalizePath(candidate.PreviousPath) == path));
    }

    private static bool HasNearbyUsnCreation(FileAuditDisplayEvent item, IReadOnlyCollection<FileAuditDisplayEvent> all)
    {
        return all.Any(candidate =>
            candidate.Id != item.Id
            && candidate.Source.Contains("usn-journal", StringComparison.OrdinalIgnoreCase)
            && candidate.Action is "created" or "created_or_appended"
            && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(5)
            && PathsReferToSameItem(candidate.Path, item.Path));
    }

    private static bool HasLaterDisplayAccessEcho(FileAuditDisplayEvent item, IReadOnlyCollection<FileAuditDisplayEvent> all)
    {
        return all.Any(candidate =>
            candidate.Id != item.Id
            && candidate.Action == "accessed"
            && candidate.TimestampUtc > item.TimestampUtc
            && candidate.TimestampUtc - item.TimestampUtc >= TimeSpan.FromSeconds(2)
            && candidate.TimestampUtc - item.TimestampUtc <= TimeSpan.FromSeconds(30)
            && PathsReferToSameItem(candidate.Path, item.Path));
    }

    private static IEnumerable<FileAuditDisplayEvent> ResolveUnknownDisplayUsers(IEnumerable<FileAuditDisplayEvent> events)
    {
        var all = events.ToArray();
        return all.Select(item =>
        {
            if (!IsUnknownUser(item.User))
            {
                return item;
            }

            var candidate = all
                .Where(candidate => candidate.Id != item.Id
                    && string.Equals(candidate.Server, item.Server, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.Share, item.Share, StringComparison.OrdinalIgnoreCase)
                    && !IsUnknownUser(candidate.User)
                    && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(60)
                    && HasRelatedDisplayPath(item, candidate))
                .OrderBy(candidate => Math.Abs((candidate.TimestampUtc - item.TimestampUtc).TotalMilliseconds))
                .FirstOrDefault();

            return candidate is null ? item : item with { User = candidate.User };
        });
    }

    private static IEnumerable<FileAuditDisplayEvent> PromoteLikelyInitialCreations(IEnumerable<FileAuditDisplayEvent> events)
    {
        var all = events.ToArray();
        return all.Select(item => IsLikelyInitialCreationEvent(item, all)
            ? item with { Action = "created", DisplayAction = "Criação", DisplayTarget = GetLeafName(item.Path) }
            : item);
    }

    private static bool IsLikelyInitialCreationEvent(FileAuditDisplayEvent item, IReadOnlyCollection<FileAuditDisplayEvent> events)
    {
        if (item.Action is not ("changed" or "modified") || !item.Source.Contains("usn-journal", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IsFileLikePath(item.Path))
        {
            return false;
        }

        if (HasNearbyDisplayAppendWriteEvidence(item, events))
        {
            return false;
        }

        var path = NormalizePath(item.Path);
        if (events.Any(candidate =>
                candidate.Id != item.Id
                && candidate.TimestampUtc <= item.TimestampUtc
                && item.TimestampUtc - candidate.TimestampUtc <= TimeSpan.FromSeconds(10)
                && NormalizePath(candidate.Path) == path
                && candidate.Action is "created" or "created_or_appended"))
        {
            return false;
        }

        var earlier = events.Where(candidate =>
            candidate.Id != item.Id
            && candidate.TimestampUtc < item.TimestampUtc
            && (NormalizePath(candidate.Path) == path || NormalizePath(candidate.PreviousPath) == path));

        if (earlier.Any(candidate => NormalizePath(candidate.Path) == path && candidate.Action is not "accessed"))
        {
            return false;
        }

        if (earlier.Any(candidate => IsStrongLifecycleAction(candidate.Action) && !IsIgnorableEarlierLifecycle(candidate, path)))
        {
            return false;
        }

        if (events.Any(candidate =>
                candidate.Id != item.Id
                && candidate.Action is "renamed" or "moved"
                && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(15)
                && (NormalizePath(candidate.Path) == path || NormalizePath(candidate.PreviousPath) == path)))
        {
            return false;
        }

        var hasEarlierIgnorableLifecycle = earlier.Any(candidate => IsIgnorableEarlierLifecycle(candidate, path));

        if (!item.Source.Equals("usn-journal", StringComparison.OrdinalIgnoreCase)
            && !hasEarlierIgnorableLifecycle
            && !HasNearbySiblingInitialCreationSignal(item, events))
        {
            return false;
        }

        return events.Any(candidate =>
            candidate.Id != item.Id
            && candidate.TimestampUtc >= item.TimestampUtc
            && candidate.TimestampUtc - item.TimestampUtc <= TimeSpan.FromMinutes(5)
            && (NormalizePath(candidate.Path) == path || NormalizePath(candidate.PreviousPath) == path)
            && candidate.Action is "deleted" or "renamed" or "moved");
    }

    private static TransitionResult? TryBuildExplicitTransition(
        FileAuditEvent current,
        IReadOnlyCollection<ClusterItem> relevant,
        IReadOnlyCollection<ClusterItem> evidenceCluster,
        IReadOnlyCollection<FileAuditEvent> history)
    {
        if (string.IsNullOrWhiteSpace(current.PreviousPath) || current.Action is not ("renamed" or "moved"))
        {
            return null;
        }

        var ambiguous = TryBuildAmbiguousSamePathTransition(current, relevant);
        if (ambiguous is not null)
        {
            return ambiguous;
        }

        var previousPath = current.PreviousPath;
        if (PathsReferToSameItem(previousPath, current.Path))
        {
            return null;
        }

        var isFileTransition = IsFileLikePath(current.Path) && IsFileLikePath(previousPath);
        var isFolderTransition = IsLikelyFolderPath(current.Path) && IsLikelyFolderPath(previousPath);
        if (!isFileTransition && !isFolderTransition)
        {
            return null;
        }

        var isProvisionalFolderOrigin = IsProvisionalFolderName(previousPath)
            && IsLikelyFolderPath(current.Path)
            && NormalizePath(GetParentPath(previousPath)) == NormalizePath(GetParentPath(current.Path));
        var shouldPreserveProvisionalFolderRename = isProvisionalFolderOrigin
            && HasNearbyCreationAtPath(evidenceCluster, previousPath, current.TimestampUtc)
            && (HasMaterializedFolderContent(evidenceCluster, previousPath, current.TimestampUtc)
                || HasNearbyTransitionFromPath(evidenceCluster, current.Path, current.TimestampUtc));
        var isProvisionalOrigin = current.Action == "renamed"
            && (IsMaterializedProvisionalDocumentRename(previousPath, current.Path)
                || (IsTransientArtifactPath(previousPath) && IsProvisionalDocumentName(current.Path))
                || (isProvisionalFolderOrigin && !shouldPreserveProvisionalFolderRename))
            && !(HasNearbyCreationAtPath(evidenceCluster, previousPath, current.TimestampUtc)
                && !isProvisionalFolderOrigin)
            && !HasEstablishedItemAtPathBefore(history, previousPath, current.TimestampUtc)
            && !HasNearbyTransitionDestination(evidenceCluster, previousPath, current.TimestampUtc);
        var action = IsMove(previousPath, current.Path) ? "moved" : "renamed";
        var displayAction = isProvisionalOrigin ? "Criação" : action == "moved" ? "Movido" : "Renomeado";
        var consumedEvents = relevant
            .Where(item => ShouldConsumeTransitionEvent(item.Event, previousPath, current.Path, current.TimestampUtc, isProvisionalOrigin))
            .ToArray();
        var consumed = consumedEvents
            .Select(item => item.Index)
            .ToArray();
        var baseEvent = consumedEvents
            .Where(item => item.Event.Action is "renamed" or "moved")
            .Select(item => item.Event)
            .OrderByDescending(GetEventWeight)
            .ThenByDescending(item => item.TimestampUtc)
            .FirstOrDefault() ?? current;
        var userEvent = consumedEvents
            .Select(item => item.Event)
            .Where(item => !IsUnknownUser(item.User))
            .OrderByDescending(GetEventWeight)
            .FirstOrDefault();

        var display = ToDisplayEvent(baseEvent with
        {
            Id = StableSyntheticGuid(baseEvent.Id, $"{action}:explicit"),
            TimestampUtc = current.TimestampUtc,
            Action = isProvisionalOrigin ? "created" : action,
            PreviousPath = isProvisionalOrigin ? null : previousPath,
            Path = current.Path,
            User = IsUnknownUser(baseEvent.User) && userEvent is not null ? userEvent.User : baseEvent.User,
            Sid = baseEvent.Sid ?? userEvent?.Sid,
            SourceHost = baseEvent.SourceHost ?? userEvent?.SourceHost,
            SourceIp = baseEvent.SourceIp ?? userEvent?.SourceIp,
            ProcessName = baseEvent.ProcessName ?? userEvent?.ProcessName
        }) with
        {
            DisplayAction = displayAction,
            DisplayTarget = GetLeafName(current.Path)
        };

        return new TransitionResult(consumed, display);
    }

    private static TransitionResult? TryBuildAmbiguousSamePathTransition(FileAuditEvent current, IReadOnlyCollection<ClusterItem> relevant)
    {
        if (string.IsNullOrWhiteSpace(current.PreviousPath) || !PathsReferToSameItem(current.PreviousPath, current.Path))
        {
            return null;
        }

        var target = relevant
            .Select(item => item.Event)
            .FirstOrDefault(item =>
                item.Id != current.Id
                && item.Action == "accessed"
                && item.Source.Contains("windows-security-log", StringComparison.OrdinalIgnoreCase)
                && SameObjectShape(item.Path, current.Path)
                && !PathsReferToSameItem(item.Path, current.Path)
                && string.Equals(GetLeafName(item.Path), GetLeafName(current.Path), StringComparison.OrdinalIgnoreCase)
                && (item.TimestampUtc - current.TimestampUtc).Duration() <= TimeSpan.FromMilliseconds(2500));
        if (target is null)
        {
            return null;
        }

        var targetParent = NormalizePath(GetParentPath(target.Path));
        var hasParentTouch = relevant.Any(item =>
            item.Event.Id != current.Id
            && item.Event.Action is "created_or_appended" or "modified"
            && NormalizePath(item.Event.Path) == targetParent
            && (item.Event.TimestampUtc - current.TimestampUtc).Duration() <= TimeSpan.FromMilliseconds(2500));
        if (!hasParentTouch)
        {
            return null;
        }

        var consumed = relevant
            .Where(item => item.Event.Id == current.Id
                || (NormalizePath(item.Event.Path) == NormalizePath(target.Path)
                    && (item.Event.TimestampUtc - current.TimestampUtc).Duration() <= TimeSpan.FromMilliseconds(2500))
                || (NormalizePath(item.Event.Path) == targetParent
                    && item.Event.Action is "created_or_appended" or "modified"
                    && (item.Event.TimestampUtc - current.TimestampUtc).Duration() <= TimeSpan.FromMilliseconds(2500)))
            .Select(item => item.Index)
            .ToArray();

        return new TransitionResult(consumed, ToDisplayEvent(target with
        {
            Id = StableSyntheticGuid(current.Id, $"{target.Id}:ambiguous-move"),
            TimestampUtc = current.TimestampUtc,
            Action = "moved",
            PreviousPath = current.PreviousPath
        }) with
        {
            DisplayAction = "Movido",
            DisplayTarget = GetLeafName(target.Path)
        });
    }

    private static TransitionResult? TryBuildSecurityLogRenameTransition(FileAuditEvent current, IReadOnlyCollection<ClusterItem> relevant)
    {
        if (!current.Source.Contains("windows-security-log", StringComparison.OrdinalIgnoreCase)
            || current.Action is not ("accessed" or "deleted"))
        {
            return null;
        }

        var deleted = relevant
            .Select(item => item.Event)
            .FirstOrDefault(item =>
                item.Action == "deleted"
                && item.Source.Contains("windows-security-log", StringComparison.OrdinalIgnoreCase)
                && SameObjectShape(item.Path, current.Path));
        if (deleted is null)
        {
            return null;
        }

        var deletedTime = deleted.TimestampUtc;
        var deletedExtension = GetPathExtension(deleted.Path);
        var target = relevant
            .Select(item => item.Event)
            .FirstOrDefault(item =>
                item.Action == "accessed"
                && item.Source.Contains("windows-security-log", StringComparison.OrdinalIgnoreCase)
                && SameObjectShape(item.Path, deleted.Path)
                && !PathsReferToSameItem(item.Path, deleted.Path)
                && NormalizeUser(item.User) == NormalizeUser(deleted.User)
                && (item.TimestampUtc - deletedTime).Duration() <= TimeSpan.FromMilliseconds(2500)
                && IsLikelySecurityTransitionTarget(deleted.Path, item.Path, deletedExtension));
        if (target is null)
        {
            return null;
        }

        var targetLooksNew = relevant.Any(item =>
            item.Event.Id != target.Id
            && item.Event.Action is "created" or "created_or_appended"
            && PathsReferToSameItem(item.Event.Path, target.Path)
            && (item.Event.TimestampUtc - deletedTime).Duration() <= TimeSpan.FromMilliseconds(2500));
        if (targetLooksNew)
        {
            return null;
        }

        var targetWasDeleted = relevant.Any(item =>
            item.Event.Id != target.Id
            && item.Event.Action == "deleted"
            && PathsReferToSameItem(item.Event.Path, target.Path)
            && (item.Event.TimestampUtc - deletedTime).Duration() <= TimeSpan.FromMilliseconds(2500));
        if (targetWasDeleted)
        {
            return null;
        }

        var targetParent = NormalizePath(GetParentPath(target.Path));
        var currentParent = NormalizePath(GetParentPath(current.Path));
        var hasParentTouch = relevant.Any(item =>
            item.Event.Action is "created_or_appended" or "modified"
            && (NormalizePath(item.Event.Path) == targetParent || NormalizePath(item.Event.Path) == currentParent)
            && (item.Event.TimestampUtc - deletedTime).Duration() <= TimeSpan.FromMilliseconds(2500));
        if (!hasParentTouch)
        {
            return null;
        }

        var consumed = relevant
            .Where(item =>
            {
                var delta = (item.Event.TimestampUtc - deletedTime).Duration();
                return item.Event.Id == deleted.Id
                    || (NormalizePath(item.Event.Path) == NormalizePath(target.Path)
                        && item.Event.Action != "deleted"
                        && delta <= TimeSpan.FromMilliseconds(2500))
                    || (NormalizePath(item.Event.Path) == targetParent && item.Event.Action is "created_or_appended" or "modified" && delta <= TimeSpan.FromMilliseconds(2500))
                    || (item.Event.Action is "renamed" or "moved"
                        && delta <= TimeSpan.FromMilliseconds(2500)
                        && PathsReferToSameItem(item.Event.PreviousPath, deleted.Path)
                        && (PathsReferToSameItem(item.Event.Path, deleted.Path) || PathsReferToSameItem(item.Event.Path, target.Path)));
            })
            .Select(item => item.Index)
            .ToArray();

        var action = IsMove(deleted.Path, target.Path) ? "moved" : "renamed";
        return new TransitionResult(consumed, ToDisplayEvent(target with
        {
            Id = StableSyntheticGuid(deleted.Id, $"{target.Id}:security-rename"),
            TimestampUtc = deleted.TimestampUtc,
            Action = action,
            PreviousPath = deleted.Path
        }) with
        {
            DisplayAction = action == "moved" ? "Movido" : "Renomeado",
            DisplayTarget = GetLeafName(target.Path)
        });
    }

    private static bool IsOperationalNoise(FileAuditEvent item)
    {
        var path = item.Path.ToLowerInvariant();
        var previousPath = (item.PreviousPath ?? "").ToLowerInvariant();
        var officeMaterialization = item.Action == "renamed"
            && IsTransientArtifactPath(previousPath)
            && !IsTransientArtifactPath(item.Path)
            && IsProvisionalDocumentName(item.Path);

        return path.EndsWith("\\appsettings.agent.json", StringComparison.Ordinal)
            || path.EndsWith("\\agent-state.json", StringComparison.Ordinal)
            || path.EndsWith("\\pending-events.ndjson", StringComparison.Ordinal)
            || path.Contains("\\logs\\", StringComparison.Ordinal)
            || path.EndsWith("\\logs", StringComparison.Ordinal)
            || IsAgentSelfAccess(item)
            || IsTransientArtifactPath(item.Path)
            || (IsTransientArtifactPath(previousPath) && !officeMaterialization);
    }

    private static bool IsAgentSelfAccess(FileAuditEvent item)
    {
        return item.Action == "accessed"
            && item.Source.Contains("windows-security-log", StringComparison.OrdinalIgnoreCase)
            && IsAgentProcess(item.ProcessName);
    }

    private static bool IsAgentProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var normalized = processName.Replace('/', '\\').Trim();
        return normalized.Equals("FileServerMonitor.Agent.exe", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("\\FileServerMonitor.Agent.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransientRenameNoise(FileAuditEvent current, IReadOnlyCollection<ClusterItem> cluster)
    {
        if (current.Action is not ("renamed" or "moved"))
        {
            return false;
        }

        if (PathsReferToSameItem(current.PreviousPath, current.Path)
            && !cluster.Any(item =>
                item.Event.Id != current.Id
                && item.Event.Action == "accessed"
                && item.Event.Source.Contains("windows-security-log", StringComparison.OrdinalIgnoreCase)
                && !PathsReferToSameItem(item.Event.Path, current.Path)
                && SameObjectShape(item.Event.Path, current.Path)
                && string.Equals(GetLeafName(item.Event.Path), GetLeafName(current.Path), StringComparison.OrdinalIgnoreCase)
                && (item.Event.TimestampUtc - current.TimestampUtc).Duration() <= TimeSpan.FromMilliseconds(2500)))
        {
            return true;
        }

        var touchesTransient = IsTransientArtifactPath(current.Path) || IsTransientArtifactPath(current.PreviousPath ?? "");
        if (!touchesTransient)
        {
            return false;
        }

        if (current.Action == "renamed"
            && IsTransientArtifactPath(current.PreviousPath ?? "")
            && !IsTransientArtifactPath(current.Path)
            && IsProvisionalDocumentName(current.Path))
        {
            return false;
        }

        return cluster.Any(item =>
            item.Event.Id != current.Id
            && item.Event.Action == "deleted"
            && IsFileLikePath(item.Event.Path)
            && (NormalizePath(item.Event.Path).StartsWith(NormalizePath(current.PreviousPath), StringComparison.OrdinalIgnoreCase)
                || NormalizePath(item.Event.Path).StartsWith(NormalizePath(current.Path), StringComparison.OrdinalIgnoreCase)
                || NormalizePath(GetParentPath(item.Event.Path)) == NormalizePath(current.PreviousPath)));
    }

    private static bool IsProvisionalDocumentNoise(FileAuditEvent current, IReadOnlyCollection<FileAuditEvent> ordered)
    {
        var isProvisionalName = IsProvisionalDocumentName(current.Path) || IsProvisionalFolderName(current.Path);
        if (!isProvisionalName)
        {
            return false;
        }

        if (current.Action is "created" or "created_or_appended")
        {
            return ShouldSuppressProvisionalCreate(ToDisplayEvent(current), ordered.Select(ToDisplayEvent).ToArray());
        }

        if (current.Action == "deleted")
        {
            return false;
        }

        var currentPath = NormalizePath(current.Path);
        return ordered.Any(item =>
            item.Id != current.Id
            && (item.TimestampUtc - current.TimestampUtc).Duration() <= TimeSpan.FromSeconds(15)
            && (IsFileLikePath(item.Path) || IsLikelyFolderPath(item.Path))
            && NormalizePath(item.PreviousPath) == currentPath
            && !IsTransientArtifactPath(item.Path));
    }

    private static bool IsRedundantRenameAfterCreation(FileAuditEvent current, IReadOnlyCollection<FileAuditEvent> ordered)
    {
        if (current.Action != "renamed" || string.IsNullOrWhiteSpace(current.PreviousPath) || !IsProvisionalDocumentName(current.PreviousPath))
        {
            return false;
        }

        return ordered.Any(item =>
            item.Id != current.Id
            && (item.TimestampUtc - current.TimestampUtc).Duration() <= TimeSpan.FromSeconds(15)
            && item.Action is "created" or "created_or_appended"
            && NormalizePath(item.Path) == NormalizePath(current.Path));
    }

    private static bool IsRedundantDeletedNoise(FileAuditEvent current, IReadOnlyCollection<ClusterItem> cluster)
    {
        if (current.Action != "deleted")
        {
            return false;
        }

        var currentPath = NormalizePath(current.Path);
        var relocatedPath = cluster
            .Select(item => item.Event)
            .Where(candidate => candidate.Action == "moved"
                && IsLikelyFolderPath(candidate.Path)
                && !string.IsNullOrWhiteSpace(candidate.PreviousPath)
                && candidate.TimestampUtc <= current.TimestampUtc
                && current.TimestampUtc - candidate.TimestampUtc <= TimeSpan.FromSeconds(15))
            .OrderBy(candidate => candidate.TimestampUtc)
            .Aggregate(current.Path, (path, move) => TryRelocatePath(path, move.PreviousPath!, move.Path));
        if (!PathsReferToSameItem(relocatedPath, current.Path)
            && cluster.Any(item =>
                item.Event.Id != current.Id
                && item.Event.Action == "deleted"
                && (item.Event.TimestampUtc - current.TimestampUtc).Duration() <= TimeSpan.FromSeconds(3)
                && PathsReferToSameItem(item.Event.Path, relocatedPath)))
        {
            return true;
        }

        return cluster.Any(item =>
        {
            var candidate = item.Event;
            if (candidate.Id == current.Id || candidate.Action is not ("renamed" or "moved"))
            {
                return false;
            }

            if (PathsReferToSameItem(candidate.PreviousPath, candidate.Path))
            {
                return false;
            }

            var delta = (candidate.TimestampUtc - current.TimestampUtc).Duration();
            if (delta > TimeSpan.FromSeconds(3))
            {
                return false;
            }

            return NormalizePath(candidate.PreviousPath) == currentPath;
        });
    }

    private static bool IsRedundantCreationNoise(FileAuditEvent current, IReadOnlyCollection<ClusterItem> cluster)
    {
        if (current.Action is not ("created_or_appended" or "created"))
        {
            return false;
        }

        var currentPath = NormalizePath(current.Path);
        return cluster.Any(item =>
        {
            var candidate = item.Event;
            if (candidate.Id == current.Id || candidate.Action is not ("renamed" or "moved" or "created"))
            {
                return false;
            }

            if (candidate.Action is "renamed" or "moved"
                && NormalizePath(candidate.PreviousPath) == currentPath
                && !IsProvisionalDocumentName(current.Path))
            {
                return false;
            }

            return NormalizePath(candidate.Path) == currentPath || NormalizePath(candidate.PreviousPath) == currentPath;
        });
    }

    private static bool IsRootOnlyNoise(FileAuditEvent current, IReadOnlyCollection<ClusterItem> cluster)
    {
        if (!IsLikelyFolderPath(current.Path) || current.Action is "deleted" or "renamed" or "moved")
        {
            return false;
        }

        if (current.Action == "permission_changed")
        {
            return false;
        }

        if (IsShareRootPath(current))
        {
            return cluster.Any(item =>
                item.Event.Id != current.Id
                && (item.Event.TimestampUtc - current.TimestampUtc).Duration() <= TimeSpan.FromSeconds(15)
                && NormalizePath(item.Event.Path).StartsWith($"{NormalizePath(current.Path)}\\", StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static bool IsRedundantParentCreate(FileAuditEvent current, IReadOnlyCollection<ClusterItem> cluster)
    {
        return current.Source == "windows-security-log"
            && current.Action == "created_or_appended"
            && cluster.Any(item =>
                item.Event.Id != current.Id
                && item.Event.Server == current.Server
                && item.Event.Share == current.Share
                && item.Event.User == current.User
                && (item.Event.Action == "deleted" || IsRenameLikeAction(item.Event.Action))
                && GetParentPath(item.Event.Path) == current.Path);
    }

    private static bool IsUnknownUsnNoise(
        FileAuditEvent current,
        IReadOnlyCollection<ClusterItem> cluster,
        IReadOnlyCollection<ClusterItem>? clusterAll = null)
    {
        if (HasNearbySecurityAppendWriteEvidence(current, clusterAll ?? cluster))
        {
            return false;
        }

        return current.Source == "usn-journal"
            && IsUnknownUser(current.User)
            && current.Action is "changed" or "modified"
            && (IsProvisionalDocumentName(current.Path)
                || !cluster.Any(item =>
                    item.Event.Id != current.Id
                    && item.Event.Path == current.Path
                    && item.Event.Source != "usn-journal"
                    && item.Event.Action is "changed" or "modified" or "created_or_appended"))
            && cluster.Any(item =>
                item.Event.Id != current.Id
                && item.Event.Path == current.Path
                && item.Event.Source != "usn-journal"
                && item.Event.Action is not ("changed" or "modified" or "created_or_appended"));
    }

    private static bool IsRedundantChangedNoise(
        FileAuditEvent current,
        IReadOnlyCollection<ClusterItem> cluster,
        IReadOnlyCollection<ClusterItem>? clusterAll = null)
    {
        if (!current.Source.Contains("usn-journal", StringComparison.OrdinalIgnoreCase)
            || current.Action is not ("changed" or "modified"))
        {
            return false;
        }

        var path = NormalizePath(current.Path);
        var hasLaterLifecycleAfterChange = cluster.Any(item =>
        {
            var candidate = item.Event;
            return candidate.Id != current.Id
                && candidate.TimestampUtc > current.TimestampUtc
                && candidate.TimestampUtc - current.TimestampUtc > TimeSpan.FromSeconds(3)
                && candidate.TimestampUtc - current.TimestampUtc <= TimeSpan.FromMinutes(5)
                && candidate.Action is "deleted" or "renamed" or "moved"
                && (NormalizePath(candidate.Path) == path || NormalizePath(candidate.PreviousPath) == path);
        });
        if (hasLaterLifecycleAfterChange)
        {
            return false;
        }

        if (HasNearbySecurityAppendWriteEvidence(current, clusterAll ?? cluster))
        {
            return false;
        }

        return cluster.Any(item =>
        {
            var candidate = item.Event;
            if (candidate.Id == current.Id)
            {
                return false;
            }

            var samePath = NormalizePath(candidate.Path) == path || NormalizePath(candidate.PreviousPath) == path;
            if (!samePath)
            {
                return false;
            }

            if (candidate.Action is "renamed" or "moved" or "deleted")
            {
                return (candidate.TimestampUtc - current.TimestampUtc).Duration() <= TimeSpan.FromSeconds(2);
            }

            if (candidate.Action == "created" && IsFileLikePath(candidate.Path))
            {
                return IsNearImmediateCreationEcho(candidate.TimestampUtc, current.TimestampUtc);
            }

            if (candidate.Action == "created_or_appended"
                && candidate.Source.Contains("usn-journal", StringComparison.OrdinalIgnoreCase)
                && IsFileLikePath(candidate.Path))
            {
                return IsNearImmediateCreationEcho(candidate.TimestampUtc, current.TimestampUtc);
            }

            return candidate.Source.Contains("usn-journal", StringComparison.OrdinalIgnoreCase)
                && candidate.Action is "changed" or "modified"
                && candidate.TimestampUtc > current.TimestampUtc;
        });
    }

    private static bool HasNearbySecurityAppendWriteEvidence(FileAuditEvent current, IReadOnlyCollection<ClusterItem> cluster)
    {
        if (!current.Source.Contains("usn-journal", StringComparison.OrdinalIgnoreCase)
            || current.Action is not ("changed" or "modified")
            || !IsFileLikePath(current.Path))
        {
            return false;
        }

        return cluster.Any(item =>
        {
            var candidate = item.Event;
            return candidate.Id != current.Id
                && candidate.Source.Equals("windows-security-log", StringComparison.OrdinalIgnoreCase)
                && candidate.Action == "created_or_appended"
                && NormalizePath(candidate.Path) == NormalizePath(current.Path)
                && (candidate.TimestampUtc - current.TimestampUtc).Duration() <= TimeSpan.FromSeconds(2);
        });
    }

    private static bool HasNearbyDisplayAppendWriteEvidence(FileAuditDisplayEvent item, IReadOnlyCollection<FileAuditDisplayEvent> all)
    {
        if (!item.Source.Contains("usn-journal", StringComparison.OrdinalIgnoreCase)
            || item.Action is not ("changed" or "modified")
            || !IsFileLikePath(item.Path))
        {
            return false;
        }

        return all.Any(candidate =>
            candidate.Id != item.Id
            && candidate.Source.Equals("windows-security-log", StringComparison.OrdinalIgnoreCase)
            && candidate.Action is "created_or_appended" or "modified"
            && NormalizePath(candidate.Path) == NormalizePath(item.Path)
            && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(2));
    }

    private static bool IsTransientDisplayNoise(FileAuditDisplayEvent item)
    {
        if (IsTransientArtifactPath(item.Path) || IsTransientArtifactPath(item.PreviousPath ?? ""))
        {
            return true;
        }

        if (item.Action is "moved" or "renamed" && !string.IsNullOrWhiteSpace(item.PreviousPath))
        {
            var currentParentSegments = NormalizePath(GetParentPath(item.Path)).Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var previousParentSegments = NormalizePath(GetParentPath(item.PreviousPath)).Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return currentParentSegments.Any(IsTransientContainerSegment) || previousParentSegments.Any(IsTransientContainerSegment);
        }

        return false;
    }

    private static bool IsRedundantDisplayPermissionEcho(FileAuditDisplayEvent item, IReadOnlyCollection<FileAuditDisplayEvent> all)
    {
        if (item.Action != "permission_changed")
        {
            return false;
        }

        var priority = GetDisplaySourcePriority(item);
        return all.Any(candidate =>
        {
            if (candidate.Id == item.Id || candidate.Action != "permission_changed")
            {
                return false;
            }

            if (NormalizePath(candidate.Path) != NormalizePath(item.Path) || NormalizeUser(candidate.User) != NormalizeUser(item.User))
            {
                return false;
            }

            if ((candidate.TimestampUtc - item.TimestampUtc).Duration() > TimeSpan.FromSeconds(2))
            {
                return false;
            }

            var candidatePriority = GetDisplaySourcePriority(candidate);
            return candidatePriority > priority
                || (candidatePriority == priority
                    && (candidate.TimestampUtc > item.TimestampUtc
                        || (candidate.TimestampUtc == item.TimestampUtc && string.CompareOrdinal(candidate.Id.ToString(), item.Id.ToString()) < 0)));
        });
    }

    private static bool IsPermissionEchoDuringDelete(FileAuditDisplayEvent item, IReadOnlyCollection<FileAuditDisplayEvent> all)
    {
        if (item.Action != "permission_changed")
        {
            return false;
        }

        return all.Any(candidate =>
            candidate.Id != item.Id
            && candidate.Action == "deleted"
            && PathsReferToSameItem(candidate.Path, item.Path)
            && NormalizeUser(candidate.User) == NormalizeUser(item.User)
            && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromMilliseconds(1500));
    }

    private static bool IsRedundantDisplayDeleted(FileAuditDisplayEvent item, IReadOnlyCollection<FileAuditDisplayEvent> all)
    {
        if (item.Action != "deleted")
        {
            return false;
        }

        var stalePathFromFolderMove = all.Any(candidate =>
        {
            if (candidate.Id == item.Id
                || candidate.Action != "moved"
                || !IsLikelyFolderPath(candidate.Path)
                || string.IsNullOrWhiteSpace(candidate.PreviousPath)
                || candidate.TimestampUtc > item.TimestampUtc
                || item.TimestampUtc - candidate.TimestampUtc > TimeSpan.FromMinutes(5))
            {
                return false;
            }

            var relocatedPath = TryRelocatePath(item.Path, candidate.PreviousPath, candidate.Path);
            return !PathsReferToSameItem(relocatedPath, item.Path)
                && all.Any(other =>
                    other.Id != item.Id
                    && other.Action == "deleted"
                    && (other.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(5)
                    && PathsReferToSameItem(other.Path, relocatedPath));
        });
        if (stalePathFromFolderMove)
        {
            return true;
        }

        return all.Any(candidate =>
            candidate.Id != item.Id
            && candidate.Action is "renamed" or "moved"
            && !IsTransientArtifactPath(candidate.Path)
            && !PathsReferToSameItem(candidate.PreviousPath, candidate.Path)
            && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(15)
            && PathsReferToSameItem(candidate.PreviousPath, item.Path));
    }

    private static bool IsRedundantDisplayDeletedDuplicate(FileAuditDisplayEvent item, IReadOnlyCollection<FileAuditDisplayEvent> all)
    {
        if (item.Action != "deleted")
        {
            return false;
        }

        var itemResolvedPath = ResolvePathThroughEarlierFolderMoves(item.Path, item.TimestampUtc, all);
        if (!PathsReferToSameItem(itemResolvedPath, item.Path)
            && all.Any(candidate =>
                candidate.Id != item.Id
                && candidate.Action == "deleted"
                && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(5)
                && PathsReferToSameItem(candidate.Path, itemResolvedPath)))
        {
            return true;
        }

        return all.Any(candidate =>
        {
            if (candidate.Id == item.Id || candidate.Action != "deleted")
            {
                return false;
            }

            if ((candidate.TimestampUtc - item.TimestampUtc).Duration() > TimeSpan.FromSeconds(5))
            {
                return false;
            }

            var sameDirectPath = PathsReferToSameItem(candidate.Path, item.Path);
            var sameResolvedPath = PathsReferToSameItem(
                ResolvePathThroughEarlierFolderMoves(candidate.Path, candidate.TimestampUtc, all),
                itemResolvedPath);
            if (!sameDirectPath && !sameResolvedPath)
            {
                return false;
            }

            if (!sameDirectPath && sameResolvedPath)
            {
                var candidateResolvedPath = ResolvePathThroughEarlierFolderMoves(candidate.Path, candidate.TimestampUtc, all);
                var candidateMatchesResolved = NormalizePath(candidate.Path) == NormalizePath(candidateResolvedPath);
                var itemMatchesResolved = NormalizePath(item.Path) == NormalizePath(itemResolvedPath);
                if (candidateMatchesResolved != itemMatchesResolved)
                {
                    return candidateMatchesResolved;
                }
            }

            if (HasReplacementCharacter(item.Path) != HasReplacementCharacter(candidate.Path))
            {
                return HasReplacementCharacter(item.Path);
            }

            var candidateWeight = GetEventWeight(candidate);
            var itemWeight = GetEventWeight(item);
            if (candidateWeight != itemWeight)
            {
                return candidateWeight > itemWeight;
            }

            return candidate.TimestampUtc < item.TimestampUtc;
        });
    }

    private static bool IsRedundantDisplayProvisionalDelete(FileAuditDisplayEvent item, IReadOnlyCollection<FileAuditDisplayEvent> all)
    {
        if (item.Action != "deleted")
        {
            return false;
        }

        var provisionalKind = GetProvisionalDocumentKind(item.Path);
        if (provisionalKind is null or "text")
        {
            return false;
        }

        var parent = NormalizePath(GetParentPath(item.Path));
        var extension = GetPathExtension(item.Path);
        var samePathCreation = all.Any(candidate =>
            candidate.Id != item.Id
            && candidate.Action is "created" or "created_or_appended"
            && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromMinutes(5)
            && PathsReferToSameItem(candidate.Path, item.Path));
        if (samePathCreation)
        {
            return false;
        }

        return all.Any(candidate =>
        {
            if (candidate.Id == item.Id || (candidate.TimestampUtc - item.TimestampUtc).Duration() > TimeSpan.FromSeconds(45))
            {
                return false;
            }

            if (candidate.Action is "renamed" or "moved")
            {
                return NormalizePath(candidate.PreviousPath) == NormalizePath(item.Path);
            }

            return candidate.Action is "created" or "created_or_appended"
                && !IsProvisionalDocumentName(candidate.Path)
                && NormalizePath(GetParentPath(candidate.Path)) == parent
                && GetPathExtension(candidate.Path) == extension;
        });
    }

    private static bool IsSuspiciousMoveEcho(FileAuditDisplayEvent item, IReadOnlyCollection<FileAuditDisplayEvent> all)
    {
        if (item.Action != "moved" || string.IsNullOrWhiteSpace(item.PreviousPath))
        {
            return false;
        }

        var currentParent = NormalizePath(GetParentPath(item.Path));
        var previousParent = NormalizePath(GetParentPath(item.PreviousPath));
        if (!previousParent.StartsWith($"{currentParent}\\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var deletedPrevious = all.Any(candidate =>
            candidate.Id != item.Id
            && candidate.Action == "deleted"
            && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(30)
            && NormalizePath(candidate.Path) == NormalizePath(item.PreviousPath));
        var destinationSignal = all.Any(candidate =>
            candidate.Id != item.Id
            && candidate.Action != "deleted"
            && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(8)
            && NormalizePath(candidate.Path) == NormalizePath(item.Path));

        return deletedPrevious && !destinationSignal;
    }

    private static bool IsRedundantDisplayFolderChangedEcho(FileAuditDisplayEvent item, IReadOnlyCollection<FileAuditDisplayEvent> all)
    {
        if (!IsLikelyFolderPath(item.Path) || item.Action is not ("changed" or "modified"))
        {
            return false;
        }

        if (all.Any(candidate =>
            candidate.Id != item.Id
            && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(5)
            && NormalizePath(candidate.Path) == NormalizePath(item.Path)
            && candidate.Action is "created" or "created_or_appended" or "renamed" or "moved" or "deleted"))
        {
            return true;
        }

        if (item.Source.Contains("usn-journal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var folderPath = NormalizePath(item.Path);
        return all.Any(candidate =>
            candidate.Id != item.Id
            && candidate.Action == "moved"
            && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(30)
            && NormalizePath(GetParentPath(candidate.Path)) == folderPath);
    }

    private static bool IsRedundantDisplayRenameAfterCreate(FileAuditDisplayEvent item, IReadOnlyCollection<FileAuditDisplayEvent> all)
    {
        return item.Action is "renamed" or "moved"
            && !string.IsNullOrWhiteSpace(item.PreviousPath)
            && IsProvisionalDocumentName(item.PreviousPath)
            && all.Any(candidate =>
                candidate.Id != item.Id
                && candidate.Action is "created" or "created_or_appended"
                && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(15)
                && NormalizePath(candidate.Path) == NormalizePath(item.Path));
    }

    private static bool IsRedundantDisplayCreateEcho(FileAuditDisplayEvent item, IReadOnlyCollection<FileAuditDisplayEvent> all)
    {
        if (item.Action is not ("created" or "created_or_appended"))
        {
            return false;
        }

        if (IsShareRootDisplayPath(item)
            && all.Any(candidate =>
                candidate.Id != item.Id
                && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(15)
                && NormalizePath(candidate.Path).StartsWith($"{NormalizePath(item.Path)}\\", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (item.Source.Equals("windows-security-log", StringComparison.OrdinalIgnoreCase)
            && all.Any(candidate =>
                candidate.Id != item.Id
                && candidate.Action == "deleted"
                && candidate.TimestampUtc <= item.TimestampUtc
                && item.TimestampUtc - candidate.TimestampUtc <= TimeSpan.FromSeconds(2)
                && PathsReferToSameItem(candidate.Path, item.Path)))
        {
            return true;
        }

        if (IsLikelyFolderPath(item.Path)
            && !item.Source.Contains("usn-journal", StringComparison.OrdinalIgnoreCase))
        {
            var folderPath = NormalizePath(item.Path);
            if (all.Any(candidate =>
                candidate.Id != item.Id
                && candidate.Action == "moved"
                && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(5)
                    && NormalizePath(GetParentPath(candidate.Path)) == folderPath))
            {
                return true;
            }

        }

        if (item.Action == "created_or_appended"
            && item.Source.Equals("windows-security-log", StringComparison.OrdinalIgnoreCase)
            && IsFileLikePath(item.Path))
        {
            return all.Any(candidate =>
                candidate.Id != item.Id
                && candidate.Action is "changed" or "modified"
                && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(15)
                && PathsReferToSameItem(candidate.Path, item.Path));
        }

        if (item.Source == "windows-security-log" && HasReplacementCharacter(item.Path))
        {
            return all.Any(candidate =>
                candidate.Id != item.Id
                && candidate.Source.Contains("usn-journal", StringComparison.OrdinalIgnoreCase)
                && candidate.Action is "created" or "created_or_appended"
                && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(5)
                && PathsReferToSameItem(candidate.Path, item.Path));
        }

        var provisionalOrigin = IsProvisionalDocumentName(item.Path) || IsProvisionalFolderName(item.Path);
        return provisionalOrigin
            && all.Any(candidate =>
                candidate.Id != item.Id
                && candidate.Action is "renamed" or "moved"
                && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(15)
                && NormalizePath(candidate.Path) == NormalizePath(item.Path)
                && !IsTransientArtifactPath(candidate.PreviousPath ?? ""));
    }

    private static bool IsRedundantDisplayCreatedDuplicate(FileAuditDisplayEvent item, IReadOnlyCollection<FileAuditDisplayEvent> all)
    {
        if (item.Action is not ("created" or "created_or_appended"))
        {
            return false;
        }

        return all.Any(candidate =>
        {
            if (candidate.Id == item.Id || candidate.Action is not ("created" or "created_or_appended"))
            {
                return false;
            }

            if ((candidate.TimestampUtc - item.TimestampUtc).Duration() > TimeSpan.FromSeconds(5) || !PathsReferToSameItem(candidate.Path, item.Path))
            {
                return false;
            }

            var candidateWeight = GetEventWeight(candidate);
            var itemWeight = GetEventWeight(item);
            if (candidateWeight != itemWeight)
            {
                return candidateWeight > itemWeight;
            }

            return candidate.TimestampUtc < item.TimestampUtc;
        });
    }

    private static bool IsRedundantDisplayChangedEcho(FileAuditDisplayEvent item, IReadOnlyCollection<FileAuditDisplayEvent> all)
    {
        if (item.Action is not ("changed" or "modified"))
        {
            return false;
        }

        var path = NormalizePath(item.Path);
        var nearDelete = all.Any(candidate =>
            candidate.Id != item.Id
            && candidate.Action == "deleted"
            && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(3)
            && (NormalizePath(candidate.Path) == path || NormalizePath(candidate.PreviousPath) == path));
        if (nearDelete)
        {
            return true;
        }

        if (IsLikelyFolderPath(item.Path))
        {
            return false;
        }

        var hasLaterLifecycleAfterChange = all.Any(candidate =>
            candidate.Id != item.Id
            && candidate.TimestampUtc > item.TimestampUtc
            && candidate.TimestampUtc - item.TimestampUtc > TimeSpan.FromSeconds(3)
            && candidate.TimestampUtc - item.TimestampUtc <= TimeSpan.FromMinutes(5)
            && candidate.Action is "deleted" or "renamed" or "moved"
            && (NormalizePath(candidate.Path) == path || NormalizePath(candidate.PreviousPath) == path));
        if (hasLaterLifecycleAfterChange)
        {
            return false;
        }

        if (item.Source.Equals("windows-security-log", StringComparison.OrdinalIgnoreCase)
            && all.Any(candidate =>
                candidate.Id != item.Id
                && candidate.Action == "created"
                && candidate.Source.Contains("usn-journal", StringComparison.OrdinalIgnoreCase)
                && NormalizePath(candidate.Path) == path
                && IsSecurityModifyCreationEcho(candidate.TimestampUtc, item.TimestampUtc)))
        {
            return true;
        }

        return all.Any(candidate =>
            candidate.Id != item.Id
            && candidate.Action is "created" or "created_or_appended"
            && !(candidate.Action == "created_or_appended"
                && candidate.Source.Equals("windows-security-log", StringComparison.OrdinalIgnoreCase)
                && IsFileLikePath(candidate.Path))
            && NormalizePath(candidate.Path) == path
            && IsNearImmediateCreationEcho(candidate.TimestampUtc, item.TimestampUtc));
    }

    private static bool IsRedundantDisplayAccessedEcho(FileAuditDisplayEvent item, IReadOnlyCollection<FileAuditDisplayEvent> all)
    {
        if (item.Action != "accessed")
        {
            return false;
        }

        var path = NormalizePath(item.Path);
        return all.Any(candidate =>
        {
            if (candidate.Id == item.Id)
            {
                return false;
            }

            if (candidate.Action == "accessed")
            {
                var candidateIsTransitionEcho = all.Any(transition =>
                    transition.Id != candidate.Id
                    && transition.Action is "renamed" or "moved"
                    && candidate.TimestampUtc >= transition.TimestampUtc
                    && candidate.TimestampUtc - transition.TimestampUtc <= TimeSpan.FromSeconds(5)
                    && (PathsReferToSameItem(transition.Path, candidate.Path)
                        || PathsReferToSameItem(transition.PreviousPath, candidate.Path)));

                return IsFileLikePath(item.Path)
                    && !candidateIsTransitionEcho
                    && PathsReferToSameItem(candidate.Path, item.Path)
                    && NormalizeUser(candidate.User) == NormalizeUser(item.User)
                    && candidate.TimestampUtc > item.TimestampUtc
                    && candidate.TimestampUtc - item.TimestampUtc <= TimeSpan.FromSeconds(5);
            }

            if (candidate.Action is "changed" or "modified")
            {
                if (!IsFileLikePath(item.Path) || !PathsReferToSameItem(candidate.Path, item.Path))
                {
                    return false;
                }

                var delta = item.TimestampUtc - candidate.TimestampUtc;
                return (delta >= TimeSpan.Zero && delta <= TimeSpan.FromSeconds(15))
                    || delta.Duration() <= TimeSpan.FromSeconds(2);
            }

            if (candidate.Action == "permission_changed")
            {
                return PathsReferToSameItem(candidate.Path, item.Path)
                    && NormalizeUser(candidate.User) == NormalizeUser(item.User)
                    && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(5);
            }

            if (candidate.Action is not ("created" or "created_or_appended" or "renamed" or "moved" or "deleted"))
            {
                return false;
            }

            var window = candidate.Action is "created" or "created_or_appended"
                ? TimeSpan.FromSeconds(2)
                : TimeSpan.FromSeconds(1);

            var transitionEchoDelta = item.TimestampUtc - candidate.TimestampUtc;
            if (candidate.Action is "renamed" or "moved"
                && transitionEchoDelta >= TimeSpan.Zero
                && transitionEchoDelta <= TimeSpan.FromSeconds(5)
                && (PathsReferToSameItem(candidate.Path, item.Path)
                    || PathsReferToSameItem(candidate.PreviousPath, item.Path)))
            {
                return true;
            }

            return (candidate.TimestampUtc - item.TimestampUtc).Duration() <= window
                && (PathsReferToSameItem(candidate.Path, item.Path)
                    || PathsReferToSameItem(candidate.PreviousPath, item.Path)
                    || path == NormalizePath(GetParentPath(candidate.Path))
                    || path == NormalizePath(GetParentPath(candidate.PreviousPath ?? "")));
        });
    }

    private static IReadOnlyCollection<FileAuditEvent> DeduplicateRawEvents(IEnumerable<FileAuditEvent> events)
    {
        var grouped = new Dictionary<string, FileAuditEvent>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in events)
        {
            var key = string.Join("|",
                item.Action == "deleted" ? "" : item.User,
                item.Action,
                NormalizePath(item.Path),
                NormalizePath(item.PreviousPath),
                TrimToSecond(item.TimestampUtc).ToString("O"));

            grouped[key] = grouped.TryGetValue(key, out var existing)
                ? MergeDuplicateEvent(existing, item)
                : item;
        }

        return MergeUnknownRawDuplicates(grouped.Values).ToArray();
    }

    private static IEnumerable<FileAuditEvent> MergeUnknownRawDuplicates(IEnumerable<FileAuditEvent> events)
    {
        return events
            .GroupBy(item => string.Join("|",
                item.Server,
                item.Share,
                item.Action,
                NormalizePath(item.Path),
                NormalizePath(item.PreviousPath),
                TrimToSecond(item.TimestampUtc).ToString("O")), StringComparer.OrdinalIgnoreCase)
            .SelectMany(group =>
            {
                var items = group.ToArray();
                var knownUsers = items
                    .Where(item => !IsUnknownUser(item.User))
                    .Select(item => item.User)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (knownUsers.Length != 1 || items.All(item => !IsUnknownUser(item.User)))
                {
                    return items;
                }

                var merged = items.Aggregate(MergeDuplicateEvent);
                return new[] { merged with { User = knownUsers[0] } };
            });
    }

    private static FileAuditEvent MergeDuplicateEvent(FileAuditEvent left, FileAuditEvent right)
    {
        var winner = GetEventWeight(right) > GetEventWeight(left) ? right : left;
        var other = winner.Id == right.Id ? left : right;

        return winner with
        {
            User = IsUnknownUser(winner.User) && !IsUnknownUser(other.User) ? other.User : winner.User,
            Sid = winner.Sid ?? other.Sid,
            SourceHost = winner.SourceHost ?? other.SourceHost,
            SourceIp = winner.SourceIp ?? other.SourceIp,
            ProcessName = winner.ProcessName ?? other.ProcessName,
            Source = MergeEventSources(winner.Source, other.Source)
        };
    }

    private static FileAuditDisplayEvent ToDisplayEvent(FileAuditEvent item)
    {
        return new FileAuditDisplayEvent(
            item.Id,
            item.TimestampUtc,
            item.Server,
            item.Share,
            item.Path,
            item.PreviousPath,
            item.ObjectType,
            item.Action,
            item.User,
            item.Sid,
            item.SourceHost,
            item.SourceIp,
            item.ProcessName,
            item.FileSizeBytes,
            item.Extension,
            item.Result,
            item.Severity,
            item.Source,
            FormatAction(item.Action, item),
            GetLeafName(item.Path));
    }

    private static FileAuditEvent ToAuditEvent(FileAuditDisplayEvent item)
    {
        return new FileAuditEvent(
            item.Id,
            item.TimestampUtc,
            item.Server,
            item.Share,
            item.Path,
            item.PreviousPath,
            item.ObjectType,
            item.Action,
            item.User,
            item.Sid,
            item.SourceHost,
            item.SourceIp,
            item.ProcessName,
            item.FileSizeBytes,
            item.Extension,
            item.Result,
            item.Severity,
            item.Source);
    }

    private static string GetSemanticEventKey(FileAuditDisplayEvent item)
    {
        var action = item.Action is "changed" or "modified" ? "changed" : item.Action;
        var user = item.Action == "deleted" ? "" : item.User;
        return string.Join("|", user, action, NormalizePath(item.Path), NormalizePath(item.PreviousPath), TrimToSecond(item.TimestampUtc).ToString("O"));
    }

    private static string MergeEventSources(string left, string right)
    {
        var hasUsn = left.Contains("usn-journal", StringComparison.OrdinalIgnoreCase) || right.Contains("usn-journal", StringComparison.OrdinalIgnoreCase);
        var hasSecurity = left.Contains("security-log", StringComparison.OrdinalIgnoreCase) || right.Contains("security-log", StringComparison.OrdinalIgnoreCase);
        return hasUsn && hasSecurity ? "usn-journal+security-log" : left;
    }

    private static int GetEventWeight(FileAuditEvent item)
    {
        var sourceWeight = item.Source.Contains("usn-journal+security-log", StringComparison.OrdinalIgnoreCase)
            ? 30
            : item.Source.Contains("usn-journal", StringComparison.OrdinalIgnoreCase) ? 20 : 10;
        var actionWeight = item.Action is "moved" or "renamed"
            ? 30
            : item.Action == "deleted" ? 20 : item.Action is "created_or_appended" or "created" ? 15 : 5;
        return sourceWeight + actionWeight;
    }

    private static int GetEventWeight(FileAuditDisplayEvent item)
    {
        return GetEventWeight(ToAuditEvent(item));
    }

    private static int GetDisplaySourcePriority(FileAuditDisplayEvent item)
    {
        if (item.Source.Contains("usn-journal+security-log", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (item.Source.Contains("windows-security-log", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return item.Source.Contains("usn-journal", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    private static bool HasRelatedDisplayPath(FileAuditDisplayEvent left, FileAuditDisplayEvent right)
    {
        var leftPaths = new[] { left.Path, left.PreviousPath }.Where(IsPresentString).Select(NormalizePath).ToArray();
        var rightPaths = new[] { right.Path, right.PreviousPath }.Where(IsPresentString).Select(NormalizePath).ToArray();
        return leftPaths.Any(leftPath =>
            rightPaths.Any(rightPath =>
                leftPath == rightPath
                || rightPath.StartsWith($"{leftPath}\\", StringComparison.OrdinalIgnoreCase)
                || leftPath.StartsWith($"{rightPath}\\", StringComparison.OrdinalIgnoreCase)
                || NormalizePath(GetParentPath(leftPath)) == NormalizePath(GetParentPath(rightPath))));
    }

    private static bool ShouldSuppressProvisionalCreate(FileAuditDisplayEvent item, IReadOnlyCollection<FileAuditDisplayEvent> all)
    {
        if (item.Action is not ("created" or "created_or_appended"))
        {
            return false;
        }

        var path = NormalizePath(item.Path);
        var isProvisionalDocument = IsProvisionalDocumentName(item.Path);
        var isProvisionalFolder = IsProvisionalFolderName(item.Path);
        if (!isProvisionalDocument && !isProvisionalFolder)
        {
            return false;
        }

        return all.Any(candidate =>
            candidate.Id != item.Id
            && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(45)
            && (
                (candidate.Action is "renamed" or "moved"
                    && NormalizePath(candidate.PreviousPath) == path
                    && !IsTransientArtifactPath(candidate.Path))));
    }

    private static bool HasDisplayCreation(IEnumerable<FileAuditDisplayEvent> events, string path)
    {
        return events.Any(item => NormalizePath(item.Path) == path && item.Action is "created" or "created_or_appended");
    }

    private static bool HasEarlierDisplayCreation(IEnumerable<FileAuditDisplayEvent> events, string path, DateTimeOffset beforeUtc)
    {
        return events.Any(item =>
            item.TimestampUtc <= beforeUtc
            && NormalizePath(item.Path) == path
            && item.Action is "created" or "created_or_appended");
    }

    private static bool HasDisplayTransitionDestination(IEnumerable<FileAuditDisplayEvent> events, string path, DateTimeOffset beforeUtc)
    {
        return events.Any(item =>
            item.Action is "renamed" or "moved"
            && NormalizePath(item.Path) == NormalizePath(path)
            && item.TimestampUtc <= beforeUtc);
    }

    private static bool HasNearbyDisplayTransition(IEnumerable<FileAuditDisplayEvent> events, string path, DateTimeOffset timestampUtc)
    {
        var normalizedPath = NormalizePath(path);
        return events.Any(item =>
            item.Action is "renamed" or "moved"
            && (item.TimestampUtc - timestampUtc).Duration() <= TimeSpan.FromSeconds(15)
            && (NormalizePath(item.Path) == normalizedPath || NormalizePath(item.PreviousPath) == normalizedPath));
    }

    private static bool HasNearbyCreationAtPath(IEnumerable<ClusterItem> relevant, string path, DateTimeOffset beforeUtc)
    {
        var normalizedPath = NormalizePath(path);
        return relevant.Any(item =>
            item.Event.TimestampUtc <= beforeUtc
            && item.Event.Action is "created" or "created_or_appended"
            && NormalizePath(item.Event.Path) == normalizedPath);
    }

    private static bool HasMaterializedFolderContent(IEnumerable<ClusterItem> relevant, string folderPath, DateTimeOffset beforeUtc)
    {
        var normalizedFolder = NormalizePath(folderPath);
        return relevant.Any(item =>
            item.Event.TimestampUtc <= beforeUtc
            && !string.IsNullOrWhiteSpace(item.Event.Path)
            && NormalizePath(item.Event.Path).StartsWith($"{normalizedFolder}\\", StringComparison.OrdinalIgnoreCase)
            && item.Event.Action is not "accessed");
    }

    private static bool HasNearbyTransitionFromPath(IEnumerable<ClusterItem> relevant, string path, DateTimeOffset timestampUtc)
    {
        var normalizedPath = NormalizePath(path);
        return relevant.Any(item =>
            item.Event.Action is "renamed" or "moved"
            && NormalizePath(item.Event.PreviousPath) == normalizedPath
            && item.Event.TimestampUtc > timestampUtc
            && item.Event.TimestampUtc - timestampUtc <= TimeSpan.FromSeconds(15));
    }

    private static bool HasLikelyInitialDisplayCreation(IEnumerable<FileAuditDisplayEvent> events, string path)
    {
        var all = events.ToArray();
        return all.Any(item => NormalizePath(item.Path) == path && IsLikelyInitialCreationEvent(item, all));
    }

    private static bool HasEarlierRawChange(string path, DateTimeOffset timestampUtc, IEnumerable<FileAuditEvent> rawEvents)
    {
        return rawEvents.Any(item => item.TimestampUtc < timestampUtc && NormalizePath(item.Path) == path && item.Action is "changed" or "modified");
    }

    private static bool HasEarlierStrongRawHistory(string path, DateTimeOffset timestampUtc, IEnumerable<FileAuditEvent> rawEvents)
    {
        return rawEvents.Any(item =>
            item.TimestampUtc < timestampUtc
            && (NormalizePath(item.Path) == path || NormalizePath(item.PreviousPath) == path)
            && IsStrongLifecycleAction(item.Action)
            && !IsIgnorableEarlierRawLifecycle(item, path));
    }

    private static bool HasEstablishedItemAtPathBefore(
        IEnumerable<FileAuditEvent> rawEvents,
        string path,
        DateTimeOffset timestampUtc)
    {
        var normalizedPath = NormalizePath(path);
        var latestLifecycle = rawEvents
            .Where(item => item.TimestampUtc < timestampUtc - CorrelationWindow)
            .Where(item => IsStrongLifecycleAction(item.Action))
            .Where(item => NormalizePath(item.Path) == normalizedPath
                || NormalizePath(item.PreviousPath) == normalizedPath)
            .OrderByDescending(item => item.TimestampUtc)
            .FirstOrDefault();

        if (latestLifecycle is null || latestLifecycle.Action == "deleted")
        {
            return false;
        }

        var movedAway = latestLifecycle.Action is "renamed" or "moved"
            && NormalizePath(latestLifecycle.PreviousPath) == normalizedPath
            && NormalizePath(latestLifecycle.Path) != normalizedPath;
        return !movedAway;
    }

    private static bool HasEarlierNonDeletedStrongRawHistory(string path, DateTimeOffset timestampUtc, IEnumerable<FileAuditEvent> rawEvents)
    {
        return rawEvents.Any(item =>
            item.TimestampUtc < timestampUtc
            && (NormalizePath(item.Path) == path || NormalizePath(item.PreviousPath) == path)
            && item.Action != "deleted"
            && IsStrongLifecycleAction(item.Action)
            && !IsIgnorableEarlierRawLifecycle(item, path));
    }

    private static bool HasNearbyRawDelete(string path, DateTimeOffset timestampUtc, IEnumerable<FileAuditEvent> rawEvents)
    {
        return rawEvents.Any(item =>
            item.Action == "deleted"
            && (item.TimestampUtc - timestampUtc).Duration() <= TimeSpan.FromSeconds(5)
            && PathsReferToSameItem(item.Path, path));
    }

    private static bool HasLaterLifecycleSignal(string path, DateTimeOffset timestampUtc, IEnumerable<FileAuditEvent> rawEvents)
    {
        return rawEvents.Any(item =>
            item.TimestampUtc >= timestampUtc
            && item.TimestampUtc - timestampUtc <= TimeSpan.FromMinutes(5)
            && (NormalizePath(item.Path) == path || NormalizePath(item.PreviousPath) == path)
            && item.Action is "deleted" or "renamed" or "moved");
    }

    private static bool HasNearbyRawTransition(string path, DateTimeOffset timestampUtc, IEnumerable<FileAuditEvent> rawEvents)
    {
        return rawEvents.Any(item =>
            item.Action is "renamed" or "moved"
            && (item.TimestampUtc - timestampUtc).Duration() <= TimeSpan.FromSeconds(15)
            && (NormalizePath(item.Path) == path || NormalizePath(item.PreviousPath) == path));
    }

    private static bool HasLaterAccessEchoSignal(string path, DateTimeOffset timestampUtc, IEnumerable<FileAuditEvent> rawEvents)
    {
        return rawEvents.Any(item =>
            item.Action == "accessed"
            && item.TimestampUtc > timestampUtc
            && item.TimestampUtc - timestampUtc >= TimeSpan.FromSeconds(2)
            && item.TimestampUtc - timestampUtc <= TimeSpan.FromSeconds(30)
            && NormalizePath(item.Path) == path);
    }

    private static bool HasNearbySiblingCreationSignal(FileAuditEvent item, IEnumerable<FileAuditEvent> rawEvents)
    {
        var parent = NormalizePath(GetParentPath(item.Path));
        var path = NormalizePath(item.Path);
        return rawEvents.Any(candidate =>
            candidate.Id != item.Id
            && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(5)
            && NormalizePath(candidate.Path) != path
            && NormalizePath(GetParentPath(candidate.Path)) == parent
            && candidate.Action is "created" or "created_or_appended");
    }

    private static bool HasNearbySiblingInitialCreationSignal(
        FileAuditDisplayEvent item,
        IReadOnlyCollection<FileAuditDisplayEvent> events)
    {
        var parent = NormalizePath(GetParentPath(item.Path));
        var path = NormalizePath(item.Path);
        return events.Any(candidate =>
            candidate.Id != item.Id
            && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(15)
            && NormalizePath(candidate.Path) != path
            && NormalizePath(GetParentPath(candidate.Path)) == parent
            && (candidate.Action is "created" or "created_or_appended"
                || candidate.Action is "changed" or "modified"
                    && candidate.Source.Contains("usn-journal", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool HasNearbyChildCreationSignal(FileAuditEvent item, IEnumerable<FileAuditEvent> rawEvents)
    {
        var folder = NormalizePath(item.Path);
        return rawEvents.Any(candidate =>
            candidate.Id != item.Id
            && (candidate.TimestampUtc - item.TimestampUtc).Duration() <= TimeSpan.FromSeconds(5)
            && NormalizePath(GetParentPath(candidate.Path)) == folder
            && candidate.Action is "created" or "created_or_appended");
    }

    private static bool IsIgnorableEarlierLifecycle(FileAuditDisplayEvent item, string targetPath)
    {
        var path = NormalizePath(item.Path);
        var previousPath = NormalizePath(item.PreviousPath);
        var touchesTransient = IsTransientArtifactPath(item.Path)
            || IsTransientArtifactPath(item.PreviousPath ?? "")
            || IsProvisionalDocumentName(item.Path)
            || IsProvisionalDocumentName(item.PreviousPath ?? "")
            || IsProvisionalFolderName(item.Path)
            || IsProvisionalFolderName(item.PreviousPath ?? "");
        return touchesTransient && (path == targetPath || previousPath == targetPath);
    }

    private static bool IsIgnorableEarlierRawLifecycle(FileAuditEvent item, string targetPath)
    {
        var path = NormalizePath(item.Path);
        var previousPath = NormalizePath(item.PreviousPath);
        var touchesTransient = IsTransientArtifactPath(item.Path)
            || IsTransientArtifactPath(item.PreviousPath ?? "")
            || IsProvisionalDocumentName(item.Path)
            || IsProvisionalDocumentName(item.PreviousPath ?? "")
            || IsProvisionalFolderName(item.Path)
            || IsProvisionalFolderName(item.PreviousPath ?? "");
        return touchesTransient && (path == targetPath || previousPath == targetPath);
    }

    private static bool IsStrongLifecycleAction(string action)
    {
        return action is "created" or "created_or_appended" or "deleted" or "renamed" or "moved";
    }

    private static bool IsCreationEchoWindow(DateTimeOffset creationTimestampUtc, DateTimeOffset changedTimestampUtc)
    {
        if (creationTimestampUtc <= changedTimestampUtc)
        {
            return changedTimestampUtc - creationTimestampUtc <= TimeSpan.FromSeconds(5);
        }

        return creationTimestampUtc - changedTimestampUtc <= TimeSpan.FromSeconds(2);
    }

    private static bool IsNearImmediateCreationEcho(DateTimeOffset creationTimestampUtc, DateTimeOffset changedTimestampUtc)
    {
        if (creationTimestampUtc <= changedTimestampUtc)
        {
            return changedTimestampUtc - creationTimestampUtc <= TimeSpan.FromMilliseconds(750);
        }

        return creationTimestampUtc - changedTimestampUtc <= TimeSpan.FromMilliseconds(750);
    }

    private static bool IsSecurityModifyCreationEcho(DateTimeOffset creationTimestampUtc, DateTimeOffset changedTimestampUtc)
    {
        if (creationTimestampUtc <= changedTimestampUtc)
        {
            return changedTimestampUtc - creationTimestampUtc <= TimeSpan.FromMilliseconds(1500);
        }

        return creationTimestampUtc - changedTimestampUtc <= TimeSpan.FromMilliseconds(750);
    }

    private static string ResolvePathThroughEarlierFolderMoves(
        string path,
        DateTimeOffset timestampUtc,
        IReadOnlyCollection<FileAuditDisplayEvent> events)
    {
        var resolved = path;
        foreach (var move in events
                     .Where(candidate => candidate.Action == "moved"
                         && IsLikelyFolderPath(candidate.Path)
                         && !string.IsNullOrWhiteSpace(candidate.PreviousPath)
                         && candidate.TimestampUtc <= timestampUtc
                         && timestampUtc - candidate.TimestampUtc <= TimeSpan.FromMinutes(5))
                     .OrderBy(candidate => candidate.TimestampUtc))
        {
            var previousRoot = NormalizePath(move.PreviousPath);
            var nextRoot = NormalizePath(move.Path);
            var current = NormalizePath(resolved);
            if (current == previousRoot)
            {
                resolved = move.Path;
                continue;
            }

            if (current.StartsWith($"{previousRoot}\\", StringComparison.OrdinalIgnoreCase))
            {
                resolved = TryRelocatePath(resolved, move.PreviousPath!, move.Path);
            }
        }

        return resolved;
    }

    private static string TryRelocatePath(string path, string previousRootPath, string nextRootPath)
    {
        var current = NormalizePath(path);
        var previousRoot = NormalizePath(previousRootPath);
        if (current == previousRoot)
        {
            return nextRootPath;
        }

        if (!current.StartsWith($"{previousRoot}\\", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var suffix = path.Length >= previousRootPath.Length ? path[previousRootPath.Length..] : "";
        return $"{nextRootPath}{suffix}";
    }

    private static bool HasNearbyTransitionDestination(IEnumerable<ClusterItem> relevant, string path, DateTimeOffset beforeUtc)
    {
        var normalizedPath = NormalizePath(path);
        return relevant.Any(item => item.Event.Action is "renamed" or "moved"
            && NormalizePath(item.Event.Path) == normalizedPath
            && item.Event.TimestampUtc <= beforeUtc);
    }

    private static bool ShouldConsumeTransitionEvent(FileAuditEvent item, string previousPath, string nextPath, DateTimeOffset transitionTimestampUtc, bool isProvisionalOrigin)
    {
        var normalizedPath = NormalizePath(item.Path);
        var normalizedPrevious = NormalizePath(item.PreviousPath);
        var normalizedTransitionPrevious = NormalizePath(previousPath);
        var normalizedTransitionNext = NormalizePath(nextPath);

        if (item.Action is "permission_changed" or "changed" or "modified")
        {
            return false;
        }

        if (item.Action == "accessed")
        {
            var touchesTransitionPath = normalizedPath == normalizedTransitionPrevious
                || normalizedPath == normalizedTransitionNext
                || normalizedPrevious == normalizedTransitionPrevious
                || normalizedPrevious == normalizedTransitionNext;

            return touchesTransitionPath
                && (item.TimestampUtc - transitionTimestampUtc).Duration() <= TimeSpan.FromSeconds(1);
        }

        if (item.Action is "renamed" or "moved")
        {
            return normalizedPath == normalizedTransitionNext && normalizedPrevious == normalizedTransitionPrevious;
        }

        if (!isProvisionalOrigin
            && item.Action is "created" or "created_or_appended"
            && normalizedPath == normalizedTransitionPrevious)
        {
            return false;
        }

        if (isProvisionalOrigin && item.Action == "deleted" && normalizedPath == normalizedTransitionNext)
        {
            return false;
        }

        if (item.Action == "deleted" && normalizedPath == normalizedTransitionNext)
        {
            return false;
        }

        return normalizedPath == normalizedTransitionPrevious
            || normalizedPath == normalizedTransitionNext
            || normalizedPrevious == normalizedTransitionPrevious
            || normalizedPrevious == normalizedTransitionNext
            || (item.Action == "created_or_appended" && normalizedPath == normalizedTransitionNext);
    }

    private static bool SameObjectShape(string leftPath, string rightPath)
    {
        return IsFileLikePath(leftPath) == IsFileLikePath(rightPath);
    }

    private static bool IsLikelySecurityTransitionTarget(string previousPath, string nextPath, string previousExtension)
    {
        if (IsFileLikePath(previousPath))
        {
            if (GetPathExtension(nextPath) != previousExtension)
            {
                return false;
            }

            var sameParent = NormalizePath(GetParentPath(nextPath)) == NormalizePath(GetParentPath(previousPath));
            return sameParent
                ? IsLikelyRenameLeafVariant(previousPath, nextPath)
                : string.Equals(GetLeafName(previousPath), GetLeafName(nextPath), StringComparison.OrdinalIgnoreCase);
        }

        var sameFolder = NormalizePath(GetParentPath(nextPath)) == NormalizePath(GetParentPath(previousPath));
        return sameFolder
            ? string.Equals(GetLeafName(previousPath), GetLeafName(nextPath), StringComparison.OrdinalIgnoreCase)
                || IsLikelyRenameLeafVariant(previousPath, nextPath)
            : string.Equals(GetLeafName(previousPath), GetLeafName(nextPath), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyRenameLeafVariant(string previousPath, string nextPath)
    {
        var previousStem = GetPathStem(previousPath);
        var nextStem = GetPathStem(nextPath);
        if (string.IsNullOrWhiteSpace(previousStem) || previousStem == nextStem || !nextStem.StartsWith(previousStem, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = nextStem[previousStem.Length..];
        return Regex.IsMatch(suffix, @"^[\s._() -]*\d+[\s._() -]*$");
    }

    private static bool IsShareRootPath(FileAuditEvent item)
    {
        var share = item.Share.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(share))
        {
            return false;
        }

        if (!string.Equals(GetLeafName(item.Path), share, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parent = NormalizePath(GetParentPath(item.Path));
        return Regex.IsMatch(parent, @"^[a-z]:$", RegexOptions.IgnoreCase) || Regex.IsMatch(parent, @"^\\\\[^\\]+$");
    }

    private static bool IsShareRootDisplayPath(FileAuditDisplayEvent item)
    {
        var share = item.Share.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(share))
        {
            return false;
        }

        if (!string.Equals(GetLeafName(item.Path), share, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parent = NormalizePath(GetParentPath(item.Path));
        return Regex.IsMatch(parent, @"^[a-z]:$", RegexOptions.IgnoreCase) || Regex.IsMatch(parent, @"^\\\\[^\\]+$");
    }

    private static bool IsRenameLikeAction(string action)
    {
        return action is "renamed" or "changed" or "modified";
    }

    private static bool IsMove(string previousPath, string nextPath)
    {
        return !string.Equals(GetParentPath(previousPath), GetParentPath(nextPath), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFolderEvent(FileAuditDisplayEvent item)
    {
        return item.ObjectType is "folder" or "directory" || IsLikelyFolderPath(item.Path);
    }

    private static bool IsLikelyFolderPath(string path)
    {
        return !GetLeafName(path).Contains('.', StringComparison.Ordinal);
    }

    private static bool IsFileLikePath(string path)
    {
        return GetLeafName(path).Contains('.', StringComparison.Ordinal);
    }

    private static bool IsDescendantPath(string path, string parentPath)
    {
        return NormalizePath(path).StartsWith($"{NormalizePath(parentPath)}\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string? path)
    {
        return (path ?? "").Trim().Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
    }

    private static bool PathsReferToSameItem(string? left, string? right)
    {
        var normalizedLeft = NormalizePath(left);
        var normalizedRight = NormalizePath(right);
        if (normalizedLeft == normalizedRight)
        {
            return true;
        }

        if (!HasReplacementCharacter(normalizedLeft) && !HasReplacementCharacter(normalizedRight))
        {
            return false;
        }

        if (normalizedLeft.Length != normalizedRight.Length)
        {
            return false;
        }

        for (var index = 0; index < normalizedLeft.Length; index++)
        {
            var leftChar = normalizedLeft[index];
            var rightChar = normalizedRight[index];
            if (leftChar != rightChar && leftChar != '\uFFFD' && rightChar != '\uFFFD')
            {
                return false;
            }
        }

        return true;
    }

    private static string GetParentPath(string? path)
    {
        var clean = path ?? "";
        var segments = clean.Split('\\');
        return segments.Length > 1 ? string.Join("\\", segments.Take(segments.Length - 1)) : clean;
    }

    private static string GetLeafName(string path)
    {
        return path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;
    }

    private static string GetPathExtension(string path)
    {
        var leaf = GetLeafName(path);
        var index = leaf.LastIndexOf('.');
        return index >= 0 ? leaf[index..].ToLowerInvariant() : "";
    }

    private static string GetPathStem(string path)
    {
        var leaf = GetLeafName(path).ToLowerInvariant();
        var index = leaf.LastIndexOf('.');
        return index >= 0 ? leaf[..index] : leaf;
    }

    private static bool IsProvisionalDocumentName(string path)
    {
        return GetProvisionalDocumentKind(path) is not null;
    }

    private static bool IsMaterializedProvisionalDocumentRename(string previousPath, string nextPath)
    {
        var previousKind = GetProvisionalDocumentKind(previousPath);
        if (previousKind is null)
        {
            return false;
        }

        var nextKind = GetProvisionalDocumentKind(nextPath);
        return nextKind is null || !string.Equals(previousKind, nextKind, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetProvisionalDocumentKind(string path)
    {
        var leaf = Regex.Replace(GetLeafName(path).ToLowerInvariant(), @" \(\d+\)(?=\.[^.]+$)", "");
        if ((leaf.Contains("documento de texto") || leaf.Contains("new text document")) && leaf.EndsWith(".txt", StringComparison.Ordinal))
        {
            return "text";
        }

        if ((leaf.Contains("planilha") && leaf.Contains("excel") || leaf.Contains("excel worksheet")) && leaf.EndsWith(".xlsx", StringComparison.Ordinal))
        {
            return "excel";
        }

        if ((leaf.Contains("documento") && leaf.Contains("word") || leaf.Contains("word document")) && leaf.EndsWith(".docx", StringComparison.Ordinal))
        {
            return "word";
        }

        if ((leaf.Contains("apresenta") && leaf.Contains("powerpoint") || leaf.Contains("powerpoint presentation")) && leaf.EndsWith(".pptx", StringComparison.Ordinal))
        {
            return "powerpoint";
        }

        if (leaf.Contains("publisher") && leaf.EndsWith(".pub", StringComparison.Ordinal))
        {
            return "publisher";
        }

        if ((leaf.Contains("imagem de bitmap") || leaf.Contains("bitmap image")) && leaf.EndsWith(".bmp", StringComparison.Ordinal))
        {
            return "bitmap";
        }

        return null;
    }

    private static bool IsProvisionalFolderName(string path)
    {
        var leaf = GetLeafName(path).ToLowerInvariant();
        return Regex.IsMatch(leaf, @"^(?:nova pasta|new folder)(?: \(\d+\))?$");
    }

    private static bool IsTransientArtifactPath(string path)
    {
        var leaf = GetLeafName(path).ToLowerInvariant();
        var segments = NormalizePath(path).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(IsTransientContainerSegment)
            || leaf.StartsWith("$", StringComparison.Ordinal)
            || leaf.StartsWith("~$", StringComparison.Ordinal)
            || leaf is "thumbs.db" or "desktop.ini" or "volumejoblock.bin" or "startupprofiledata-noninteractive"
            || leaf.EndsWith(".tmp", StringComparison.Ordinal)
            || Regex.IsMatch(leaf, @"^__psscriptpolicytest_[^.]+\..*\.(?:ps1|psm1)$")
            || Regex.IsMatch(leaf, @"^optimizationstate\.xml(\.|$)")
            || leaf == "optimizationscanlog.sl"
            || Regex.IsMatch(leaf, @"^chunkstorestatistics\.xml(\.|$)")
            || Regex.IsMatch(leaf, @"^dedupstatistics\.xml(\.|$)")
            || Regex.IsMatch(leaf, @"^changes\.optimization\.")
            || Regex.IsMatch(leaf, @"^\d{8}\.\d{8,9}\.(?:\d{2}\.cd|ccc)$");
    }

    private static bool IsTransientContainerSegment(string segment)
    {
        return segment == "$recycle.bin"
            || Regex.IsMatch(segment, @"^\$i[a-z0-9]{5,}$", RegexOptions.IgnoreCase)
            || Regex.IsMatch(segment, @"^\$r[a-z0-9]{5,}$", RegexOptions.IgnoreCase)
            || Regex.IsMatch(segment, @"^\$[a-z0-9]{7,}$", RegexOptions.IgnoreCase);
    }

    private static bool HasReplacementCharacter(string? value)
    {
        return (value ?? "").Contains('\uFFFD', StringComparison.Ordinal);
    }

    private static bool IsUnknownUser(string? user)
    {
        return string.IsNullOrWhiteSpace(user) || user.Trim().Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeUser(string? user)
    {
        return (user ?? "").Trim().ToLowerInvariant();
    }

    private static bool IsPresentString(string? value)
    {
        return !string.IsNullOrWhiteSpace(value);
    }

    private static DateTimeOffset TrimToSecond(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.Offset);
    }

    private static bool IsSyntheticDelete(FileAuditDisplayEvent item)
    {
        return item.Id == StableSyntheticGuid(item.Id, $"deleted:{NormalizePath(item.Path)}");
    }

    private static string FormatAction(string action, FileAuditEvent? item = null)
    {
        if (action == "renamed" && item?.PreviousPath is not null && IsMove(item.PreviousPath, item.Path))
        {
            return "Movido";
        }

        return action switch
        {
            "accessed" => "Acessado",
            "changed" => "Alterado",
            "created" => "Criação",
            "created_or_appended" => "Criação",
            "deleted" => "Excluído",
            "modified" => "Alterado",
            "permission_changed" => "Permissão alterada",
            "renamed" or "renamed_new" or "renamed_old" => "Renomeado",
            "moved" => "Movido",
            _ => action
        };
    }

    private static Guid StableSyntheticGuid(Guid sourceId, string suffix)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{sourceId:N}:{suffix}"));
        return new Guid(bytes[..16]);
    }

    private sealed record ClusterItem(FileAuditEvent Event, int Index);

    private sealed record TransitionResult(IReadOnlyCollection<int> ConsumedIndexes, FileAuditDisplayEvent Event);
}
