using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using SwitcherCore;

/// <summary>
/// End-to-end check on this PC's real data: back up every default item, restore it into a sandbox
/// folder (never the real one) and verify each restored file against the zip.
/// Usage: dotnet run --project tests/SwitcherCore.Smoke -- --real-backup &lt;workDir&gt;
/// </summary>
static class RealBackupCheck
{
    public static int Run(string workDir)
    {
        var work = Path.Combine(workDir, "real-backup-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(work);
        var sw = Stopwatch.StartNew();

        var scans = BackupService.Items.Where(i => i.DefaultOn).Select(i => BackupService.Scan(i)).ToList();
        foreach (var s in scans)
            Console.WriteLine($"  scan {s.Item.Id,-18} {s.Files.Count,7:N0} files  {BackupService.FormatBytes(s.Bytes),10}");
        Console.WriteLine($"  scanned in {sw.Elapsed.TotalSeconds:0.0}s");

        var zip = Path.Combine(work, "backup.zip");
        sw.Restart();
        var res = BackupService.Create(zip, scans, "smoke");
        Console.WriteLine($"  backup: {res.Files:N0} files, {BackupService.FormatBytes(res.Bytes)} -> zip {BackupService.FormatBytes(res.ZipBytes)} in {sw.Elapsed.TotalSeconds:0.0}s, {res.Skipped.Count} skipped");
        foreach (var s in res.Skipped.Take(10)) Console.WriteLine("    skipped: " + s);

        var dest = Path.Combine(work, "restored");
        sw.Restart();
        var manifest = BackupService.ReadManifest(zip);
        var rr = BackupService.Restore(zip, manifest.Items.Select(i => i.Id).ToHashSet(), RestoreMode.BackupWins,
                                      Path.Combine(work, "safety"), null, default, i => Path.Combine(dest, i.Id));
        Console.WriteLine($"  restore: {rr.Written:N0} written, {rr.Errors.Count} errors in {sw.Elapsed.TotalSeconds:0.0}s");
        foreach (var e in rr.Errors.Take(10)) Console.WriteLine("    error: " + e);

        // Every zip entry must match its restored file byte for byte.
        sw.Restart();
        int ok = 0, bad = 0;
        using (var z = ZipFile.OpenRead(zip))
        {
            foreach (var entry in z.Entries)
            {
                if (entry.FullName == BackupService.ManifestName || entry.FullName.EndsWith('/')) continue;
                var slash = entry.FullName.IndexOf('/');
                var file = Path.Combine(dest, entry.FullName[..slash], entry.FullName[(slash + 1)..].Replace('/', '\\'));
                byte[] a, b;
                using (var s = entry.Open()) a = SHA256.HashData(s);
                using (var s = File.OpenRead(file)) b = SHA256.HashData(s);
                if (a.AsSpan().SequenceEqual(b)) ok++;
                else { bad++; if (bad <= 5) Console.WriteLine("    MISMATCH " + entry.FullName); }
            }
        }
        Console.WriteLine($"  verify: {ok:N0} identical, {bad} different in {sw.Elapsed.TotalSeconds:0.0}s");

        bool pass = bad == 0 && rr.Errors.Count == 0 && rr.Written == res.Files && res.Files > 0;
        Console.WriteLine(pass ? "\nREAL BACKUP CHECK PASSED" : "\nREAL BACKUP CHECK FAILED");
        try { Directory.Delete(work, true); } catch { Console.WriteLine("  (could not delete " + work + ")"); }
        return pass ? 0 : 1;
    }
}
