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
    IReadOnlyCollection<FileInventoryTopFolder> TopFolders,
    IReadOnlyCollection<FileInventoryTopExtension> TopExtensions,
    IReadOnlyCollection<FileInventoryAgeBucket> AgeBuckets);

public sealed record FileInventoryTopFolder(string Path, long FileCount, long FolderCount, long TotalBytes);

public sealed record FileInventoryTopExtension(string Extension, long FileCount, long TotalBytes);

public sealed record FileInventoryAgeBucket(string Label, long FileCount, long TotalBytes);

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
            TopFolders: BuildTopFolders(items, safeTop),
            TopExtensions: BuildTopExtensions(files, safeTop),
            AgeBuckets: BuildAgeBuckets(files, currentNow));
    }

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
}
