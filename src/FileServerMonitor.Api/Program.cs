using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
#if SQLSERVER
using Microsoft.Data.SqlClient;
#endif

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MonitorOptions>(
    builder.Configuration.GetSection(MonitorOptions.SectionName));
builder.Services.AddSingleton<AgentHealthStore>();
builder.Services.AddSingleton<AlertNotificationService>();
builder.Services.AddSingleton<AlertRuleStore>();
builder.Services.AddSingleton<AlertStore>();
builder.Services.AddSingleton<MonitoredPathStore>();
builder.Services.AddSingleton<AdminAuditStore>();
builder.Services.AddHostedService<RetentionWorker>();
builder.Services.AddCors(options =>
{
    var allowedOrigins = builder.Configuration
        .GetSection("Cors:AllowedOrigins")
        .Get<string[]>()
        ?.Where(item => !string.IsNullOrWhiteSpace(item))
        .Select(item => item.Trim().TrimEnd('/'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray()
        ?? new[]
        {
            "http://localhost:3000",
            "http://localhost:5173",
            "http://localhost:4173",
            "http://127.0.0.1:3000",
            "http://127.0.0.1:5173",
            "http://127.0.0.1:4173"
        };

    options.AddPolicy("WebApp", policy =>
    {
        policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var storageProvider = builder.Configuration.GetValue("Monitor:StorageProvider", "SqlServer");

if (storageProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
{
#if SQLSERVER
    builder.Services.AddSingleton<IEventRepository, SqlServerEventRepository>();
#else
    Console.Error.WriteLine("SQL Server desativado neste build. Usando armazenamento em memoria.");
    builder.Services.AddSingleton<IEventRepository, InMemoryEventRepository>();
#endif
}
else
{
    builder.Services.AddSingleton<IEventRepository, InMemoryEventRepository>();
}

var app = builder.Build();

app.UseCors("WebApp");
app.Use(async (context, next) =>
{
    var authOptions = AuthOptions.FromConfiguration(context.RequestServices.GetRequiredService<IConfiguration>());

    if (!authOptions.Enabled || AuthHelpers.IsAnonymousPath(context.Request.Path))
    {
        await next(context);
        return;
    }

    var providedKey = AuthHelpers.GetProvidedApiKey(context.Request);
    var requiresAdmin = AuthHelpers.RequiresAdminKey(context.Request);

    if (!authOptions.MatchesAnyKey(providedKey))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ErrorResponse("Nao autorizado."));
        return;
    }

    if (requiresAdmin && !authOptions.MatchesAdminKey(providedKey))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new ErrorResponse("Chave administrativa obrigatoria."));
        return;
    }

    await next(context);
});

app.MapGet("/", () => Results.Redirect("/health"));

app.MapGet("/health", async (IEventRepository repository, CancellationToken cancellationToken) =>
{
    var stats = await repository.GetStatsAsync(cancellationToken);

    return Results.Ok(new HealthResponse(
        Service: "FileServerMonitor.Api",
        Status: "healthy",
        TimestampUtc: DateTimeOffset.UtcNow,
        StorageProvider: repository.ProviderName,
        StoredEvents: stats.StoredEvents,
        LastEventUtc: stats.LastEventUtc));
});

app.MapPost("/api/events", async (
    FileAuditEventRequest request,
    IEventRepository repository,
    AlertStore alerts,
    CancellationToken cancellationToken) =>
{
    var auditEvent = request.ToAuditEvent();
    await repository.AddAsync(auditEvent, cancellationToken);
    var generatedAlerts = await alerts.AnalyzeAsync(new[] { auditEvent }, cancellationToken);

    return Results.Created($"/api/events/{auditEvent.Id}", new EventIngestResponse(auditEvent, generatedAlerts));
});

app.MapPost("/api/events/batch", async (
    FileAuditEventRequest[] requests,
    IEventRepository repository,
    AlertStore alerts,
    CancellationToken cancellationToken) =>
{
    if (requests.Length == 0)
    {
        return Results.BadRequest(new ErrorResponse("A lista de eventos nao pode estar vazia."));
    }

    if (requests.Length > 1_000)
    {
        return Results.BadRequest(new ErrorResponse("Envie no maximo 1000 eventos por lote."));
    }

    var events = requests.Select(request => request.ToAuditEvent()).ToArray();
    await repository.AddBatchAsync(events, cancellationToken);
    var generatedAlerts = await alerts.AnalyzeAsync(events, cancellationToken);

    return Results.Accepted(value: new BatchIngestResponse(
        events.Length,
        events.Select(item => item.Id).ToArray(),
        generatedAlerts));
});

app.MapGet("/api/events", async (
    string? server,
    string? user,
    string? action,
    string? path,
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc,
    int? take,
    IEventRepository repository,
    CancellationToken cancellationToken) =>
{
    var query = new EventQuery(
        Server: server,
        User: user,
        Action: action,
        Path: path,
        FromUtc: fromUtc,
        ToUtc: toUtc,
        Take: take is > 0 and <= 500 ? take.Value : 100);

    var events = await repository.QueryAsync(query, cancellationToken);

    return Results.Ok(events);
});

app.MapGet("/api/events/export.csv", async (
    string? server,
    string? user,
    string? action,
    string? path,
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc,
    int? take,
    IEventRepository repository,
    CancellationToken cancellationToken) =>
{
    var query = new EventQuery(
        Server: server,
        User: user,
        Action: action,
        Path: path,
        FromUtc: fromUtc,
        ToUtc: toUtc,
        Take: take is > 0 and <= 10_000 ? take.Value : 1_000);

    var events = await repository.QueryAsync(query, cancellationToken);
    var csv = EventCsvExporter.Export(events);

    return Results.Text(csv, "text/csv; charset=utf-8");
});

app.MapGet("/api/events/{id:guid}", async (
    Guid id,
    IEventRepository repository,
    CancellationToken cancellationToken) =>
{
    var auditEvent = await repository.FindAsync(id, cancellationToken);

    return auditEvent is null
        ? Results.NotFound(new ErrorResponse("Evento nao encontrado."))
        : Results.Ok(auditEvent);
});

app.MapGet("/api/alerts", async (
    string? severity,
    string? status,
    int? take,
    AlertStore alerts,
    CancellationToken cancellationToken) =>
{
    var query = new AlertQuery(
        Severity: severity,
        Status: status,
        Take: take is > 0 and <= 500 ? take.Value : 100);

    var result = await alerts.QueryAsync(query, cancellationToken);

    return Results.Ok(result);
});

app.MapGet("/api/alerts/export.csv", async (
    string? severity,
    string? status,
    int? take,
    AlertStore alerts,
    CancellationToken cancellationToken) =>
{
    var query = new AlertQuery(
        Severity: severity,
        Status: status,
        Take: take is > 0 and <= 10_000 ? take.Value : 1_000);
    var result = await alerts.QueryAsync(query, cancellationToken);
    var csv = AlertCsvExporter.Export(result);

    return Results.Text(csv, "text/csv; charset=utf-8");
});

app.MapPost("/api/alerts/{id:guid}/ack", async (
    Guid id,
    AlertStore alerts,
    AdminAuditStore adminAudit,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var alert = await alerts.AcknowledgeAsync(id, cancellationToken);

    if (alert is not null)
    {
        await adminAudit.AddAsync(AdminAuditEntry.Create(
            Action: "alert.acknowledge",
            EntityType: "alert",
            EntityId: id.ToString(),
            Actor: AdminAuditHelpers.GetActor(httpContext),
            SourceIp: AdminAuditHelpers.GetSourceIp(httpContext),
            Details: new
            {
                alert.Rule,
                alert.Severity,
                alert.Server,
                alert.User
            }), cancellationToken);
    }

    return alert is null
        ? Results.NotFound(new ErrorResponse("Alerta nao encontrado."))
        : Results.Ok(alert);
});

app.MapGet("/api/alert-rules", async (
    AlertRuleStore rules,
    CancellationToken cancellationToken) =>
{
    var result = await rules.ListAsync(cancellationToken);
    return Results.Ok(result);
});

app.MapPut("/api/alert-rules/{ruleName}", async (
    string ruleName,
    AlertRuleUpdateRequest request,
    AlertRuleStore rules,
    AdminAuditStore adminAudit,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var updated = await rules.UpdateAsync(ruleName, request, cancellationToken);

    if (updated is null)
    {
        return Results.NotFound(new ErrorResponse("Regra de alerta nao encontrada."));
    }

        await adminAudit.AddAsync(AdminAuditEntry.Create(
            Action: "alert_rule.update",
            EntityType: "alert-rule",
        EntityId: updated.Rule,
        Actor: AdminAuditHelpers.GetActor(httpContext),
        SourceIp: AdminAuditHelpers.GetSourceIp(httpContext),
        Details: new
            {
                updated.Rule,
                updated.Enabled,
                updated.Severity,
                updated.Threshold,
                updated.SecondaryThreshold,
                updated.SecondarySeverity,
                updated.ServerFilter,
                updated.ShareFilter,
                updated.PathFilter,
                updated.ActiveFromHour,
                updated.ActiveToHour,
                updated.ActiveDays,
                updated.ExcludedUsers,
                updated.ExcludedHosts,
                updated.ExcludedProcesses,
                updated.TimeZoneId
            }), cancellationToken);

    return Results.Ok(updated);
});

app.MapPost("/api/alert-rules/{ruleName}/simulate", async (
    string ruleName,
    AlertRuleSimulationRequest request,
    AlertStore alerts,
    IEventRepository repository,
    CancellationToken cancellationToken) =>
{
    var fromUtc = request.FromUtc ?? DateTimeOffset.UtcNow.AddHours(-24);
    var toUtc = request.ToUtc ?? DateTimeOffset.UtcNow;
    var take = request.Take is > 0 and <= 5_000 ? request.Take.Value : 5_000;
    var query = new EventQuery(
        Server: request.Server,
        User: request.User,
        Action: request.Action,
        Path: request.Path,
        FromUtc: fromUtc,
        ToUtc: toUtc,
        Take: take);
    var events = await repository.QueryAsync(query, cancellationToken);
    var result = await alerts.SimulateAsync(ruleName, fromUtc, toUtc, events, cancellationToken);

    return result is null
        ? Results.NotFound(new ErrorResponse("Regra de alerta nao encontrada."))
        : Results.Ok(result);
});

app.MapPost("/api/agents/heartbeat", async (
    AgentHeartbeatRequest request,
    AgentHealthStore store,
    CancellationToken cancellationToken) =>
{
    var heartbeat = new AgentHealthResponse(
        AgentId: request.AgentId,
        Server: request.Server,
        Status: request.Status,
        LastHeartbeatUtc: DateTimeOffset.UtcNow,
        Version: request.Version,
        LastRecordId: request.LastRecordId,
        LastUsnByVolume: request.LastUsnByVolume ?? new Dictionary<string, long>(),
        Message: request.Message,
        PendingQueueEvents: request.PendingQueueEvents,
        LastSuccessfulSendUtc: request.LastSuccessfulSendUtc);

    await store.UpsertAsync(heartbeat, cancellationToken);

    return Results.Accepted(value: heartbeat);
});

app.MapGet("/api/agents/health", async (
    AgentHealthStore store,
    CancellationToken cancellationToken) =>
{
    var agents = await store.ListAsync(cancellationToken);

    return Results.Ok(agents);
});

app.MapGet("/api/agents/config", async (
    string server,
    MonitoredPathStore paths,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(server))
    {
        return Results.BadRequest(new ErrorResponse("Servidor e obrigatorio."));
    }

    var activePaths = await paths.ListAsync(server, "active", cancellationToken);
    var usnVolumes = activePaths
        .Select(item => TryGetWindowsVolume(item.Path))
        .OfType<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(item => item)
        .ToArray();
    var defaultShare = activePaths
        .Select(item => item.Share)
        .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
        ?? "FileServer";

    return Results.Ok(new AgentConfigResponse(
        Server: server.Trim(),
        GeneratedUtc: DateTimeOffset.UtcNow,
        DefaultShare: defaultShare,
        UsnVolumes: usnVolumes,
        MonitoredPaths: activePaths));
});

app.MapGet("/api/reports/activity-summary", async (
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc,
    string? server,
    string? share,
    string? user,
    string? action,
    int? take,
    IEventRepository repository,
    CancellationToken cancellationToken) =>
{
    var now = DateTimeOffset.UtcNow;
    var query = new ActivitySummaryQuery(
        FromUtc: fromUtc ?? now.AddHours(-24),
        ToUtc: toUtc ?? now,
        Server: server,
        Share: share,
        User: user,
        Action: action,
        Take: take is > 0 and <= 50 ? take.Value : 10);
    var summary = await repository.GetActivitySummaryAsync(query, cancellationToken);

    return Results.Ok(summary);
});

app.MapGet("/api/reports/baseline-anomalies", async (
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc,
    string? server,
    string? share,
    string? user,
    string? action,
    int? take,
    IEventRepository repository,
    CancellationToken cancellationToken) =>
{
    var now = DateTimeOffset.UtcNow;
    var query = new BaselineAnomalyQuery(
        FromUtc: fromUtc ?? now.AddHours(-24),
        ToUtc: toUtc ?? now,
        Server: server,
        Share: share,
        User: user,
        Action: action,
        BaselineWindows: 7,
        Take: take is > 0 and <= 20 ? take.Value : 8);
    var result = await repository.GetBaselineAnomaliesAsync(query, cancellationToken);

    return Results.Ok(result);
});

app.MapGet("/api/reports/baseline-anomalies/export.csv", async (
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc,
    string? server,
    string? share,
    string? user,
    string? action,
    int? take,
    IEventRepository repository,
    CancellationToken cancellationToken) =>
{
    var now = DateTimeOffset.UtcNow;
    var query = new BaselineAnomalyQuery(
        FromUtc: fromUtc ?? now.AddHours(-24),
        ToUtc: toUtc ?? now,
        Server: server,
        Share: share,
        User: user,
        Action: action,
        BaselineWindows: 7,
        Take: take is > 0 and <= 50 ? take.Value : 20);
    var result = await repository.GetBaselineAnomaliesAsync(query, cancellationToken);
    var csv = BaselineAnomalyCsvExporter.Export(result);

    return Results.Text(csv, "text/csv; charset=utf-8");
});

app.MapGet("/api/monitored-paths", async (
    string? server,
    string? status,
    MonitoredPathStore store,
    CancellationToken cancellationToken) =>
{
    var paths = await store.ListAsync(server, status, cancellationToken);

    return Results.Ok(paths);
});

app.MapGet("/api/admin-audit", async (
    string? action,
    string? entityType,
    int? take,
    AdminAuditStore store,
    CancellationToken cancellationToken) =>
{
    var entries = await store.ListAsync(new AdminAuditQuery(
        Action: action,
        EntityType: entityType,
        Take: take is > 0 and <= 500 ? take.Value : 100), cancellationToken);

    return Results.Ok(entries);
});

app.MapPost("/api/monitored-paths", async (
    MonitoredPathRequest request,
    MonitoredPathStore store,
    AdminAuditStore adminAudit,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var path = await store.CreateAsync(request, cancellationToken);

    await adminAudit.AddAsync(AdminAuditEntry.Create(
        Action: "monitored_path.create",
        EntityType: "monitored_path",
        EntityId: path.Id.ToString(),
        Actor: AdminAuditHelpers.GetActor(httpContext),
        SourceIp: AdminAuditHelpers.GetSourceIp(httpContext),
        Details: path), cancellationToken);

    return Results.Created($"/api/monitored-paths/{path.Id}", path);
});

app.MapPut("/api/monitored-paths/{id:guid}", async (
    Guid id,
    MonitoredPathRequest request,
    MonitoredPathStore store,
    AdminAuditStore adminAudit,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var path = await store.UpdateAsync(id, request, cancellationToken);

    if (path is not null)
    {
        await adminAudit.AddAsync(AdminAuditEntry.Create(
            Action: "monitored_path.update",
            EntityType: "monitored_path",
            EntityId: id.ToString(),
            Actor: AdminAuditHelpers.GetActor(httpContext),
            SourceIp: AdminAuditHelpers.GetSourceIp(httpContext),
            Details: path), cancellationToken);
    }

    return path is null
        ? Results.NotFound(new ErrorResponse("Caminho monitorado nao encontrado."))
        : Results.Ok(path);
});

app.MapDelete("/api/monitored-paths/{id:guid}", async (
    Guid id,
    MonitoredPathStore store,
    AdminAuditStore adminAudit,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var removed = await store.DeleteAsync(id, cancellationToken);

    if (removed)
    {
        await adminAudit.AddAsync(AdminAuditEntry.Create(
            Action: "monitored_path.delete",
            EntityType: "monitored_path",
            EntityId: id.ToString(),
            Actor: AdminAuditHelpers.GetActor(httpContext),
            SourceIp: AdminAuditHelpers.GetSourceIp(httpContext),
            Details: new { id }), cancellationToken);
    }

    return removed
        ? Results.NoContent()
        : Results.NotFound(new ErrorResponse("Caminho monitorado nao encontrado."));
});

app.Run();

static string? TryGetWindowsVolume(string path)
{
    var normalized = path.Trim();

    if (normalized.Length >= 2 && normalized[1] == ':')
    {
        return normalized[..2].ToUpperInvariant();
    }

    return null;
}

internal interface IEventRepository
{
    string ProviderName { get; }

    Task AddAsync(FileAuditEvent auditEvent, CancellationToken cancellationToken);

    Task AddBatchAsync(IReadOnlyCollection<FileAuditEvent> events, CancellationToken cancellationToken);

    Task<FileAuditEvent?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<FileAuditEvent>> QueryAsync(EventQuery query, CancellationToken cancellationToken);

    Task<EventStoreStats> GetStatsAsync(CancellationToken cancellationToken);

    Task<ActivitySummaryResponse> GetActivitySummaryAsync(ActivitySummaryQuery query, CancellationToken cancellationToken);

    Task<BaselineAnomalyResponse> GetBaselineAnomaliesAsync(BaselineAnomalyQuery query, CancellationToken cancellationToken);

    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, int batchSize, CancellationToken cancellationToken);
}

#if SQLSERVER
internal sealed class SqlServerEventRepository : IEventRepository
{
    private readonly string _connectionString;

    public SqlServerEventRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("SqlServer")
            ?? throw new InvalidOperationException("ConnectionStrings:SqlServer nao foi configurada.");
    }

    public string ProviderName => "SqlServer";

    public async Task AddAsync(FileAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        await AddBatchAsync(new[] { auditEvent }, cancellationToken);
    }

    public async Task AddBatchAsync(IReadOnlyCollection<FileAuditEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var auditEvent in events)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO dbo.FileAuditEvents
                    (
                        Id,
                        TimestampUtc,
                        ServerName,
                        ShareName,
                        FullPath,
                        PreviousPath,
                        ObjectType,
                        ActionName,
                        UserName,
                        Sid,
                        SourceHost,
                        SourceIp,
                        ProcessName,
                        FileSizeBytes,
                        Extension,
                        ResultName,
                        Severity,
                        SourceName
                    )
                    VALUES
                    (
                        @Id,
                        @TimestampUtc,
                        @ServerName,
                        @ShareName,
                        @FullPath,
                        @PreviousPath,
                        @ObjectType,
                        @ActionName,
                        @UserName,
                        @Sid,
                        @SourceHost,
                        @SourceIp,
                        @ProcessName,
                        @FileSizeBytes,
                        @Extension,
                        @ResultName,
                        @Severity,
                        @SourceName
                    );
                    """;

                AddEventParameters(command, auditEvent);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<FileAuditEvent?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1)
                Id,
                TimestampUtc,
                ServerName,
                ShareName,
                FullPath,
                PreviousPath,
                ObjectType,
                ActionName,
                UserName,
                Sid,
                SourceHost,
                SourceIp,
                ProcessName,
                FileSizeBytes,
                Extension,
                ResultName,
                Severity,
                SourceName
            FROM dbo.FileAuditEvents
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? ReadEvent(reader)
            : null;
    }

    public async Task<IReadOnlyCollection<FileAuditEvent>> QueryAsync(EventQuery query, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = BuildQuerySql(query, command);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<FileAuditEvent>();

        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(ReadEvent(reader));
        }

        return events;
    }

    public async Task<EventStoreStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT_BIG(1) AS StoredEvents,
                MAX(TimestampUtc) AS LastEventUtc
            FROM dbo.FileAuditEvents;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return new EventStoreStats(0, null);
        }

        var count = Convert.ToInt64(reader["StoredEvents"]);
        DateTimeOffset? lastEventUtc = reader["LastEventUtc"] == DBNull.Value
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind((DateTime)reader["LastEventUtc"], DateTimeKind.Utc));

        return new EventStoreStats(count, lastEventUtc);
    }

    public async Task<int> PurgeOlderThanAsync(
        DateTimeOffset cutoffUtc,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var totalDeleted = 0;
        var safeBatchSize = Math.Clamp(batchSize, 100, 100_000);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE TOP (@BatchSize)
                FROM dbo.FileAuditEvents
                WHERE TimestampUtc < @CutoffUtc;

                SELECT @@ROWCOUNT;
                """;
            command.Parameters.AddWithValue("@BatchSize", safeBatchSize);
            command.Parameters.AddWithValue("@CutoffUtc", cutoffUtc.UtcDateTime);

            var deleted = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            totalDeleted += deleted;

            if (deleted < safeBatchSize)
            {
                break;
            }
        }

        return totalDeleted;
    }

    public async Task<ActivitySummaryResponse> GetActivitySummaryAsync(
        ActivitySummaryQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var total = await CountEventsAsync(connection, query, cancellationToken);
        var byAction = await QueryDimensionAsync(connection, query, "ActionName", cancellationToken);
        var byShare = await QueryDimensionAsync(connection, query, "ShareName", cancellationToken);
        var byUser = await QueryDimensionAsync(connection, query, "UserName", cancellationToken);

        return new ActivitySummaryResponse(
            FromUtc: query.FromUtc,
            ToUtc: query.ToUtc,
            TotalEvents: total,
            ByAction: byAction,
            ByShare: byShare,
            ByUser: byUser);
    }

    public async Task<BaselineAnomalyResponse> GetBaselineAnomaliesAsync(
        BaselineAnomalyQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var byAction = await BuildAnomaliesAsync(connection, query, "ActionName", cancellationToken);
        var byShare = await BuildAnomaliesAsync(connection, query, "ShareName", cancellationToken);
        var byUser = await BuildAnomaliesAsync(connection, query, "UserName", cancellationToken);

        return new BaselineAnomalyResponse(
            FromUtc: query.FromUtc,
            ToUtc: query.ToUtc,
            BaselineWindows: query.BaselineWindows,
            ByAction: byAction,
            ByShare: byShare,
            ByUser: byUser);
    }

    private static string BuildQuerySql(EventQuery query, SqlCommand command)
    {
        command.Parameters.AddWithValue("@Take", query.Take);

        var predicates = new List<string>();

        if (!string.IsNullOrWhiteSpace(query.Server))
        {
            predicates.Add("ServerName LIKE @Server");
            command.Parameters.AddWithValue("@Server", $"%{query.Server}%");
        }

        if (!string.IsNullOrWhiteSpace(query.User))
        {
            predicates.Add("UserName LIKE @User");
            command.Parameters.AddWithValue("@User", $"%{query.User}%");
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            predicates.Add("ActionName = @Action");
            command.Parameters.AddWithValue("@Action", query.Action);
        }

        if (!string.IsNullOrWhiteSpace(query.Path))
        {
            predicates.Add("FullPath LIKE @Path");
            command.Parameters.AddWithValue("@Path", $"%{query.Path}%");
        }

        if (query.FromUtc is not null)
        {
            predicates.Add("TimestampUtc >= @FromUtc");
            command.Parameters.AddWithValue("@FromUtc", query.FromUtc.Value.UtcDateTime);
        }

        if (query.ToUtc is not null)
        {
            predicates.Add("TimestampUtc <= @ToUtc");
            command.Parameters.AddWithValue("@ToUtc", query.ToUtc.Value.UtcDateTime);
        }

        var where = predicates.Count == 0
            ? string.Empty
            : $"WHERE {string.Join(" AND ", predicates)}";

        return $$"""
            SELECT TOP (@Take)
                Id,
                TimestampUtc,
                ServerName,
                ShareName,
                FullPath,
                PreviousPath,
                ObjectType,
                ActionName,
                UserName,
                Sid,
                SourceHost,
                SourceIp,
                ProcessName,
                FileSizeBytes,
                Extension,
                ResultName,
                Severity,
                SourceName
            FROM dbo.FileAuditEvents
            {{where}}
            ORDER BY TimestampUtc DESC;
            """;
    }

    private static async Task<long> CountEventsAsync(
        SqlConnection connection,
        ActivitySummaryQuery query,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var where = BuildSummaryWhereSql(command, query);
        command.CommandText = $$"""
            SELECT COUNT_BIG(1)
            FROM dbo.FileAuditEvents
            {{where}};
            """;

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<IReadOnlyCollection<ActivitySummaryItem>> QueryDimensionAsync(
        SqlConnection connection,
        ActivitySummaryQuery query,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var where = BuildSummaryWhereSql(command, query);
        command.CommandText = $$"""
            SELECT TOP (@Take)
                {{columnName}} AS Name,
                COUNT_BIG(1) AS EventCount
            FROM dbo.FileAuditEvents
            {{where}}
            GROUP BY {{columnName}}
            ORDER BY EventCount DESC, Name ASC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<ActivitySummaryItem>();

        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ActivitySummaryItem(
                Name: reader.GetString(reader.GetOrdinal("Name")),
                EventCount: Convert.ToInt64(reader["EventCount"])));
        }

        return items;
    }

    private static async Task<IReadOnlyCollection<BaselineAnomalyItem>> BuildAnomaliesAsync(
        SqlConnection connection,
        BaselineAnomalyQuery query,
        string columnName,
        CancellationToken cancellationToken)
    {
        var current = await QueryDimensionCountsAsync(
            connection,
            query.Server,
            query.Share,
            query.User,
            query.Action,
            query.FromUtc,
            query.ToUtc,
            columnName,
            cancellationToken);

        var baselineTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var windowSize = query.ToUtc - query.FromUtc;

        for (var index = 1; index <= query.BaselineWindows; index++)
        {
            var windowTo = query.FromUtc - TimeSpan.FromTicks(windowSize.Ticks * (index - 1));
            var windowFrom = windowTo - windowSize;
            var items = await QueryDimensionCountsAsync(
                connection,
                query.Server,
                query.Share,
                query.User,
                query.Action,
                windowFrom,
                windowTo,
                columnName,
                cancellationToken);

            foreach (var item in items)
            {
                baselineTotals[item.Key] = baselineTotals.GetValueOrDefault(item.Key, 0) + item.Value;
            }
        }

        return BaselineAnomalyCalculator.Build(current, baselineTotals, query.BaselineWindows, query.Take);
    }

    private static async Task<Dictionary<string, long>> QueryDimensionCountsAsync(
        SqlConnection connection,
        string? server,
        string? share,
        string? user,
        string? action,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("@FromUtc", fromUtc.UtcDateTime);
        command.Parameters.AddWithValue("@ToUtc", toUtc.UtcDateTime);
        var predicates = new List<string>
        {
            "TimestampUtc >= @FromUtc",
            "TimestampUtc <= @ToUtc"
        };

        if (!string.IsNullOrWhiteSpace(server))
        {
            predicates.Add("ServerName LIKE @Server");
            command.Parameters.AddWithValue("@Server", $"%{server}%");
        }

        if (!string.IsNullOrWhiteSpace(share))
        {
            predicates.Add("ShareName LIKE @Share");
            command.Parameters.AddWithValue("@Share", $"%{share}%");
        }

        if (!string.IsNullOrWhiteSpace(user))
        {
            predicates.Add("UserName LIKE @User");
            command.Parameters.AddWithValue("@User", $"%{user}%");
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            predicates.Add("ActionName = @Action");
            command.Parameters.AddWithValue("@Action", action);
        }

        command.CommandText = $$"""
            SELECT
                {{columnName}} AS Name,
                COUNT_BIG(1) AS EventCount
            FROM dbo.FileAuditEvents
            WHERE {{string.Join(" AND ", predicates)}}
            GROUP BY {{columnName}};
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetString(reader.GetOrdinal("Name"))] = Convert.ToInt64(reader["EventCount"]);
        }

        return result;
    }

    private static void AddSummaryWindowParameters(SqlCommand command, ActivitySummaryQuery query)
    {
        command.Parameters.AddWithValue("@FromUtc", query.FromUtc.UtcDateTime);
        command.Parameters.AddWithValue("@ToUtc", query.ToUtc.UtcDateTime);
        command.Parameters.AddWithValue("@Take", query.Take);
    }

    private static string BuildSummaryWhereSql(SqlCommand command, ActivitySummaryQuery query)
    {
        AddSummaryWindowParameters(command, query);
        var predicates = new List<string>
        {
            "TimestampUtc >= @FromUtc",
            "TimestampUtc <= @ToUtc"
        };

        if (!string.IsNullOrWhiteSpace(query.Server))
        {
            predicates.Add("ServerName LIKE @SummaryServer");
            command.Parameters.AddWithValue("@SummaryServer", $"%{query.Server}%");
        }

        if (!string.IsNullOrWhiteSpace(query.Share))
        {
            predicates.Add("ShareName LIKE @SummaryShare");
            command.Parameters.AddWithValue("@SummaryShare", $"%{query.Share}%");
        }

        if (!string.IsNullOrWhiteSpace(query.User))
        {
            predicates.Add("UserName LIKE @SummaryUser");
            command.Parameters.AddWithValue("@SummaryUser", $"%{query.User}%");
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            predicates.Add("ActionName = @SummaryAction");
            command.Parameters.AddWithValue("@SummaryAction", query.Action);
        }

        return $"WHERE {string.Join(" AND ", predicates)}";
    }

    private static void AddEventParameters(SqlCommand command, FileAuditEvent auditEvent)
    {
        command.Parameters.AddWithValue("@Id", auditEvent.Id);
        command.Parameters.AddWithValue("@TimestampUtc", auditEvent.TimestampUtc.UtcDateTime);
        command.Parameters.AddWithValue("@ServerName", auditEvent.Server);
        command.Parameters.AddWithValue("@ShareName", auditEvent.Share);
        command.Parameters.AddWithValue("@FullPath", auditEvent.Path);
        command.Parameters.AddWithValue("@PreviousPath", DbValue(auditEvent.PreviousPath));
        command.Parameters.AddWithValue("@ObjectType", auditEvent.ObjectType);
        command.Parameters.AddWithValue("@ActionName", auditEvent.Action);
        command.Parameters.AddWithValue("@UserName", auditEvent.User);
        command.Parameters.AddWithValue("@Sid", DbValue(auditEvent.Sid));
        command.Parameters.AddWithValue("@SourceHost", DbValue(auditEvent.SourceHost));
        command.Parameters.AddWithValue("@SourceIp", DbValue(auditEvent.SourceIp));
        command.Parameters.AddWithValue("@ProcessName", DbValue(auditEvent.ProcessName));
        command.Parameters.AddWithValue("@FileSizeBytes", DbValue(auditEvent.FileSizeBytes));
        command.Parameters.AddWithValue("@Extension", DbValue(auditEvent.Extension));
        command.Parameters.AddWithValue("@ResultName", auditEvent.Result);
        command.Parameters.AddWithValue("@Severity", auditEvent.Severity);
        command.Parameters.AddWithValue("@SourceName", auditEvent.Source);
    }

    private static FileAuditEvent ReadEvent(SqlDataReader reader)
    {
        return new FileAuditEvent(
            Id: reader.GetGuid(reader.GetOrdinal("Id")),
            TimestampUtc: new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("TimestampUtc")), DateTimeKind.Utc)),
            Server: reader.GetString(reader.GetOrdinal("ServerName")),
            Share: reader.GetString(reader.GetOrdinal("ShareName")),
            Path: reader.GetString(reader.GetOrdinal("FullPath")),
            PreviousPath: ReadNullableString(reader, "PreviousPath"),
            ObjectType: reader.GetString(reader.GetOrdinal("ObjectType")),
            Action: reader.GetString(reader.GetOrdinal("ActionName")),
            User: reader.GetString(reader.GetOrdinal("UserName")),
            Sid: ReadNullableString(reader, "Sid"),
            SourceHost: ReadNullableString(reader, "SourceHost"),
            SourceIp: ReadNullableString(reader, "SourceIp"),
            ProcessName: ReadNullableString(reader, "ProcessName"),
            FileSizeBytes: ReadNullableLong(reader, "FileSizeBytes"),
            Extension: ReadNullableString(reader, "Extension"),
            Result: reader.GetString(reader.GetOrdinal("ResultName")),
            Severity: reader.GetString(reader.GetOrdinal("Severity")),
            Source: reader.GetString(reader.GetOrdinal("SourceName")));
    }

    private static string? ReadNullableString(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static long? ReadNullableLong(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static object DbValue<T>(T? value)
    {
        return value is null ? DBNull.Value : value;
    }
}
#endif

internal sealed class InMemoryEventRepository : IEventRepository
{
    private readonly ConcurrentQueue<FileAuditEvent> _events = new();
    private readonly int _maxEvents;

    public InMemoryEventRepository(IConfiguration configuration)
    {
        _maxEvents = configuration.GetValue("Monitor:InMemoryMaxEvents", 10_000);
    }

    public string ProviderName => "InMemory";

    public Task AddAsync(FileAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        Add(auditEvent);
        return Task.CompletedTask;
    }

    public Task AddBatchAsync(IReadOnlyCollection<FileAuditEvent> events, CancellationToken cancellationToken)
    {
        foreach (var auditEvent in events)
        {
            Add(auditEvent);
        }

        return Task.CompletedTask;
    }

    public Task<FileAuditEvent?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        return Task.FromResult(_events.FirstOrDefault(item => item.Id == id));
    }

    public Task<IReadOnlyCollection<FileAuditEvent>> QueryAsync(EventQuery query, CancellationToken cancellationToken)
    {
        IEnumerable<FileAuditEvent> events = _events;

        if (!string.IsNullOrWhiteSpace(query.Server))
        {
            events = events.Where(item => item.Server.Contains(query.Server, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.User))
        {
            events = events.Where(item => item.User.Contains(query.User, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            events = events.Where(item => item.Action.Equals(query.Action, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Path))
        {
            events = events.Where(item => item.Path.Contains(query.Path, StringComparison.OrdinalIgnoreCase));
        }

        if (query.FromUtc is not null)
        {
            events = events.Where(item => item.TimestampUtc >= query.FromUtc);
        }

        if (query.ToUtc is not null)
        {
            events = events.Where(item => item.TimestampUtc <= query.ToUtc);
        }

        IReadOnlyCollection<FileAuditEvent> result = events
            .OrderByDescending(item => item.TimestampUtc)
            .Take(query.Take)
            .ToArray();

        return Task.FromResult(result);
    }

    public Task<EventStoreStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        var stats = new EventStoreStats(
            StoredEvents: _events.Count,
            LastEventUtc: _events.LastOrDefault()?.TimestampUtc);

        return Task.FromResult(stats);
    }

    public Task<ActivitySummaryResponse> GetActivitySummaryAsync(
        ActivitySummaryQuery query,
        CancellationToken cancellationToken)
    {
        var events = _events
            .Where(item => item.TimestampUtc >= query.FromUtc)
            .Where(item => item.TimestampUtc <= query.ToUtc)
            .Where(item => MatchesText(item.Server, query.Server))
            .Where(item => MatchesText(item.Share, query.Share))
            .Where(item => MatchesText(item.User, query.User))
            .Where(item => MatchesAction(item.Action, query.Action))
            .ToArray();

        var summary = new ActivitySummaryResponse(
            FromUtc: query.FromUtc,
            ToUtc: query.ToUtc,
            TotalEvents: events.Length,
            ByAction: Summarize(events, item => item.Action, query.Take),
            ByShare: Summarize(events, item => item.Share, query.Take),
            ByUser: Summarize(events, item => item.User, query.Take));

        return Task.FromResult(summary);
    }

    public Task<BaselineAnomalyResponse> GetBaselineAnomaliesAsync(
        BaselineAnomalyQuery query,
        CancellationToken cancellationToken)
    {
        var current = _events
            .Where(item => item.TimestampUtc >= query.FromUtc && item.TimestampUtc <= query.ToUtc)
            .Where(item => MatchesText(item.Server, query.Server))
            .Where(item => MatchesText(item.Share, query.Share))
            .Where(item => MatchesText(item.User, query.User))
            .Where(item => MatchesAction(item.Action, query.Action))
            .ToArray();

        var windowSize = query.ToUtc - query.FromUtc;
        var baselineEvents = new List<FileAuditEvent>();

        for (var index = 1; index <= query.BaselineWindows; index++)
        {
            var windowTo = query.FromUtc - TimeSpan.FromTicks(windowSize.Ticks * (index - 1));
            var windowFrom = windowTo - windowSize;
            baselineEvents.AddRange(_events.Where(item =>
                item.TimestampUtc >= windowFrom
                && item.TimestampUtc <= windowTo
                && MatchesText(item.Server, query.Server)
                && MatchesText(item.Share, query.Share)
                && MatchesText(item.User, query.User)
                && MatchesAction(item.Action, query.Action)));
        }

        return Task.FromResult(new BaselineAnomalyResponse(
            FromUtc: query.FromUtc,
            ToUtc: query.ToUtc,
            BaselineWindows: query.BaselineWindows,
            ByAction: BaselineAnomalyCalculator.Build(
                CountBy(current, item => item.Action),
                CountBy(baselineEvents, item => item.Action),
                query.BaselineWindows,
                query.Take),
            ByShare: BaselineAnomalyCalculator.Build(
                CountBy(current, item => item.Share),
                CountBy(baselineEvents, item => item.Share),
                query.BaselineWindows,
                query.Take),
            ByUser: BaselineAnomalyCalculator.Build(
                CountBy(current, item => item.User),
                CountBy(baselineEvents, item => item.User),
                query.BaselineWindows,
                query.Take)));
    }

    public Task<int> PurgeOlderThanAsync(
        DateTimeOffset cutoffUtc,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var deleted = 0;

        while (_events.TryPeek(out var auditEvent) && auditEvent.TimestampUtc < cutoffUtc)
        {
            if (_events.TryDequeue(out _))
            {
                deleted++;
            }
        }

        return Task.FromResult(deleted);
    }

    private void Add(FileAuditEvent auditEvent)
    {
        _events.Enqueue(auditEvent);

        while (_events.Count > _maxEvents && _events.TryDequeue(out _))
        {
        }
    }

    private static IReadOnlyCollection<ActivitySummaryItem> Summarize(
        IReadOnlyCollection<FileAuditEvent> events,
        Func<FileAuditEvent, string> selector,
        int take)
    {
        return events
            .GroupBy(selector, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ActivitySummaryItem(group.Key, group.LongCount()))
            .OrderByDescending(item => item.EventCount)
            .ThenBy(item => item.Name)
            .Take(take)
            .ToArray();
    }

    private static Dictionary<string, long> CountBy(
        IEnumerable<FileAuditEvent> events,
        Func<FileAuditEvent, string> selector)
    {
        return events
            .GroupBy(selector, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.LongCount(), StringComparer.OrdinalIgnoreCase);
    }

    private static bool MatchesText(string value, string? filter)
    {
        return string.IsNullOrWhiteSpace(filter)
            || value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesAction(string value, string? filter)
    {
        return string.IsNullOrWhiteSpace(filter)
            || value.Equals(filter, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class AgentHealthStore
{
    private readonly ConcurrentDictionary<string, AgentHealthResponse> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _staleMinutes;
    private readonly int _backlogWarningThreshold;
#if SQLSERVER
    private readonly bool _persistHeartbeats;
    private readonly string? _connectionString;
#endif

    public AgentHealthStore(IConfiguration configuration)
    {
        _staleMinutes = configuration.GetValue("Agents:StaleMinutes", 10);
        _backlogWarningThreshold = configuration.GetValue("Agents:BacklogWarningThreshold", 1000);
#if SQLSERVER
        _persistHeartbeats = configuration.GetValue("Monitor:StorageProvider", "SqlServer")
            .Equals("SqlServer", StringComparison.OrdinalIgnoreCase);
        _connectionString = configuration.GetConnectionString("SqlServer");
#endif
    }

    public async Task UpsertAsync(AgentHealthResponse heartbeat, CancellationToken cancellationToken)
    {
        _agents.AddOrUpdate(heartbeat.AgentId, heartbeat, (_, _) => heartbeat);

#if SQLSERVER
        if (_persistHeartbeats)
        {
            await UpsertSqlAsync(heartbeat, cancellationToken);
        }
#endif
    }

    public async Task<IReadOnlyCollection<AgentHealthResponse>> ListAsync(CancellationToken cancellationToken)
    {
#if SQLSERVER
        if (_persistHeartbeats)
        {
            var agents = await ListSqlAsync(cancellationToken);

            foreach (var agent in agents)
            {
                _agents.AddOrUpdate(agent.AgentId, agent, (_, _) => agent);
            }

            return NormalizeAgentHealth(agents);
        }
#endif

        return ListMemory();
    }

    private IReadOnlyCollection<AgentHealthResponse> ListMemory()
    {
        return _agents.Values
            .OrderBy(item => item.Server)
            .ThenBy(item => item.AgentId)
            .Select(NormalizeAgentHealth)
            .ToArray();
    }

    private IReadOnlyCollection<AgentHealthResponse> NormalizeAgentHealth(IReadOnlyCollection<AgentHealthResponse> agents)
    {
        return agents.Select(NormalizeAgentHealth).ToArray();
    }

    private AgentHealthResponse NormalizeAgentHealth(AgentHealthResponse agent)
    {
        if (agent.LastHeartbeatUtc is null)
        {
            return agent with
            {
                Status = "stale",
                IsStale = true,
                StaleAfterMinutes = _staleMinutes,
                BacklogWarningThreshold = _backlogWarningThreshold,
                Message = agent.Message ?? "Agente sem heartbeat registrado."
            };
        }

        var minutesSinceHeartbeat = DateTimeOffset.UtcNow.Subtract(agent.LastHeartbeatUtc.Value).TotalMinutes;

        if (minutesSinceHeartbeat > Math.Max(1, _staleMinutes))
        {
            return agent with
            {
                Status = "stale",
                IsStale = true,
                StaleAfterMinutes = _staleMinutes,
                BacklogWarningThreshold = _backlogWarningThreshold,
                Message = agent.Message ?? $"Sem heartbeat ha {Math.Floor(minutesSinceHeartbeat)} minuto(s)."
            };
        }

        if (agent.PendingQueueEvents >= Math.Max(1, _backlogWarningThreshold))
        {
            return agent with
            {
                Status = "backlog",
                IsStale = false,
                StaleAfterMinutes = _staleMinutes,
                BacklogWarningThreshold = _backlogWarningThreshold,
                Message = agent.Message ?? $"Fila local com {agent.PendingQueueEvents} evento(s) pendente(s)."
            };
        }

        return agent with
        {
            IsStale = false,
            StaleAfterMinutes = _staleMinutes,
            BacklogWarningThreshold = _backlogWarningThreshold
        };
    }

#if SQLSERVER
    private async Task UpsertSqlAsync(AgentHealthResponse heartbeat, CancellationToken cancellationToken)
    {
        await using var connection = CreateSqlConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            MERGE dbo.AgentHeartbeats AS target
            USING (SELECT @AgentId AS AgentId) AS source
                ON target.AgentId = source.AgentId
            WHEN MATCHED THEN
                UPDATE SET
                    ServerName = @ServerName,
                    StatusName = @StatusName,
                    LastHeartbeatUtc = @LastHeartbeatUtc,
                    VersionName = @VersionName,
                    LastRecordId = @LastRecordId,
                    LastUsnByVolumeJson = @LastUsnByVolumeJson,
                    PendingQueueEvents = @PendingQueueEvents,
                    LastSuccessfulSendUtc = @LastSuccessfulSendUtc,
                    Message = @Message
            WHEN NOT MATCHED THEN
                INSERT
                (
                    AgentId,
                    ServerName,
                    StatusName,
                    LastHeartbeatUtc,
                    VersionName,
                    LastRecordId,
                    LastUsnByVolumeJson,
                    PendingQueueEvents,
                    LastSuccessfulSendUtc,
                    Message
                )
                VALUES
                (
                    @AgentId,
                    @ServerName,
                    @StatusName,
                    @LastHeartbeatUtc,
                    @VersionName,
                    @LastRecordId,
                    @LastUsnByVolumeJson,
                    @PendingQueueEvents,
                    @LastSuccessfulSendUtc,
                    @Message
                );
            """;

        AddHeartbeatParameters(command, heartbeat);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyCollection<AgentHealthResponse>> ListSqlAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateSqlConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                AgentId,
                ServerName,
                StatusName,
                LastHeartbeatUtc,
                VersionName,
                LastRecordId,
                LastUsnByVolumeJson,
                PendingQueueEvents,
                LastSuccessfulSendUtc,
                Message
            FROM dbo.AgentHeartbeats
            ORDER BY ServerName, AgentId;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var agents = new List<AgentHealthResponse>();

        while (await reader.ReadAsync(cancellationToken))
        {
            agents.Add(ReadHeartbeat(reader));
        }

        return agents;
    }

    private SqlConnection CreateSqlConnection()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:SqlServer nao foi configurada.");
        }

        return new SqlConnection(_connectionString);
    }

    private static void AddHeartbeatParameters(SqlCommand command, AgentHealthResponse heartbeat)
    {
        command.Parameters.AddWithValue("@AgentId", heartbeat.AgentId);
        command.Parameters.AddWithValue("@ServerName", heartbeat.Server);
        command.Parameters.AddWithValue("@StatusName", heartbeat.Status);
        command.Parameters.AddWithValue("@LastHeartbeatUtc", (heartbeat.LastHeartbeatUtc ?? DateTimeOffset.UtcNow).UtcDateTime);
        command.Parameters.AddWithValue("@VersionName", DbValue(heartbeat.Version));
        command.Parameters.AddWithValue("@LastRecordId", heartbeat.LastRecordId);
        command.Parameters.AddWithValue("@LastUsnByVolumeJson", JsonSerializer.Serialize(heartbeat.LastUsnByVolume));
        command.Parameters.AddWithValue("@PendingQueueEvents", heartbeat.PendingQueueEvents);
        command.Parameters.AddWithValue("@LastSuccessfulSendUtc", DbValue(heartbeat.LastSuccessfulSendUtc?.UtcDateTime));
        command.Parameters.AddWithValue("@Message", DbValue(heartbeat.Message));
    }

    private static AgentHealthResponse ReadHeartbeat(SqlDataReader reader)
    {
        var lastUsnJson = ReadNullableString(reader, "LastUsnByVolumeJson");
        var lastUsnByVolume = string.IsNullOrWhiteSpace(lastUsnJson)
            ? new Dictionary<string, long>()
            : JsonSerializer.Deserialize<Dictionary<string, long>>(lastUsnJson) ?? new Dictionary<string, long>();

        return new AgentHealthResponse(
            AgentId: reader.GetString(reader.GetOrdinal("AgentId")),
            Server: reader.GetString(reader.GetOrdinal("ServerName")),
            Status: reader.GetString(reader.GetOrdinal("StatusName")),
            LastHeartbeatUtc: ReadUtcDateTimeOffset(reader, "LastHeartbeatUtc"),
            Version: ReadNullableString(reader, "VersionName"),
            LastRecordId: reader.GetInt64(reader.GetOrdinal("LastRecordId")),
            LastUsnByVolume: lastUsnByVolume,
            Message: ReadNullableString(reader, "Message"),
            PendingQueueEvents: reader.GetInt32(reader.GetOrdinal("PendingQueueEvents")),
            LastSuccessfulSendUtc: ReadNullableUtcDateTimeOffset(reader, "LastSuccessfulSendUtc"));
    }

    private static DateTimeOffset ReadUtcDateTimeOffset(SqlDataReader reader, string name)
    {
        var value = reader.GetDateTime(reader.GetOrdinal(name));
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private static DateTimeOffset? ReadNullableUtcDateTimeOffset(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);

        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
    }

    private static string? ReadNullableString(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static object DbValue<T>(T? value)
    {
        return value is null ? DBNull.Value : value;
    }
#endif
}

internal sealed class MonitoredPathStore
{
    private readonly ConcurrentDictionary<Guid, MonitoredPath> _paths = new();
#if SQLSERVER
    private readonly bool _persistPaths;
    private readonly string? _connectionString;
#endif

    public MonitoredPathStore(IConfiguration configuration)
    {
#if SQLSERVER
        _persistPaths = configuration.GetValue("Monitor:StorageProvider", "SqlServer")
            .Equals("SqlServer", StringComparison.OrdinalIgnoreCase);
        _connectionString = configuration.GetConnectionString("SqlServer");
#endif
    }

    public async Task<IReadOnlyCollection<MonitoredPath>> ListAsync(
        string? server,
        string? status,
        CancellationToken cancellationToken)
    {
#if SQLSERVER
        if (_persistPaths)
        {
            var persistedPaths = await ListSqlAsync(server, status, cancellationToken);

            foreach (var path in persistedPaths)
            {
                _paths.AddOrUpdate(path.Id, path, (_, _) => path);
            }

            return persistedPaths;
        }
#endif

        IEnumerable<MonitoredPath> paths = _paths.Values;

        if (!string.IsNullOrWhiteSpace(server))
        {
            paths = paths.Where(item => item.Server.Contains(server, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            paths = paths.Where(item => item.Status.Equals(status, StringComparison.OrdinalIgnoreCase));
        }

        return paths
            .OrderBy(item => item.Server)
            .ThenBy(item => item.Share)
            .ThenBy(item => item.Path)
            .ToArray();
    }

    public async Task<MonitoredPath> CreateAsync(
        MonitoredPathRequest request,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var path = NormalizeRequest(request, Guid.NewGuid(), now, now);

        _paths[path.Id] = path;

#if SQLSERVER
        if (_persistPaths)
        {
            await UpsertSqlAsync(path, cancellationToken);
        }
#endif

        return path;
    }

    public async Task<MonitoredPath?> UpdateAsync(
        Guid id,
        MonitoredPathRequest request,
        CancellationToken cancellationToken)
    {
        var existing = await FindAsync(id, cancellationToken);

        if (existing is null)
        {
            return null;
        }

        var updated = NormalizeRequest(request, id, existing.CreatedUtc, DateTimeOffset.UtcNow);
        _paths[id] = updated;

#if SQLSERVER
        if (_persistPaths)
        {
            await UpsertSqlAsync(updated, cancellationToken);
        }
#endif

        return updated;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var removed = _paths.TryRemove(id, out _);

#if SQLSERVER
        if (_persistPaths)
        {
            removed = await DeleteSqlAsync(id, cancellationToken);
        }
#endif

        return removed;
    }

    private async Task<MonitoredPath?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        if (_paths.TryGetValue(id, out var path))
        {
            return path;
        }

#if SQLSERVER
        if (_persistPaths)
        {
            return await FindSqlAsync(id, cancellationToken);
        }
#endif

        return null;
    }

    private static MonitoredPath NormalizeRequest(
        MonitoredPathRequest request,
        Guid id,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc)
    {
        var server = Require(request.Server, "Servidor");
        var share = Require(request.Share, "Compartilhamento");
        var path = Require(request.Path, "Caminho");
        var status = NormalizeChoice(request.Status, "planned", "planned", "active", "paused", "retired");
        var priority = NormalizeChoice(request.Priority, "normal", "low", "normal", "high", "critical");

        return new MonitoredPath(
            Id: id,
            Server: server,
            Share: share,
            Path: path,
            Status: status,
            Priority: priority,
            Owner: TrimOptional(request.Owner),
            Notes: TrimOptional(request.Notes),
            CreatedUtc: createdUtc,
            UpdatedUtc: updatedUtc);
    }

    private static string Require(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new BadHttpRequestException($"{fieldName} e obrigatorio.");
        }

        return value.Trim();
    }

    private static string NormalizeChoice(string? value, string fallback, params string[] allowed)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim().ToLowerInvariant();

        if (!allowed.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            throw new BadHttpRequestException($"Valor invalido: {value}.");
        }

        return normalized;
    }

    private static string? TrimOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

#if SQLSERVER
    private async Task<IReadOnlyCollection<MonitoredPath>> ListSqlAsync(
        string? server,
        string? status,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateSqlConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var predicates = new List<string>();

        if (!string.IsNullOrWhiteSpace(server))
        {
            predicates.Add("ServerName LIKE @ServerName");
            command.Parameters.AddWithValue("@ServerName", $"%{server}%");
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            predicates.Add("StatusName = @StatusName");
            command.Parameters.AddWithValue("@StatusName", status);
        }

        var where = predicates.Count == 0
            ? string.Empty
            : $"WHERE {string.Join(" AND ", predicates)}";

        command.CommandText = $$"""
            SELECT
                Id,
                ServerName,
                ShareName,
                RootPath,
                StatusName,
                PriorityName,
                OwnerName,
                Notes,
                CreatedUtc,
                UpdatedUtc
            FROM dbo.MonitoredPaths
            {{where}}
            ORDER BY ServerName, ShareName, RootPath;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var paths = new List<MonitoredPath>();

        while (await reader.ReadAsync(cancellationToken))
        {
            paths.Add(ReadMonitoredPath(reader));
        }

        return paths;
    }

    private async Task<MonitoredPath?> FindSqlAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = CreateSqlConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1)
                Id,
                ServerName,
                ShareName,
                RootPath,
                StatusName,
                PriorityName,
                OwnerName,
                Notes,
                CreatedUtc,
                UpdatedUtc
            FROM dbo.MonitoredPaths
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? ReadMonitoredPath(reader)
            : null;
    }

    private async Task UpsertSqlAsync(MonitoredPath path, CancellationToken cancellationToken)
    {
        await using var connection = CreateSqlConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            MERGE dbo.MonitoredPaths AS target
            USING (SELECT @Id AS Id) AS source
                ON target.Id = source.Id
            WHEN MATCHED THEN
                UPDATE SET
                    ServerName = @ServerName,
                    ShareName = @ShareName,
                    RootPath = @RootPath,
                    StatusName = @StatusName,
                    PriorityName = @PriorityName,
                    OwnerName = @OwnerName,
                    Notes = @Notes,
                    UpdatedUtc = @UpdatedUtc
            WHEN NOT MATCHED THEN
                INSERT
                (
                    Id,
                    ServerName,
                    ShareName,
                    RootPath,
                    StatusName,
                    PriorityName,
                    OwnerName,
                    Notes,
                    CreatedUtc,
                    UpdatedUtc
                )
                VALUES
                (
                    @Id,
                    @ServerName,
                    @ShareName,
                    @RootPath,
                    @StatusName,
                    @PriorityName,
                    @OwnerName,
                    @Notes,
                    @CreatedUtc,
                    @UpdatedUtc
                );
            """;
        AddMonitoredPathParameters(command, path);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> DeleteSqlAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = CreateSqlConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM dbo.MonitoredPaths
            WHERE Id = @Id;

            SELECT @@ROWCOUNT;
            """;
        command.Parameters.AddWithValue("@Id", id);

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private SqlConnection CreateSqlConnection()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:SqlServer nao foi configurada.");
        }

        return new SqlConnection(_connectionString);
    }

    private static void AddMonitoredPathParameters(SqlCommand command, MonitoredPath path)
    {
        command.Parameters.AddWithValue("@Id", path.Id);
        command.Parameters.AddWithValue("@ServerName", path.Server);
        command.Parameters.AddWithValue("@ShareName", path.Share);
        command.Parameters.AddWithValue("@RootPath", path.Path);
        command.Parameters.AddWithValue("@StatusName", path.Status);
        command.Parameters.AddWithValue("@PriorityName", path.Priority);
        command.Parameters.AddWithValue("@OwnerName", DbValue(path.Owner));
        command.Parameters.AddWithValue("@Notes", DbValue(path.Notes));
        command.Parameters.AddWithValue("@CreatedUtc", path.CreatedUtc.UtcDateTime);
        command.Parameters.AddWithValue("@UpdatedUtc", path.UpdatedUtc.UtcDateTime);
    }

    private static MonitoredPath ReadMonitoredPath(SqlDataReader reader)
    {
        return new MonitoredPath(
            Id: reader.GetGuid(reader.GetOrdinal("Id")),
            Server: reader.GetString(reader.GetOrdinal("ServerName")),
            Share: reader.GetString(reader.GetOrdinal("ShareName")),
            Path: reader.GetString(reader.GetOrdinal("RootPath")),
            Status: reader.GetString(reader.GetOrdinal("StatusName")),
            Priority: reader.GetString(reader.GetOrdinal("PriorityName")),
            Owner: ReadNullableString(reader, "OwnerName"),
            Notes: ReadNullableString(reader, "Notes"),
            CreatedUtc: ReadUtcDateTimeOffset(reader, "CreatedUtc"),
            UpdatedUtc: ReadUtcDateTimeOffset(reader, "UpdatedUtc"));
    }

    private static DateTimeOffset ReadUtcDateTimeOffset(SqlDataReader reader, string name)
    {
        var value = reader.GetDateTime(reader.GetOrdinal(name));
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private static string? ReadNullableString(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static object DbValue<T>(T? value)
    {
        return value is null ? DBNull.Value : value;
    }
#endif
}

internal sealed class AdminAuditStore
{
    private readonly ConcurrentQueue<AdminAuditEntry> _entries = new();
    private readonly int _maxEntries;
#if SQLSERVER
    private readonly bool _persistEntries;
    private readonly string? _connectionString;
#endif

    public AdminAuditStore(IConfiguration configuration)
    {
        _maxEntries = configuration.GetValue("AdminAudit:InMemoryMaxEntries", 10_000);
#if SQLSERVER
        _persistEntries = configuration.GetValue("Monitor:StorageProvider", "SqlServer")
            .Equals("SqlServer", StringComparison.OrdinalIgnoreCase);
        _connectionString = configuration.GetConnectionString("SqlServer");
#endif
    }

    public async Task AddAsync(AdminAuditEntry entry, CancellationToken cancellationToken)
    {
        _entries.Enqueue(entry);

        while (_entries.Count > _maxEntries && _entries.TryDequeue(out _))
        {
        }

#if SQLSERVER
        if (_persistEntries)
        {
            await InsertSqlAsync(entry, cancellationToken);
        }
#endif
    }

    public async Task<IReadOnlyCollection<AdminAuditEntry>> ListAsync(
        AdminAuditQuery query,
        CancellationToken cancellationToken)
    {
#if SQLSERVER
        if (_persistEntries)
        {
            return await ListSqlAsync(query, cancellationToken);
        }
#endif

        IEnumerable<AdminAuditEntry> entries = _entries;

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            entries = entries.Where(item => item.Action.Equals(query.Action, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            entries = entries.Where(item => item.EntityType.Equals(query.EntityType, StringComparison.OrdinalIgnoreCase));
        }

        return entries
            .OrderByDescending(item => item.TimestampUtc)
            .Take(query.Take)
            .ToArray();
    }

#if SQLSERVER
    private async Task InsertSqlAsync(AdminAuditEntry entry, CancellationToken cancellationToken)
    {
        await using var connection = CreateSqlConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.AdminAuditLog
            (
                Id,
                TimestampUtc,
                ActionName,
                EntityType,
                EntityId,
                ActorName,
                SourceIp,
                DetailsJson
            )
            VALUES
            (
                @Id,
                @TimestampUtc,
                @ActionName,
                @EntityType,
                @EntityId,
                @ActorName,
                @SourceIp,
                @DetailsJson
            );
            """;
        AddParameters(command, entry);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyCollection<AdminAuditEntry>> ListSqlAsync(
        AdminAuditQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateSqlConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("@Take", query.Take);

        var predicates = new List<string>();

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            predicates.Add("ActionName = @ActionName");
            command.Parameters.AddWithValue("@ActionName", query.Action);
        }

        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            predicates.Add("EntityType = @EntityType");
            command.Parameters.AddWithValue("@EntityType", query.EntityType);
        }

        var where = predicates.Count == 0
            ? string.Empty
            : $"WHERE {string.Join(" AND ", predicates)}";

        command.CommandText = $$"""
            SELECT TOP (@Take)
                Id,
                TimestampUtc,
                ActionName,
                EntityType,
                EntityId,
                ActorName,
                SourceIp,
                DetailsJson
            FROM dbo.AdminAuditLog
            {{where}}
            ORDER BY TimestampUtc DESC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<AdminAuditEntry>();

        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(ReadEntry(reader));
        }

        return entries;
    }

    private SqlConnection CreateSqlConnection()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:SqlServer nao foi configurada.");
        }

        return new SqlConnection(_connectionString);
    }

    private static void AddParameters(SqlCommand command, AdminAuditEntry entry)
    {
        command.Parameters.AddWithValue("@Id", entry.Id);
        command.Parameters.AddWithValue("@TimestampUtc", entry.TimestampUtc.UtcDateTime);
        command.Parameters.AddWithValue("@ActionName", entry.Action);
        command.Parameters.AddWithValue("@EntityType", entry.EntityType);
        command.Parameters.AddWithValue("@EntityId", entry.EntityId);
        command.Parameters.AddWithValue("@ActorName", entry.Actor);
        command.Parameters.AddWithValue("@SourceIp", DbValue(entry.SourceIp));
        command.Parameters.AddWithValue("@DetailsJson", DbValue(entry.DetailsJson));
    }

    private static AdminAuditEntry ReadEntry(SqlDataReader reader)
    {
        return new AdminAuditEntry(
            Id: reader.GetGuid(reader.GetOrdinal("Id")),
            TimestampUtc: ReadUtcDateTimeOffset(reader, "TimestampUtc"),
            Action: reader.GetString(reader.GetOrdinal("ActionName")),
            EntityType: reader.GetString(reader.GetOrdinal("EntityType")),
            EntityId: reader.GetString(reader.GetOrdinal("EntityId")),
            Actor: reader.GetString(reader.GetOrdinal("ActorName")),
            SourceIp: ReadNullableString(reader, "SourceIp"),
            DetailsJson: ReadNullableString(reader, "DetailsJson"));
    }

    private static DateTimeOffset ReadUtcDateTimeOffset(SqlDataReader reader, string name)
    {
        var value = reader.GetDateTime(reader.GetOrdinal(name));
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private static string? ReadNullableString(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static object DbValue<T>(T? value)
    {
        return value is null ? DBNull.Value : value;
    }
#endif
}

internal static class AdminAuditHelpers
{
    public static string GetActor(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Actor", out var actor)
            && !string.IsNullOrWhiteSpace(actor.FirstOrDefault()))
        {
            return actor.First()!;
        }

        if (context.Request.Headers.TryGetValue("X-Agent-Id", out var agentId)
            && !string.IsNullOrWhiteSpace(agentId.FirstOrDefault()))
        {
            return $"agent:{agentId.First()}";
        }

        return "api-key";
    }

    public static string? GetSourceIp(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
        {
            return forwardedFor.FirstOrDefault()?.Split(',')[0].Trim();
        }

        return context.Connection.RemoteIpAddress?.ToString();
    }
}

internal sealed class AlertRuleStore
{
    private static readonly AlertRuleConfig[] DefaultRules =
    {
        new(
            Rule: "mass-delete",
            Title: "Exclusao em massa",
            Description: "Dispara quando muitas exclusoes acontecem pelo mesmo usuario em uma janela curta.",
            Enabled: true,
            Severity: "critical",
            Threshold: 50,
            SecondaryThreshold: null,
            SecondarySeverity: null,
            ServerFilter: null,
            ShareFilter: null,
            PathFilter: null,
            ActiveFromHour: null,
            ActiveToHour: null,
            ActiveDays: null,
            ExcludedUsers: null,
            ExcludedHosts: null,
            ExcludedProcesses: null,
            TimeZoneId: null,
            UpdatedUtc: DateTimeOffset.UtcNow),
        new(
            Rule: "mass-rename",
            Title: "Renomeacao em massa",
            Description: "Dispara quando muitas renomeacoes acontecem pelo mesmo usuario em uma janela curta.",
            Enabled: true,
            Severity: "critical",
            Threshold: 100,
            SecondaryThreshold: null,
            SecondarySeverity: null,
            ServerFilter: null,
            ShareFilter: null,
            PathFilter: null,
            ActiveFromHour: null,
            ActiveToHour: null,
            ActiveDays: null,
            ExcludedUsers: null,
            ExcludedHosts: null,
            ExcludedProcesses: null,
            TimeZoneId: null,
            UpdatedUtc: DateTimeOffset.UtcNow),
        new(
            Rule: "possible-ransomware",
            Title: "Possivel ransomware",
            Description: "Dispara por volume alto de alteracoes ou por extensoes suspeitas em curto periodo.",
            Enabled: true,
            Severity: "high",
            Threshold: 250,
            SecondaryThreshold: 10,
            SecondarySeverity: "critical",
            ServerFilter: null,
            ShareFilter: null,
            PathFilter: null,
            ActiveFromHour: null,
            ActiveToHour: null,
            ActiveDays: null,
            ExcludedUsers: null,
            ExcludedHosts: null,
            ExcludedProcesses: null,
            TimeZoneId: null,
            UpdatedUtc: DateTimeOffset.UtcNow),
        new(
            Rule: "permission-change",
            Title: "Alteracao de permissao",
            Description: "Dispara quando arquivos ou pastas recebem mudancas de permissao.",
            Enabled: true,
            Severity: "high",
            Threshold: null,
            SecondaryThreshold: null,
            SecondarySeverity: null,
            ServerFilter: null,
            ShareFilter: null,
            PathFilter: null,
            ActiveFromHour: null,
            ActiveToHour: null,
            ActiveDays: null,
            ExcludedUsers: null,
            ExcludedHosts: null,
            ExcludedProcesses: null,
            TimeZoneId: null,
            UpdatedUtc: DateTimeOffset.UtcNow)
    };

    private readonly ConcurrentDictionary<string, AlertRuleConfig> _rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly AlertOptions _defaults;
#if SQLSERVER
    private readonly bool _persistRules;
    private readonly string? _connectionString;
    private bool _loadedFromSql;
#endif

    public AlertRuleStore(IConfiguration configuration)
    {
        _defaults = new AlertOptions(
            WindowMinutes: configuration.GetValue("Alerts:WindowMinutes", 5),
            DedupMinutes: configuration.GetValue("Alerts:DedupMinutes", 10),
            MassDeleteThreshold: configuration.GetValue("Alerts:MassDeleteThreshold", 50),
            MassRenameThreshold: configuration.GetValue("Alerts:MassRenameThreshold", 100),
            RansomwareActivityThreshold: configuration.GetValue("Alerts:RansomwareActivityThreshold", 250),
            SuspiciousExtensionThreshold: configuration.GetValue("Alerts:SuspiciousExtensionThreshold", 10));

        foreach (var rule in BuildDefaultRules())
        {
            _rules[rule.Rule] = rule;
        }

#if SQLSERVER
        _persistRules = configuration.GetValue("Monitor:StorageProvider", "SqlServer")
            .Equals("SqlServer", StringComparison.OrdinalIgnoreCase);
        _connectionString = configuration.GetConnectionString("SqlServer");
#endif
    }

    public async Task<IReadOnlyCollection<AlertRuleConfig>> ListAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        return _rules.Values.OrderBy(item => item.Rule).ToArray();
    }

    public async Task<AlertRuleConfig?> UpdateAsync(
        string ruleName,
        AlertRuleUpdateRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);

        if (!_rules.TryGetValue(ruleName, out var existing))
        {
            return null;
        }

        var updated = existing with
        {
            Enabled = request.Enabled,
            Severity = NormalizeSeverity(request.Severity, existing.Severity),
            Threshold = request.Threshold > 0 ? request.Threshold : null,
            SecondaryThreshold = request.SecondaryThreshold > 0 ? request.SecondaryThreshold : null,
            SecondarySeverity = NormalizeSecondarySeverity(request.SecondarySeverity, existing.SecondarySeverity),
            ServerFilter = NormalizeFilter(request.ServerFilter),
            ShareFilter = NormalizeFilter(request.ShareFilter),
            PathFilter = NormalizeFilter(request.PathFilter),
            ActiveFromHour = NormalizeHour(request.ActiveFromHour),
            ActiveToHour = NormalizeHour(request.ActiveToHour),
            ActiveDays = NormalizeActiveDays(request.ActiveDays),
            ExcludedUsers = NormalizeCsvList(request.ExcludedUsers),
            ExcludedHosts = NormalizeCsvList(request.ExcludedHosts),
            ExcludedProcesses = NormalizeCsvList(request.ExcludedProcesses),
            TimeZoneId = NormalizeFilter(request.TimeZoneId),
            UpdatedUtc = DateTimeOffset.UtcNow
        };

        ValidateRule(updated);
        _rules[updated.Rule] = updated;

#if SQLSERVER
        if (_persistRules)
        {
            await UpsertSqlAsync(updated, cancellationToken);
        }
#endif

        return updated;
    }

    public async Task<FileServerMonitor.Core.AlertOptions> GetCoreOptionsAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);

        var massDelete = _rules["mass-delete"];
        var massRename = _rules["mass-rename"];
        var ransomware = _rules["possible-ransomware"];
        var permissionChange = _rules["permission-change"];

        return new FileServerMonitor.Core.AlertOptions(
            WindowMinutes: _defaults.WindowMinutes,
            DedupMinutes: _defaults.DedupMinutes,
            MassDeleteThreshold: massDelete.Threshold ?? _defaults.MassDeleteThreshold,
            MassRenameThreshold: massRename.Threshold ?? _defaults.MassRenameThreshold,
            RansomwareActivityThreshold: ransomware.Threshold ?? _defaults.RansomwareActivityThreshold,
            SuspiciousExtensionThreshold: ransomware.SecondaryThreshold ?? _defaults.SuspiciousExtensionThreshold,
            MassDeleteEnabled: massDelete.Enabled,
            MassDeleteSeverity: massDelete.Severity,
            MassRenameEnabled: massRename.Enabled,
            MassRenameSeverity: massRename.Severity,
            RansomwareEnabled: ransomware.Enabled,
            RansomwareSeverity: ransomware.Severity,
            RansomwareCriticalSeverity: ransomware.SecondarySeverity ?? "critical",
            PermissionChangeEnabled: permissionChange.Enabled,
            PermissionChangeSeverity: permissionChange.Severity);
    }

    private IReadOnlyCollection<AlertRuleConfig> BuildDefaultRules()
    {
        return DefaultRules
            .Select(rule => rule.Rule switch
            {
                "mass-delete" => rule with
                {
                    Threshold = _defaults.MassDeleteThreshold
                },
                "mass-rename" => rule with
                {
                    Threshold = _defaults.MassRenameThreshold
                },
                "possible-ransomware" => rule with
                {
                    Threshold = _defaults.RansomwareActivityThreshold,
                    SecondaryThreshold = _defaults.SuspiciousExtensionThreshold
                },
                _ => rule
            })
            .ToArray();
    }

    private static void ValidateRule(AlertRuleConfig rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Severity))
        {
            throw new BadHttpRequestException("Severidade obrigatoria.");
        }

        if ((rule.Rule.Equals("mass-delete", StringComparison.OrdinalIgnoreCase)
                || rule.Rule.Equals("mass-rename", StringComparison.OrdinalIgnoreCase)
                || rule.Rule.Equals("possible-ransomware", StringComparison.OrdinalIgnoreCase))
            && (!rule.Threshold.HasValue || rule.Threshold.Value < 1))
        {
            throw new BadHttpRequestException("Threshold invalido para a regra.");
        }

        if (rule.Rule.Equals("possible-ransomware", StringComparison.OrdinalIgnoreCase)
            && (!rule.SecondaryThreshold.HasValue || rule.SecondaryThreshold.Value < 1))
        {
            throw new BadHttpRequestException("SecondaryThreshold invalido para a regra de ransomware.");
        }

        var hasFrom = rule.ActiveFromHour.HasValue;
        var hasTo = rule.ActiveToHour.HasValue;

        if (hasFrom != hasTo)
        {
            throw new BadHttpRequestException("ActiveFromHour e ActiveToHour devem ser informados juntos.");
        }

        if (hasFrom && !string.IsNullOrWhiteSpace(rule.TimeZoneId))
        {
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(rule.TimeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
                throw new BadHttpRequestException("TimeZoneId invalido.");
            }
            catch (InvalidTimeZoneException)
            {
                throw new BadHttpRequestException("TimeZoneId invalido.");
            }
        }

        _ = ParseActiveDays(rule.ActiveDays);
    }

    private static string NormalizeSeverity(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
    }

    private static int? NormalizeHour(int? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        if (value.Value is < 0 or > 23)
        {
            throw new BadHttpRequestException("Hora deve estar entre 0 e 23.");
        }

        return value.Value;
    }

    private static string? NormalizeActiveDays(string? value)
    {
        var days = ParseActiveDays(value);
        return days.Count == 0 ? null : string.Join(',', days);
    }

    private static IReadOnlyCollection<string> ParseActiveDays(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sun"] = "sun",
            ["dom"] = "sun",
            ["mon"] = "mon",
            ["seg"] = "mon",
            ["tue"] = "tue",
            ["ter"] = "tue",
            ["wed"] = "wed",
            ["qua"] = "wed",
            ["thu"] = "thu",
            ["qui"] = "thu",
            ["fri"] = "fri",
            ["sex"] = "fri",
            ["sat"] = "sat",
            ["sab"] = "sat"
        };

        var normalized = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item =>
            {
                if (!map.TryGetValue(item, out var canonical))
                {
                    throw new BadHttpRequestException("ActiveDays invalido. Use seg,ter,qua,qui,sex,sab,dom ou mon,tue,wed,thu,fri,sat,sun.");
                }

                return canonical;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized;
    }

    private static string? NormalizeFilter(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? NormalizeCsvList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var items = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return items.Length == 0 ? null : string.Join(',', items);
    }

    private static string? NormalizeSecondarySeverity(string? value, string? fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
#if SQLSERVER
        if (!_persistRules || _loadedFromSql)
        {
            return;
        }

        await using var connection = CreateSqlConnection();
        await connection.OpenAsync(cancellationToken);

        await using (var seedCommand = connection.CreateCommand())
        {
            foreach (var rule in _rules.Values)
            {
                seedCommand.Parameters.Clear();
                seedCommand.CommandText = """
                    IF NOT EXISTS (SELECT 1 FROM dbo.AlertRules WHERE RuleName = @RuleName)
                    BEGIN
                        INSERT INTO dbo.AlertRules
                        (
                            RuleName,
                            Title,
                            Description,
                            IsEnabled,
                            Severity,
                            ThresholdValue,
                            SecondaryThresholdValue,
                            SecondarySeverity,
                            ServerFilter,
                            ShareFilter,
                            PathFilter,
                            ActiveFromHour,
                            ActiveToHour,
                            ActiveDays,
                            ExcludedUsers,
                            ExcludedHosts,
                            ExcludedProcesses,
                            TimeZoneId,
                            UpdatedUtc
                        )
                        VALUES
                        (
                            @RuleName,
                            @Title,
                            @Description,
                            @IsEnabled,
                            @Severity,
                            @ThresholdValue,
                            @SecondaryThresholdValue,
                            @SecondarySeverity,
                            @ServerFilter,
                            @ShareFilter,
                            @PathFilter,
                            @ActiveFromHour,
                            @ActiveToHour,
                            @ActiveDays,
                            @ExcludedUsers,
                            @ExcludedHosts,
                            @ExcludedProcesses,
                            @TimeZoneId,
                            @UpdatedUtc
                        )
                    END;
                    """;
                AddRuleParameters(seedCommand, rule);
                await seedCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                RuleName,
                Title,
                Description,
                IsEnabled,
                Severity,
                ThresholdValue,
                SecondaryThresholdValue,
                SecondarySeverity,
                ServerFilter,
                ShareFilter,
                PathFilter,
                ActiveFromHour,
                ActiveToHour,
                ActiveDays,
                ExcludedUsers,
                ExcludedHosts,
                ExcludedProcesses,
                TimeZoneId,
                UpdatedUtc
            FROM dbo.AlertRules;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var rule = ReadRule(reader);
            _rules[rule.Rule] = rule;
        }

        _loadedFromSql = true;
#else
        await Task.CompletedTask;
#endif
    }

#if SQLSERVER
    private async Task UpsertSqlAsync(AlertRuleConfig rule, CancellationToken cancellationToken)
    {
        await using var connection = CreateSqlConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            MERGE dbo.AlertRules AS target
            USING (SELECT @RuleName AS RuleName) AS source
            ON target.RuleName = source.RuleName
            WHEN MATCHED THEN
                UPDATE SET
                    Title = @Title,
                    Description = @Description,
                    IsEnabled = @IsEnabled,
                    Severity = @Severity,
                    ThresholdValue = @ThresholdValue,
                    SecondaryThresholdValue = @SecondaryThresholdValue,
                    SecondarySeverity = @SecondarySeverity,
                    ServerFilter = @ServerFilter,
                    ShareFilter = @ShareFilter,
                    PathFilter = @PathFilter,
                    ActiveFromHour = @ActiveFromHour,
                    ActiveToHour = @ActiveToHour,
                    ActiveDays = @ActiveDays,
                    ExcludedUsers = @ExcludedUsers,
                    ExcludedHosts = @ExcludedHosts,
                    ExcludedProcesses = @ExcludedProcesses,
                    TimeZoneId = @TimeZoneId,
                    UpdatedUtc = @UpdatedUtc
            WHEN NOT MATCHED THEN
                INSERT
                (
                    RuleName,
                    Title,
                    Description,
                    IsEnabled,
                    Severity,
                    ThresholdValue,
                    SecondaryThresholdValue,
                    SecondarySeverity,
                    ServerFilter,
                    ShareFilter,
                    PathFilter,
                    ActiveFromHour,
                    ActiveToHour,
                    ActiveDays,
                    ExcludedUsers,
                    ExcludedHosts,
                    ExcludedProcesses,
                    TimeZoneId,
                    UpdatedUtc
                )
                VALUES
                (
                    @RuleName,
                    @Title,
                    @Description,
                    @IsEnabled,
                    @Severity,
                    @ThresholdValue,
                    @SecondaryThresholdValue,
                    @SecondarySeverity,
                    @ServerFilter,
                    @ShareFilter,
                    @PathFilter,
                    @ActiveFromHour,
                    @ActiveToHour,
                    @ActiveDays,
                    @ExcludedUsers,
                    @ExcludedHosts,
                    @ExcludedProcesses,
                    @TimeZoneId,
                    @UpdatedUtc
                );
            """;
        AddRuleParameters(command, rule);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqlConnection CreateSqlConnection()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:SqlServer nao foi configurada.");
        }

        return new SqlConnection(_connectionString);
    }

    private static void AddRuleParameters(SqlCommand command, AlertRuleConfig rule)
    {
        command.Parameters.AddWithValue("@RuleName", rule.Rule);
        command.Parameters.AddWithValue("@Title", rule.Title);
        command.Parameters.AddWithValue("@Description", rule.Description);
        command.Parameters.AddWithValue("@IsEnabled", rule.Enabled);
        command.Parameters.AddWithValue("@Severity", rule.Severity);
        command.Parameters.AddWithValue("@ThresholdValue", DbValue(rule.Threshold));
        command.Parameters.AddWithValue("@SecondaryThresholdValue", DbValue(rule.SecondaryThreshold));
        command.Parameters.AddWithValue("@SecondarySeverity", DbValue(rule.SecondarySeverity));
        command.Parameters.AddWithValue("@ServerFilter", DbValue(rule.ServerFilter));
        command.Parameters.AddWithValue("@ShareFilter", DbValue(rule.ShareFilter));
        command.Parameters.AddWithValue("@PathFilter", DbValue(rule.PathFilter));
        command.Parameters.AddWithValue("@ActiveFromHour", DbValue(rule.ActiveFromHour));
        command.Parameters.AddWithValue("@ActiveToHour", DbValue(rule.ActiveToHour));
        command.Parameters.AddWithValue("@ActiveDays", DbValue(rule.ActiveDays));
        command.Parameters.AddWithValue("@ExcludedUsers", DbValue(rule.ExcludedUsers));
        command.Parameters.AddWithValue("@ExcludedHosts", DbValue(rule.ExcludedHosts));
        command.Parameters.AddWithValue("@ExcludedProcesses", DbValue(rule.ExcludedProcesses));
        command.Parameters.AddWithValue("@TimeZoneId", DbValue(rule.TimeZoneId));
        command.Parameters.AddWithValue("@UpdatedUtc", rule.UpdatedUtc.UtcDateTime);
    }

    private static AlertRuleConfig ReadRule(SqlDataReader reader)
    {
        return new AlertRuleConfig(
            Rule: reader.GetString(reader.GetOrdinal("RuleName")),
            Title: reader.GetString(reader.GetOrdinal("Title")),
            Description: reader.GetString(reader.GetOrdinal("Description")),
            Enabled: reader.GetBoolean(reader.GetOrdinal("IsEnabled")),
            Severity: reader.GetString(reader.GetOrdinal("Severity")),
            Threshold: ReadNullableInt(reader, "ThresholdValue"),
            SecondaryThreshold: ReadNullableInt(reader, "SecondaryThresholdValue"),
            SecondarySeverity: ReadNullableString(reader, "SecondarySeverity"),
            ServerFilter: ReadNullableString(reader, "ServerFilter"),
            ShareFilter: ReadNullableString(reader, "ShareFilter"),
            PathFilter: ReadNullableString(reader, "PathFilter"),
            ActiveFromHour: ReadNullableInt(reader, "ActiveFromHour"),
            ActiveToHour: ReadNullableInt(reader, "ActiveToHour"),
            ActiveDays: ReadNullableString(reader, "ActiveDays"),
            ExcludedUsers: ReadNullableString(reader, "ExcludedUsers"),
            ExcludedHosts: ReadNullableString(reader, "ExcludedHosts"),
            ExcludedProcesses: ReadNullableString(reader, "ExcludedProcesses"),
            TimeZoneId: ReadNullableString(reader, "TimeZoneId"),
            UpdatedUtc: ReadUtcDateTimeOffset(reader, "UpdatedUtc"));
    }

    private static int? ReadNullableInt(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static string? ReadNullableString(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset ReadUtcDateTimeOffset(SqlDataReader reader, string name)
    {
        var value = reader.GetDateTime(reader.GetOrdinal(name));
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private static object DbValue<T>(T? value)
    {
        return value is null ? DBNull.Value : value;
    }
#endif
}

internal sealed class AlertStore
{
    private readonly ConcurrentDictionary<Guid, FileServerAlert> _alerts = new();
    private readonly ConcurrentQueue<FileAuditEvent> _recentEvents = new();
    private readonly AlertOptions _options;
    private readonly AlertRuleStore _ruleStore;
#if SQLSERVER
    private readonly bool _persistAlerts;
    private readonly string? _connectionString;
#endif
    private readonly AlertNotificationService _notificationService;

    public AlertStore(IConfiguration configuration, AlertNotificationService notificationService, AlertRuleStore ruleStore)
    {
        _notificationService = notificationService;
        _ruleStore = ruleStore;
        _options = new AlertOptions(
            WindowMinutes: configuration.GetValue("Alerts:WindowMinutes", 5),
            DedupMinutes: configuration.GetValue("Alerts:DedupMinutes", 10),
            MassDeleteThreshold: configuration.GetValue("Alerts:MassDeleteThreshold", 50),
            MassRenameThreshold: configuration.GetValue("Alerts:MassRenameThreshold", 100),
            RansomwareActivityThreshold: configuration.GetValue("Alerts:RansomwareActivityThreshold", 250),
            SuspiciousExtensionThreshold: configuration.GetValue("Alerts:SuspiciousExtensionThreshold", 10));

#if SQLSERVER
        _persistAlerts = configuration.GetValue("Monitor:StorageProvider", "SqlServer")
            .Equals("SqlServer", StringComparison.OrdinalIgnoreCase);
        _connectionString = configuration.GetConnectionString("SqlServer");
#endif
    }

    public async Task<IReadOnlyCollection<FileServerAlert>> AnalyzeAsync(
        IReadOnlyCollection<FileAuditEvent> newEvents,
        CancellationToken cancellationToken)
    {
        foreach (var auditEvent in newEvents)
        {
            _recentEvents.Enqueue(auditEvent);
        }

        TrimRecentEvents();

        var generated = new List<FileServerAlert>();
        var rules = await _ruleStore.ListAsync(cancellationToken);

        foreach (var group in newEvents.GroupBy(item => new AlertScope(item.Server, item.User)))
        {
            var scopeEvents = GetScopeEvents(group.Key);

            foreach (var rule in rules.Where(item => item.Enabled))
            {
                var matchingEvents = scopeEvents
                    .Where(item => RuleApplies(rule, item))
                    .ToArray();

                if (matchingEvents.Length == 0)
                {
                    continue;
                }

                var ruleEngine = new FileServerMonitor.Core.AlertRuleEngine(BuildCoreOptionsForRule(rule));
                generated.AddRange(ruleEngine
                    .Evaluate(matchingEvents.Select(ToCoreEvent).ToArray())
                    .Where(alert => alert.Rule.Equals(rule.Rule, StringComparison.OrdinalIgnoreCase))
                    .Select(FromCoreAlert));
            }
        }

        var added = new List<FileServerAlert>();

        foreach (var alert in generated)
        {
            var key = BuildDedupKey(alert);
            var alreadyExists = await HasRecentDuplicateAsync(key, cancellationToken);

            if (alreadyExists)
            {
                continue;
            }

            var alertToAdd = alert with { DedupKey = key };

            if (_alerts.TryAdd(alertToAdd.Id, alertToAdd))
            {
#if SQLSERVER
                if (_persistAlerts)
                {
                    await InsertAlertAsync(alertToAdd, cancellationToken);
                }
#endif

                added.Add(alertToAdd);
            }
        }

        var addedAlerts = added
            .OrderByDescending(alert => alert.CreatedUtc)
            .ToArray();

        await _notificationService.NotifyAsync(addedAlerts, cancellationToken);

        return addedAlerts;
    }

    public async Task<IReadOnlyCollection<FileServerAlert>> QueryAsync(
        AlertQuery query,
        CancellationToken cancellationToken)
    {
#if SQLSERVER
        if (_persistAlerts)
        {
            return await QuerySqlAsync(query, cancellationToken);
        }
#endif

        return QueryMemory(query);
    }

    public async Task<AlertRuleSimulationResponse?> SimulateAsync(
        string ruleName,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<FileAuditEvent> events,
        CancellationToken cancellationToken)
    {
        var rule = (await _ruleStore.ListAsync(cancellationToken))
            .FirstOrDefault(item => item.Rule.Equals(ruleName, StringComparison.OrdinalIgnoreCase));

        if (rule is null)
        {
            return null;
        }

        var orderedEvents = events
            .OrderBy(item => item.TimestampUtc)
            .ToArray();
        var matchingEvents = orderedEvents
            .Where(item => RuleApplies(rule, item))
            .ToArray();
        var simulatedAlerts = new List<FileServerAlert>();
        var seenAlerts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in matchingEvents.GroupBy(item => new AlertScope(item.Server, item.User)))
        {
            var scopeEvents = group
                .OrderBy(item => item.TimestampUtc)
                .ToArray();

            for (var index = 0; index < scopeEvents.Length; index++)
            {
                var windowEnd = scopeEvents[index].TimestampUtc;
                var windowStart = windowEnd.AddMinutes(-_options.WindowMinutes);
                var windowEvents = scopeEvents
                    .Where(item => item.TimestampUtc >= windowStart && item.TimestampUtc <= windowEnd)
                    .ToArray();

                var ruleEngine = new FileServerMonitor.Core.AlertRuleEngine(BuildCoreOptionsForRule(rule));
                var alerts = ruleEngine
                    .Evaluate(windowEvents.Select(ToCoreEvent).ToArray())
                    .Where(item => item.Rule.Equals(rule.Rule, StringComparison.OrdinalIgnoreCase))
                    .Select(FromCoreAlert);

                foreach (var alert in alerts)
                {
                    var key = BuildSimulationAlertKey(alert);

                    if (seenAlerts.Add(key))
                    {
                        simulatedAlerts.Add(alert with { DedupKey = BuildDedupKey(alert) });
                    }
                }
            }
        }

        var resultAlerts = simulatedAlerts
            .OrderByDescending(item => item.CreatedUtc)
            .ToArray();

        return new AlertRuleSimulationResponse(
            Rule: rule.Rule,
            Title: rule.Title,
            FromUtc: fromUtc,
            ToUtc: toUtc,
            EvaluatedEvents: orderedEvents.Length,
            MatchingEvents: matchingEvents.Length,
            AlertCount: resultAlerts.Length,
            Alerts: resultAlerts);
    }

    public async Task<FileServerAlert?> AcknowledgeAsync(Guid id, CancellationToken cancellationToken)
    {
#if SQLSERVER
        if (_persistAlerts)
        {
            return await AcknowledgeSqlAsync(id, cancellationToken);
        }
#endif

        return AcknowledgeMemory(id);
    }

    public async Task<int> PurgeOlderThanAsync(
        DateTimeOffset cutoffUtc,
        int batchSize,
        CancellationToken cancellationToken)
    {
#if SQLSERVER
        if (_persistAlerts)
        {
            return await PurgeSqlAsync(cutoffUtc, batchSize, cancellationToken);
        }
#endif

        return PurgeMemory(cutoffUtc);
    }

    private IReadOnlyCollection<FileServerAlert> QueryMemory(AlertQuery query)
    {
        IEnumerable<FileServerAlert> alerts = _alerts.Values;

        if (!string.IsNullOrWhiteSpace(query.Severity))
        {
            alerts = alerts.Where(item => item.Severity.Equals(query.Severity, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            alerts = alerts.Where(item => item.Status.Equals(query.Status, StringComparison.OrdinalIgnoreCase));
        }

        return alerts
            .OrderByDescending(item => item.CreatedUtc)
            .Take(query.Take)
            .ToArray();
    }

    private FileServerAlert? AcknowledgeMemory(Guid id)
    {
        if (!_alerts.TryGetValue(id, out var alert))
        {
            return null;
        }

        var updated = alert with
        {
            Status = "acknowledged",
            AcknowledgedUtc = DateTimeOffset.UtcNow
        };

        _alerts[id] = updated;

        return updated;
    }

    private int PurgeMemory(DateTimeOffset cutoffUtc)
    {
        var deleted = 0;

        foreach (var alert in _alerts.Values.Where(item => item.CreatedUtc < cutoffUtc).ToArray())
        {
            if (_alerts.TryRemove(alert.Id, out _))
            {
                deleted++;
            }
        }

        return deleted;
    }

    private async Task<bool> HasRecentDuplicateAsync(string dedupKey, CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(_options.DedupMinutes));
        var memoryDuplicate = _alerts.Values.Any(existing =>
            existing.DedupKey.Equals(dedupKey, StringComparison.OrdinalIgnoreCase)
            && existing.CreatedUtc >= cutoff);

        if (memoryDuplicate)
        {
            return true;
        }

#if SQLSERVER
        if (!_persistAlerts)
        {
            return false;
        }

        await using var connection = CreateSqlConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) 1
            FROM dbo.FileServerAlerts
            WHERE DedupKey = @DedupKey
              AND CreatedUtc >= @CreatedUtc;
            """;
        command.Parameters.AddWithValue("@DedupKey", dedupKey);
        command.Parameters.AddWithValue("@CreatedUtc", cutoff.UtcDateTime);

        var result = await command.ExecuteScalarAsync(cancellationToken);

        return result is not null;
#else
        return false;
#endif
    }

#if SQLSERVER
    private async Task InsertAlertAsync(FileServerAlert alert, CancellationToken cancellationToken)
    {
        await using var connection = CreateSqlConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.FileServerAlerts
            (
                Id,
                RuleName,
                Severity,
                StatusName,
                Title,
                Description,
                ServerName,
                UserName,
                EventCount,
                FirstEventUtc,
                LastEventUtc,
                CreatedUtc,
                AcknowledgedUtc,
                SamplePathsJson,
                DedupKey
            )
            VALUES
            (
                @Id,
                @RuleName,
                @Severity,
                @StatusName,
                @Title,
                @Description,
                @ServerName,
                @UserName,
                @EventCount,
                @FirstEventUtc,
                @LastEventUtc,
                @CreatedUtc,
                @AcknowledgedUtc,
                @SamplePathsJson,
                @DedupKey
            );
            """;

        AddAlertParameters(command, alert);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyCollection<FileServerAlert>> QuerySqlAsync(
        AlertQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateSqlConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("@Take", query.Take);

        var predicates = new List<string>();

        if (!string.IsNullOrWhiteSpace(query.Severity))
        {
            predicates.Add("Severity = @Severity");
            command.Parameters.AddWithValue("@Severity", query.Severity);
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            predicates.Add("StatusName = @StatusName");
            command.Parameters.AddWithValue("@StatusName", query.Status);
        }

        var where = predicates.Count == 0
            ? string.Empty
            : $"WHERE {string.Join(" AND ", predicates)}";

        command.CommandText = $$"""
            SELECT TOP (@Take)
                Id,
                RuleName,
                Severity,
                StatusName,
                Title,
                Description,
                ServerName,
                UserName,
                EventCount,
                FirstEventUtc,
                LastEventUtc,
                CreatedUtc,
                AcknowledgedUtc,
                SamplePathsJson,
                DedupKey
            FROM dbo.FileServerAlerts
            {{where}}
            ORDER BY CreatedUtc DESC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var alerts = new List<FileServerAlert>();

        while (await reader.ReadAsync(cancellationToken))
        {
            alerts.Add(ReadAlert(reader));
        }

        return alerts;
    }

    private async Task<FileServerAlert?> AcknowledgeSqlAsync(Guid id, CancellationToken cancellationToken)
    {
        var acknowledgedUtc = DateTimeOffset.UtcNow;

        await using var connection = CreateSqlConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.FileServerAlerts
            SET StatusName = @StatusName,
                AcknowledgedUtc = @AcknowledgedUtc
            OUTPUT
                inserted.Id,
                inserted.RuleName,
                inserted.Severity,
                inserted.StatusName,
                inserted.Title,
                inserted.Description,
                inserted.ServerName,
                inserted.UserName,
                inserted.EventCount,
                inserted.FirstEventUtc,
                inserted.LastEventUtc,
                inserted.CreatedUtc,
                inserted.AcknowledgedUtc,
                inserted.SamplePathsJson,
                inserted.DedupKey
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@StatusName", "acknowledged");
        command.Parameters.AddWithValue("@AcknowledgedUtc", acknowledgedUtc.UtcDateTime);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var alert = ReadAlert(reader);
        _alerts[id] = alert;

        return alert;
    }

    private async Task<int> PurgeSqlAsync(
        DateTimeOffset cutoffUtc,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var totalDeleted = 0;
        var safeBatchSize = Math.Clamp(batchSize, 100, 100_000);

        await using var connection = CreateSqlConnection();
        await connection.OpenAsync(cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE TOP (@BatchSize)
                FROM dbo.FileServerAlerts
                WHERE CreatedUtc < @CutoffUtc;

                SELECT @@ROWCOUNT;
                """;
            command.Parameters.AddWithValue("@BatchSize", safeBatchSize);
            command.Parameters.AddWithValue("@CutoffUtc", cutoffUtc.UtcDateTime);

            var deleted = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            totalDeleted += deleted;

            if (deleted < safeBatchSize)
            {
                break;
            }
        }

        return totalDeleted;
    }

    private SqlConnection CreateSqlConnection()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:SqlServer nao foi configurada.");
        }

        return new SqlConnection(_connectionString);
    }

    private static void AddAlertParameters(SqlCommand command, FileServerAlert alert)
    {
        command.Parameters.AddWithValue("@Id", alert.Id);
        command.Parameters.AddWithValue("@RuleName", alert.Rule);
        command.Parameters.AddWithValue("@Severity", alert.Severity);
        command.Parameters.AddWithValue("@StatusName", alert.Status);
        command.Parameters.AddWithValue("@Title", alert.Title);
        command.Parameters.AddWithValue("@Description", alert.Description);
        command.Parameters.AddWithValue("@ServerName", alert.Server);
        command.Parameters.AddWithValue("@UserName", alert.User);
        command.Parameters.AddWithValue("@EventCount", alert.EventCount);
        command.Parameters.AddWithValue("@FirstEventUtc", alert.FirstEventUtc.UtcDateTime);
        command.Parameters.AddWithValue("@LastEventUtc", alert.LastEventUtc.UtcDateTime);
        command.Parameters.AddWithValue("@CreatedUtc", alert.CreatedUtc.UtcDateTime);
        command.Parameters.AddWithValue("@AcknowledgedUtc", DbValue(alert.AcknowledgedUtc?.UtcDateTime));
        command.Parameters.AddWithValue("@SamplePathsJson", JsonSerializer.Serialize(alert.SamplePaths));
        command.Parameters.AddWithValue("@DedupKey", alert.DedupKey);
    }

    private static FileServerAlert ReadAlert(SqlDataReader reader)
    {
        var samplePathsJson = ReadNullableString(reader, "SamplePathsJson");
        var samplePaths = string.IsNullOrWhiteSpace(samplePathsJson)
            ? Array.Empty<string>()
            : JsonSerializer.Deserialize<string[]>(samplePathsJson) ?? Array.Empty<string>();

        return new FileServerAlert(
            Id: reader.GetGuid(reader.GetOrdinal("Id")),
            Rule: reader.GetString(reader.GetOrdinal("RuleName")),
            Severity: reader.GetString(reader.GetOrdinal("Severity")),
            Status: reader.GetString(reader.GetOrdinal("StatusName")),
            Title: reader.GetString(reader.GetOrdinal("Title")),
            Description: reader.GetString(reader.GetOrdinal("Description")),
            Server: reader.GetString(reader.GetOrdinal("ServerName")),
            User: reader.GetString(reader.GetOrdinal("UserName")),
            EventCount: reader.GetInt32(reader.GetOrdinal("EventCount")),
            FirstEventUtc: ReadUtcDateTimeOffset(reader, "FirstEventUtc"),
            LastEventUtc: ReadUtcDateTimeOffset(reader, "LastEventUtc"),
            CreatedUtc: ReadUtcDateTimeOffset(reader, "CreatedUtc"),
            AcknowledgedUtc: ReadNullableUtcDateTimeOffset(reader, "AcknowledgedUtc"),
            SamplePaths: samplePaths,
            DedupKey: reader.GetString(reader.GetOrdinal("DedupKey")));
    }

    private static DateTimeOffset ReadUtcDateTimeOffset(SqlDataReader reader, string name)
    {
        var value = reader.GetDateTime(reader.GetOrdinal(name));
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private static DateTimeOffset? ReadNullableUtcDateTimeOffset(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);

        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
    }

    private static string? ReadNullableString(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static object DbValue<T>(T? value)
    {
        return value is null ? DBNull.Value : value;
    }
#endif

    private IReadOnlyCollection<FileAuditEvent> GetScopeEvents(AlertScope scope)
    {
        var windowStart = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(_options.WindowMinutes));

        return _recentEvents
            .Where(item => item.TimestampUtc >= windowStart)
            .Where(item => item.Server.Equals(scope.Server, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.User.Equals(scope.User, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static FileServerMonitor.Core.FileAuditEvent ToCoreEvent(FileAuditEvent auditEvent)
    {
        return new FileServerMonitor.Core.FileAuditEvent(
            Id: auditEvent.Id,
            TimestampUtc: auditEvent.TimestampUtc,
            Server: auditEvent.Server,
            Share: auditEvent.Share,
            Path: auditEvent.Path,
            PreviousPath: auditEvent.PreviousPath,
            ObjectType: auditEvent.ObjectType,
            Action: auditEvent.Action,
            User: auditEvent.User,
            Sid: auditEvent.Sid,
            SourceHost: auditEvent.SourceHost,
            SourceIp: auditEvent.SourceIp,
            ProcessName: auditEvent.ProcessName,
            FileSizeBytes: auditEvent.FileSizeBytes,
            Extension: auditEvent.Extension,
            Result: auditEvent.Result,
            Severity: auditEvent.Severity,
            Source: auditEvent.Source);
    }

    private static FileServerAlert FromCoreAlert(FileServerMonitor.Core.FileServerAlert alert)
    {
        return new FileServerAlert(
            Id: alert.Id,
            Rule: alert.Rule,
            Severity: alert.Severity,
            Status: alert.Status,
            Title: alert.Title,
            Description: alert.Description,
            Server: alert.Server,
            User: alert.User,
            EventCount: alert.EventCount,
            FirstEventUtc: alert.FirstEventUtc,
            LastEventUtc: alert.LastEventUtc,
            CreatedUtc: alert.CreatedUtc,
            AcknowledgedUtc: alert.AcknowledgedUtc,
            SamplePaths: alert.SamplePaths,
            DedupKey: alert.DedupKey);
    }

    private void TrimRecentEvents()
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(_options.WindowMinutes * 2));

        while (_recentEvents.TryPeek(out var auditEvent) && auditEvent.TimestampUtc < cutoff)
        {
            _recentEvents.TryDequeue(out _);
        }
    }

    private static string BuildDedupKey(FileServerAlert alert)
    {
        return $"{alert.Rule}:{alert.Server}:{alert.User}";
    }

    private static string BuildSimulationAlertKey(FileServerAlert alert)
    {
        return $"{alert.Rule}:{alert.Server}:{alert.User}:{alert.FirstEventUtc:O}:{alert.LastEventUtc:O}:{alert.EventCount}";
    }

    private FileServerMonitor.Core.AlertOptions BuildCoreOptionsForRule(AlertRuleConfig rule)
    {
        return rule.Rule switch
        {
            "mass-delete" => new FileServerMonitor.Core.AlertOptions(
                WindowMinutes: _options.WindowMinutes,
                DedupMinutes: _options.DedupMinutes,
                MassDeleteThreshold: rule.Threshold ?? _options.MassDeleteThreshold,
                MassRenameEnabled: false,
                RansomwareEnabled: false,
                PermissionChangeEnabled: false,
                MassDeleteSeverity: rule.Severity),
            "mass-rename" => new FileServerMonitor.Core.AlertOptions(
                WindowMinutes: _options.WindowMinutes,
                DedupMinutes: _options.DedupMinutes,
                MassDeleteEnabled: false,
                MassRenameThreshold: rule.Threshold ?? _options.MassRenameThreshold,
                RansomwareEnabled: false,
                PermissionChangeEnabled: false,
                MassRenameSeverity: rule.Severity),
            "possible-ransomware" => new FileServerMonitor.Core.AlertOptions(
                WindowMinutes: _options.WindowMinutes,
                DedupMinutes: _options.DedupMinutes,
                MassDeleteEnabled: false,
                MassRenameEnabled: false,
                RansomwareActivityThreshold: rule.Threshold ?? _options.RansomwareActivityThreshold,
                SuspiciousExtensionThreshold: rule.SecondaryThreshold ?? _options.SuspiciousExtensionThreshold,
                PermissionChangeEnabled: false,
                RansomwareSeverity: rule.Severity,
                RansomwareCriticalSeverity: rule.SecondarySeverity ?? "critical"),
            "permission-change" => new FileServerMonitor.Core.AlertOptions(
                WindowMinutes: _options.WindowMinutes,
                DedupMinutes: _options.DedupMinutes,
                MassDeleteEnabled: false,
                MassRenameEnabled: false,
                RansomwareEnabled: false,
                PermissionChangeSeverity: rule.Severity),
            _ => new FileServerMonitor.Core.AlertOptions()
        };
    }

    private static bool RuleApplies(AlertRuleConfig rule, FileAuditEvent auditEvent)
    {
        if (MatchesExcludedValue(rule.ExcludedUsers, auditEvent.User))
        {
            return false;
        }

        if (MatchesExcludedValue(rule.ExcludedHosts, auditEvent.SourceHost))
        {
            return false;
        }

        if (MatchesExcludedValue(rule.ExcludedProcesses, auditEvent.ProcessName))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.ServerFilter)
            && !auditEvent.Server.Equals(rule.ServerFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.ShareFilter)
            && !auditEvent.Share.Equals(rule.ShareFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.PathFilter)
            && !auditEvent.Path.StartsWith(rule.PathFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (rule.ActiveFromHour.HasValue && rule.ActiveToHour.HasValue)
        {
            var timestamp = auditEvent.TimestampUtc;

            if (!string.IsNullOrWhiteSpace(rule.TimeZoneId))
            {
                try
                {
                    timestamp = TimeZoneInfo.ConvertTime(timestamp, TimeZoneInfo.FindSystemTimeZoneById(rule.TimeZoneId));
                }
                catch (TimeZoneNotFoundException)
                {
                    return false;
                }
                catch (InvalidTimeZoneException)
                {
                    return false;
                }
            }

            var hour = timestamp.Hour;
            var start = rule.ActiveFromHour.Value;
            var end = rule.ActiveToHour.Value;
            var isInsideWindow = start == end
                || start < end
                    ? hour >= start && hour < end
                    : hour >= start || hour < end;

            if (!isInsideWindow)
            {
                return false;
            }
        }

        var activeDays = ParseRuleActiveDays(rule.ActiveDays);

        if (activeDays.Count > 0)
        {
            var timestamp = auditEvent.TimestampUtc;

            if (!string.IsNullOrWhiteSpace(rule.TimeZoneId))
            {
                try
                {
                    timestamp = TimeZoneInfo.ConvertTime(timestamp, TimeZoneInfo.FindSystemTimeZoneById(rule.TimeZoneId));
                }
                catch (TimeZoneNotFoundException)
                {
                    return false;
                }
                catch (InvalidTimeZoneException)
                {
                    return false;
                }
            }

            var day = timestamp.DayOfWeek switch
            {
                DayOfWeek.Sunday => "sun",
                DayOfWeek.Monday => "mon",
                DayOfWeek.Tuesday => "tue",
                DayOfWeek.Wednesday => "wed",
                DayOfWeek.Thursday => "thu",
                DayOfWeek.Friday => "fri",
                DayOfWeek.Saturday => "sat",
                _ => string.Empty
            };

            if (!activeDays.Contains(day, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyCollection<string> ParseRuleActiveDays(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool MatchesExcludedValue(string? csv, string? value)
    {
        if (string.IsNullOrWhiteSpace(csv) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return csv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(item => item.Equals(value, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class AlertNotificationService
{
    private static readonly IReadOnlyDictionary<string, int> SeverityRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["info"] = 0,
        ["low"] = 1,
        ["medium"] = 2,
        ["warning"] = 3,
        ["high"] = 4,
        ["critical"] = 5
    };

    private readonly NotificationOptions _options;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public AlertNotificationService(IConfiguration configuration)
    {
        _options = new NotificationOptions(
            Enabled: configuration.GetValue("Notifications:Enabled", false),
            WebhookUrl: configuration.GetValue<string>("Notifications:WebhookUrl"),
            Format: configuration.GetValue("Notifications:Format", "generic"),
            MinimumSeverity: configuration.GetValue("Notifications:MinimumSeverity", "critical"));
    }

    public async Task NotifyAsync(
        IReadOnlyCollection<FileServerAlert> alerts,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.WebhookUrl))
        {
            return;
        }

        foreach (var alert in alerts.Where(ShouldNotify))
        {
            try
            {
                var payload = _options.Format.Equals("teams", StringComparison.OrdinalIgnoreCase)
                    ? BuildTeamsPayload(alert)
                    : BuildGenericPayload(alert);

                using var response = await _httpClient.PostAsJsonAsync(_options.WebhookUrl, payload, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    Console.Error.WriteLine($"Falha ao notificar alerta {alert.Id}: {(int)response.StatusCode}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Console.Error.WriteLine($"Falha ao notificar alerta {alert.Id}: {ex.Message}");
            }
        }
    }

    private bool ShouldNotify(FileServerAlert alert)
    {
        return GetSeverityRank(alert.Severity) >= GetSeverityRank(_options.MinimumSeverity);
    }

    private static object BuildGenericPayload(FileServerAlert alert)
    {
        return new
        {
            type = "fileserver-monitor-alert",
            id = alert.Id,
            rule = alert.Rule,
            severity = alert.Severity,
            status = alert.Status,
            title = alert.Title,
            description = alert.Description,
            server = alert.Server,
            user = alert.User,
            eventCount = alert.EventCount,
            firstEventUtc = alert.FirstEventUtc,
            lastEventUtc = alert.LastEventUtc,
            createdUtc = alert.CreatedUtc,
            samplePaths = alert.SamplePaths
        };
    }

    private static object BuildTeamsPayload(FileServerAlert alert)
    {
        return new Dictionary<string, object?>
        {
            ["@type"] = "MessageCard",
            ["@context"] = "https://schema.org/extensions",
            ["summary"] = alert.Title,
            ["themeColor"] = alert.Severity.Equals("critical", StringComparison.OrdinalIgnoreCase) ? "B91C1C" : "B45309",
            ["title"] = alert.Title,
            ["text"] = alert.Description,
            ["sections"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["facts"] = new[]
                    {
                        new { name = "Severidade", value = alert.Severity },
                        new { name = "Regra", value = alert.Rule },
                        new { name = "Servidor", value = alert.Server },
                        new { name = "Usuario", value = alert.User },
                        new { name = "Eventos", value = alert.EventCount.ToString() },
                        new { name = "Criado em UTC", value = alert.CreatedUtc.ToString("O") }
                    }
                }
            }
        };
    }

    private static int GetSeverityRank(string severity)
    {
        return SeverityRank.TryGetValue(severity, out var rank) ? rank : 0;
    }
}

internal sealed class RetentionWorker : BackgroundService
{
    private readonly IEventRepository _events;
    private readonly AlertStore _alerts;
    private readonly RetentionOptions _options;
    private readonly ILogger<RetentionWorker> _logger;

    public RetentionWorker(
        IEventRepository events,
        AlertStore alerts,
        IConfiguration configuration,
        ILogger<RetentionWorker> logger)
    {
        _events = events;
        _alerts = alerts;
        _logger = logger;
        _options = new RetentionOptions(
            Enabled: configuration.GetValue("Retention:Enabled", false),
            EventsDays: configuration.GetValue("Retention:EventsDays", 180),
            AlertsDays: configuration.GetValue("Retention:AlertsDays", 365),
            IntervalHours: configuration.GetValue("Retention:IntervalHours", 24),
            PurgeBatchSize: configuration.GetValue("Retention:PurgeBatchSize", 10_000));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Retencao automatica desativada.");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, _options.IntervalHours));

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var eventCutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, _options.EventsDays));
        var alertCutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, _options.AlertsDays));
        var batchSize = Math.Clamp(_options.PurgeBatchSize, 100, 100_000);

        try
        {
            var deletedEvents = await _events.PurgeOlderThanAsync(eventCutoff, batchSize, cancellationToken);
            var deletedAlerts = await _alerts.PurgeOlderThanAsync(alertCutoff, batchSize, cancellationToken);

            if (deletedEvents > 0 || deletedAlerts > 0)
            {
                _logger.LogInformation(
                    "Retencao executada. Eventos removidos: {DeletedEvents}. Alertas removidos: {DeletedAlerts}.",
                    deletedEvents,
                    deletedAlerts);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao executar retencao automatica.");
        }
    }
}

internal sealed record MonitorOptions
{
    public const string SectionName = "Monitor";

    public string StorageProvider { get; init; } = "SqlServer";

    public int InMemoryMaxEvents { get; init; } = 10_000;
}

internal sealed record AuthOptions(bool Enabled, string? ApiKey, string? AdminApiKey)
{
    public static AuthOptions FromConfiguration(IConfiguration configuration)
    {
        return new AuthOptions(
            Enabled: configuration.GetValue("Auth:Enabled", false),
            ApiKey: configuration.GetValue<string>("Auth:ApiKey"),
            AdminApiKey: configuration.GetValue<string>("Auth:AdminApiKey"));
    }

    public bool MatchesAnyKey(string? providedKey)
    {
        return Matches(ApiKey, providedKey)
            || Matches(GetEffectiveAdminApiKey(), providedKey);
    }

    public bool MatchesAdminKey(string? providedKey)
    {
        return Matches(GetEffectiveAdminApiKey(), providedKey);
    }

    private string? GetEffectiveAdminApiKey()
    {
        return string.IsNullOrWhiteSpace(AdminApiKey) ? ApiKey : AdminApiKey;
    }

    private static bool Matches(string? expectedKey, string? providedKey)
    {
        return !string.IsNullOrWhiteSpace(expectedKey)
            && expectedKey.Equals(providedKey, StringComparison.Ordinal);
    }
}

internal static class AuthHelpers
{
    public static bool IsAnonymousPath(PathString path)
    {
        return path == "/"
            || path.StartsWithSegments("/health");
    }

    public static bool RequiresAdminKey(HttpRequest request)
    {
        if (request.Path.StartsWithSegments("/api/admin-audit"))
        {
            return true;
        }

        if (request.Path.StartsWithSegments("/api/alerts")
            && HttpMethods.IsPost(request.Method))
        {
            return true;
        }

        if (request.Path.StartsWithSegments("/api/alert-rules")
            && !HttpMethods.IsGet(request.Method))
        {
            return true;
        }

        if (request.Path.StartsWithSegments("/api/monitored-paths")
            && !HttpMethods.IsGet(request.Method))
        {
            return true;
        }

        return false;
    }

    public static string? GetProvidedApiKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-Api-Key", out var apiKey))
        {
            return apiKey.FirstOrDefault();
        }

        var authorization = request.Headers.Authorization.FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(authorization)
            && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authorization["Bearer ".Length..].Trim();
        }

        return null;
    }
}

internal sealed record AlertOptions(
    int WindowMinutes = 5,
    int DedupMinutes = 10,
    int MassDeleteThreshold = 50,
    int MassRenameThreshold = 100,
    int RansomwareActivityThreshold = 250,
    int SuspiciousExtensionThreshold = 10)
{
    public const string SectionName = "Alerts";
}

internal sealed record NotificationOptions(
    bool Enabled,
    string? WebhookUrl,
    string Format,
    string MinimumSeverity);

internal sealed record RetentionOptions(
    bool Enabled,
    int EventsDays,
    int AlertsDays,
    int IntervalHours,
    int PurgeBatchSize);

internal sealed record FileAuditEvent(
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

internal sealed record FileAuditEventRequest(
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
    string? Source)
{
    public FileAuditEvent ToAuditEvent()
    {
        FileServerMonitor.Core.FileAuditEvent normalized;

        try
        {
            normalized = FileServerMonitor.Core.FileAuditEventNormalizer.Normalize(
                new FileServerMonitor.Core.FileAuditEventInput(
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
                    Source));
        }
        catch (ArgumentException ex)
        {
            throw new BadHttpRequestException(ex.Message);
        }

        return new FileAuditEvent(
            Id: normalized.Id,
            TimestampUtc: normalized.TimestampUtc,
            Server: normalized.Server,
            Share: normalized.Share,
            Path: normalized.Path,
            PreviousPath: normalized.PreviousPath,
            ObjectType: normalized.ObjectType,
            Action: normalized.Action,
            User: normalized.User,
            Sid: normalized.Sid,
            SourceHost: normalized.SourceHost,
            SourceIp: normalized.SourceIp,
            ProcessName: normalized.ProcessName,
            FileSizeBytes: normalized.FileSizeBytes,
            Extension: normalized.Extension,
            Result: normalized.Result,
            Severity: normalized.Severity,
            Source: normalized.Source);
    }
}

internal sealed record EventQuery(
    string? Server,
    string? User,
    string? Action,
    string? Path,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    int Take);

internal sealed record EventStoreStats(long StoredEvents, DateTimeOffset? LastEventUtc);

internal static class EventCsvExporter
{
    private static readonly string[] Header =
    {
        "id",
        "timestampUtc",
        "server",
        "share",
        "path",
        "previousPath",
        "objectType",
        "action",
        "user",
        "sid",
        "sourceHost",
        "sourceIp",
        "processName",
        "fileSizeBytes",
        "extension",
        "result",
        "severity",
        "source"
    };

    public static string Export(IReadOnlyCollection<FileAuditEvent> events)
    {
        var builder = new StringBuilder();
        CsvWriter.AppendRow(builder, Header);

        foreach (var auditEvent in events)
        {
            CsvWriter.AppendRow(
                builder,
                auditEvent.Id,
                auditEvent.TimestampUtc,
                auditEvent.Server,
                auditEvent.Share,
                auditEvent.Path,
                auditEvent.PreviousPath,
                auditEvent.ObjectType,
                auditEvent.Action,
                auditEvent.User,
                auditEvent.Sid,
                auditEvent.SourceHost,
                auditEvent.SourceIp,
                auditEvent.ProcessName,
                auditEvent.FileSizeBytes,
                auditEvent.Extension,
                auditEvent.Result,
                auditEvent.Severity,
                auditEvent.Source);
        }

        return builder.ToString();
    }
}

internal static class AlertCsvExporter
{
    public static string Export(IReadOnlyCollection<FileServerAlert> alerts)
    {
        var builder = new StringBuilder();
        CsvWriter.AppendRow(builder,
            "id",
            "rule",
            "severity",
            "status",
            "title",
            "description",
            "server",
            "user",
            "eventCount",
            "firstEventUtc",
            "lastEventUtc",
            "createdUtc",
            "acknowledgedUtc",
            "samplePaths");

        foreach (var alert in alerts)
        {
            CsvWriter.AppendRow(builder,
                alert.Id,
                alert.Rule,
                alert.Severity,
                alert.Status,
                alert.Title,
                alert.Description,
                alert.Server,
                alert.User,
                alert.EventCount,
                alert.FirstEventUtc,
                alert.LastEventUtc,
                alert.CreatedUtc,
                alert.AcknowledgedUtc,
                string.Join(" | ", alert.SamplePaths));
        }

        return builder.ToString();
    }
}

internal static class BaselineAnomalyCsvExporter
{
    public static string Export(BaselineAnomalyResponse response)
    {
        var builder = new StringBuilder();
        CsvWriter.AppendRow(builder,
            "fromUtc",
            "toUtc",
            "baselineWindows",
            "dimension",
            "name",
            "currentCount",
            "baselineAverage",
            "deltaPercent");

        AppendItems(builder, response, "action", response.ByAction);
        AppendItems(builder, response, "share", response.ByShare);
        AppendItems(builder, response, "user", response.ByUser);

        return builder.ToString();
    }

    private static void AppendItems(
        StringBuilder builder,
        BaselineAnomalyResponse response,
        string dimension,
        IReadOnlyCollection<BaselineAnomalyItem> items)
    {
        foreach (var item in items)
        {
            CsvWriter.AppendRow(builder,
                response.FromUtc,
                response.ToUtc,
                response.BaselineWindows,
                dimension,
                item.Name,
                item.CurrentCount,
                item.BaselineAverage,
                item.DeltaPercent);
        }
    }
}

internal static class CsvWriter
{
    public static void AppendRow(StringBuilder builder, params object?[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(Escape(values[index]));
        }

        builder.AppendLine();
    }

    private static string Escape(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            DateTimeOffset timestamp => timestamp.ToString("O"),
            _ => value.ToString() ?? string.Empty
        };

        if (!text.Contains('"') && !text.Contains(',') && !text.Contains('\n') && !text.Contains('\r'))
        {
            return text;
        }

        return $"\"{text.Replace("\"", "\"\"")}\"";
    }
}

internal static class BaselineAnomalyCalculator
{
    public static IReadOnlyCollection<BaselineAnomalyItem> Build(
        IReadOnlyDictionary<string, long> current,
        IReadOnlyDictionary<string, long> baselineTotals,
        int baselineWindows,
        int take)
    {
        var names = current.Keys
            .Concat(baselineTotals.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return names
            .Select(name =>
            {
                var currentCount = current.GetValueOrDefault(name, 0);
                var baselineAverage = baselineTotals.GetValueOrDefault(name, 0) / (double)Math.Max(1, baselineWindows);
                var deltaPercent = baselineAverage <= 0
                    ? (currentCount > 0 ? 100d : 0d)
                    : ((currentCount - baselineAverage) / baselineAverage) * 100d;

                return new BaselineAnomalyItem(name, currentCount, Math.Round(baselineAverage, 2), Math.Round(deltaPercent, 2));
            })
            .Where(item => item.CurrentCount > 0)
            .OrderByDescending(item => item.DeltaPercent)
            .ThenByDescending(item => item.CurrentCount)
            .Take(take)
            .ToArray();
    }
}

internal sealed record ActivitySummaryQuery(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string? Server,
    string? Share,
    string? User,
    string? Action,
    int Take);

internal sealed record BaselineAnomalyQuery(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string? Server,
    string? Share,
    string? User,
    string? Action,
    int BaselineWindows,
    int Take);

internal sealed record ActivitySummaryResponse(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    long TotalEvents,
    IReadOnlyCollection<ActivitySummaryItem> ByAction,
    IReadOnlyCollection<ActivitySummaryItem> ByShare,
    IReadOnlyCollection<ActivitySummaryItem> ByUser);

internal sealed record ActivitySummaryItem(string Name, long EventCount);

internal sealed record BaselineAnomalyResponse(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int BaselineWindows,
    IReadOnlyCollection<BaselineAnomalyItem> ByAction,
    IReadOnlyCollection<BaselineAnomalyItem> ByShare,
    IReadOnlyCollection<BaselineAnomalyItem> ByUser);

internal sealed record BaselineAnomalyItem(
    string Name,
    long CurrentCount,
    double BaselineAverage,
    double DeltaPercent);

internal sealed record AlertScope(string Server, string User);

internal sealed record AlertQuery(string? Severity, string? Status, int Take);

internal sealed record AlertRuleConfig(
    string Rule,
    string Title,
    string Description,
    bool Enabled,
    string Severity,
    int? Threshold,
    int? SecondaryThreshold,
    string? SecondarySeverity,
    string? ServerFilter,
    string? ShareFilter,
    string? PathFilter,
    int? ActiveFromHour,
    int? ActiveToHour,
    string? ActiveDays,
    string? ExcludedUsers,
    string? ExcludedHosts,
    string? ExcludedProcesses,
    string? TimeZoneId,
    DateTimeOffset UpdatedUtc);

internal sealed record AlertRuleUpdateRequest(
    bool Enabled,
    string? Severity,
    int? Threshold,
    int? SecondaryThreshold,
    string? SecondarySeverity,
    string? ServerFilter,
    string? ShareFilter,
    string? PathFilter,
    int? ActiveFromHour,
    int? ActiveToHour,
    string? ActiveDays,
    string? ExcludedUsers,
    string? ExcludedHosts,
    string? ExcludedProcesses,
    string? TimeZoneId);

internal sealed record AlertRuleSimulationRequest(
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    int? Take,
    string? Server,
    string? User,
    string? Action,
    string? Path);

internal sealed record AlertRuleSimulationResponse(
    string Rule,
    string Title,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int EvaluatedEvents,
    int MatchingEvents,
    int AlertCount,
    IReadOnlyCollection<FileServerAlert> Alerts);

internal sealed record FileServerAlert(
    Guid Id,
    string Rule,
    string Severity,
    string Status,
    string Title,
    string Description,
    string Server,
    string User,
    int EventCount,
    DateTimeOffset FirstEventUtc,
    DateTimeOffset LastEventUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? AcknowledgedUtc,
    string[] SamplePaths,
    string DedupKey);

internal sealed record HealthResponse(
    string Service,
    string Status,
    DateTimeOffset TimestampUtc,
    string StorageProvider,
    long StoredEvents,
    DateTimeOffset? LastEventUtc);

internal sealed record EventIngestResponse(FileAuditEvent Event, IReadOnlyCollection<FileServerAlert> Alerts);

internal sealed record BatchIngestResponse(
    int AcceptedEvents,
    Guid[] EventIds,
    IReadOnlyCollection<FileServerAlert> Alerts);

internal sealed record AgentHeartbeatRequest(
    string AgentId,
    string Server,
    string Status,
    string Version,
    long LastRecordId,
    IReadOnlyDictionary<string, long>? LastUsnByVolume,
    string? Message,
    int PendingQueueEvents = 0,
    DateTimeOffset? LastSuccessfulSendUtc = null);

internal sealed record AgentHealthResponse(
    string AgentId,
    string Server,
    string Status,
    DateTimeOffset? LastHeartbeatUtc,
    string? Version,
    long LastRecordId,
    IReadOnlyDictionary<string, long> LastUsnByVolume,
    string? Message,
    int PendingQueueEvents = 0,
    DateTimeOffset? LastSuccessfulSendUtc = null,
    int BacklogWarningThreshold = 1000,
    bool IsStale = false,
    int StaleAfterMinutes = 10);

internal sealed record AgentConfigResponse(
    string Server,
    DateTimeOffset GeneratedUtc,
    string DefaultShare,
    string[] UsnVolumes,
    IReadOnlyCollection<MonitoredPath> MonitoredPaths);

internal sealed record AdminAuditEntry(
    Guid Id,
    DateTimeOffset TimestampUtc,
    string Action,
    string EntityType,
    string EntityId,
    string Actor,
    string? SourceIp,
    string? DetailsJson)
{
    public static AdminAuditEntry Create(
        string Action,
        string EntityType,
        string EntityId,
        string Actor,
        string? SourceIp,
        object? Details)
    {
        return new AdminAuditEntry(
            Id: Guid.NewGuid(),
            TimestampUtc: DateTimeOffset.UtcNow,
            Action: Action,
            EntityType: EntityType,
            EntityId: EntityId,
            Actor: Actor,
            SourceIp: SourceIp,
            DetailsJson: Details is null ? null : JsonSerializer.Serialize(Details));
    }
}

internal sealed record AdminAuditQuery(
    string? Action,
    string? EntityType,
    int Take);

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

internal sealed record MonitoredPathRequest(
    string? Server,
    string? Share,
    string? Path,
    string? Status,
    string? Priority,
    string? Owner,
    string? Notes);

internal sealed record ErrorResponse(string Message);
