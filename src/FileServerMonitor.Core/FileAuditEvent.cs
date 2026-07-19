namespace FileServerMonitor.Core;

public sealed record FileAuditEvent(
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
    string? AgentId = null,
    string? SourceEventId = null,
    string? CursorType = null,
    long? RecordId = null,
    long? Usn = null,
    string? Volume = null,
    string? FileReferenceId = null);

public sealed record FileAuditEventInput(
    DateTimeOffset? TimestampUtc,
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
    string? Result,
    string? Severity,
    string? Source,
    string? AgentId = null,
    string? CursorType = null,
    long? RecordId = null,
    long? Usn = null,
    string? Volume = null,
    string? FileReferenceId = null);

public static class FileAuditEventNormalizer
{
    public static FileAuditEvent Normalize(FileAuditEventInput input)
    {
        var timestampUtc = input.TimestampUtc ?? DateTimeOffset.UtcNow;
        var agentId = Clean(input.AgentId);
        var cursorType = Clean(input.CursorType)?.ToLowerInvariant();
        var volume = Clean(input.Volume)?.ToUpperInvariant();
        var fileReferenceId = Clean(input.FileReferenceId);

        return new FileAuditEvent(
            Id: Guid.NewGuid(),
            TimestampUtc: timestampUtc,
            Server: Required(input.Server, nameof(input.Server)),
            Share: Required(input.Share, nameof(input.Share)),
            Path: Required(input.Path, nameof(input.Path)),
            PreviousPath: Clean(input.PreviousPath),
            ObjectType: Required(input.ObjectType, nameof(input.ObjectType)).ToLowerInvariant(),
            Action: Required(input.Action, nameof(input.Action)).ToLowerInvariant(),
            User: Required(input.User, nameof(input.User)),
            Sid: Clean(input.Sid),
            SourceHost: Clean(input.SourceHost),
            SourceIp: Clean(input.SourceIp),
            ProcessName: Clean(input.ProcessName),
            FileSizeBytes: input.FileSizeBytes,
            Extension: NormalizeExtension(input.Extension, input.Path),
            Result: Clean(input.Result) ?? "success",
            Severity: Clean(input.Severity) ?? "info",
            Source: Clean(input.Source) ?? "manual-ingest",
            AgentId: agentId,
            SourceEventId: SourceEventIdentity.Create(
                agentId,
                cursorType,
                timestampUtc,
                input.RecordId,
                input.Usn,
                volume,
                fileReferenceId),
            CursorType: cursorType,
            RecordId: input.RecordId,
            Usn: input.Usn,
            Volume: volume,
            FileReferenceId: fileReferenceId);
    }

    private static string Required(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"O campo {fieldName} e obrigatorio.", fieldName);
        }

        return value.Trim();
    }

    private static string? Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? NormalizeExtension(string? extension, string path)
    {
        var cleanExtension = Clean(extension);

        if (cleanExtension is not null && IsValidExtension(cleanExtension))
        {
            return cleanExtension.StartsWith('.') ? cleanExtension.ToLowerInvariant() : $".{cleanExtension.ToLowerInvariant()}";
        }

        var pathExtension = System.IO.Path.GetExtension(path);

        return IsValidExtension(pathExtension) ? pathExtension.ToLowerInvariant() : null;
    }

    private static bool IsValidExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        var value = extension.Trim();
        var normalizedLength = value.StartsWith('.') ? value.Length : value.Length + 1;

        return normalizedLength is > 1 and <= 32
            && !value.Contains('\\')
            && !value.Contains('/')
            && !value.Any(char.IsWhiteSpace);
    }
}
