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
    string Source);

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
    string? Source);

public static class FileAuditEventNormalizer
{
    public static FileAuditEvent Normalize(FileAuditEventInput input)
    {
        return new FileAuditEvent(
            Id: Guid.NewGuid(),
            TimestampUtc: input.TimestampUtc ?? DateTimeOffset.UtcNow,
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
            Source: Clean(input.Source) ?? "manual-ingest");
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

        if (!string.IsNullOrWhiteSpace(cleanExtension))
        {
            return cleanExtension.StartsWith('.') ? cleanExtension.ToLowerInvariant() : $".{cleanExtension.ToLowerInvariant()}";
        }

        var pathExtension = System.IO.Path.GetExtension(path);

        return string.IsNullOrWhiteSpace(pathExtension) ? null : pathExtension.ToLowerInvariant();
    }
}
