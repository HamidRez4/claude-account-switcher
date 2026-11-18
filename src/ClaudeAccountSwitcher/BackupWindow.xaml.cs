using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using SwitcherCore;

namespace ClaudeAccountSwitcher;

public sealed class BackupItemVm : Observable
{
    bool _isChecked;
    string _sizeText = "scanning…";

    public BackupItemVm(string id, string title, string description, bool sensitive, bool isChecked)
    {
        Id = id;
        Title = title;
        Description = description;
        Sensitive = sensitive;
        _isChecked = isChecked;
    }

    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public bool Sensitive { get; }
    public ItemScan? Scan { get; set; }
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
    public string SizeText { get => _sizeText; set => Set(ref _sizeText, value); }

    public static string Describe(int files, long bytes) =>
        files == 0 ? "nothing here" : $"{BackupService.FormatBytes(bytes)}\n{files:N0} file{(files == 1 ? "" : "s")}";
}

public partial class BackupWindow : Window
{
    readonly ObservableCollection<BackupItemVm> _createItems = new();
    readonly ObservableCollection<BackupItemVm> _restoreItems = new();
    Task? _scanTask;
    CancellationTokenSource? _cts;
    string? _restoreZip;
    BackupManifest? _manifest;

    /// <summary>True when a restore wrote something, so the main window should reload.</summary>
    public bool Restored { get; private set; }

    public BackupWindow()
    {
        InitializeComponent();
        CreateItems.ItemsSource = _createItems;
        RestoreItems.ItemsSource = _restoreItems;

        foreach (var item in BackupService.Items)
        {
            var vm = new BackupItemVm(item.Id, item.Title, item.Description, item.Sensitive, item.DefaultOn);
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BackupItemVm.IsChecked)) UpdateCreateTotal(); };
            _createItems.Add(vm);
        }
        _restoreItems.CollectionChanged += (_, _) => UpdateRestoreButton();

        Loaded += (_, _) => { _scanTask = ScanAllAsync(); UpdateRunningNote(); };
        Activated += (_, _) => UpdateRunningNote();
        Closing += (s, e) =>
        {
            if (Overlay.Visibility == Visibility.Visible) { e.Cancel = true; _cts?.Cancel(); }
        };
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
        }
        catch { }
    }

    void UpdateRunningNote()
    {
        bool desktop = ClaudeProcesses.DesktopRunning(), cli = ClaudeProcesses.CliRunning();
        RunningNote.Visibility = desktop || cli ? Visibility.Visible : Visibility.Collapsed;
        RunningNote.Text = desktop && cli ? "⚠ Claude Desktop and Claude Code are running"
                         : desktop ? "⚠ Claude Desktop is running"
                         : "⚠ Claude Code is running";
    }

    void OnTab(object sender, RoutedEventArgs e)
    {
        if (CreatePanel == null) return;
        bool create = TabCreate.IsChecked == true;
        CreatePanel.Visibility = create ? Visibility.Visible : Visibility.Collapsed;
        RestorePanel.Visibility = create ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------ create

    async Task ScanAllAsync()
    {
        foreach (var vm in _createItems)
        {
            var item = BackupService.FindItem(vm.Id)!;
            try
            {
                var scan = await Task.Run(() => BackupService.Scan(item));
                vm.Scan = scan;
                vm.SizeText = BackupItemVm.Describe(scan.Files.Count, scan.Bytes);
            }
            catch (Exception ex)
            {
                vm.SizeText = "can't read";
                vm.Scan = new ItemScan { Item = item };
                Debug.WriteLine(ex);
            }
            UpdateCreateTotal();
        }
    }

    void UpdateCreateTotal()
    {
        var chosen = _createItems.Where(v => v.IsChecked).ToList();
        if (chosen.Any(v => v.Scan == null)) { CreateTotal.Text = "Scanning…"; return; }
        long bytes = chosen.Sum(v => v.Scan!.Bytes);
        int files = chosen.Sum(v => v.Scan!.Files.Count);
        CreateTotal.Text = files == 0
            ? "Nothing selected"
            : $"{BackupService.FormatBytes(bytes)} in {files:N0} files  ·  the zip will be smaller";
        CreateButton.IsEnabled = files > 0;
    }

    static string DefaultBackupFolder()
    {
        // Prefer a fixed drive other than the Windows one, since that one gets wiped on a reinstall.
        var sys = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
        try
        {
            var other = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed &&
                            !string.Equals(d.RootDirectory.FullName, sys, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.AvailableFreeSpace)
                .FirstOrDefault();
            if (other != null) return Path.Combine(other.RootDirectory.FullName, "Claude Backups");
        }
        catch { }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (_scanTask != null) await _scanTask;
        var scans = _createItems.Where(v => v.IsChecked && v.Scan != null && v.Scan.Files.Count > 0).Select(v => v.Scan!).ToList();
        if (scans.Count == 0) return;

        if (_createItems.Any(v => v.IsChecked && v.Sensitive) &&
            Dialog.Show(this, "Include sign-in tokens?",
                "The backup will contain your Claude Code login token and Desktop device keys. Anyone who gets the zip can use your account.\n\nOnly keep it somewhere private.",
                DialogKind.Warning, buttons: new[] { "Include them", "Cancel" }) != 0)
            return;

        var folder = DefaultBackupFolder();
        try { Directory.CreateDirectory(folder); } catch { folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); }
        var dlg = new SaveFileDialog
        {
            Title = "Save Claude backup",
            Filter = "Claude backup (*.zip)|*.zip",
            FileName = $"claude-backup-{DateTime.Now:yyyy-MM-dd-HHmm}.zip",
            InitialDirectory = folder,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        var zipPath = dlg.FileName;

        var sysRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (string.Equals(Path.GetPathRoot(zipPath), sysRoot, StringComparison.OrdinalIgnoreCase) &&
            Dialog.Show(this, "Saving on the Windows drive",
                $"{sysRoot} is usually wiped when Windows is reinstalled. Copy the zip to another drive, a USB stick or the cloud afterwards.",
                DialogKind.Warning, buttons: new[] { "Save here anyway", "Cancel" }) != 0)
            return;

        var space = SafeFreeSpace(zipPath);
        var need = scans.Sum(s => s.Bytes);
        if (space >= 0 && space < need / 3)
        {
            Dialog.Show(this, "Not enough space",
                $"{BackupService.FormatBytes(space)} free on that drive; the backup may need up to {BackupService.FormatBytes(need)}.",
                DialogKind.Error);
            return;
        }

        _cts = new CancellationTokenSource();
        ShowOverlay("Creating backup…");
        var progress = new Progress<BackupProgress>(ReportProgress);
        try
        {
            var ct = _cts.Token;
            var res = await Task.Run(() => BackupService.Create(zipPath, scans, AppInfo.Version, progress, ct));
            HideOverlay();
            var msg = $"{res.Files:N0} files ({BackupService.FormatBytes(res.Bytes)}) saved as a {BackupService.FormatBytes(res.ZipBytes)} zip:\n{res.ZipPath}";
            if (res.Skipped.Count > 0)
                msg += $"\n\n{res.Skipped.Count} file(s) could not be read (usually files Claude keeps locked while running):\n" +
                       string.Join("\n", res.Skipped.Take(5));
            if (Dialog.Show(this, "Backup saved", msg, res.Skipped.Count == 0 ? DialogKind.Success : DialogKind.Warning,
                            buttons: new[] { "Show in folder", "Close" }) == 0)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{res.ZipPath}\"") { UseShellExecute = true });
        }
        catch (OperationCanceledException)
        {
            HideOverlay();
            Dialog.Show(this, "Backup cancelled", "No file was written.", DialogKind.Info);
        }
        catch (Exception ex)
        {
            HideOverlay();
            Dialog.Show(this, "Backup failed", ex.Message, DialogKind.Error);
        }
    }

    static long SafeFreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch { return -1; }
    }

    // ------------------------------------------------------------------ restore

    async void OnPickBackup(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open Claude backup",
            Filter = "Claude backup (*.zip)|*.zip|All files|*.*",
            InitialDirectory = Directory.Exists(DefaultBackupFolder()) ? DefaultBackupFolder() : null,
        };
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FileName;

        try
        {
            var m = await Task.Run(() => BackupService.ReadManifest(path));
            _restoreZip = path;
            _manifest = m;
        }
        catch (Exception ex)
        {
            Dialog.Show(this, "Can't open this backup", ex.Message, DialogKind.Error);
            return;
        }

        RestorePath.Text = path;
        var mf = _manifest!;
        ManifestCard.Visibility = Visibility.Visible;
        ManifestTitle.Text = $"Backup from {mf.CreatedAt.LocalDateTime:yyyy-MM-dd  HH:mm}";
        ManifestDetail.Text = $"PC {mf.Machine}  ·  user {mf.UserName}  ·  {mf.Items.Sum(i => i.Files):N0} files, " +
                              $"{BackupService.FormatBytes(mf.Items.Sum(i => i.Bytes))}" +
                              (string.IsNullOrEmpty(mf.AppVersion) ? "" : $"  ·  made with v{mf.AppVersion}");

        var here = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(mf.UserProfile) && !string.Equals(mf.UserProfile, here, StringComparison.OrdinalIgnoreCase))
        {
            ManifestWarning.Visibility = Visibility.Visible;
            ManifestWarning.Text = $"Your user folder was {mf.UserProfile} and is now {here}. Files go to the new folder; " +
                                   "projects that lived inside the old user folder will show up under their old path in Claude.";
        }
        else ManifestWarning.Visibility = Visibility.Collapsed;

        _restoreItems.Clear();
        foreach (var it in mf.Items)
        {
            var known = BackupService.FindItem(it.Id);
            var vm = new BackupItemVm(it.Id, known?.Title ?? it.Title, known?.Description ?? it.RootTemplate, it.Sensitive, it.Files > 0)
            {
                SizeText = BackupItemVm.Describe(it.Files, it.Bytes),
            };
            vm.PropertyChanged += (_, _) => UpdateRestoreButton();
            _restoreItems.Add(vm);
        }
        RestoreEmpty.Visibility = _restoreItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateRestoreButton();
    }

    void UpdateRestoreButton() =>
        RestoreButton.IsEnabled = _restoreZip != null && _restoreItems.Any(v => v.IsChecked);

    async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_restoreZip == null || _manifest == null) return;
        var ids = _restoreItems.Where(v => v.IsChecked).Select(v => v.Id).ToHashSet();
        if (ids.Count == 0) return;
        var mode = RestoreMode.SelectedIndex == 1 ? SwitcherCore.RestoreMode.OnlyMissing : SwitcherCore.RestoreMode.BackupWins;

        bool desktop = ClaudeProcesses.DesktopRunning(), cli = ClaudeProcesses.CliRunning();
        if (desktop || cli)
        {
            var who = desktop && cli ? "Claude Desktop and Claude Code are" : desktop ? "Claude Desktop is" : "Claude Code is";
            if (Dialog.Show(this, "Close Claude first",
                    $"{who} running. Restoring while it runs can be overwritten by Claude a moment later, or fail on files it keeps open.\n\n" +
                    "Quit Claude Desktop from the tray icon and close any terminal running claude, then restore.",
                    DialogKind.Warning, buttons: new[] { "Restore anyway", "Cancel" }, accentIndex: 1) != 0)
                return;
        }

        var names = string.Join("\n", _restoreItems.Where(v => v.IsChecked).Select(v => "•  " + v.Title));
        var how = mode == SwitcherCore.RestoreMode.BackupWins
            ? "Files that already exist here are replaced by the backup's copy (the current ones are saved aside first)."
            : "Files that already exist here are left alone; only missing ones are added.";
        if (Dialog.Show(this, "Restore this backup?", $"{names}\n\n{how}", DialogKind.Info,
                        buttons: new[] { "Restore", "Cancel" }) != 0)
            return;

        _cts = new CancellationTokenSource();
        ShowOverlay("Restoring…");
        var progress = new Progress<BackupProgress>(ReportProgress);
        var zip = _restoreZip;
        try
        {
            var ct = _cts.Token;
            var safetyRoot = Path.Combine(AppSettings.AppDataDir, "restore-safety");
            var res = await Task.Run(() => BackupService.Restore(zip, ids, mode, safetyRoot, progress, ct));
            HideOverlay();
            Restored = res.Written + res.Replaced > 0;

            var lines = new List<string>();
            if (res.Written > 0) lines.Add($"•  {res.Written:N0} files added");
            if (res.Replaced > 0) lines.Add($"•  {res.Replaced:N0} files replaced");
            if (res.Unchanged > 0) lines.Add($"•  {res.Unchanged:N0} already identical");
            if (res.KeptExisting > 0) lines.Add($"•  {res.KeptExisting:N0} kept as they were");
            var msg = string.Join("\n", lines);
            if (res.SafetyDir != null) msg += $"\n\nReplaced files were saved to:\n{res.SafetyDir}";
            if (res.Errors.Count > 0) msg += $"\n\n{res.Errors.Count} problem(s):\n" + string.Join("\n", res.Errors.Take(6));
            msg += "\n\nStart Claude Desktop now. If it asks, sign in again with the same accounts.";
            Dialog.Show(this, res.Errors.Count == 0 ? "Restore finished" : "Restore finished with problems", msg,
                        res.Errors.Count == 0 ? DialogKind.Success : DialogKind.Warning);
        }
        catch (OperationCanceledException)
        {
            HideOverlay();
            Restored = true; // part of it may have been written
            Dialog.Show(this, "Restore stopped", "Files restored before you cancelled stay in place; nothing else was changed.", DialogKind.Info);
        }
        catch (Exception ex)
        {
            HideOverlay();
            Restored = true;
            Dialog.Show(this, "Restore failed", ex.Message, DialogKind.Error);
        }
    }

    // ------------------------------------------------------------------ progress

    void ShowOverlay(string title)
    {
        ProgressTitle.Text = title;
        Bar.Value = 0;
        ProgressPercent.Text = "0%";
        ProgressCount.Text = "";
        ProgressFile.Text = "";
        CancelButton.IsEnabled = true;
        Overlay.Visibility = Visibility.Visible;
    }

    void HideOverlay() => Overlay.Visibility = Visibility.Collapsed;

    void ReportProgress(BackupProgress p)
    {
        Bar.Value = p.Fraction;
        ProgressPercent.Text = $"{p.Fraction * 100:0}%";
        ProgressCount.Text = $"{p.DoneFiles:N0} / {p.TotalFiles:N0} files  ·  {BackupService.FormatBytes(p.DoneBytes)} / {BackupService.FormatBytes(p.TotalBytes)}";
        ProgressFile.Text = p.Current;
    }

    void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.IsEnabled = false;
        ProgressTitle.Text = "Stopping…";
    }
}
