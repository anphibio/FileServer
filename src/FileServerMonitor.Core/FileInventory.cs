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
    string? Error);

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
    string? Error);

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
    IReadOnlyCollection<FileInventoryRecommendation> Recommendations);

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
    long ExecutableFileBytes);

public sealed record FileInventoryTopFolder(string Path, long FileCount, long FolderCount, long TotalBytes);

public sealed record FileInventoryTopExtension(string Extension, long FileCount, long TotalBytes);

public sealed record FileInventoryContentCategory(string Category, long FileCount, long TotalBytes);

public sealed record FileInventoryGrowthSummary(
    long FileCountDelta,
    long FolderCountDelta,
    long TotalBytesDelta,
    IReadOnlyCollection<FileInventoryFolderGrowth> TopGrowingFolders);

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
            Error: string.IsNullOrWhiteSpace(input.Error) ? null : input.Error.Trim());
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
            Governance: BuildGovernanceMetrics(files, currentNow),
            TopFolders: BuildTopFolders(items, safeTop),
            TopExtensions: BuildTopExtensions(files, safeTop),
            ContentCategories: BuildContentCategories(files),
            TopLargeFiles: BuildTopLargeFiles(files, safeTop, currentNow),
            TopInactiveFiles: BuildTopInactiveFiles(files, safeTop, currentNow),
            TopExecutableFiles: BuildTopExecutableFiles(files, safeTop, currentNow),
            AgeBuckets: BuildAgeBuckets(files, currentNow),
            ObservedActivity: BuildObservedActivitySummary(Array.Empty<FileInventoryObservedActivityInput>(), safeTop),
            Growth: BuildEmptyGrowthSummary(),
            Recommendations: BuildRecommendations(snapshot, items, files, currentNow));
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
            ExecutableFileBytes: executableFiles.Sum(item => item.SizeBytes));
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
        DateTimeOffset nowUtc)
    {
        var recommendations = new List<FileInventoryRecommendation>();
        var metrics = BuildGovernanceMetrics(files, nowUtc);
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
