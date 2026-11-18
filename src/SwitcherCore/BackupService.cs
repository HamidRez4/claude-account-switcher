using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwitcherCore;

/// <summary>One group of files that a full backup can hold.</summary>
public sealed class BackupItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    /// <summary>Folder the files live in, with <c>{APPDATA}</c> / <c>{USERPROFILE}</c> placeholders.</summary>
    public required string RootTemplate { get; init; }
    /// <summary>When set, only these files (relative to the root) belong to the item.</summary>
    public string[]? OnlyFiles { get; init; }
    /// <summary>Top-level names under the root that are skipped (caches, binaries, browser state…).</summary>
    public string[] ExcludeTopLevel { get; init; } = Array.Empty<string>();
    public bool Sensitive { get; init; }
    public bool DefaultOn { get; init; } = true;

    public string Root => BackupService.Expand(RootTemplate);
}

public sealed class ScannedFile
{
    public required string Relative { get; init; }
    public required string FullPath { get; init; }
    public long Size { get; init; }
}

public sealed class ItemScan
{
    public required BackupItem Item { get; init; }
    public List<ScannedFile> Files { get; } = new();
    public long Bytes => Files.Sum(f => f.Size);
}

public sealed class BackupProgress
{
    public long DoneBytes { get; init; }
    public long TotalBytes { get; init; }
    public int DoneFiles { get; init; }
    public int TotalFiles { get; init; }
    public string Current { get; init; } = "";
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)DoneBytes / TotalBytes, 0, 1);
}

public sealed class BackupManifest
{
    public int Format { get; set; } = 1;
    public string? AppVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? Machine { get; set; }
    public string? UserName { get; set; }
    public string? UserProfile { get; set; }
    public string? AppData { get; set; }
    public List<ManifestItem> Items { get; set; } = new();
    public List<string> SkippedFiles { get; set; } = new();
    /// <summary>Entries whose source could not be read to the end; restore skips them.</summary>
    public List<string> BrokenEntries { get; set; } = new();
}

public sealed class ManifestItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string RootTemplate { get; set; } = "";
    public int Files { get; set; }
    public long Bytes { get; set; }
    public bool Sensitive { get; set; }
}

public sealed class BackupResult
{
    public string ZipPath { get; set; } = "";
    public int Files { get; set; }
    public long Bytes { get; set; }
    public long ZipBytes { get; set; }
    public List<string> Skipped { get; } = new();
}

public enum RestoreMode
{
    /// <summary>Files from the backup replace what is here (the right choice on a fresh Windows).</summary>
    BackupWins,
    /// <summary>Only files that don't exist here yet are written.</summary>
    OnlyMissing,
}

public sealed class RestoreResult
{
    public int Written { get; set; }
    public int Replaced { get; set; }
    public int Unchanged { get; set; }
    public int KeptExisting { get; set; }
    public List<string> Errors { get; } = new();
    public string? SafetyDir { get; set; }
}

/// <summary>Full backup of everything Claude needs to bring sessions back after a Windows reinstall.</summary>
public static class BackupService
{
    public const string ManifestName = "backup-manifest.json";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Desktop files that hold device keys or tokens; they only go in the "secrets" item.</summary>
    static readonly string[] DesktopSecretFiles = { "buddy-tokens.json", "ant-device-registry.json", "ant-did" };

    public static readonly IReadOnlyList<BackupItem> Items = new[]
    {
        new BackupItem
        {
            Id = "desktop-sessions",
            Title = "Claude Desktop sessions",
            Description = "The session list of every account (claude-code-sessions).",
            RootTemplate = @"{APPDATA}\Claude\claude-code-sessions",
        },
        new BackupItem
        {
            Id = "cli-data",
            Title = "Conversations & Claude Code data",
            Description = "Full chat history (.claude\\projects), file history for rewind, memory, CLAUDE.md, settings, skills, agents, plugins.",
            RootTemplate = @"{USERPROFILE}\.claude",
            ExcludeTopLevel = new[]
            {
                ".credentials.json", "debug", "telemetry", "shell-snapshots", "ide", "statsig", "cache",
                "downloads", "session-env", "sessions", "logs",
            },
        },
        new BackupItem
        {
            Id = "cli-config",
            Title = "Claude Code config (.claude.json)",
            Description = "Per-project settings, MCP servers, trusted folders, account profile.",
            RootTemplate = @"{USERPROFILE}",
            OnlyFiles = new[] { ".claude.json" },
        },
        new BackupItem
        {
            Id = "desktop-data",
            Title = "Claude Desktop settings & extras",
            Description = "App config, MCP config, worktree list, side-session notes, presets, agent sessions. Caches and browser data are left out.",
            RootTemplate = @"{APPDATA}\Claude",
            ExcludeTopLevel = new[]
            {
                "claude-code-sessions", // its own item
                "Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache", "GPUPersistentCache",
                "GrShaderCache", "ShaderCache", "Crashpad", "logs", "sentry", "blob_storage", "claude-code",
                "claude-code-spare", "vm_bundles", "ssh-cli-cache", "Network", "IndexedDB", "Local Storage",
                "Session Storage", "WebStorage", "Partitions", "Service Worker", "Shared Dictionary", "SharedStorage",
                "VideoDecodeStats", "shared_proto_db", "DIPS", "DIPS-wal", "InterestGroups", "Local State",
                "Preferences", "lockfile", "fcache", "Cookies", "Cookies-journal", "Trust Tokens", "Trust Tokens-journal",
                "TransportSecurity", "Network Persistent State", "declarative_performance_observer.db",
                "declarative_performance_observer.db-journal", "scratch-workspaces",
                "Extension State", "Extension Rules", "Extension Scripts", "Local Extension Settings",
                "Managed Extension Settings", "Sync Extension Settings", "Web Data", "Web Data-journal",
                "History", "History-journal", "Visited Links", "Login Data", "Login Data-journal",
                "buddy-tokens.json", "ant-device-registry.json", "ant-did",
            },
        },
        new BackupItem
        {
            Id = "switcher",
            Title = "This app's settings",
            Description = "Emails and labels this app has learned for each account.",
            RootTemplate = @"{APPDATA}\ClaudeAccountSwitcher",
            OnlyFiles = new[] { "settings.json" },
        },
        new BackupItem
        {
            Id = "secrets",
            Title = "Sign-in tokens",
            Description = "Claude Code login (.credentials.json) and Desktop device keys. Keeps the CLI signed in, but anyone with the backup file can use them.",
            RootTemplate = @"{USERPROFILE}",
            OnlyFiles = new[]
            {
                @".claude\.credentials.json",
                @"AppData\Roaming\Claude\buddy-tokens.json",
                @"AppData\Roaming\Claude\ant-device-registry.json",
                @"AppData\Roaming\Claude\ant-did",
            },
            Sensitive = true,
            DefaultOn = false,
        },
    };

    public static BackupItem? FindItem(string id) => Items.FirstOrDefault(i => i.Id == id);

    public static string Expand(string template) => template
        .Replace("{APPDATA}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
        .Replace("{USERPROFILE}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    // ------------------------------------------------------------------ scan

    public static ItemScan Scan(BackupItem item, CancellationToken ct = default) => Scan(item, item.Root, ct);

    public static ItemScan Scan(BackupItem item, string root, CancellationToken ct = default)
    {
        var scan = new ItemScan { Item = item };
        if (!Directory.Exists(root)) return scan;

        if (item.OnlyFiles != null)
        {
            foreach (var rel in item.OnlyFiles)
            {
                var full = Path.Combine(root, rel);
                if (File.Exists(full)) scan.Files.Add(new ScannedFile { Relative = rel, FullPath = full, Size = new FileInfo(full).Length });
            }
            return scan;
        }

        var excluded = new HashSet<string>(item.ExcludeTopLevel, StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            bool top = string.Equals(dir, root, StringComparison.OrdinalIgnoreCase);
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch { continue; }

            foreach (var e in entries)
            {
                if (top && excluded.Contains(e.Name)) continue;
                if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // junctions / symlinks
                if (e is DirectoryInfo d) { stack.Push(d.FullName); continue; }
                if (e is not FileInfo f) continue;
                if (f.Name.EndsWith(".cas-tmp", StringComparison.OrdinalIgnoreCase)) continue;
                if (f.Name.Equals("LOCK", StringComparison.Ordinal)) continue; // LevelDB lock files: always held, never useful
                scan.Files.Add(new ScannedFile
                {
                    Relative = Path.GetRelativePath(root, f.FullName),
                    FullPath = f.FullName,
                    Size = f.Length,
                });
            }
        }
        return scan;
    }

    // ------------------------------------------------------------------ create

    public static BackupResult Create(string zipPath, IReadOnlyList<ItemScan> scans, string? appVersion,
                                      IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new BackupResult { ZipPath = zipPath };
        long total = scans.Sum(s => s.Bytes);
        int totalFiles = scans.Sum(s => s.Files.Count);
        long done = 0;
        int doneFiles = 0;

        var manifest = new BackupManifest
        {
            AppVersion = appVersion,
            CreatedAt = DateTimeOffset.Now,
            Machine = Environment.MachineName,
            UserName = Environment.UserName,
            UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath))!);
        var partial = zipPath + ".partial";
        try
        {
            using (var fs = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var buffer = new byte[1 << 20];
                foreach (var scan in scans)
                {
                    int files = 0;
                    long bytes = 0;
                    foreach (var f in scan.Files)
                    {
                        ct.ThrowIfCancellationRequested();
                        progress?.Report(new BackupProgress
                        {
                            DoneBytes = done, TotalBytes = total, DoneFiles = doneFiles, TotalFiles = totalFiles,
                            Current = scan.Item.Id + "\\" + f.Relative,
                        });
                        string? entryName = null;
                        try
                        {
                            using var src = SessionStore.OpenShared(f.FullPath);
                            // Read the first block before adding the entry, so a locked file is skipped
                            // instead of leaving an empty entry behind.
                            int n = src.Read(buffer, 0, buffer.Length);
                            entryName = EntryName(scan.Item.Id, f.Relative);
                            var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
                            try { entry.LastWriteTime = File.GetLastWriteTime(f.FullPath); } catch { }
                            using var dst = entry.Open();
                            long written = 0;
                            while (n > 0)
                            {
                                ct.ThrowIfCancellationRequested();
                                dst.Write(buffer, 0, n);
                                written += n;
                                n = src.Read(buffer, 0, buffer.Length);
                            }
                            files++;
                            bytes += written;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            // Locked or vanished file: note it and carry on. If it failed half way, the
                            // entry is incomplete; the manifest marks it so restore never writes it.
                            var note = $"{scan.Item.Id}\\{f.Relative}: {ex.Message}";
                            result.Skipped.Add(note);
                            manifest.SkippedFiles.Add(note);
                            if (entryName != null) manifest.BrokenEntries.Add(entryName);
                        }
                        done += f.Size;
                        doneFiles++;
                    }
                    manifest.Items.Add(new ManifestItem
                    {
                        Id = scan.Item.Id, Title = scan.Item.Title, RootTemplate = scan.Item.RootTemplate,
                        Files = files, Bytes = bytes, Sensitive = scan.Item.Sensitive,
                    });
                    result.Files += files;
                    result.Bytes += bytes;
                }

                var m = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
                using (var w = new StreamWriter(m.Open()))
                    w.Write(JsonSerializer.Serialize(manifest, Json));
            }
            File.Move(partial, zipPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(partial); } catch { }
            throw;
        }

        result.ZipBytes = new FileInfo(zipPath).Length;
        progress?.Report(new BackupProgress { DoneBytes = total, TotalBytes = total, DoneFiles = totalFiles, TotalFiles = totalFiles, Current = "Done" });
        return result;
    }

    static string EntryName(string itemId, string relative) => itemId + "/" + relative.Replace('\\', '/');

    // ------------------------------------------------------------------ restore

    public static BackupManifest ReadManifest(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry(ManifestName) ?? throw new InvalidDataException("This zip is not a Claude Account Switcher backup (no manifest).");
        using var r = new StreamReader(entry.Open());
        var m = JsonSerializer.Deserialize<BackupManifest>(r.ReadToEnd(), Json)
                ?? throw new InvalidDataException("The backup manifest is empty.");
        if (m.Format > 1) throw new InvalidDataException($"This backup was made by a newer version (format {m.Format}). Update the app first.");
        return m;
    }

    /// <summary>
    /// Writes the chosen items back to this user's folders. Every file it replaces is first copied to
    /// <paramref name="safetyRoot"/>. <paramref name="rootOverride"/> lets tests restore into a sandbox.
    /// </summary>
    public static RestoreResult Restore(string zipPath, ISet<string> itemIds, RestoreMode mode, string safetyRoot,
                                        IProgress<BackupProgress>? progress = null, CancellationToken ct = default,
                                        Func<ManifestItem, string>? rootOverride = null)
    {
        var result = new RestoreResult();
        var manifest = ReadManifest(zipPath);
        var safetyDir = Path.Combine(safetyRoot, "restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        bool safetyUsed = false;

        using var zip = ZipFile.OpenRead(zipPath);
        var chosen = manifest.Items.Where(i => itemIds.Contains(i.Id)).ToList();
        var work = new List<(ManifestItem item, ZipArchiveEntry entry, string rel)>();
        var broken = new HashSet<string>(manifest.BrokenEntries ?? new(), StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName == ManifestName || broken.Contains(entry.FullName)) continue;
            var slash = entry.FullName.IndexOf('/');
            if (slash <= 0) continue;
            var item = chosen.FirstOrDefault(i => i.Id == entry.FullName[..slash]);
            if (item == null) continue;
            work.Add((item, entry, entry.FullName[(slash + 1)..].Replace('/', Path.DirectorySeparatorChar)));
        }

        long total = work.Sum(w => w.entry.Length);
        long done = 0;
        int doneFiles = 0;
        var buffer = new byte[1 << 20];

        foreach (var (item, entry, rel) in work)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new BackupProgress
            {
                DoneBytes = done, TotalBytes = total, DoneFiles = doneFiles, TotalFiles = work.Count,
                Current = item.Id + "\\" + rel,
            });

            try
            {
                var root = Path.GetFullPath(rootOverride?.Invoke(item) ?? Expand(item.RootTemplate));
                var dest = Path.GetFullPath(Path.Combine(root, rel));
                // Never write outside the item's folder (a crafted zip could try "..\..").
                if (Path.IsPathRooted(rel) || !dest.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    result.Errors.Add($"{entry.FullName}: path leaves the target folder, skipped.");
                    continue;
                }

                bool exists = File.Exists(dest);
                if (exists)
                {
                    if (mode == RestoreMode.OnlyMissing) { result.KeptExisting++; continue; }
                    var fi = new FileInfo(dest);
                    if (fi.Length == entry.Length &&
                        Math.Abs((fi.LastWriteTime - entry.LastWriteTime.DateTime).TotalSeconds) < 2.5)
                    {
                        result.Unchanged++;
                        continue;
                    }
                    // Safety copy of what is about to be replaced.
                    var safe = Path.Combine(safetyDir, item.Id, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(safe)!);
                    using (var s = SessionStore.OpenShared(dest))
                    using (var d = File.Create(safe))
                        s.CopyTo(d);
                    safetyUsed = true;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                var tmp = dest + ".cas-tmp";
                using (var src = entry.Open())
                using (var dst = File.Create(tmp))
                {
                    int n;
                    while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        dst.Write(buffer, 0, n);
                    }
                }

                // Desktop config.json carries an encrypted sign-in cache that only works on the PC that
                // wrote it. Keep this PC's sign-in keys so restoring doesn't log Claude out.
                if (exists && item.Id == "desktop-data" && rel.Equals("config.json", StringComparison.OrdinalIgnoreCase))
                    KeepCurrentOauthKeys(tmp, dest);

                try { File.SetLastWriteTime(tmp, entry.LastWriteTime.DateTime); } catch { }
                File.Move(tmp, dest, overwrite: true);
                if (exists) result.Replaced++; else result.Written++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Errors.Add($"{item.Id}\\{rel}: {ex.Message}");
            }
            finally
            {
                done += entry.Length;
                doneFiles++;
            }
        }

        if (safetyUsed)
        {
            result.SafetyDir = safetyDir;
            try
            {
                File.WriteAllText(Path.Combine(safetyDir, "about.txt"),
                    $"Files that were replaced while restoring {Path.GetFileName(zipPath)} on {DateTime.Now}.\r\n" +
                    "Each folder is a backup item; paths inside are relative to that item's folder.\r\n");
            }
            catch { }
        }
        progress?.Report(new BackupProgress { DoneBytes = total, TotalBytes = total, DoneFiles = work.Count, TotalFiles = work.Count, Current = "Done" });
        return result;
    }

    static void KeepCurrentOauthKeys(string restoredFile, string currentFile)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(restoredFile)) is not JsonObject restored) return;
            if (JsonNode.Parse(File.ReadAllText(currentFile)) is not JsonObject current) return;
            foreach (var key in restored.Select(kv => kv.Key).Where(k => k.StartsWith("oauth:", StringComparison.Ordinal)).ToList())
                restored.Remove(key);
            foreach (var kv in current.Where(kv => kv.Key.StartsWith("oauth:", StringComparison.Ordinal)).ToList())
                restored[kv.Key] = kv.Value?.DeepClone();
            File.WriteAllText(restoredFile, restored.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* leave the restored file as-is */ }
    }

    public static string FormatBytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.#} {u[i]}";
    }
}
