namespace FileServerMonitor.Core;

public sealed record FileInventoryAclEntryInput(
    string? Principal,
    string? Sid,
    string? Rights,
    string? AccessType,
    bool IsInherited);

public sealed record FileInventoryAclAssessment(
    bool Collected,
    string? Owner,
    bool InheritanceProtected,
    string RiskLevel,
    string? BroadAccessPrincipals,
    string? BroadAccessRights,
    string? Error);

public sealed record FileInventoryAclPolicy(
    IReadOnlyCollection<string> ExpectedBroadReadPrincipals,
    IReadOnlyCollection<string> ExpectedBroadWritePrincipals)
{
    public static FileInventoryAclPolicy Empty { get; } = new(
        Array.Empty<string>(),
        Array.Empty<string>());
}

public static class FileInventoryAclClassifier
{
    private static readonly HashSet<string> BroadSids = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-1-0",       // Everyone
        "S-1-5-11",      // Authenticated Users
        "S-1-5-32-545"   // BUILTIN\Users
    };

    private static readonly string[] WriteRights =
    {
        "fullcontrol",
        "modify",
        "write",
        "createfiles",
        "createdirectories",
        "delete",
        "changepermissions",
        "takeownership"
    };

    public static FileInventoryAclAssessment Assess(
        string? owner,
        bool inheritanceProtected,
        IReadOnlyCollection<FileInventoryAclEntryInput>? entries,
        string? error,
        FileInventoryAclPolicy? policy = null)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return new FileInventoryAclAssessment(
                Collected: false,
                Owner: Normalize(owner),
                InheritanceProtected: inheritanceProtected,
                RiskLevel: "error",
                BroadAccessPrincipals: null,
                BroadAccessRights: null,
                Error: error.Trim());
        }

        var broadEntries = (entries ?? Array.Empty<FileInventoryAclEntryInput>())
            .Where(IsAllowedBroadEntry)
            .ToArray();
        var effectivePolicy = policy ?? FileInventoryAclPolicy.Empty;
        var unapprovedEntries = broadEntries
            .Where(entry => !IsExpected(entry, effectivePolicy))
            .ToArray();
        var hasUnapprovedBroadWrite = unapprovedEntries.Any(entry => ContainsWriteRight(entry.Rights));
        var riskLevel = hasUnapprovedBroadWrite
            ? "critical"
            : unapprovedEntries.Length > 0 || inheritanceProtected
                ? "attention"
                : broadEntries.Length > 0
                    ? "expected"
                    : "clear";

        return new FileInventoryAclAssessment(
            Collected: true,
            Owner: Normalize(owner),
            InheritanceProtected: inheritanceProtected,
            RiskLevel: riskLevel,
            BroadAccessPrincipals: JoinDistinct(broadEntries.Select(ResolvePrincipal)),
            BroadAccessRights: JoinDistinct(broadEntries.Select(entry => Normalize(entry.Rights))),
            Error: null);
    }

    private static bool IsExpected(FileInventoryAclEntryInput entry, FileInventoryAclPolicy policy)
    {
        var approved = ContainsWriteRight(entry.Rights)
            ? policy.ExpectedBroadWritePrincipals
            : policy.ExpectedBroadReadPrincipals.Concat(policy.ExpectedBroadWritePrincipals);
        return approved.Any(value => IdentityMatches(value, entry));
    }

    private static bool IdentityMatches(string configuredIdentity, FileInventoryAclEntryInput entry)
    {
        var configured = Normalize(configuredIdentity);
        if (configured is null)
        {
            return false;
        }

        return IdentityValueMatches(configured, entry.Sid)
            || IdentityValueMatches(configured, entry.Principal);
    }

    private static bool IdentityValueMatches(string configured, string? observedIdentity)
    {
        var observed = Normalize(observedIdentity);
        if (observed is null)
        {
            return false;
        }

        return configured.Equals(observed, StringComparison.OrdinalIgnoreCase)
            || GetIdentityLeaf(configured).Equals(GetIdentityLeaf(observed), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetIdentityLeaf(string identity)
    {
        var separator = Math.Max(identity.LastIndexOf('\\'), identity.LastIndexOf('/'));
        return separator >= 0 && separator < identity.Length - 1
            ? identity[(separator + 1)..]
            : identity;
    }

    private static bool IsAllowedBroadEntry(FileInventoryAclEntryInput entry)
    {
        return !string.Equals(entry.AccessType?.Trim(), "Deny", StringComparison.OrdinalIgnoreCase)
            && IsBroadPrincipal(entry.Sid, entry.Principal);
    }

    private static bool IsBroadPrincipal(string? sid, string? principal)
    {
        var normalizedSid = Normalize(sid);
        if (normalizedSid is not null
            && (BroadSids.Contains(normalizedSid) || normalizedSid.EndsWith("-513", StringComparison.Ordinal)))
        {
            return true;
        }

        var normalizedPrincipal = Normalize(principal);
        return normalizedPrincipal is not null
            && (normalizedPrincipal.EndsWith("\\Everyone", StringComparison.OrdinalIgnoreCase)
                || normalizedPrincipal.Equals("Everyone", StringComparison.OrdinalIgnoreCase)
                || normalizedPrincipal.EndsWith("\\Authenticated Users", StringComparison.OrdinalIgnoreCase)
                || normalizedPrincipal.Equals("Authenticated Users", StringComparison.OrdinalIgnoreCase)
                || normalizedPrincipal.EndsWith("\\Domain Users", StringComparison.OrdinalIgnoreCase)
                || normalizedPrincipal.EndsWith("\\Users", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsWriteRight(string? rights)
    {
        var normalized = Normalize(rights)?.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalized is not null && WriteRights.Any(normalized.Contains);
    }

    private static string ResolvePrincipal(FileInventoryAclEntryInput entry)
    {
        return Normalize(entry.Principal) ?? Normalize(entry.Sid) ?? "UNKNOWN";
    }

    private static string? JoinDistinct(IEnumerable<string?> values)
    {
        var result = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return result.Length == 0 ? null : string.Join("; ", result);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
