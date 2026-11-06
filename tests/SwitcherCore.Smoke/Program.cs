// Smoke test for SwitcherCore.
//   1) Reads the real claude-code-sessions folder (read only) and prints what it finds.
//   2) Copies that folder to a temp dir and runs copy/move transfers there, checking the results.
// With --fixture (or when there is no real folder, e.g. on CI) it builds a fake two-account tree instead.
// Usage: dotnet run --project tests/SwitcherCore.Smoke [-- [--fixture] [<workDir>]]
using SwitcherCore;

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what);
    if (!ok) failures++;
}

var useFixture = args.Contains("--fixture");
var workArg = args.FirstOrDefault(a => !a.StartsWith("--"));
var realRoot = SessionStore.DefaultRoot;
if (!useFixture && SessionStore.Load(realRoot).Count < 2)
{
    Console.WriteLine($"No two accounts under {realRoot}; using a generated fixture.");
    useFixture = true;
}

var work = workArg ?? Path.Combine(Path.GetTempPath(), "cas-smoke-" + Guid.NewGuid().ToString("N")[..8]);
if (Directory.Exists(work)) Directory.Delete(work, true);
if (useFixture)
{
    realRoot = Path.Combine(work, "fixture-source");
    BuildFixture(realRoot);
}

Console.WriteLine($"== Reading {realRoot}");
var accounts = SessionStore.Load(realRoot);
if (!useFixture)
{
    var settings = new AppSettings(); // fresh: don't touch the app's real settings
    AccountResolver.Resolve(accounts, settings);
}
foreach (var a in accounts)
{
    Console.WriteLine($"  {a.Uuid}  email={a.Identity.Email ?? "?"}  name={a.Identity.DisplayName ?? "-"}  src={a.Identity.Source ?? "-"}" +
                      $"  desktop={a.IsDesktopActive} cli={a.IsCliActive}");
    foreach (var s in a.Spaces)
    {
        Console.WriteLine($"     org {s.OrgUuid}: {s.Sessions.Count} sessions, {s.TombstoneIds.Count} tombstones, {s.ArchivedIds.Count} archived" +
                          (s == a.PrimarySpace ? "  [primary]" : ""));
        foreach (var x in s.Sessions.Take(3))
            Console.WriteLine($"        {x.LastActivityAt:yyyy-MM-dd HH:mm}  {x.Title}  ({x.ProjectName})");
    }
}
Check(accounts.Count >= 2, "found at least two accounts");
if (!useFixture) Check(accounts.All(a => a.Identity.Email != null), "every account has an email");
if (accounts.Count < 2) return 1;

// ---- transfer tests on a copy -------------------------------------------------------------
var root = Path.Combine(work, "claude-code-sessions");
var backups = Path.Combine(work, "backups");
CopyDir(realRoot, root);
Console.WriteLine($"\n== Transfer tests in {root}");

List<Account> Reload() => SessionStore.Load(root);
var acc = Reload();
var A = acc.OrderByDescending(a => a.SessionCount).First().PrimarySpace!;
var B = acc.First(a => a.Uuid != A.AccountUuid).PrimarySpace!;
Console.WriteLine($"  A = {A.AccountUuid[..8]}/{A.OrgUuid[..8]} ({A.Sessions.Count})   B = {B.AccountUuid[..8]}/{B.OrgUuid[..8]} ({B.Sessions.Count})");

// 1) Copy a session B doesn't have, after planting a tombstone for it in B.
var missing = A.Sessions.FirstOrDefault(s => B.Find(s.Id) == null);
if (missing != null)
{
    File.WriteAllText(Path.Combine(B.DirectoryPath, "deleted_" + missing.Id), "1793729006063");
    var r = TransferService.Transfer(A, B, new[] { missing }, TransferMode.Copy, ConflictPolicy.OverwriteIfNewer, backups, root);
    var b2 = SessionStore.LoadSpace(B.AccountUuid, B.OrgUuid, B.DirectoryPath);
    Check(r.Created == 1 && r.Errors.Count == 0, $"copy new session '{missing.Title}' → created");
    Check(b2.Find(missing.Id) != null, "target now lists it");
    Check(!b2.TombstoneIds.Contains(missing.Id), "target tombstone was cleared");
    Check(File.Exists(missing.FilePath), "copy kept the source file");
    Check(File.ReadAllBytes(missing.FilePath).SequenceEqual(File.ReadAllBytes(Path.Combine(B.DirectoryPath, missing.FileName))), "bytes identical");
    Check(r.BackupPath != null && File.Exists(Path.Combine(r.BackupPath, "manifest.json")), "backup manifest written");
    Check(r.BackupPath != null && Directory.EnumerateFiles(r.BackupPath, "deleted_" + missing.Id, SearchOption.AllDirectories).Any(), "tombstone backed up");
}
else Console.WriteLine("  (skip) every A session already exists in B");

// 2) Same session again → policy behaviour.
acc = Reload();
A = acc.First(a => a.Uuid == A.AccountUuid).Spaces.First(s => s.OrgUuid == A.OrgUuid);
B = acc.First(a => a.Uuid == B.AccountUuid).Spaces.First(s => s.OrgUuid == B.OrgUuid);
var shared = A.Sessions.First(s => B.Find(s.Id) != null);
Check(TransferService.Compare(shared, B) is TargetState.Same or TargetState.SourceNewer or TargetState.TargetNewer, "compare detects existing copy");
var r2 = TransferService.Transfer(A, B, new[] { shared }, TransferMode.Copy, ConflictPolicy.Skip, backups, root);
Check(r2.Skipped == 1 && r2.Transferred == 0, "Skip policy skips existing");

// 3) Make the target copy older, then OverwriteIfNewer should replace it and back it up.
var bFile = Path.Combine(B.DirectoryPath, shared.FileName);
var json = File.ReadAllText(bFile);
var older = System.Text.RegularExpressions.Regex.Replace(json, "\"lastActivityAt\":\\d+", "\"lastActivityAt\":1000");
File.WriteAllText(bFile, older);
B = SessionStore.LoadSpace(B.AccountUuid, B.OrgUuid, B.DirectoryPath);
Check(TransferService.Compare(shared, B) == TargetState.SourceNewer, "older target copy detected as SourceNewer");
var r3 = TransferService.Transfer(A, B, new[] { shared }, TransferMode.Copy, ConflictPolicy.OverwriteIfNewer, backups, root);
Check(r3.Overwritten == 1, "OverwriteIfNewer replaced the older copy");
Check(r3.BackupPath != null && Directory.EnumerateFiles(r3.BackupPath, shared.FileName, SearchOption.AllDirectories)
          .Any(f => File.ReadAllText(f).Contains("\"lastActivityAt\":1000")), "replaced copy is in the backup");

// 4) Move a session from B to A (one A doesn't have, or any), with archived state.
acc = Reload();
A = acc.First(a => a.Uuid == A.AccountUuid).Spaces.First(s => s.OrgUuid == A.OrgUuid);
B = acc.First(a => a.Uuid == B.AccountUuid).Spaces.First(s => s.OrgUuid == B.OrgUuid);
var toMove = B.Sessions.FirstOrDefault(s => A.Find(s.Id) == null && s.IsArchived)
             ?? B.Sessions.FirstOrDefault(s => A.Find(s.Id) == null)
             ?? B.Sessions.First();
var r4 = TransferService.Transfer(B, A, new[] { toMove }, TransferMode.Move, ConflictPolicy.OverwriteIfNewer, backups, root);
var a4 = SessionStore.LoadSpace(A.AccountUuid, A.OrgUuid, A.DirectoryPath);
var b4 = SessionStore.LoadSpace(B.AccountUuid, B.OrgUuid, B.DirectoryPath);
Check(r4.Errors.Count == 0, "move had no errors " + string.Join("; ", r4.Errors));
Check(a4.Find(toMove.Id) != null, $"moved '{toMove.Title}' is in A");
Check(b4.Find(toMove.Id) == null, "moved session gone from B");
Check(!File.Exists(toMove.FilePath), "source file deleted");
if (toMove.IsArchived)
{
    Check(a4.ArchivedIds.Contains("local_" + toMove.Id), "archived state carried to A's index");
    Check(!b4.ArchivedIds.Contains("local_" + toMove.Id), "removed from B's archive index");
}
Check(r4.BackupPath != null && Directory.EnumerateFiles(r4.BackupPath, toMove.FileName, SearchOption.AllDirectories).Any(), "moved source backed up");

// 5) Same folder is refused.
var r5 = TransferService.Transfer(A, A, A.Sessions.Take(1), TransferMode.Move, ConflictPolicy.AlwaysOverwrite, backups, root);
Check(r5.Errors.Count == 1 && r5.Transferred == 0, "same source and target refused");

// 6) Real tree untouched.
var again = SessionStore.Load(realRoot);
Check(again.Sum(a => a.SessionCount) == accounts.Sum(a => a.SessionCount), "real sessions folder unchanged");

Console.WriteLine(failures == 0 ? "\nALL PASSED" : $"\n{failures} FAILED");
try { if (workArg == null) Directory.Delete(work, true); } catch { }
return failures == 0 ? 0 : 1;

// Two accounts sharing some sessions, with an archived one and a tombstone, like a real tree.
static void BuildFixture(string root)
{
    const string accA = "aaaaaaaa-0000-4000-8000-000000000001", orgA = "aaaaaaaa-0000-4000-8000-0000000000a1";
    const string accB = "bbbbbbbb-0000-4000-8000-000000000002", orgB = "bbbbbbbb-0000-4000-8000-0000000000b1";
    var dirA = Path.Combine(root, accA, orgA);
    var dirB = Path.Combine(root, accB, orgB);
    Directory.CreateDirectory(dirA);
    Directory.CreateDirectory(dirB);
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    void Session(string dir, string id, string title, long last, bool archived = false) =>
        File.WriteAllText(Path.Combine(dir, $"local_{id}.json"),
            $$"""{"sessionId":"local_{{id}}","cliSessionId":"{{Guid.NewGuid()}}","cwd":"C:/Projects/{{title.Replace(' ', '-')}}","createdAt":{{last - 3600000}},"lastActivityAt":{{last}},"model":"claude-test","isArchived":{{(archived ? "true" : "false")}},"title":"{{title}}"}""");

    string Id(int n) => $"00000000-0000-4000-8000-{n:D12}";
    Session(dirA, Id(1), "Only in A", now - 1000);
    Session(dirA, Id(2), "Shared", now - 2000);
    Session(dirB, Id(2), "Shared", now - 2000);
    Session(dirA, Id(3), "Shared newer in B", now - 5000);
    Session(dirB, Id(3), "Shared newer in B", now - 100);
    Session(dirB, Id(4), "Archived only in B", now - 3000, archived: true);
    Session(dirB, Id(5), "Only in B", now - 4000);
    File.WriteAllText(Path.Combine(dirB, "archived-sessions.idx"), $$"""{"v":1,"archived":["local_{{Id(4)}}"]}""");
    File.WriteAllText(Path.Combine(dirA, "deleted_" + Id(9)), now.ToString());
}

static void CopyDir(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
    foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
}
