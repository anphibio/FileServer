using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileServerMonitor.Core;

public sealed record ColdArchiveFile(
    Guid ArchiveId,
    string Dataset,
    string RelativePath,
    int RecordCount,
    long FileSizeBytes,
    string Sha256,
    DateTimeOffset CreatedUtc);

public static class ColdArchiveFileWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };

    public static async Task<ColdArchiveFile> WriteAsync(
        string archiveRoot,
        string dataset,
        Guid runId,
        int sequence,
        IReadOnlyCollection<IReadOnlyDictionary<string, object?>> records,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataset);
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0)
        {
            throw new ArgumentException("O lote de arquivamento nao pode estar vazio.", nameof(records));
        }

        var safeDataset = NormalizeDataset(dataset);
        var createdUtc = DateTimeOffset.UtcNow;
        var archiveId = Guid.NewGuid();
        var directory = Path.Combine(
            Path.GetFullPath(archiveRoot),
            createdUtc.ToString("yyyy"),
            createdUtc.ToString("MM"),
            createdUtc.ToString("dd"));
        Directory.CreateDirectory(directory);

        var fileName = $"{safeDataset}-{runId:N}-{Math.Max(1, sequence):D6}-{archiveId:N}.jsonl.gz";
        var finalPath = Path.Combine(directory, fileName);
        var temporaryPath = finalPath + ".part";

        try
        {
            await using (var file = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize, leaveOpen: false))
            await using (var writer = new StreamWriter(gzip, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 64 * 1024))
            {
                foreach (var record in records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync(JsonSerializer.Serialize(record, SerializerOptions).AsMemory(), cancellationToken);
                }
            }

            File.Move(temporaryPath, finalPath);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    finalPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            await using var hashStream = new FileStream(
                finalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(hashStream, cancellationToken);
            var relativePath = Path.GetRelativePath(Path.GetFullPath(archiveRoot), finalPath)
                .Replace(Path.DirectorySeparatorChar, '/');

            return new ColdArchiveFile(
                archiveId,
                safeDataset,
                relativePath,
                records.Count,
                new FileInfo(finalPath).Length,
                Convert.ToHexString(hash).ToLowerInvariant(),
                createdUtc);
        }
        catch
        {
            DeleteIfExists(temporaryPath);
            DeleteIfExists(finalPath);
            throw;
        }
    }

    private static string NormalizeDataset(string dataset)
    {
        var normalized = dataset.Trim().ToLowerInvariant();
        if (normalized.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("O nome do conjunto de dados contem caracteres invalidos.", nameof(dataset));
        }

        return normalized;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
