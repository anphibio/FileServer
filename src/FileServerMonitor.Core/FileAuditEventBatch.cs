namespace FileServerMonitor.Core;

public static class FileAuditEventBatch
{
    public static IReadOnlyList<FileAuditEvent> SelectUniqueSourceEvidence(
        IEnumerable<FileAuditEvent> events) =>
        SelectUniqueSourceEvidence(events, item => item.AgentId, item => item.SourceEventId);

    public static IReadOnlyList<T> SelectUniqueSourceEvidence<T>(
        IEnumerable<T> events,
        Func<T, string?> agentIdSelector,
        Func<T, string?> sourceEventIdSelector)
    {
        var selected = new List<T>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var auditEvent in events)
        {
            var agentId = agentIdSelector(auditEvent);
            var sourceEventId = sourceEventIdSelector(auditEvent);
            if (string.IsNullOrWhiteSpace(agentId)
                || string.IsNullOrWhiteSpace(sourceEventId)
                || identities.Add($"{agentId}\u001f{sourceEventId}"))
            {
                selected.Add(auditEvent);
            }
        }

        return selected;
    }
}
