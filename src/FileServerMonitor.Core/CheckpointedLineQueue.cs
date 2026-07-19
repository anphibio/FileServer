using System.Buffers;
using System.Text;

namespace FileServerMonitor.Core;

public sealed record CheckpointedLineQueueFlushResult(
    int SentLines,
    bool Completed,
    long CursorOffset);

public static class CheckpointedLineQueue
{
    private const long DefaultCompactionThresholdBytes = 64L * 1024 * 1024;

    public static async Task AppendAsync(
        string path,
        IEnumerable<string> lines,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(line))
            {
                await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            }
        }

        await writer.FlushAsync(cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<CheckpointedLineQueueFlushResult> FlushAsync(
        string path,
        int batchSize,
        int maxLines,
        Func<IReadOnlyList<string>, CancellationToken, Task<bool>> sendBatchAsync,
        CancellationToken cancellationToken,
        long compactionThresholdBytes = DefaultCompactionThresholdBytes)
    {
        if (!File.Exists(path))
        {
            DeleteCursor(path);
            return new CheckpointedLineQueueFlushResult(0, true, 0);
        }

        var safeBatchSize = Math.Clamp(batchSize, 1, 2_000);
        var safeMaxLines = Math.Max(1, maxLines);
        var cursor = ReadCursor(path);
        var fileLength = new FileInfo(path).Length;
        if (cursor < 0 || cursor > fileLength)
        {
            cursor = 0;
            WriteCursor(path, cursor);
        }

        var sentLines = 0;
        var confirmedCursor = cursor;
        var sendFailed = false;

        await using (var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            stream.Seek(cursor, SeekOrigin.Begin);
            using var reader = new Utf8LineReader(stream, cursor);

            while (sentLines < safeMaxLines)
            {
                var targetBatchSize = Math.Min(safeBatchSize, safeMaxLines - sentLines);
                var batch = new List<string>(targetBatchSize);
                var batchCursor = confirmedCursor;

                while (batch.Count < targetBatchSize)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        break;
                    }

                    batchCursor = line.Value.NextOffset;
                    if (!string.IsNullOrWhiteSpace(line.Value.Value))
                    {
                        batch.Add(line.Value.Value);
                    }
                }

                if (batch.Count == 0)
                {
                    confirmedCursor = batchCursor;
                    break;
                }

                if (!await sendBatchAsync(batch, cancellationToken))
                {
                    sendFailed = true;
                    break;
                }

                sentLines += batch.Count;
                confirmedCursor = batchCursor;
                WriteCursor(path, confirmedCursor);
            }
        }

        fileLength = new FileInfo(path).Length;
        var completed = !sendFailed && confirmedCursor >= fileLength;
        if (completed)
        {
            Delete(path);
            return new CheckpointedLineQueueFlushResult(sentLines, true, 0);
        }

        if (confirmedCursor != cursor)
        {
            WriteCursor(path, confirmedCursor);
        }

        var safeCompactionThreshold = Math.Max(1024 * 1024, compactionThresholdBytes);
        if (confirmedCursor >= safeCompactionThreshold && confirmedCursor >= fileLength / 2)
        {
            Compact(path, confirmedCursor);
            confirmedCursor = 0;
        }

        return new CheckpointedLineQueueFlushResult(sentLines, false, confirmedCursor);
    }

    public static int CountPendingLines(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        var cursor = ReadCursor(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (cursor < 0 || cursor > stream.Length)
        {
            cursor = 0;
        }

        stream.Seek(cursor, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var count = 0;
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                count++;
            }
        }

        return count;
    }

    public static void Delete(string path)
    {
        TryDelete(path);
        DeleteCursor(path);
        TryDelete(GetCompactionPath(path));
    }

    private static void Compact(string path, long cursor)
    {
        var compactPath = GetCompactionPath(path);
        TryDelete(compactPath);

        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(compactPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            input.Seek(cursor, SeekOrigin.Begin);
            input.CopyTo(output, 128 * 1024);
            output.Flush(flushToDisk: true);
        }

        File.Move(compactPath, path, overwrite: true);
        WriteCursor(path, 0);
    }

    private static long ReadCursor(string path)
    {
        var cursorPath = GetCursorPath(path);
        if (!File.Exists(cursorPath))
        {
            return 0;
        }

        try
        {
            return long.TryParse(File.ReadAllText(cursorPath), out var cursor) ? cursor : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static void WriteCursor(string path, long cursor)
    {
        var cursorPath = GetCursorPath(path);
        var tempPath = $"{cursorPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, cursor.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(tempPath, cursorPath, overwrite: true);
    }

    private static void DeleteCursor(string path) => TryDelete(GetCursorPath(path));
    private static string GetCursorPath(string path) => $"{path}.cursor";
    private static string GetCompactionPath(string path) => $"{path}.compact";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A proxima operacao pode concluir a limpeza sem comprometer os dados pendentes.
        }
    }

    private sealed class Utf8LineReader : IDisposable
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        private int _bufferOffset;
        private int _bufferCount;
        private long _nextOffset;
        private bool _disposed;

        public Utf8LineReader(Stream stream, long startOffset)
        {
            _stream = stream;
            _nextOffset = startOffset;
        }

        public async ValueTask<QueueLine?> ReadLineAsync(CancellationToken cancellationToken)
        {
            using var line = new MemoryStream();

            while (true)
            {
                if (_bufferOffset >= _bufferCount)
                {
                    _bufferCount = await _stream.ReadAsync(_buffer.AsMemory(), cancellationToken);
                    _bufferOffset = 0;
                    if (_bufferCount == 0)
                    {
                        DisposeBuffer();
                        if (line.Length == 0)
                        {
                            return null;
                        }

                        return BuildLine(line);
                    }
                }

                var newlineIndex = Array.IndexOf(_buffer, (byte)'\n', _bufferOffset, _bufferCount - _bufferOffset);
                if (newlineIndex < 0)
                {
                    var available = _bufferCount - _bufferOffset;
                    line.Write(_buffer, _bufferOffset, available);
                    _bufferOffset += available;
                    _nextOffset += available;
                    continue;
                }

                var length = newlineIndex - _bufferOffset;
                line.Write(_buffer, _bufferOffset, length);
                _bufferOffset = newlineIndex + 1;
                _nextOffset += length + 1;
                return BuildLine(line);
            }
        }

        private QueueLine BuildLine(MemoryStream line)
        {
            var bytes = line.GetBuffer().AsSpan(0, checked((int)line.Length));
            if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
            {
                bytes = bytes[..^1];
            }

            return new QueueLine(Encoding.UTF8.GetString(bytes), _nextOffset);
        }

        private void DisposeBuffer()
        {
            if (_disposed)
            {
                return;
            }

            ArrayPool<byte>.Shared.Return(_buffer);
            _disposed = true;
        }

        public void Dispose()
        {
            DisposeBuffer();
            GC.SuppressFinalize(this);
        }
    }

    private readonly record struct QueueLine(string Value, long NextOffset);
}
