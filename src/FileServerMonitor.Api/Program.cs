using System.Collections.Concurrent;
using System.Diagnostics;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileServerMonitor.Core;
#if SQLSERVER
using Microsoft.Data.SqlClient;
#endif

var builder = WebApplication.CreateBuilder(args);
var apiStartedUtc = DateTimeOffset.UtcNow;
const string TimelineCorrelationVersion = "core-v1";
const int TimelineRebuildPaddingSeconds = 30;

builder.Services.Configure<MonitorOptions>(
    builder.Configuration.GetSection(MonitorOptions.SectionName));
builder.Services.AddSingleton<AgentHealthStore>();
builder.Services.AddSingleton<AlertNotificationService>();
builder.Services.AddSingleton<AlertRuleStore>();
builder.Services.AddSingleton<AlertStore>();
builder.Services.AddSingleton<MonitoredPathStore>();
builder.Services.AddSingleton<AdminAuditStore>();
builder.Services.AddSingleton<LdapAuthSettingsStore>();
builder.Services.AddSingleton<RetentionSettingsStore>();
builder.Services.AddSingleton<InventoryScanSettingsStore>();
builder.Services.AddSingleton<LdapAuthenticator>();
builder.Services.AddSingleton<TimelineMaterializationCoordinator>();
builder.Services.AddSingleton<TimelineMaterializer>();
builder.Services.AddHostedService<RetentionWorker>();
builder.Services.AddHostedService<TimelineMaterializationWorker>();
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
    builder.Services.AddSingleton<ITimelineRepository, SqlServerTimelineRepository>();
    builder.Services.AddSingleton<IInventoryRepository, SqlServerInventoryRepository>();
#else
    Console.Error.WriteLine("SQL Server desativado neste build. Usando armazenamento em memoria.");
    builder.Services.AddSingleton<IEventRepository, InMemoryEventRepository>();
    builder.Services.AddSingleton<ITimelineRepository, InMemoryTimelineRepository>();
    builder.Services.AddSingleton<IInventoryRepository, InMemoryInventoryRepository>();
#endif
}
else
{
    builder.Services.AddSingleton<IEventRepository, InMemoryEventRepository>();
    builder.Services.AddSingleton<ITimelineRepository, InMemoryTimelineRepository>();
    builder.Services.AddSingleton<IInventoryRepository, InMemoryInventoryRepository>();
}

var app = builder.Build();

app.UseCors("WebApp");
app.Use(async (context, next) =>
{
    var authOptions = AuthOptions.FromConfiguration(context.RequestServices.GetRequiredService<IConfiguration>());
    var ldapSettings = await context.RequestServices.GetRequiredService<LdapAuthSettingsStore>().GetAsync(context.RequestAborted);
    var authRequired = authOptions.Enabled || ldapSettings.Enabled;

    if (!authRequired || AuthHelpers.IsAnonymousPath(context.Request.Path))
    {
        await next(context);
        return;
    }

    var providedKey = AuthHelpers.GetProvidedApiKey(context.Request);
    var requiredRole = AuthHelpers.GetRequiredRole(context.Request);
    var session = AuthSessionToken.TryValidate(
        AuthHelpers.GetBearerToken(context.Request),
        authOptions.GetSigningSecret());

    if (!authOptions.MatchesAnyKey(providedKey) && session is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ErrorResponse("Nao autorizado."));
        return;
    }

    if (session is not null)
    {
        context.User = session.ToPrincipal();
    }

    if (requiredRole is not null
        && !authOptions.MatchesAdminKey(providedKey)
        && !AuthHelpers.HasRequiredRole(session?.Role, requiredRole.Value))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new ErrorResponse("Permissao insuficiente para esta operacao."));
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

app.MapGet("/metrics", async (
    IEventRepository repository,
    IInventoryRepository inventory,
    AgentHealthStore agents,
    RetentionSettingsStore retentionStore,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var now = DateTimeOffset.UtcNow;
    var database = await BuildDatabaseMetricsAsync(repository, now, cancellationToken);
    var capacity = await BuildDatabaseCapacityMetricsAsync(configuration, repository, now, cancellationToken);
    var inventoryMetrics = await BuildInventoryMetricsAsync(inventory, now, cancellationToken);
    var agentSummary = await BuildAgentMetricsAsync(agents, now, cancellationToken);
    var thresholds = new MetricsThresholds(
        AgentStaleMinutes: configuration.GetValue("Agents:StaleMinutes", 10),
        AgentBacklogWarningThreshold: configuration.GetValue("Agents:BacklogWarningThreshold", 1000),
        LastEventWarningSeconds: configuration.GetValue("Metrics:LastEventWarningSeconds", 1800));
    var retention = RetentionMetrics.FromSettings(await retentionStore.GetAsync(cancellationToken));
    var api = new ApiMetrics(
        Status: "healthy",
        StorageProvider: repository.ProviderName,
        UptimeSeconds: Math.Max(0, (long)now.Subtract(apiStartedUtc).TotalSeconds),
        StartedUtc: apiStartedUtc,
        MachineName: Environment.MachineName,
        ProcessId: Environment.ProcessId);
    var status = ResolveMetricsStatus(database, agentSummary, inventoryMetrics);

    return Results.Ok(new MetricsResponse(
        Service: "FileServerMonitor.Api",
        Status: status,
        TimestampUtc: now,
        Api: api,
        Database: database,
        Capacity: capacity,
        Inventory: inventoryMetrics,
        Agents: agentSummary,
        Retention: retention,
        Thresholds: thresholds));
});

app.MapPost("/api/events", async (
    FileAuditEventRequest request,
    IEventRepository repository,
    TimelineMaterializationCoordinator materialization,
    AlertStore alerts,
    CancellationToken cancellationToken) =>
{
    var auditEvent = request.ToAuditEvent();
    await repository.AddAsync(auditEvent, cancellationToken);
    QueueTimelineMaterialization(new[] { auditEvent }, materialization);
    var generatedAlerts = await alerts.AnalyzeAsync(new[] { auditEvent }, cancellationToken);

    return Results.Created($"/api/events/{auditEvent.Id}", new EventIngestResponse(auditEvent, generatedAlerts));
});

app.MapPost("/api/events/batch", async (
    FileAuditEventRequest[] requests,
    IEventRepository repository,
    TimelineMaterializationCoordinator materialization,
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
    QueueTimelineMaterialization(events, materialization);
    var generatedAlerts = await alerts.AnalyzeAsync(events, cancellationToken);

    return Results.Accepted(value: new BatchIngestResponse(
        events.Length,
        events.Select(item => item.Id).ToArray(),
        generatedAlerts));
});

app.MapGet("/api/events", async (
    string? server,
    string? share,
    string? user,
    string? action,
    string? path,
    string? sourceHost,
    string? sourceIp,
    string? extension,
    string? result,
    string? severity,
    string? source,
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc,
    int? take,
    IEventRepository repository,
    CancellationToken cancellationToken) =>
{
    var query = new EventQuery(
        Server: server,
        Share: share,
        User: user,
        Action: action,
        Path: path,
        SourceHost: sourceHost,
        SourceIp: sourceIp,
        Extension: extension,
        Result: result,
        Severity: severity,
        Source: source,
        FromUtc: fromUtc,
        ToUtc: toUtc,
        Take: take is > 0 and <= 5_000 ? take.Value : 100);

    var events = await repository.QueryAsync(query, cancellationToken);

    return Results.Ok(events);
});

app.MapGet("/api/events/timeline", async (
    string? server,
    string? share,
    string? user,
    string? action,
    string? path,
    string? sourceHost,
    string? sourceIp,
    string? extension,
    string? result,
    string? severity,
    string? source,
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc,
    int? take,
    IEventRepository repository,
    ITimelineRepository timelineRepository,
    CancellationToken cancellationToken) =>
{
    var timelineQuery = new TimelineQuery(
        Server: server,
        Share: share,
        User: user,
        Action: action,
        Path: path,
        SourceHost: sourceHost,
        SourceIp: sourceIp,
        Extension: extension,
        Result: result,
        Severity: severity,
        Source: source,
        FromUtc: fromUtc,
        ToUtc: toUtc,
        Take: take is > 0 and <= 20_000 ? take.Value : 100);
    var persistedTimeline = await QueryPersistedTimelineIfCoveredAsync(timelineQuery, timelineRepository, cancellationToken);
    if (persistedTimeline is not null)
    {
        return Results.Ok(persistedTimeline
            .Where(item => MatchesTimelineQuery(item, timelineQuery))
            .ToArray());
    }

    var events = await QueryTimelineSourceEventsAsync(
        server,
        share,
        user,
        action,
        path,
        sourceHost,
        sourceIp,
        extension,
        result,
        severity,
        source,
        fromUtc,
        toUtc,
        timelineQuery.Take,
        repository,
        cancellationToken);
    var timeline = TimelineMaterializer.ProjectTimeline(events, user, action)
        .Where(item => MatchesTimelineQuery(item, timelineQuery))
        .ToArray();

    return Results.Ok(timeline);
});

app.MapGet("/api/events/timeline/export.csv", async (
    string? server,
    string? share,
    string? user,
    string? action,
    string? path,
    string? sourceHost,
    string? sourceIp,
    string? extension,
    string? result,
    string? severity,
    string? source,
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc,
    int? take,
    IEventRepository repository,
    ITimelineRepository timelineRepository,
    CancellationToken cancellationToken) =>
{
    var timelineQuery = new TimelineQuery(
        Server: server,
        Share: share,
        User: user,
        Action: action,
        Path: path,
        SourceHost: sourceHost,
        SourceIp: sourceIp,
        Extension: extension,
        Result: result,
        Severity: severity,
        Source: source,
        FromUtc: fromUtc,
        ToUtc: toUtc,
        Take: take is > 0 and <= 20_000 ? take.Value : 10_000);
    var persistedTimeline = await QueryPersistedTimelineIfCoveredAsync(timelineQuery, timelineRepository, cancellationToken);
    if (persistedTimeline is not null)
    {
        var persistedCsv = TimelineCsvExporter.Export(persistedTimeline
            .Where(item => MatchesTimelineQuery(item, timelineQuery))
            .ToArray());
        return Results.Text(persistedCsv, "text/csv; charset=utf-8");
    }

    var events = await QueryTimelineSourceEventsAsync(
        server,
        share,
        user,
        action,
        path,
        sourceHost,
        sourceIp,
        extension,
        result,
        severity,
        source,
        fromUtc,
        toUtc,
        timelineQuery.Take,
        repository,
        cancellationToken);
    var timeline = TimelineMaterializer.ProjectTimeline(events, user, action)
        .Where(item => MatchesTimelineQuery(item, timelineQuery))
        .ToArray();
    var csv = TimelineCsvExporter.Export(timeline);

    return Results.Text(csv, "text/csv; charset=utf-8");
});

app.MapGet("/api/events/timeline/page", async (
    string? server,
    string? share,
    string? user,
    string? action,
    string? path,
    string? search,
    string? sourceHost,
    string? sourceIp,
    string? extension,
    string? result,
    string? severity,
    string? source,
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc,
    int? page,
    int? pageSize,
    int? windowTake,
    IEventRepository repository,
    ITimelineRepository timelineRepository,
    CancellationToken cancellationToken) =>
{
    var safePage = Math.Max(1, page ?? 1);
    var safePageSize = pageSize is > 0 and <= 100 ? pageSize.Value : 25;
    var minimumWindow = safePage * safePageSize * 4;
    var safeWindowTake = Math.Clamp(Math.Max(windowTake ?? 1_000, minimumWindow), 100, 20_000);
    var pageQuery = new TimelineQuery(
        Server: server,
        Share: share,
        User: user,
        Action: action,
        Path: path,
        SourceHost: sourceHost,
        SourceIp: sourceIp,
        Extension: extension,
        Result: result,
        Severity: severity,
        Source: source,
        FromUtc: fromUtc,
        ToUtc: toUtc,
        Take: safeWindowTake);
    var persistedTimeline = await QueryPersistedTimelineIfCoveredAsync(pageQuery, timelineRepository, cancellationToken);
    var windowEvents = persistedTimeline?.Count ?? 0;
    var filteredTimeline = (persistedTimeline ?? Array.Empty<FileAuditDisplayEvent>())
        .Where(item => MatchesTimelineQuery(item, pageQuery))
        .Where(item => MatchesTimelineSearch(item, search))
        .ToArray();
    if (persistedTimeline is null)
    {
        var events = await QueryTimelineSourceEventsAsync(
            server,
            share,
            user,
            action,
            path,
            sourceHost,
            sourceIp,
            extension,
            result,
            severity,
            source,
            fromUtc,
            toUtc,
            safeWindowTake,
            repository,
            cancellationToken);
        windowEvents = events.Count;
        filteredTimeline = TimelineMaterializer.ProjectTimeline(events, user, action)
            .Where(item => MatchesTimelineQuery(item, pageQuery))
            .Where(item => MatchesTimelineSearch(item, search))
            .ToArray();
    }
    var totalItems = filteredTimeline.Length;
    var totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)safePageSize));
    var clampedPage = Math.Min(safePage, totalPages);
    var items = filteredTimeline
        .Skip((clampedPage - 1) * safePageSize)
        .Take(safePageSize)
        .ToArray();

    return Results.Ok(new TimelinePageResponse(
        Items: items,
        Page: clampedPage,
        PageSize: safePageSize,
        TotalItems: totalItems,
        TotalPages: totalPages,
        WindowRawEvents: windowEvents));
});

app.MapGet("/api/events/export.csv", async (
    string? server,
    string? share,
    string? user,
    string? action,
    string? path,
    string? sourceHost,
    string? sourceIp,
    string? extension,
    string? result,
    string? severity,
    string? source,
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc,
    int? take,
    IEventRepository repository,
    CancellationToken cancellationToken) =>
{
    var query = new EventQuery(
        Server: server,
        Share: share,
        User: user,
        Action: action,
        Path: path,
        SourceHost: sourceHost,
        SourceIp: sourceIp,
        Extension: extension,
        Result: result,
        Severity: severity,
        Source: source,
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

app.MapPost("/api/events/timeline/rebuild", async (
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc,
    int? take,
    TimelineMaterializer materializer,
    CancellationToken cancellationToken) =>
{
    var safeTake = take is > 0 and <= 50_000 ? take.Value : 20_000;
    var rebuild = await materializer.RebuildAsync(fromUtc, toUtc, safeTake, cancellationToken);

    return Results.Ok(new TimelineRebuildResponse(
        RawEvents: rebuild.RawEvents,
        TimelineEvents: rebuild.TimelineEvents,
        FromUtc: rebuild.FromUtc,
        ToUtc: rebuild.ToUtc,
        CorrelationVersion: TimelineCorrelationVersion));
});

app.MapPost("/api/inventory/snapshots/start", async (
    InventorySnapshotStartRequest request,
    IInventoryRepository inventory,
    CancellationToken cancellationToken) =>
{
    var snapshot = await inventory.StartSnapshotAsync(request.ToSnapshot(), cancellationToken);
    return Results.Created($"/api/inventory/snapshots/{snapshot.Id}", snapshot);
});

app.MapPost("/api/inventory/snapshots/{id:guid}/items", async (
    Guid id,
    InventoryItemBatchRequest request,
    IInventoryRepository inventory,
    CancellationToken cancellationToken) =>
{
    if (request.Items.Length == 0)
    {
        return Results.BadRequest(new ErrorResponse("A lista de itens nao pode estar vazia."));
    }

    if (request.Items.Length > 2_000)
    {
        return Results.BadRequest(new ErrorResponse("Envie no maximo 2000 itens por lote."));
    }

    var items = request.Items
        .Select(item => item.ToInventoryItem(id))
        .ToArray();
    await inventory.AddBatchAsync(id, items, cancellationToken);

    return Results.Accepted(value: new InventoryBatchIngestResponse(SnapshotId: id, AcceptedItems: items.Length));
});

app.MapPost("/api/inventory/snapshots/{id:guid}/complete", async (
    Guid id,
    InventorySnapshotCompleteRequest request,
    IInventoryRepository inventory,
    CancellationToken cancellationToken) =>
{
    var snapshot = await inventory.CompleteSnapshotAsync(
        id,
        request.Status ?? "completed",
        request.Error,
        cancellationToken);

    return snapshot is null
        ? Results.NotFound(new ErrorResponse("Snapshot de inventario nao encontrado."))
        : Results.Ok(snapshot);
});

app.MapGet("/api/inventory/summary", async (
    string? server,
    string? share,
    string? rootPath,
    int? top,
    IInventoryRepository inventory,
    ITimelineRepository timeline,
    CancellationToken cancellationToken) =>
{
    var safeTop = top is > 0 and <= 50 ? top.Value : 10;
    var summary = await inventory.GetLatestSummaryAsync(server, share, rootPath, safeTop, cancellationToken);
    var now = DateTimeOffset.UtcNow;
    var observedActivity = summary.SnapshotId is null
        ? FileInventoryAnalyzer.BuildObservedActivitySummary(Array.Empty<FileInventoryObservedActivityInput>(), safeTop)
        : await timeline.GetObservedActivitySummaryAsync(
            server: summary.Server ?? server,
            share: summary.Share ?? share,
            rootPath: summary.RootPath ?? rootPath,
            fromUtc: now.AddDays(-30),
            toUtc: now,
            top: safeTop,
            cancellationToken);

    var enrichedSummary = summary with { ObservedActivity = observedActivity };
    var insight = FileInventoryAnalyzer.BuildManagerialInsight(enrichedSummary);
    var executiveOverview = FileInventoryAnalyzer.BuildExecutiveOverview(enrichedSummary with { Insight = insight });

    return Results.Ok(enrichedSummary with
    {
        Insight = insight,
        ExecutiveOverview = executiveOverview
    });
});

app.MapGet("/api/inventory/items", async (
    string? server,
    string? share,
    string? rootPath,
    string? kind,
    string? path,
    string? extension,
    int? take,
    IInventoryRepository inventory,
    CancellationToken cancellationToken) =>
{
    var safeTake = Math.Clamp(take ?? 100, 1, 500);
    var items = await inventory.QueryLatestItemsAsync(
        server,
        share,
        rootPath,
        kind,
        path,
        extension,
        safeTake,
        cancellationToken);

    return Results.Ok(items);
});

app.MapGet("/api/inventory/snapshots", async (
    string? server,
    string? share,
    int? take,
    IInventoryRepository inventory,
    CancellationToken cancellationToken) =>
{
    var snapshots = await inventory.GetSnapshotsAsync(server, share, take is > 0 and <= 200 ? take.Value : 20, cancellationToken);
    return Results.Ok(snapshots);
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
        Share: null,
        User: request.User,
        Action: request.Action,
        Path: request.Path,
        SourceHost: null,
        SourceIp: null,
        Extension: null,
        Result: null,
        Severity: null,
        Source: null,
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
        LastSuccessfulSendUtc: request.LastSuccessfulSendUtc,
        LastCollectedEventUtc: request.LastCollectedEventUtc,
        LastCycle: request.LastCycle);

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
    InventoryScanSettingsStore inventoryScan,
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
    var inventorySettings = await inventoryScan.GetAsync(cancellationToken);

    return Results.Ok(new AgentConfigResponse(
        Server: server.Trim(),
        GeneratedUtc: DateTimeOffset.UtcNow,
        DefaultShare: defaultShare,
        UsnVolumes: usnVolumes,
        MonitoredPaths: activePaths,
        InventoryScan: InventoryScanSettingsResponse.FromSettings(inventorySettings)));
});

app.MapGet("/api/database/capacity", async (
    IConfiguration configuration,
    IEventRepository repository,
    CancellationToken cancellationToken) =>
{
    var capacity = await BuildDatabaseCapacityAsync(configuration, repository, cancellationToken);
    return Results.Ok(capacity);
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
    ITimelineRepository timelineRepository,
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
    var timelineSummary = await QueryPersistedTimelineActivitySummaryIfCoveredAsync(query, timelineRepository, cancellationToken);
    if (timelineSummary is not null)
    {
        return Results.Ok(timelineSummary);
    }

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
    ITimelineRepository timelineRepository,
    ILoggerFactory loggerFactory,
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

    try
    {
        var timelineResult = await QueryPersistedTimelineBaselineAnomaliesIfCoveredAsync(query, timelineRepository, cancellationToken);
        if (timelineResult is not null)
        {
            return Results.Ok(timelineResult);
        }

        var result = await repository.GetBaselineAnomaliesAsync(query, cancellationToken);

        return Results.Ok(result);
    }
    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
    {
        loggerFactory
            .CreateLogger("BaselineAnomalies")
            .LogWarning(exception, "Falha ao calcular anomalias de baseline. Retornando lista vazia.");

        return Results.Ok(new BaselineAnomalyResponse(
            FromUtc: query.FromUtc,
            ToUtc: query.ToUtc,
            BaselineWindows: query.BaselineWindows,
            ByAction: Array.Empty<BaselineAnomalyItem>(),
            ByShare: Array.Empty<BaselineAnomalyItem>(),
            ByUser: Array.Empty<BaselineAnomalyItem>()));
    }
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
    ITimelineRepository timelineRepository,
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
    var result = await QueryPersistedTimelineBaselineAnomaliesIfCoveredAsync(query, timelineRepository, cancellationToken)
        ?? await repository.GetBaselineAnomaliesAsync(query, cancellationToken);
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

app.MapGet("/api/auth/status", async (
    LdapAuthSettingsStore store,
    CancellationToken cancellationToken) =>
{
    var settings = await store.GetAsync(cancellationToken);
    return Results.Ok(AuthStatusResponse.FromSettings(settings));
});

app.MapPost("/api/auth/login", async (
    LoginRequest request,
    LdapAuthSettingsStore store,
    LdapAuthenticator authenticator,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var settings = await store.GetAsync(cancellationToken);

    if (!settings.Enabled)
    {
        return Results.BadRequest(new ErrorResponse("Autenticacao LDAP/AD nao esta habilitada."));
    }

    var result = await authenticator.AuthenticateAsync(settings, request, cancellationToken);

    if (!result.Success || result.User is null)
    {
        return Results.Json(
            new ErrorResponse(result.Error ?? "Credenciais invalidas ou usuario sem grupo autorizado."),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var token = AuthSessionToken.Create(result.User, AuthOptions.FromConfiguration(configuration).GetSigningSecret());
    return Results.Ok(new LoginResponse(token, result.User, DateTimeOffset.UtcNow.AddHours(8)));
});

app.MapGet("/api/auth/config", async (
    LdapAuthSettingsStore store,
    CancellationToken cancellationToken) =>
{
    var settings = await store.GetAsync(cancellationToken);
    return Results.Ok(AuthConfigResponse.FromSettings(settings));
});

app.MapPut("/api/auth/config", async (
    LdapAuthSettingsRequest request,
    LdapAuthSettingsStore store,
    AdminAuditStore adminAudit,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var settings = await store.SaveAsync(request, cancellationToken);

    await adminAudit.AddAsync(AdminAuditEntry.Create(
        Action: "auth.ldap.update",
        EntityType: "auth_settings",
        EntityId: "ldap-ad",
        Actor: AdminAuditHelpers.GetActor(httpContext),
        SourceIp: AdminAuditHelpers.GetSourceIp(httpContext),
        Details: AuthConfigResponse.FromSettings(settings)), cancellationToken);

    return Results.Ok(AuthConfigResponse.FromSettings(settings));
});

app.MapGet("/api/retention/config", async (
    RetentionSettingsStore store,
    CancellationToken cancellationToken) =>
{
    var settings = await store.GetAsync(cancellationToken);
    return Results.Ok(RetentionSettingsResponse.FromSettings(settings));
});

app.MapPut("/api/retention/config", async (
    RetentionSettingsRequest request,
    RetentionSettingsStore store,
    AdminAuditStore adminAudit,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var settings = await store.SaveAsync(request, cancellationToken);

    await adminAudit.AddAsync(AdminAuditEntry.Create(
        Action: "retention.update",
        EntityType: "retention_settings",
        EntityId: "default",
        Actor: AdminAuditHelpers.GetActor(httpContext),
        SourceIp: AdminAuditHelpers.GetSourceIp(httpContext),
        Details: RetentionSettingsResponse.FromSettings(settings)), cancellationToken);

    return Results.Ok(RetentionSettingsResponse.FromSettings(settings));
});

app.MapGet("/api/inventory/config", async (
    InventoryScanSettingsStore store,
    CancellationToken cancellationToken) =>
{
    var settings = await store.GetAsync(cancellationToken);
    return Results.Ok(InventoryScanSettingsResponse.FromSettings(settings));
});

app.MapPut("/api/inventory/config", async (
    InventoryScanSettingsRequest request,
    InventoryScanSettingsStore store,
    AdminAuditStore adminAudit,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var settings = await store.SaveAsync(request, cancellationToken);

    await adminAudit.AddAsync(AdminAuditEntry.Create(
        Action: "inventory.scan.update",
        EntityType: "inventory_scan_settings",
        EntityId: "default",
        Actor: AdminAuditHelpers.GetActor(httpContext),
        SourceIp: AdminAuditHelpers.GetSourceIp(httpContext),
        Details: InventoryScanSettingsResponse.FromSettings(settings)), cancellationToken);

    return Results.Ok(InventoryScanSettingsResponse.FromSettings(settings));
});

app.MapPost("/api/inventory/scan-now", async (
    InventoryScanSettingsStore store,
    AdminAuditStore adminAudit,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var settings = await store.RequestRunNowAsync(cancellationToken);

    await adminAudit.AddAsync(AdminAuditEntry.Create(
        Action: "inventory.scan.request",
        EntityType: "inventory_scan_settings",
        EntityId: "default",
        Actor: AdminAuditHelpers.GetActor(httpContext),
        SourceIp: AdminAuditHelpers.GetSourceIp(httpContext),
        Details: InventoryScanSettingsResponse.FromSettings(settings)), cancellationToken);

    return Results.Ok(InventoryScanSettingsResponse.FromSettings(settings));
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

static async Task<IReadOnlyCollection<FileAuditEvent>> QueryTimelineSourceEventsAsync(
    string? server,
    string? share,
    string? user,
    string? action,
    string? path,
    string? sourceHost,
    string? sourceIp,
    string? extension,
    string? result,
    string? severity,
    string? source,
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc,
    int take,
    IEventRepository repository,
    CancellationToken cancellationToken)
{
    var actionFilter = BuildTimelineSourceActionFilter(action);
    var shouldFocusUserInBaseQuery = !string.IsNullOrWhiteSpace(user)
        && !string.IsNullOrWhiteSpace(actionFilter)
        && IsDirectTimelineAction(action);
    var baseTake = !shouldFocusUserInBaseQuery && !string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(actionFilter)
        ? Math.Min(take, 5_000)
        : take;
    var baseQuery = new EventQuery(
        Server: server,
        Share: share,
        User: shouldFocusUserInBaseQuery ? user : null,
        Action: actionFilter,
        Path: path,
        SourceHost: sourceHost,
        SourceIp: sourceIp,
        Extension: extension,
        Result: result,
        Severity: severity,
        Source: source,
        FromUtc: fromUtc,
        ToUtc: toUtc,
        Take: baseTake);

    var events = (await repository.QueryAsync(baseQuery, cancellationToken)).ToList();

    if (!shouldFocusUserInBaseQuery && !string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(actionFilter))
    {
        var userFocused = await repository.QueryAsync(baseQuery with { User = user, Take = take }, cancellationToken);
        var seen = events.Select(item => item.Id).ToHashSet();
        foreach (var item in userFocused)
        {
            if (seen.Add(item.Id))
            {
                events.Add(item);
            }
        }
    }

    if (!string.IsNullOrWhiteSpace(path))
    {
        var seen = events.Select(item => item.Id).ToHashSet();
        foreach (var ancestorPath in EnumerateAncestorPaths(path))
        {
            var ancestorQuery = baseQuery with
            {
                Path = ancestorPath,
                Action = "renamed,moved,deleted,created,created_or_appended",
                User = null,
                Take = Math.Min(Math.Max(take, 200), 5_000)
            };

            var ancestorEvents = await repository.QueryAsync(ancestorQuery, cancellationToken);
            foreach (var item in ancestorEvents)
            {
                if (seen.Add(item.Id))
                {
                    events.Add(item);
                }
            }
        }
    }

    return events;
}

static IReadOnlyCollection<string> EnumerateAncestorPaths(string path)
{
    var normalized = (path ?? string.Empty).Trim().Replace('/', '\\').TrimEnd('\\');
    if (string.IsNullOrWhiteSpace(normalized))
    {
        return Array.Empty<string>();
    }

    var values = new List<string>();
    var current = normalized;
    for (var depth = 0; depth < 8; depth++)
    {
        var parent = GetParentPathForQuery(current);
        if (string.IsNullOrWhiteSpace(parent))
        {
            break;
        }

        values.Add(parent);
        current = parent;
    }

    return values
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static string GetParentPathForQuery(string path)
{
    var normalized = (path ?? string.Empty).Trim().Replace('/', '\\').TrimEnd('\\');
    if (string.IsNullOrWhiteSpace(normalized))
    {
        return string.Empty;
    }

    var lastSeparator = normalized.LastIndexOf('\\');
    if (lastSeparator <= 0)
    {
        return string.Empty;
    }

    if (lastSeparator == 2 && normalized.Length >= 2 && normalized[1] == ':')
    {
        return normalized[..3];
    }

    return normalized[..lastSeparator];
}

static string? BuildTimelineSourceActionFilter(string? action)
{
    return action?.Trim().ToLowerInvariant() switch
    {
        "accessed" => "accessed",
        "created" => "created,created_or_appended,renamed,changed,modified",
        "deleted" => "deleted",
        "modified" => "modified,changed,created_or_appended",
        "moved" => "moved,renamed,deleted,accessed,created,created_or_appended,modified,changed",
        "permission_changed" => "permission_changed",
        "renamed" => "renamed,moved,deleted,accessed,created,created_or_appended,modified,changed",
        _ => null
    };
}

static bool IsDirectTimelineAction(string? action)
{
    return action?.Trim().ToLowerInvariant() is "accessed" or "created" or "deleted" or "modified" or "permission_changed";
}

static void QueueTimelineMaterialization(
    IReadOnlyCollection<FileAuditEvent> ingestedEvents,
    TimelineMaterializationCoordinator materialization)
{
    if (ingestedEvents.Count == 0)
    {
        return;
    }

    var fromUtc = ingestedEvents.Min(item => item.TimestampUtc).AddSeconds(-TimelineRebuildPaddingSeconds);
    var toUtc = ingestedEvents.Max(item => item.TimestampUtc).AddSeconds(TimelineRebuildPaddingSeconds);
    materialization.Enqueue(new TimelineMaterializationWindow(fromUtc, toUtc));
}

static async Task<IReadOnlyCollection<FileAuditDisplayEvent>?> QueryPersistedTimelineIfCoveredAsync(
    TimelineQuery query,
    ITimelineRepository timelineRepository,
    CancellationToken cancellationToken)
{
    if (!string.IsNullOrWhiteSpace(query.User)
        && query.Action?.Equals("accessed", StringComparison.OrdinalIgnoreCase) == true)
    {
        return null;
    }

    var coverage = await timelineRepository.GetCoverageAsync(cancellationToken);
    if (query.FromUtc is null && query.ToUtc is null && coverage.Count > 0)
    {
        return await timelineRepository.QueryAsync(query, cancellationToken);
    }

    if (!HasTimelineCoverage(coverage, query.FromUtc, query.ToUtc))
    {
        return null;
    }

    return await timelineRepository.QueryAsync(query, cancellationToken);
}

static async Task<ActivitySummaryResponse?> QueryPersistedTimelineActivitySummaryIfCoveredAsync(
    ActivitySummaryQuery query,
    ITimelineRepository timelineRepository,
    CancellationToken cancellationToken)
{
    return await IsTimelineCoveredAsync(query.FromUtc, query.ToUtc, timelineRepository, cancellationToken)
        ? await timelineRepository.GetActivitySummaryAsync(query, cancellationToken)
        : null;
}

static async Task<BaselineAnomalyResponse?> QueryPersistedTimelineBaselineAnomaliesIfCoveredAsync(
    BaselineAnomalyQuery query,
    ITimelineRepository timelineRepository,
    CancellationToken cancellationToken)
{
    var windowSize = query.ToUtc - query.FromUtc;
    var baselineFromUtc = query.FromUtc - TimeSpan.FromTicks(windowSize.Ticks * query.BaselineWindows);

    return await IsTimelineCoveredAsync(baselineFromUtc, query.ToUtc, timelineRepository, cancellationToken)
        ? await timelineRepository.GetBaselineAnomaliesAsync(query, cancellationToken)
        : null;
}

static async Task<bool> IsTimelineCoveredAsync(
    DateTimeOffset fromUtc,
    DateTimeOffset toUtc,
    ITimelineRepository timelineRepository,
    CancellationToken cancellationToken)
{
    var coverage = await timelineRepository.GetCoverageAsync(cancellationToken);

    return HasTimelineCoverage(coverage, fromUtc, toUtc);
}

static bool HasTimelineCoverage(
    TimelineCoverage coverage,
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc)
{
    if (coverage.Count <= 0 || coverage.FromUtc is null || coverage.ToUtc is null)
    {
        return false;
    }

    var effectiveFromUtc = fromUtc ?? coverage.FromUtc;
    if (coverage.FromUtc > effectiveFromUtc)
    {
        return false;
    }

    var effectiveToUtc = toUtc ?? DateTimeOffset.UtcNow.AddSeconds(-TimelineRebuildPaddingSeconds);
    if (effectiveToUtc < effectiveFromUtc)
    {
        return false;
    }

    return coverage.ToUtc >= effectiveToUtc;
}

static bool MatchesTimelineSearch(FileAuditDisplayEvent auditEvent, string? search)
{
    if (string.IsNullOrWhiteSpace(search))
    {
        return true;
    }

    var needle = search.Trim();
    return auditEvent.Server.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || auditEvent.Share.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || auditEvent.Path.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || (auditEvent.PreviousPath?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
        || auditEvent.User.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || (auditEvent.SourceHost?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
        || (auditEvent.SourceIp?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
        || auditEvent.Action.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || auditEvent.DisplayAction.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || auditEvent.Source.Contains(needle, StringComparison.OrdinalIgnoreCase);
}

static bool MatchesTimelineQuery(FileAuditDisplayEvent auditEvent, TimelineQuery query)
{
    if (!string.IsNullOrWhiteSpace(query.Server)
        && !auditEvent.Server.Contains(query.Server, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (!string.IsNullOrWhiteSpace(query.Share)
        && !auditEvent.Share.Contains(query.Share, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (!string.IsNullOrWhiteSpace(query.User)
        && !auditEvent.User.Contains(query.User, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var actions = ParseTimelineActionFilter(query.Action);
    if (actions.Length > 0
        && !actions.Contains(auditEvent.Action, StringComparer.OrdinalIgnoreCase))
    {
        return false;
    }

    if (!string.IsNullOrWhiteSpace(query.Path)
        && !auditEvent.Path.Contains(query.Path, StringComparison.OrdinalIgnoreCase)
        && !(auditEvent.PreviousPath?.Contains(query.Path, StringComparison.OrdinalIgnoreCase) ?? false))
    {
        return false;
    }

    if (!string.IsNullOrWhiteSpace(query.SourceHost)
        && !(auditEvent.SourceHost?.Contains(query.SourceHost, StringComparison.OrdinalIgnoreCase) ?? false))
    {
        return false;
    }

    if (!string.IsNullOrWhiteSpace(query.SourceIp)
        && !(auditEvent.SourceIp?.Contains(query.SourceIp, StringComparison.OrdinalIgnoreCase) ?? false))
    {
        return false;
    }

    var extensions = SplitCsvFilter(query.Extension)
        .Select(NormalizeTimelineFilterExtension)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (extensions.Length > 0
        && (auditEvent.Extension is null
            || !extensions.Contains(auditEvent.Extension, StringComparer.OrdinalIgnoreCase)))
    {
        return false;
    }

    if (!string.IsNullOrWhiteSpace(query.Result)
        && !auditEvent.Result.Contains(query.Result, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (!string.IsNullOrWhiteSpace(query.Severity)
        && !auditEvent.Severity.Equals(query.Severity, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (!string.IsNullOrWhiteSpace(query.Source)
        && !auditEvent.Source.Contains(query.Source, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (query.FromUtc.HasValue && auditEvent.TimestampUtc < query.FromUtc.Value)
    {
        return false;
    }

    if (query.ToUtc.HasValue && auditEvent.TimestampUtc > query.ToUtc.Value)
    {
        return false;
    }

    return true;
}

static string[] ParseTimelineActionFilter(string? action)
{
    if (string.IsNullOrWhiteSpace(action))
    {
        return Array.Empty<string>();
    }

    var normalized = action.Trim().ToLowerInvariant();
    if (normalized is "read" or "opened" or "access" or "accessed")
    {
        return ["accessed"];
    }

    if (normalized is "created_or_appended" or "append" or "appended")
    {
        return ["created_or_appended"];
    }

    return SplitCsvFilter(action)
        .Select(item => item.Trim().ToLowerInvariant())
        .Where(item => item.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static IEnumerable<string> SplitCsvFilter(string? value)
{
    return string.IsNullOrWhiteSpace(value)
        ? Array.Empty<string>()
        : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

static string NormalizeTimelineFilterExtension(string value)
{
    var normalized = value.Trim();
    if (string.IsNullOrWhiteSpace(normalized))
    {
        return string.Empty;
    }

    return normalized.StartsWith('.')
        ? normalized.ToLowerInvariant()
        : $".{normalized.ToLowerInvariant()}";
}

static async Task<DatabaseMetrics> BuildDatabaseMetricsAsync(
    IEventRepository repository,
    DateTimeOffset now,
    CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();

    try
    {
        var stats = await repository.GetStatsAsync(cancellationToken);
        stopwatch.Stop();

        return new DatabaseMetrics(
            Status: "healthy",
            Provider: repository.ProviderName,
            StoredEvents: stats.StoredEvents,
            LastEventUtc: stats.LastEventUtc,
            LastEventAgeSeconds: GetAgeSeconds(now, stats.LastEventUtc),
            QueryDurationMs: stopwatch.ElapsedMilliseconds,
            Error: null);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        stopwatch.Stop();

        return new DatabaseMetrics(
            Status: "critical",
            Provider: repository.ProviderName,
            StoredEvents: null,
            LastEventUtc: null,
            LastEventAgeSeconds: null,
            QueryDurationMs: stopwatch.ElapsedMilliseconds,
            Error: ex.Message);
    }
}

static async Task<DatabaseCapacityResponse> BuildDatabaseCapacityAsync(
    IConfiguration configuration,
    IEventRepository repository,
    CancellationToken cancellationToken)
{
    var generatedUtc = DateTimeOffset.UtcNow;

#if SQLSERVER
    if (repository.ProviderName.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
    {
        var connectionString = configuration.GetConnectionString("SqlServer")
            ?? throw new InvalidOperationException("ConnectionStrings:SqlServer nao foi configurada.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var tables = await QueryCapacityTablesAsync(connection, cancellationToken);
        var windows = await QueryCapacityWindowsAsync(connection, cancellationToken);
        var daily = await QueryCapacityDailyCountsAsync(connection, generatedUtc.AddDays(-30), generatedUtc, cancellationToken);
        var totalRows = tables.Sum(item => item.RowCount);
        var totalMb = tables.Sum(item => item.ReservedMb);
        var timelineWindow = windows.FirstOrDefault(item => item.Name.Equals("Linha do tempo", StringComparison.OrdinalIgnoreCase));

        return new DatabaseCapacityResponse(
            GeneratedUtc: generatedUtc,
            Provider: repository.ProviderName,
            Status: "healthy",
            TotalRows: totalRows,
            TotalReservedMb: Math.Round(totalMb, 2),
            TimelineRows: timelineWindow?.RowCount ?? 0,
            TimelineFromUtc: timelineWindow?.FromUtc,
            TimelineToUtc: timelineWindow?.ToUtc,
            Tables: tables,
            Windows: windows,
            DailyCounts: daily,
            Message: null);
    }
#endif

    var stats = await repository.GetStatsAsync(cancellationToken);
    return new DatabaseCapacityResponse(
        GeneratedUtc: generatedUtc,
        Provider: repository.ProviderName,
        Status: "limited",
        TotalRows: stats.StoredEvents,
        TotalReservedMb: 0,
        TimelineRows: 0,
        TimelineFromUtc: null,
        TimelineToUtc: stats.LastEventUtc,
        Tables: Array.Empty<DatabaseCapacityTable>(),
        Windows: Array.Empty<DatabaseCapacityWindow>(),
        DailyCounts: Array.Empty<DatabaseCapacityDailyCount>(),
        Message: "Capacidade detalhada disponivel apenas com SQL Server.");
}

static async Task<DatabaseCapacityMetrics> BuildDatabaseCapacityMetricsAsync(
    IConfiguration configuration,
    IEventRepository repository,
    DateTimeOffset now,
    CancellationToken cancellationToken)
{
    try
    {
#if SQLSERVER
        if (repository.ProviderName.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = configuration.GetConnectionString("SqlServer")
                ?? throw new InvalidOperationException("ConnectionStrings:SqlServer nao foi configurada.");

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            var tables = await QueryCapacityTablesAsync(connection, cancellationToken);
            var windows = await QueryCapacityWindowsAsync(connection, cancellationToken);
            var timelineWindow = windows.FirstOrDefault(item => item.Name.Equals("Linha do tempo", StringComparison.OrdinalIgnoreCase));
            var rawWindow = windows.FirstOrDefault(item => item.Name.Equals("Eventos brutos", StringComparison.OrdinalIgnoreCase));
            var alertWindow = windows.FirstOrDefault(item => item.Name.Equals("Alertas", StringComparison.OrdinalIgnoreCase));

            return new DatabaseCapacityMetrics(
                Status: "healthy",
                TotalRows: tables.Sum(item => item.RowCount),
                TotalReservedMb: Math.Round(tables.Sum(item => item.ReservedMb), 2),
                RawEventRows: rawWindow?.RowCount ?? 0,
                TimelineRows: timelineWindow?.RowCount ?? 0,
                AlertRows: alertWindow?.RowCount ?? 0,
                TimelineOldestAgeSeconds: GetAgeSeconds(now, timelineWindow?.FromUtc),
                TimelineNewestAgeSeconds: GetAgeSeconds(now, timelineWindow?.ToUtc),
                Error: null);
        }
#endif

        var stats = await repository.GetStatsAsync(cancellationToken);
        return new DatabaseCapacityMetrics(
            Status: "limited",
            TotalRows: stats.StoredEvents,
            TotalReservedMb: 0,
            RawEventRows: stats.StoredEvents,
            TimelineRows: 0,
            AlertRows: 0,
            TimelineOldestAgeSeconds: null,
            TimelineNewestAgeSeconds: null,
            Error: null);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return new DatabaseCapacityMetrics(
            Status: "critical",
            TotalRows: 0,
            TotalReservedMb: 0,
            RawEventRows: 0,
            TimelineRows: 0,
            AlertRows: 0,
            TimelineOldestAgeSeconds: null,
            TimelineNewestAgeSeconds: null,
            Error: ex.Message);
    }
}

#if SQLSERVER
static async Task<IReadOnlyCollection<DatabaseCapacityTable>> QueryCapacityTablesAsync(
    SqlConnection connection,
    CancellationToken cancellationToken)
{
    await using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT
            t.name AS TableName,
            SUM(p.row_count) AS [RowCount],
            SUM(p.reserved_page_count) * 8.0 / 1024.0 AS ReservedMb,
            SUM(p.used_page_count) * 8.0 / 1024.0 AS UsedMb
        FROM sys.dm_db_partition_stats p
        INNER JOIN sys.tables t ON p.object_id = t.object_id
        WHERE t.name IN
        (
            N'FileAuditEvents',
            N'FileAuditTimelineEvents',
            N'FileServerAlerts',
            N'AgentHeartbeats',
            N'MonitoredPaths',
            N'AdminAuditLog'
        )
            AND p.index_id IN (0, 1)
        GROUP BY t.name
        ORDER BY ReservedMb DESC, [RowCount] DESC;
        """;

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var result = new List<DatabaseCapacityTable>();

    while (await reader.ReadAsync(cancellationToken))
    {
        result.Add(new DatabaseCapacityTable(
            Name: LabelForCapacityTable(reader.GetString(reader.GetOrdinal("TableName"))),
            PhysicalName: reader.GetString(reader.GetOrdinal("TableName")),
            RowCount: Convert.ToInt64(reader["RowCount"]),
            ReservedMb: Math.Round(Convert.ToDouble(reader["ReservedMb"]), 2),
            UsedMb: Math.Round(Convert.ToDouble(reader["UsedMb"]), 2)));
    }

    return result;
}

static async Task<IReadOnlyCollection<DatabaseCapacityWindow>> QueryCapacityWindowsAsync(
    SqlConnection connection,
    CancellationToken cancellationToken)
{
    await using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT
            N'Eventos brutos' AS Name,
            COUNT_BIG(1) AS [RowCount],
            MIN(TimestampUtc) AS FromUtc,
            MAX(TimestampUtc) AS ToUtc
        FROM dbo.FileAuditEvents
        UNION ALL
        SELECT
            N'Linha do tempo' AS Name,
            COUNT_BIG(1) AS [RowCount],
            MIN(TimestampUtc) AS FromUtc,
            MAX(TimestampUtc) AS ToUtc
        FROM dbo.FileAuditTimelineEvents
        UNION ALL
        SELECT
            N'Alertas' AS Name,
            COUNT_BIG(1) AS [RowCount],
            MIN(CreatedUtc) AS FromUtc,
            MAX(CreatedUtc) AS ToUtc
        FROM dbo.FileServerAlerts;
        """;

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var result = new List<DatabaseCapacityWindow>();

    while (await reader.ReadAsync(cancellationToken))
    {
        result.Add(new DatabaseCapacityWindow(
            Name: reader.GetString(reader.GetOrdinal("Name")),
            RowCount: Convert.ToInt64(reader["RowCount"]),
            FromUtc: ReadNullableUtc(reader["FromUtc"]),
            ToUtc: ReadNullableUtc(reader["ToUtc"])));
    }

    return result;
}

static async Task<IReadOnlyCollection<DatabaseCapacityDailyCount>> QueryCapacityDailyCountsAsync(
    SqlConnection connection,
    DateTimeOffset fromUtc,
    DateTimeOffset toUtc,
    CancellationToken cancellationToken)
{
    await using var command = connection.CreateCommand();
    command.Parameters.AddWithValue("@FromUtc", fromUtc.UtcDateTime);
    command.Parameters.AddWithValue("@ToUtc", toUtc.UtcDateTime);
    command.CommandText = """
        SELECT
            CAST(TimestampUtc AS date) AS BucketDate,
            N'Eventos brutos' AS SeriesName,
            COUNT_BIG(1) AS EventCount
        FROM dbo.FileAuditEvents
        WHERE TimestampUtc >= @FromUtc AND TimestampUtc <= @ToUtc
        GROUP BY CAST(TimestampUtc AS date)
        UNION ALL
        SELECT
            CAST(TimestampUtc AS date) AS BucketDate,
            N'Linha do tempo' AS SeriesName,
            COUNT_BIG(1) AS EventCount
        FROM dbo.FileAuditTimelineEvents
        WHERE TimestampUtc >= @FromUtc AND TimestampUtc <= @ToUtc
        GROUP BY CAST(TimestampUtc AS date)
        UNION ALL
        SELECT
            CAST(CreatedUtc AS date) AS BucketDate,
            N'Alertas' AS SeriesName,
            COUNT_BIG(1) AS EventCount
        FROM dbo.FileServerAlerts
        WHERE CreatedUtc >= @FromUtc AND CreatedUtc <= @ToUtc
        GROUP BY CAST(CreatedUtc AS date)
        ORDER BY BucketDate DESC, SeriesName ASC;
        """;

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var result = new List<DatabaseCapacityDailyCount>();

    while (await reader.ReadAsync(cancellationToken))
    {
        var date = ((DateTime)reader["BucketDate"]).ToString("yyyy-MM-dd");
        result.Add(new DatabaseCapacityDailyCount(
            Date: date,
            Series: reader.GetString(reader.GetOrdinal("SeriesName")),
            Count: Convert.ToInt64(reader["EventCount"])));
    }

    return result;
}

static string LabelForCapacityTable(string physicalName)
{
    return physicalName switch
    {
        "FileAuditEvents" => "Eventos brutos",
        "FileAuditTimelineEvents" => "Linha do tempo",
        "FileServerAlerts" => "Alertas",
        "AgentHeartbeats" => "Agentes",
        "MonitoredPaths" => "Caminhos",
        "AdminAuditLog" => "Auditoria administrativa",
        _ => physicalName
    };
}

static DateTimeOffset? ReadNullableUtc(object value)
{
    return value == DBNull.Value
        ? null
        : new DateTimeOffset(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc));
}
#endif

static async Task<AgentMetricsSummary> BuildAgentMetricsAsync(
    AgentHealthStore store,
    DateTimeOffset now,
    CancellationToken cancellationToken)
{
    try
    {
        var agents = await store.ListAsync(cancellationToken);
        var items = agents
            .Select(agent => new AgentMetricsItem(
                AgentId: agent.AgentId,
                Server: agent.Server,
                Status: agent.Status,
                LastHeartbeatUtc: agent.LastHeartbeatUtc,
                LastHeartbeatAgeSeconds: GetAgeSeconds(now, agent.LastHeartbeatUtc),
                Version: agent.Version,
                LastRecordId: agent.LastRecordId,
                LastUsnByVolume: agent.LastUsnByVolume,
                PendingQueueEvents: agent.PendingQueueEvents,
                LastSuccessfulSendUtc: agent.LastSuccessfulSendUtc,
                LastSuccessfulSendAgeSeconds: GetAgeSeconds(now, agent.LastSuccessfulSendUtc),
                LastCollectedEventUtc: agent.LastCollectedEventUtc,
                LastCollectedEventAgeSeconds: GetAgeSeconds(now, agent.LastCollectedEventUtc),
                LastCycle: agent.LastCycle,
                OperationalStatus: agent.OperationalStatus,
                OperationalMessage: agent.OperationalMessage,
                HasCycleError: agent.HasCycleError,
                Message: agent.Message,
                IsStale: agent.IsStale))
            .OrderBy(item => item.Server)
            .ThenBy(item => item.AgentId)
            .ToArray();
        var stale = items.Count(item => item.IsStale);
        var backlog = items.Count(item => item.Status.Equals("backlog", StringComparison.OrdinalIgnoreCase)
            || item.PendingQueueEvents > 0);
        var attention = items.Count(item => item.OperationalStatus.Equals("attention", StringComparison.OrdinalIgnoreCase));
        var critical = items.Count(item => item.OperationalStatus.Equals("critical", StringComparison.OrdinalIgnoreCase));
        var cycleErrors = items.Count(item => item.HasCycleError);
        var lastCycles = items
            .Select(item => item.LastCycle)
            .Where(item => item is not null)
            .Cast<FileServerMonitor.Core.AgentCycleMetrics>()
            .ToArray();
        var unhealthy = items.Count(item => !item.OperationalStatus.Equals("ok", StringComparison.OrdinalIgnoreCase));

        return new AgentMetricsSummary(
            Status: ResolveAgentStatus(items.Length, stale, unhealthy, backlog, critical),
            Total: items.Length,
            Running: items.Count(item => item.Status.Equals("running", StringComparison.OrdinalIgnoreCase)),
            Stale: stale,
            Backlog: backlog,
            Attention: attention,
            Critical: critical,
            CycleErrors: cycleErrors,
            Unhealthy: unhealthy,
            MaxPendingQueueEvents: items.Length == 0 ? 0 : items.Max(item => item.PendingQueueEvents),
            MaxHeartbeatAgeSeconds: items
                .Select(item => item.LastHeartbeatAgeSeconds)
                .Where(item => item.HasValue)
                .DefaultIfEmpty(0)
                .Max(),
            MaxCollectedEventAgeSeconds: items
                .Select(item => item.LastCollectedEventAgeSeconds)
                .Where(item => item.HasValue)
                .DefaultIfEmpty(0)
                .Max(),
            LastCycleSecurityEventsRead: lastCycles.Sum(item => item.SecurityEventsRead),
            LastCycleUsnEventsRead: lastCycles.Sum(item => item.UsnEventsRead),
            LastCycleCorrelatedEvents: lastCycles.Sum(item => item.CorrelatedEvents),
            LastCycleSentEvents: lastCycles.Sum(item => item.SentEvents),
            LastCycleQueuedEvents: lastCycles.Sum(item => item.QueuedEvents),
            MaxCycleDurationMs: lastCycles.Length == 0 ? 0 : lastCycles.Max(item => item.DurationMs),
            Items: items,
            Error: null);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return new AgentMetricsSummary(
            Status: "critical",
            Total: 0,
            Running: 0,
            Stale: 0,
            Backlog: 0,
            Attention: 0,
            Critical: 0,
            CycleErrors: 0,
            Unhealthy: 0,
            MaxPendingQueueEvents: 0,
            MaxHeartbeatAgeSeconds: null,
            MaxCollectedEventAgeSeconds: null,
            LastCycleSecurityEventsRead: 0,
            LastCycleUsnEventsRead: 0,
            LastCycleCorrelatedEvents: 0,
            LastCycleSentEvents: 0,
            LastCycleQueuedEvents: 0,
            MaxCycleDurationMs: 0,
            Items: Array.Empty<AgentMetricsItem>(),
            Error: ex.Message);
    }
}

static async Task<InventoryMetrics> BuildInventoryMetricsAsync(
    IInventoryRepository inventory,
    DateTimeOffset now,
    CancellationToken cancellationToken)
{
    try
    {
        var latest = (await inventory.GetSnapshotsAsync(server: null, share: null, take: 1, cancellationToken))
            .FirstOrDefault();

        if (latest is null)
        {
            return new InventoryMetrics(
                Status: "empty",
                LastSnapshotId: null,
                LastSnapshotStatus: null,
                LastScanStartedUtc: null,
                LastScanFinishedUtc: null,
                LastScanAgeSeconds: null,
                FileCount: 0,
                FolderCount: 0,
                TotalBytes: 0,
                ErrorCount: 0,
                Inactive365DaysFileCount: 0,
                Inactive365DaysBytes: 0,
                LargeFileCount: 0,
                LargeFileBytes: 0,
                ExecutableFileCount: 0,
                ExecutableFileBytes: 0,
                Server: null,
                Share: null,
                RootPath: null,
                Error: null);
        }

        var status = latest.Status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            ? "critical"
            : latest.ErrorCount > 0 || latest.Status.Equals("completed_with_errors", StringComparison.OrdinalIgnoreCase)
                ? "degraded"
                : latest.Status.Equals("running", StringComparison.OrdinalIgnoreCase)
                    ? "running"
                    : "healthy";
        var summary = latest.Status is "completed" or "completed_with_errors"
            ? await inventory.GetLatestSummaryAsync(latest.Server, latest.Share, latest.RootPath, top: 1, cancellationToken)
            : null;

        return new InventoryMetrics(
            Status: status,
            LastSnapshotId: latest.Id,
            LastSnapshotStatus: latest.Status,
            LastScanStartedUtc: latest.StartedUtc,
            LastScanFinishedUtc: latest.FinishedUtc,
            LastScanAgeSeconds: GetAgeSeconds(now, latest.FinishedUtc ?? latest.StartedUtc),
            FileCount: latest.FileCount,
            FolderCount: latest.FolderCount,
            TotalBytes: latest.TotalBytes,
            ErrorCount: latest.ErrorCount,
            Inactive365DaysFileCount: summary?.Governance.Inactive365DaysFileCount ?? 0,
            Inactive365DaysBytes: summary?.Governance.Inactive365DaysBytes ?? 0,
            LargeFileCount: summary?.Governance.LargeFileCount ?? 0,
            LargeFileBytes: summary?.Governance.LargeFileBytes ?? 0,
            ExecutableFileCount: summary?.Governance.ExecutableFileCount ?? 0,
            ExecutableFileBytes: summary?.Governance.ExecutableFileBytes ?? 0,
            Server: latest.Server,
            Share: latest.Share,
            RootPath: latest.RootPath,
            Error: latest.Error);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return new InventoryMetrics(
            Status: "critical",
            LastSnapshotId: null,
            LastSnapshotStatus: null,
            LastScanStartedUtc: null,
            LastScanFinishedUtc: null,
            LastScanAgeSeconds: null,
            FileCount: 0,
            FolderCount: 0,
            TotalBytes: 0,
            ErrorCount: 0,
            Inactive365DaysFileCount: 0,
            Inactive365DaysBytes: 0,
            LargeFileCount: 0,
            LargeFileBytes: 0,
            ExecutableFileCount: 0,
            ExecutableFileBytes: 0,
            Server: null,
            Share: null,
            RootPath: null,
            Error: ex.Message);
    }
}

static long? GetAgeSeconds(DateTimeOffset now, DateTimeOffset? value)
{
    return value is null
        ? null
        : Math.Max(0, (long)now.Subtract(value.Value).TotalSeconds);
}

static string ResolveAgentStatus(int total, int stale, int unhealthy, int backlog, int critical)
{
    if (total == 0 || stale > 0 || critical > 0)
    {
        return "critical";
    }

    if (unhealthy > 0 || backlog > 0)
    {
        return "degraded";
    }

    return "healthy";
}

static string ResolveMetricsStatus(DatabaseMetrics database, AgentMetricsSummary agents, InventoryMetrics inventory)
{
    if (database.Status.Equals("critical", StringComparison.OrdinalIgnoreCase)
        || agents.Status.Equals("critical", StringComparison.OrdinalIgnoreCase)
        || inventory.Status.Equals("critical", StringComparison.OrdinalIgnoreCase))
    {
        return "critical";
    }

    if (!database.Status.Equals("healthy", StringComparison.OrdinalIgnoreCase)
        || !agents.Status.Equals("healthy", StringComparison.OrdinalIgnoreCase)
        || inventory.Status.Equals("degraded", StringComparison.OrdinalIgnoreCase))
    {
        return "degraded";
    }

    return "healthy";
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

internal interface ITimelineRepository
{
    Task ReplaceWindowAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<FileAuditDisplayEvent> events,
        string correlationVersion,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<FileAuditDisplayEvent>> QueryAsync(TimelineQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<FileAuditDisplayEvent>> QueryKnownLiveDescendantsAsync(
        string previousRoot,
        DateTimeOffset beforeUtc,
        int take,
        CancellationToken cancellationToken);

    Task<ActivitySummaryResponse> GetActivitySummaryAsync(ActivitySummaryQuery query, CancellationToken cancellationToken);

    Task<FileInventoryObservedActivitySummary> GetObservedActivitySummaryAsync(
        string? server,
        string? share,
        string? rootPath,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int top,
        CancellationToken cancellationToken);

    Task<BaselineAnomalyResponse> GetBaselineAnomaliesAsync(BaselineAnomalyQuery query, CancellationToken cancellationToken);

    Task<TimelineCoverage> GetCoverageAsync(CancellationToken cancellationToken);

    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, int batchSize, CancellationToken cancellationToken);
}

internal interface IInventoryRepository
{
    Task<FileInventorySnapshot> StartSnapshotAsync(FileInventorySnapshot snapshot, CancellationToken cancellationToken);

    Task AddBatchAsync(Guid snapshotId, IReadOnlyCollection<FileInventoryItem> items, CancellationToken cancellationToken);

    Task<FileInventorySnapshot?> CompleteSnapshotAsync(Guid snapshotId, string status, string? error, CancellationToken cancellationToken);

    Task<FileInventorySummary> GetLatestSummaryAsync(string? server, string? share, string? rootPath, int top, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<FileInventoryItem>> QueryLatestItemsAsync(
        string? server,
        string? share,
        string? rootPath,
        string? kind,
        string? path,
        string? extension,
        int take,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<FileInventorySnapshot>> GetSnapshotsAsync(string? server, string? share, int take, CancellationToken cancellationToken);
}

#if SQLSERVER
internal sealed class SqlServerTimelineRepository : ITimelineRepository
{
    private readonly string _connectionString;
    private int _schemaEnsured;

    public SqlServerTimelineRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("SqlServer")
            ?? throw new InvalidOperationException("ConnectionStrings:SqlServer nao foi configurada.");
    }

    public async Task ReplaceWindowAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<FileAuditDisplayEvent> events,
        string correlationVersion,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureOperationalIndexesAsync(connection, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = """
                    DELETE FROM dbo.FileAuditTimelineEvents
                    WHERE TimestampUtc >= @FromUtc AND TimestampUtc <= @ToUtc;
                    """;
                delete.Parameters.AddWithValue("@FromUtc", fromUtc.UtcDateTime);
                delete.Parameters.AddWithValue("@ToUtc", toUtc.UtcDateTime);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var item in events)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO dbo.FileAuditTimelineEvents
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
                        SourceName,
                        DisplayAction,
                        DisplayTarget,
                        CorrelationVersion,
                        CorrelatedUtc
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
                        @SourceName,
                        @DisplayAction,
                        @DisplayTarget,
                        @CorrelationVersion,
                        SYSUTCDATETIME()
                    );
                    """;
                AddTimelineParameters(insert, item, correlationVersion);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyCollection<FileAuditDisplayEvent>> QueryAsync(TimelineQuery query, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureOperationalIndexesAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = BuildTimelineQuerySql(query, command);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<FileAuditDisplayEvent>();
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(ReadTimelineEvent(reader));
        }

        return events;
    }

    public async Task<IReadOnlyCollection<FileAuditDisplayEvent>> QueryKnownLiveDescendantsAsync(
        string previousRoot,
        DateTimeOffset beforeUtc,
        int take,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = FileInventoryNormalizer.NormalizePath(previousRoot).TrimEnd('\\');
        if (string.IsNullOrWhiteSpace(normalizedRoot))
        {
            return Array.Empty<FileAuditDisplayEvent>();
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureOperationalIndexesAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.Parameters.AddWithValue("@RootPrefix", $"{normalizedRoot}\\%");
        command.Parameters.AddWithValue("@BeforeUtc", beforeUtc.UtcDateTime);
        command.Parameters.AddWithValue("@Take", Math.Clamp(take, 1, 10_000));
        command.CommandText = """
            WITH CandidateHistory AS
            (
                SELECT
                    *,
                    ROW_NUMBER() OVER (PARTITION BY FullPath ORDER BY TimestampUtc DESC, CorrelatedUtc DESC) AS PathRank
                FROM dbo.FileAuditTimelineEvents
                WHERE TimestampUtc < @BeforeUtc
                  AND FullPath LIKE @RootPrefix
                  AND ObjectType <> N'folder'
            ),
            LiveCandidates AS
            (
                SELECT TOP (@Take) *
                FROM CandidateHistory candidate
                WHERE candidate.PathRank = 1
                  AND candidate.ActionName <> N'deleted'
                  AND NOT EXISTS
                  (
                      SELECT 1
                      FROM dbo.FileAuditTimelineEvents terminal
                      WHERE terminal.TimestampUtc > candidate.TimestampUtc
                        AND terminal.TimestampUtc < @BeforeUtc
                        AND (
                            (terminal.FullPath = candidate.FullPath AND terminal.ActionName = N'deleted')
                            OR (terminal.PreviousPath = candidate.FullPath AND terminal.ActionName IN (N'moved', N'renamed', N'deleted'))
                        )
                  )
                ORDER BY candidate.TimestampUtc DESC
            )
            SELECT
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
                SourceName,
                DisplayAction,
                DisplayTarget
            FROM LiveCandidates
            ORDER BY TimestampUtc DESC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<FileAuditDisplayEvent>();
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(ReadTimelineEvent(reader));
        }

        return events;
    }

    public async Task<ActivitySummaryResponse> GetActivitySummaryAsync(
        ActivitySummaryQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureOperationalIndexesAsync(connection, cancellationToken);

        var total = await CountTimelineEventsAsync(connection, query, cancellationToken);
        var byAction = await QueryTimelineDimensionAsync(connection, query, "DisplayAction", cancellationToken);
        var byShare = await QueryTimelineDimensionAsync(connection, query, "ShareName", cancellationToken);
        var byUser = await QueryTimelineDimensionAsync(connection, query, "UserName", cancellationToken);

        return new ActivitySummaryResponse(
            FromUtc: query.FromUtc,
            ToUtc: query.ToUtc,
            TotalEvents: total,
            ByAction: byAction,
            ByShare: byShare,
            ByUser: byUser);
    }

    public async Task<FileInventoryObservedActivitySummary> GetObservedActivitySummaryAsync(
        string? server,
        string? share,
        string? rootPath,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int top,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureOperationalIndexesAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        var where = BuildObservedActivityWhereSql(command, server, share, rootPath, fromUtc, toUtc);
        command.Parameters.AddWithValue("@Top", Math.Clamp(top, 1, 100));
        command.CommandText = $$"""
            WITH Filtered AS
            (
                SELECT
                    TimestampUtc,
                    UserName,
                    DisplayAction,
                    CASE
                        WHEN ObjectType = N'folder' THEN FullPath
                        WHEN CHARINDEX(N'\', REVERSE(FullPath)) > 0 THEN LEFT(FullPath, LEN(FullPath) - CHARINDEX(N'\', REVERSE(FullPath)))
                        ELSE FullPath
                    END AS FolderPath
                FROM dbo.FileAuditTimelineEvents
                {{where}}
            ),
            FolderTotals AS
            (
                SELECT FolderPath, COUNT_BIG(1) AS EventCount, MAX(TimestampUtc) AS LastActivityUtc
                FROM Filtered
                GROUP BY FolderPath
            ),
            FolderActions AS
            (
                SELECT
                    FolderPath,
                    DisplayAction,
                    ROW_NUMBER() OVER (PARTITION BY FolderPath ORDER BY COUNT_BIG(1) DESC, DisplayAction ASC) AS ActionRank
                FROM Filtered
                GROUP BY FolderPath, DisplayAction
            )
            SELECT TOP (@Top)
                FolderTotals.FolderPath,
                FolderTotals.EventCount,
                FolderTotals.LastActivityUtc,
                FolderActions.DisplayAction AS TopAction
            FROM FolderTotals
            INNER JOIN FolderActions
                ON FolderActions.FolderPath = FolderTotals.FolderPath
               AND FolderActions.ActionRank = 1
            ORDER BY FolderTotals.EventCount DESC, FolderTotals.LastActivityUtc DESC, FolderTotals.FolderPath ASC;

            WITH Filtered AS
            (
                SELECT TimestampUtc, UserName, DisplayAction
                FROM dbo.FileAuditTimelineEvents
                {{where}}
            ),
            UserTotals AS
            (
                SELECT UserName, COUNT_BIG(1) AS EventCount, MAX(TimestampUtc) AS LastActivityUtc
                FROM Filtered
                GROUP BY UserName
            ),
            UserActions AS
            (
                SELECT
                    UserName,
                    DisplayAction,
                    ROW_NUMBER() OVER (PARTITION BY UserName ORDER BY COUNT_BIG(1) DESC, DisplayAction ASC) AS ActionRank
                FROM Filtered
                GROUP BY UserName, DisplayAction
            )
            SELECT TOP (@Top)
                UserTotals.UserName,
                UserTotals.EventCount,
                UserTotals.LastActivityUtc,
                UserActions.DisplayAction AS TopAction
            FROM UserTotals
            INNER JOIN UserActions
                ON UserActions.UserName = UserTotals.UserName
               AND UserActions.ActionRank = 1
            ORDER BY UserTotals.EventCount DESC, UserTotals.LastActivityUtc DESC, UserTotals.UserName ASC;

            SELECT COUNT_BIG(1) AS TotalEvents
            FROM dbo.FileAuditTimelineEvents
            {{where}};
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var topFolders = new List<FileInventoryTopActivityFolder>();
        while (await reader.ReadAsync(cancellationToken))
        {
            topFolders.Add(new FileInventoryTopActivityFolder(
                Path: reader.GetString(reader.GetOrdinal("FolderPath")),
                EventCount: Convert.ToInt64(reader["EventCount"]),
                LastActivityUtc: new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("LastActivityUtc")), DateTimeKind.Utc)),
                TopAction: reader.GetString(reader.GetOrdinal("TopAction"))));
        }

        await reader.NextResultAsync(cancellationToken);
        var topUsers = new List<FileInventoryTopActivityUser>();
        while (await reader.ReadAsync(cancellationToken))
        {
            topUsers.Add(new FileInventoryTopActivityUser(
                User: reader.GetString(reader.GetOrdinal("UserName")),
                EventCount: Convert.ToInt64(reader["EventCount"]),
                LastActivityUtc: new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("LastActivityUtc")), DateTimeKind.Utc)),
                TopAction: reader.GetString(reader.GetOrdinal("TopAction"))));
        }

        await reader.NextResultAsync(cancellationToken);
        var totalEvents = await reader.ReadAsync(cancellationToken)
            ? Convert.ToInt64(reader["TotalEvents"])
            : 0;

        return new FileInventoryObservedActivitySummary(totalEvents, topFolders, topUsers);
    }

    public async Task<BaselineAnomalyResponse> GetBaselineAnomaliesAsync(
        BaselineAnomalyQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureOperationalIndexesAsync(connection, cancellationToken);

        var byAction = await BuildTimelineAnomaliesAsync(connection, query, "DisplayAction", cancellationToken);
        var byShare = await BuildTimelineAnomaliesAsync(connection, query, "ShareName", cancellationToken);
        var byUser = await BuildTimelineAnomaliesAsync(connection, query, "UserName", cancellationToken);

        return new BaselineAnomalyResponse(
            FromUtc: query.FromUtc,
            ToUtc: query.ToUtc,
            BaselineWindows: query.BaselineWindows,
            ByAction: byAction,
            ByShare: byShare,
            ByUser: byUser);
    }

    public async Task<TimelineCoverage> GetCoverageAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureOperationalIndexesAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT_BIG(1) AS TimelineEvents,
                MIN(TimestampUtc) AS FromUtc,
                MAX(TimestampUtc) AS ToUtc
            FROM dbo.FileAuditTimelineEvents;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new TimelineCoverage(0, null, null);
        }

        return new TimelineCoverage(
            Count: Convert.ToInt64(reader["TimelineEvents"]),
            FromUtc: reader["FromUtc"] == DBNull.Value
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind((DateTime)reader["FromUtc"], DateTimeKind.Utc)),
            ToUtc: reader["ToUtc"] == DBNull.Value
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind((DateTime)reader["ToUtc"], DateTimeKind.Utc)));
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
        await EnsureOperationalIndexesAsync(connection, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE TOP (@BatchSize)
                FROM dbo.FileAuditTimelineEvents
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

    private async Task EnsureOperationalIndexesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _schemaEnsured, 1, 0) != 0)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 300;
        command.CommandText = """
            IF OBJECT_ID(N'dbo.FileAuditTimelineEvents', N'U') IS NOT NULL
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileAuditTimelineEvents_Action_Time' AND object_id = OBJECT_ID(N'dbo.FileAuditTimelineEvents'))
                BEGIN
                    CREATE INDEX IX_FileAuditTimelineEvents_Action_Time
                        ON dbo.FileAuditTimelineEvents (ActionName, TimestampUtc DESC)
                        INCLUDE (ServerName, ShareName, UserName, DisplayAction, DisplayTarget);
                END;

                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileAuditTimelineEvents_DisplayAction_Time' AND object_id = OBJECT_ID(N'dbo.FileAuditTimelineEvents'))
                BEGIN
                    CREATE INDEX IX_FileAuditTimelineEvents_DisplayAction_Time
                        ON dbo.FileAuditTimelineEvents (DisplayAction, TimestampUtc DESC)
                        INCLUDE (ServerName, ShareName, UserName, DisplayTarget);
                END;

                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileAuditTimelineEvents_User_Time' AND object_id = OBJECT_ID(N'dbo.FileAuditTimelineEvents'))
                BEGIN
                    CREATE INDEX IX_FileAuditTimelineEvents_User_Time
                        ON dbo.FileAuditTimelineEvents (UserName, TimestampUtc DESC)
                        INCLUDE (ServerName, ShareName, ActionName, DisplayAction, DisplayTarget);
                END;

                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileAuditTimelineEvents_Extension_Time' AND object_id = OBJECT_ID(N'dbo.FileAuditTimelineEvents'))
                BEGIN
                    CREATE INDEX IX_FileAuditTimelineEvents_Extension_Time
                        ON dbo.FileAuditTimelineEvents (Extension, TimestampUtc DESC)
                        INCLUDE (ServerName, ShareName, UserName, ActionName, DisplayAction, DisplayTarget);
                END;

            END;
            """;

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            Interlocked.Exchange(ref _schemaEnsured, 0);
            throw;
        }
    }

    private static string BuildTimelineQuerySql(TimelineQuery query, SqlCommand command)
    {
        command.Parameters.AddWithValue("@Take", query.Take);
        var predicates = new List<string>();

        if (!string.IsNullOrWhiteSpace(query.Server))
        {
            predicates.Add("ServerName LIKE @Server");
            command.Parameters.AddWithValue("@Server", $"%{query.Server}%");
        }

        if (!string.IsNullOrWhiteSpace(query.Share))
        {
            predicates.Add("ShareName LIKE @Share");
            command.Parameters.AddWithValue("@Share", $"%{query.Share}%");
        }

        if (!string.IsNullOrWhiteSpace(query.User))
        {
            predicates.Add("UserName LIKE @User");
            command.Parameters.AddWithValue("@User", $"%{query.User}%");
        }

        var actions = BuildTimelineResultActionFilter(query.Action);
        if (actions.Length == 1)
        {
            predicates.Add("ActionName = @TimelineAction");
            command.Parameters.AddWithValue("@TimelineAction", actions[0]);
        }
        else if (actions.Length > 1)
        {
            var parameterNames = new List<string>();
            for (var index = 0; index < actions.Length; index++)
            {
                var parameterName = $"@TimelineAction{index}";
                parameterNames.Add(parameterName);
                command.Parameters.AddWithValue(parameterName, actions[index]);
            }

            predicates.Add($"ActionName IN ({string.Join(", ", parameterNames)})");
        }

        if (!string.IsNullOrWhiteSpace(query.Path))
        {
            predicates.Add("(FullPath LIKE @Path OR PreviousPath LIKE @Path)");
            command.Parameters.AddWithValue("@Path", $"%{query.Path}%");
        }

        if (!string.IsNullOrWhiteSpace(query.SourceHost))
        {
            predicates.Add("SourceHost LIKE @SourceHost");
            command.Parameters.AddWithValue("@SourceHost", $"%{query.SourceHost}%");
        }

        if (!string.IsNullOrWhiteSpace(query.SourceIp))
        {
            predicates.Add("SourceIp LIKE @SourceIp");
            command.Parameters.AddWithValue("@SourceIp", $"%{query.SourceIp}%");
        }

        var extensions = SplitFilterValues(query.Extension)
            .Select(NormalizeExtensionFilter)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (extensions.Length > 0)
        {
            var parameterNames = new List<string>();
            for (var index = 0; index < extensions.Length; index++)
            {
                var parameterName = $"@TimelineExtension{index}";
                parameterNames.Add(parameterName);
                command.Parameters.AddWithValue(parameterName, extensions[index]);
            }

            predicates.Add($"Extension IN ({string.Join(", ", parameterNames)})");
        }

        if (!string.IsNullOrWhiteSpace(query.Result))
        {
            predicates.Add("ResultName LIKE @Result");
            command.Parameters.AddWithValue("@Result", $"%{query.Result}%");
        }

        if (!string.IsNullOrWhiteSpace(query.Severity))
        {
            predicates.Add("Severity = @Severity");
            command.Parameters.AddWithValue("@Severity", query.Severity);
        }

        if (!string.IsNullOrWhiteSpace(query.Source))
        {
            predicates.Add("SourceName LIKE @Source");
            command.Parameters.AddWithValue("@Source", $"%{query.Source}%");
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
                SourceName,
                DisplayAction,
                DisplayTarget
            FROM dbo.FileAuditTimelineEvents
            {{where}}
            ORDER BY TimestampUtc DESC;
            """;
    }

    private static async Task<long> CountTimelineEventsAsync(
        SqlConnection connection,
        ActivitySummaryQuery query,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var where = BuildTimelineSummaryWhereSql(command, query);
        command.CommandText = $$"""
            SELECT COUNT_BIG(1)
            FROM dbo.FileAuditTimelineEvents
            {{where}};
            """;

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<IReadOnlyCollection<ActivitySummaryItem>> QueryTimelineDimensionAsync(
        SqlConnection connection,
        ActivitySummaryQuery query,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var where = BuildTimelineSummaryWhereSql(command, query);
        command.CommandText = $$"""
            SELECT TOP (@Take)
                {{columnName}} AS Name,
                COUNT_BIG(1) AS EventCount
            FROM dbo.FileAuditTimelineEvents
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

    private static string BuildObservedActivityWhereSql(
        SqlCommand command,
        string? server,
        string? share,
        string? rootPath,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        var predicates = new List<string>
        {
            "TimestampUtc >= @ObservedFromUtc",
            "TimestampUtc <= @ObservedToUtc",
            "UserName NOT LIKE N'%$'"
        };
        command.Parameters.AddWithValue("@ObservedFromUtc", fromUtc.UtcDateTime);
        command.Parameters.AddWithValue("@ObservedToUtc", toUtc.UtcDateTime);

        if (!string.IsNullOrWhiteSpace(server))
        {
            predicates.Add("ServerName LIKE @ObservedServer");
            command.Parameters.AddWithValue("@ObservedServer", $"%{server.Trim()}%");
        }

        if (!string.IsNullOrWhiteSpace(share))
        {
            predicates.Add("ShareName LIKE @ObservedShare");
            command.Parameters.AddWithValue("@ObservedShare", $"%{share.Trim()}%");
        }

        if (!string.IsNullOrWhiteSpace(rootPath))
        {
            var normalizedRoot = FileInventoryNormalizer.NormalizePath(rootPath);
            predicates.Add("(FullPath = @ObservedRootPath OR FullPath LIKE @ObservedRootPathPrefix)");
            command.Parameters.AddWithValue("@ObservedRootPath", normalizedRoot);
            command.Parameters.AddWithValue("@ObservedRootPathPrefix", $"{normalizedRoot}\\%");
        }

        return $"WHERE {string.Join(" AND ", predicates)}";
    }

    private static async Task<IReadOnlyCollection<BaselineAnomalyItem>> BuildTimelineAnomaliesAsync(
        SqlConnection connection,
        BaselineAnomalyQuery query,
        string columnName,
        CancellationToken cancellationToken)
    {
        var current = await QueryTimelineDimensionCountsAsync(
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
            var items = await QueryTimelineDimensionCountsAsync(
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

    private static async Task<Dictionary<string, long>> QueryTimelineDimensionCountsAsync(
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
        var query = new ActivitySummaryQuery(fromUtc, toUtc, server, share, user, action, Take: 1);
        var where = BuildTimelineSummaryWhereSql(command, query, includeTake: false);
        command.CommandText = $$"""
            SELECT
                {{columnName}} AS Name,
                COUNT_BIG(1) AS EventCount
            FROM dbo.FileAuditTimelineEvents
            {{where}}
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

    private static string BuildTimelineSummaryWhereSql(SqlCommand command, ActivitySummaryQuery query, bool includeTake = true)
    {
        command.Parameters.AddWithValue("@FromUtc", query.FromUtc.UtcDateTime);
        command.Parameters.AddWithValue("@ToUtc", query.ToUtc.UtcDateTime);
        if (includeTake)
        {
            command.Parameters.AddWithValue("@Take", query.Take);
        }

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

        var actions = BuildTimelineResultActionFilter(query.Action);
        if (actions.Length == 1)
        {
            predicates.Add("ActionName = @SummaryAction");
            command.Parameters.AddWithValue("@SummaryAction", actions[0]);
        }
        else if (actions.Length > 1)
        {
            var parameterNames = new List<string>();
            for (var index = 0; index < actions.Length; index++)
            {
                var parameterName = $"@SummaryAction{index}";
                parameterNames.Add(parameterName);
                command.Parameters.AddWithValue(parameterName, actions[index]);
            }

            predicates.Add($"ActionName IN ({string.Join(", ", parameterNames)})");
        }

        return $"WHERE {string.Join(" AND ", predicates)}";
    }

    private static void AddTimelineParameters(SqlCommand command, FileAuditDisplayEvent item, string correlationVersion)
    {
        command.Parameters.AddWithValue("@Id", item.Id);
        command.Parameters.AddWithValue("@TimestampUtc", item.TimestampUtc.UtcDateTime);
        command.Parameters.AddWithValue("@ServerName", item.Server);
        command.Parameters.AddWithValue("@ShareName", item.Share);
        command.Parameters.AddWithValue("@FullPath", item.Path);
        command.Parameters.AddWithValue("@PreviousPath", (object?)item.PreviousPath ?? DBNull.Value);
        command.Parameters.AddWithValue("@ObjectType", item.ObjectType);
        command.Parameters.AddWithValue("@ActionName", item.Action);
        command.Parameters.AddWithValue("@UserName", item.User);
        command.Parameters.AddWithValue("@Sid", (object?)item.Sid ?? DBNull.Value);
        command.Parameters.AddWithValue("@SourceHost", (object?)item.SourceHost ?? DBNull.Value);
        command.Parameters.AddWithValue("@SourceIp", (object?)item.SourceIp ?? DBNull.Value);
        command.Parameters.AddWithValue("@ProcessName", (object?)item.ProcessName ?? DBNull.Value);
        command.Parameters.AddWithValue("@FileSizeBytes", (object?)item.FileSizeBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("@Extension", (object?)item.Extension ?? DBNull.Value);
        command.Parameters.AddWithValue("@ResultName", item.Result);
        command.Parameters.AddWithValue("@Severity", item.Severity);
        command.Parameters.AddWithValue("@SourceName", item.Source);
        command.Parameters.AddWithValue("@DisplayAction", item.DisplayAction);
        command.Parameters.AddWithValue("@DisplayTarget", item.DisplayTarget);
        command.Parameters.AddWithValue("@CorrelationVersion", correlationVersion);
    }

    private static FileAuditDisplayEvent ReadTimelineEvent(SqlDataReader reader)
    {
        return new FileAuditDisplayEvent(
            Id: reader.GetGuid(reader.GetOrdinal("Id")),
            TimestampUtc: new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("TimestampUtc")), DateTimeKind.Utc)),
            Server: reader.GetString(reader.GetOrdinal("ServerName")),
            Share: reader.GetString(reader.GetOrdinal("ShareName")),
            Path: reader.GetString(reader.GetOrdinal("FullPath")),
            PreviousPath: reader["PreviousPath"] as string,
            ObjectType: reader.GetString(reader.GetOrdinal("ObjectType")),
            Action: reader.GetString(reader.GetOrdinal("ActionName")),
            User: reader.GetString(reader.GetOrdinal("UserName")),
            Sid: reader["Sid"] as string,
            SourceHost: reader["SourceHost"] as string,
            SourceIp: reader["SourceIp"] as string,
            ProcessName: reader["ProcessName"] as string,
            FileSizeBytes: reader["FileSizeBytes"] == DBNull.Value ? null : Convert.ToInt64(reader["FileSizeBytes"]),
            Extension: reader["Extension"] as string,
            Result: reader.GetString(reader.GetOrdinal("ResultName")),
            Severity: reader.GetString(reader.GetOrdinal("Severity")),
            Source: reader.GetString(reader.GetOrdinal("SourceName")),
            DisplayAction: reader.GetString(reader.GetOrdinal("DisplayAction")),
            DisplayTarget: reader.GetString(reader.GetOrdinal("DisplayTarget")));
    }

    private static IEnumerable<string> SplitFilterValues(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static string NormalizeExtensionFilter(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith(".", StringComparison.Ordinal)
            ? trimmed.ToLowerInvariant()
            : $".{trimmed.ToLowerInvariant()}";
    }

    private static string[] BuildTimelineResultActionFilter(string? action)
    {
        return action?.Trim().ToLowerInvariant() switch
        {
            "created" => new[] { "created", "created_or_appended" },
            "modified" => new[] { "modified", "changed" },
            "accessed" => new[] { "accessed" },
            "deleted" => new[] { "deleted" },
            "renamed" => new[] { "renamed" },
            "moved" => new[] { "moved" },
            "permission_changed" => new[] { "permission_changed" },
            _ => Array.Empty<string>()
        };
    }
}

internal sealed class SqlServerInventoryRepository : IInventoryRepository
{
    private readonly string _connectionString;
    private int _schemaEnsured;

    public SqlServerInventoryRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("SqlServer")
            ?? throw new InvalidOperationException("ConnectionStrings:SqlServer nao foi configurada.");
    }

    public async Task<FileInventorySnapshot> StartSnapshotAsync(FileInventorySnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.FileInventorySnapshots
            (
                Id,
                ServerName,
                ShareName,
                RootPath,
                StartedUtc,
                FinishedUtc,
                StatusName,
                FileCount,
                FolderCount,
                TotalBytes,
                ErrorCount,
                ErrorText
            )
            VALUES
            (
                @Id,
                @ServerName,
                @ShareName,
                @RootPath,
                @StartedUtc,
                NULL,
                @StatusName,
                0,
                0,
                0,
                0,
                NULL
            );
            """;
        command.Parameters.AddWithValue("@Id", snapshot.Id);
        command.Parameters.AddWithValue("@ServerName", snapshot.Server);
        command.Parameters.AddWithValue("@ShareName", snapshot.Share);
        command.Parameters.AddWithValue("@RootPath", snapshot.RootPath);
        command.Parameters.AddWithValue("@StartedUtc", snapshot.StartedUtc.UtcDateTime);
        command.Parameters.AddWithValue("@StatusName", snapshot.Status);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return snapshot;
    }

    public async Task AddBatchAsync(Guid snapshotId, IReadOnlyCollection<FileInventoryItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var item in items)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO dbo.FileInventoryItems
                    (
                        Id,
                        SnapshotId,
                        ScannedAtUtc,
                        ServerName,
                        ShareName,
                        RootPath,
                        FullPath,
                        RelativePath,
                        ItemName,
                        ItemType,
                        Extension,
                        SizeBytes,
                        Depth,
                        CreatedUtc,
                        ModifiedUtc,
                        AccessedUtc,
                        StatusName,
                        ErrorText
                    )
                    VALUES
                    (
                        @Id,
                        @SnapshotId,
                        @ScannedAtUtc,
                        @ServerName,
                        @ShareName,
                        @RootPath,
                        @FullPath,
                        @RelativePath,
                        @ItemName,
                        @ItemType,
                        @Extension,
                        @SizeBytes,
                        @Depth,
                        @CreatedUtc,
                        @ModifiedUtc,
                        @AccessedUtc,
                        @StatusName,
                        @ErrorText
                    );
                    """;
                AddItemParameters(command, item with { SnapshotId = snapshotId });
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

    public async Task<FileInventorySnapshot?> CompleteSnapshotAsync(
        Guid snapshotId,
        string status,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE dbo.FileInventorySnapshots
                SET
                    FinishedUtc = SYSUTCDATETIME(),
                    StatusName = @StatusName,
                    FileCount = (
                        SELECT COUNT_BIG(1)
                        FROM dbo.FileInventoryItems
                        WHERE SnapshotId = @Id AND ItemType = N'file' AND StatusName = N'active'
                    ),
                    FolderCount = (
                        SELECT COUNT_BIG(1)
                        FROM dbo.FileInventoryItems
                        WHERE SnapshotId = @Id AND ItemType = N'folder' AND StatusName = N'active'
                    ),
                    TotalBytes = (
                        SELECT COALESCE(SUM(SizeBytes), 0)
                        FROM dbo.FileInventoryItems
                        WHERE SnapshotId = @Id AND ItemType = N'file' AND StatusName = N'active'
                    ),
                    ErrorCount = (
                        SELECT COUNT_BIG(1)
                        FROM dbo.FileInventoryItems
                        WHERE SnapshotId = @Id AND StatusName = N'error'
                    ),
                    ErrorText = @ErrorText
                WHERE Id = @Id;

                SELECT @@ROWCOUNT;
                """;
            command.Parameters.AddWithValue("@Id", snapshotId);
            command.Parameters.AddWithValue("@StatusName", string.IsNullOrWhiteSpace(status) ? "completed" : status.Trim());
            command.Parameters.AddWithValue("@ErrorText", DbValue(error));

            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                return null;
            }
        }

        return await FindSnapshotAsync(connection, snapshotId, cancellationToken);
    }

    public async Task<FileInventorySummary> GetLatestSummaryAsync(
        string? server,
        string? share,
        string? rootPath,
        int top,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        var snapshot = await FindLatestSnapshotAsync(connection, server, share, rootPath, cancellationToken);
        if (snapshot is null)
        {
            return FileInventoryAnalyzer.BuildSummary(null, Array.Empty<FileInventoryItem>(), top);
        }

        var previousSnapshot = await FindPreviousSnapshotAsync(connection, snapshot, cancellationToken);
        return await BuildSqlSummaryAsync(connection, snapshot, previousSnapshot, top, cancellationToken);
    }

    public async Task<IReadOnlyCollection<FileInventoryItem>> QueryLatestItemsAsync(
        string? server,
        string? share,
        string? rootPath,
        string? kind,
        string? path,
        string? extension,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        var snapshot = await FindLatestSnapshotAsync(connection, server, share, rootPath, cancellationToken);
        if (snapshot is null)
        {
            return Array.Empty<FileInventoryItem>();
        }

        await using var command = connection.CreateCommand();
        var predicates = new List<string> { "SnapshotId = @SnapshotId" };
        var normalizedKind = NormalizeInventoryItemKind(kind);
        var orderBy = "FullPath ASC";

        switch (normalizedKind)
        {
            case "large":
                predicates.Add("ItemType = N'file'");
                predicates.Add("StatusName = N'active'");
                predicates.Add("SizeBytes >= 1073741824");
                orderBy = "SizeBytes DESC, FullPath ASC";
                break;
            case "inactive365":
                predicates.Add("ItemType = N'file'");
                predicates.Add("StatusName = N'active'");
                predicates.Add("COALESCE(AccessedUtc, ModifiedUtc, CreatedUtc) <= DATEADD(day, -365, SYSUTCDATETIME())");
                orderBy = "COALESCE(AccessedUtc, ModifiedUtc, CreatedUtc) ASC, SizeBytes DESC, FullPath ASC";
                break;
            case "executable":
                predicates.Add("ItemType = N'file'");
                predicates.Add("StatusName = N'active'");
                predicates.Add("Extension IN (N'.exe', N'.msi', N'.dll', N'.ps1', N'.bat', N'.cmd', N'.vbs', N'.js', N'.jar', N'.scr', N'.com')");
                orderBy = "SizeBytes DESC, FullPath ASC";
                break;
            case "errors":
                predicates.Add("(StatusName <> N'active' OR ErrorText IS NOT NULL)");
                orderBy = "FullPath ASC";
                break;
            default:
                predicates.Add("StatusName = N'active'");
                break;
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            predicates.Add("FullPath LIKE @Path");
            command.Parameters.AddWithValue("@Path", $"%{path.Trim()}%");
        }

        var normalizedExtension = NormalizeInventoryExtension(extension);
        if (!string.IsNullOrWhiteSpace(normalizedExtension))
        {
            predicates.Add("Extension = @Extension");
            command.Parameters.AddWithValue("@Extension", normalizedExtension);
        }

        command.CommandText = $$"""
            SELECT TOP (@Take)
                Id, SnapshotId, ScannedAtUtc, ServerName, ShareName, RootPath, FullPath,
                RelativePath, ItemName, ItemType, Extension, SizeBytes, Depth,
                CreatedUtc, ModifiedUtc, AccessedUtc, StatusName, ErrorText
            FROM dbo.FileInventoryItems
            WHERE {{string.Join(" AND ", predicates)}}
            ORDER BY {{orderBy}};
            """;
        command.Parameters.AddWithValue("@SnapshotId", snapshot.Id);
        command.Parameters.AddWithValue("@Take", Math.Clamp(take, 1, 500));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<FileInventoryItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadItem(reader));
        }

        return result;
    }

    public async Task<IReadOnlyCollection<FileInventorySnapshot>> GetSnapshotsAsync(
        string? server,
        string? share,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = BuildSnapshotsSql(command, server, share, take);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var snapshots = new List<FileInventorySnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            snapshots.Add(ReadSnapshot(reader));
        }

        return snapshots;
    }

    private async Task EnsureSchemaAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _schemaEnsured, 1, 0) != 0)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 300;
        command.CommandText = """
            IF OBJECT_ID(N'dbo.FileInventorySnapshots', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.FileInventorySnapshots
                (
                    Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_FileInventorySnapshots PRIMARY KEY,
                    ServerName NVARCHAR(128) NOT NULL,
                    ShareName NVARCHAR(128) NOT NULL,
                    RootPath NVARCHAR(1024) NOT NULL,
                    StartedUtc DATETIME2 NOT NULL,
                    FinishedUtc DATETIME2 NULL,
                    StatusName NVARCHAR(32) NOT NULL,
                    FileCount BIGINT NOT NULL CONSTRAINT DF_FileInventorySnapshots_FileCount DEFAULT 0,
                    FolderCount BIGINT NOT NULL CONSTRAINT DF_FileInventorySnapshots_FolderCount DEFAULT 0,
                    TotalBytes BIGINT NOT NULL CONSTRAINT DF_FileInventorySnapshots_TotalBytes DEFAULT 0,
                    ErrorCount BIGINT NOT NULL CONSTRAINT DF_FileInventorySnapshots_ErrorCount DEFAULT 0,
                    ErrorText NVARCHAR(2048) NULL
                );
            END;

            IF OBJECT_ID(N'dbo.FileInventoryItems', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.FileInventoryItems
                (
                    Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_FileInventoryItems PRIMARY KEY,
                    SnapshotId UNIQUEIDENTIFIER NOT NULL,
                    ScannedAtUtc DATETIME2 NOT NULL,
                    ServerName NVARCHAR(128) NOT NULL,
                    ShareName NVARCHAR(128) NOT NULL,
                    RootPath NVARCHAR(1024) NOT NULL,
                    FullPath NVARCHAR(2048) NOT NULL,
                    RelativePath NVARCHAR(2048) NOT NULL,
                    ItemName NVARCHAR(512) NOT NULL,
                    ItemType NVARCHAR(16) NOT NULL,
                    Extension NVARCHAR(64) NULL,
                    SizeBytes BIGINT NOT NULL,
                    Depth INT NOT NULL,
                    CreatedUtc DATETIME2 NULL,
                    ModifiedUtc DATETIME2 NULL,
                    AccessedUtc DATETIME2 NULL,
                    StatusName NVARCHAR(32) NOT NULL,
                    ErrorText NVARCHAR(2048) NULL,
                    CONSTRAINT FK_FileInventoryItems_Snapshot
                        FOREIGN KEY (SnapshotId) REFERENCES dbo.FileInventorySnapshots(Id)
                        ON DELETE CASCADE
                );
            END;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileInventorySnapshots_Latest' AND object_id = OBJECT_ID(N'dbo.FileInventorySnapshots'))
            BEGIN
                CREATE INDEX IX_FileInventorySnapshots_Latest
                    ON dbo.FileInventorySnapshots (ServerName, ShareName, RootPath, StartedUtc DESC)
                    INCLUDE (FinishedUtc, StatusName, FileCount, FolderCount, TotalBytes, ErrorCount);
            END;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileInventoryItems_Snapshot_Path' AND object_id = OBJECT_ID(N'dbo.FileInventoryItems'))
            BEGIN
                CREATE INDEX IX_FileInventoryItems_Snapshot_Path
                    ON dbo.FileInventoryItems (SnapshotId, FullPath)
                    INCLUDE (ItemType, Extension, SizeBytes, ModifiedUtc, AccessedUtc, StatusName);
            END;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileInventoryItems_Snapshot_Extension' AND object_id = OBJECT_ID(N'dbo.FileInventoryItems'))
            BEGIN
                CREATE INDEX IX_FileInventoryItems_Snapshot_Extension
                    ON dbo.FileInventoryItems (SnapshotId, Extension)
                    INCLUDE (ItemType, SizeBytes, StatusName);
            END;
            """;

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            Interlocked.Exchange(ref _schemaEnsured, 0);
            throw;
        }
    }

    private static async Task<FileInventorySnapshot?> FindSnapshotAsync(
        SqlConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1)
                Id, ServerName, ShareName, RootPath, StartedUtc, FinishedUtc, StatusName,
                FileCount, FolderCount, TotalBytes, ErrorCount, ErrorText
            FROM dbo.FileInventorySnapshots
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSnapshot(reader) : null;
    }

    private static async Task<FileInventorySnapshot?> FindLatestSnapshotAsync(
        SqlConnection connection,
        string? server,
        string? share,
        string? rootPath,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var predicates = new List<string> { "StatusName IN (N'completed', N'completed_with_errors')" };
        if (!string.IsNullOrWhiteSpace(server))
        {
            predicates.Add("ServerName LIKE @ServerName");
            command.Parameters.AddWithValue("@ServerName", $"%{server}%");
        }

        if (!string.IsNullOrWhiteSpace(share))
        {
            predicates.Add("ShareName LIKE @ShareName");
            command.Parameters.AddWithValue("@ShareName", $"%{share}%");
        }

        if (!string.IsNullOrWhiteSpace(rootPath))
        {
            predicates.Add("RootPath LIKE @RootPath");
            command.Parameters.AddWithValue("@RootPath", $"%{rootPath}%");
        }

        command.CommandText = $$"""
            SELECT TOP (1)
                Id, ServerName, ShareName, RootPath, StartedUtc, FinishedUtc, StatusName,
                FileCount, FolderCount, TotalBytes, ErrorCount, ErrorText
            FROM dbo.FileInventorySnapshots
            WHERE {{string.Join(" AND ", predicates)}}
            ORDER BY StartedUtc DESC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSnapshot(reader) : null;
    }

    private static async Task<FileInventorySnapshot?> FindPreviousSnapshotAsync(
        SqlConnection connection,
        FileInventorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1)
                Id, ServerName, ShareName, RootPath, StartedUtc, FinishedUtc, StatusName,
                FileCount, FolderCount, TotalBytes, ErrorCount, ErrorText
            FROM dbo.FileInventorySnapshots
            WHERE StatusName IN (N'completed', N'completed_with_errors')
              AND ServerName = @ServerName
              AND ShareName = @ShareName
              AND RootPath = @RootPath
              AND StartedUtc < @StartedUtc
            ORDER BY StartedUtc DESC;
            """;
        command.Parameters.AddWithValue("@ServerName", snapshot.Server);
        command.Parameters.AddWithValue("@ShareName", snapshot.Share);
        command.Parameters.AddWithValue("@RootPath", snapshot.RootPath);
        command.Parameters.AddWithValue("@StartedUtc", snapshot.StartedUtc.UtcDateTime);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSnapshot(reader) : null;
    }

    private static async Task<IReadOnlyCollection<FileInventoryItem>> QuerySnapshotItemsAsync(
        SqlConnection connection,
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                Id, SnapshotId, ScannedAtUtc, ServerName, ShareName, RootPath, FullPath,
                RelativePath, ItemName, ItemType, Extension, SizeBytes, Depth,
                CreatedUtc, ModifiedUtc, AccessedUtc, StatusName, ErrorText
            FROM dbo.FileInventoryItems
            WHERE SnapshotId = @SnapshotId;
            """;
        command.Parameters.AddWithValue("@SnapshotId", snapshotId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<FileInventoryItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    private static async Task<FileInventorySummary> BuildSqlSummaryAsync(
        SqlConnection connection,
        FileInventorySnapshot snapshot,
        FileInventorySnapshot? previousSnapshot,
        int top,
        CancellationToken cancellationToken)
    {
        var growth = previousSnapshot is null
            ? FileInventoryAnalyzer.BuildEmptyGrowthSummary()
            : await BuildSqlGrowthSummaryAsync(connection, snapshot, previousSnapshot, top, cancellationToken);
        var comparison = previousSnapshot is null
            ? FileInventoryAnalyzer.BuildEmptyCycleComparison()
            : FileInventoryAnalyzer.BuildCycleComparison(snapshot, previousSnapshot, growth);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 300;
        command.CommandText = """
            WITH ActiveFiles AS
            (
                SELECT SizeBytes, Extension, CreatedUtc, ModifiedUtc, AccessedUtc
                FROM dbo.FileInventoryItems
                WHERE SnapshotId = @SnapshotId
                  AND ItemType = N'file'
                  AND StatusName = N'active'
            )
            SELECT
                SUM(CASE WHEN COALESCE(AccessedUtc, ModifiedUtc, CreatedUtc) <= DATEADD(day, -180, SYSUTCDATETIME()) THEN 1 ELSE 0 END) AS Inactive180DaysFileCount,
                SUM(CASE WHEN COALESCE(AccessedUtc, ModifiedUtc, CreatedUtc) <= DATEADD(day, -180, SYSUTCDATETIME()) THEN SizeBytes ELSE 0 END) AS Inactive180DaysBytes,
                SUM(CASE WHEN COALESCE(AccessedUtc, ModifiedUtc, CreatedUtc) <= DATEADD(day, -365, SYSUTCDATETIME()) THEN 1 ELSE 0 END) AS Inactive365DaysFileCount,
                SUM(CASE WHEN COALESCE(AccessedUtc, ModifiedUtc, CreatedUtc) <= DATEADD(day, -365, SYSUTCDATETIME()) THEN SizeBytes ELSE 0 END) AS Inactive365DaysBytes,
                SUM(CASE WHEN AccessedUtc IS NULL THEN 1 ELSE 0 END) AS NeverAccessedFileCount,
                SUM(CASE WHEN AccessedUtc IS NULL THEN SizeBytes ELSE 0 END) AS NeverAccessedBytes,
                SUM(CASE WHEN SizeBytes >= 1073741824 THEN 1 ELSE 0 END) AS LargeFileCount,
                SUM(CASE WHEN SizeBytes >= 1073741824 THEN SizeBytes ELSE 0 END) AS LargeFileBytes,
                SUM(CASE WHEN Extension IN (N'.exe', N'.msi', N'.dll', N'.ps1', N'.bat', N'.cmd', N'.vbs', N'.js', N'.jar', N'.scr', N'.com') THEN 1 ELSE 0 END) AS ExecutableFileCount,
                SUM(CASE WHEN Extension IN (N'.exe', N'.msi', N'.dll', N'.ps1', N'.bat', N'.cmd', N'.vbs', N'.js', N'.jar', N'.scr', N'.com') THEN SizeBytes ELSE 0 END) AS ExecutableFileBytes
            FROM ActiveFiles;

            WITH ActiveItems AS
            (
                SELECT
                    ItemType,
                    SizeBytes,
                    CASE
                        WHEN ItemType = N'folder' THEN FullPath
                        WHEN CHARINDEX(N'\', REVERSE(FullPath)) > 0 THEN LEFT(FullPath, LEN(FullPath) - CHARINDEX(N'\', REVERSE(FullPath)))
                        ELSE FullPath
                    END AS ParentPath
                FROM dbo.FileInventoryItems
                WHERE SnapshotId = @SnapshotId
                  AND StatusName = N'active'
            )
            SELECT TOP (@Top)
                ParentPath,
                SUM(CASE WHEN ItemType = N'file' THEN 1 ELSE 0 END) AS FileCount,
                SUM(CASE WHEN ItemType = N'folder' THEN 1 ELSE 0 END) AS FolderCount,
                SUM(CASE WHEN ItemType = N'file' THEN SizeBytes ELSE 0 END) AS TotalBytes
            FROM ActiveItems
            GROUP BY ParentPath
            HAVING SUM(CASE WHEN ItemType = N'file' THEN 1 ELSE 0 END) > 0
                OR SUM(CASE WHEN ItemType = N'folder' THEN 1 ELSE 0 END) > 0
            ORDER BY TotalBytes DESC, ParentPath ASC;

            SELECT TOP (@Top)
                COALESCE(Extension, N'(sem extensao)') AS Extension,
                COUNT_BIG(*) AS FileCount,
                SUM(SizeBytes) AS TotalBytes
            FROM dbo.FileInventoryItems
            WHERE SnapshotId = @SnapshotId
              AND ItemType = N'file'
              AND StatusName = N'active'
            GROUP BY COALESCE(Extension, N'(sem extensao)')
            ORDER BY TotalBytes DESC, Extension ASC;

            WITH CategorizedFiles AS
            (
                SELECT
                    SizeBytes,
                    CASE
                        WHEN Extension IN (N'.doc', N'.docx', N'.odt', N'.rtf', N'.txt', N'.pdf', N'.xls', N'.xlsx', N'.ods', N'.csv', N'.ppt', N'.pptx', N'.odp') THEN N'documentos'
                        WHEN Extension IN (N'.jpg', N'.jpeg', N'.png', N'.gif', N'.bmp', N'.tif', N'.tiff', N'.webp', N'.svg') THEN N'imagens'
                        WHEN Extension IN (N'.mp4', N'.mov', N'.avi', N'.mkv', N'.wmv', N'.mpg', N'.mpeg') THEN N'videos'
                        WHEN Extension IN (N'.mp3', N'.wav', N'.wma', N'.aac', N'.flac', N'.ogg') THEN N'audio'
                        WHEN Extension IN (N'.zip', N'.rar', N'.7z', N'.tar', N'.gz', N'.bz2') THEN N'compactados'
                        WHEN Extension IN (N'.exe', N'.msi', N'.jar', N'.scr', N'.com', N'.rpm', N'.deb', N'.pkg', N'.plugin') THEN N'instaladores'
                        WHEN Extension IN (N'.ps1', N'.bat', N'.cmd', N'.vbs', N'.js') THEN N'scripts'
                        WHEN Extension IN (N'.dll', N'.sys') THEN N'binarios'
                        WHEN Extension IN (N'.kdbx') THEN N'dados sensiveis'
                        WHEN Extension IN (N'.dwg', N'.dxf') THEN N'projetos cad'
                        WHEN Extension IN (N'.bak', N'.sql', N'.db', N'.mdb', N'.accdb', N'.sqlite', N'.log') THEN N'dados'
                        WHEN Extension IN (N'.iso', N'.ova', N'.vhd', N'.vhdx', N'.vmdk') THEN N'imagens de disco'
                        ELSE N'outros'
                    END AS Category
                FROM dbo.FileInventoryItems
                WHERE SnapshotId = @SnapshotId
                  AND ItemType = N'file'
                  AND StatusName = N'active'
            )
            SELECT
                Category,
                COUNT_BIG(*) AS FileCount,
                SUM(SizeBytes) AS TotalBytes
            FROM CategorizedFiles
            GROUP BY Category
            ORDER BY TotalBytes DESC, Category ASC;

            SELECT TOP (@Top)
                FullPath,
                ItemName,
                Extension,
                SizeBytes,
                ModifiedUtc,
                AccessedUtc,
                DATEDIFF(day, COALESCE(ModifiedUtc, CreatedUtc, AccessedUtc), SYSUTCDATETIME()) AS AgeDays
            FROM dbo.FileInventoryItems
            WHERE SnapshotId = @SnapshotId
              AND ItemType = N'file'
              AND StatusName = N'active'
            ORDER BY SizeBytes DESC, FullPath ASC;

            SELECT TOP (@Top)
                FullPath,
                ItemName,
                Extension,
                SizeBytes,
                ModifiedUtc,
                AccessedUtc,
                DATEDIFF(day, COALESCE(ModifiedUtc, CreatedUtc, AccessedUtc), SYSUTCDATETIME()) AS AgeDays
            FROM dbo.FileInventoryItems
            WHERE SnapshotId = @SnapshotId
              AND ItemType = N'file'
              AND StatusName = N'active'
              AND COALESCE(ModifiedUtc, CreatedUtc, AccessedUtc) IS NOT NULL
            ORDER BY COALESCE(ModifiedUtc, CreatedUtc, AccessedUtc) ASC, SizeBytes DESC, FullPath ASC;

            SELECT TOP (@Top)
                FullPath,
                ItemName,
                Extension,
                SizeBytes,
                ModifiedUtc,
                AccessedUtc,
                DATEDIFF(day, COALESCE(ModifiedUtc, CreatedUtc, AccessedUtc), SYSUTCDATETIME()) AS AgeDays
            FROM dbo.FileInventoryItems
            WHERE SnapshotId = @SnapshotId
              AND ItemType = N'file'
              AND StatusName = N'active'
              AND Extension IN (N'.exe', N'.msi', N'.dll', N'.ps1', N'.bat', N'.cmd', N'.vbs', N'.js', N'.jar', N'.scr', N'.com')
            ORDER BY SizeBytes DESC, FullPath ASC;

            WITH ActiveFiles AS
            (
                SELECT SizeBytes, COALESCE(AccessedUtc, ModifiedUtc, CreatedUtc) AS ReferenceUtc
                FROM dbo.FileInventoryItems
                WHERE SnapshotId = @SnapshotId
                  AND ItemType = N'file'
                  AND StatusName = N'active'
            ),
            Buckets AS
            (
                SELECT N'0-30 dias' AS Label, 0 AS SortOrder, 0 AS MinDays, 30 AS MaxDays
                UNION ALL SELECT N'31-90 dias', 1, 31, 90
                UNION ALL SELECT N'91-180 dias', 2, 91, 180
                UNION ALL SELECT N'181-365 dias', 3, 181, 365
                UNION ALL SELECT N'+365 dias', 4, 366, NULL
            )
            SELECT
                Buckets.Label,
                COUNT_BIG(ActiveFiles.ReferenceUtc) AS FileCount,
                COALESCE(SUM(CASE WHEN ActiveFiles.ReferenceUtc IS NULL THEN 0 ELSE ActiveFiles.SizeBytes END), 0) AS TotalBytes
            FROM Buckets
            LEFT JOIN ActiveFiles
                ON ActiveFiles.ReferenceUtc IS NOT NULL
               AND DATEDIFF(day, ActiveFiles.ReferenceUtc, SYSUTCDATETIME()) >= Buckets.MinDays
               AND (Buckets.MaxDays IS NULL OR DATEDIFF(day, ActiveFiles.ReferenceUtc, SYSUTCDATETIME()) <= Buckets.MaxDays)
            GROUP BY Buckets.Label, Buckets.SortOrder
            ORDER BY Buckets.SortOrder;
            """;
        command.Parameters.AddWithValue("@SnapshotId", snapshot.Id);
        command.Parameters.AddWithValue("@Top", Math.Clamp(top, 1, 100));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var governance = await ReadInventoryGovernanceAsync(reader, cancellationToken);

        await reader.NextResultAsync(cancellationToken);
        var topFolders = await ReadTopFoldersAsync(reader, cancellationToken);

        await reader.NextResultAsync(cancellationToken);
        var topExtensions = await ReadTopExtensionsAsync(reader, cancellationToken);

        await reader.NextResultAsync(cancellationToken);
        var contentCategories = await ReadContentCategoriesAsync(reader, cancellationToken);

        await reader.NextResultAsync(cancellationToken);
        var topLargeFiles = await ReadFileCandidatesAsync(reader, cancellationToken);

        await reader.NextResultAsync(cancellationToken);
        var topInactiveFiles = await ReadFileCandidatesAsync(reader, cancellationToken);

        await reader.NextResultAsync(cancellationToken);
        var topExecutableFiles = await ReadFileCandidatesAsync(reader, cancellationToken);

        await reader.NextResultAsync(cancellationToken);
        var ageBuckets = await ReadAgeBucketsAsync(reader, cancellationToken);

        return new FileInventorySummary(
            SnapshotId: snapshot.Id,
            Server: snapshot.Server,
            Share: snapshot.Share,
            RootPath: snapshot.RootPath,
            StartedUtc: snapshot.StartedUtc,
            FinishedUtc: snapshot.FinishedUtc,
            Status: snapshot.Status,
            FileCount: snapshot.FileCount,
            FolderCount: snapshot.FolderCount,
            TotalBytes: snapshot.TotalBytes,
            ErrorCount: snapshot.ErrorCount,
            Governance: governance,
            TopFolders: topFolders,
            TopExtensions: topExtensions,
            ContentCategories: contentCategories,
            TopLargeFiles: topLargeFiles,
            TopInactiveFiles: topInactiveFiles,
            TopExecutableFiles: topExecutableFiles,
            AgeBuckets: ageBuckets,
            ObservedActivity: FileInventoryAnalyzer.BuildObservedActivitySummary(Array.Empty<FileInventoryObservedActivityInput>(), top),
            Growth: growth,
            Comparison: comparison,
            Insight: FileInventoryAnalyzer.BuildEmptyManagerialInsight(),
            ExecutiveOverview: FileInventoryAnalyzer.BuildEmptyExecutiveOverview(),
            Recommendations: BuildInventoryRecommendations(snapshot, governance));
    }

    private static async Task<FileInventoryGrowthSummary> BuildSqlGrowthSummaryAsync(
        SqlConnection connection,
        FileInventorySnapshot currentSnapshot,
        FileInventorySnapshot previousSnapshot,
        int top,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 300;
        command.CommandText = """
            WITH CurrentFiles AS
            (
                SELECT SizeBytes
                FROM dbo.FileInventoryItems
                WHERE SnapshotId = @CurrentSnapshotId
                  AND ItemType = N'file'
                  AND StatusName = N'active'
            ),
            CurrentFolders AS
            (
                SELECT Id
                FROM dbo.FileInventoryItems
                WHERE SnapshotId = @CurrentSnapshotId
                  AND ItemType = N'folder'
                  AND StatusName = N'active'
            ),
            PreviousFiles AS
            (
                SELECT SizeBytes
                FROM dbo.FileInventoryItems
                WHERE SnapshotId = @PreviousSnapshotId
                  AND ItemType = N'file'
                  AND StatusName = N'active'
            ),
            PreviousFolders AS
            (
                SELECT Id
                FROM dbo.FileInventoryItems
                WHERE SnapshotId = @PreviousSnapshotId
                  AND ItemType = N'folder'
                  AND StatusName = N'active'
            )
            SELECT
                (SELECT COUNT_BIG(*) FROM CurrentFiles) - (SELECT COUNT_BIG(*) FROM PreviousFiles) AS FileCountDelta,
                (SELECT COUNT_BIG(*) FROM CurrentFolders) - (SELECT COUNT_BIG(*) FROM PreviousFolders) AS FolderCountDelta,
                COALESCE((SELECT SUM(SizeBytes) FROM CurrentFiles), 0) - COALESCE((SELECT SUM(SizeBytes) FROM PreviousFiles), 0) AS TotalBytesDelta;

            WITH CurrentItems AS
            (
                SELECT
                    CASE
                        WHEN ItemType = N'folder' THEN FullPath
                        WHEN CHARINDEX(N'\', REVERSE(FullPath)) > 0 THEN LEFT(FullPath, LEN(FullPath) - CHARINDEX(N'\', REVERSE(FullPath)))
                        ELSE FullPath
                    END AS ParentPath,
                    ItemType,
                    SizeBytes
                FROM dbo.FileInventoryItems
                WHERE SnapshotId = @CurrentSnapshotId
                  AND StatusName = N'active'
            ),
            PreviousItems AS
            (
                SELECT
                    CASE
                        WHEN ItemType = N'folder' THEN FullPath
                        WHEN CHARINDEX(N'\', REVERSE(FullPath)) > 0 THEN LEFT(FullPath, LEN(FullPath) - CHARINDEX(N'\', REVERSE(FullPath)))
                        ELSE FullPath
                    END AS ParentPath,
                    ItemType,
                    SizeBytes
                FROM dbo.FileInventoryItems
                WHERE SnapshotId = @PreviousSnapshotId
                  AND StatusName = N'active'
            ),
            CurrentFoldersAgg AS
            (
                SELECT
                    ParentPath,
                    SUM(CASE WHEN ItemType = N'file' THEN 1 ELSE 0 END) AS FileCount,
                    SUM(CASE WHEN ItemType = N'folder' THEN 1 ELSE 0 END) AS FolderCount,
                    SUM(CASE WHEN ItemType = N'file' THEN SizeBytes ELSE 0 END) AS TotalBytes
                FROM CurrentItems
                GROUP BY ParentPath
            ),
            PreviousFoldersAgg AS
            (
                SELECT
                    ParentPath,
                    SUM(CASE WHEN ItemType = N'file' THEN 1 ELSE 0 END) AS FileCount,
                    SUM(CASE WHEN ItemType = N'folder' THEN 1 ELSE 0 END) AS FolderCount,
                    SUM(CASE WHEN ItemType = N'file' THEN SizeBytes ELSE 0 END) AS TotalBytes
                FROM PreviousItems
                GROUP BY ParentPath
            )
            SELECT TOP (@Top)
                COALESCE(CurrentFoldersAgg.ParentPath, PreviousFoldersAgg.ParentPath) AS ParentPath,
                COALESCE(CurrentFoldersAgg.FileCount, 0) - COALESCE(PreviousFoldersAgg.FileCount, 0) AS FileCountDelta,
                COALESCE(CurrentFoldersAgg.FolderCount, 0) - COALESCE(PreviousFoldersAgg.FolderCount, 0) AS FolderCountDelta,
                COALESCE(CurrentFoldersAgg.TotalBytes, 0) - COALESCE(PreviousFoldersAgg.TotalBytes, 0) AS TotalBytesDelta
            FROM CurrentFoldersAgg
            FULL OUTER JOIN PreviousFoldersAgg
                ON CurrentFoldersAgg.ParentPath = PreviousFoldersAgg.ParentPath
            WHERE COALESCE(CurrentFoldersAgg.FileCount, 0) - COALESCE(PreviousFoldersAgg.FileCount, 0) > 0
               OR COALESCE(CurrentFoldersAgg.FolderCount, 0) - COALESCE(PreviousFoldersAgg.FolderCount, 0) > 0
               OR COALESCE(CurrentFoldersAgg.TotalBytes, 0) - COALESCE(PreviousFoldersAgg.TotalBytes, 0) > 0
            ORDER BY TotalBytesDelta DESC, FileCountDelta DESC, ParentPath ASC;
            """;
        command.Parameters.AddWithValue("@CurrentSnapshotId", currentSnapshot.Id);
        command.Parameters.AddWithValue("@PreviousSnapshotId", previousSnapshot.Id);
        command.Parameters.AddWithValue("@Top", Math.Clamp(top, 1, 100));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var fileDelta = 0L;
        var folderDelta = 0L;
        var bytesDelta = 0L;
        if (await reader.ReadAsync(cancellationToken))
        {
            fileDelta = ReadInt64(reader, "FileCountDelta");
            folderDelta = ReadInt64(reader, "FolderCountDelta");
            bytesDelta = ReadInt64(reader, "TotalBytesDelta");
        }

        await reader.NextResultAsync(cancellationToken);
        var topGrowingFolders = new List<FileInventoryFolderGrowth>();
        while (await reader.ReadAsync(cancellationToken))
        {
            topGrowingFolders.Add(new FileInventoryFolderGrowth(
                Path: reader.GetString(reader.GetOrdinal("ParentPath")),
                FileCountDelta: ReadInt64(reader, "FileCountDelta"),
                FolderCountDelta: ReadInt64(reader, "FolderCountDelta"),
                TotalBytesDelta: ReadInt64(reader, "TotalBytesDelta")));
        }

        return new FileInventoryGrowthSummary(fileDelta, folderDelta, bytesDelta, topGrowingFolders);
    }

    private static async Task<FileInventoryGovernanceMetrics> ReadInventoryGovernanceAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new FileInventoryGovernanceMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        return new FileInventoryGovernanceMetrics(
            Inactive180DaysFileCount: ReadInt64(reader, "Inactive180DaysFileCount"),
            Inactive180DaysBytes: ReadInt64(reader, "Inactive180DaysBytes"),
            Inactive365DaysFileCount: ReadInt64(reader, "Inactive365DaysFileCount"),
            Inactive365DaysBytes: ReadInt64(reader, "Inactive365DaysBytes"),
            NeverAccessedFileCount: ReadInt64(reader, "NeverAccessedFileCount"),
            NeverAccessedBytes: ReadInt64(reader, "NeverAccessedBytes"),
            LargeFileCount: ReadInt64(reader, "LargeFileCount"),
            LargeFileBytes: ReadInt64(reader, "LargeFileBytes"),
            ExecutableFileCount: ReadInt64(reader, "ExecutableFileCount"),
            ExecutableFileBytes: ReadInt64(reader, "ExecutableFileBytes"));
    }

    private static async Task<IReadOnlyCollection<FileInventoryTopFolder>> ReadTopFoldersAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var result = new List<FileInventoryTopFolder>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new FileInventoryTopFolder(
                Path: reader.GetString(reader.GetOrdinal("ParentPath")),
                FileCount: ReadInt64(reader, "FileCount"),
                FolderCount: ReadInt64(reader, "FolderCount"),
                TotalBytes: ReadInt64(reader, "TotalBytes")));
        }

        return result;
    }

    private static async Task<IReadOnlyCollection<FileInventoryTopExtension>> ReadTopExtensionsAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var result = new List<FileInventoryTopExtension>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new FileInventoryTopExtension(
                Extension: reader.GetString(reader.GetOrdinal("Extension")),
                FileCount: ReadInt64(reader, "FileCount"),
                TotalBytes: ReadInt64(reader, "TotalBytes")));
        }

        return result;
    }

    private static async Task<IReadOnlyCollection<FileInventoryContentCategory>> ReadContentCategoriesAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var result = new List<FileInventoryContentCategory>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new FileInventoryContentCategory(
                Category: reader.GetString(reader.GetOrdinal("Category")),
                FileCount: ReadInt64(reader, "FileCount"),
                TotalBytes: ReadInt64(reader, "TotalBytes")));
        }

        return result;
    }

    private static async Task<IReadOnlyCollection<FileInventoryFileCandidate>> ReadFileCandidatesAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var result = new List<FileInventoryFileCandidate>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var ageOrdinal = reader.GetOrdinal("AgeDays");
            result.Add(new FileInventoryFileCandidate(
                Path: reader.GetString(reader.GetOrdinal("FullPath")),
                Name: reader.GetString(reader.GetOrdinal("ItemName")),
                Extension: ReadNullableString(reader, "Extension"),
                SizeBytes: ReadInt64(reader, "SizeBytes"),
                ModifiedUtc: ReadDateTimeOffset(reader, "ModifiedUtc"),
                AccessedUtc: ReadDateTimeOffset(reader, "AccessedUtc"),
                AgeDays: reader.IsDBNull(ageOrdinal) ? null : Convert.ToInt32(reader.GetValue(ageOrdinal))));
        }

        return result;
    }

    private static async Task<IReadOnlyCollection<FileInventoryAgeBucket>> ReadAgeBucketsAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var result = new List<FileInventoryAgeBucket>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new FileInventoryAgeBucket(
                Label: reader.GetString(reader.GetOrdinal("Label")),
                FileCount: ReadInt64(reader, "FileCount"),
                TotalBytes: ReadInt64(reader, "TotalBytes")));
        }

        return result;
    }

    private static IReadOnlyCollection<FileInventoryRecommendation> BuildInventoryRecommendations(
        FileInventorySnapshot snapshot,
        FileInventoryGovernanceMetrics metrics)
    {
        var recommendations = new List<FileInventoryRecommendation>();
        var totalBytes = snapshot.TotalBytes;
        var inactive365Percent = totalBytes == 0 ? 0 : metrics.Inactive365DaysBytes * 100m / totalBytes;
        var inactive180Percent = totalBytes == 0 ? 0 : metrics.Inactive180DaysBytes * 100m / totalBytes;

        if (metrics.Inactive365DaysFileCount > 0)
        {
            recommendations.Add(new FileInventoryRecommendation(
                Title: "Arquivos sem uso ha mais de 1 ano",
                Detail: $"{metrics.Inactive365DaysFileCount:N0} arquivo(s), {FormatInventoryBytes(metrics.Inactive365DaysBytes)} ({inactive365Percent:N1}% do volume) podem entrar em politica de arquivamento.",
                Severity: inactive365Percent >= 25 ? "warning" : "info"));
        }

        if (metrics.Inactive365DaysFileCount == 0 && metrics.Inactive180DaysFileCount > 0)
        {
            recommendations.Add(new FileInventoryRecommendation(
                Title: "Arquivos frios acima de 180 dias",
                Detail: $"{metrics.Inactive180DaysFileCount:N0} arquivo(s), {FormatInventoryBytes(metrics.Inactive180DaysBytes)} ({inactive180Percent:N1}% do volume) merecem revisao gerencial.",
                Severity: inactive180Percent >= 25 ? "warning" : "info"));
        }

        if (metrics.LargeFileCount > 0)
        {
            recommendations.Add(new FileInventoryRecommendation(
                Title: "Arquivos grandes concentrando espaco",
                Detail: $"{metrics.LargeFileCount:N0} arquivo(s) acima de 1 GB somam {FormatInventoryBytes(metrics.LargeFileBytes)}.",
                Severity: "info"));
        }

        if (metrics.ExecutableFileCount > 0)
        {
            recommendations.Add(new FileInventoryRecommendation(
                Title: "Executaveis e scripts no compartilhamento",
                Detail: $"{metrics.ExecutableFileCount:N0} arquivo(s) executavel(is) ou script(s) somam {FormatInventoryBytes(metrics.ExecutableFileBytes)}. Revise necessidade, localizacao e permissao.",
                Severity: "warning"));
        }

        if (snapshot.ErrorCount > 0)
        {
            recommendations.Add(new FileInventoryRecommendation(
                Title: "Itens sem leitura no scan",
                Detail: $"{snapshot.ErrorCount:N0} item(ns) nao puderam ser lidos. Revise permissao da conta de scan ou caminhos inacessiveis.",
                Severity: "warning"));
        }

        return recommendations
            .OrderByDescending(item => item.Severity == "warning")
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
    }

    private static string FormatInventoryBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        var value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:N0} {units[unit]}";
    }

    private static long ReadInt64(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt64(reader.GetValue(ordinal));
    }

    private static string BuildSnapshotsSql(SqlCommand command, string? server, string? share, int take)
    {
        command.Parameters.AddWithValue("@Take", take);
        var predicates = new List<string>();
        if (!string.IsNullOrWhiteSpace(server))
        {
            predicates.Add("ServerName LIKE @ServerName");
            command.Parameters.AddWithValue("@ServerName", $"%{server}%");
        }

        if (!string.IsNullOrWhiteSpace(share))
        {
            predicates.Add("ShareName LIKE @ShareName");
            command.Parameters.AddWithValue("@ShareName", $"%{share}%");
        }

        var where = predicates.Count == 0 ? "" : $"WHERE {string.Join(" AND ", predicates)}";
        return $$"""
            SELECT TOP (@Take)
                Id, ServerName, ShareName, RootPath, StartedUtc, FinishedUtc, StatusName,
                FileCount, FolderCount, TotalBytes, ErrorCount, ErrorText
            FROM dbo.FileInventorySnapshots
            {{where}}
            ORDER BY StartedUtc DESC;
            """;
    }

    private static string NormalizeInventoryItemKind(string? kind)
    {
        var normalized = string.IsNullOrWhiteSpace(kind) ? "all" : kind.Trim().ToLowerInvariant();
        return normalized switch
        {
            "large" or "grandes" => "large",
            "inactive365" or "inactive" or "inativos" => "inactive365",
            "executable" or "executables" or "scripts" or "executaveis" => "executable",
            "errors" or "erros" => "errors",
            _ => "all"
        };
    }

    private static string? NormalizeInventoryExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var normalized = extension.Trim().ToLowerInvariant();
        return normalized.StartsWith('.') ? normalized : $".{normalized}";
    }

    private static void AddItemParameters(SqlCommand command, FileInventoryItem item)
    {
        command.Parameters.AddWithValue("@Id", item.Id);
        command.Parameters.AddWithValue("@SnapshotId", item.SnapshotId);
        command.Parameters.AddWithValue("@ScannedAtUtc", item.ScannedAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("@ServerName", item.Server);
        command.Parameters.AddWithValue("@ShareName", item.Share);
        command.Parameters.AddWithValue("@RootPath", item.RootPath);
        command.Parameters.AddWithValue("@FullPath", item.Path);
        command.Parameters.AddWithValue("@RelativePath", item.RelativePath);
        command.Parameters.AddWithValue("@ItemName", item.Name);
        command.Parameters.AddWithValue("@ItemType", item.ItemType);
        command.Parameters.AddWithValue("@Extension", DbValue(item.Extension));
        command.Parameters.AddWithValue("@SizeBytes", item.SizeBytes);
        command.Parameters.AddWithValue("@Depth", item.Depth);
        command.Parameters.AddWithValue("@CreatedUtc", DbValue(item.CreatedUtc?.UtcDateTime));
        command.Parameters.AddWithValue("@ModifiedUtc", DbValue(item.ModifiedUtc?.UtcDateTime));
        command.Parameters.AddWithValue("@AccessedUtc", DbValue(item.AccessedUtc?.UtcDateTime));
        command.Parameters.AddWithValue("@StatusName", item.Status);
        command.Parameters.AddWithValue("@ErrorText", DbValue(item.Error));
    }

    private static FileInventorySnapshot ReadSnapshot(SqlDataReader reader)
    {
        return new FileInventorySnapshot(
            Id: reader.GetGuid(reader.GetOrdinal("Id")),
            Server: reader.GetString(reader.GetOrdinal("ServerName")),
            Share: reader.GetString(reader.GetOrdinal("ShareName")),
            RootPath: reader.GetString(reader.GetOrdinal("RootPath")),
            StartedUtc: ReadDateTimeOffset(reader, "StartedUtc")!.Value,
            FinishedUtc: ReadDateTimeOffset(reader, "FinishedUtc"),
            Status: reader.GetString(reader.GetOrdinal("StatusName")),
            FileCount: Convert.ToInt64(reader["FileCount"]),
            FolderCount: Convert.ToInt64(reader["FolderCount"]),
            TotalBytes: Convert.ToInt64(reader["TotalBytes"]),
            ErrorCount: Convert.ToInt64(reader["ErrorCount"]),
            Error: ReadNullableString(reader, "ErrorText"));
    }

    private static FileInventoryItem ReadItem(SqlDataReader reader)
    {
        return new FileInventoryItem(
            Id: reader.GetGuid(reader.GetOrdinal("Id")),
            SnapshotId: reader.GetGuid(reader.GetOrdinal("SnapshotId")),
            ScannedAtUtc: ReadDateTimeOffset(reader, "ScannedAtUtc")!.Value,
            Server: reader.GetString(reader.GetOrdinal("ServerName")),
            Share: reader.GetString(reader.GetOrdinal("ShareName")),
            RootPath: reader.GetString(reader.GetOrdinal("RootPath")),
            Path: reader.GetString(reader.GetOrdinal("FullPath")),
            RelativePath: reader.GetString(reader.GetOrdinal("RelativePath")),
            Name: reader.GetString(reader.GetOrdinal("ItemName")),
            ItemType: reader.GetString(reader.GetOrdinal("ItemType")),
            Extension: ReadNullableString(reader, "Extension"),
            SizeBytes: Convert.ToInt64(reader["SizeBytes"]),
            Depth: Convert.ToInt32(reader["Depth"]),
            CreatedUtc: ReadDateTimeOffset(reader, "CreatedUtc"),
            ModifiedUtc: ReadDateTimeOffset(reader, "ModifiedUtc"),
            AccessedUtc: ReadDateTimeOffset(reader, "AccessedUtc"),
            Status: reader.GetString(reader.GetOrdinal("StatusName")),
            Error: ReadNullableString(reader, "ErrorText"));
    }

    private static DateTimeOffset? ReadDateTimeOffset(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal)
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
    }

    private static string? ReadNullableString(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;
}

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

        if (!string.IsNullOrWhiteSpace(query.Share))
        {
            predicates.Add("ShareName LIKE @Share");
            command.Parameters.AddWithValue("@Share", $"%{query.Share}%");
        }

        if (!string.IsNullOrWhiteSpace(query.User))
        {
            predicates.Add("UserName LIKE @User");
            command.Parameters.AddWithValue("@User", $"%{query.User}%");
        }

        var actions = SplitFilterValues(query.Action)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (actions.Length == 1)
        {
            predicates.Add("ActionName = @Action");
            command.Parameters.AddWithValue("@Action", actions[0]);
        }
        else if (actions.Length > 1)
        {
            var parameterNames = new List<string>();
            for (var index = 0; index < actions.Length; index++)
            {
                var parameterName = $"@Action{index}";
                parameterNames.Add(parameterName);
                command.Parameters.AddWithValue(parameterName, actions[index]);
            }

            predicates.Add($"ActionName IN ({string.Join(", ", parameterNames)})");
        }

        if (!string.IsNullOrWhiteSpace(query.Path))
        {
            predicates.Add("(FullPath LIKE @Path OR PreviousPath LIKE @Path)");
            command.Parameters.AddWithValue("@Path", $"%{query.Path}%");
        }

        if (!string.IsNullOrWhiteSpace(query.SourceHost))
        {
            predicates.Add("SourceHost LIKE @SourceHost");
            command.Parameters.AddWithValue("@SourceHost", $"%{query.SourceHost}%");
        }

        if (!string.IsNullOrWhiteSpace(query.SourceIp))
        {
            predicates.Add("SourceIp LIKE @SourceIp");
            command.Parameters.AddWithValue("@SourceIp", $"%{query.SourceIp}%");
        }

        var extensions = SplitFilterValues(query.Extension)
            .Select(NormalizeExtensionFilter)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (extensions.Length > 0)
        {
            var parameterNames = new List<string>();
            for (var index = 0; index < extensions.Length; index++)
            {
                var parameterName = $"@Extension{index}";
                parameterNames.Add(parameterName);
                command.Parameters.AddWithValue(parameterName, extensions[index]);
            }

            predicates.Add($"Extension IN ({string.Join(", ", parameterNames)})");
        }

        if (!string.IsNullOrWhiteSpace(query.Result))
        {
            predicates.Add("ResultName LIKE @Result");
            command.Parameters.AddWithValue("@Result", $"%{query.Result}%");
        }

        if (!string.IsNullOrWhiteSpace(query.Severity))
        {
            predicates.Add("Severity = @Severity");
            command.Parameters.AddWithValue("@Severity", query.Severity);
        }

        if (!string.IsNullOrWhiteSpace(query.Source))
        {
            predicates.Add("SourceName LIKE @Source");
            command.Parameters.AddWithValue("@Source", $"%{query.Source}%");
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

    private static IEnumerable<string> SplitFilterValues(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static string NormalizeExtensionFilter(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith(".", StringComparison.Ordinal)
            ? trimmed.ToLowerInvariant()
            : $".{trimmed.ToLowerInvariant()}";
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

internal sealed class InMemoryTimelineRepository : ITimelineRepository
{
    private readonly ConcurrentDictionary<Guid, FileAuditDisplayEvent> _events = new();

    public Task ReplaceWindowAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<FileAuditDisplayEvent> events,
        string correlationVersion,
        CancellationToken cancellationToken)
    {
        var idsToRemove = _events.Values
            .Where(item => item.TimestampUtc >= fromUtc && item.TimestampUtc <= toUtc)
            .Select(item => item.Id)
            .ToArray();

        foreach (var id in idsToRemove)
        {
            _events.TryRemove(id, out _);
        }

        foreach (var item in events)
        {
            _events[item.Id] = item;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<FileAuditDisplayEvent>> QueryAsync(TimelineQuery query, CancellationToken cancellationToken)
    {
        IEnumerable<FileAuditDisplayEvent> events = _events.Values;

        if (!string.IsNullOrWhiteSpace(query.Server))
        {
            events = events.Where(item => item.Server.Contains(query.Server, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Share))
        {
            events = events.Where(item => item.Share.Contains(query.Share, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.User))
        {
            events = events.Where(item => item.User.Contains(query.User, StringComparison.OrdinalIgnoreCase));
        }

        var actions = BuildTimelineResultActionFilter(query.Action);
        if (actions.Length > 0)
        {
            events = events.Where(item => actions.Contains(item.Action, StringComparer.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Path))
        {
            events = events.Where(item =>
                item.Path.Contains(query.Path, StringComparison.OrdinalIgnoreCase)
                || (item.PreviousPath?.Contains(query.Path, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (!string.IsNullOrWhiteSpace(query.SourceHost))
        {
            events = events.Where(item => item.SourceHost?.Contains(query.SourceHost, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        if (!string.IsNullOrWhiteSpace(query.SourceIp))
        {
            events = events.Where(item => item.SourceIp?.Contains(query.SourceIp, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        var extensions = SplitFilterValues(query.Extension)
            .Select(NormalizeExtensionFilter)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (extensions.Length > 0)
        {
            events = events.Where(item => item.Extension is not null && extensions.Contains(item.Extension, StringComparer.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Result))
        {
            events = events.Where(item => item.Result.Contains(query.Result, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Severity))
        {
            events = events.Where(item => item.Severity.Equals(query.Severity, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Source))
        {
            events = events.Where(item => item.Source.Contains(query.Source, StringComparison.OrdinalIgnoreCase));
        }

        if (query.FromUtc is not null)
        {
            events = events.Where(item => item.TimestampUtc >= query.FromUtc);
        }

        if (query.ToUtc is not null)
        {
            events = events.Where(item => item.TimestampUtc <= query.ToUtc);
        }

        IReadOnlyCollection<FileAuditDisplayEvent> result = events
            .OrderByDescending(item => item.TimestampUtc)
            .Take(query.Take)
            .ToArray();

        return Task.FromResult(result);
    }

    public Task<IReadOnlyCollection<FileAuditDisplayEvent>> QueryKnownLiveDescendantsAsync(
        string previousRoot,
        DateTimeOffset beforeUtc,
        int take,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = FileInventoryNormalizer.NormalizePath(previousRoot).TrimEnd('\\');
        if (string.IsNullOrWhiteSpace(normalizedRoot))
        {
            return Task.FromResult<IReadOnlyCollection<FileAuditDisplayEvent>>(Array.Empty<FileAuditDisplayEvent>());
        }

        var candidates = _events.Values
            .Where(item => item.TimestampUtc < beforeUtc)
            .Where(item => item.ObjectType != "folder")
            .Where(item => FileInventoryNormalizer.NormalizePath(item.Path).StartsWith($"{normalizedRoot}\\", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => FileInventoryNormalizer.NormalizePath(item.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.TimestampUtc).First())
            .Where(item => item.Action != "deleted")
            .Where(item => !_events.Values.Any(terminal =>
                terminal.TimestampUtc > item.TimestampUtc
                && terminal.TimestampUtc < beforeUtc
                && (
                    (FileInventoryNormalizer.NormalizePath(terminal.Path).Equals(FileInventoryNormalizer.NormalizePath(item.Path), StringComparison.OrdinalIgnoreCase)
                        && terminal.Action == "deleted")
                    || (FileInventoryNormalizer.NormalizePath(terminal.PreviousPath ?? "").Equals(FileInventoryNormalizer.NormalizePath(item.Path), StringComparison.OrdinalIgnoreCase)
                        && terminal.Action is "moved" or "renamed" or "deleted"))))
            .OrderByDescending(item => item.TimestampUtc)
            .Take(Math.Clamp(take, 1, 10_000))
            .ToArray();

        return Task.FromResult<IReadOnlyCollection<FileAuditDisplayEvent>>(candidates);
    }

    public Task<ActivitySummaryResponse> GetActivitySummaryAsync(
        ActivitySummaryQuery query,
        CancellationToken cancellationToken)
    {
        var events = FilterForSummary(query.FromUtc, query.ToUtc, query.Server, query.Share, query.User, query.Action).ToArray();

        return Task.FromResult(new ActivitySummaryResponse(
            FromUtc: query.FromUtc,
            ToUtc: query.ToUtc,
            TotalEvents: events.Length,
            ByAction: SummarizeTimeline(events, item => item.DisplayAction, query.Take),
            ByShare: SummarizeTimeline(events, item => item.Share, query.Take),
            ByUser: SummarizeTimeline(events, item => item.User, query.Take)));
    }

    public Task<FileInventoryObservedActivitySummary> GetObservedActivitySummaryAsync(
        string? server,
        string? share,
        string? rootPath,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int top,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = string.IsNullOrWhiteSpace(rootPath)
            ? null
            : FileInventoryNormalizer.NormalizePath(rootPath);
        var events = _events.Values
            .Where(item => item.TimestampUtc >= fromUtc && item.TimestampUtc <= toUtc)
            .Where(item => MatchesText(item.Server, server))
            .Where(item => MatchesText(item.Share, share))
            .Where(item => normalizedRoot is null
                || item.Path.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || item.Path.StartsWith($"{normalizedRoot}\\", StringComparison.OrdinalIgnoreCase))
            .Select(item => new FileInventoryObservedActivityInput(
                item.TimestampUtc,
                item.Path,
                item.User,
                item.DisplayAction))
            .ToArray();

        return Task.FromResult(FileInventoryAnalyzer.BuildObservedActivitySummary(events, top));
    }

    public Task<BaselineAnomalyResponse> GetBaselineAnomaliesAsync(
        BaselineAnomalyQuery query,
        CancellationToken cancellationToken)
    {
        var current = FilterForSummary(query.FromUtc, query.ToUtc, query.Server, query.Share, query.User, query.Action).ToArray();
        var windowSize = query.ToUtc - query.FromUtc;
        var baselineEvents = new List<FileAuditDisplayEvent>();

        for (var index = 1; index <= query.BaselineWindows; index++)
        {
            var windowTo = query.FromUtc - TimeSpan.FromTicks(windowSize.Ticks * (index - 1));
            var windowFrom = windowTo - windowSize;
            baselineEvents.AddRange(FilterForSummary(windowFrom, windowTo, query.Server, query.Share, query.User, query.Action));
        }

        return Task.FromResult(new BaselineAnomalyResponse(
            FromUtc: query.FromUtc,
            ToUtc: query.ToUtc,
            BaselineWindows: query.BaselineWindows,
            ByAction: BaselineAnomalyCalculator.Build(
                CountTimelineBy(current, item => item.DisplayAction),
                CountTimelineBy(baselineEvents, item => item.DisplayAction),
                query.BaselineWindows,
                query.Take),
            ByShare: BaselineAnomalyCalculator.Build(
                CountTimelineBy(current, item => item.Share),
                CountTimelineBy(baselineEvents, item => item.Share),
                query.BaselineWindows,
                query.Take),
            ByUser: BaselineAnomalyCalculator.Build(
                CountTimelineBy(current, item => item.User),
                CountTimelineBy(baselineEvents, item => item.User),
                query.BaselineWindows,
                query.Take)));
    }

    public Task<TimelineCoverage> GetCoverageAsync(CancellationToken cancellationToken)
    {
        var values = _events.Values.ToArray();
        if (values.Length == 0)
        {
            return Task.FromResult(new TimelineCoverage(0, null, null));
        }

        return Task.FromResult(new TimelineCoverage(
            Count: values.LongLength,
            FromUtc: values.Min(item => item.TimestampUtc),
            ToUtc: values.Max(item => item.TimestampUtc)));
    }

    public Task<int> PurgeOlderThanAsync(
        DateTimeOffset cutoffUtc,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var deleted = 0;
        var idsToRemove = _events.Values
            .Where(item => item.TimestampUtc < cutoffUtc)
            .Select(item => item.Id)
            .ToArray();

        foreach (var id in idsToRemove)
        {
            if (_events.TryRemove(id, out _))
            {
                deleted++;
            }
        }

        return Task.FromResult(deleted);
    }

    private static IEnumerable<string> SplitFilterValues(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static string NormalizeExtensionFilter(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith(".", StringComparison.Ordinal)
            ? trimmed.ToLowerInvariant()
            : $".{trimmed.ToLowerInvariant()}";
    }

    private static string[] BuildTimelineResultActionFilter(string? action)
    {
        return action?.Trim().ToLowerInvariant() switch
        {
            "created" => new[] { "created", "created_or_appended" },
            "modified" => new[] { "modified", "changed" },
            "accessed" => new[] { "accessed" },
            "deleted" => new[] { "deleted" },
            "renamed" => new[] { "renamed" },
            "moved" => new[] { "moved" },
            "permission_changed" => new[] { "permission_changed" },
            _ => Array.Empty<string>()
        };
    }

    private IEnumerable<FileAuditDisplayEvent> FilterForSummary(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string? server,
        string? share,
        string? user,
        string? action)
    {
        IEnumerable<FileAuditDisplayEvent> events = _events.Values
            .Where(item => item.TimestampUtc >= fromUtc)
            .Where(item => item.TimestampUtc <= toUtc)
            .Where(item => MatchesText(item.Server, server))
            .Where(item => MatchesText(item.Share, share))
            .Where(item => MatchesText(item.User, user));

        var actions = BuildTimelineResultActionFilter(action);
        if (actions.Length > 0)
        {
            events = events.Where(item => actions.Contains(item.Action, StringComparer.OrdinalIgnoreCase));
        }

        return events;
    }

    private static IReadOnlyCollection<ActivitySummaryItem> SummarizeTimeline(
        IEnumerable<FileAuditDisplayEvent> events,
        Func<FileAuditDisplayEvent, string> selector,
        int take)
    {
        return events
            .GroupBy(selector, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ActivitySummaryItem(group.Key, group.LongCount()))
            .OrderByDescending(item => item.EventCount)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .ToArray();
    }

    private static Dictionary<string, long> CountTimelineBy(
        IEnumerable<FileAuditDisplayEvent> events,
        Func<FileAuditDisplayEvent, string> selector)
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
}

internal sealed class InMemoryInventoryRepository : IInventoryRepository
{
    private readonly ConcurrentDictionary<Guid, FileInventorySnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, FileInventoryItem>> _items = new();

    public Task<FileInventorySnapshot> StartSnapshotAsync(FileInventorySnapshot snapshot, CancellationToken cancellationToken)
    {
        _snapshots[snapshot.Id] = snapshot;
        _items[snapshot.Id] = new ConcurrentDictionary<Guid, FileInventoryItem>();
        return Task.FromResult(snapshot);
    }

    public Task AddBatchAsync(Guid snapshotId, IReadOnlyCollection<FileInventoryItem> items, CancellationToken cancellationToken)
    {
        var bucket = _items.GetOrAdd(snapshotId, _ => new ConcurrentDictionary<Guid, FileInventoryItem>());
        foreach (var item in items)
        {
            bucket[item.Id] = item with { SnapshotId = snapshotId };
        }

        return Task.CompletedTask;
    }

    public Task<FileInventorySnapshot?> CompleteSnapshotAsync(
        Guid snapshotId,
        string status,
        string? error,
        CancellationToken cancellationToken)
    {
        if (!_snapshots.TryGetValue(snapshotId, out var snapshot))
        {
            return Task.FromResult<FileInventorySnapshot?>(null);
        }

        var items = _items.TryGetValue(snapshotId, out var bucket)
            ? bucket.Values.ToArray()
            : Array.Empty<FileInventoryItem>();
        var files = items.Where(item => item.ItemType == "file" && item.Status == "active").ToArray();
        var folders = items.Where(item => item.ItemType == "folder" && item.Status == "active").ToArray();
        var completed = snapshot with
        {
            FinishedUtc = DateTimeOffset.UtcNow,
            Status = string.IsNullOrWhiteSpace(status) ? "completed" : status.Trim(),
            FileCount = files.LongLength,
            FolderCount = folders.LongLength,
            TotalBytes = files.Sum(item => item.SizeBytes),
            ErrorCount = items.LongCount(item => item.Status == "error"),
            Error = string.IsNullOrWhiteSpace(error) ? null : error.Trim()
        };
        _snapshots[snapshotId] = completed;

        return Task.FromResult<FileInventorySnapshot?>(completed);
    }

    public Task<FileInventorySummary> GetLatestSummaryAsync(
        string? server,
        string? share,
        string? rootPath,
        int top,
        CancellationToken cancellationToken)
    {
        var snapshot = _snapshots.Values
            .Where(item => item.Status is "completed" or "completed_with_errors")
            .Where(item => MatchesText(item.Server, server))
            .Where(item => MatchesText(item.Share, share))
            .Where(item => MatchesText(item.RootPath, rootPath))
            .OrderByDescending(item => item.StartedUtc)
            .FirstOrDefault();
        if (snapshot is null)
        {
            return Task.FromResult(FileInventoryAnalyzer.BuildSummary(null, Array.Empty<FileInventoryItem>(), top));
        }

        var items = _items.TryGetValue(snapshot.Id, out var bucket)
            ? bucket.Values.ToArray()
            : Array.Empty<FileInventoryItem>();
        var previousSnapshot = _snapshots.Values
            .Where(item => item.Status is "completed" or "completed_with_errors")
            .Where(item => item.Server.Equals(snapshot.Server, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.Share.Equals(snapshot.Share, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.RootPath.Equals(snapshot.RootPath, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.StartedUtc < snapshot.StartedUtc)
            .OrderByDescending(item => item.StartedUtc)
            .FirstOrDefault();
        var previousItems = previousSnapshot is not null && _items.TryGetValue(previousSnapshot.Id, out var previousBucket)
            ? previousBucket.Values.ToArray()
            : Array.Empty<FileInventoryItem>();
        var growth = previousSnapshot is null
            ? FileInventoryAnalyzer.BuildEmptyGrowthSummary()
            : FileInventoryAnalyzer.BuildGrowthSummary(items, previousItems, top);
        var comparison = previousSnapshot is null
            ? FileInventoryAnalyzer.BuildEmptyCycleComparison()
            : FileInventoryAnalyzer.BuildCycleComparison(snapshot, previousSnapshot, growth);

        return Task.FromResult(FileInventoryAnalyzer.BuildSummary(snapshot, items, top) with
        {
            Growth = growth,
            Comparison = comparison
        });
    }

    public Task<IReadOnlyCollection<FileInventoryItem>> QueryLatestItemsAsync(
        string? server,
        string? share,
        string? rootPath,
        string? kind,
        string? path,
        string? extension,
        int take,
        CancellationToken cancellationToken)
    {
        var snapshot = _snapshots.Values
            .Where(item => item.Status is "completed" or "completed_with_errors")
            .Where(item => MatchesText(item.Server, server))
            .Where(item => MatchesText(item.Share, share))
            .Where(item => MatchesText(item.RootPath, rootPath))
            .OrderByDescending(item => item.StartedUtc)
            .FirstOrDefault();
        if (snapshot is null || !_items.TryGetValue(snapshot.Id, out var bucket))
        {
            return Task.FromResult<IReadOnlyCollection<FileInventoryItem>>(Array.Empty<FileInventoryItem>());
        }

        var normalizedKind = NormalizeInventoryItemKind(kind);
        var normalizedExtension = NormalizeInventoryExtension(extension);
        IEnumerable<FileInventoryItem> query = bucket.Values;

        query = normalizedKind switch
        {
            "large" => query
                .Where(item => item.ItemType == "file" && item.Status == "active" && item.SizeBytes >= 1024L * 1024L * 1024L)
                .OrderByDescending(item => item.SizeBytes)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase),
            "inactive365" => query
                .Where(item => item.ItemType == "file" && item.Status == "active" && IsInactiveForDays(item, 365))
                .OrderBy(item => item.AccessedUtc ?? item.ModifiedUtc ?? item.CreatedUtc)
                .ThenByDescending(item => item.SizeBytes)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase),
            "executable" => query
                .Where(item => item.ItemType == "file" && item.Status == "active" && IsExecutableOrScriptExtension(item.Extension))
                .OrderByDescending(item => item.SizeBytes)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase),
            "errors" => query
                .Where(item => item.Status != "active" || !string.IsNullOrWhiteSpace(item.Error))
                .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase),
            _ => query
                .Where(item => item.Status == "active")
                .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
        };

        if (!string.IsNullOrWhiteSpace(path))
        {
            query = query.Where(item => item.Path.Contains(path.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(normalizedExtension))
        {
            query = query.Where(item => string.Equals(item.Extension, normalizedExtension, StringComparison.OrdinalIgnoreCase));
        }

        return Task.FromResult<IReadOnlyCollection<FileInventoryItem>>(query.Take(Math.Clamp(take, 1, 500)).ToArray());
    }

    public Task<IReadOnlyCollection<FileInventorySnapshot>> GetSnapshotsAsync(
        string? server,
        string? share,
        int take,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<FileInventorySnapshot> snapshots = _snapshots.Values
            .Where(item => MatchesText(item.Server, server))
            .Where(item => MatchesText(item.Share, share))
            .OrderByDescending(item => item.StartedUtc)
            .Take(take)
            .ToArray();

        return Task.FromResult(snapshots);
    }

    private static bool MatchesText(string value, string? filter)
    {
        return string.IsNullOrWhiteSpace(filter)
            || value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeInventoryItemKind(string? kind)
    {
        var normalized = string.IsNullOrWhiteSpace(kind) ? "all" : kind.Trim().ToLowerInvariant();
        return normalized switch
        {
            "large" or "grandes" => "large",
            "inactive365" or "inactive" or "inativos" => "inactive365",
            "executable" or "executables" or "scripts" or "executaveis" => "executable",
            "errors" or "erros" => "errors",
            _ => "all"
        };
    }

    private static string? NormalizeInventoryExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var normalized = extension.Trim().ToLowerInvariant();
        return normalized.StartsWith('.') ? normalized : $".{normalized}";
    }

    private static bool IsInactiveForDays(FileInventoryItem item, int days)
    {
        var reference = item.AccessedUtc ?? item.ModifiedUtc ?? item.CreatedUtc;
        return reference is not null && reference.Value <= DateTimeOffset.UtcNow.AddDays(-days);
    }

    private static bool IsExecutableOrScriptExtension(string? extension)
    {
        return extension is ".exe" or ".msi" or ".dll" or ".ps1" or ".bat" or ".cmd" or ".vbs" or ".js" or ".jar" or ".scr" or ".com";
    }
}

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

        if (!string.IsNullOrWhiteSpace(query.Share))
        {
            events = events.Where(item => item.Share.Contains(query.Share, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.User))
        {
            events = events.Where(item => item.User.Contains(query.User, StringComparison.OrdinalIgnoreCase));
        }

        var actions = SplitFilterValues(query.Action)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (actions.Length > 0)
        {
            events = events.Where(item => actions.Contains(item.Action, StringComparer.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Path))
        {
            events = events.Where(item =>
                item.Path.Contains(query.Path, StringComparison.OrdinalIgnoreCase)
                || (item.PreviousPath?.Contains(query.Path, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (!string.IsNullOrWhiteSpace(query.SourceHost))
        {
            events = events.Where(item => item.SourceHost?.Contains(query.SourceHost, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        if (!string.IsNullOrWhiteSpace(query.SourceIp))
        {
            events = events.Where(item => item.SourceIp?.Contains(query.SourceIp, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        var extensions = SplitFilterValues(query.Extension)
            .Select(NormalizeExtensionFilter)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (extensions.Length > 0)
        {
            events = events.Where(item => item.Extension is not null && extensions.Contains(item.Extension, StringComparer.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Result))
        {
            events = events.Where(item => item.Result.Contains(query.Result, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Severity))
        {
            events = events.Where(item => item.Severity.Equals(query.Severity, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Source))
        {
            events = events.Where(item => item.Source.Contains(query.Source, StringComparison.OrdinalIgnoreCase));
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

    private static IEnumerable<string> SplitFilterValues(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static string NormalizeExtensionFilter(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith(".", StringComparison.Ordinal)
            ? trimmed.ToLowerInvariant()
            : $".{trimmed.ToLowerInvariant()}";
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
    private readonly int _sendLagWarningMinutes;
#if SQLSERVER
    private readonly bool _persistHeartbeats;
    private readonly string? _connectionString;
#endif

    public AgentHealthStore(IConfiguration configuration)
    {
        _staleMinutes = configuration.GetValue("Agents:StaleMinutes", 10);
        _backlogWarningThreshold = configuration.GetValue("Agents:BacklogWarningThreshold", 1000);
        _sendLagWarningMinutes = configuration.GetValue("Agents:SendLagWarningMinutes", 15);
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
        var now = DateTimeOffset.UtcNow;
        var operationalHealth = FileServerMonitor.Core.AgentOperationalHealth.Evaluate(
            new FileServerMonitor.Core.AgentOperationalHealthInput(
                Status: agent.Status,
                LastHeartbeatUtc: agent.LastHeartbeatUtc,
                LastSuccessfulSendUtc: agent.LastSuccessfulSendUtc,
                PendingQueueEvents: agent.PendingQueueEvents,
                LastCycle: agent.LastCycle,
                NowUtc: now,
                StaleAfterMinutes: _staleMinutes,
                BacklogWarningThreshold: _backlogWarningThreshold,
                SendLagWarningMinutes: _sendLagWarningMinutes));
        var lastCollectedEventAgeSeconds = GetAgeSecondsForAgent(now, agent.LastCollectedEventUtc);
        var normalized = agent with
        {
            OperationalStatus = operationalHealth.Level,
            OperationalMessage = operationalHealth.Reason,
            LastHeartbeatAgeSeconds = operationalHealth.LastHeartbeatAgeSeconds,
            LastSuccessfulSendAgeSeconds = operationalHealth.LastSuccessfulSendAgeSeconds,
            LastCollectedEventAgeSeconds = lastCollectedEventAgeSeconds,
            HasCycleError = operationalHealth.HasError,
            StaleAfterMinutes = _staleMinutes,
            BacklogWarningThreshold = _backlogWarningThreshold
        };

        if (agent.LastHeartbeatUtc is null)
        {
            return normalized with
            {
                Status = "stale",
                IsStale = true,
                Message = agent.Message ?? "Agente sem heartbeat registrado."
            };
        }

        var minutesSinceHeartbeat = now.Subtract(agent.LastHeartbeatUtc.Value).TotalMinutes;

        if (minutesSinceHeartbeat > Math.Max(1, _staleMinutes))
        {
            return normalized with
            {
                Status = "stale",
                IsStale = true,
                Message = agent.Message ?? $"Sem heartbeat ha {Math.Floor(minutesSinceHeartbeat)} minuto(s)."
            };
        }

        if (agent.PendingQueueEvents >= Math.Max(1, _backlogWarningThreshold))
        {
            return normalized with
            {
                Status = "backlog",
                IsStale = false,
                Message = agent.Message ?? $"Fila local com {agent.PendingQueueEvents} evento(s) pendente(s)."
            };
        }

        return normalized with
        {
            IsStale = false
        };
    }

    private static long? GetAgeSecondsForAgent(DateTimeOffset now, DateTimeOffset? value)
    {
        return value is null
            ? null
            : Math.Max(0, (long)now.Subtract(value.Value).TotalSeconds);
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
                    LastCollectedEventUtc = @LastCollectedEventUtc,
                    LastCycleStartedUtc = @LastCycleStartedUtc,
                    LastCycleFinishedUtc = @LastCycleFinishedUtc,
                    LastCycleDurationMs = @LastCycleDurationMs,
                    LastCycleSecurityEventsRead = @LastCycleSecurityEventsRead,
                    LastCycleUsnEventsRead = @LastCycleUsnEventsRead,
                    LastCycleCorrelatedEvents = @LastCycleCorrelatedEvents,
                    LastCycleSentEvents = @LastCycleSentEvents,
                    LastCycleQueuedEvents = @LastCycleQueuedEvents,
                    LastCycleError = @LastCycleError,
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
                    LastCollectedEventUtc,
                    LastCycleStartedUtc,
                    LastCycleFinishedUtc,
                    LastCycleDurationMs,
                    LastCycleSecurityEventsRead,
                    LastCycleUsnEventsRead,
                    LastCycleCorrelatedEvents,
                    LastCycleSentEvents,
                    LastCycleQueuedEvents,
                    LastCycleError,
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
                    @LastCollectedEventUtc,
                    @LastCycleStartedUtc,
                    @LastCycleFinishedUtc,
                    @LastCycleDurationMs,
                    @LastCycleSecurityEventsRead,
                    @LastCycleUsnEventsRead,
                    @LastCycleCorrelatedEvents,
                    @LastCycleSentEvents,
                    @LastCycleQueuedEvents,
                    @LastCycleError,
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
                LastCollectedEventUtc,
                LastCycleStartedUtc,
                LastCycleFinishedUtc,
                LastCycleDurationMs,
                LastCycleSecurityEventsRead,
                LastCycleUsnEventsRead,
                LastCycleCorrelatedEvents,
                LastCycleSentEvents,
                LastCycleQueuedEvents,
                LastCycleError,
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
        command.Parameters.AddWithValue("@LastCollectedEventUtc", DbValue(heartbeat.LastCollectedEventUtc?.UtcDateTime));
        command.Parameters.AddWithValue("@LastCycleStartedUtc", DbValue(heartbeat.LastCycle?.StartedUtc?.UtcDateTime));
        command.Parameters.AddWithValue("@LastCycleFinishedUtc", DbValue(heartbeat.LastCycle?.FinishedUtc?.UtcDateTime));
        command.Parameters.AddWithValue("@LastCycleDurationMs", DbValue(heartbeat.LastCycle?.DurationMs));
        command.Parameters.AddWithValue("@LastCycleSecurityEventsRead", DbValue(heartbeat.LastCycle?.SecurityEventsRead));
        command.Parameters.AddWithValue("@LastCycleUsnEventsRead", DbValue(heartbeat.LastCycle?.UsnEventsRead));
        command.Parameters.AddWithValue("@LastCycleCorrelatedEvents", DbValue(heartbeat.LastCycle?.CorrelatedEvents));
        command.Parameters.AddWithValue("@LastCycleSentEvents", DbValue(heartbeat.LastCycle?.SentEvents));
        command.Parameters.AddWithValue("@LastCycleQueuedEvents", DbValue(heartbeat.LastCycle?.QueuedEvents));
        command.Parameters.AddWithValue("@LastCycleError", DbValue(TruncateHeartbeatText(heartbeat.LastCycle?.Error)));
        command.Parameters.AddWithValue("@Message", DbValue(TruncateHeartbeatText(heartbeat.Message)));
    }

    private static string? TruncateHeartbeatText(string? value)
    {
        const int maximumLength = 1024;
        return string.IsNullOrEmpty(value) || value.Length <= maximumLength
            ? value
            : value[..maximumLength];
    }

    private static AgentHealthResponse ReadHeartbeat(SqlDataReader reader)
    {
        var lastUsnJson = ReadNullableString(reader, "LastUsnByVolumeJson");
        var lastUsnByVolume = string.IsNullOrWhiteSpace(lastUsnJson)
            ? new Dictionary<string, long>()
            : JsonSerializer.Deserialize<Dictionary<string, long>>(lastUsnJson) ?? new Dictionary<string, long>();
        var lastCycle = ReadLastCycle(reader);

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
            LastSuccessfulSendUtc: ReadNullableUtcDateTimeOffset(reader, "LastSuccessfulSendUtc"),
            LastCollectedEventUtc: ReadNullableUtcDateTimeOffset(reader, "LastCollectedEventUtc"),
            LastCycle: lastCycle);
    }

    private static FileServerMonitor.Core.AgentCycleMetrics? ReadLastCycle(SqlDataReader reader)
    {
        var duration = ReadNullableLong(reader, "LastCycleDurationMs");

        if (duration is null)
        {
            return null;
        }

        return new FileServerMonitor.Core.AgentCycleMetrics(
            StartedUtc: ReadNullableUtcDateTimeOffset(reader, "LastCycleStartedUtc"),
            FinishedUtc: ReadNullableUtcDateTimeOffset(reader, "LastCycleFinishedUtc"),
            DurationMs: duration.Value,
            SecurityEventsRead: ReadNullableInt(reader, "LastCycleSecurityEventsRead") ?? 0,
            UsnEventsRead: ReadNullableInt(reader, "LastCycleUsnEventsRead") ?? 0,
            CorrelatedEvents: ReadNullableInt(reader, "LastCycleCorrelatedEvents") ?? 0,
            SentEvents: ReadNullableInt(reader, "LastCycleSentEvents") ?? 0,
            QueuedEvents: ReadNullableInt(reader, "LastCycleQueuedEvents") ?? 0,
            Error: ReadNullableString(reader, "LastCycleError"));
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

    private static int? ReadNullableInt(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
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

internal sealed class RetentionSettingsStore
{
    private RetentionOptions _settings;
#if SQLSERVER
    private readonly bool _persistSettings;
    private readonly string? _connectionString;
#endif

    public RetentionSettingsStore(IConfiguration configuration)
    {
#if SQLSERVER
        _persistSettings = configuration.GetValue("Monitor:StorageProvider", "SqlServer")
            .Equals("SqlServer", StringComparison.OrdinalIgnoreCase);
        _connectionString = configuration.GetConnectionString("SqlServer");
#endif

        _settings = Normalize(new RetentionSettingsRequest(
            Enabled: configuration.GetValue("Retention:Enabled", false),
            EventsDays: configuration.GetValue<int?>("Retention:EventsDays"),
            TimelineDays: configuration.GetValue<int?>("Retention:TimelineDays"),
            AlertsDays: configuration.GetValue<int?>("Retention:AlertsDays"),
            IntervalHours: configuration.GetValue<int?>("Retention:IntervalHours"),
            PurgeBatchSize: configuration.GetValue<int?>("Retention:PurgeBatchSize")));
    }

    public async Task<RetentionOptions> GetAsync(CancellationToken cancellationToken)
    {
#if SQLSERVER
        if (_persistSettings)
        {
            var persisted = await ReadSqlAsync(cancellationToken);
            if (persisted is not null)
            {
                _settings = persisted;
            }
        }
#endif

        return _settings;
    }

    public async Task<RetentionOptions> SaveAsync(RetentionSettingsRequest request, CancellationToken cancellationToken)
    {
        var settings = Normalize(request);
        _settings = settings;

#if SQLSERVER
        if (_persistSettings)
        {
            await UpsertSqlAsync(settings, cancellationToken);
        }
#endif

        return settings;
    }

    private static RetentionOptions Normalize(RetentionSettingsRequest request)
    {
        var eventsDays = Math.Clamp(request.EventsDays ?? 180, 7, 3650);
        var timelineDays = Math.Clamp(request.TimelineDays ?? eventsDays, 7, 3650);
        var alertsDays = Math.Clamp(request.AlertsDays ?? 365, 7, 3650);

        return new RetentionOptions(
            Enabled: request.Enabled,
            EventsDays: eventsDays,
            TimelineDays: timelineDays,
            AlertsDays: alertsDays,
            IntervalHours: Math.Clamp(request.IntervalHours ?? 24, 1, 168),
            PurgeBatchSize: Math.Clamp(request.PurgeBatchSize ?? 10_000, 100, 100_000),
            UpdatedUtc: DateTimeOffset.UtcNow);
    }

#if SQLSERVER
    private async Task<RetentionOptions?> ReadSqlAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateSqlConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureSqlSchemaAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1)
                Enabled,
                EventsDays,
                TimelineDays,
                AlertsDays,
                IntervalHours,
                PurgeBatchSize,
                UpdatedUtc
            FROM dbo.RetentionSettings
            WHERE Id = 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSettings(reader) : null;
    }

    private async Task UpsertSqlAsync(RetentionOptions settings, CancellationToken cancellationToken)
    {
        await using var connection = CreateSqlConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureSqlSchemaAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            MERGE dbo.RetentionSettings AS target
            USING (SELECT 1 AS Id) AS source
                ON target.Id = source.Id
            WHEN MATCHED THEN
                UPDATE SET
                    Enabled = @Enabled,
                    EventsDays = @EventsDays,
                    TimelineDays = @TimelineDays,
                    AlertsDays = @AlertsDays,
                    IntervalHours = @IntervalHours,
                    PurgeBatchSize = @PurgeBatchSize,
                    UpdatedUtc = @UpdatedUtc
            WHEN NOT MATCHED THEN
                INSERT
                (
                    Id,
                    Enabled,
                    EventsDays,
                    TimelineDays,
                    AlertsDays,
                    IntervalHours,
                    PurgeBatchSize,
                    UpdatedUtc
                )
                VALUES
                (
                    1,
                    @Enabled,
                    @EventsDays,
                    @TimelineDays,
                    @AlertsDays,
                    @IntervalHours,
                    @PurgeBatchSize,
                    @UpdatedUtc
                );
            """;
        AddParameters(command, settings);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureSqlSchemaAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'dbo.RetentionSettings', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.RetentionSettings
                (
                    Id INT NOT NULL CONSTRAINT PK_RetentionSettings PRIMARY KEY,
                    Enabled BIT NOT NULL,
                    EventsDays INT NOT NULL,
                    TimelineDays INT NOT NULL,
                    AlertsDays INT NOT NULL,
                    IntervalHours INT NOT NULL,
                    PurgeBatchSize INT NOT NULL,
                    UpdatedUtc DATETIME2(3) NOT NULL
                );
            END;
            """;
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

    private static void AddParameters(SqlCommand command, RetentionOptions settings)
    {
        command.Parameters.AddWithValue("@Enabled", settings.Enabled);
        command.Parameters.AddWithValue("@EventsDays", settings.EventsDays);
        command.Parameters.AddWithValue("@TimelineDays", settings.TimelineDays);
        command.Parameters.AddWithValue("@AlertsDays", settings.AlertsDays);
        command.Parameters.AddWithValue("@IntervalHours", settings.IntervalHours);
        command.Parameters.AddWithValue("@PurgeBatchSize", settings.PurgeBatchSize);
        command.Parameters.AddWithValue("@UpdatedUtc", settings.UpdatedUtc.UtcDateTime);
    }

    private static RetentionOptions ReadSettings(SqlDataReader reader)
    {
        return new RetentionOptions(
            Enabled: reader.GetBoolean(reader.GetOrdinal("Enabled")),
            EventsDays: reader.GetInt32(reader.GetOrdinal("EventsDays")),
            TimelineDays: reader.GetInt32(reader.GetOrdinal("TimelineDays")),
            AlertsDays: reader.GetInt32(reader.GetOrdinal("AlertsDays")),
            IntervalHours: reader.GetInt32(reader.GetOrdinal("IntervalHours")),
            PurgeBatchSize: reader.GetInt32(reader.GetOrdinal("PurgeBatchSize")),
            UpdatedUtc: new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("UpdatedUtc")), DateTimeKind.Utc)));
    }
#endif
}

internal sealed class InventoryScanSettingsStore
{
    private InventoryScanOptions _settings;
#if SQLSERVER
    private readonly bool _persistSettings;
    private readonly string? _connectionString;
#endif

    public InventoryScanSettingsStore(IConfiguration configuration)
    {
#if SQLSERVER
        _persistSettings = configuration.GetValue("Monitor:StorageProvider", "SqlServer")
            .Equals("SqlServer", StringComparison.OrdinalIgnoreCase);
        _connectionString = configuration.GetConnectionString("SqlServer");
#endif

        _settings = Normalize(new InventoryScanSettingsRequest(
            Enabled: configuration.GetValue("InventoryScan:Enabled", false),
            IntervalHours: configuration.GetValue<int?>("InventoryScan:IntervalHours"),
            BatchSize: configuration.GetValue<int?>("InventoryScan:BatchSize"),
            MaxItemsPerScan: configuration.GetValue<int?>("InventoryScan:MaxItemsPerScan"),
            IncludeLastAccessTime: configuration.GetValue("InventoryScan:IncludeLastAccessTime", true),
            WindowStartLocal: configuration.GetValue<string>("InventoryScan:WindowStartLocal"),
            WindowEndLocal: configuration.GetValue<string>("InventoryScan:WindowEndLocal"),
            RootPath: configuration.GetValue<string>("InventoryScan:RootPath"),
            Server: configuration.GetValue<string>("InventoryScan:Server"),
            Share: configuration.GetValue<string>("InventoryScan:Share"),
            RunRequestedUtc: null));
    }

    public async Task<InventoryScanOptions> GetAsync(CancellationToken cancellationToken)
    {
#if SQLSERVER
        if (_persistSettings)
        {
            var persisted = await ReadSqlAsync(cancellationToken);
            if (persisted is not null)
            {
                _settings = persisted;
            }
        }
#endif

        return _settings;
    }

    public async Task<InventoryScanOptions> SaveAsync(InventoryScanSettingsRequest request, CancellationToken cancellationToken)
    {
        var settings = Normalize(request);
        _settings = settings;

#if SQLSERVER
        if (_persistSettings)
        {
            await UpsertSqlAsync(settings, cancellationToken);
        }
#endif

        return settings;
    }

    public async Task<InventoryScanOptions> RequestRunNowAsync(CancellationToken cancellationToken)
    {
        var current = await GetAsync(cancellationToken);
        var settings = current with
        {
            Enabled = true,
            RunRequestedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        _settings = settings;

#if SQLSERVER
        if (_persistSettings)
        {
            await UpsertSqlAsync(settings, cancellationToken);
        }
#endif

        return settings;
    }

    private static InventoryScanOptions Normalize(InventoryScanSettingsRequest request)
    {
        var rootPath = string.IsNullOrWhiteSpace(request.RootPath)
            ? @"C:\Corporativo"
            : request.RootPath.Trim().Replace('/', '\\').TrimEnd('\\');

        return new InventoryScanOptions(
            Enabled: request.Enabled,
            IntervalHours: Math.Clamp(request.IntervalHours ?? 24, 1, 168),
            BatchSize: Math.Clamp(request.BatchSize ?? 1000, 100, 2000),
            MaxItemsPerScan: Math.Clamp(request.MaxItemsPerScan ?? 0, 0, 10_000_000),
            IncludeLastAccessTime: request.IncludeLastAccessTime,
            WindowStartLocal: request.WindowStartLocal?.Trim() ?? string.Empty,
            WindowEndLocal: request.WindowEndLocal?.Trim() ?? string.Empty,
            RootPath: rootPath,
            Server: string.IsNullOrWhiteSpace(request.Server) ? "FileServer" : request.Server.Trim(),
            Share: string.IsNullOrWhiteSpace(request.Share) ? "Corporativo" : request.Share.Trim(),
            RunRequestedUtc: request.RunRequestedUtc,
            UpdatedUtc: DateTimeOffset.UtcNow);
    }

#if SQLSERVER
    private async Task<InventoryScanOptions?> ReadSqlAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateSqlConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureSqlSchemaAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1)
                Enabled,
                IntervalHours,
                BatchSize,
                MaxItemsPerScan,
                IncludeLastAccessTime,
                WindowStartLocal,
                WindowEndLocal,
                RootPath,
                ServerName,
                ShareName,
                UpdatedUtc,
                RunRequestedUtc
            FROM dbo.InventoryScanSettings
            WHERE Id = 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSettings(reader) : null;
    }

    private async Task UpsertSqlAsync(InventoryScanOptions settings, CancellationToken cancellationToken)
    {
        await using var connection = CreateSqlConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureSqlSchemaAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            MERGE dbo.InventoryScanSettings AS target
            USING (SELECT 1 AS Id) AS source
                ON target.Id = source.Id
            WHEN MATCHED THEN
                UPDATE SET
                    Enabled = @Enabled,
                    IntervalHours = @IntervalHours,
                    BatchSize = @BatchSize,
                    MaxItemsPerScan = @MaxItemsPerScan,
                    IncludeLastAccessTime = @IncludeLastAccessTime,
                    WindowStartLocal = @WindowStartLocal,
                    WindowEndLocal = @WindowEndLocal,
                    RootPath = @RootPath,
                    ServerName = @ServerName,
                    ShareName = @ShareName,
                    UpdatedUtc = @UpdatedUtc,
                    RunRequestedUtc = @RunRequestedUtc
            WHEN NOT MATCHED THEN
                INSERT
                (
                    Id,
                    Enabled,
                    IntervalHours,
                    BatchSize,
                    MaxItemsPerScan,
                    IncludeLastAccessTime,
                    WindowStartLocal,
                    WindowEndLocal,
                    RootPath,
                    ServerName,
                    ShareName,
                    UpdatedUtc,
                    RunRequestedUtc
                )
                VALUES
                (
                    1,
                    @Enabled,
                    @IntervalHours,
                    @BatchSize,
                    @MaxItemsPerScan,
                    @IncludeLastAccessTime,
                    @WindowStartLocal,
                    @WindowEndLocal,
                    @RootPath,
                    @ServerName,
                    @ShareName,
                    @UpdatedUtc,
                    @RunRequestedUtc
                );
            """;
        AddParameters(command, settings);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureSqlSchemaAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'dbo.InventoryScanSettings', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.InventoryScanSettings
                (
                    Id INT NOT NULL CONSTRAINT PK_InventoryScanSettings PRIMARY KEY,
                    Enabled BIT NOT NULL,
                    IntervalHours INT NOT NULL,
                    BatchSize INT NOT NULL,
                    MaxItemsPerScan INT NOT NULL,
                    IncludeLastAccessTime BIT NOT NULL,
                    WindowStartLocal NVARCHAR(16) NOT NULL,
                    WindowEndLocal NVARCHAR(16) NOT NULL,
                    RootPath NVARCHAR(1024) NOT NULL,
                    ServerName NVARCHAR(128) NOT NULL,
                    ShareName NVARCHAR(128) NOT NULL,
                    UpdatedUtc DATETIME2(3) NOT NULL,
                    RunRequestedUtc DATETIME2(3) NULL
                );
            END;
            ELSE IF COL_LENGTH(N'dbo.InventoryScanSettings', N'RunRequestedUtc') IS NULL
            BEGIN
                ALTER TABLE dbo.InventoryScanSettings ADD RunRequestedUtc DATETIME2(3) NULL;
            END;
            """;
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

    private static void AddParameters(SqlCommand command, InventoryScanOptions settings)
    {
        command.Parameters.AddWithValue("@Enabled", settings.Enabled);
        command.Parameters.AddWithValue("@IntervalHours", settings.IntervalHours);
        command.Parameters.AddWithValue("@BatchSize", settings.BatchSize);
        command.Parameters.AddWithValue("@MaxItemsPerScan", settings.MaxItemsPerScan);
        command.Parameters.AddWithValue("@IncludeLastAccessTime", settings.IncludeLastAccessTime);
        command.Parameters.AddWithValue("@WindowStartLocal", settings.WindowStartLocal);
        command.Parameters.AddWithValue("@WindowEndLocal", settings.WindowEndLocal);
        command.Parameters.AddWithValue("@RootPath", settings.RootPath);
        command.Parameters.AddWithValue("@ServerName", settings.Server);
        command.Parameters.AddWithValue("@ShareName", settings.Share);
        command.Parameters.AddWithValue("@UpdatedUtc", settings.UpdatedUtc.UtcDateTime);
        command.Parameters.AddWithValue("@RunRequestedUtc", settings.RunRequestedUtc?.UtcDateTime ?? (object)DBNull.Value);
    }

    private static InventoryScanOptions ReadSettings(SqlDataReader reader)
    {
        return new InventoryScanOptions(
            Enabled: reader.GetBoolean(reader.GetOrdinal("Enabled")),
            IntervalHours: reader.GetInt32(reader.GetOrdinal("IntervalHours")),
            BatchSize: reader.GetInt32(reader.GetOrdinal("BatchSize")),
            MaxItemsPerScan: reader.GetInt32(reader.GetOrdinal("MaxItemsPerScan")),
            IncludeLastAccessTime: reader.GetBoolean(reader.GetOrdinal("IncludeLastAccessTime")),
            WindowStartLocal: reader.GetString(reader.GetOrdinal("WindowStartLocal")),
            WindowEndLocal: reader.GetString(reader.GetOrdinal("WindowEndLocal")),
            RootPath: reader.GetString(reader.GetOrdinal("RootPath")),
            Server: reader.GetString(reader.GetOrdinal("ServerName")),
            Share: reader.GetString(reader.GetOrdinal("ShareName")),
            RunRequestedUtc: ReadNullableDateTimeOffset(reader, "RunRequestedUtc"),
            UpdatedUtc: new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("UpdatedUtc")), DateTimeKind.Utc)));
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal)
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
    }
#endif
}

internal sealed record TimelineMaterializationResult(
    int RawEvents,
    int TimelineEvents,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc);

internal sealed class TimelineMaterializer
{
    private const int RebuildPaddingSeconds = 30;
    private const string CorrelationVersion = "core-v1";
    private readonly IEventRepository _events;
    private readonly ITimelineRepository _timeline;

    public TimelineMaterializer(IEventRepository events, ITimelineRepository timeline)
    {
        _events = events;
        _timeline = timeline;
    }

    public async Task<TimelineMaterializationResult> RebuildAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int take,
        CancellationToken cancellationToken)
    {
        var query = new EventQuery(
            Server: null,
            Share: null,
            User: null,
            Action: null,
            Path: null,
            SourceHost: null,
            SourceIp: null,
            Extension: null,
            Result: null,
            Severity: null,
            Source: null,
            FromUtc: fromUtc,
            ToUtc: toUtc,
            Take: Math.Clamp(take, 1, 50_000));
        var rawEvents = await _events.QueryAsync(query, cancellationToken);
        if (rawEvents.Count == 0)
        {
            return new TimelineMaterializationResult(0, 0, fromUtc, toUtc);
        }

        var fromWindow = fromUtc ?? rawEvents.Min(item => item.TimestampUtc).AddSeconds(-RebuildPaddingSeconds);
        var toWindow = toUtc ?? rawEvents.Max(item => item.TimestampUtc).AddSeconds(RebuildPaddingSeconds);
        var contextEvents = await QueryKnownDescendantContextAsync(rawEvents, cancellationToken);
        var source = contextEvents.Count == 0
            ? rawEvents
            : rawEvents.Concat(contextEvents).ToArray();
        var projected = ProjectTimeline(source);
        var windowTimeline = projected
            .Where(item => item.TimestampUtc >= fromWindow && item.TimestampUtc <= toWindow)
            .ToArray();

        await _timeline.ReplaceWindowAsync(
            fromWindow,
            toWindow,
            windowTimeline,
            CorrelationVersion,
            cancellationToken);

        return new TimelineMaterializationResult(
            RawEvents: rawEvents.Count,
            TimelineEvents: windowTimeline.Length,
            FromUtc: rawEvents.Min(item => item.TimestampUtc),
            ToUtc: rawEvents.Max(item => item.TimestampUtc));
    }

    private async Task<IReadOnlyCollection<FileAuditEvent>> QueryKnownDescendantContextAsync(
        IReadOnlyCollection<FileAuditEvent> rawEvents,
        CancellationToken cancellationToken)
    {
        var transitions = rawEvents
            .Where(item => item.Action is "moved" or "renamed"
                && !string.IsNullOrWhiteSpace(item.PreviousPath)
                && item.ObjectType is "folder" or "directory")
            .OrderBy(item => item.TimestampUtc)
            .ToArray();
        if (transitions.Length == 0)
        {
            return Array.Empty<FileAuditEvent>();
        }

        var context = new Dictionary<Guid, FileAuditEvent>();
        foreach (var transition in transitions)
        {
            var descendants = await _timeline.QueryKnownLiveDescendantsAsync(
                transition.PreviousPath!,
                transition.TimestampUtc,
                5_000,
                cancellationToken);

            foreach (var descendant in descendants)
            {
                context.TryAdd(descendant.Id, ToAuditEvent(descendant));
            }
        }

        return context.Values.ToArray();
    }

    internal static IReadOnlyCollection<FileAuditDisplayEvent> ProjectTimeline(
        IReadOnlyCollection<FileAuditEvent> events,
        string? user = null,
        string? action = null)
    {
        return new EventTimelineProjector()
            .BuildDisplayEvents(events.Select(ToCoreAuditEvent).ToArray())
            .Select(ToApiDisplayEvent)
            .Where(item => string.IsNullOrWhiteSpace(user)
                || item.User.Contains(user, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(action)
                || item.Action.Equals(action, StringComparison.OrdinalIgnoreCase)
                || item.DisplayAction.Equals(action, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static FileServerMonitor.Core.FileAuditEvent ToCoreAuditEvent(FileAuditEvent auditEvent)
    {
        return new FileServerMonitor.Core.FileAuditEvent(
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

    private static FileAuditDisplayEvent ToApiDisplayEvent(FileServerMonitor.Core.FileAuditDisplayEvent auditEvent)
    {
        return new FileAuditDisplayEvent(
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
            auditEvent.Source,
            auditEvent.DisplayAction,
            auditEvent.DisplayTarget);
    }

    private static FileAuditEvent ToAuditEvent(FileAuditDisplayEvent auditEvent)
    {
        return new FileAuditEvent(
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
}

internal sealed class TimelineMaterializationWorker : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxDebounce = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private readonly TimelineMaterializationCoordinator _coordinator;
    private readonly TimelineMaterializer _materializer;
    private readonly ILogger<TimelineMaterializationWorker> _logger;

    public TimelineMaterializationWorker(
        TimelineMaterializationCoordinator coordinator,
        TimelineMaterializer materializer,
        ILogger<TimelineMaterializationWorker> logger)
    {
        _coordinator = coordinator;
        _materializer = materializer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimelineMaterializationWindow window;
            try
            {
                window = await _coordinator.ClaimAsync(stoppingToken, Debounce, MaxDebounce);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = await _materializer.RebuildAsync(
                    window.FromUtc,
                    window.ToUtc,
                    50_000,
                    stoppingToken);
                _coordinator.Complete(window);
                _logger.LogInformation(
                    "Timeline materializada em {ElapsedMs} ms. Janela {FromUtc:o} a {ToUtc:o}; brutos {RawEvents}; correlacionados {TimelineEvents}.",
                    stopwatch.ElapsedMilliseconds,
                    window.FromUtc,
                    window.ToUtc,
                    result.RawEvents,
                    result.TimelineEvents);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _coordinator.Retry(window);
                break;
            }
            catch (Exception ex)
            {
                _coordinator.Retry(window);
                _logger.LogError(
                    ex,
                    "Falha ao materializar timeline da janela {FromUtc:o} a {ToUtc:o}.",
                    window.FromUtc,
                    window.ToUtc);
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }
    }
}

internal sealed class RetentionWorker : BackgroundService
{
    private readonly IEventRepository _events;
    private readonly ITimelineRepository _timeline;
    private readonly AlertStore _alerts;
    private readonly RetentionSettingsStore _settings;
    private readonly ILogger<RetentionWorker> _logger;

    public RetentionWorker(
        IEventRepository events,
        ITimelineRepository timeline,
        AlertStore alerts,
        RetentionSettingsStore settings,
        ILogger<RetentionWorker> logger)
    {
        _events = events;
        _timeline = timeline;
        _alerts = alerts;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = await _settings.GetAsync(stoppingToken);

            if (options.Enabled)
            {
                await RunOnceAsync(options, stoppingToken);
            }
            else
            {
                _logger.LogDebug("Retencao automatica desativada.");
            }

            var interval = options.Enabled
                ? TimeSpan.FromHours(Math.Max(1, options.IntervalHours))
                : TimeSpan.FromMinutes(5);
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task RunOnceAsync(RetentionOptions options, CancellationToken cancellationToken)
    {
        var eventCutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, options.EventsDays));
        var timelineCutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, options.TimelineDays));
        var alertCutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, options.AlertsDays));
        var batchSize = Math.Clamp(options.PurgeBatchSize, 100, 100_000);

        try
        {
            var deletedEvents = await _events.PurgeOlderThanAsync(eventCutoff, batchSize, cancellationToken);
            var deletedTimelineEvents = await _timeline.PurgeOlderThanAsync(timelineCutoff, batchSize, cancellationToken);
            var deletedAlerts = await _alerts.PurgeOlderThanAsync(alertCutoff, batchSize, cancellationToken);

            if (deletedEvents > 0 || deletedTimelineEvents > 0 || deletedAlerts > 0)
            {
                _logger.LogInformation(
                    "Retencao executada. Eventos brutos removidos: {DeletedEvents}. Timeline removida: {DeletedTimelineEvents}. Alertas removidos: {DeletedAlerts}.",
                    deletedEvents,
                    deletedTimelineEvents,
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

internal enum AuthRole
{
    Reader,
    Operator,
    Admin
}

internal sealed record LdapAuthSettings(
    bool Enabled,
    string Host,
    int Port,
    string Security,
    int TimeoutSeconds,
    bool ValidateTlsCertificate,
    string BaseDn,
    string BindFormat,
    string DomainSuffix,
    string NetbiosDomain,
    string AdminGroupDn,
    string OperatorGroupDn,
    string ReaderGroupDn,
    DateTimeOffset UpdatedUtc)
{
    public static LdapAuthSettings Default => new(
        Enabled: false,
        Host: "",
        Port: 636,
        Security: "LDAPS",
        TimeoutSeconds: 5,
        ValidateTlsCertificate: true,
        BaseDn: "",
        BindFormat: "DOMINIO\\usuario",
        DomainSuffix: "",
        NetbiosDomain: "",
        AdminGroupDn: "",
        OperatorGroupDn: "",
        ReaderGroupDn: "",
        UpdatedUtc: DateTimeOffset.UtcNow);
}

internal sealed record LdapAuthSettingsRequest(
    bool Enabled,
    string? Host,
    int? Port,
    string? Security,
    int? TimeoutSeconds,
    bool? ValidateTlsCertificate,
    string? BaseDn,
    string? BindFormat,
    string? DomainSuffix,
    string? NetbiosDomain,
    string? AdminGroupDn,
    string? OperatorGroupDn,
    string? ReaderGroupDn);

internal sealed record AuthConfigResponse(
    bool Enabled,
    string Host,
    int Port,
    string Security,
    int TimeoutSeconds,
    bool ValidateTlsCertificate,
    string BaseDn,
    string BindFormat,
    string DomainSuffix,
    string NetbiosDomain,
    string AdminGroupDn,
    string OperatorGroupDn,
    string ReaderGroupDn,
    string ConfigurationStatus,
    string LoginMode,
    DateTimeOffset UpdatedUtc)
{
    public static AuthConfigResponse FromSettings(LdapAuthSettings settings)
    {
        return new AuthConfigResponse(
            settings.Enabled,
            settings.Host,
            settings.Port,
            settings.Security,
            settings.TimeoutSeconds,
            settings.ValidateTlsCertificate,
            settings.BaseDn,
            settings.BindFormat,
            settings.DomainSuffix,
            settings.NetbiosDomain,
            settings.AdminGroupDn,
            settings.OperatorGroupDn,
            settings.ReaderGroupDn,
            settings.IsComplete() ? "complete" : "incomplete",
            settings.Enabled ? "ldap-ad" : "api-key",
            settings.UpdatedUtc);
    }
}

internal sealed record AuthStatusResponse(
    bool Enabled,
    string ConfigurationStatus,
    string LoginMode,
    DateTimeOffset UpdatedUtc)
{
    public static AuthStatusResponse FromSettings(LdapAuthSettings settings)
    {
        return new AuthStatusResponse(
            settings.Enabled,
            settings.IsComplete() ? "complete" : "incomplete",
            settings.Enabled ? "ldap-ad" : "api-key",
            settings.UpdatedUtc);
    }
}

internal sealed record LoginRequest(string? Username, string? Password);

internal sealed record AuthenticatedUser(
    string Username,
    string DisplayName,
    string DistinguishedName,
    string Role,
    IReadOnlyCollection<string> Groups);

internal sealed record LoginResponse(string Token, AuthenticatedUser User, DateTimeOffset ExpiresUtc);

internal sealed record LdapAuthResult(bool Success, AuthenticatedUser? User, string? Error)
{
    public static LdapAuthResult Failed(string error) => new(false, null, error);
}

internal static class LdapAuthSettingsExtensions
{
    public static bool IsComplete(this LdapAuthSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.Host)
            && !string.IsNullOrWhiteSpace(settings.BaseDn)
            && !string.IsNullOrWhiteSpace(settings.AdminGroupDn)
            && !string.IsNullOrWhiteSpace(settings.OperatorGroupDn)
            && !string.IsNullOrWhiteSpace(settings.ReaderGroupDn);
    }
}

internal sealed class LdapAuthSettingsStore
{
    private LdapAuthSettings _settings = LdapAuthSettings.Default;
#if SQLSERVER
    private readonly bool _persistSettings;
    private readonly string? _connectionString;
#endif

    public LdapAuthSettingsStore(IConfiguration configuration)
    {
#if SQLSERVER
        _persistSettings = configuration.GetValue("Monitor:StorageProvider", "SqlServer")
            .Equals("SqlServer", StringComparison.OrdinalIgnoreCase);
        _connectionString = configuration.GetConnectionString("SqlServer");
#endif

        _settings = Normalize(new LdapAuthSettingsRequest(
            Enabled: configuration.GetValue("Auth:Ldap:Enabled", false),
            Host: configuration.GetValue<string>("Auth:Ldap:Host"),
            Port: configuration.GetValue<int?>("Auth:Ldap:Port"),
            Security: configuration.GetValue<string>("Auth:Ldap:Security"),
            TimeoutSeconds: configuration.GetValue<int?>("Auth:Ldap:TimeoutSeconds"),
            ValidateTlsCertificate: configuration.GetValue<bool?>("Auth:Ldap:ValidateTlsCertificate"),
            BaseDn: configuration.GetValue<string>("Auth:Ldap:BaseDn"),
            BindFormat: configuration.GetValue<string>("Auth:Ldap:BindFormat"),
            DomainSuffix: configuration.GetValue<string>("Auth:Ldap:DomainSuffix"),
            NetbiosDomain: configuration.GetValue<string>("Auth:Ldap:NetbiosDomain"),
            AdminGroupDn: configuration.GetValue<string>("Auth:Ldap:AdminGroupDn"),
            OperatorGroupDn: configuration.GetValue<string>("Auth:Ldap:OperatorGroupDn"),
            ReaderGroupDn: configuration.GetValue<string>("Auth:Ldap:ReaderGroupDn")));
    }

    public async Task<LdapAuthSettings> GetAsync(CancellationToken cancellationToken)
    {
#if SQLSERVER
        if (_persistSettings)
        {
            var persisted = await ReadSqlAsync(cancellationToken);
            if (persisted is not null)
            {
                _settings = persisted;
            }
        }
#endif

        return _settings;
    }

    public async Task<LdapAuthSettings> SaveAsync(LdapAuthSettingsRequest request, CancellationToken cancellationToken)
    {
        var settings = Normalize(request);
        _settings = settings;

#if SQLSERVER
        if (_persistSettings)
        {
            await UpsertSqlAsync(settings, cancellationToken);
        }
#endif

        return settings;
    }

    private static LdapAuthSettings Normalize(LdapAuthSettingsRequest request)
    {
        var security = NormalizeSecurity(request.Security);
        var port = request.Port is > 0 and <= 65535
            ? request.Port.Value
            : security.Equals("LDAPS", StringComparison.OrdinalIgnoreCase) ? 636 : 389;

        return new LdapAuthSettings(
            Enabled: request.Enabled,
            Host: Trim(request.Host),
            Port: port,
            Security: security,
            TimeoutSeconds: Math.Clamp(request.TimeoutSeconds ?? 5, 1, 60),
            ValidateTlsCertificate: request.ValidateTlsCertificate ?? true,
            BaseDn: Trim(request.BaseDn),
            BindFormat: string.IsNullOrWhiteSpace(request.BindFormat) ? "DOMINIO\\usuario" : request.BindFormat.Trim(),
            DomainSuffix: Trim(request.DomainSuffix),
            NetbiosDomain: Trim(request.NetbiosDomain),
            AdminGroupDn: Trim(request.AdminGroupDn),
            OperatorGroupDn: Trim(request.OperatorGroupDn),
            ReaderGroupDn: Trim(request.ReaderGroupDn),
            UpdatedUtc: DateTimeOffset.UtcNow);
    }

    private static string NormalizeSecurity(string? value)
    {
        var security = string.IsNullOrWhiteSpace(value) ? "LDAPS" : value.Trim().ToUpperInvariant();
        return security is "LDAP" or "LDAPS" ? security : "LDAPS";
    }

    private static string Trim(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

#if SQLSERVER
    private async Task<LdapAuthSettings?> ReadSqlAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateSqlConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureSqlSchemaAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1)
                Enabled,
                HostName,
                PortNumber,
                SecurityMode,
                TimeoutSeconds,
                ValidateTlsCertificate,
                BaseDn,
                BindFormat,
                DomainSuffix,
                NetbiosDomain,
                AdminGroupDn,
                OperatorGroupDn,
                ReaderGroupDn,
                UpdatedUtc
            FROM dbo.LdapAuthSettings
            WHERE Id = 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSettings(reader) : null;
    }

    private async Task UpsertSqlAsync(LdapAuthSettings settings, CancellationToken cancellationToken)
    {
        await using var connection = CreateSqlConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureSqlSchemaAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            MERGE dbo.LdapAuthSettings AS target
            USING (SELECT 1 AS Id) AS source
                ON target.Id = source.Id
            WHEN MATCHED THEN
                UPDATE SET
                    Enabled = @Enabled,
                    HostName = @HostName,
                    PortNumber = @PortNumber,
                    SecurityMode = @SecurityMode,
                    TimeoutSeconds = @TimeoutSeconds,
                    ValidateTlsCertificate = @ValidateTlsCertificate,
                    BaseDn = @BaseDn,
                    BindFormat = @BindFormat,
                    DomainSuffix = @DomainSuffix,
                    NetbiosDomain = @NetbiosDomain,
                    AdminGroupDn = @AdminGroupDn,
                    OperatorGroupDn = @OperatorGroupDn,
                    ReaderGroupDn = @ReaderGroupDn,
                    UpdatedUtc = @UpdatedUtc
            WHEN NOT MATCHED THEN
                INSERT
                (
                    Id,
                    Enabled,
                    HostName,
                    PortNumber,
                    SecurityMode,
                    TimeoutSeconds,
                    ValidateTlsCertificate,
                    BaseDn,
                    BindFormat,
                    DomainSuffix,
                    NetbiosDomain,
                    AdminGroupDn,
                    OperatorGroupDn,
                    ReaderGroupDn,
                    UpdatedUtc
                )
                VALUES
                (
                    1,
                    @Enabled,
                    @HostName,
                    @PortNumber,
                    @SecurityMode,
                    @TimeoutSeconds,
                    @ValidateTlsCertificate,
                    @BaseDn,
                    @BindFormat,
                    @DomainSuffix,
                    @NetbiosDomain,
                    @AdminGroupDn,
                    @OperatorGroupDn,
                    @ReaderGroupDn,
                    @UpdatedUtc
                );
            """;
        AddParameters(command, settings);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureSqlSchemaAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'dbo.LdapAuthSettings', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.LdapAuthSettings
                (
                    Id INT NOT NULL CONSTRAINT PK_LdapAuthSettings PRIMARY KEY,
                    Enabled BIT NOT NULL,
                    HostName NVARCHAR(256) NOT NULL,
                    PortNumber INT NOT NULL,
                    SecurityMode NVARCHAR(16) NOT NULL,
                    TimeoutSeconds INT NOT NULL,
                    ValidateTlsCertificate BIT NOT NULL,
                    BaseDn NVARCHAR(1024) NOT NULL,
                    BindFormat NVARCHAR(64) NOT NULL,
                    DomainSuffix NVARCHAR(256) NOT NULL,
                    NetbiosDomain NVARCHAR(128) NOT NULL,
                    AdminGroupDn NVARCHAR(1024) NOT NULL,
                    OperatorGroupDn NVARCHAR(1024) NOT NULL,
                    ReaderGroupDn NVARCHAR(1024) NOT NULL,
                    UpdatedUtc DATETIME2(3) NOT NULL
                );
            END;
            ELSE IF COL_LENGTH(N'dbo.LdapAuthSettings', N'ValidateTlsCertificate') IS NULL
            BEGIN
                ALTER TABLE dbo.LdapAuthSettings
                ADD ValidateTlsCertificate BIT NOT NULL CONSTRAINT DF_LdapAuthSettings_ValidateTlsCertificate DEFAULT (1);
            END;
            """;
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

    private static void AddParameters(SqlCommand command, LdapAuthSettings settings)
    {
        command.Parameters.AddWithValue("@Enabled", settings.Enabled);
        command.Parameters.AddWithValue("@HostName", settings.Host);
        command.Parameters.AddWithValue("@PortNumber", settings.Port);
        command.Parameters.AddWithValue("@SecurityMode", settings.Security);
        command.Parameters.AddWithValue("@TimeoutSeconds", settings.TimeoutSeconds);
        command.Parameters.AddWithValue("@ValidateTlsCertificate", settings.ValidateTlsCertificate);
        command.Parameters.AddWithValue("@BaseDn", settings.BaseDn);
        command.Parameters.AddWithValue("@BindFormat", settings.BindFormat);
        command.Parameters.AddWithValue("@DomainSuffix", settings.DomainSuffix);
        command.Parameters.AddWithValue("@NetbiosDomain", settings.NetbiosDomain);
        command.Parameters.AddWithValue("@AdminGroupDn", settings.AdminGroupDn);
        command.Parameters.AddWithValue("@OperatorGroupDn", settings.OperatorGroupDn);
        command.Parameters.AddWithValue("@ReaderGroupDn", settings.ReaderGroupDn);
        command.Parameters.AddWithValue("@UpdatedUtc", settings.UpdatedUtc.UtcDateTime);
    }

    private static LdapAuthSettings ReadSettings(SqlDataReader reader)
    {
        return new LdapAuthSettings(
            Enabled: reader.GetBoolean(reader.GetOrdinal("Enabled")),
            Host: reader.GetString(reader.GetOrdinal("HostName")),
            Port: reader.GetInt32(reader.GetOrdinal("PortNumber")),
            Security: reader.GetString(reader.GetOrdinal("SecurityMode")),
            TimeoutSeconds: reader.GetInt32(reader.GetOrdinal("TimeoutSeconds")),
            ValidateTlsCertificate: reader.GetBoolean(reader.GetOrdinal("ValidateTlsCertificate")),
            BaseDn: reader.GetString(reader.GetOrdinal("BaseDn")),
            BindFormat: reader.GetString(reader.GetOrdinal("BindFormat")),
            DomainSuffix: reader.GetString(reader.GetOrdinal("DomainSuffix")),
            NetbiosDomain: reader.GetString(reader.GetOrdinal("NetbiosDomain")),
            AdminGroupDn: reader.GetString(reader.GetOrdinal("AdminGroupDn")),
            OperatorGroupDn: reader.GetString(reader.GetOrdinal("OperatorGroupDn")),
            ReaderGroupDn: reader.GetString(reader.GetOrdinal("ReaderGroupDn")),
            UpdatedUtc: new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("UpdatedUtc")), DateTimeKind.Utc)));
    }
#endif
}

internal sealed class LdapAuthenticator
{
    private readonly ILogger<LdapAuthenticator> _logger;

    public LdapAuthenticator(ILogger<LdapAuthenticator> logger)
    {
        _logger = logger;
    }

    public Task<LdapAuthResult> AuthenticateAsync(
        LdapAuthSettings settings,
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Task.FromResult(LdapAuthResult.Failed("Usuario e senha sao obrigatorios."));
        }

        if (!settings.IsComplete())
        {
            return Task.FromResult(LdapAuthResult.Failed("Configuracao LDAP/AD incompleta."));
        }

        var errors = new List<string>();

        foreach (var host in GetCandidateHosts(settings.Host))
        {
            try
            {
                var loginName = BuildBindName(settings, request.Username.Trim());
                using var connection = CreateConnection(settings, host);
                connection.AuthType = AuthType.Basic;
                connection.Bind(new NetworkCredential(loginName, request.Password));

                var user = SearchUser(connection, settings, request.Username.Trim());
                if (user is null)
                {
                    return Task.FromResult(LdapAuthResult.Failed("Usuario autenticado, mas nao encontrado no diretorio."));
                }

                if (!TryResolveRole(connection, settings, user, out var role))
                {
                    _logger.LogWarning(
                        "Usuario LDAP/AD {Username} autenticado, mas sem grupos autorizados. DN={UserDn}. Grupos diretos={Groups}",
                        user.Username,
                        user.DistinguishedName,
                        string.Join(";", user.Groups));
                    return Task.FromResult(LdapAuthResult.Failed("Usuario sem grupo autorizado."));
                }

                return Task.FromResult(new LdapAuthResult(
                    true,
                    user with { Role = role.ToString().ToLowerInvariant() },
                    null));
            }
            catch (Exception ex) when (ex is LdapException or DirectoryOperationException or InvalidOperationException or TypeInitializationException or DllNotFoundException)
            {
                var detail = DescribeLdapFailure(host, ex);
                errors.Add(detail);
                _logger.LogWarning(ex, "Falha LDAP/AD ao autenticar em {Host}:{Port}. Detalhe: {Detail}", host, settings.Port, detail);
            }
        }

        return Task.FromResult(LdapAuthResult.Failed(errors.Count == 0
            ? "Nao foi possivel conectar ao LDAP/AD."
            : string.Join(" | ", errors)));
    }

    private static LdapConnection CreateConnection(LdapAuthSettings settings, string host)
    {
        Environment.SetEnvironmentVariable(
            "LDAPTLS_REQCERT",
            settings.ValidateTlsCertificate ? "demand" : "never");

        var identifier = new LdapDirectoryIdentifier(host, settings.Port, fullyQualifiedDnsHostName: !IPAddress.TryParse(host, out _), connectionless: false);
        var connection = new LdapConnection(identifier)
        {
            Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds)
        };

        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;

        if (settings.Security.Equals("LDAPS", StringComparison.OrdinalIgnoreCase))
        {
            connection.SessionOptions.SecureSocketLayer = true;
        }

        return connection;
    }

    private static IReadOnlyCollection<string> GetCandidateHosts(string configuredHost)
    {
        var hosts = new List<string> { configuredHost };

        if (IPAddress.TryParse(configuredHost, out var address))
        {
            try
            {
                var hostEntry = Dns.GetHostEntry(address);
                if (!string.IsNullOrWhiteSpace(hostEntry.HostName))
                {
                    hosts.Insert(0, hostEntry.HostName);
                }
            }
            catch
            {
                // Mantem o IP configurado como candidato principal se o DNS reverso falhar.
            }
        }

        return hosts
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string DescribeLdapFailure(string host, Exception exception)
    {
        if (exception is LdapException ldapException)
        {
            var serverError = string.IsNullOrWhiteSpace(ldapException.ServerErrorMessage)
                ? null
                : $" server='{ldapException.ServerErrorMessage}'";
            return $"{host}: LDAP {ldapException.ErrorCode} - {ldapException.Message}{serverError}";
        }

        return $"{host}: {exception.GetType().Name} - {exception.Message}";
    }

    private static string BuildBindName(LdapAuthSettings settings, string username)
    {
        if (username.Contains('\\') || username.Contains('@') || username.Contains("=", StringComparison.Ordinal))
        {
            return username;
        }

        return settings.BindFormat switch
        {
            "usuario@dominio" when !string.IsNullOrWhiteSpace(settings.DomainSuffix) => $"{username}@{settings.DomainSuffix}",
            "DN" => username,
            _ when !string.IsNullOrWhiteSpace(settings.NetbiosDomain) => $"{settings.NetbiosDomain}\\{username}",
            _ when !string.IsNullOrWhiteSpace(settings.DomainSuffix) => $"{username}@{settings.DomainSuffix}",
            _ => username
        };
    }

    private static AuthenticatedUser? SearchUser(LdapConnection connection, LdapAuthSettings settings, string username)
    {
        var accountName = username.Contains('\\') ? username.Split('\\').Last() : username.Split('@').First();
        var filter = $"(|(sAMAccountName={EscapeLdapFilter(accountName)})(userPrincipalName={EscapeLdapFilter(username)}))";
        var request = new SearchRequest(
            settings.BaseDn,
            filter,
            SearchScope.Subtree,
            "displayName",
            "distinguishedName",
            "memberOf",
            "sAMAccountName",
            "userPrincipalName");
        var response = (SearchResponse)connection.SendRequest(request);
        var entry = response.Entries.Cast<SearchResultEntry>().FirstOrDefault();

        if (entry is null)
        {
            return null;
        }

        var groups = GetAttributeValues(entry, "memberOf");
        var displayName = GetAttributeValue(entry, "displayName")
            ?? GetAttributeValue(entry, "sAMAccountName")
            ?? accountName;
        var distinguishedName = GetAttributeValue(entry, "distinguishedName") ?? string.Empty;

        return new AuthenticatedUser(accountName, displayName, distinguishedName, "reader", groups);
    }

    private static bool TryResolveRole(LdapConnection connection, LdapAuthSettings settings, AuthenticatedUser user, out AuthRole role)
    {
        if (IsUserInGroup(connection, user, settings.AdminGroupDn))
        {
            role = AuthRole.Admin;
            return true;
        }

        if (IsUserInGroup(connection, user, settings.OperatorGroupDn))
        {
            role = AuthRole.Operator;
            return true;
        }

        if (IsUserInGroup(connection, user, settings.ReaderGroupDn))
        {
            role = AuthRole.Reader;
            return true;
        }

        role = AuthRole.Reader;
        return false;
    }

    private static bool IsUserInGroup(LdapConnection connection, AuthenticatedUser user, string configuredGroup)
    {
        if (string.IsNullOrWhiteSpace(configuredGroup))
        {
            return false;
        }

        if (ContainsGroup(user.Groups, configuredGroup))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(user.DistinguishedName))
        {
            return false;
        }

        var filter = $"(&(objectClass=group)(distinguishedName={EscapeLdapFilter(configuredGroup)})(member:1.2.840.113556.1.4.1941:={EscapeLdapFilter(user.DistinguishedName)}))";
        var request = new SearchRequest(configuredGroup, filter, SearchScope.Base, "distinguishedName");

        try
        {
            var response = (SearchResponse)connection.SendRequest(request);
            return response.Entries.Count > 0;
        }
        catch (DirectoryOperationException)
        {
            return false;
        }
    }

    private static bool ContainsGroup(IEnumerable<string> groups, string configuredGroup)
    {
        return !string.IsNullOrWhiteSpace(configuredGroup)
            && groups.Any(group => group.Equals(configuredGroup, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetAttributeValue(SearchResultEntry entry, string name)
    {
        return entry.Attributes.Contains(name) && entry.Attributes[name].Count > 0
            ? entry.Attributes[name][0]?.ToString()
            : null;
    }

    private static IReadOnlyCollection<string> GetAttributeValues(SearchResultEntry entry, string name)
    {
        if (!entry.Attributes.Contains(name))
        {
            return Array.Empty<string>();
        }

        return entry.Attributes[name]
            .Cast<object>()
            .Select(value => value.ToString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
    }

    private static string EscapeLdapFilter(string value)
    {
        return value
            .Replace("\\", "\\5c", StringComparison.Ordinal)
            .Replace("*", "\\2a", StringComparison.Ordinal)
            .Replace("(", "\\28", StringComparison.Ordinal)
            .Replace(")", "\\29", StringComparison.Ordinal)
            .Replace("\0", "\\00", StringComparison.Ordinal);
    }
}

internal sealed record AuthSessionPayload(
    string Username,
    string DisplayName,
    string DistinguishedName,
    string Role,
    IReadOnlyCollection<string> Groups,
    long ExpiresUnixSeconds)
{
    public ClaimsPrincipal ToPrincipal()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, Username),
            new(ClaimTypes.GivenName, DisplayName),
            new(ClaimTypes.Role, Role)
        };

        claims.AddRange(Groups.Select(group => new Claim("ad_group", group)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "ldap-ad"));
    }
}

internal static class AuthSessionToken
{
    public static string Create(AuthenticatedUser user, string secret)
    {
        var payload = new AuthSessionPayload(
            user.Username,
            user.DisplayName,
            user.DistinguishedName,
            user.Role,
            user.Groups,
            DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds());
        var json = JsonSerializer.Serialize(payload);
        var body = Base64UrlEncode(Encoding.UTF8.GetBytes(json));
        var signature = Sign(body, secret);

        return $"{body}.{signature}";
    }

    public static AuthSessionPayload? TryValidate(string? token, string secret)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split('.', 2);
        if (parts.Length != 2 || !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Sign(parts[0], secret)),
            Encoding.UTF8.GetBytes(parts[1])))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<AuthSessionPayload>(Encoding.UTF8.GetString(Base64UrlDecode(parts[0])));
            return payload is not null && payload.ExpiresUnixSeconds > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                ? payload
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Sign(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
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

    public string GetSigningSecret()
    {
        return GetEffectiveAdminApiKey()
            ?? ApiKey
            ?? "fileserver-monitor-local-session-development-secret";
    }

    public string? GetEffectiveAdminApiKey()
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
            || path.StartsWithSegments("/health")
            || path.StartsWithSegments("/metrics")
            || path.StartsWithSegments("/api/auth/login")
            || path.StartsWithSegments("/api/auth/status");
    }

    public static AuthRole? GetRequiredRole(HttpRequest request)
    {
        if (request.Path.StartsWithSegments("/api/admin-audit"))
        {
            return AuthRole.Admin;
        }

        if (request.Path.StartsWithSegments("/api/auth/config"))
        {
            return AuthRole.Admin;
        }

        if (request.Path.StartsWithSegments("/api/retention/config"))
        {
            return AuthRole.Admin;
        }

        if (request.Path.StartsWithSegments("/api/inventory/config") && !HttpMethods.IsGet(request.Method))
        {
            return AuthRole.Admin;
        }

        if (request.Path.StartsWithSegments("/api/inventory/scan-now"))
        {
            return AuthRole.Admin;
        }

        if (request.Path.StartsWithSegments("/api/alerts") && HttpMethods.IsPost(request.Method))
        {
            return AuthRole.Operator;
        }

        if (request.Path.StartsWithSegments("/api/alert-rules") && !HttpMethods.IsGet(request.Method))
        {
            return AuthRole.Operator;
        }

        if (request.Path.StartsWithSegments("/api/monitored-paths") && !HttpMethods.IsGet(request.Method))
        {
            return AuthRole.Operator;
        }

        if (request.Path.StartsWithSegments("/api/database/capacity"))
        {
            return AuthRole.Operator;
        }

        return null;
    }

    public static bool HasRequiredRole(string? actualRole, AuthRole requiredRole)
    {
        return GetRoleWeight(actualRole) >= GetRoleWeight(requiredRole.ToString());
    }

    private static int GetRoleWeight(string? role)
    {
        return role?.ToLowerInvariant() switch
        {
            "admin" => 3,
            "operator" => 2,
            "reader" => 1,
            _ => 0
        };
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

    public static string? GetBearerToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.FirstOrDefault();

        return !string.IsNullOrWhiteSpace(authorization)
            && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : null;
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
    int TimelineDays,
    int AlertsDays,
    int IntervalHours,
    int PurgeBatchSize,
    DateTimeOffset UpdatedUtc);

internal sealed record RetentionSettingsRequest(
    bool Enabled,
    int? EventsDays,
    int? TimelineDays,
    int? AlertsDays,
    int? IntervalHours,
    int? PurgeBatchSize);

internal sealed record RetentionSettingsResponse(
    bool Enabled,
    int EventsDays,
    int TimelineDays,
    int AlertsDays,
    int IntervalHours,
    int PurgeBatchSize,
    DateTimeOffset UpdatedUtc)
{
    public static RetentionSettingsResponse FromSettings(RetentionOptions settings)
    {
        return new RetentionSettingsResponse(
            settings.Enabled,
            settings.EventsDays,
            settings.TimelineDays,
            settings.AlertsDays,
            settings.IntervalHours,
            settings.PurgeBatchSize,
            settings.UpdatedUtc);
    }
}

internal sealed record InventoryScanOptions(
    bool Enabled,
    int IntervalHours,
    int BatchSize,
    int MaxItemsPerScan,
    bool IncludeLastAccessTime,
    string WindowStartLocal,
    string WindowEndLocal,
    string RootPath,
    string Server,
    string Share,
    DateTimeOffset? RunRequestedUtc,
    DateTimeOffset UpdatedUtc);

internal sealed record InventoryScanSettingsRequest(
    bool Enabled,
    int? IntervalHours,
    int? BatchSize,
    int? MaxItemsPerScan,
    bool IncludeLastAccessTime,
    string? WindowStartLocal,
    string? WindowEndLocal,
    string? RootPath,
    string? Server,
    string? Share,
    DateTimeOffset? RunRequestedUtc);

internal sealed record InventoryScanSettingsResponse(
    bool Enabled,
    int IntervalHours,
    int BatchSize,
    int MaxItemsPerScan,
    bool IncludeLastAccessTime,
    string WindowStartLocal,
    string WindowEndLocal,
    string RootPath,
    string Server,
    string Share,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? RunRequestedUtc)
{
    public static InventoryScanSettingsResponse FromSettings(InventoryScanOptions settings)
    {
        return new InventoryScanSettingsResponse(
            settings.Enabled,
            settings.IntervalHours,
            settings.BatchSize,
            settings.MaxItemsPerScan,
            settings.IncludeLastAccessTime,
            settings.WindowStartLocal,
            settings.WindowEndLocal,
            settings.RootPath,
            settings.Server,
            settings.Share,
            settings.UpdatedUtc,
            settings.RunRequestedUtc);
    }
}

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

internal sealed record FileAuditDisplayEvent(
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
    string DisplayAction,
    string DisplayTarget);

internal sealed record TimelinePageResponse(
    IReadOnlyCollection<FileAuditDisplayEvent> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages,
    int WindowRawEvents);

internal sealed record TimelineQuery(
    string? Server,
    string? Share,
    string? User,
    string? Action,
    string? Path,
    string? SourceHost,
    string? SourceIp,
    string? Extension,
    string? Result,
    string? Severity,
    string? Source,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    int Take);

internal sealed record TimelineRebuildResponse(
    int RawEvents,
    int TimelineEvents,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    string CorrelationVersion);

internal sealed record TimelineCoverage(
    long Count,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc);

internal sealed record InventorySnapshotStartRequest(
    string Server,
    string Share,
    string RootPath)
{
    public FileInventorySnapshot ToSnapshot()
    {
        return new FileInventorySnapshot(
            Id: Guid.NewGuid(),
            Server: string.IsNullOrWhiteSpace(Server) ? "UNKNOWN" : Server.Trim(),
            Share: string.IsNullOrWhiteSpace(Share) ? "UNKNOWN" : Share.Trim(),
            RootPath: FileInventoryNormalizer.NormalizePath(RootPath),
            StartedUtc: DateTimeOffset.UtcNow,
            FinishedUtc: null,
            Status: "running",
            FileCount: 0,
            FolderCount: 0,
            TotalBytes: 0,
            ErrorCount: 0,
            Error: null);
    }
}

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
    string? Error)
{
    public FileInventoryItem ToInventoryItem(Guid snapshotId)
    {
        return FileInventoryNormalizer.Normalize(new FileInventoryItemInput(
            SnapshotId: snapshotId,
            ScannedAtUtc: ScannedAtUtc,
            Server: Server,
            Share: Share,
            RootPath: RootPath,
            Path: Path,
            RelativePath: RelativePath,
            Name: Name,
            ItemType: ItemType,
            SizeBytes: SizeBytes,
            CreatedUtc: CreatedUtc,
            ModifiedUtc: ModifiedUtc,
            AccessedUtc: AccessedUtc,
            Error: Error));
    }
}

internal sealed record InventorySnapshotCompleteRequest(string? Status, string? Error);

internal sealed record InventoryBatchIngestResponse(Guid SnapshotId, int AcceptedItems);

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
    string? Share,
    string? User,
    string? Action,
    string? Path,
    string? SourceHost,
    string? SourceIp,
    string? Extension,
    string? Result,
    string? Severity,
    string? Source,
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

internal static class TimelineCsvExporter
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
        "displayAction",
        "displayTarget",
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

    public static string Export(IReadOnlyCollection<FileAuditDisplayEvent> events)
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
                auditEvent.DisplayAction,
                auditEvent.DisplayTarget,
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

internal sealed record MetricsResponse(
    string Service,
    string Status,
    DateTimeOffset TimestampUtc,
    ApiMetrics Api,
    DatabaseMetrics Database,
    DatabaseCapacityMetrics Capacity,
    InventoryMetrics Inventory,
    AgentMetricsSummary Agents,
    RetentionMetrics Retention,
    MetricsThresholds Thresholds);

internal sealed record ApiMetrics(
    string Status,
    string StorageProvider,
    long UptimeSeconds,
    DateTimeOffset StartedUtc,
    string MachineName,
    int ProcessId);

internal sealed record DatabaseMetrics(
    string Status,
    string Provider,
    long? StoredEvents,
    DateTimeOffset? LastEventUtc,
    long? LastEventAgeSeconds,
    long QueryDurationMs,
    string? Error);

internal sealed record DatabaseCapacityMetrics(
    string Status,
    long TotalRows,
    double TotalReservedMb,
    long RawEventRows,
    long TimelineRows,
    long AlertRows,
    long? TimelineOldestAgeSeconds,
    long? TimelineNewestAgeSeconds,
    string? Error);

internal sealed record DatabaseCapacityResponse(
    DateTimeOffset GeneratedUtc,
    string Provider,
    string Status,
    long TotalRows,
    double TotalReservedMb,
    long TimelineRows,
    DateTimeOffset? TimelineFromUtc,
    DateTimeOffset? TimelineToUtc,
    IReadOnlyCollection<DatabaseCapacityTable> Tables,
    IReadOnlyCollection<DatabaseCapacityWindow> Windows,
    IReadOnlyCollection<DatabaseCapacityDailyCount> DailyCounts,
    string? Message);

internal sealed record DatabaseCapacityTable(
    string Name,
    string PhysicalName,
    long RowCount,
    double ReservedMb,
    double UsedMb);

internal sealed record DatabaseCapacityWindow(
    string Name,
    long RowCount,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc);

internal sealed record DatabaseCapacityDailyCount(
    string Date,
    string Series,
    long Count);

internal sealed record InventoryMetrics(
    string Status,
    Guid? LastSnapshotId,
    string? LastSnapshotStatus,
    DateTimeOffset? LastScanStartedUtc,
    DateTimeOffset? LastScanFinishedUtc,
    long? LastScanAgeSeconds,
    long FileCount,
    long FolderCount,
    long TotalBytes,
    long ErrorCount,
    long Inactive365DaysFileCount,
    long Inactive365DaysBytes,
    long LargeFileCount,
    long LargeFileBytes,
    long ExecutableFileCount,
    long ExecutableFileBytes,
    string? Server,
    string? Share,
    string? RootPath,
    string? Error);

internal sealed record AgentMetricsSummary(
    string Status,
    int Total,
    int Running,
    int Stale,
    int Backlog,
    int Attention,
    int Critical,
    int CycleErrors,
    int Unhealthy,
    int MaxPendingQueueEvents,
    long? MaxHeartbeatAgeSeconds,
    long? MaxCollectedEventAgeSeconds,
    int LastCycleSecurityEventsRead,
    int LastCycleUsnEventsRead,
    int LastCycleCorrelatedEvents,
    int LastCycleSentEvents,
    int LastCycleQueuedEvents,
    long MaxCycleDurationMs,
    IReadOnlyCollection<AgentMetricsItem> Items,
    string? Error);

internal sealed record AgentMetricsItem(
    string AgentId,
    string Server,
    string Status,
    DateTimeOffset? LastHeartbeatUtc,
    long? LastHeartbeatAgeSeconds,
    string? Version,
    long LastRecordId,
    IReadOnlyDictionary<string, long> LastUsnByVolume,
    int PendingQueueEvents,
    DateTimeOffset? LastSuccessfulSendUtc,
    long? LastSuccessfulSendAgeSeconds,
    DateTimeOffset? LastCollectedEventUtc,
    long? LastCollectedEventAgeSeconds,
    FileServerMonitor.Core.AgentCycleMetrics? LastCycle,
    string OperationalStatus,
    string? OperationalMessage,
    bool HasCycleError,
    string? Message,
    bool IsStale);

internal sealed record RetentionMetrics(
    bool Enabled,
    int EventsDays,
    int TimelineDays,
    int AlertsDays,
    int IntervalHours,
    int PurgeBatchSize,
    DateTimeOffset UpdatedUtc)
{
    public static RetentionMetrics FromSettings(RetentionOptions settings)
    {
        return new RetentionMetrics(
            settings.Enabled,
            settings.EventsDays,
            settings.TimelineDays,
            settings.AlertsDays,
            settings.IntervalHours,
            settings.PurgeBatchSize,
            settings.UpdatedUtc);
    }
}

internal sealed record MetricsThresholds(
    int AgentStaleMinutes,
    int AgentBacklogWarningThreshold,
    int LastEventWarningSeconds);

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
    DateTimeOffset? LastSuccessfulSendUtc = null,
    DateTimeOffset? LastCollectedEventUtc = null,
    FileServerMonitor.Core.AgentCycleMetrics? LastCycle = null);

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
    DateTimeOffset? LastCollectedEventUtc = null,
    FileServerMonitor.Core.AgentCycleMetrics? LastCycle = null,
    int BacklogWarningThreshold = 1000,
    bool IsStale = false,
    int StaleAfterMinutes = 10,
    string OperationalStatus = "unknown",
    string? OperationalMessage = null,
    long? LastHeartbeatAgeSeconds = null,
    long? LastSuccessfulSendAgeSeconds = null,
    long? LastCollectedEventAgeSeconds = null,
    bool HasCycleError = false);

internal sealed record AgentConfigResponse(
    string Server,
    DateTimeOffset GeneratedUtc,
    string DefaultShare,
    string[] UsnVolumes,
    IReadOnlyCollection<MonitoredPath> MonitoredPaths,
    InventoryScanSettingsResponse InventoryScan);

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
