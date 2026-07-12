namespace FileServerMonitor.Core;

public static class KnownPathMap
{
    public static void RelocateDescendants(
        IDictionary<string, string> pathsByFileReference,
        string previousPath,
        string nextPath)
    {
        var previousRoot = NormalizePath(previousPath);
        var nextRoot = NormalizePath(nextPath);

        if (previousRoot.Length == 0 || nextRoot.Length == 0 || previousRoot.Equals(nextRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var item in pathsByFileReference.ToArray())
        {
            var currentPath = NormalizePath(item.Value);
            if (!currentPath.Equals(previousRoot, StringComparison.OrdinalIgnoreCase)
                && !currentPath.StartsWith($"{previousRoot}\\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = currentPath[previousRoot.Length..];
            pathsByFileReference[item.Key] = $"{nextRoot}{suffix}";
        }
    }

    private static string NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim().Replace('/', '\\').TrimEnd('\\');
    }
}
