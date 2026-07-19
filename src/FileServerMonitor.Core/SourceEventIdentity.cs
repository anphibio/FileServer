using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FileServerMonitor.Core;

public static class SourceEventIdentity
{
    public static string? Create(
        string? agentId,
        string? cursorType,
        DateTimeOffset timestampUtc,
        long? recordId,
        long? usn,
        string? volume,
        string? fileReferenceId)
    {
        if (string.IsNullOrWhiteSpace(agentId)
            || string.IsNullOrWhiteSpace(cursorType)
            || (recordId is null && usn is null))
        {
            return null;
        }

        var canonical = string.Join('\u001f',
            Normalize(agentId),
            Normalize(cursorType),
            timestampUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
            recordId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            usn?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Normalize(volume),
            Normalize(fileReferenceId));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
}
