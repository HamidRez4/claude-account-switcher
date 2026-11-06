using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SwitcherCore;

/// <summary>
/// Works out which email each account UUID belongs to. Sources, strongest first:
/// manual labels, the <c>oauthAccount</c> block in <c>~/.claude.json</c> (and its backups),
/// the claude.ai web storage the desktop app keeps (IndexedDB / Local Storage), and finally
/// whatever an earlier run discovered.
/// </summary>
public static class AccountResolver
{
    static readonly Regex EmailAfterKey = new(
        @"(?<![A-Za-z_])email_address[^A-Za-z0-9]{0,6}?([A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,24})",
        RegexOptions.Compiled);

    static readonly Regex NameAfterKey = new(
        @"(?<![A-Za-z_])(?:display_name|full_name)[^\p{L}\p{N}]{0,6}?([\p{L}\p{N}][\p{L}\p{N} ._'\-]{0,40})",
        RegexOptions.Compiled);

    const int Window = 700;
    const long MaxScanFileBytes = 40L * 1024 * 1024;

    public sealed class ActiveInfo
    {
        public string? DesktopAccountUuid { get; set; }
        public string? DesktopOrgUuid { get; set; }
        public string? CliAccountUuid { get; set; }
    }

    public static string ClaudeAppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Fills <see cref="Account.Identity"/> and active flags for each account, and updates the discovered cache.</summary>
    public static ActiveInfo Resolve(IReadOnlyList<Account> accounts, AppSettings settings)
    {
        var active = ReadActive();
        var found = new Dictionary<string, AccountIdentity>(StringComparer.OrdinalIgnoreCase);

        // 1) ~/.claude.json style files (exact, structured).
        foreach (var (uuid, ident, orgUuid) in ReadOauthAccounts())
        {
            Merge(found, uuid, ident);
            var acc = accounts.FirstOrDefault(a => a.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase));
            if (acc != null && orgUuid != null) acc.KnownOrgUuid ??= orgUuid;
        }

        // 2) Scan desktop web storage for accounts still missing an email.
        var missing = accounts.Where(a => !found.TryGetValue(a.Uuid, out var f) || f.Email == null)
                              .Select(a => a.Uuid).ToList();
        if (missing.Count > 0)
            foreach (var kv in ScanAppStorage(missing))
                Merge(found, kv.Key, kv.Value);

        foreach (var acc in accounts)
        {
            var ident = new AccountIdentity();
            found.TryGetValue(acc.Uuid, out var auto);
            settings.Discovered.TryGetValue(acc.Uuid, out var cached);
            settings.Manual.TryGetValue(acc.Uuid, out var manual);

            ident.Email = Pick(manual?.Email, auto?.Email, cached?.Email);
            ident.DisplayName = Pick(manual?.DisplayName, auto?.DisplayName, cached?.DisplayName);
            ident.OrganizationName = Pick(auto?.OrganizationName, cached?.OrganizationName);
            ident.Source = !string.IsNullOrWhiteSpace(manual?.Email) ? "manual"
                         : auto?.Email != null ? auto.Source
                         : cached?.Email != null ? "remembered"
                         : null;
            acc.Identity = ident;

            if (auto?.Email != null)
            {
                var keep = settings.Discovered.TryGetValue(acc.Uuid, out var prev) ? prev : new AccountIdentity();
                keep.Email = auto.Email;
                keep.DisplayName = auto.DisplayName ?? keep.DisplayName;
                keep.OrganizationName = auto.OrganizationName ?? keep.OrganizationName;
                keep.Source = auto.Source;
                settings.Discovered[acc.Uuid] = keep;
            }

            acc.IsDesktopActive = acc.Uuid.Equals(active.DesktopAccountUuid, StringComparison.OrdinalIgnoreCase);
            acc.IsCliActive = acc.Uuid.Equals(active.CliAccountUuid, StringComparison.OrdinalIgnoreCase);
            if (acc.IsDesktopActive && active.DesktopOrgUuid != null) acc.KnownOrgUuid ??= active.DesktopOrgUuid;
        }
        return active;
    }

    static string? Pick(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    static void Merge(Dictionary<string, AccountIdentity> into, string uuid, AccountIdentity ident)
    {
        if (!into.TryGetValue(uuid, out var cur)) { into[uuid] = ident; return; }
        cur.Email ??= ident.Email;
        cur.DisplayName ??= ident.DisplayName;
        cur.OrganizationName ??= ident.OrganizationName;
        cur.Source ??= ident.Source;
    }

    public static ActiveInfo ReadActive()
    {
        var info = new ActiveInfo();
        try
        {
            var cfg = Path.Combine(ClaudeAppDataDir, "config.json");
            if (File.Exists(cfg) && JsonNode.Parse(ReadShared(cfg)) is JsonObject o)
            {
                info.DesktopAccountUuid = Str(o["lastKnownAccountUuid"]);
                // Some builds nest the current account under another key; look for the first accountUuid/orgUuid pair.
                info.DesktopOrgUuid = FindFirst(o, "orgUuid");
                info.DesktopAccountUuid ??= FindFirst(o, "accountUuid");
            }
        }
        catch { }
        try
        {
            var cj = Path.Combine(Home, ".claude.json");
            if (File.Exists(cj) && JsonNode.Parse(ReadShared(cj)) is JsonObject o)
                info.CliAccountUuid = Str(o["oauthAccount"]?["accountUuid"]);
        }
        catch { }
        return info;
    }

    static string? FindFirst(JsonNode? node, string key)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kv in obj)
                {
                    if (kv.Key == key && Str(kv.Value) is { } s) return s;
                    if (FindFirst(kv.Value, key) is { } r) return r;
                }
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    if (FindFirst(item, key) is { } r) return r;
                break;
        }
        return null;
    }

    static string? Str(JsonNode? n)
    {
        try { return n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null; }
        catch { return null; }
    }

    /// <summary>oauthAccount blocks from ~/.claude.json, its backups, and claude-swap style config copies.</summary>
    static IEnumerable<(string uuid, AccountIdentity ident, string? org)> ReadOauthAccounts()
    {
        var files = new List<string>();
        foreach (var name in new[] { ".claude.json", ".claude.json.backup" })
            files.Add(Path.Combine(Home, name));
        try
        {
            files.AddRange(Directory.EnumerateFiles(Home, ".claude.json.*", SearchOption.TopDirectoryOnly));
        }
        catch { }
        foreach (var dir in new[] { Path.Combine(Home, ".claude-swap-backup"), Path.Combine(Home, ".claude", "backups") })
        {
            try
            {
                if (Directory.Exists(dir))
                    files.AddRange(Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories));
            }
            catch { }
        }

        foreach (var f in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            JsonNode? root;
            try
            {
                if (!File.Exists(f) || new FileInfo(f).Length > 20 * 1024 * 1024) continue;
                root = JsonNode.Parse(ReadShared(f));
            }
            catch { continue; }

            foreach (var oa in FindOauthBlocks(root))
            {
                var uuid = Str(oa["accountUuid"]);
                var email = Str(oa["emailAddress"]);
                if (uuid == null || email == null) continue;
                yield return (uuid, new AccountIdentity
                {
                    Email = email,
                    DisplayName = Str(oa["displayName"]) ?? Str(oa["fullName"]),
                    OrganizationName = Str(oa["organizationName"]),
                    Source = Path.GetFileName(f),
                }, Str(oa["organizationUuid"]));
            }
        }
    }

    static IEnumerable<JsonObject> FindOauthBlocks(JsonNode? node, int depth = 0)
    {
        if (depth > 6) yield break;
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey("accountUuid") && obj.ContainsKey("emailAddress")) yield return obj;
            foreach (var kv in obj)
                foreach (var x in FindOauthBlocks(kv.Value, depth + 1)) yield return x;
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
                foreach (var x in FindOauthBlocks(item, depth + 1)) yield return x;
        }
    }

    /// <summary>
    /// Byte-scans the desktop app's claude.ai storage for the API's account object
    /// (<c>uuid … email_address …</c>) next to each wanted account uuid.
    /// </summary>
    public static Dictionary<string, AccountIdentity> ScanAppStorage(IReadOnlyCollection<string> uuids, string? appDataDir = null)
    {
        var baseDir = appDataDir ?? ClaudeAppDataDir;
        var dirs = new[] { "IndexedDB", "Local Storage", "Session Storage", "WebStorage", "Partitions" }
            .Select(d => Path.Combine(baseDir, d)).Where(Directory.Exists);

        var votes = uuids.ToDictionary(u => u, _ => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                                       StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var latin1 = Encoding.Latin1;

        foreach (var dir in dirs)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList(); }
            catch { continue; }

            foreach (var file in files)
            {
                string text;
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Length == 0 || fi.Length > MaxScanFileBytes) continue;
                    using var fs = SessionStore.OpenShared(file);
                    using var ms = new MemoryStream();
                    fs.CopyTo(ms);
                    text = latin1.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                }
                catch { continue; }

                foreach (var uuid in uuids)
                {
                    int idx = 0;
                    while ((idx = text.IndexOf(uuid, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
                    {
                        var start = idx + uuid.Length;
                        var len = Math.Min(Window, text.Length - start);
                        var slice = text.Substring(start, len);
                        var m = EmailAfterKey.Match(slice);
                        if (m.Success)
                        {
                            var email = m.Groups[1].Value.TrimStart('.', '-', '_', '%', '+').ToLowerInvariant();
                            var v = votes[uuid];
                            v[email] = v.GetValueOrDefault(email) + 1;
                            if (!names.ContainsKey(uuid))
                            {
                                var n = NameAfterKey.Match(slice, m.Index);
                                if (n.Success && n.Index - m.Index < 200) names[uuid] = n.Groups[1].Value.Trim();
                            }
                        }
                        idx = start;
                    }
                }
            }
        }

        var result = new Dictionary<string, AccountIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var (uuid, v) in votes)
        {
            if (v.Count == 0) continue;
            result[uuid] = new AccountIdentity
            {
                Email = v.OrderByDescending(kv => kv.Value).First().Key,
                DisplayName = names.GetValueOrDefault(uuid),
                Source = "Claude app storage",
            };
        }
        return result;
    }

    static string ReadShared(string file)
    {
        using var fs = SessionStore.OpenShared(file);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }
}
