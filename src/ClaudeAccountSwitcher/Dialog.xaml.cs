using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudeAccountSwitcher;

public enum DialogKind { Info, Warning, Success, Error }

public partial class Dialog : Window
{
    public int Result { get; private set; } = -1;

    Dialog() => InitializeComponent();

    void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    static Dialog Build(Window? owner, string title, string message, DialogKind kind, UIElement? extra, string[] buttons, int accentIndex)
    {
        var d = new Dialog { Owner = owner, Title = title };
        d.TitleText.Text = title;
        d.MessageText.Text = message;
        d.MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        if (extra != null) d.Extra.Content = extra; else d.Extra.Visibility = Visibility.Collapsed;

        var (glyph, fg, bg) = kind switch
        {
            DialogKind.Warning => ("", "AmberBrush", "AmberSoftBrush"),
            DialogKind.Success => ("", "GreenBrush", "GreenSoftBrush"),
            DialogKind.Error => ("", "RedBrush", "AccentSoftBrush"),
            _ => ("", "AccentBrush", "AccentSoftBrush"),
        };
        d.IconGlyph.Text = glyph;
        d.IconGlyph.Foreground = (Brush)Application.Current.Resources[fg];
        d.IconBox.Background = (Brush)Application.Current.Resources[bg];

        for (int i = 0; i < buttons.Length; i++)
        {
            int idx = i;
            var b = new Button
            {
                Content = buttons[i],
                Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 96,
                Style = (Style)Application.Current.Resources[i == accentIndex ? "AccentButton" : "GhostButton"],
            };
            if (i == accentIndex) { b.Height = 36; b.FontSize = 13; b.IsDefault = true; }
            if (i == buttons.Length - 1 && buttons.Length > 1 && i != accentIndex) b.IsCancel = true;
            b.Click += (_, _) => { d.Result = idx; d.Close(); };
            d.Buttons.Children.Add(b);
        }
        d.KeyDown += (_, e) => { if (e.Key == Key.Escape) d.Close(); };
        return d;
    }

    public static int Show(Window? owner, string title, string message, DialogKind kind = DialogKind.Info,
                           UIElement? extra = null, string[]? buttons = null, int accentIndex = 0)
    {
        var d = Build(owner, title, message, kind, extra, buttons ?? new[] { "OK" }, accentIndex);
        d.ShowDialog();
        return d.Result;
    }

    /// <summary>Two text fields (email + label). Returns null on cancel.</summary>
    public static (string email, string name)? AskIdentity(Window owner, string title, string message, string email, string name)
    {
        TextBox MakeBox(string text, string hint) => new() { Text = text, Tag = hint, Margin = new Thickness(0, 4, 0, 10) };
        TextBlock MakeLabel(string t) => new()
        {
            Text = t, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["FaintBrush"],
        };

        var emailBox = MakeBox(email, "name@example.com");
        var nameBox = MakeBox(name, "e.g. Work, Personal…");
        var panel = new StackPanel();
        panel.Children.Add(MakeLabel("EMAIL"));
        panel.Children.Add(emailBox);
        panel.Children.Add(MakeLabel("LABEL (OPTIONAL)"));
        panel.Children.Add(nameBox);

        var d = Build(owner, title, message, DialogKind.Info, panel, new[] { "Save", "Clear", "Cancel" }, 0);
        d.Loaded += (_, _) => { emailBox.Focus(); emailBox.SelectAll(); };
        d.ShowDialog();
        return d.Result switch
        {
            0 => (emailBox.Text.Trim(), nameBox.Text.Trim()),
            1 => ("", ""),
            _ => null,
        };
    }
}
