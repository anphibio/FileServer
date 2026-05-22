namespace FileServerMonitor.Core;

public sealed record AlertOptions(
    int WindowMinutes = 5,
    int DedupMinutes = 10,
    int MassDeleteThreshold = 50,
    int MassRenameThreshold = 100,
    int RansomwareActivityThreshold = 250,
    int SuspiciousExtensionThreshold = 10,
    bool MassDeleteEnabled = true,
    string MassDeleteSeverity = "critical",
    bool MassRenameEnabled = true,
    string MassRenameSeverity = "critical",
    bool RansomwareEnabled = true,
    string RansomwareSeverity = "high",
    string RansomwareCriticalSeverity = "critical",
    bool PermissionChangeEnabled = true,
    string PermissionChangeSeverity = "high");

public sealed record FileServerAlert(
    Guid Id,
    string Rule,
    string Severity,
    string Status,
    string Title,
    string Description,
    string Server,
    string User,
    int EventCount,
    DateTimeOffset FirstEventUtc,
    DateTimeOffset LastEventUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? AcknowledgedUtc,
    string[] SamplePaths,
    string DedupKey);

public sealed class AlertRuleEngine
{
    private static readonly string[] RansomwareLikeActions =
    {
        "modified",
        "renamed",
        "deleted",
        "created",
        "created_or_appended"
    };

    private static readonly string[] SuspiciousExtensions =
    {
        ".lock",
        ".locked",
        ".crypt",
        ".crypted",
        ".crypto",
        ".encrypt",
        ".encrypted",
        ".enc",
        ".pay",
        ".ransom"
    };

    private readonly AlertOptions _options;

    public AlertRuleEngine(AlertOptions options)
    {
        _options = options;
    }

    public IReadOnlyCollection<FileServerAlert> Evaluate(IReadOnlyCollection<FileAuditEvent> events)
    {
        var alerts = new List<FileServerAlert>();

        foreach (var group in events.GroupBy(item => new AlertScope(item.Server, item.User)))
        {
            var scopeEvents = group.ToArray();

            alerts.AddRange(DetectMassDelete(group.Key, scopeEvents));
            alerts.AddRange(DetectMassRename(group.Key, scopeEvents));
            alerts.AddRange(DetectRansomwarePattern(group.Key, scopeEvents));
        }

        alerts.AddRange(DetectPermissionChanges(events));

        return alerts;
    }

    private IReadOnlyCollection<FileServerAlert> DetectMassDelete(AlertScope scope, IReadOnlyCollection<FileAuditEvent> events)
    {
        if (!_options.MassDeleteEnabled)
        {
            return Array.Empty<FileServerAlert>();
        }

        var deleted = events
            .Where(item => item.Action.Equals("deleted", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (deleted.Length < _options.MassDeleteThreshold)
        {
            return Array.Empty<FileServerAlert>();
        }

        return new[]
        {
            CreateAlert(
                rule: "mass-delete",
                severity: _options.MassDeleteSeverity,
                title: "Exclusao em massa detectada",
                description: $"{deleted.Length} exclusoes por {scope.User} em {_options.WindowMinutes} minutos.",
                scope,
                deleted)
        };
    }

    private IReadOnlyCollection<FileServerAlert> DetectMassRename(AlertScope scope, IReadOnlyCollection<FileAuditEvent> events)
    {
        if (!_options.MassRenameEnabled)
        {
            return Array.Empty<FileServerAlert>();
        }

        var renamed = events
            .Where(item => item.Action.Equals("renamed", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (renamed.Length < _options.MassRenameThreshold)
        {
            return Array.Empty<FileServerAlert>();
        }

        return new[]
        {
            CreateAlert(
                rule: "mass-rename",
                severity: _options.MassRenameSeverity,
                title: "Renomeacao em massa detectada",
                description: $"{renamed.Length} renomeacoes por {scope.User} em {_options.WindowMinutes} minutos.",
                scope,
                renamed)
        };
    }

    private IReadOnlyCollection<FileServerAlert> DetectRansomwarePattern(AlertScope scope, IReadOnlyCollection<FileAuditEvent> events)
    {
        if (!_options.RansomwareEnabled)
        {
            return Array.Empty<FileServerAlert>();
        }

        var suspicious = events
            .Where(item => RansomwareLikeActions.Contains(item.Action, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        var suspiciousExtensionCount = suspicious.Count(item =>
            !string.IsNullOrWhiteSpace(item.Extension)
            && SuspiciousExtensions.Contains(item.Extension, StringComparer.OrdinalIgnoreCase));

        if (suspicious.Length < _options.RansomwareActivityThreshold
            && suspiciousExtensionCount < _options.SuspiciousExtensionThreshold)
        {
            return Array.Empty<FileServerAlert>();
        }

        var severity = suspiciousExtensionCount >= _options.SuspiciousExtensionThreshold
            ? _options.RansomwareCriticalSeverity
            : _options.RansomwareSeverity;

        return new[]
        {
            CreateAlert(
                rule: "possible-ransomware",
                severity,
                title: "Possivel comportamento de ransomware",
                description: $"{suspicious.Length} alteracoes suspeitas e {suspiciousExtensionCount} extensoes suspeitas por {scope.User}.",
                scope,
                suspicious)
        };
    }

    private IReadOnlyCollection<FileServerAlert> DetectPermissionChanges(IReadOnlyCollection<FileAuditEvent> events)
    {
        if (!_options.PermissionChangeEnabled)
        {
            return Array.Empty<FileServerAlert>();
        }

        return events
            .Where(item => item.Action.Equals("permission_changed", StringComparison.OrdinalIgnoreCase))
            .Select(item => CreateAlert(
                rule: "permission-change",
                severity: _options.PermissionChangeSeverity,
                title: "Alteracao de permissao detectada",
                description: $"{item.User} alterou permissoes em {item.Path}.",
                scope: new AlertScope(item.Server, item.User),
                events: new[] { item }))
            .ToArray();
    }

    private FileServerAlert CreateAlert(
        string rule,
        string severity,
        string title,
        string description,
        AlertScope scope,
        IReadOnlyCollection<FileAuditEvent> events)
    {
        var firstEvent = events.OrderBy(item => item.TimestampUtc).First();
        var lastEvent = events.OrderByDescending(item => item.TimestampUtc).First();
        var samplePaths = events
            .Select(item => item.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();

        return new FileServerAlert(
            Id: Guid.NewGuid(),
            Rule: rule,
            Severity: severity,
            Status: "open",
            Title: title,
            Description: description,
            Server: scope.Server,
            User: scope.User,
            EventCount: events.Count,
            FirstEventUtc: firstEvent.TimestampUtc,
            LastEventUtc: lastEvent.TimestampUtc,
            CreatedUtc: DateTimeOffset.UtcNow,
            AcknowledgedUtc: null,
            SamplePaths: samplePaths,
            DedupKey: $"{rule}:{scope.Server}:{scope.User}");
    }

    private sealed record AlertScope(string Server, string User);
}
