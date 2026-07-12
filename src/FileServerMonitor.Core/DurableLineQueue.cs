namespace FileServerMonitor.Core;

public sealed record DurableLineQueueFlushResult(int SentLines, bool Completed);

public static class DurableLineQueue
{
    public static async Task<DurableLineQueueFlushResult> FlushAsync(
        string path,
        int batchSize,
        int maxLines,
        Func<IReadOnlyList<string>, CancellationToken, Task<bool>> sendBatchAsync,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new DurableLineQueueFlushResult(0, true);
        }

        var safeBatchSize = Math.Clamp(batchSize, 1, 2_000);
        var safeMaxLines = Math.Max(safeBatchSize, maxLines);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var sentLines = 0;
        var hasRemainingLines = false;

        try
        {
            using var input = new StreamReader(path);
            await using var output = new StreamWriter(tempPath, append: false);
            var batch = new List<string>(safeBatchSize);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await input.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                batch.Add(line);
                if (batch.Count < safeBatchSize && sentLines + batch.Count < safeMaxLines)
                {
                    continue;
                }

                if (!await sendBatchAsync(batch, cancellationToken))
                {
                    await WriteLinesAsync(output, batch, cancellationToken);
                    hasRemainingLines = true;
                    await CopyRemainingLinesAsync(input, output, cancellationToken);
                    batch.Clear();
                    break;
                }

                sentLines += batch.Count;
                batch.Clear();

                if (sentLines >= safeMaxLines)
                {
                    hasRemainingLines = await CopyRemainingLinesAsync(input, output, cancellationToken);
                    break;
                }
            }

            if (batch.Count > 0)
            {
                if (await sendBatchAsync(batch, cancellationToken))
                {
                    sentLines += batch.Count;
                }
                else
                {
                    await WriteLinesAsync(output, batch, cancellationToken);
                    hasRemainingLines = true;
                }
            }

            await output.FlushAsync(cancellationToken);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        if (hasRemainingLines)
        {
            File.Move(tempPath, path, overwrite: true);
            return new DurableLineQueueFlushResult(sentLines, false);
        }

        TryDelete(tempPath);
        File.Delete(path);
        return new DurableLineQueueFlushResult(sentLines, true);
    }

    private static async Task WriteLinesAsync(StreamWriter output, IEnumerable<string> lines, CancellationToken cancellationToken)
    {
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await output.WriteLineAsync(line.AsMemory(), cancellationToken);
        }
    }

    private static async Task<bool> CopyRemainingLinesAsync(StreamReader input, StreamWriter output, CancellationToken cancellationToken)
    {
        var copied = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await input.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            await output.WriteLineAsync(line.AsMemory(), cancellationToken);
            copied = true;
        }

        return copied;
    }

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
            // O arquivo temporario nao afeta a fila original; uma proxima execucao pode removê-lo.
        }
    }
}
