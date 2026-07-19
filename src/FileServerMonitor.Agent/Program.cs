using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var configPath = args.FirstOrDefault(arg => arg.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
    ?? "appsettings.agent.json";

var options = AgentOptions.Load(configPath);
var state = AgentState.Load(options.StateFile);
var agent = new FileServerAgent(options, state);

if (OperatingSystem.IsWindows() && !Environment.UserInteractive)
{
    WindowsServiceRuntime.Run("FileServerMonitorAgent", cancellationToken => agent.RunAsync(cancellationToken, handleConsoleCancel: false));
}
else
{
    await agent.RunAsync(CancellationToken.None, handleConsoleCancel: true);
}

internal sealed class FileServerAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly AgentOptions _options;
    private readonly AgentState _state;
    private readonly HttpClient _httpClient;
    private AgentConfigResponse? _remoteConfig;
    private DateTimeOffset? _lastRemoteConfigFetchUtc;
    private FileServerMonitor.Core.AgentCycleMetrics? _lastCycle;
    private DateTimeOffset? _lastInventoryScanStartedUtc;
    private DateTimeOffset? _lastInventoryScanRequestUtc;
    private Task? _inventoryScanTask;
    private int _sentFromQueueSinceLastCollection;

    public FileServerAgent(AgentOptions options, AgentState state)
    {
        _options = options;
        _state = state;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(options.ApiBaseUrl),
            Timeout = TimeSpan.FromSeconds(options.ApiRequestTimeoutSeconds)
        };

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
        }

        _httpClient.DefaultRequestHeaders.Add("X-Agent-Id", options.AgentId);
    }

    public async Task RunAsync(CancellationToken cancellationToken, bool handleConsoleCancel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_options.StateFile) ?? ".");
        Directory.CreateDirectory(Path.GetDirectoryName(_options.QueueFile) ?? ".");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler? cancelHandler = null;

        if (handleConsoleCancel)
        {
            cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cts.Cancel();
            };

            Console.CancelKeyPress += cancelHandler;
        }

        Console.WriteLine($"FileServerMonitor.Agent iniciado. AgentId={_options.AgentId}; Server={_options.Server}; Api={_options.ApiBaseUrl}");

        try
        {
            while (!cts.IsCancellationRequested)
            {
                var cycleStartedUtc = DateTimeOffset.UtcNow;
                try
                {
                    await RefreshRemoteConfigAsync(cts.Token);
                    await FlushQueueAsync(cts.Token);
                    await CollectAndSendAsync(cts.Token);
                    TryStartInventoryScan(cts.Token);
                    await SendHeartbeatAsync("running", null, cts.Token);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Falha no ciclo de coleta: {ex.Message}");
                    if (_lastCycle?.StartedUtc is null || _lastCycle.StartedUtc < cycleStartedUtc)
                    {
                        _lastCycle = BuildErrorCycle(cycleStartedUtc, ex);
                    }

                    await SendHeartbeatAsync("degraded", ex.Message, CancellationToken.None);
                }

                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), cts.Token);
            }
        }
        finally
        {
            if (cancelHandler is not null)
            {
                Console.CancelKeyPress -= cancelHandler;
            }

            await SendHeartbeatAsync("stopped", "Agente finalizado.", CancellationToken.None);
        }
    }

    private async Task CollectAndSendAsync(CancellationToken cancellationToken)
    {
        var cycleStartedUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var sentEvents = Interlocked.Exchange(ref _sentFromQueueSinceLastCollection, 0);
        var queuedEvents = 0;
        string? cycleError = null;
        CollectionResult? result = null;

        try
        {
            result = await CollectEventsAsync(cancellationToken);
            var collected = result.Events
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .Pipe(DeduplicateCollectedEvents)
            .ToArray();

            if (collected.Length == 0)
            {
                AdvanceState(result.CursorAdvances);
                _state.Save(_options.StateFile);
                return;
            }

            _state.LastCollectedEventUtc = MaxTimestamp(collected, _state.LastCollectedEventUtc);

            var eventsToSend = _options.SendSecurityLogEvents
                ? collected
                : collected
                    .Where(item => !item.CursorType.Equals("security", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

            if (eventsToSend.Length == 0)
            {
                AdvanceState(collected.Concat(result.CursorAdvances).ToArray());
                _state.LastSuccessfulSendUtc = DateTimeOffset.UtcNow;
                _state.Save(_options.StateFile);
                return;
            }

            var events = eventsToSend.Select(item => item.ToApiRequest(_options.AgentId)).ToArray();
            var sentCount = await TrySendBatchAsync(events, cancellationToken);

            if (sentCount < events.Length)
            {
                var unsentEvents = eventsToSend.Skip(sentCount).ToArray();
                await AppendQueueAsync(unsentEvents, cancellationToken);
                AdvanceState(collected.Concat(result.CursorAdvances).ToArray());
                _state.Save(_options.StateFile);
                sentEvents = sentCount;
                queuedEvents = unsentEvents.Length;
                return;
            }

            sentEvents = eventsToSend.Length;
            AdvanceState(collected.Concat(result.CursorAdvances).ToArray());
            _state.LastSuccessfulSendUtc = DateTimeOffset.UtcNow;
            _state.Save(_options.StateFile);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            cycleError = ex.Message;
            throw;
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _lastCycle = BuildCycleMetrics(cycleStartedUtc, stopwatch.ElapsedMilliseconds, result, sentEvents, queuedEvents, cycleError);
            }
        }
    }

    private void TryStartInventoryScan(CancellationToken cancellationToken)
    {
        var settings = GetEffectiveInventoryScanSettings();
        if (settings?.Enabled != true || settings.Roots.Length == 0)
        {
            return;
        }

        if (_inventoryScanTask is { IsCompleted: false })
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var forceRun = settings.RunRequestedUtc is not null
            && (_lastInventoryScanRequestUtc is null || settings.RunRequestedUtc > _lastInventoryScanRequestUtc);

        if (!forceRun && _lastInventoryScanStartedUtc is not null
            && now - _lastInventoryScanStartedUtc.Value < TimeSpan.FromHours(Math.Max(1, settings.IntervalHours)))
        {
            return;
        }

        if (!forceRun && !IsInsideInventoryWindow(now, settings))
        {
            return;
        }

        _lastInventoryScanStartedUtc = now;
        if (forceRun)
        {
            _lastInventoryScanRequestUtc = settings.RunRequestedUtc;
            Console.WriteLine($"Inventario: scan manual solicitado em {settings.RunRequestedUtc:O}");
        }

        _inventoryScanTask = Task.Run(async () =>
        {
            foreach (var root in settings.Roots.Where(item => !string.IsNullOrWhiteSpace(item.Path)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RunInventoryScanAsync(root, settings, cancellationToken);
            }
        }, cancellationToken);
    }

    private async Task RunInventoryScanAsync(
        InventoryScanRoot root,
        InventoryScanOptions settings,
        CancellationToken cancellationToken)
    {
        var rootPath = Path.GetFullPath(root.Path);
        Console.WriteLine($"Inventario: iniciando scan em {rootPath}");
        InventorySnapshotStartResponse? snapshot = null;
        var sentItems = 0;
        var errors = 0;

        try
        {
            snapshot = await StartInventorySnapshotAsync(root, rootPath, cancellationToken);
            var batch = new List<InventoryItemRequest>(Math.Clamp(settings.BatchSize, 100, 2000));
            foreach (var item in EnumerateInventoryItems(root, rootPath, settings, cancellationToken))
            {
                if (item.Error is not null)
                {
                    errors++;
                }

                batch.Add(item);
                if (batch.Count >= Math.Clamp(settings.BatchSize, 100, 2000))
                {
                    await SendInventoryBatchAsync(snapshot.Id, batch, cancellationToken);
                    sentItems += batch.Count;
                    batch.Clear();
                }

                if (settings.MaxItemsPerScan > 0 && sentItems + batch.Count >= settings.MaxItemsPerScan)
                {
                    break;
                }
            }

            if (batch.Count > 0)
            {
                await SendInventoryBatchAsync(snapshot.Id, batch, cancellationToken);
                sentItems += batch.Count;
            }

            var status = errors > 0 ? "completed_with_errors" : "completed";
            await CompleteInventorySnapshotAsync(snapshot.Id, status, errors > 0 ? $"{errors} erro(s) durante o scan." : null, cancellationToken);
            Console.WriteLine($"Inventario: scan concluido em {rootPath}; itens={sentItems}; erros={errors}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Inventario: falha no scan de {rootPath}: {ex.Message}");
            if (snapshot is not null)
            {
                await CompleteInventorySnapshotAsync(snapshot.Id, "failed", ex.Message, CancellationToken.None);
            }
        }
    }

    private IEnumerable<InventoryItemRequest> EnumerateInventoryItems(
        InventoryScanRoot root,
        string rootPath,
        InventoryScanOptions settings,
        CancellationToken cancellationToken)
    {
        var stack = new Stack<string>();
        stack.Push(rootPath);
        var itemCount = 0;

        while (stack.Count > 0)
        {
            if (settings.MaxItemsPerScan > 0 && itemCount >= settings.MaxItemsPerScan)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();

            DirectoryInfo? directoryInfo = null;
            InventoryItemRequest directoryItem;
            try
            {
                directoryInfo = new DirectoryInfo(current);
                directoryItem = BuildInventoryItem(root, rootPath, directoryInfo, "folder", settings.IncludeLastAccessTime, error: null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                directoryItem = BuildInventoryError(root, rootPath, current, "folder", ex.Message);
            }

            itemCount++;
            yield return directoryItem;

            if (directoryItem.Error is not null)
            {
                continue;
            }

            IEnumerable<string> children;
            InventoryItemRequest? directoryError = null;
            try
            {
                children = Directory.EnumerateFileSystemEntries(directoryInfo!.FullName).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                children = Array.Empty<string>();
                directoryError = BuildInventoryError(root, rootPath, directoryInfo!.FullName, "folder", ex.Message);
            }

            if (directoryError is not null)
            {
                itemCount++;
                yield return directoryError;
                continue;
            }

            foreach (var child in children)
            {
                if (settings.MaxItemsPerScan > 0 && itemCount >= settings.MaxItemsPerScan)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(child))
                {
                    stack.Push(child);
                    continue;
                }

                FileInfo fileInfo;
                InventoryItemRequest fileItem;
                try
                {
                    fileInfo = new FileInfo(child);
                    fileItem = BuildInventoryItem(root, rootPath, fileInfo, "file", settings.IncludeLastAccessTime, error: null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    fileItem = BuildInventoryError(root, rootPath, child, "file", ex.Message);
                }

                itemCount++;
                yield return fileItem;
            }
        }
    }

    private InventoryItemRequest BuildInventoryItem(
        InventoryScanRoot root,
        string rootPath,
        FileSystemInfo info,
        string itemType,
        bool includeLastAccessTime,
        string? error)
    {
        var sizeBytes = info is FileInfo fileInfo ? fileInfo.Length : 0;
        return new InventoryItemRequest(
            ScannedAtUtc: DateTimeOffset.UtcNow,
            Server: string.IsNullOrWhiteSpace(root.Server) ? _options.Server : root.Server,
            Share: string.IsNullOrWhiteSpace(root.Share) ? _options.DefaultShare : root.Share,
            RootPath: rootPath,
            Path: info.FullName,
            RelativePath: Path.GetRelativePath(rootPath, info.FullName),
            Name: info.Name,
            ItemType: itemType,
            SizeBytes: sizeBytes,
            CreatedUtc: ToUtc(info.CreationTimeUtc),
            ModifiedUtc: ToUtc(info.LastWriteTimeUtc),
            AccessedUtc: includeLastAccessTime ? ToUtc(info.LastAccessTimeUtc) : null,
            Error: error);
    }

    private InventoryItemRequest BuildInventoryError(
        InventoryScanRoot root,
        string rootPath,
        string path,
        string itemType,
        string error)
    {
        return new InventoryItemRequest(
            ScannedAtUtc: DateTimeOffset.UtcNow,
            Server: string.IsNullOrWhiteSpace(root.Server) ? _options.Server : root.Server,
            Share: string.IsNullOrWhiteSpace(root.Share) ? _options.DefaultShare : root.Share,
            RootPath: rootPath,
            Path: path,
            RelativePath: Path.GetRelativePath(rootPath, path),
            Name: Path.GetFileName(path),
            ItemType: itemType,
            SizeBytes: 0,
            CreatedUtc: null,
            ModifiedUtc: null,
            AccessedUtc: null,
            Error: error);
    }

    private async Task<InventorySnapshotStartResponse> StartInventorySnapshotAsync(
        InventoryScanRoot root,
        string rootPath,
        CancellationToken cancellationToken)
    {
        var request = new InventorySnapshotStartRequest(
            Server: string.IsNullOrWhiteSpace(root.Server) ? _options.Server : root.Server,
            Share: string.IsNullOrWhiteSpace(root.Share) ? _options.DefaultShare : root.Share,
            RootPath: rootPath);
        using var response = await _httpClient.PostAsJsonAsync("/api/inventory/snapshots/start", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InventorySnapshotStartResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("API nao retornou o snapshot de inventario.");
    }

    private async Task SendInventoryBatchAsync(Guid snapshotId, IReadOnlyCollection<InventoryItemRequest> items, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync($"/api/inventory/snapshots/{snapshotId}/items", new InventoryItemBatchRequest(items.ToArray()), JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task CompleteInventorySnapshotAsync(Guid snapshotId, string status, string? error, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync($"/api/inventory/snapshots/{snapshotId}/complete", new InventorySnapshotCompleteRequest(status, error), JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static bool IsInsideInventoryWindow(DateTimeOffset now, InventoryScanOptions settings)
    {
        if (string.IsNullOrWhiteSpace(settings.WindowStartLocal) || string.IsNullOrWhiteSpace(settings.WindowEndLocal))
        {
            return true;
        }

        if (!TimeOnly.TryParse(settings.WindowStartLocal, out var start)
            || !TimeOnly.TryParse(settings.WindowEndLocal, out var end))
        {
            return true;
        }

        var current = TimeOnly.FromDateTime(now.LocalDateTime);
        return start <= end
            ? current >= start && current <= end
            : current >= start || current <= end;
    }

    private static DateTimeOffset? ToUtc(DateTime value)
    {
        return value == default ? null : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private async Task<CollectionResult> CollectEventsAsync(CancellationToken cancellationToken)
    {
        var collected = new List<CollectedFileEvent>();
        var securityEventsRead = 0;
        var usnEventsRead = 0;

        if (_options.EnableSecurityLogCollector)
        {
            var scriptPath = Path.GetFullPath(_options.SecurityLogScriptPath);
            var arguments = BuildSecurityLogArguments(scriptPath);
            var securityEvents = await RunCollectorScriptAsync("Security Log", arguments, _options.SecurityLogTimeoutSeconds, cancellationToken);
            securityEventsRead = securityEvents.Count;
            Console.WriteLine($"Coleta Security: lastRecordId={_state.LastRecordId}; recebidos={securityEvents.Count}");
            collected.AddRange(securityEvents);
        }

        if (_options.EnableUsnJournalCollector)
        {
            foreach (var volume in GetEffectiveUsnVolumes())
            {
                var scriptPath = Path.GetFullPath(_options.UsnJournalScriptPath);
                var startUsn = _state.LastUsnByVolume.TryGetValue(volume, out var value)
                    ? value
                    : 0;
                var basePath = GetEffectiveUsnBasePath(volume);
                var arguments = BuildUsnJournalArguments(scriptPath, volume, startUsn, basePath);
                var usnEvents = await RunCollectorScriptAsync($"USN {volume}", arguments, _options.UsnJournalTimeoutSeconds, cancellationToken);
                var visibleUsnEvents = usnEvents.Count(item => !IsCursorAdvance(item));
                usnEventsRead += visibleUsnEvents;
                Console.WriteLine($"Coleta USN: volume={volume}; startUsn={startUsn}; basePath={basePath}; recebidos={visibleUsnEvents}");
                collected.AddRange(usnEvents);
            }
        }

        var cursorAdvances = collected.Where(IsCursorAdvance).ToArray();
        var collectedEvents = collected.Where(item => !IsCursorAdvance(item)).ToArray();
        var output = _options.EnableCorrelation
            ? CorrelateEvents(collectedEvents)
            : collectedEvents;

        var filtered = FilterByRemoteConfig(output)
            .OrderBy(item => item.Usn ?? long.MaxValue)
            .ThenBy(item => item.RecordId ?? long.MaxValue)
            .ThenBy(item => item.TimestampUtc)
            .ToArray();

        Console.WriteLine($"Coleta final: brutos={collectedEvents.Length}; pos-correlacao={output.Count}; pos-filtro={filtered.Length}");
        return new CollectionResult(
            Events: filtered,
            CursorAdvances: cursorAdvances,
            RawEvents: collectedEvents.Length,
            SecurityEventsRead: securityEventsRead,
            UsnEventsRead: usnEventsRead,
            CorrelatedEvents: output.Count);
    }

    private static bool IsCursorAdvance(CollectedFileEvent item)
    {
        return item.CursorType.Equals("usn_checkpoint", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyCollection<CollectedFileEvent>> RunCollectorScriptAsync(
        string collectorName,
        string arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.PowerShellPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Nao foi possivel iniciar o PowerShell.");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            throw new TimeoutException($"Coletor {collectorName} excedeu o limite de {timeoutSeconds} segundo(s).");
        }

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"PowerShell retornou codigo {process.ExitCode}: {error}");
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return Array.Empty<CollectedFileEvent>();
        }

        return JsonSerializer.Deserialize<CollectedFileEvent[]>(output, JsonOptions)
            ?? Array.Empty<CollectedFileEvent>();
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // O timeout ja sera reportado no heartbeat; falha ao encerrar o filho nao deve esconder a causa.
        }
    }

    private string BuildSecurityLogArguments(string scriptPath)
    {
        var parts = new List<string>
        {
            "-NoProfile",
            "-ExecutionPolicy Bypass",
            "-File",
            Quote(scriptPath),
            "-LastRecordId",
            _state.LastRecordId.ToString(),
            "-MaxEvents",
            _options.BatchSize.ToString(),
            "-EventIds",
            string.Join(",", _options.EventIds)
        };
        parts.AddRange(new[]
        {
            "-ServerName",
            Quote(_options.Server),
            "-DefaultShare",
            Quote(GetEffectiveDefaultShare())
        });

        return string.Join(" ", parts);
    }

    private string BuildUsnJournalArguments(string scriptPath, string volume, long startUsn, string basePath)
    {
        var knownPathMapPath = WriteKnownPathMapFile(volume);
        var parts = new List<string>
        {
            "-NoProfile",
            "-ExecutionPolicy Bypass",
            "-File",
            Quote(scriptPath),
            "-Volume",
            Quote(volume),
            "-BasePath",
            Quote(basePath),
            "-StartUsn",
            startUsn.ToString(),
            "-MaxEvents",
            _options.BatchSize.ToString(),
            "-ServerName",
            Quote(_options.Server),
            "-DefaultShare",
            Quote(GetEffectiveDefaultShare())
        };

        if (!string.IsNullOrWhiteSpace(knownPathMapPath))
        {
            parts.Add("-KnownPathByFileIdJsonPath");
            parts.Add(Quote(knownPathMapPath));
        }

        return string.Join(" ", parts);
    }

    private string? WriteKnownPathMapFile(string volume)
    {
        if (!_state.KnownPathByFileIdByVolume.TryGetValue(volume, out var knownPaths) || knownPaths.Count == 0)
        {
            return null;
        }

        var stateDirectory = Path.GetDirectoryName(_options.StateFile) ?? ".";
        Directory.CreateDirectory(stateDirectory);
        var safeVolume = volume.Replace(":", "", StringComparison.Ordinal).Replace('\\', '_').Replace('/', '_');
        var path = Path.Combine(stateDirectory, $"known-paths-{safeVolume}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(knownPaths, JsonOptions), Encoding.UTF8);
        return path;
    }

    private string GetEffectiveUsnBasePath(string volume)
    {
        var normalizedVolume = NormalizePathPrefix(volume);

        if (_options.EnableRemoteConfig && (_remoteConfig?.MonitoredPaths.Length ?? 0) > 0)
        {
            var matchingPath = _remoteConfig!.MonitoredPaths
                .Where(item => item.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
                .Select(item => NormalizePathPrefix(item.Path))
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item)
                    && item.StartsWith(normalizedVolume, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Length)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(matchingPath))
            {
                return matchingPath;
            }
        }

        return normalizedVolume;
    }

    private async Task FlushQueueAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.QueueFile))
        {
            return;
        }

        var sentEvents = 0;
        var drainPlan = FileServerMonitor.Core.DurableLineQueue.CreateDrainPlan(
            _options.ApiBatchSize,
            _options.QueueFlushBatchesPerCycle,
            _options.QueueFlushMaxEventsPerCycle);
        var result = await FileServerMonitor.Core.DurableLineQueue.FlushAsync(
            _options.QueueFile,
            drainPlan.BatchSize,
            drainPlan.MaxLines,
            async (lines, token) =>
            {
                var queued = lines
                    .Select(line => JsonSerializer.Deserialize<CollectedFileEvent>(line, JsonOptions))
                    .Where(item => item is not null)
                    .Cast<CollectedFileEvent>()
                    .ToArray();

                if (queued.Length != lines.Count)
                {
                    Console.Error.WriteLine("Fila local contem evento invalido; mantendo lote para nova tentativa.");
                    return false;
                }

                var requests = queued.Select(item => item.ToApiRequest(_options.AgentId)).ToArray();
                if (await TrySendBatchAsync(requests, token) != requests.Length)
                {
                    return false;
                }

                // Mantem compatibilidade com filas antigas, criadas antes do cursor passar a avancar no enqueue.
                AdvanceState(queued);
                sentEvents += queued.Length;
                return true;
            },
            cancellationToken);

        if (sentEvents <= 0)
        {
            return;
        }

        _sentFromQueueSinceLastCollection += sentEvents;
        _state.LastSuccessfulSendUtc = DateTimeOffset.UtcNow;
        _state.Save(_options.StateFile);
        Console.WriteLine($"Fila local: enviados={sentEvents}; pendente={(result.Completed ? 0 : "sim")}.");
    }

    private async Task<int> TrySendBatchAsync(FileAuditEventRequest[] events, CancellationToken cancellationToken)
    {
        if (events.Length == 0)
        {
            return 0;
        }

        var sentCount = 0;
        try
        {
            foreach (var chunk in events.Chunk(_options.ApiBatchSize))
            {
                using var response = await _httpClient.PostAsJsonAsync("/api/events/batch", chunk, JsonOptions, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    sentCount += chunk.Length;
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.Error.WriteLine($"API rejeitou lote: {(int)response.StatusCode} {body}");
                return sentCount;
            }

            return sentCount;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"API indisponivel: {ex.Message}");
            return sentCount;
        }
    }

    private async Task AppendQueueAsync(IReadOnlyCollection<CollectedFileEvent> events, CancellationToken cancellationToken)
    {
        var lines = events.Select(item => JsonSerializer.Serialize(item, JsonOptions));
        await File.AppendAllLinesAsync(_options.QueueFile, lines, cancellationToken);
    }

    private async Task SendHeartbeatAsync(string status, string? message, CancellationToken cancellationToken)
    {
        var heartbeat = new AgentHeartbeatRequest(
            AgentId: _options.AgentId,
            Server: _options.Server,
            Status: status,
            Version: typeof(FileServerAgent).Assembly.GetName().Version?.ToString() ?? "dev",
            LastRecordId: _state.LastRecordId,
            LastUsnByVolume: _state.LastUsnByVolume,
            Message: message ?? BuildHeartbeatMessage(),
            PendingQueueEvents: CountPendingQueueEvents(),
            LastSuccessfulSendUtc: _state.LastSuccessfulSendUtc,
            LastCollectedEventUtc: _state.LastCollectedEventUtc,
            LastCycle: _lastCycle);

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/api/agents/heartbeat", heartbeat, JsonOptions, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.Error.WriteLine($"API rejeitou heartbeat: {(int)response.StatusCode} {body}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"API indisponivel para heartbeat: {ex.Message}");
        }
    }

    private static string Quote(string value)
    {
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private async Task RefreshRemoteConfigAsync(CancellationToken cancellationToken)
    {
        if (!_options.EnableRemoteConfig)
        {
            return;
        }

        if (_lastRemoteConfigFetchUtc is not null
            && DateTimeOffset.UtcNow - _lastRemoteConfigFetchUtc.Value < TimeSpan.FromMinutes(_options.RemoteConfigRefreshMinutes))
        {
            return;
        }

        try
        {
            var path = $"/api/agents/config?server={Uri.EscapeDataString(_options.Server)}";
            var config = await _httpClient.GetFromJsonAsync<AgentConfigResponse>(path, JsonOptions, cancellationToken);

            if (config is not null)
            {
                _remoteConfig = config;
                _lastRemoteConfigFetchUtc = DateTimeOffset.UtcNow;
                Console.WriteLine($"Configuracao remota aplicada. Caminhos ativos={config.MonitoredPaths.Length}; Volumes USN={string.Join(",", config.UsnVolumes)}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Console.Error.WriteLine($"Nao foi possivel carregar configuracao remota: {ex.Message}");
        }
    }

    private string[] GetEffectiveUsnVolumes()
    {
        if (_options.EnableRemoteConfig && _remoteConfig?.UsnVolumes.Length > 0)
        {
            return _remoteConfig.UsnVolumes;
        }

        return _options.UsnVolumes;
    }

    private string GetEffectiveDefaultShare()
    {
        if (_options.EnableRemoteConfig && !string.IsNullOrWhiteSpace(_remoteConfig?.DefaultShare))
        {
            return _remoteConfig.DefaultShare;
        }

        return _options.DefaultShare;
    }

    private InventoryScanOptions? GetEffectiveInventoryScanSettings()
    {
        if (_options.EnableRemoteConfig && _remoteConfig?.InventoryScan is { } remote)
        {
            return new InventoryScanOptions(
                Enabled: remote.Enabled,
                IntervalHours: remote.IntervalHours,
                BatchSize: remote.BatchSize,
                MaxItemsPerScan: remote.MaxItemsPerScan,
                IncludeLastAccessTime: remote.IncludeLastAccessTime,
                WindowStartLocal: remote.WindowStartLocal,
                WindowEndLocal: remote.WindowEndLocal,
                RunRequestedUtc: remote.RunRequestedUtc,
                Roots: string.IsNullOrWhiteSpace(remote.RootPath)
                    ? Array.Empty<InventoryScanRoot>()
                    : new[]
                    {
                        new InventoryScanRoot(
                            Path: remote.RootPath,
                            Server: string.IsNullOrWhiteSpace(remote.Server) ? _options.Server : remote.Server,
                            Share: string.IsNullOrWhiteSpace(remote.Share) ? GetEffectiveDefaultShare() : remote.Share)
                    });
        }

        return _options.InventoryScan;
    }

    private IReadOnlyCollection<CollectedFileEvent> FilterByRemoteConfig(IReadOnlyCollection<CollectedFileEvent> events)
    {
        if (!_options.EnableRemoteConfig || !_options.FilterToConfiguredPaths || (_remoteConfig?.MonitoredPaths.Length ?? 0) == 0)
        {
            return events;
        }

        var roots = _remoteConfig?.MonitoredPaths
            .Where(item => item.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            .Select(item => NormalizePathPrefix(item.Path))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        if (roots.Length == 0)
        {
            return events;
        }

        return events
            .Where(item =>
            {
                var candidates = new[] { NormalizePathPrefix(item.Path), NormalizePathPrefix(item.PreviousPath) }
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToArray();

                return candidates.Length > 0
                    && roots.Any(root =>
                        candidates.Any(candidate => candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)));
            })
            .ToArray();
    }

    private string? BuildHeartbeatMessage()
    {
        if (!_options.EnableRemoteConfig || _remoteConfig is null)
        {
            return null;
        }

        return $"Config remota: {_remoteConfig.MonitoredPaths.Length} caminho(s) ativo(s).";
    }

    private static string NormalizePathPrefix(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim().TrimEnd('\\', '/');
    }

    private static DateTimeOffset? MaxTimestamp(
        IReadOnlyCollection<CollectedFileEvent> events,
        DateTimeOffset? current)
    {
        if (events.Count == 0)
        {
            return current;
        }

        var max = events.Max(item => item.TimestampUtc);

        return current is null || max > current
            ? max
            : current;
    }

    private static FileServerMonitor.Core.AgentCycleMetrics BuildErrorCycle(DateTimeOffset startedUtc, Exception ex)
    {
        return BuildCycleMetrics(startedUtc, (long)DateTimeOffset.UtcNow.Subtract(startedUtc).TotalMilliseconds, null, 0, 0, ex.Message);
    }

    private static FileServerMonitor.Core.AgentCycleMetrics BuildCycleMetrics(
        DateTimeOffset startedUtc,
        long durationMs,
        CollectionResult? result,
        int sentEvents,
        int queuedEvents,
        string? error)
    {
        return new FileServerMonitor.Core.AgentCycleMetrics(
            StartedUtc: startedUtc,
            FinishedUtc: DateTimeOffset.UtcNow,
            DurationMs: Math.Max(0, durationMs),
            SecurityEventsRead: result?.SecurityEventsRead ?? 0,
            UsnEventsRead: result?.UsnEventsRead ?? 0,
            CorrelatedEvents: result?.CorrelatedEvents ?? 0,
            SentEvents: sentEvents,
            QueuedEvents: queuedEvents,
            Error: error);
    }

    private int CountPendingQueueEvents()
    {
        if (!File.Exists(_options.QueueFile))
        {
            return 0;
        }

        try
        {
            return File.ReadLines(_options.QueueFile)
                .Count(line => !string.IsNullOrWhiteSpace(line));
        }
        catch (IOException)
        {
            return -1;
        }
    }

    private static FileServerMonitor.Core.CollectedFileEvent ToCoreEvent(CollectedFileEvent item)
    {
        return new FileServerMonitor.Core.CollectedFileEvent(
            CursorType: item.CursorType,
            RecordId: item.RecordId,
            Usn: item.Usn,
            Volume: item.Volume,
            TimestampUtc: item.TimestampUtc,
            Server: item.Server,
            Share: item.Share,
            Path: item.Path,
            PreviousPath: item.PreviousPath,
            ObjectType: item.ObjectType,
            Action: item.Action,
            User: item.User,
            Sid: item.Sid,
            SourceHost: item.SourceHost,
            SourceIp: item.SourceIp,
            ProcessName: item.ProcessName,
            FileSizeBytes: item.FileSizeBytes,
            Extension: item.Extension,
            FileReferenceId: item.FileReferenceId,
            Result: item.Result,
            Severity: item.Severity,
            Source: item.Source);
    }

    private static CollectedFileEvent FromCoreEvent(FileServerMonitor.Core.CollectedFileEvent item)
    {
        return new CollectedFileEvent(
            CursorType: item.CursorType,
            RecordId: item.RecordId,
            Usn: item.Usn,
            Volume: item.Volume,
            TimestampUtc: item.TimestampUtc,
            Server: item.Server,
            Share: item.Share,
            Path: item.Path,
            PreviousPath: item.PreviousPath,
            ObjectType: item.ObjectType,
            Action: item.Action,
            User: item.User,
            Sid: item.Sid,
            SourceHost: item.SourceHost,
            SourceIp: item.SourceIp,
            ProcessName: item.ProcessName,
            FileSizeBytes: item.FileSizeBytes,
            Extension: item.Extension,
            FileReferenceId: item.FileReferenceId,
            Result: item.Result,
            Severity: item.Severity,
            Source: item.Source);
    }

    private IReadOnlyCollection<CollectedFileEvent> CorrelateEvents(IReadOnlyCollection<CollectedFileEvent> events)
    {
        var correlator = new FileServerMonitor.Core.EventCorrelator(
            TimeSpan.FromSeconds(_options.CorrelationWindowSeconds));

        return correlator
            .Correlate(events.Select(ToCoreEvent).ToArray())
            .Select(FromCoreEvent)
            .ToArray();
    }

    private void AdvanceState(IReadOnlyCollection<CollectedFileEvent> events)
    {
        foreach (var item in events)
        {
            if (item.RecordId is not null)
            {
                _state.LastRecordId = Math.Max(_state.LastRecordId, item.RecordId.Value);
            }

            if (item.Usn is not null && !string.IsNullOrWhiteSpace(item.Volume))
            {
                var current = _state.LastUsnByVolume.TryGetValue(item.Volume, out var value)
                    ? value
                    : 0;

                _state.LastUsnByVolume[item.Volume] = Math.Max(current, item.Usn.Value);

                UpdateKnownPathMap(item);
            }
        }
    }

    private void UpdateKnownPathMap(CollectedFileEvent item)
    {
        if (string.IsNullOrWhiteSpace(item.Volume)
            || string.IsNullOrWhiteSpace(item.FileReferenceId)
            || string.IsNullOrWhiteSpace(item.Path))
        {
            return;
        }

        if (!_state.KnownPathByFileIdByVolume.TryGetValue(item.Volume, out var knownPaths))
        {
            knownPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _state.KnownPathByFileIdByVolume[item.Volume] = knownPaths;
        }

        if (item.Action.Equals("deleted", StringComparison.OrdinalIgnoreCase))
        {
            knownPaths.Remove(item.FileReferenceId);
            return;
        }

        if (item.Action is "renamed" or "moved"
            && IsFolderObject(item.ObjectType)
            && !string.IsNullOrWhiteSpace(item.PreviousPath))
        {
            FileServerMonitor.Core.KnownPathMap.RelocateDescendants(knownPaths, item.PreviousPath, item.Path);
        }

        knownPaths[item.FileReferenceId] = item.Path;
    }

    private static bool IsFolderObject(string objectType)
    {
        return objectType.Equals("folder", StringComparison.OrdinalIgnoreCase)
            || objectType.Equals("directory", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<CollectedFileEvent> DeduplicateCollectedEvents(IEnumerable<CollectedFileEvent> events)
    {
        return events
            .GroupBy(item =>
            {
                if (!item.CursorType.Equals("security", StringComparison.OrdinalIgnoreCase))
                {
                    return $"keep:{item.CursorType}:{item.RecordId}:{item.Usn}";
                }

                var rounded = new DateTimeOffset(
                    item.TimestampUtc.Year,
                    item.TimestampUtc.Month,
                    item.TimestampUtc.Day,
                    item.TimestampUtc.Hour,
                    item.TimestampUtc.Minute,
                    item.TimestampUtc.Second,
                    item.TimestampUtc.Offset);

                if (item.Action.Equals("deleted", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Join("|",
                        "deleted",
                        item.Server,
                        item.User,
                        item.Path,
                        item.ObjectType,
                        rounded.ToString("o"));
                }

                if (item.Action.Equals("created_or_appended", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Join("|",
                        "created_or_appended",
                        item.Server,
                        item.User,
                        item.Path,
                        item.ObjectType,
                        rounded.ToString("o"));
                }

                return string.Join("|",
                    "keep",
                    item.CursorType,
                    item.RecordId,
                    item.Usn);
            }, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(GetCollectedEventPriority)
                .ThenByDescending(item => item.RecordId ?? 0)
                .ThenByDescending(item => item.TimestampUtc)
                .First());
    }

    private static int GetCollectedEventPriority(CollectedFileEvent item)
    {
        var sourcePriority = item.Source switch
        {
            "usn-journal+security-log" => 30,
            "windows-security-log" => 20,
            "usn-journal" => 10,
            _ => 0
        };

        var actionPriority = item.Action switch
        {
            "created" => 6,
            "renamed" => 6,
            "deleted" => 6,
            "permission_changed" => 5,
            "modified" => 4,
            "created_or_appended" => 3,
            "changed" => 2,
            "accessed" => 1,
            _ => 0
        };

        return sourcePriority + actionPriority;
    }
}

internal static class EnumerableExtensions
{
    public static TResult Pipe<TSource, TResult>(this TSource source, Func<TSource, TResult> transform)
    {
        return transform(source);
    }
}

internal sealed record AgentOptions(
    string AgentId,
    string Server,
    string ApiBaseUrl,
    int ApiRequestTimeoutSeconds,
    int ApiBatchSize,
    string? ApiKey,
    int PollIntervalSeconds,
    int BatchSize,
    int QueueFlushBatchesPerCycle,
    int QueueFlushMaxEventsPerCycle,
    bool EnableSecurityLogCollector,
    bool EnableUsnJournalCollector,
    bool EnableCorrelation,
    bool EnableRemoteConfig,
    bool FilterToConfiguredPaths,
    int CorrelationWindowSeconds,
    int RemoteConfigRefreshMinutes,
    bool SendSecurityLogEvents,
    string[] UsnVolumes,
    string StateFile,
    string QueueFile,
    string PowerShellPath,
    string SecurityLogScriptPath,
    string UsnJournalScriptPath,
    int SecurityLogTimeoutSeconds,
    int UsnJournalTimeoutSeconds,
    string DefaultShare,
    int[] EventIds,
    InventoryScanOptions? InventoryScan)
{
    public static AgentOptions Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Arquivo de configuracao do agente nao encontrado.", path);
        }

        var fullConfigPath = Path.GetFullPath(path);
        var baseDirectory = Path.GetDirectoryName(fullConfigPath) ?? AppContext.BaseDirectory;
        var json = File.ReadAllText(path);
        var options = JsonSerializer.Deserialize<AgentOptions>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Configuracao do agente invalida.");

        return options with
        {
            PollIntervalSeconds = Math.Max(options.PollIntervalSeconds, 5),
            ApiRequestTimeoutSeconds = options.ApiRequestTimeoutSeconds is >= 30 and <= 600
                ? options.ApiRequestTimeoutSeconds
                : 120,
            ApiBatchSize = options.ApiBatchSize is >= 10 and <= 1_000
                ? options.ApiBatchSize
                : 1_000,
            BatchSize = options.BatchSize is > 0 and <= 1000 ? options.BatchSize : 200,
            QueueFlushBatchesPerCycle = options.QueueFlushBatchesPerCycle is > 0 and <= 100 ? options.QueueFlushBatchesPerCycle : 10,
            QueueFlushMaxEventsPerCycle = options.QueueFlushMaxEventsPerCycle is >= 100 and <= 100_000
                ? options.QueueFlushMaxEventsPerCycle
                : 10_000,
            CorrelationWindowSeconds = options.CorrelationWindowSeconds is > 0 and <= 300 ? options.CorrelationWindowSeconds : 10,
            RemoteConfigRefreshMinutes = options.RemoteConfigRefreshMinutes is > 0 and <= 1440 ? options.RemoteConfigRefreshMinutes : 5,
            UsnVolumes = options.UsnVolumes is null || options.UsnVolumes.Length == 0 ? new[] { "D:" } : options.UsnVolumes,
            StateFile = ResolvePath(baseDirectory, options.StateFile),
            QueueFile = ResolvePath(baseDirectory, options.QueueFile),
            SecurityLogScriptPath = ResolvePath(baseDirectory, options.SecurityLogScriptPath),
            UsnJournalScriptPath = ResolvePath(baseDirectory, options.UsnJournalScriptPath),
            SecurityLogTimeoutSeconds = options.SecurityLogTimeoutSeconds is >= 10 and <= 600 ? options.SecurityLogTimeoutSeconds : 60,
            UsnJournalTimeoutSeconds = options.UsnJournalTimeoutSeconds is >= 10 and <= 600 ? options.UsnJournalTimeoutSeconds : 120,
            InventoryScan = NormalizeInventoryScan(options.InventoryScan)
        };
    }

    private static InventoryScanOptions NormalizeInventoryScan(InventoryScanOptions? options)
    {
        if (options is null)
        {
            return new InventoryScanOptions(
                Enabled: false,
                IntervalHours: 24,
                BatchSize: 500,
                MaxItemsPerScan: 0,
                IncludeLastAccessTime: false,
                WindowStartLocal: "01:00",
                WindowEndLocal: "05:00",
                RunRequestedUtc: null,
                Roots: Array.Empty<InventoryScanRoot>());
        }

        return options with
        {
            IntervalHours = options.IntervalHours <= 0 ? 24 : options.IntervalHours,
            BatchSize = options.BatchSize is >= 100 and <= 2000 ? options.BatchSize : 500,
            Roots = options.Roots ?? Array.Empty<InventoryScanRoot>()
        };
    }

    private static string ResolvePath(string baseDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(baseDirectory, path));
    }
}

internal sealed record InventoryScanOptions(
    bool Enabled,
    int IntervalHours,
    int BatchSize,
    int MaxItemsPerScan,
    bool IncludeLastAccessTime,
    string? WindowStartLocal,
    string? WindowEndLocal,
    DateTimeOffset? RunRequestedUtc,
    InventoryScanRoot[] Roots);

internal sealed record InventoryScanRoot(
    string Path,
    string? Server,
    string? Share);

internal sealed record AgentConfigResponse(
    string Server,
    DateTimeOffset GeneratedUtc,
    string DefaultShare,
    string[] UsnVolumes,
    MonitoredPath[] MonitoredPaths,
    InventoryScanSettingsResponse? InventoryScan);

internal sealed record InventoryScanSettingsResponse(
    bool Enabled,
    int IntervalHours,
    int BatchSize,
    int MaxItemsPerScan,
    bool IncludeLastAccessTime,
    string? WindowStartLocal,
    string? WindowEndLocal,
    string RootPath,
    string Server,
    string Share,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? RunRequestedUtc);

internal sealed record MonitoredPath(
    Guid Id,
    string Server,
    string Share,
    string Path,
    string Status,
    string Priority,
    string? Owner,
    string? Notes,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

internal sealed record CollectionResult(
    IReadOnlyCollection<CollectedFileEvent> Events,
    IReadOnlyCollection<CollectedFileEvent> CursorAdvances,
    int RawEvents,
    int SecurityEventsRead,
    int UsnEventsRead,
    int CorrelatedEvents);

internal sealed record AgentState
{
    public long LastRecordId { get; set; }

    public Dictionary<string, long> LastUsnByVolume { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Dictionary<string, string>> KnownPathByFileIdByVolume { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset? LastSuccessfulSendUtc { get; set; }

    public DateTimeOffset? LastCollectedEventUtc { get; set; }

    public static AgentState Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AgentState();
        }

        var json = File.ReadAllText(path);
        var state = JsonSerializer.Deserialize<AgentState>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? new AgentState();

        state.LastUsnByVolume = new Dictionary<string, long>(
            state.LastUsnByVolume ?? new Dictionary<string, long>(),
            StringComparer.OrdinalIgnoreCase);

        state.KnownPathByFileIdByVolume = new Dictionary<string, Dictionary<string, string>>(
            (state.KnownPathByFileIdByVolume ?? new Dictionary<string, Dictionary<string, string>>())
                .ToDictionary(
                    item => item.Key,
                    item => new Dictionary<string, string>(item.Value ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        return state;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        });
        File.WriteAllText(path, json);
    }
}

internal sealed record CollectedFileEvent(
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
    string? FileReferenceId,
    string Result,
    string Severity,
    string Source)
{
    public string CursorKey => CursorType.Equals("usn", StringComparison.OrdinalIgnoreCase)
        ? $"usn:{Volume}:{Usn}"
        : $"security:{RecordId}";

    public FileAuditEventRequest ToApiRequest(string agentId)
    {
        return new FileAuditEventRequest(
            TimestampUtc,
            Server,
            Share,
            Path,
            PreviousPath,
            ObjectType,
            Action,
            User,
            Sid,
            SourceHost,
            SourceIp,
            ProcessName,
            FileSizeBytes,
            Extension,
            Result,
            Severity,
            Source,
            agentId,
            CursorType,
            RecordId,
            Usn,
            Volume,
            FileReferenceId);
    }
}

internal sealed record FileAuditEventRequest(
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
    string AgentId,
    string CursorType,
    long? RecordId,
    long? Usn,
    string? Volume,
    string? FileReferenceId);

internal sealed record InventorySnapshotStartRequest(
    string Server,
    string Share,
    string RootPath);

internal sealed record InventorySnapshotStartResponse(
    Guid Id,
    string Server,
    string Share,
    string RootPath,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    string Status,
    long FileCount,
    long FolderCount,
    long TotalBytes,
    long ErrorCount,
    string? Error);

internal sealed record InventoryItemBatchRequest(InventoryItemRequest[] Items);

internal sealed record InventoryItemRequest(
    DateTimeOffset? ScannedAtUtc,
    string Server,
    string Share,
    string RootPath,
    string Path,
    string? RelativePath,
    string? Name,
    string ItemType,
    long? SizeBytes,
    DateTimeOffset? CreatedUtc,
    DateTimeOffset? ModifiedUtc,
    DateTimeOffset? AccessedUtc,
    string? Error);

internal sealed record InventorySnapshotCompleteRequest(string Status, string? Error);

internal sealed record AgentHeartbeatRequest(
    string AgentId,
    string Server,
    string Status,
    string Version,
    long LastRecordId,
    IReadOnlyDictionary<string, long> LastUsnByVolume,
    string? Message,
    int PendingQueueEvents,
    DateTimeOffset? LastSuccessfulSendUtc,
    DateTimeOffset? LastCollectedEventUtc,
    FileServerMonitor.Core.AgentCycleMetrics? LastCycle);

internal sealed class WindowsServiceRuntime
{
    private const int SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    private const int SERVICE_START_PENDING = 0x00000002;
    private const int SERVICE_STOP_PENDING = 0x00000003;
    private const int SERVICE_RUNNING = 0x00000004;
    private const int SERVICE_STOPPED = 0x00000001;
    private const int SERVICE_ACCEPT_STOP = 0x00000001;
    private const int SERVICE_CONTROL_STOP = 0x00000001;
    private const int SERVICE_CONTROL_SHUTDOWN = 0x00000005;
    private const int NO_ERROR = 0;

    private readonly string _serviceName;
    private readonly Func<CancellationToken, Task> _runAsync;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private IntPtr _statusHandle;
    private readonly ManualResetEventSlim _stopped = new(false);
    private ServiceStatusHandleExDelegate? _controlHandler;
    private ServiceMainDelegate? _serviceMain;

    private WindowsServiceRuntime(string serviceName, Func<CancellationToken, Task> runAsync)
    {
        _serviceName = serviceName;
        _runAsync = runAsync;
    }

    public static void Run(string serviceName, Func<CancellationToken, Task> runAsync)
    {
        var runtime = new WindowsServiceRuntime(serviceName, runAsync);
        runtime.RunInternal();
    }

    private void RunInternal()
    {
        _serviceMain = ServiceMain;
        _controlHandler = ServiceControlHandler;

        var table = new[]
        {
            new ServiceTableEntry
            {
                ServiceName = _serviceName,
                ServiceProc = _serviceMain
            },
            new ServiceTableEntry()
        };

        if (!StartServiceCtrlDispatcher(table))
        {
            throw new InvalidOperationException($"Nao foi possivel iniciar o dispatcher do servico Windows. Codigo={Marshal.GetLastWin32Error()}");
        }
    }

    private void ServiceMain(int argc, IntPtr argv)
    {
        _statusHandle = RegisterServiceCtrlHandlerEx(_serviceName, _controlHandler!, IntPtr.Zero);

        if (_statusHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Nao foi possivel registrar o handler do servico. Codigo={Marshal.GetLastWin32Error()}");
        }

        UpdateStatus(SERVICE_START_PENDING);

        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => _runAsync(_cts.Token));
        _runTask.ContinueWith(task =>
        {
            var exitCode = task.Exception is null ? NO_ERROR : 1;
            UpdateStatus(SERVICE_STOPPED, win32ExitCode: exitCode);
            _stopped.Set();
        }, TaskScheduler.Default);

        UpdateStatus(SERVICE_RUNNING, controlsAccepted: SERVICE_ACCEPT_STOP);
        _stopped.Wait();
    }

    private int ServiceControlHandler(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        if (control is SERVICE_CONTROL_STOP or SERVICE_CONTROL_SHUTDOWN)
        {
            UpdateStatus(SERVICE_STOP_PENDING);
            _cts?.Cancel();
        }

        return NO_ERROR;
    }

    private void UpdateStatus(int currentState, int controlsAccepted = 0, int win32ExitCode = NO_ERROR, int waitHint = 3000)
    {
        if (_statusHandle == IntPtr.Zero)
        {
            return;
        }

        var status = new ServiceStatus
        {
            ServiceType = SERVICE_WIN32_OWN_PROCESS,
            CurrentState = currentState,
            ControlsAccepted = controlsAccepted,
            Win32ExitCode = win32ExitCode,
            ServiceSpecificExitCode = 0,
            CheckPoint = 0,
            WaitHint = waitHint
        };

        SetServiceStatus(_statusHandle, ref status);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ServiceName;
        public ServiceMainDelegate? ServiceProc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainDelegate(int argc, IntPtr argv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ServiceStatusHandleExDelegate(int control, int eventType, IntPtr eventData, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] serviceTable);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(
        string serviceName,
        ServiceStatusHandleExDelegate callback,
        IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr serviceStatusHandle, ref ServiceStatus status);
}
