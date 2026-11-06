using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SwitcherCore;

/// <summary>Reads the <c>%APPDATA%\Claude\claude-code-sessions</c> tree.</summary>
public static class SessionStore
{
    public static readonly Regex UuidRegex =
        new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled);

    public const string ArchiveIndexFile = "archived-sessions.idx";

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code-sessions");

    public static List<Account> Load(string root)
    {
        var accounts = new List<Account>();
        if (!Directory.Exists(root)) return accounts;

        foreach (var accDir in Directory.EnumerateDirectories(root))
        {
            var accUuid = Path.GetFileName(accDir);
            if (!UuidRegex.IsMatch(accUuid)) continue;

            var account = new Account { Uuid = accUuid };
            foreach (var orgDir in Directory.EnumerateDirectories(accDir))
            {
                var orgUuid = Path.GetFileName(orgDir);
                if (!UuidRegex.IsMatch(orgUuid)) continue;
                account.Spaces.Add(LoadSpace(accUuid, orgUuid, orgDir));
            }
            accounts.Add(account);
        }
        return accounts;
    }

    public static AccountSpace LoadSpace(string accountUuid, string orgUuid, string dir)
    {
        var space = new AccountSpace { AccountUuid = accountUuid, OrgUuid = orgUuid, DirectoryPath = dir };

        foreach (var id in ReadArchiveIndex(dir)) space.ArchivedIds.Add(id);

        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("deleted_", StringComparison.OrdinalIgnoreCase))
            {
                space.TombstoneIds.Add(name["deleted_".Length..]);
            }
            else if (name.StartsWith("local_", StringComparison.OrdinalIgnoreCase) &&
                     name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var s = ReadSession(file, space.ArchivedIds);
                if (s != null) space.Sessions.Add(s);
            }
        }

        space.Sessions.Sort((a, b) =>
            Nullable.Compare(b.LastActivityAt ?? b.CreatedAt, a.LastActivityAt ?? a.CreatedAt));
        return space;
    }

    public static SessionInfo? ReadSession(string file, ISet<string>? archivedIds = null)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var id = name.StartsWith("local_", StringComparison.OrdinalIgnoreCase) ? name["local_".Length..] : name;
        try
        {
            using var stream = OpenShared(file);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true });
            var r = doc.RootElement;
            bool archived = GetBool(r, "isArchived") ||
                            (archivedIds?.Contains("local_" + id) ?? false) ||
                            (archivedIds?.Contains(id) ?? false);
            return new SessionInfo
            {
                Id = id,
                FilePath = file,
                Title = GetString(r, "title") is { Length: > 0 } t ? t : "(untitled)",
                Cwd = GetString(r, "cwd") ?? GetString(r, "originCwd") ?? "",
                Model = GetString(r, "model") ?? "",
                CliSessionId = GetString(r, "cliSessionId") ?? "",
                CreatedAt = GetTime(r, "createdAt"),
                LastActivityAt = GetTime(r, "lastActivityAt") ?? GetTime(r, "lastFocusedAt") ?? GetTime(r, "createdAt"),
                IsArchived = archived,
                IsStarred = GetBool(r, "isStarred"),
                SizeBytes = new FileInfo(file).Length,
            };
        }
        catch
        {
            // Unreadable/partially written file: still list it so it can be moved.
            var fi = new FileInfo(file);
            return new SessionInfo
            {
                Id = id,
                FilePath = file,
                Title = "(unreadable) " + id,
                LastActivityAt = fi.Exists ? fi.LastWriteTimeUtc : null,
                SizeBytes = fi.Exists ? fi.Length : 0,
            };
        }
    }

    /// <summary>Returns the raw entries of <c>archived-sessions.idx</c> (they look like <c>local_&lt;id&gt;</c>).</summary>
    public static List<string> ReadArchiveIndex(string dir)
    {
        var path = Path.Combine(dir, ArchiveIndexFile);
        var list = new List<string>();
        if (!File.Exists(path)) return list;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node?["archived"] is JsonArray arr)
                foreach (var item in arr)
                    if (item?.GetValue<string>() is { } s) list.Add(s);
        }
        catch { /* ignore a corrupt index */ }
        return list;
    }

    internal static FileStream OpenShared(string file) =>
        new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    static string? GetString(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    static bool GetBool(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    static DateTimeOffset? GetTime(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v)) return null;
        try
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var ms) && ms > 0)
                return DateTimeOffset.FromUnixTimeMilliseconds(ms);
            if (v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var dt))
                return dt;
        }
        catch { }
        return null;
    }
}
