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
        var securityEvents = events
            .Where(item => item.CursorType.Equals("security", StringComparison.OrdinalIgnoreCase))
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .Where(item => !string.IsNullOrWhiteSpace(item.User) && !item.User.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (securityEvents.Length == 0)
        {
            return events;
        }

        var matchedSecurityRecordIds = new HashSet<long>();
        var correlatedEvents = new List<CollectedFileEvent>(events.Count);

        foreach (var item in events)
        {
            if (!item.CursorType.Equals("usn", StringComparison.OrdinalIgnoreCase))
            {
                correlatedEvents.Add(item);
                continue;
            }

            var match = FindBestSecurityMatch(item, securityEvents);

            if (match is null)
            {
                correlatedEvents.Add(item);
                continue;
            }

            if (match.RecordId is not null && ShouldSuppressSecurityMatch(item, match))
            {
                matchedSecurityRecordIds.Add(match.RecordId.Value);
            }

            correlatedEvents.Add(item with
            {
                User = match.User,
                Sid = item.Sid ?? match.Sid,
                SourceHost = item.SourceHost ?? match.SourceHost,
                SourceIp = item.SourceIp ?? match.SourceIp,
                ProcessName = item.ProcessName == "fsutil.exe" ? match.ProcessName : item.ProcessName,
                Severity = "info",
                Source = "usn-journal+security-log"
            });
        }

        return correlatedEvents
            .Where(item =>
                !item.CursorType.Equals("security", StringComparison.OrdinalIgnoreCase)
                || item.RecordId is null
                || !matchedSecurityRecordIds.Contains(item.RecordId.Value))
            .ToArray();
    }

    private CollectedFileEvent? FindBestSecurityMatch(
        CollectedFileEvent usnEvent,
        IReadOnlyCollection<CollectedFileEvent> securityEvents)
    {
        var normalizedUsnPath = NormalizePath(usnEvent.Path);

        return securityEvents
            .Select(item => new
            {
                Event = item,
                TimeDistance = (usnEvent.TimestampUtc - item.TimestampUtc).Duration(),
                PathScore = GetPathScore(normalizedUsnPath, NormalizePath(item.Path))
            })
            .Where(item => item.TimeDistance <= _correlationWindow)
            .Where(item => item.PathScore > 0)
            .OrderByDescending(item => item.PathScore)
            .ThenBy(item => item.TimeDistance)
            .Select(item => item.Event)
            .FirstOrDefault();
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
