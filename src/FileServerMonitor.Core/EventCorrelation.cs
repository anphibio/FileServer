namespace FileServerMonitor.Core;

public sealed record CollectedFileEvent(
    string CursorType,
    long? RecordId,
    long? Usn,
    string? Volume,
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
    string? FileReferenceId,
    string Result,
    string Severity,
    string Source);

public sealed class EventCorrelator
{
    private readonly TimeSpan _correlationWindow;

    public EventCorrelator(TimeSpan correlationWindow)
    {
        _correlationWindow = correlationWindow;
    }

    public IReadOnlyCollection<CollectedFileEvent> Correlate(IReadOnlyCollection<CollectedFileEvent> events)
    {
        var preparedEvents = CollapseUsnRenamePairs(events).ToArray();
        var securityEvents = events
            .Where(item => item.CursorType.Equals("security", StringComparison.OrdinalIgnoreCase))
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .Where(item => !string.IsNullOrWhiteSpace(item.User) && !item.User.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (securityEvents.Length == 0)
        {
            return preparedEvents.Select(FinalizeCorrelatedEvent).ToArray();
        }

        var matchedSecurityRecordIds = new HashSet<long>();
        var correlatedEvents = new List<CollectedFileEvent>(preparedEvents.Length);

        foreach (var item in preparedEvents)
        {
            if (!item.CursorType.Equals("usn", StringComparison.OrdinalIgnoreCase))
            {
                correlatedEvents.Add(item);
                continue;
            }

            var matches = FindCompatibleSecurityMatches(item, securityEvents).ToArray();
            var match = matches.FirstOrDefault();

            if (match is null)
            {
                correlatedEvents.Add(item);
                continue;
            }

            foreach (var candidate in matches)
            {
                if (candidate.RecordId is not null && ShouldSuppressSecurityMatch(item, candidate))
                {
                    matchedSecurityRecordIds.Add(candidate.RecordId.Value);
                }
            }

            correlatedEvents.Add(item with
            {
                User = match.User,
                Sid = item.Sid ?? match.Sid,
                SourceHost = item.SourceHost ?? match.SourceHost,
                SourceIp = item.SourceIp ?? match.SourceIp,
                ProcessName = item.ProcessName == "fsutil.exe" ? match.ProcessName : item.ProcessName,
                Action = GetCorrelatedAction(item, match),
                Severity = "info",
                Source = "usn-journal+security-log"
            });
        }

        return correlatedEvents
            .Select(FinalizeCorrelatedEvent)
            .Where(item =>
                !item.CursorType.Equals("security", StringComparison.OrdinalIgnoreCase)
                || item.RecordId is null
                || !matchedSecurityRecordIds.Contains(item.RecordId.Value))
            .ToArray();
    }

    private IEnumerable<CollectedFileEvent> CollapseUsnRenamePairs(IReadOnlyCollection<CollectedFileEvent> events)
    {
        var ordered = events
            .OrderBy(item => item.CursorType.Equals("usn", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.Usn ?? long.MaxValue)
            .ThenBy(item => item.RecordId ?? long.MaxValue)
            .ThenBy(item => item.TimestampUtc)
            .ToArray();

        var consumed = new HashSet<int>();

        for (var index = 0; index < ordered.Length; index++)
        {
            if (consumed.Contains(index))
            {
                continue;
            }

            var current = ordered[index];

            if (!current.CursorType.Equals("usn", StringComparison.OrdinalIgnoreCase))
            {
                yield return current;
                continue;
            }

            if (!IsRenameMarker(current.Action))
            {
                if (TryBuildRenameFromUsnNoise(ordered, current, index, consumed, out var inferredRename))
                {
                    yield return inferredRename;
                    continue;
                }

                yield return current;
                continue;
            }

            var pairIndex = FindRenamePairIndex(ordered, current, index);

            if (pairIndex >= 0)
            {
                var pair = ordered[pairIndex];
                var oldEvent = current.Action.Equals("renamed_old", StringComparison.OrdinalIgnoreCase) ? current : pair;
                var newEvent = current.Action.Equals("renamed_new", StringComparison.OrdinalIgnoreCase) ? current : pair;

                foreach (var noiseIndex in FindRenameNoiseIndexes(ordered, oldEvent, newEvent, index, pairIndex))
                {
                    consumed.Add(noiseIndex);
                }

                yield return newEvent with
                {
                    PreviousPath = oldEvent.Path,
                    Action = ClassifyPathTransition(oldEvent.Path, newEvent.Path),
                    ObjectType = PromoteObjectType(oldEvent.ObjectType, newEvent.ObjectType)
                };

                consumed.Add(pairIndex);
                continue;
            }

            yield return current with { Action = "renamed" };
        }
    }

    private static bool IsRenameMarker(string action)
    {
        return action.Equals("renamed_old", StringComparison.OrdinalIgnoreCase)
            || action.Equals("renamed_new", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsnNoiseAction(string action)
    {
        return action is "changed" or "modified" or "created" or "created_or_appended";
    }

    private bool TryBuildRenameFromUsnNoise(
        CollectedFileEvent[] ordered,
        CollectedFileEvent current,
        int currentIndex,
        ISet<int> consumed,
        out CollectedFileEvent inferredRename)
    {
        inferredRename = default!;

        if (!current.CursorType.Equals("usn", StringComparison.OrdinalIgnoreCase)
            || !IsUsnNoiseAction(current.Action)
            || string.IsNullOrWhiteSpace(current.FileReferenceId))
        {
            return false;
        }

        var pairIndex = ordered
            .Select((candidate, candidateIndex) => new { candidate, candidateIndex })
            .Where(item => item.candidateIndex != currentIndex)
            .Where(item => !consumed.Contains(item.candidateIndex))
            .Where(item => item.candidate.CursorType.Equals("usn", StringComparison.OrdinalIgnoreCase))
            .Where(item => IsUsnNoiseAction(item.candidate.Action))
            .Where(item => string.Equals(item.candidate.Volume, current.Volume, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.candidate.Server.Equals(current.Server, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.candidate.Share.Equals(current.Share, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.Equals(item.candidate.FileReferenceId, current.FileReferenceId, StringComparison.OrdinalIgnoreCase))
            .Where(item => !NormalizePath(item.candidate.Path).Equals(NormalizePath(current.Path), StringComparison.OrdinalIgnoreCase))
            .Where(item => (item.candidate.TimestampUtc - current.TimestampUtc).Duration() <= _correlationWindow)
            .OrderBy(item => item.candidate.TimestampUtc)
            .ThenBy(item => item.candidate.Usn ?? long.MaxValue)
            .FirstOrDefault()?.candidateIndex ?? -1;

        if (pairIndex < 0)
        {
            return false;
        }

        var pair = ordered[pairIndex];
        var orderedPair = new[] { current, pair }
            .OrderBy(item => item.TimestampUtc)
            .ThenBy(item => item.Usn ?? long.MaxValue)
            .ToArray();
        var oldEvent = orderedPair[0];
        var newEvent = orderedPair[1];

        if (NormalizePath(oldEvent.Path).Equals(NormalizePath(newEvent.Path), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var noiseIndex in FindRenameNoiseIndexes(ordered, oldEvent, newEvent, currentIndex, pairIndex))
        {
            consumed.Add(noiseIndex);
        }

        consumed.Add(pairIndex);
        inferredRename = newEvent with
        {
            PreviousPath = oldEvent.Path,
            Action = ClassifyPathTransition(oldEvent.Path, newEvent.Path),
            ObjectType = PromoteObjectType(oldEvent.ObjectType, newEvent.ObjectType)
        };
        return true;
    }

    private static CollectedFileEvent FinalizeCorrelatedEvent(CollectedFileEvent item)
    {
        if ((item.Action.Equals("changed", StringComparison.OrdinalIgnoreCase)
                || item.Action.Equals("modified", StringComparison.OrdinalIgnoreCase))
            && IsProvisionalDocumentPath(item.Path)
            && string.IsNullOrWhiteSpace(item.PreviousPath))
        {
            return item with { Action = "created" };
        }

        if (item.Action.Equals("created", StringComparison.OrdinalIgnoreCase)
            && IsProvisionalDocumentPath(item.PreviousPath))
        {
            return item with { PreviousPath = null };
        }

        return item;
    }

    private static string GetCorrelatedAction(CollectedFileEvent usnEvent, CollectedFileEvent securityEvent)
    {
        if ((usnEvent.Action.Equals("changed", StringComparison.OrdinalIgnoreCase)
                || usnEvent.Action.Equals("modified", StringComparison.OrdinalIgnoreCase))
            && securityEvent.Action.Equals("created_or_appended", StringComparison.OrdinalIgnoreCase)
            && IsProvisionalDocumentPath(usnEvent.Path)
            && NormalizePath(usnEvent.Path).Equals(NormalizePath(securityEvent.Path), StringComparison.OrdinalIgnoreCase))
        {
            return "created";
        }

        return usnEvent.Action;
    }

    private static string ClassifyPathTransition(string previousPath, string nextPath)
    {
        if (IsProvisionalDocumentPath(previousPath))
        {
            return "created";
        }

        return IsMove(previousPath, nextPath) ? "moved" : "renamed";
    }

    private static bool IsMove(string previousPath, string nextPath)
    {
        var previousParent = GetParentPath(previousPath);
        var nextParent = GetParentPath(nextPath);

        return !previousParent.Equals(nextParent, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetParentPath(string path)
    {
        var normalized = NormalizePath(path);
        var separatorIndex = normalized.LastIndexOf('\\');

        return separatorIndex <= 0 ? normalized : normalized[..separatorIndex];
    }

    private static bool IsProvisionalDocumentPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var leafName = GetLeafName(path).ToLowerInvariant();

        return leafName.StartsWith("novo documento de texto", StringComparison.Ordinal)
            || leafName.StartsWith("new text document", StringComparison.Ordinal)
            || leafName.StartsWith("nova imagem de bitmap", StringComparison.Ordinal)
            || leafName.StartsWith("new bitmap image", StringComparison.Ordinal)
            || leafName.StartsWith("novo(a) planilha do microsoft excel", StringComparison.Ordinal)
            || leafName.StartsWith("new microsoft excel worksheet", StringComparison.Ordinal)
            || leafName.StartsWith("novo(a) documento do microsoft word", StringComparison.Ordinal)
            || leafName.StartsWith("new microsoft word document", StringComparison.Ordinal)
            || (leafName.StartsWith("novo(a) apresenta", StringComparison.Ordinal)
                && leafName.Contains("microsoft powerpoint", StringComparison.Ordinal))
            || leafName.StartsWith("new microsoft powerpoint presentation", StringComparison.Ordinal)
            || leafName.StartsWith("novo(a) microsoft publisher document", StringComparison.Ordinal)
            || leafName.StartsWith("new microsoft publisher document", StringComparison.Ordinal);
    }

    private static string GetLeafName(string path)
    {
        var normalized = NormalizePath(path);
        var separatorIndex = normalized.LastIndexOf('\\');

        return separatorIndex < 0 ? normalized : normalized[(separatorIndex + 1)..];
    }

    private IEnumerable<int> FindRenameNoiseIndexes(
        CollectedFileEvent[] ordered,
        CollectedFileEvent oldEvent,
        CollectedFileEvent newEvent,
        int currentIndex,
        int pairIndex)
    {
        var fileReferenceId = string.IsNullOrWhiteSpace(newEvent.FileReferenceId)
            ? oldEvent.FileReferenceId
            : newEvent.FileReferenceId;
        var pathCandidates = new[] { NormalizePath(oldEvent.Path), NormalizePath(newEvent.Path) }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (string.IsNullOrWhiteSpace(fileReferenceId) && pathCandidates.Length == 0)
        {
            return Array.Empty<int>();
        }

        return ordered
            .Select((candidate, candidateIndex) => new { candidate, candidateIndex })
            .Where(item => item.candidateIndex != currentIndex && item.candidateIndex != pairIndex)
            .Where(item => item.candidate.CursorType.Equals("usn", StringComparison.OrdinalIgnoreCase))
            .Where(item => !IsRenameMarker(item.candidate.Action))
            .Where(item => IsUsnNoiseAction(item.candidate.Action))
            .Where(item => string.Equals(item.candidate.Volume, newEvent.Volume, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.candidate.Server.Equals(newEvent.Server, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.candidate.Share.Equals(newEvent.Share, StringComparison.OrdinalIgnoreCase))
            .Where(item =>
                (!string.IsNullOrWhiteSpace(fileReferenceId)
                    && fileReferenceId.Equals(item.candidate.FileReferenceId, StringComparison.OrdinalIgnoreCase))
                || pathCandidates.Contains(NormalizePath(item.candidate.Path), StringComparer.OrdinalIgnoreCase))
            .Where(item => (item.candidate.TimestampUtc - newEvent.TimestampUtc).Duration() <= _correlationWindow)
            .Select(item => item.candidateIndex)
            .ToArray();
    }


    private static int FindRenamePairIndex(CollectedFileEvent[] ordered, CollectedFileEvent current, int currentIndex)
    {
        var targetAction = current.Action.Equals("renamed_old", StringComparison.OrdinalIgnoreCase)
            ? "renamed_new"
            : "renamed_old";

        var candidates = ordered
            .Select((candidate, candidateIndex) => new { candidate, candidateIndex })
            .Where(item => item.candidateIndex != currentIndex)
            .Where(item => item.candidate.CursorType.Equals("usn", StringComparison.OrdinalIgnoreCase))
            .Where(item => item.candidate.Action.Equals(targetAction, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.Equals(item.candidate.Volume, current.Volume, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.candidate.Server.Equals(current.Server, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.candidate.Share.Equals(current.Share, StringComparison.OrdinalIgnoreCase))
            .Select(item => new
            {
                item.candidateIndex,
                SameFileReference =
                    !string.IsNullOrWhiteSpace(current.FileReferenceId)
                    && current.FileReferenceId.Equals(item.candidate.FileReferenceId, StringComparison.OrdinalIgnoreCase),
                UsnDistance = Math.Abs((item.candidate.Usn ?? long.MaxValue) - (current.Usn ?? long.MaxValue)),
                IndexDistance = Math.Abs(item.candidateIndex - currentIndex),
                TimeDistance = (item.candidate.TimestampUtc - current.TimestampUtc).Duration(),
                PreferForward = current.Action.Equals("renamed_old", StringComparison.OrdinalIgnoreCase)
                    ? item.candidateIndex > currentIndex
                    : item.candidateIndex < currentIndex
            })
            .Where(item => item.SameFileReference || item.IndexDistance == 1)
            .OrderByDescending(item => item.SameFileReference)
            .ThenByDescending(item => item.PreferForward)
            .ThenBy(item => item.UsnDistance)
            .ThenBy(item => item.TimeDistance)
            .ThenBy(item => item.IndexDistance)
            .FirstOrDefault();

        return candidates?.candidateIndex ?? -1;
    }

    private static string PromoteObjectType(string currentType, string nextType)
    {
        if (string.Equals(nextType, "file", StringComparison.OrdinalIgnoreCase)
            || string.Equals(currentType, "file", StringComparison.OrdinalIgnoreCase))
        {
            return "file";
        }

        if (string.Equals(nextType, "directory", StringComparison.OrdinalIgnoreCase)
            || string.Equals(currentType, "directory", StringComparison.OrdinalIgnoreCase))
        {
            return "directory";
        }

        return nextType;
    }

    private IEnumerable<CollectedFileEvent> FindCompatibleSecurityMatches(
        CollectedFileEvent usnEvent,
        IReadOnlyCollection<CollectedFileEvent> securityEvents)
    {
        var pathCandidates = new[] { NormalizePath(usnEvent.Path), NormalizePath(usnEvent.PreviousPath) }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (pathCandidates.Length == 0)
        {
            return Array.Empty<CollectedFileEvent>();
        }

        return securityEvents
            .Where(item => item.CursorType.Equals("security", StringComparison.OrdinalIgnoreCase))
            .Select(item => new
            {
                Event = item,
                TimeDistance = (usnEvent.TimestampUtc - item.TimestampUtc).Duration(),
                PathScore = pathCandidates.Max(candidate => GetPathScore(candidate, NormalizePath(item.Path)))
            })
            .Where(item => item.TimeDistance <= _correlationWindow)
            .Where(item => item.PathScore > 0)
            .OrderByDescending(item => item.PathScore)
            .ThenBy(item => item.TimeDistance)
            .Select(item => item.Event);
    }

    private static bool ShouldSuppressSecurityMatch(CollectedFileEvent usnEvent, CollectedFileEvent securityEvent)
    {
        if (string.IsNullOrWhiteSpace(securityEvent.Path))
        {
            return false;
        }

        var normalizedSecurityPath = NormalizePath(securityEvent.Path);
        var usnCandidates = new[] { usnEvent.Path, usnEvent.PreviousPath }
            .Select(NormalizePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (usnCandidates.Length == 0)
        {
            return false;
        }

        var compatiblePath = usnCandidates.Any(candidate =>
            candidate.Equals(normalizedSecurityPath, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(normalizedSecurityPath, StringComparison.OrdinalIgnoreCase)
            || normalizedSecurityPath.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));

        if (!compatiblePath)
        {
            return false;
        }

        return securityEvent.Action is "accessed" or "created_or_appended" or "modified" or "deleted" or "renamed";
    }

    public static int GetPathScore(string usnPath, string securityPath)
    {
        if (string.IsNullOrWhiteSpace(usnPath) || string.IsNullOrWhiteSpace(securityPath))
        {
            return 0;
        }

        if (usnPath.Equals(securityPath, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        var usnFileName = Path.GetFileName(usnPath);
        var securityFileName = Path.GetFileName(securityPath);

        if (!string.IsNullOrWhiteSpace(usnFileName)
            && usnFileName.Equals(securityFileName, StringComparison.OrdinalIgnoreCase))
        {
            return 60;
        }

        return securityPath.Contains(usnPath, StringComparison.OrdinalIgnoreCase)
            || usnPath.Contains(securityPath, StringComparison.OrdinalIgnoreCase)
            ? 30
            : 0;
    }

    public static string NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim().Replace('/', '\\').TrimEnd('\\');
    }
}
