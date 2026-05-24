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

    public FileServerAgent(AgentOptions options, AgentState state)
    {
        _options = options;
        _state = state;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(options.ApiBaseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
        }
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
                try
                {
                    await RefreshRemoteConfigAsync(cts.Token);
                    await SendHeartbeatAsync("running", null, cts.Token);
                    await FlushQueueAsync(cts.Token);
                    await CollectAndSendAsync(cts.Token);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Falha no ciclo de coleta: {ex.Message}");
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
        var collected = (await CollectEventsAsync(cancellationToken))
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .Pipe(DeduplicateCollectedEvents)
            .ToArray();

        if (collected.Length == 0)
        {
            return;
        }

        var eventsToSend = _options.SendSecurityLogEvents
            ? collected
            : collected
                .Where(item => !item.CursorType.Equals("security", StringComparison.OrdinalIgnoreCase))
                .ToArray();

        if (eventsToSend.Length == 0)
        {
            AdvanceState(collected);
            _state.LastSuccessfulSendUtc = DateTimeOffset.UtcNow;
            _state.Save(_options.StateFile);
            return;
        }

        var events = eventsToSend.Select(item => item.ToApiRequest()).ToArray();
        var sent = await TrySendBatchAsync(events, cancellationToken);

        if (!sent)
        {
            await AppendQueueAsync(eventsToSend, cancellationToken);
            return;
        }

        AdvanceState(collected);
        _state.LastSuccessfulSendUtc = DateTimeOffset.UtcNow;
        _state.Save(_options.StateFile);
    }

    private async Task<IReadOnlyCollection<CollectedFileEvent>> CollectEventsAsync(CancellationToken cancellationToken)
    {
        var collected = new List<CollectedFileEvent>();

        if (_options.EnableSecurityLogCollector)
        {
            var scriptPath = Path.GetFullPath(_options.SecurityLogScriptPath);
            var arguments = BuildSecurityLogArguments(scriptPath);
            var securityEvents = await RunCollectorScriptAsync(arguments, cancellationToken);
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
                var usnEvents = await RunCollectorScriptAsync(arguments, cancellationToken);
                Console.WriteLine($"Coleta USN: volume={volume}; startUsn={startUsn}; basePath={basePath}; recebidos={usnEvents.Count}");
                collected.AddRange(usnEvents);
            }
        }

        var output = _options.EnableCorrelation
            ? CorrelateEvents(collected)
            : collected;

        var filtered = FilterByRemoteConfig(output)
            .OrderByDescending(item => item.TimestampUtc)
            .Take(_options.BatchSize)
            .OrderBy(item => item.TimestampUtc)
            .ToArray();

        Console.WriteLine($"Coleta final: brutos={collected.Count}; pos-correlacao={output.Count}; pos-filtro={filtered.Length}");
        return filtered;
    }

    private async Task<IReadOnlyCollection<CollectedFileEvent>> RunCollectorScriptAsync(
        string arguments,
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

        await process.WaitForExitAsync(cancellationToken);

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

        return string.Join(" ", parts);
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

        var lines = await File.ReadAllLinesAsync(_options.QueueFile, cancellationToken);
        var queued = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<CollectedFileEvent>(line, JsonOptions))
            .Where(item => item is not null)
            .Cast<CollectedFileEvent>()
            .OrderBy(item => item.TimestampUtc)
            .Take(_options.BatchSize)
            .ToArray();

        if (queued.Length == 0)
        {
            File.Delete(_options.QueueFile);
            return;
        }

        var sent = await TrySendBatchAsync(queued.Select(item => item.ToApiRequest()).ToArray(), cancellationToken);

        if (!sent)
        {
            return;
        }

        var sentKeys = queued.Select(item => item.CursorKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = lines
            .Where(line =>
            {
                var item = JsonSerializer.Deserialize<CollectedFileEvent>(line, JsonOptions);
                return item is not null && !sentKeys.Contains(item.CursorKey);
            })
            .ToArray();

        if (remaining.Length == 0)
        {
            File.Delete(_options.QueueFile);
        }
        else
        {
            await File.WriteAllLinesAsync(_options.QueueFile, remaining, cancellationToken);
        }

        AdvanceState(queued);
        _state.LastSuccessfulSendUtc = DateTimeOffset.UtcNow;
        _state.Save(_options.StateFile);
    }

    private async Task<bool> TrySendBatchAsync(FileAuditEventRequest[] events, CancellationToken cancellationToken)
    {
        if (events.Length == 0)
        {
            return true;
        }

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/api/events/batch", events, JsonOptions, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.Error.WriteLine($"API rejeitou lote: {(int)response.StatusCode} {body}");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"API indisponivel: {ex.Message}");
            return false;
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
            LastSuccessfulSendUtc: _state.LastSuccessfulSendUtc);

        try
        {
            await _httpClient.PostAsJsonAsync("/api/agents/heartbeat", heartbeat, JsonOptions, cancellationToken);
        }
        catch
        {
            // Heartbeat nao pode interromper a coleta.
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
            }
        }
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
    string? ApiKey,
    int PollIntervalSeconds,
    int BatchSize,
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
    string DefaultShare,
    int[] EventIds)
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
            BatchSize = options.BatchSize is > 0 and <= 1000 ? options.BatchSize : 200,
            CorrelationWindowSeconds = options.CorrelationWindowSeconds is > 0 and <= 300 ? options.CorrelationWindowSeconds : 10,
            RemoteConfigRefreshMinutes = options.RemoteConfigRefreshMinutes is > 0 and <= 1440 ? options.RemoteConfigRefreshMinutes : 5,
            UsnVolumes = options.UsnVolumes is null || options.UsnVolumes.Length == 0 ? new[] { "D:" } : options.UsnVolumes,
            StateFile = ResolvePath(baseDirectory, options.StateFile),
            QueueFile = ResolvePath(baseDirectory, options.QueueFile),
            SecurityLogScriptPath = ResolvePath(baseDirectory, options.SecurityLogScriptPath),
            UsnJournalScriptPath = ResolvePath(baseDirectory, options.UsnJournalScriptPath)
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

internal sealed record AgentConfigResponse(
    string Server,
    DateTimeOffset GeneratedUtc,
    string DefaultShare,
    string[] UsnVolumes,
    MonitoredPath[] MonitoredPaths);

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

internal sealed record AgentState
{
    public long LastRecordId { get; set; }

    public Dictionary<string, long> LastUsnByVolume { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset? LastSuccessfulSendUtc { get; set; }

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

    public FileAuditEventRequest ToApiRequest()
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
            Source);
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
    string Source);

internal sealed record AgentHeartbeatRequest(
    string AgentId,
    string Server,
    string Status,
    string Version,
    long LastRecordId,
    IReadOnlyDictionary<string, long> LastUsnByVolume,
    string? Message,
    int PendingQueueEvents,
    DateTimeOffset? LastSuccessfulSendUtc);

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
