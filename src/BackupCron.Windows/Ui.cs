using BackupCron.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BackupCron.Windows;

internal static class Ui
{
    public static readonly Brush Background = new SolidColorBrush(Color.FromRgb(246, 248, 250));
    public static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(16, 122, 137));
    public static readonly Brush TextColor = new SolidColorBrush(Color.FromRgb(28, 37, 48));
    public static Button Button(string key, Action action, bool primary = false)
    {
        var button = new Button { Content = L10n.Text(key), Padding = new Thickness(13, 7, 13, 7), Margin = new Thickness(3), MinHeight = 34 };
        if (primary) { button.Background = Accent; button.Foreground = Brushes.White; }
        button.Click += (_, _) => action(); return button;
    }
    public static TextBlock Text(string value, double size = 14) => new() { Text = value, FontSize = size, Foreground = TextColor, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
    public static StackPanel Row(params UIElement[] elements)
    { var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) }; foreach (var e in elements) panel.Children.Add(e); return panel; }
    public static void Label(Panel panel, string key, UIElement control)
    { panel.Children.Add(Text(L10n.Text(key))); if (control is FrameworkElement f) f.Margin = new Thickness(4, 0, 4, 8); panel.Children.Add(control); }
    public static TextBox Input(string value = "") => new() { Text = value, Padding = new Thickness(7), MinHeight = 32 };
    public static CheckBox Check(string key, bool enabled) => new() { Content = L10n.Text(key), IsChecked = enabled, Margin = new Thickness(4, 8, 4, 8) };
    public static void Error(Window owner, Exception ex) => MessageBox.Show(owner, ex.Message, "BackupCron", MessageBoxButton.OK, MessageBoxImage.Error);
    public static string Date(DateTimeOffset? value) => value?.ToLocalTime().ToString("g") ?? "—";
}
