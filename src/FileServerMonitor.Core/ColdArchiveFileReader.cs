using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace FileServerMonitor.Core;

public sealed record ColdArchiveReadResult(
    int RecordCount,
    long FileSizeBytes,
    string Sha256,
    IReadOnlyList<JsonElement> Records);

public static class ColdArchiveFileReader
{
    public static async Task<ColdArchiveReadResult> ReadAsync(
        string fullPath,
        string expectedSha256,
        int expectedRecordCount,
        CancellationToken cancellationToken)
    {
        var records = new List<JsonElement>(Math.Max(0, expectedRecordCount));
        await foreach (var batch in ReadBatchesAsync(
            fullPath,
            expectedSha256,
            expectedRecordCount,
            Math.Clamp(expectedRecordCount, 1, 1_000),
            cancellationToken))
        {
            records.AddRange(batch);
        }

        return new ColdArchiveReadResult(
            records.Count,
            new FileInfo(fullPath).Length,
            NormalizeHash(expectedSha256),
            records);
    }

    public static async IAsyncEnumerable<IReadOnlyList<JsonElement>> ReadBatchesAsync(
        string fullPath,
        string expectedSha256,
        int expectedRecordCount,
        int batchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        if (expectedRecordCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRecordCount));
        }

        var normalizedHash = NormalizeHash(expectedSha256);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("O arquivo frio nao foi encontrado.", fullPath);
        }

        await using (var hashStream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var actualHash = await SHA256.HashDataAsync(hashStream, cancellationToken);
            var expectedHash = Convert.FromHexString(normalizedHash);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            {
                throw new InvalidDataException("O SHA-256 do arquivo frio diverge do manifesto.");
            }
        }

        var safeBatchSize = Math.Clamp(batchSize, 1, 10_000);
        var recordsRead = 0;
        var batch = new List<JsonElement>(safeBatchSize);

        await using var file = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: false);
        using var reader = new StreamReader(gzip);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidDataException("O arquivo frio contem uma linha vazia inesperada.");
            }

            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("O arquivo frio contem um registro JSON invalido.");
            }

            batch.Add(document.RootElement.Clone());
            recordsRead++;
            if (recordsRead > expectedRecordCount)
            {
                throw new InvalidDataException("O arquivo frio contem mais registros que o manifesto.");
            }

            if (batch.Count == safeBatchSize)
            {
                yield return batch;
                batch = new List<JsonElement>(safeBatchSize);
            }
        }

        if (recordsRead != expectedRecordCount)
        {
            throw new InvalidDataException(
                $"O arquivo frio contem {recordsRead} registro(s), mas o manifesto informa {expectedRecordCount}.");
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    private static string NormalizeHash(string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        var normalized = expectedSha256.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("O SHA-256 esperado deve conter 64 caracteres hexadecimais.", nameof(expectedSha256));
        }

        return normalized;
    }
}
