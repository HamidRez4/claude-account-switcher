using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using SwitcherCore;

namespace ClaudeAccountSwitcher;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public static class Fmt
{
    public static string Relative(DateTimeOffset? t)
    {
        if (t == null) return "—";
        var d = DateTimeOffset.Now - t.Value;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays}d ago";
        if (d.TotalDays < 60) return $"{(int)(d.TotalDays / 7)}w ago";
        return t.Value.LocalDateTime.ToString("yyyy-MM-dd");
    }

    public static string Absolute(DateTimeOffset? t) =>
        t == null ? "unknown" : t.Value.LocalDateTime.ToString("yyyy-MM-dd  HH:mm");

    public static string ShortModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "";
        var m = model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) ? model[7..] : model;
        var bracket = m.IndexOf('[');
        if (bracket > 0) m = m[..bracket];
        return m;
    }

    static readonly Color[] AvatarColors =
    {
        Color.FromRgb(0xD9, 0x77, 0x57), Color.FromRgb(0x6E, 0xA8, 0xFE), Color.FromRgb(0x4C, 0xC3, 0x8A),
        Color.FromRgb(0xB4, 0x8C, 0xF2), Color.FromRgb(0xE5, 0xB4, 0x54), Color.FromRgb(0x3F, 0xC1, 0xC9),
        Color.FromRgb(0xF0, 0x6A, 0x9A),
    };

    public static Brush AvatarBrush(string seed)
    {
        int h = 0;
        foreach (var c in seed) h = unchecked(h * 31 + c);
        var col = AvatarColors[(int)((uint)h % AvatarColors.Length)];
        var b = new LinearGradientBrush(col, Color.FromArgb(0xFF, (byte)(col.R * 0.7), (byte)(col.G * 0.7), (byte)(col.B * 0.7)), 45);
        b.Freeze();
        return b;
    }
}

public sealed class AccountVm : Observable
{
    public AccountVm(Account account) => Account = account;

    public Account Account { get; }
    public string Uuid => Account.Uuid;
    public string ShortUuid => Account.ShortUuid;
    public bool HasEmail => Account.Identity.Email != null;
    public string Title => Account.Identity.Email ?? "Unknown account";

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Account.Identity.DisplayName)) parts.Add(Account.Identity.DisplayName!);
            if (!string.IsNullOrWhiteSpace(Account.Identity.OrganizationName) &&
                Account.Identity.OrganizationName != Account.Identity.DisplayName) parts.Add(Account.Identity.OrganizationName!);
            if (parts.Count == 0) parts.Add(HasEmail ? "" : "Click ✎ to set an email/label");
            return string.Join(" · ", parts.Where(p => p.Length > 0));
        }
    }

    public string Initial
    {
        get
        {
            var s = Account.Identity.DisplayName ?? Account.Identity.Email ?? Account.Uuid;
            return s.Length > 0 ? char.ToUpperInvariant(s[0]).ToString() : "?";
        }
    }

    public Brush Avatar => Fmt.AvatarBrush(Account.Uuid);
    public int SessionCount => Account.SessionCount;
    public string SessionCountText => SessionCount == 1 ? "1 session" : $"{SessionCount} sessions";
    public string LastActive => "active " + Fmt.Relative(Account.LastActivityAt);
    public bool IsDesktopActive => Account.IsDesktopActive;
    public bool IsCliActive => Account.IsCliActive;
    public string SourceText => Account.Identity.Source is { } s ? "email from " + s : "email not found";
    public string Tooltip => $"Account {Account.Uuid}\n{SourceText}\n{string.Join("\n", Account.Spaces.Select(sp => $"org {sp.OrgUuid}: {sp.Sessions.Count} sessions"))}";
}

public sealed class SpaceOption
{
    public SpaceOption(Account account, AccountSpace space, bool isPrimary)
    {
        Account = account;
        Space = space;
        IsPrimary = isPrimary;
    }

    public Account Account { get; }
    public AccountSpace Space { get; }
    public bool IsPrimary { get; }
    public string Key => Space.AccountUuid + "/" + Space.OrgUuid;
    public string Title => Account.Label;
    public string Detail =>
        $"org {Space.OrgUuid[..8]} · {Space.Sessions.Count} sessions" + (IsPrimary ? "" : " · secondary org");
    public string OrgLabel =>
        $"Org {Space.OrgUuid[..8]}  ·  {Space.Sessions.Count} sessions" + (IsPrimary ? "  (main)" : "");
    public Brush Avatar => Fmt.AvatarBrush(Account.Uuid);
    public string Initial => (Account.Identity.DisplayName ?? Account.Identity.Email ?? Account.Uuid)[..1].ToUpperInvariant();
}

public sealed class SessionRow : Observable
{
    bool _isSelected;
    TargetState _state;

    public SessionRow(SessionInfo info) => Info = info;

    public SessionInfo Info { get; }
    public string Title => Info.Title;
    public string ProjectName => string.IsNullOrEmpty(Info.ProjectName) ? "no folder" : Info.ProjectName;
    public string Cwd => Info.Cwd;
    public string When => Fmt.Relative(Info.LastActivityAt);
    public string Model => Fmt.ShortModel(Info.Model);
    public bool HasModel => Model.Length > 0;
    public bool IsArchived => Info.IsArchived;
    public bool IsStarred => Info.IsStarred;

    public string Tooltip =>
        $"{Info.Title}\n{Info.Cwd}\n\nLast activity: {Fmt.Absolute(Info.LastActivityAt)}\nCreated: {Fmt.Absolute(Info.CreatedAt)}" +
        $"\nSession: local_{Info.Id}" + (Info.CliSessionId.Length > 0 ? $"\nCLI session: {Info.CliSessionId}" : "") +
        $"\nSize: {Info.SizeBytes / 1024.0:0.#} KB";

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public TargetState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value)) { Raise(nameof(StateText)); Raise(nameof(StateTip)); }
        }
    }

    public string StateText => State switch
    {
        TargetState.Missing => "New",
        TargetState.SourceNewer => "Newer here",
        TargetState.Same => "In sync",
        TargetState.TargetNewer => "Newer there",
        _ => "",
    };

    public string StateTip => State switch
    {
        TargetState.Missing => "Not in the target account yet.",
        TargetState.SourceNewer => "Target has an older copy — this one has newer activity.",
        TargetState.Same => "Target already has this exact session.",
        TargetState.TargetNewer => "Target's copy has NEWER activity than this one.",
        _ => "",
    };
}
