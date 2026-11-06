using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SwitcherCore;

namespace ClaudeAccountSwitcher;

public partial class MainWindow : Window
{
    AppSettings _settings = AppSettings.Load();
    string _root = SessionStore.DefaultRoot;
    List<Account> _accounts = new();
    readonly ObservableCollection<AccountVm> _accountVms = new();
    readonly ObservableCollection<SessionRow> _rows = new();
    readonly ICollectionView _rowsView;
    SpaceOption? _source;
    SpaceOption? _target;
    bool _suppress;
    int _lastClickIndex = -1;
    bool _desktopRunning;

    public MainWindow()
    {
        InitializeComponent();
        var ver = typeof(MainWindow).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "";
        var plus = ver.IndexOf('+');
        if (plus > 0) ver = ver[..plus];
        VersionText.Text = "v" + ver;
        Title = $"Claude Account Switcher v{ver}";
        AccountsList.ItemsSource = _accountVms;
        _rowsView = CollectionViewSource.GetDefaultView(_rows);
        _rowsView.Filter = FilterRow;
        SessionsList.ItemsSource = _rowsView;
        ShowArchived.IsChecked = _settings.ShowArchived;

        Loaded += async (_, _) => await ReloadAsync();
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.F5) await ReloadAsync();
            else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && !SearchBox.IsKeyboardFocused)
            {
                OnSelectAll(this, new RoutedEventArgs());
                e.Handled = true;
            }
        };
        Activated += (_, _) => UpdateDesktopBanner();
    }

    // ------------------------------------------------------------------ window chrome

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));            // immersive dark mode
            int caption = 0x001D1714;                                         // #14171D as 0x00BBGGRR
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));       // Win11 caption colour (ignored on Win10)
        }
        catch { }
    }

    // ------------------------------------------------------------------ loading

    async Task ReloadAsync(bool keepSelection = true)
    {
        var keepSource = _source?.Key ?? _settings.LastSourceAccount;
        var keepTarget = _target?.Key ?? _settings.LastTargetSpace;
        var keepSelected = keepSelection
            ? _rows.Where(r => r.IsSelected).Select(r => r.Info.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>();

        StatusText.Text = "Reading sessions…";
        _root = string.IsNullOrWhiteSpace(_settings.SessionsRoot) ? SessionStore.DefaultRoot : _settings.SessionsRoot!;
        RootText.Text = _root;

        var settings = _settings;
        var root = _root;
        try
        {
            _accounts = await Task.Run(() =>
            {
                var list = SessionStore.Load(root);
                AccountResolver.Resolve(list, settings);
                settings.Save();
                return list;
            });
        }
        catch (Exception ex)
        {
            Dialog.Show(this, "Could not read sessions", ex.Message, DialogKind.Error);
            _accounts = new();
        }

        _suppress = true;
        _accountVms.Clear();
        foreach (var a in _accounts
                     .OrderByDescending(a => a.SessionCount > 0)
                     .ThenByDescending(a => a.LastActivityAt ?? DateTimeOffset.MinValue))
            _accountVms.Add(new AccountVm(a));
        _suppress = false;

        AccountCount.Text = _accounts.Count.ToString();
        UpdateDesktopBanner();

        if (_accounts.Count == 0)
        {
            SourceTitle.Text = "No accounts found";
            _rows.Clear();
            UpdateEmptyState();
            StatusText.Text = $"Nothing under {_root}";
            UpdateSummary();
            return;
        }

        // Default source: what was selected before; else the account Claude Desktop is NOT using
        // (the usual case is "I switched accounts, bring my sessions over").
        AccountVm? pick = null;
        if (keepSource != null)
        {
            var accUuid = keepSource.Split('/')[0];
            pick = _accountVms.FirstOrDefault(v => v.Uuid.Equals(accUuid, StringComparison.OrdinalIgnoreCase));
        }
        pick ??= _accountVms.FirstOrDefault(v => !v.IsDesktopActive && v.SessionCount > 0) ?? _accountVms[0];

        _pendingTargetKey = keepTarget;
        _pendingSelection = keepSelected;
        _pendingOrgKey = keepSource;
        AccountsList.SelectedItem = pick;
        if (AccountsList.SelectedItem == pick && _source?.Account != pick.Account) OnAccountChanged(this, null!);
        AccountsList.ScrollIntoView(pick);
        StatusText.Text = "";
        UpdateSummary();
    }

    string? _pendingTargetKey;
    string? _pendingOrgKey;
    HashSet<string>? _pendingSelection;

    void UpdateDesktopBanner()
    {
        _desktopRunning = IsClaudeDesktopRunning();
        Banner.Visibility = _desktopRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    static bool IsClaudeDesktopRunning()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("claude"))
            {
                try
                {
                    var path = p.MainModule?.FileName ?? "";
                    if (path.Contains("claude-code", StringComparison.OrdinalIgnoreCase)) continue;
                    if (path.Contains("AnthropicClaude", StringComparison.OrdinalIgnoreCase) ||
                        path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) ||
                        path.Contains(@"\Claude\", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { /* access denied: ignore */ }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return false;
    }

    // ------------------------------------------------------------------ source / target

    void OnAccountChanged(object sender, SelectionChangedEventArgs? e)
    {
        if (_suppress || AccountsList.SelectedItem is not AccountVm vm) return;
        var acc = vm.Account;
        var primary = acc.PrimarySpace;
        var options = UsefulSpaces(acc)
            .OrderByDescending(s => s == primary)
            .ThenByDescending(s => s.Sessions.Count)
            .Select(s => new SpaceOption(acc, s, s == primary))
            .ToList();

        _suppress = true;
        OrgCombo.ItemsSource = options;
        OrgCombo.Visibility = options.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        var want = options.FirstOrDefault(o => o.Key.Equals(_pendingOrgKey, StringComparison.OrdinalIgnoreCase)) ?? options.FirstOrDefault();
        _pendingOrgKey = null;
        OrgCombo.SelectedItem = want;
        _suppress = false;

        SourceTitle.Text = vm.Title;
        if (want != null) SetSource(want);
        else { _source = null; _rows.Clear(); UpdateEmptyState(); UpdateSummary(); }
    }

    /// <summary>Org folders worth showing: the main one plus any other that actually holds sessions.</summary>
    static IEnumerable<AccountSpace> UsefulSpaces(Account acc)
    {
        var primary = acc.PrimarySpace;
        return acc.Spaces.Where(s => s == primary || s.Sessions.Count > 0);
    }

    void OnOrgChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || OrgCombo.SelectedItem is not SpaceOption opt) return;
        SetSource(opt);
    }

    void SetSource(SpaceOption source)
    {
        var sameSource = _source?.Key == source.Key;
        _source = source;
        _settings.LastSourceAccount = source.Key;

        // Every other account/org folder is a possible target; main org folders first.
        var targets = new List<SpaceOption>();
        foreach (var acc in _accounts.OrderByDescending(a => a.IsDesktopActive).ThenByDescending(a => a.LastActivityAt ?? DateTimeOffset.MinValue))
        {
            var primary = acc.PrimarySpace;
            foreach (var sp in UsefulSpaces(acc).OrderByDescending(s => s == primary).ThenByDescending(s => s.Sessions.Count))
            {
                if (sp == source.Space) continue;
                targets.Add(new SpaceOption(acc, sp, sp == primary));
            }
        }
        // Secondary (near-empty) org folders of other accounts go last.
        targets = targets.OrderByDescending(t => t.IsPrimary).ToList();

        var keepKey = _pendingTargetKey ?? _target?.Key;
        _pendingTargetKey = null;
        var pick = targets.FirstOrDefault(t => t.Key.Equals(keepKey, StringComparison.OrdinalIgnoreCase))
                   ?? targets.FirstOrDefault(t => t.Account.Uuid != source.Account.Uuid && t.IsPrimary)
                   ?? targets.FirstOrDefault();

        _suppress = true;
        TargetCombo.ItemsSource = targets;
        TargetCombo.SelectedItem = pick;
        _suppress = false;
        _target = pick;
        if (pick != null) _settings.LastTargetSpace = pick.Key;

        var keep = _pendingSelection;
        _pendingSelection = null;
        if (keep == null && sameSource) keep = _rows.Where(r => r.IsSelected).Select(r => r.Info.Id).ToHashSet();
        BuildRows(keep);
    }

    void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || TargetCombo.SelectedItem is not SpaceOption t) return;
        _target = t;
        _settings.LastTargetSpace = t.Key;
        _settings.Save();
        foreach (var r in _rows) r.State = TransferService.Compare(r.Info, t.Space);
        UpdateSummary();
    }

    void BuildRows(ISet<string>? keepSelected)
    {
        _rows.Clear();
        _lastClickIndex = -1;
        if (_source == null) { UpdateEmptyState(); UpdateSummary(); return; }
        foreach (var s in _source.Space.Sessions)
        {
            var row = new SessionRow(s)
            {
                IsSelected = keepSelected?.Contains(s.Id) ?? false,
                State = _target != null ? TransferService.Compare(s, _target.Space) : TargetState.Missing,
            };
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SessionRow.IsSelected)) UpdateSummary(); };
            _rows.Add(row);
        }
        _rowsView.Refresh();
        UpdateEmptyState();
        UpdateSummary();
    }

    // ------------------------------------------------------------------ filtering & selection

    bool FilterRow(object o)
    {
        if (o is not SessionRow r) return false;
        if (ShowArchived.IsChecked != true && r.IsArchived) return false;
        var q = SearchBox.Text.Trim();
        if (q.Length == 0) return true;
        return r.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
               || r.Cwd.Contains(q, StringComparison.OrdinalIgnoreCase)
               || r.Info.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
               || r.Info.CliSessionId.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _rowsView.Refresh();
        UpdateEmptyState();
        UpdateSummary();
    }

    void OnFilterToggle(object sender, RoutedEventArgs e)
    {
        if (_rowsView == null) return;
        _settings.ShowArchived = ShowArchived.IsChecked == true;
        _rowsView.Refresh();
        UpdateEmptyState();
        UpdateSummary();
    }

    IEnumerable<SessionRow> VisibleRows => _rowsView.Cast<SessionRow>();

    void SetSelection(Func<SessionRow, bool> pred)
    {
        foreach (var r in _rows) r.IsSelected = false;
        foreach (var r in VisibleRows) r.IsSelected = pred(r);
    }

    void OnSelectAll(object sender, RoutedEventArgs e) => SetSelection(_ => true);
    void OnSelectNone(object sender, RoutedEventArgs e) => SetSelection(_ => false);
    void OnSelectMissing(object sender, RoutedEventArgs e) => SetSelection(r => r.State == TargetState.Missing);
    void OnSelectNewer(object sender, RoutedEventArgs e) =>
        SetSelection(r => r.State is TargetState.Missing or TargetState.SourceNewer);

    void OnSessionRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item || item.DataContext is not SessionRow row) return;
        if (FindParent<CheckBox>(e.OriginalSource as DependencyObject) != null)
        {
            _lastClickIndex = IndexOfVisible(row);
            return; // the checkbox toggles itself
        }

        var idx = IndexOfVisible(row);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _lastClickIndex >= 0 && idx >= 0)
        {
            var visible = VisibleRows.ToList();
            var (a, b) = (Math.Min(idx, _lastClickIndex), Math.Max(idx, _lastClickIndex));
            var value = !row.IsSelected || visible[_lastClickIndex].IsSelected;
            for (int i = a; i <= b; i++) visible[i].IsSelected = value;
        }
        else
        {
            row.IsSelected = !row.IsSelected;
        }
        _lastClickIndex = idx;
        e.Handled = true;
    }

    int IndexOfVisible(SessionRow row)
    {
        int i = 0;
        foreach (var r in VisibleRows)
        {
            if (ReferenceEquals(r, row)) return i;
            i++;
        }
        return -1;
    }

    static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    void UpdateEmptyState()
    {
        var any = VisibleRows.Any();
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Text = _rows.Count == 0 ? "This account has no sessions" : "Nothing matches the filter";
    }

    TransferMode Mode => ModeMove.IsChecked == true ? TransferMode.Move : TransferMode.Copy;

    ConflictPolicy Policy => ConflictCombo.SelectedIndex switch
    {
        1 => ConflictPolicy.AlwaysOverwrite,
        2 => ConflictPolicy.Skip,
        _ => ConflictPolicy.OverwriteIfNewer,
    };

    void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (TransferLabel != null) UpdateSummary();
    }

    void UpdateSummary()
    {
        if (TransferLabel == null) return;
        var selected = _rows.Where(r => r.IsSelected).ToList();
        var n = selected.Count;
        var verb = Mode == TransferMode.Move ? "Move" : "Copy";
        TransferLabel.Text = $"{verb} {n} session{(n == 1 ? "" : "s")}";
        TransferButton.IsEnabled = n > 0 && _target != null && _source != null;

        var hidden = selected.Count(r => !FilterRow(r));
        var total = _rows.Count;
        var visible = VisibleRows.Count();
        var parts = new List<string> { $"{n} of {total} selected" };
        if (visible != total) parts.Add($"{visible} shown");
        if (hidden > 0) parts.Add($"{hidden} selected but hidden by the filter");
        if (n > 0)
        {
            var newer = selected.Count(r => r.State == TargetState.TargetNewer);
            if (newer > 0) parts.Add($"{newer} newer in target");
        }
        StatusText.Text = _source == null ? "" : string.Join("  ·  ", parts);
    }

    // ------------------------------------------------------------------ transfer

    async void OnTransfer(object sender, RoutedEventArgs e)
    {
        if (_source == null || _target == null) return;
        var selected = _rows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0) return;

        var mode = Mode;
        var policy = Policy;
        int nNew = selected.Count(r => r.State == TargetState.Missing);
        int nNewer = selected.Count(r => r.State == TargetState.SourceNewer);
        int nSame = selected.Count(r => r.State == TargetState.Same);
        int nOlder = selected.Count(r => r.State == TargetState.TargetNewer);

        string ReplaceLine(int count, string what, bool replaced) =>
            $"•  {count} {what} — {(replaced ? "will be replaced" : "skipped")}";

        var lines = new List<string>();
        if (nNew > 0) lines.Add($"•  {nNew} new to the target");
        if (nNewer > 0) lines.Add(ReplaceLine(nNewer, "older copy there", policy != ConflictPolicy.Skip));
        if (nSame > 0) lines.Add(ReplaceLine(nSame, "already in sync", policy == ConflictPolicy.AlwaysOverwrite));
        if (nOlder > 0) lines.Add(ReplaceLine(nOlder, "NEWER copy there", policy == ConflictPolicy.AlwaysOverwrite));

        var verb = mode == TransferMode.Move ? "Move" : "Copy";
        var msg = $"{verb} {selected.Count} session{(selected.Count == 1 ? "" : "s")}\n" +
                  $"from  {_source.Account.Label}\n" +
                  $"to      {_target.Account.Label}\n\n" + string.Join("\n", lines);
        if (mode == TransferMode.Move)
            msg += "\n\nAfter copying, they are removed from the source account.";
        if (mode == TransferMode.Move && _desktopRunning && _source.Account.IsDesktopActive)
            msg += "\n\n⚠ Claude Desktop is running on the source account and may write these sessions back. Quit it first for a clean move.";
        msg += "\n\nAnything replaced or removed is backed up first.";

        var kind = nOlder > 0 && policy == ConflictPolicy.AlwaysOverwrite ? DialogKind.Warning : DialogKind.Info;
        if (Dialog.Show(this, $"{verb} sessions?", msg, kind, buttons: new[] { verb, "Cancel" }) != 0) return;

        TransferButton.IsEnabled = false;
        StatusText.Text = "Working…";
        var src = _source.Space;
        var dst = _target.Space;
        var infos = selected.Select(r => r.Info).ToList();
        var root = _root;
        TransferResult result;
        try
        {
            result = await Task.Run(() => TransferService.Transfer(src, dst, infos, mode, policy, AppSettings.BackupsDir, root));
        }
        catch (Exception ex)
        {
            Dialog.Show(this, "Transfer failed", ex.Message, DialogKind.Error);
            await ReloadAsync();
            return;
        }

        var summary = new List<string>();
        if (result.Created > 0) summary.Add($"•  {result.Created} added");
        if (result.Overwritten > 0) summary.Add($"•  {result.Overwritten} replaced");
        if (result.Skipped > 0) summary.Add($"•  {result.Skipped} skipped");
        if (result.RemovedFromSource > 0) summary.Add($"•  {result.RemovedFromSource} removed from source");
        if (result.TombstonesCleared > 0) summary.Add($"•  {result.TombstonesCleared} old 'deleted' markers cleared in target");
        var body = string.Join("\n", summary);
        if (result.Errors.Count > 0) body += "\n\nProblems:\n" + string.Join("\n", result.Errors.Take(8));
        if (_desktopRunning && result.Transferred > 0)
            body += "\n\nClaude Desktop is running — restart it to see the new sessions.";

        // Keep the same source/target, clear the selection.
        await ReloadAsync(keepSelection: false);
        StatusText.Text = $"Done: {result.Transferred} transferred, {result.Skipped} skipped.";

        Dialog.Show(this, result.Errors.Count == 0 ? "Done" : "Done, with problems", body,
                    result.Errors.Count == 0 ? DialogKind.Success : DialogKind.Warning);
    }

    // ------------------------------------------------------------------ misc buttons

    async void OnRefresh(object sender, RoutedEventArgs e) => await ReloadAsync();

    void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var dir = _source?.Space.DirectoryPath ?? _root;
        if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    void OnOpenBackups(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.BackupsDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppSettings.BackupsDir}\"") { UseShellExecute = true });
    }

    void OnCopyUuid(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string uuid })
        {
            try { Clipboard.SetText(uuid); StatusText.Text = $"Copied {uuid}"; } catch { }
        }
        e.Handled = true;
    }

    async void OnEditAlias(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: AccountVm vm }) return;
        var id = vm.Account.Identity;
        _settings.Manual.TryGetValue(vm.Uuid, out var manual);
        var res = Dialog.AskIdentity(this, "Account label",
            $"Account {vm.Uuid}\n{vm.SourceText}. Set your own email/label to override it.",
            manual?.Email ?? id.Email ?? "", manual?.DisplayName ?? id.DisplayName ?? "");
        if (res == null) return;
        var (email, name) = res.Value;
        if (email.Length == 0 && name.Length == 0) _settings.Manual.Remove(vm.Uuid);
        else _settings.Manual[vm.Uuid] = new AccountIdentity
        {
            Email = email.Length > 0 ? email : null,
            DisplayName = name.Length > 0 ? name : null,
            Source = "manual",
        };
        _settings.Save();
        await ReloadAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _settings.Save();
        base.OnClosing(e);
    }
}
