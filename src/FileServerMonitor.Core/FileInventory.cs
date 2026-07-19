using System.Globalization;

namespace FileServerMonitor.Core;

public sealed record FileInventoryItemInput(
    Guid? SnapshotId,
    DateTimeOffset? ScannedAtUtc,
    string? Server,
    string? Share,
    string? RootPath,
    string? Path,
    string? RelativePath,
    string? Name,
    string? ItemType,
    long? SizeBytes,
    DateTimeOffset? CreatedUtc,
    DateTimeOffset? ModifiedUtc,
    DateTimeOffset? AccessedUtc,
    string? Error,
    bool AclCollected = false,
    string? AclOwner = null,
    bool AclInheritanceProtected = false,
    string? AclRiskLevel = null,
    string? BroadAccessPrincipals = null,
    string? BroadAccessRights = null,
    string? AclError = null);

public sealed record FileInventoryItem(
    Guid Id,
    Guid SnapshotId,
    DateTimeOffset ScannedAtUtc,
    string Server,
    string Share,
    string RootPath,
    string Path,
    string RelativePath,
    string Name,
    string ItemType,
    string? Extension,
    long SizeBytes,
    int Depth,
    DateTimeOffset? CreatedUtc,
    DateTimeOffset? ModifiedUtc,
    DateTimeOffset? AccessedUtc,
    string Status,
    string? Error,
    bool AclCollected = false,
    string? AclOwner = null,
    bool AclInheritanceProtected = false,
    string AclRiskLevel = "not_collected",
    string? BroadAccessPrincipals = null,
    string? BroadAccessRights = null,
    string? AclError = null);

public sealed record FileInventorySnapshot(
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

public sealed record FileInventorySummary(
    Guid? SnapshotId,
    string? Server,
    string? Share,
    string? RootPath,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? FinishedUtc,
    string Status,
    long FileCount,
    long FolderCount,
    long TotalBytes,
    long ErrorCount,
    FileInventoryGovernanceMetrics Governance,
    IReadOnlyCollection<FileInventoryTopFolder> TopFolders,
    IReadOnlyCollection<FileInventoryTopExtension> TopExtensions,
    IReadOnlyCollection<FileInventoryContentCategory> ContentCategories,
    IReadOnlyCollection<FileInventoryFileCandidate> TopLargeFiles,
    IReadOnlyCollection<FileInventoryFileCandidate> TopInactiveFiles,
    IReadOnlyCollection<FileInventoryFileCandidate> TopExecutableFiles,
    IReadOnlyCollection<FileInventoryAgeBucket> AgeBuckets,
    FileInventoryObservedActivitySummary ObservedActivity,
    FileInventoryGrowthSummary Growth,
    FileInventoryCycleComparison Comparison,
    FileInventoryManagerialInsight Insight,
    FileInventoryExecutiveOverview ExecutiveOverview,
    IReadOnlyCollection<FileInventoryRecommendation> Recommendations,
    IReadOnlyCollection<FileInventoryAclRiskCandidate> AclRisks);

public sealed record FileInventoryGovernanceMetrics(
    long Inactive180DaysFileCount,
    long Inactive180DaysBytes,
    long Inactive365DaysFileCount,
    long Inactive365DaysBytes,
    long NeverAccessedFileCount,
    long NeverAccessedBytes,
    long LargeFileCount,
    long LargeFileBytes,
    long ExecutableFileCount,
    long ExecutableFileBytes,
    long AclCollectedFolderCount = 0,
    long AclErrorFolderCount = 0,
    long InheritanceProtectedFolderCount = 0,
    long BroadAccessFolderCount = 0,
    long ExpectedAclFolderCount = 0,
    long CriticalAclFolderCount = 0);

public sealed record FileInventoryAclRiskCandidate(
    string Path,
    string? Owner,
    bool InheritanceProtected,
    string RiskLevel,
    string? BroadAccessPrincipals,
    string? BroadAccessRights,
    string? Error);

public sealed record FileInventoryTopFolder(string Path, long FileCount, long FolderCount, long TotalBytes);

public sealed record FileInventoryTopExtension(string Extension, long FileCount, long TotalBytes);

public sealed record FileInventoryContentCategory(string Category, long FileCount, long TotalBytes);

public sealed record FileInventoryGrowthSummary(
    long FileCountDelta,
    long FolderCountDelta,
    long TotalBytesDelta,
    IReadOnlyCollection<FileInventoryFolderGrowth> TopGrowingFolders);

public sealed record FileInventoryCycleComparison(
    Guid? PreviousSnapshotId,
    DateTimeOffset? PreviousStartedUtc,
    long PreviousFileCount,
    long PreviousFolderCount,
    long PreviousTotalBytes,
    long PreviousErrorCount,
    decimal TotalBytesGrowthPercent,
    long ErrorCountDelta);

public sealed record FileInventoryFolderGrowth(
    string Path,
    long FileCountDelta,
    long FolderCountDelta,
    long TotalBytesDelta);

public sealed record FileInventoryFileCandidate(
    string Path,
    string Name,
    string? Extension,
    long SizeBytes,
    DateTimeOffset? ModifiedUtc,
    DateTimeOffset? AccessedUtc,
    int? AgeDays);

public sealed record FileInventoryAgeBucket(string Label, long FileCount, long TotalBytes);

public sealed record FileInventoryRecommendation(string Title, string Detail, string Severity);

public sealed record FileInventoryManagerialInsight(
    int Score,
    string Tone,
    string Trend,
    IReadOnlyCollection<string> Positives,
    IReadOnlyCollection<string> Stables,
    IReadOnlyCollection<string> Attentions);

public sealed record FileInventoryExecutiveOverview(
    IReadOnlyCollection<string> Headlines,
    IReadOnlyCollection<FileInventoryExecutiveArea> StorageHotspots,
    IReadOnlyCollection<FileInventoryExecutiveArea> ActivityHotspots,
    IReadOnlyCollection<FileInventoryExecutiveActor> UserHotspots,
    IReadOnlyCollection<FileInventoryExecutivePriority> Priorities);

public sealed record FileInventoryExecutiveArea(
    string Path,
    string Label,
    long PrimaryValue,
    string PrimaryText,
    string SecondaryText,
    string Tone);

public sealed record FileInventoryExecutiveActor(
    string User,
    long EventCount,
    string PrimaryText,
    string SecondaryText,
    string Tone);

public sealed record FileInventoryExecutivePriority(
    string Title,
    string Detail,
    string Severity,
    string Tone);

public sealed record FileInventoryObservedActivityInput(
    DateTimeOffset TimestampUtc,
    string Path,
    string User,
    string Action);

public sealed record FileInventoryObservedActivitySummary(
    long TotalEvents,
    IReadOnlyCollection<FileInventoryTopActivityFolder> TopFolders,
    IReadOnlyCollection<FileInventoryTopActivityUser> TopUsers);

public sealed record FileInventoryTopActivityFolder(
    string Path,
    long EventCount,
    DateTimeOffset LastActivityUtc,
    string TopAction);

public sealed record FileInventoryTopActivityUser(
    string User,
    long EventCount,
    DateTimeOffset LastActivityUtc,
    string TopAction);

public static class FileInventoryNormalizer
{
    public static FileInventoryItem Normalize(FileInventoryItemInput input)
    {
        var path = NormalizePath(input.Path);
        var rootPath = NormalizePath(input.RootPath);
        var relativePath = NormalizeRelativePath(input.RelativePath, path, rootPath);
        var itemType = NormalizeItemType(input.ItemType);
        var name = NormalizeName(input.Name, relativePath, path);

        return new FileInventoryItem(
            Id: Guid.NewGuid(),
            SnapshotId: input.SnapshotId ?? Guid.Empty,
            ScannedAtUtc: input.ScannedAtUtc ?? DateTimeOffset.UtcNow,
            Server: NormalizeText(input.Server, "UNKNOWN"),
            Share: NormalizeText(input.Share, "UNKNOWN"),
            RootPath: rootPath,
            Path: path,
            RelativePath: relativePath,
            Name: name,
            ItemType: itemType,
            Extension: itemType == "file" ? NormalizeExtension(path) : null,
            SizeBytes: itemType == "file" ? Math.Max(0, input.SizeBytes ?? 0) : 0,
            Depth: CalculateDepth(relativePath),
            CreatedUtc: input.CreatedUtc,
            ModifiedUtc: input.ModifiedUtc,
            AccessedUtc: input.AccessedUtc,
            Status: string.IsNullOrWhiteSpace(input.Error) ? "active" : "error",
            Error: string.IsNullOrWhiteSpace(input.Error) ? null : input.Error.Trim(),
            AclCollected: itemType == "folder" && input.AclCollected && string.IsNullOrWhiteSpace(input.AclError),
            AclOwner: NormalizeOptionalText(input.AclOwner),
            AclInheritanceProtected: itemType == "folder" && input.AclInheritanceProtected,
            AclRiskLevel: NormalizeAclRiskLevel(itemType, input.AclCollected, input.AclRiskLevel, input.AclError),
            BroadAccessPrincipals: NormalizeOptionalText(input.BroadAccessPrincipals),
            BroadAccessRights: NormalizeOptionalText(input.BroadAccessRights),
            AclError: NormalizeOptionalText(input.AclError));
    }

    public static string NormalizePath(string? path)
    {
        var normalized = NormalizeSeparators(path);
        return string.IsNullOrWhiteSpace(normalized) ? "UNKNOWN" : normalized.TrimEnd('\\');
    }

    public static string NormalizeParentPath(string path)
    {
        var normalized = NormalizePath(path);
        var index = normalized.LastIndexOf('\\');
        return index <= 0 ? normalized : normalized[..index];
    }

    private static string NormalizeText(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string? NormalizeOptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string NormalizeAclRiskLevel(string itemType, bool collected, string? riskLevel, string? error)
    {
        if (itemType != "folder")
        {
            return "not_collected";
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            return "error";
        }

        if (!collected)
        {
            return "not_collected";
        }

        return riskLevel?.Trim().ToLowerInvariant() is "clear" or "expected" or "attention" or "critical"
            ? riskLevel.Trim().ToLowerInvariant()
            : "clear";
    }

    private static string NormalizeRelativePath(string? relativePath, string path, string rootPath)
    {
        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            return NormalizeSeparators(relativePath).Trim('\\');
        }

        if (!string.IsNullOrWhiteSpace(rootPath)
            && !rootPath.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase)
            && path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return path[rootPath.Length..].Trim('\\');
        }

        return path;
    }

    private static string NormalizeName(string? name, string relativePath, string path)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        var source = string.IsNullOrWhiteSpace(relativePath) ? path : relativePath;
        var index = source.LastIndexOf('\\');
        return index < 0 ? source : source[(index + 1)..];
    }

    private static string NormalizeItemType(string? itemType)
    {
        return itemType?.Trim().Equals("folder", StringComparison.OrdinalIgnoreCase) == true
            || itemType?.Trim().Equals("directory", StringComparison.OrdinalIgnoreCase) == true
            ? "folder"
            : "file";
    }

    private static string? NormalizeExtension(string path)
    {
        var nameStart = path.LastIndexOf('\\') + 1;
        var fileName = nameStart > 0 ? path[nameStart..] : path;
        var dot = fileName.LastIndexOf('.');
        return dot <= 0 || dot == fileName.Length - 1 ? null : fileName[dot..].ToLowerInvariant();
    }

    private static int CalculateDepth(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return 0;
        }

        return relativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static string NormalizeSeparators(string? value)
    {
        return (value ?? string.Empty).Trim().Replace('/', '\\');
    }
}

public static class FileInventoryAnalyzer
{
    public static FileInventorySummary BuildSummary(
        FileInventorySnapshot? snapshot,
        IReadOnlyCollection<FileInventoryItem> items,
        int top = 10,
        DateTimeOffset? nowUtc = null)
    {
        var safeTop = Math.Clamp(top, 1, 100);
        var files = items.Where(item => item.ItemType == "file" && item.Status == "active").ToArray();
        var folders = items.Where(item => item.ItemType == "folder" && item.Status == "active").ToArray();
        var currentNow = nowUtc ?? DateTimeOffset.UtcNow;

        return new FileInventorySummary(
            SnapshotId: snapshot?.Id,
            Server: snapshot?.Server,
            Share: snapshot?.Share,
            RootPath: snapshot?.RootPath,
            StartedUtc: snapshot?.StartedUtc,
            FinishedUtc: snapshot?.FinishedUtc,
            Status: snapshot?.Status ?? "empty",
            FileCount: snapshot?.FileCount ?? files.LongLength,
            FolderCount: snapshot?.FolderCount ?? folders.LongLength,
            TotalBytes: snapshot?.TotalBytes ?? files.Sum(item => item.SizeBytes),
            ErrorCount: snapshot?.ErrorCount ?? items.LongCount(item => item.Status == "error"),
            Governance: BuildGovernanceMetrics(files, folders, currentNow),
            TopFolders: BuildTopFolders(items, safeTop),
            TopExtensions: BuildTopExtensions(files, safeTop),
            ContentCategories: BuildContentCategories(files),
            TopLargeFiles: BuildTopLargeFiles(files, safeTop, currentNow),
            TopInactiveFiles: BuildTopInactiveFiles(files, safeTop, currentNow),
            TopExecutableFiles: BuildTopExecutableFiles(files, safeTop, currentNow),
            AgeBuckets: BuildAgeBuckets(files, currentNow),
            ObservedActivity: BuildObservedActivitySummary(Array.Empty<FileInventoryObservedActivityInput>(), safeTop),
            Growth: BuildEmptyGrowthSummary(),
            Comparison: BuildEmptyCycleComparison(),
            Insight: BuildEmptyManagerialInsight(),
            ExecutiveOverview: BuildEmptyExecutiveOverview(),
            Recommendations: BuildRecommendations(snapshot, items, files, folders, currentNow),
            AclRisks: BuildAclRisks(folders, safeTop));
    }

    public static FileInventoryGrowthSummary BuildGrowthSummary(
        IReadOnlyCollection<FileInventoryItem> currentItems,
        IReadOnlyCollection<FileInventoryItem> previousItems,
        int top = 10)
    {
        var safeTop = Math.Clamp(top, 1, 100);
        var currentActive = currentItems.Where(item => item.Status == "active").ToArray();
        var previousActive = previousItems.Where(item => item.Status == "active").ToArray();
        var currentFiles = currentActive.Where(item => item.ItemType == "file").ToArray();
        var previousFiles = previousActive.Where(item => item.ItemType == "file").ToArray();
        var currentFolders = currentActive.Where(item => item.ItemType == "folder").ToArray();
        var previousFolders = previousActive.Where(item => item.ItemType == "folder").ToArray();
        var currentFolderStats = BuildFolderGrowthStats(currentActive);
        var previousFolderStats = BuildFolderGrowthStats(previousActive);

        return new FileInventoryGrowthSummary(
            FileCountDelta: currentFiles.LongLength - previousFiles.LongLength,
            FolderCountDelta: currentFolders.LongLength - previousFolders.LongLength,
            TotalBytesDelta: currentFiles.Sum(item => item.SizeBytes) - previousFiles.Sum(item => item.SizeBytes),
            TopGrowingFolders: currentFolderStats.Keys
                .Union(previousFolderStats.Keys, StringComparer.OrdinalIgnoreCase)
                .Select(path =>
                {
                    currentFolderStats.TryGetValue(path, out var current);
                    previousFolderStats.TryGetValue(path, out var previous);
                    return new FileInventoryFolderGrowth(
                        Path: path,
                        FileCountDelta: current.FileCount - previous.FileCount,
                        FolderCountDelta: current.FolderCount - previous.FolderCount,
                        TotalBytesDelta: current.TotalBytes - previous.TotalBytes);
                })
                .Where(item => item.FileCountDelta > 0 || item.FolderCountDelta > 0 || item.TotalBytesDelta > 0)
                .OrderByDescending(item => item.TotalBytesDelta)
                .ThenByDescending(item => item.FileCountDelta)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Take(safeTop)
                .ToArray());
    }

    public static FileInventoryGrowthSummary BuildEmptyGrowthSummary()
    {
        return new FileInventoryGrowthSummary(
            FileCountDelta: 0,
            FolderCountDelta: 0,
            TotalBytesDelta: 0,
            TopGrowingFolders: Array.Empty<FileInventoryFolderGrowth>());
    }

    public static FileInventoryCycleComparison BuildCycleComparison(
        FileInventorySnapshot currentSnapshot,
        FileInventorySnapshot previousSnapshot,
        FileInventoryGrowthSummary growth)
    {
        var previousTotalBytes = Math.Max(previousSnapshot.TotalBytes, 0);
        var growthPercent = previousTotalBytes > 0
            ? decimal.Round((decimal)growth.TotalBytesDelta / previousTotalBytes * 100m, 2, MidpointRounding.AwayFromZero)
            : 0m;

        return new FileInventoryCycleComparison(
            PreviousSnapshotId: previousSnapshot.Id,
            PreviousStartedUtc: previousSnapshot.StartedUtc,
            PreviousFileCount: previousSnapshot.FileCount,
            PreviousFolderCount: previousSnapshot.FolderCount,
            PreviousTotalBytes: previousSnapshot.TotalBytes,
            PreviousErrorCount: previousSnapshot.ErrorCount,
            TotalBytesGrowthPercent: growthPercent,
            ErrorCountDelta: currentSnapshot.ErrorCount - previousSnapshot.ErrorCount);
    }

    public static FileInventoryCycleComparison BuildEmptyCycleComparison()
    {
        return new FileInventoryCycleComparison(
            PreviousSnapshotId: null,
            PreviousStartedUtc: null,
            PreviousFileCount: 0,
            PreviousFolderCount: 0,
            PreviousTotalBytes: 0,
            PreviousErrorCount: 0,
            TotalBytesGrowthPercent: 0m,
            ErrorCountDelta: 0);
    }

    public static FileInventoryManagerialInsight BuildManagerialInsight(FileInventorySummary summary)
    {
        var totalBytesSafe = Math.Max(summary.TotalBytes, 1);
        var inactiveBytesRatio = (double)summary.Governance.Inactive365DaysBytes / totalBytesSafe;
        var neverAccessedBytesRatio = (double)summary.Governance.NeverAccessedBytes / totalBytesSafe;
        var warningRecommendations = summary.Recommendations.Count(item => item.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase));
        var infoRecommendations = Math.Max(0, summary.Recommendations.Count - warningRecommendations);
        var inactivePenalty = Math.Min(26, (int)Math.Round(inactiveBytesRatio * 42, MidpointRounding.AwayFromZero));
        var neverAccessPenalty = Math.Min(16, (int)Math.Round(neverAccessedBytesRatio * 28, MidpointRounding.AwayFromZero));
        var executablePenalty = Math.Min(15, checked((int)Math.Min(summary.Governance.ExecutableFileCount * 2, int.MaxValue)));
        var largePenalty = Math.Min(10, checked((int)Math.Min(summary.Governance.LargeFileCount, int.MaxValue)));
        var errorPenalty = Math.Min(15, checked((int)Math.Min(summary.ErrorCount * 4, int.MaxValue)));
        var recommendationPenalty = Math.Min(18, warningRecommendations * 5 + infoRecommendations * 2);
        var score = Math.Clamp(100 - inactivePenalty - neverAccessPenalty - executablePenalty - largePenalty - errorPenalty - recommendationPenalty, 0, 100);
        var growthPercent = (double)summary.Comparison.TotalBytesGrowthPercent;
        var errorDelta = summary.Comparison.ErrorCountDelta;

        var positives = new List<string>();
        var stables = new List<string>();
        var attentions = new List<string>();

        if (summary.ErrorCount == 0)
        {
            positives.Add("Leitura sem erro no ultimo scan.");
        }
        else
        {
            attentions.Add($"{summary.ErrorCount} erro(s) de leitura no ultimo scan.");
        }

        if (summary.ObservedActivity.TotalEvents > 0)
        {
            positives.Add("Atividade observada cruzada com a timeline persistida.");
        }
        else
        {
            stables.Add("Sem atividade recente associada ao snapshot atual.");
        }

        if (summary.Comparison.PreviousSnapshotId is null)
        {
            stables.Add("Primeiro ciclo comparavel ainda nao disponivel.");
        }
        else if (Math.Abs(growthPercent) <= 3)
        {
            stables.Add("Volume estavel entre os dois ultimos ciclos.");
        }
        else if (growthPercent > 3)
        {
            attentions.Add($"Crescimento de {FormatPercent(growthPercent)} desde o ciclo anterior.");
        }
        else
        {
            positives.Add($"Reducao de {FormatPercent(Math.Abs(growthPercent))} no volume monitorado.");
        }

        if (summary.Governance.Inactive365DaysFileCount > 0)
        {
            attentions.Add($"{summary.Governance.Inactive365DaysFileCount.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"))} arquivo(s) frio(s) ha mais de um ano.");
        }
        else
        {
            positives.Add("Nenhum arquivo frio acima de 365 dias no snapshot atual.");
        }

        if (summary.Governance.ExecutableFileCount > 0)
        {
            attentions.Add($"{summary.Governance.ExecutableFileCount.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"))} executavel(is) ou script(s) exigem revisao.");
        }
        else
        {
            positives.Add("Nenhum executavel ou script exposto no recorte principal.");
        }

        if (summary.Recommendations.Count == 0)
        {
            positives.Add("Sem recomendacoes abertas no ultimo snapshot.");
        }
        else
        {
            attentions.Add($"{summary.Recommendations.Count.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"))} recomendacao(oes) aguardando tratamento.");
        }

        var trend = "stable";
        if (score >= 85 && errorDelta <= 0 && growthPercent <= 3)
        {
            trend = "improved";
        }
        else if (score < 65 || errorDelta > 0 || growthPercent > 12)
        {
            trend = "worsened";
        }

        var tone = score >= 85 ? "green" : score >= 65 ? "amber" : "danger";

        return new FileInventoryManagerialInsight(
            Score: score,
            Tone: tone,
            Trend: trend,
            Positives: positives,
            Stables: stables,
            Attentions: attentions);
    }

    public static FileInventoryManagerialInsight BuildEmptyManagerialInsight()
    {
        return new FileInventoryManagerialInsight(
            Score: 0,
            Tone: "green",
            Trend: "stable",
            Positives: Array.Empty<string>(),
            Stables: Array.Empty<string>(),
            Attentions: Array.Empty<string>());
    }

    public static FileInventoryExecutiveOverview BuildExecutiveOverview(FileInventorySummary summary)
    {
        var ptBr = CultureInfo.GetCultureInfo("pt-BR");
        var headlines = new List<string>();

        if (summary.TopFolders.Count > 0)
        {
            var largestArea = summary.TopFolders.First();
            headlines.Add($"Maior concentracao de dados em {largestArea.Path} com {FormatBytes(largestArea.TotalBytes)}.");
        }

        if (summary.ObservedActivity.TopFolders.Count > 0)
        {
            var activityArea = summary.ObservedActivity.TopFolders.First();
            headlines.Add($"Area mais movimentada: {activityArea.Path} com {activityArea.EventCount.ToString("N0", ptBr)} evento(s).");
        }

        if (summary.ObservedActivity.TopUsers.Count > 0)
        {
            var activityUser = summary.ObservedActivity.TopUsers.First();
            headlines.Add($"Usuario mais ativo no periodo: {activityUser.User} com {activityUser.EventCount.ToString("N0", ptBr)} acao(oes).");
        }

        if (summary.Governance.Inactive365DaysFileCount > 0)
        {
            headlines.Add($"{summary.Governance.Inactive365DaysFileCount.ToString("N0", ptBr)} arquivo(s) estao frios ha mais de 365 dias.");
        }

        if (summary.Recommendations.Count > 0)
        {
            headlines.Add($"{summary.Recommendations.Count.ToString("N0", ptBr)} recomendacao(oes) automaticas seguem abertas.");
        }

        var storageHotspots = summary.TopFolders
            .Take(3)
            .Select(item => new FileInventoryExecutiveArea(
                Path: item.Path,
                Label: item.Path,
                PrimaryValue: item.TotalBytes,
                PrimaryText: FormatBytes(item.TotalBytes),
                SecondaryText: $"{item.FileCount.ToString("N0", ptBr)} arquivo(s) · {item.FolderCount.ToString("N0", ptBr)} pasta(s)",
                Tone: "navy"))
            .ToArray();

        var activityHotspots = summary.ObservedActivity.TopFolders
            .Take(3)
            .Select(item => new FileInventoryExecutiveArea(
                Path: item.Path,
                Label: item.Path,
                PrimaryValue: item.EventCount,
                PrimaryText: $"{item.EventCount.ToString("N0", ptBr)} evento(s)",
                SecondaryText: $"Ultima atividade em {item.LastActivityUtc:dd/MM/yyyy HH:mm} · acao dominante {NormalizeActionLabel(item.TopAction)}",
                Tone: "blue"))
            .ToArray();

        var userHotspots = summary.ObservedActivity.TopUsers
            .Take(3)
            .Select(item => new FileInventoryExecutiveActor(
                User: item.User,
                EventCount: item.EventCount,
                PrimaryText: $"{item.EventCount.ToString("N0", ptBr)} evento(s)",
                SecondaryText: $"Ultima atividade em {item.LastActivityUtc:dd/MM/yyyy HH:mm} · acao dominante {NormalizeActionLabel(item.TopAction)}",
                Tone: "green"))
            .ToArray();

        var priorities = summary.Recommendations
            .Take(4)
            .Select(item => new FileInventoryExecutivePriority(
                Title: item.Title,
                Detail: item.Detail,
                Severity: item.Severity,
                Tone: item.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase) ? "danger" : "amber"))
            .ToArray();

        return new FileInventoryExecutiveOverview(
            Headlines: headlines.Take(5).ToArray(),
            StorageHotspots: storageHotspots,
            ActivityHotspots: activityHotspots,
            UserHotspots: userHotspots,
            Priorities: priorities);
    }

    public static FileInventoryExecutiveOverview BuildEmptyExecutiveOverview()
    {
        return new FileInventoryExecutiveOverview(
            Headlines: Array.Empty<string>(),
            StorageHotspots: Array.Empty<FileInventoryExecutiveArea>(),
            ActivityHotspots: Array.Empty<FileInventoryExecutiveArea>(),
            UserHotspots: Array.Empty<FileInventoryExecutiveActor>(),
            Priorities: Array.Empty<FileInventoryExecutivePriority>());
    }

    private static Dictionary<string, FolderGrowthStats> BuildFolderGrowthStats(IReadOnlyCollection<FileInventoryItem> items)
    {
        return items
            .GroupBy(item => item.ItemType == "folder" ? item.Path : FileInventoryNormalizer.NormalizeParentPath(item.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new FolderGrowthStats(
                    FileCount: group.LongCount(item => item.ItemType == "file"),
                    FolderCount: group.LongCount(item => item.ItemType == "folder"),
                    TotalBytes: group.Where(item => item.ItemType == "file").Sum(item => item.SizeBytes)),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string FormatPercent(double value)
    {
        return $"{value:0.#}%".Replace(".", ",");
    }

    private static string NormalizeActionLabel(string action)
    {
        return action.Trim().ToLowerInvariant() switch
        {
            "created" => "criacao",
            "modified" => "alteracao",
            "deleted" => "exclusao",
            "renamed" => "rename",
            "moved" => "movimentacao",
            "accessed" => "acesso",
            _ => action
        };
    }

    public static FileInventoryObservedActivitySummary BuildObservedActivitySummary(
        IReadOnlyCollection<FileInventoryObservedActivityInput> events,
        int top = 10)
    {
        var safeTop = Math.Clamp(top, 1, 100);
        var normalizedEvents = events
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .Where(item => !IsMachineAccount(item.User))
            .Select(item => new FileInventoryObservedActivityInput(
                TimestampUtc: item.TimestampUtc,
                Path: FileInventoryNormalizer.NormalizePath(item.Path),
                User: string.IsNullOrWhiteSpace(item.User) ? "UNKNOWN" : item.User.Trim(),
                Action: string.IsNullOrWhiteSpace(item.Action) ? "unknown" : item.Action.Trim()))
            .ToArray();

        return new FileInventoryObservedActivitySummary(
            TotalEvents: normalizedEvents.LongLength,
            TopFolders: normalizedEvents
                .GroupBy(item => FileInventoryNormalizer.NormalizeParentPath(item.Path), StringComparer.OrdinalIgnoreCase)
                .Select(group => new FileInventoryTopActivityFolder(
                    Path: group.Key,
                    EventCount: group.LongCount(),
                    LastActivityUtc: group.Max(item => item.TimestampUtc),
                    TopAction: group
                        .GroupBy(item => item.Action, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(actionGroup => actionGroup.LongCount())
                        .ThenBy(actionGroup => actionGroup.Key, StringComparer.OrdinalIgnoreCase)
                        .First().Key))
                .OrderByDescending(item => item.EventCount)
                .ThenByDescending(item => item.LastActivityUtc)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Take(safeTop)
                .ToArray(),
            TopUsers: normalizedEvents
                .GroupBy(item => item.User, StringComparer.OrdinalIgnoreCase)
                .Select(group => new FileInventoryTopActivityUser(
                    User: group.Key,
                    EventCount: group.LongCount(),
                    LastActivityUtc: group.Max(item => item.TimestampUtc),
                    TopAction: group
                        .GroupBy(item => item.Action, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(actionGroup => actionGroup.LongCount())
                        .ThenBy(actionGroup => actionGroup.Key, StringComparer.OrdinalIgnoreCase)
                        .First().Key))
                .OrderByDescending(item => item.EventCount)
                .ThenByDescending(item => item.LastActivityUtc)
                .ThenBy(item => item.User, StringComparer.OrdinalIgnoreCase)
                .Take(safeTop)
                .ToArray());
    }

    private static bool IsMachineAccount(string? user)
    {
        if (string.IsNullOrWhiteSpace(user))
        {
            return false;
        }

        var normalized = user.Trim();
        var slash = normalized.LastIndexOf('\\');
        var account = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        return account.EndsWith("$", StringComparison.Ordinal);
    }

    private readonly record struct FolderGrowthStats(long FileCount, long FolderCount, long TotalBytes);

    private static IReadOnlyCollection<FileInventoryTopFolder> BuildTopFolders(
        IReadOnlyCollection<FileInventoryItem> items,
        int top)
    {
        return items
            .Where(item => item.Status == "active")
            .GroupBy(item => item.ItemType == "folder" ? item.Path : FileInventoryNormalizer.NormalizeParentPath(item.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => new FileInventoryTopFolder(
                Path: group.Key,
                FileCount: group.LongCount(item => item.ItemType == "file"),
                FolderCount: group.LongCount(item => item.ItemType == "folder"),
                TotalBytes: group.Where(item => item.ItemType == "file").Sum(item => item.SizeBytes)))
            .Where(item => item.FileCount > 0 || item.FolderCount > 0)
            .OrderByDescending(item => item.TotalBytes)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Take(top)
            .ToArray();
    }

    private static IReadOnlyCollection<FileInventoryTopExtension> BuildTopExtensions(
        IReadOnlyCollection<FileInventoryItem> files,
        int top)
    {
        return files
            .GroupBy(item => item.Extension ?? "(sem extensao)", StringComparer.OrdinalIgnoreCase)
            .Select(group => new FileInventoryTopExtension(
                Extension: group.Key,
                FileCount: group.LongCount(),
                TotalBytes: group.Sum(item => item.SizeBytes)))
            .OrderByDescending(item => item.TotalBytes)
            .ThenBy(item => item.Extension, StringComparer.OrdinalIgnoreCase)
            .Take(top)
            .ToArray();
    }

    private static IReadOnlyCollection<FileInventoryContentCategory> BuildContentCategories(IReadOnlyCollection<FileInventoryItem> files)
    {
        return files
            .GroupBy(item => ClassifyContentCategory(item.Extension), StringComparer.OrdinalIgnoreCase)
            .Select(group => new FileInventoryContentCategory(
                Category: group.Key,
                FileCount: group.LongCount(),
                TotalBytes: group.Sum(item => item.SizeBytes)))
            .OrderByDescending(item => item.TotalBytes)
            .ThenBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string ClassifyContentCategory(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return "outros";
        }

        return extension.Trim().ToLowerInvariant() switch
        {
            ".doc" or ".docx" or ".odt" or ".rtf" or ".txt" or ".pdf" or ".xls" or ".xlsx" or ".ods" or ".csv" or ".ppt" or ".pptx" or ".odp" => "documentos",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".webp" or ".svg" => "imagens",
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" or ".mpg" or ".mpeg" => "videos",
            ".mp3" or ".wav" or ".wma" or ".aac" or ".flac" or ".ogg" => "audio",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" => "compactados",
            ".exe" or ".msi" or ".jar" or ".scr" or ".com" or ".rpm" or ".deb" or ".pkg" or ".plugin" => "instaladores",
            ".ps1" or ".bat" or ".cmd" or ".vbs" or ".js" => "scripts",
            ".dll" or ".sys" => "binarios",
            ".kdbx" => "dados sensiveis",
            ".dwg" or ".dxf" => "projetos cad",
            ".bak" or ".sql" or ".db" or ".mdb" or ".accdb" or ".sqlite" or ".log" => "dados",
            ".iso" or ".ova" or ".vhd" or ".vhdx" or ".vmdk" => "imagens de disco",
            _ => "outros"
        };
    }

    private static IReadOnlyCollection<FileInventoryFileCandidate> BuildTopLargeFiles(
        IReadOnlyCollection<FileInventoryItem> files,
        int top,
        DateTimeOffset nowUtc)
    {
        return files
            .OrderByDescending(item => item.SizeBytes)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Take(top)
            .Select(item => BuildFileCandidate(item, nowUtc))
            .ToArray();
    }

    private static IReadOnlyCollection<FileInventoryFileCandidate> BuildTopInactiveFiles(
        IReadOnlyCollection<FileInventoryItem> files,
        int top,
        DateTimeOffset nowUtc)
    {
        return files
            .Select(item => new
            {
                Item = item,
                ReferenceUtc = item.ModifiedUtc ?? item.CreatedUtc ?? item.AccessedUtc
            })
            .Where(item => item.ReferenceUtc is not null)
            .OrderBy(item => item.ReferenceUtc)
            .ThenByDescending(item => item.Item.SizeBytes)
            .ThenBy(item => item.Item.Path, StringComparer.OrdinalIgnoreCase)
            .Take(top)
            .Select(item => BuildFileCandidate(item.Item, nowUtc))
            .ToArray();
    }

    private static IReadOnlyCollection<FileInventoryFileCandidate> BuildTopExecutableFiles(
        IReadOnlyCollection<FileInventoryItem> files,
        int top,
        DateTimeOffset nowUtc)
    {
        return files
            .Where(item => IsExecutableOrScriptExtension(item.Extension))
            .OrderByDescending(item => item.SizeBytes)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Take(top)
            .Select(item => BuildFileCandidate(item, nowUtc))
            .ToArray();
    }

    private static FileInventoryFileCandidate BuildFileCandidate(FileInventoryItem item, DateTimeOffset nowUtc)
    {
        var reference = item.ModifiedUtc ?? item.CreatedUtc ?? item.AccessedUtc;
        return new FileInventoryFileCandidate(
            Path: item.Path,
            Name: item.Name,
            Extension: item.Extension,
            SizeBytes: item.SizeBytes,
            ModifiedUtc: item.ModifiedUtc,
            AccessedUtc: item.AccessedUtc,
            AgeDays: reference is null ? null : Math.Max(0, (int)nowUtc.Subtract(reference.Value).TotalDays));
    }

    private static IReadOnlyCollection<FileInventoryAgeBucket> BuildAgeBuckets(
        IReadOnlyCollection<FileInventoryItem> files,
        DateTimeOffset nowUtc)
    {
        var buckets = new[]
        {
            BuildAgeBucket("0-30 dias", files, nowUtc, minDays: 0, maxDays: 30),
            BuildAgeBucket("31-90 dias", files, nowUtc, minDays: 31, maxDays: 90),
            BuildAgeBucket("91-180 dias", files, nowUtc, minDays: 91, maxDays: 180),
            BuildAgeBucket("181-365 dias", files, nowUtc, minDays: 181, maxDays: 365),
            BuildAgeBucket("+365 dias", files, nowUtc, minDays: 366, maxDays: null)
        };

        return buckets;
    }

    private static FileInventoryAgeBucket BuildAgeBucket(
        string label,
        IReadOnlyCollection<FileInventoryItem> files,
        DateTimeOffset nowUtc,
        int minDays,
        int? maxDays)
    {
        var bucketFiles = files.Where(item =>
        {
            var reference = item.AccessedUtc ?? item.ModifiedUtc ?? item.CreatedUtc;
            if (reference is null)
            {
                return false;
            }

            var ageDays = Math.Max(0, (int)nowUtc.Subtract(reference.Value).TotalDays);
            return ageDays >= minDays && (maxDays is null || ageDays <= maxDays.Value);
        }).ToArray();

        return new FileInventoryAgeBucket(
            Label: label,
            FileCount: bucketFiles.LongLength,
            TotalBytes: bucketFiles.Sum(item => item.SizeBytes));
    }

    private static FileInventoryGovernanceMetrics BuildGovernanceMetrics(
        IReadOnlyCollection<FileInventoryItem> files,
        IReadOnlyCollection<FileInventoryItem> folders,
        DateTimeOffset nowUtc)
    {
        var inactive180 = files.Where(item => IsInactiveForDays(item, nowUtc, 180)).ToArray();
        var inactive365 = files.Where(item => IsInactiveForDays(item, nowUtc, 365)).ToArray();
        var neverAccessed = files.Where(item => item.AccessedUtc is null).ToArray();
        var largeFiles = files.Where(item => item.SizeBytes >= 1024L * 1024L * 1024L).ToArray();
        var executableFiles = files.Where(item => IsExecutableOrScriptExtension(item.Extension)).ToArray();

        return new FileInventoryGovernanceMetrics(
            Inactive180DaysFileCount: inactive180.LongLength,
            Inactive180DaysBytes: inactive180.Sum(item => item.SizeBytes),
            Inactive365DaysFileCount: inactive365.LongLength,
            Inactive365DaysBytes: inactive365.Sum(item => item.SizeBytes),
            NeverAccessedFileCount: neverAccessed.LongLength,
            NeverAccessedBytes: neverAccessed.Sum(item => item.SizeBytes),
            LargeFileCount: largeFiles.LongLength,
            LargeFileBytes: largeFiles.Sum(item => item.SizeBytes),
            ExecutableFileCount: executableFiles.LongLength,
            ExecutableFileBytes: executableFiles.Sum(item => item.SizeBytes),
            AclCollectedFolderCount: folders.LongCount(item => item.AclCollected),
            AclErrorFolderCount: folders.LongCount(item => item.AclRiskLevel == "error"),
            InheritanceProtectedFolderCount: folders.LongCount(item => item.AclCollected && item.AclInheritanceProtected),
            BroadAccessFolderCount: folders.LongCount(item => item.AclCollected && !string.IsNullOrWhiteSpace(item.BroadAccessPrincipals)),
            ExpectedAclFolderCount: folders.LongCount(item => item.AclRiskLevel == "expected"),
            CriticalAclFolderCount: folders.LongCount(item => item.AclRiskLevel == "critical"));
    }

    private static IReadOnlyCollection<FileInventoryAclRiskCandidate> BuildAclRisks(
        IReadOnlyCollection<FileInventoryItem> folders,
        int top)
    {
        return folders
            .Where(item => item.AclRiskLevel is "critical" or "attention" or "error")
            .OrderBy(item => item.AclRiskLevel switch
            {
                "critical" => 0,
                "error" => 1,
                _ => 2
            })
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(top, 1, 100))
            .Select(item => new FileInventoryAclRiskCandidate(
                Path: item.Path,
                Owner: item.AclOwner,
                InheritanceProtected: item.AclInheritanceProtected,
                RiskLevel: item.AclRiskLevel,
                BroadAccessPrincipals: item.BroadAccessPrincipals,
                BroadAccessRights: item.BroadAccessRights,
                Error: item.AclError))
            .ToArray();
    }

    private static bool IsExecutableOrScriptExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return extension.Trim().ToLowerInvariant() is ".exe" or ".msi" or ".dll" or ".ps1" or ".bat" or ".cmd" or ".vbs" or ".js" or ".jar" or ".scr" or ".com";
    }

    private static IReadOnlyCollection<FileInventoryRecommendation> BuildRecommendations(
        FileInventorySnapshot? snapshot,
        IReadOnlyCollection<FileInventoryItem> items,
        IReadOnlyCollection<FileInventoryItem> files,
        IReadOnlyCollection<FileInventoryItem> folders,
        DateTimeOffset nowUtc)
    {
        var recommendations = new List<FileInventoryRecommendation>();
        var metrics = BuildGovernanceMetrics(files, folders, nowUtc);
        var errorCount = snapshot?.ErrorCount ?? items.LongCount(item => item.Status == "error");
        var totalBytes = snapshot?.TotalBytes ?? files.Sum(item => item.SizeBytes);
        var inactive365Percent = totalBytes == 0 ? 0 : metrics.Inactive365DaysBytes * 100m / totalBytes;
        var inactive180Percent = totalBytes == 0 ? 0 : metrics.Inactive180DaysBytes * 100m / totalBytes;

        if (metrics.Inactive365DaysFileCount > 0)
        {
            recommendations.Add(new FileInventoryRecommendation(
                Title: "Arquivos sem uso ha mais de 1 ano",
                Detail: $"{metrics.Inactive365DaysFileCount:N0} arquivo(s), {FormatBytes(metrics.Inactive365DaysBytes)} ({inactive365Percent:N1}% do volume) podem entrar em politica de arquivamento.",
                Severity: inactive365Percent >= 25 ? "warning" : "info"));
        }

        if (metrics.Inactive365DaysFileCount == 0 && metrics.Inactive180DaysFileCount > 0)
        {
            recommendations.Add(new FileInventoryRecommendation(
                Title: "Arquivos frios acima de 180 dias",
                Detail: $"{metrics.Inactive180DaysFileCount:N0} arquivo(s), {FormatBytes(metrics.Inactive180DaysBytes)} ({inactive180Percent:N1}% do volume) merecem revisao gerencial.",
                Severity: inactive180Percent >= 25 ? "warning" : "info"));
        }

        if (metrics.LargeFileCount > 0)
        {
            recommendations.Add(new FileInventoryRecommendation(
                Title: "Arquivos grandes concentrando espaco",
                Detail: $"{metrics.LargeFileCount:N0} arquivo(s) acima de 1 GB somam {FormatBytes(metrics.LargeFileBytes)}.",
                Severity: "info"));
        }

        if (metrics.ExecutableFileCount > 0)
        {
            recommendations.Add(new FileInventoryRecommendation(
                Title: "Executaveis e scripts no compartilhamento",
                Detail: $"{metrics.ExecutableFileCount:N0} arquivo(s) executavel(is) ou script(s) somam {FormatBytes(metrics.ExecutableFileBytes)}. Revise necessidade, localizacao e permissao.",
                Severity: "warning"));
        }

        if (errorCount > 0)
        {
            recommendations.Add(new FileInventoryRecommendation(
                Title: "Itens sem leitura no scan",
                Detail: $"{errorCount:N0} item(ns) nao puderam ser lidos. Revise permissao da conta de scan ou caminhos inacessiveis.",
                Severity: "warning"));
        }

        if (metrics.CriticalAclFolderCount > 0)
        {
            recommendations.Add(new FileInventoryRecommendation(
                Title: "Pastas com permissao ampla de escrita",
                Detail: $"{metrics.CriticalAclFolderCount:N0} pasta(s) permitem alteracao por grupos amplos. Valide necessidade e reduza o acesso ao menor privilegio.",
                Severity: "warning"));
        }
        else if (metrics.BroadAccessFolderCount > 0)
        {
            recommendations.Add(new FileInventoryRecommendation(
                Title: "Pastas com acesso amplo",
                Detail: $"{metrics.BroadAccessFolderCount:N0} pasta(s) possuem leitura ou execucao concedida a grupos amplos.",
                Severity: "info"));
        }

        if (metrics.InheritanceProtectedFolderCount > 0)
        {
            recommendations.Add(new FileInventoryRecommendation(
                Title: "Pastas fora da heranca de permissoes",
                Detail: $"{metrics.InheritanceProtectedFolderCount:N0} pasta(s) usam ACL protegida. Confirme se a excecao continua justificada.",
                Severity: "info"));
        }

        if (metrics.AclErrorFolderCount > 0)
        {
            recommendations.Add(new FileInventoryRecommendation(
                Title: "ACLs sem leitura",
                Detail: $"{metrics.AclErrorFolderCount:N0} pasta(s) nao tiveram a ACL lida. Revise a conta e os privilegios usados pelo scan.",
                Severity: "warning"));
        }

        return recommendations
            .OrderByDescending(item => item.Severity == "warning")
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
    }

    private static bool IsInactiveForDays(FileInventoryItem item, DateTimeOffset nowUtc, int days)
    {
        var reference = item.AccessedUtc ?? item.ModifiedUtc ?? item.CreatedUtc;
        return reference is not null && nowUtc.Subtract(reference.Value).TotalDays >= days;
    }

    private static string FormatBytes(long bytes)
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
}
