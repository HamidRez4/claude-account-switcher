using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwitcherCore;

public enum TransferMode { Copy, Move }

public enum ConflictPolicy
{
    /// <summary>Replace the target's copy only when the source copy has newer activity.</summary>
    OverwriteIfNewer,
    AlwaysOverwrite,
    Skip,
}

public enum TargetState { Missing, SourceNewer, Same, TargetNewer }

public sealed class TransferResult
{
    public int Created { get; set; }
    public int Overwritten { get; set; }
    public int Skipped { get; set; }
    public int RemovedFromSource { get; set; }
    public int TombstonesCleared { get; set; }
    public List<string> Errors { get; } = new();
    public string? BackupPath { get; set; }
    public int Transferred => Created + Overwritten;
}

public static class TransferService
{
    public static TargetState Compare(SessionInfo source, AccountSpace target)
    {
        var existing = target.Find(source.Id);
        if (existing == null) return TargetState.Missing;
        var s = source.LastActivityAt ?? DateTimeOffset.MinValue;
        var t = existing.LastActivityAt ?? DateTimeOffset.MinValue;
        if (s > t) return TargetState.SourceNewer;
        if (s < t) return TargetState.TargetNewer;
        return TargetState.Same;
    }

    /// <summary>
    /// Copies (or moves) session files from one account folder to another. Every file it is about to
    /// replace or delete is first copied to a timestamped backup folder.
    /// </summary>
    public static TransferResult Transfer(
        AccountSpace source, AccountSpace target, IEnumerable<SessionInfo> sessions,
        TransferMode mode, ConflictPolicy policy, string backupsRoot, string sessionsRoot)
    {
        var result = new TransferResult();
        if (string.Equals(Path.GetFullPath(source.DirectoryPath), Path.GetFullPath(target.DirectoryPath),
                          StringComparison.OrdinalIgnoreCase))
        {
            result.Errors.Add("Source and target are the same folder.");
            return result;
        }

        var list = sessions.ToList();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupDir = Path.Combine(backupsRoot, stamp);
        for (int i = 2; Directory.Exists(backupDir); i++) backupDir = Path.Combine(backupsRoot, $"{stamp}-{i}");
        var manifest = new List<object>();
        bool backedUpAnything = false;

        void Backup(string file)
        {
            if (!File.Exists(file)) return;
            var rel = Path.GetRelativePath(sessionsRoot, file);
            if (rel.StartsWith("..")) rel = Path.Combine("_outside", Path.GetFileName(file));
            var dest = Path.Combine(backupDir, rel);
            if (File.Exists(dest)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using (var src = SessionStore.OpenShared(file))
            using (var dst = File.Create(dest))
                src.CopyTo(dst);
            backedUpAnything = true;
        }

        Directory.CreateDirectory(target.DirectoryPath);

        var srcArchive = SessionStore.ReadArchiveIndex(source.DirectoryPath);
        var dstArchive = SessionStore.ReadArchiveIndex(target.DirectoryPath);
        bool srcArchiveChanged = false, dstArchiveChanged = false;
        var movedOut = new List<SessionInfo>();

        foreach (var s in list)
        {
            try
            {
                var srcFile = s.FilePath;
                if (!File.Exists(srcFile))
                {
                    result.Errors.Add($"{s.Title}: source file no longer exists.");
                    continue;
                }

                var dstFile = Path.Combine(target.DirectoryPath, s.FileName);
                var state = Compare(s, target);
                bool exists = File.Exists(dstFile);
                if (exists)
                {
                    bool write = policy switch
                    {
                        ConflictPolicy.AlwaysOverwrite => true,
                        ConflictPolicy.OverwriteIfNewer => state is TargetState.SourceNewer,
                        _ => false,
                    };
                    if (!write)
                    {
                        result.Skipped++;
                        manifest.Add(new { id = s.Id, title = s.Title, action = "skipped", state = state.ToString() });
                        // A move still counts as done if the target already has an equal/newer copy.
                        if (mode == TransferMode.Move && state is TargetState.Same or TargetState.TargetNewer)
                            movedOut.Add(s);
                        continue;
                    }
                    Backup(dstFile);
                }

                // Write via temp file so the desktop app never sees half a JSON.
                var tmp = dstFile + ".cas-tmp";
                using (var src = SessionStore.OpenShared(srcFile))
                using (var dst = File.Create(tmp))
                    src.CopyTo(dst);
                File.SetLastWriteTimeUtc(tmp, File.GetLastWriteTimeUtc(srcFile));
                File.Move(tmp, dstFile, overwrite: true);

                if (exists) result.Overwritten++; else result.Created++;
                manifest.Add(new { id = s.Id, title = s.Title, action = exists ? "overwritten" : "created" });

                // A tombstone in the target would hide the session there.
                var tomb = Path.Combine(target.DirectoryPath, "deleted_" + s.Id);
                if (File.Exists(tomb))
                {
                    Backup(tomb);
                    File.Delete(tomb);
                    result.TombstonesCleared++;
                }

                // Keep archived state the same as in the source.
                var key = "local_" + s.Id;
                bool inDst = dstArchive.Contains(key, StringComparer.OrdinalIgnoreCase);
                if (s.IsArchived && !inDst) { dstArchive.Add(key); dstArchiveChanged = true; }
                if (!s.IsArchived && inDst)
                {
                    dstArchive.RemoveAll(x => x.Equals(key, StringComparison.OrdinalIgnoreCase));
                    dstArchiveChanged = true;
                }

                if (mode == TransferMode.Move) movedOut.Add(s);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{s.Title}: {ex.Message}");
            }
        }

        if (mode == TransferMode.Move)
        {
            foreach (var s in movedOut)
            {
                try
                {
                    Backup(s.FilePath);
                    File.Delete(s.FilePath);
                    result.RemovedFromSource++;
                    var key = "local_" + s.Id;
                    if (srcArchive.RemoveAll(x => x.Equals(key, StringComparison.OrdinalIgnoreCase)) > 0)
                        srcArchiveChanged = true;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{s.Title}: copied, but could not remove from source ({ex.Message}).");
                }
            }
        }

        try
        {
            if (dstArchiveChanged) { Backup(Path.Combine(target.DirectoryPath, SessionStore.ArchiveIndexFile)); WriteArchiveIndex(target.DirectoryPath, dstArchive); }
            if (srcArchiveChanged) { Backup(Path.Combine(source.DirectoryPath, SessionStore.ArchiveIndexFile)); WriteArchiveIndex(source.DirectoryPath, srcArchive); }
        }
        catch (Exception ex)
        {
            result.Errors.Add("Could not update archived-sessions.idx: " + ex.Message);
        }

        try
        {
            Directory.CreateDirectory(backupDir);
            File.WriteAllText(Path.Combine(backupDir, "manifest.json"), JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.Now,
                mode = mode.ToString(),
                policy = policy.ToString(),
                source = new { account = source.AccountUuid, org = source.OrgUuid },
                target = new { account = target.AccountUuid, org = target.OrgUuid },
                sessions = manifest,
                containsBackups = backedUpAnything,
                errors = result.Errors,
            }, new JsonSerializerOptions { WriteIndented = true }));
            result.BackupPath = backupDir;
        }
        catch { }

        return result;
    }

    static void WriteArchiveIndex(string dir, List<string> ids)
    {
        var path = Path.Combine(dir, SessionStore.ArchiveIndexFile);
        JsonObject root;
        try { root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new() : new(); }
        catch { root = new(); }
        root["v"] ??= 1;
        var arr = new JsonArray();
        foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase)) arr.Add(id);
        root["archived"] = arr;
        var tmp = path + ".cas-tmp";
        File.WriteAllText(tmp, root.ToJsonString());
        File.Move(tmp, path, overwrite: true);
    }
}
