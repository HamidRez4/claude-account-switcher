namespace SwitcherCore;

/// <summary>One Claude Code desktop session: a <c>local_&lt;id&gt;.json</c> file inside an account/org folder.</summary>
public sealed record SessionInfo
{
    /// <summary>Id without the <c>local_</c> prefix (this is also what <c>deleted_&lt;id&gt;</c> tombstones use).</summary>
    public required string Id { get; init; }
    public required string FilePath { get; init; }
    public string Title { get; init; } = "(untitled)";
    public string Cwd { get; init; } = "";
    public string Model { get; init; } = "";
    public string CliSessionId { get; init; } = "";
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? LastActivityAt { get; init; }
    public bool IsArchived { get; init; }
    public bool IsStarred { get; init; }
    public long SizeBytes { get; init; }

    public string FileName => "local_" + Id + ".json";

    public string ProjectName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Cwd)) return "";
            var trimmed = Cwd.TrimEnd('\\', '/');
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
    }
}

/// <summary>An <c>&lt;accountUuid&gt;/&lt;orgUuid&gt;</c> folder: the place a set of sessions belongs to.</summary>
public sealed class AccountSpace
{
    public required string AccountUuid { get; init; }
    public required string OrgUuid { get; init; }
    public required string DirectoryPath { get; init; }
    public List<SessionInfo> Sessions { get; } = new();
    public HashSet<string> ArchivedIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> TombstoneIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset? LastActivityAt =>
        Sessions.Count == 0 ? null : Sessions.Max(s => s.LastActivityAt ?? s.CreatedAt);

    public SessionInfo? Find(string id) =>
        Sessions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
}

public sealed class AccountIdentity
{
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string? OrganizationName { get; set; }
    /// <summary>Where the email came from (claude.json, app storage, manual, cache...).</summary>
    public string? Source { get; set; }
}

public sealed class Account
{
    public required string Uuid { get; init; }
    public List<AccountSpace> Spaces { get; } = new();
    public AccountIdentity Identity { get; set; } = new();
    public bool IsDesktopActive { get; set; }
    public bool IsCliActive { get; set; }
    /// <summary>Org the account is known to use (from claude.json / desktop config), if any.</summary>
    public string? KnownOrgUuid { get; set; }

    /// <summary>The org folder that actually holds this account's sessions.</summary>
    public AccountSpace? PrimarySpace =>
        Spaces.OrderByDescending(s => s.Sessions.Count)
              .ThenByDescending(s => string.Equals(s.OrgUuid, KnownOrgUuid, StringComparison.OrdinalIgnoreCase))
              .FirstOrDefault();

    public int SessionCount => Spaces.Sum(s => s.Sessions.Count);

    public DateTimeOffset? LastActivityAt =>
        Spaces.Select(s => s.LastActivityAt).Where(d => d != null).DefaultIfEmpty(null).Max();

    public string Label => Identity.Email ?? Identity.DisplayName ?? ShortUuid;
    public string ShortUuid => Uuid.Length > 8 ? Uuid[..8] : Uuid;
}
