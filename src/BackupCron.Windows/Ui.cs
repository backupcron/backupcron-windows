using BackupCron.Core;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace BackupCron.Windows;

internal static class Ui
{
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    public static Brush Background => Brush("Canvas");
    public static Brush Accent => Brush("Accent");
    public static Brush TextColor => Brush("Ink");
    public static void Install(Application app)
    {
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BackupCron;component/Theme.xaml", UriKind.Relative) });
        if (SystemParameters.HighContrast)
        {
            foreach (var key in new[] { "Canvas", "Sidebar", "Panel", "Raised", "SuccessFill", "WarningFill", "DangerFill" }) app.Resources[key] = SystemColors.WindowBrush;
            foreach (var key in new[] { "Ink", "Muted", "Success", "Warning", "Danger" }) app.Resources[key] = SystemColors.WindowTextBrush;
            app.Resources["Line"] = SystemColors.WindowTextBrush; app.Resources["Accent"] = SystemColors.HighlightBrush;
            app.Resources["AccentText"] = SystemColors.HighlightTextBrush; app.Resources["Selection"] = SystemColors.HighlightBrush;
        }
    }
    public static void Window(Window window) { window.Background = Background; window.FontFamily = new FontFamily("Segoe UI"); window.FontSize = 13; window.Foreground = TextColor; window.UseLayoutRounding = true; TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display); }
    public static T Id<T>(T element, string id) where T : DependencyObject { AutomationProperties.SetAutomationId(element, id); return element; }
    public static Button Button(string key, Action action, bool primary = false, string? icon = null)
    {
        var button = new Button { Content = icon == null ? L10n.Text(key) : Row(Icon(icon, 16), Text(L10n.Text(key), 13)), Margin = new Thickness(0, 0, 8, 0) };
        if (primary) { button.Background = Accent; button.Foreground = Brush("AccentText"); }
        if (button.Content is StackPanel row) foreach (var child in row.Children.OfType<TextBlock>()) child.Foreground = button.Foreground;
        if (button.Content is StackPanel icons) foreach (var child in icons.Children.OfType<Viewbox>()) ((System.Windows.Shapes.Path)child.Child).Stroke = button.Foreground;
        AutomationProperties.SetName(button, L10n.Text(key)); button.Click += (_, _) => action(); return button;
    }
    public static TextBlock Text(string value, double size = 13, bool muted = false) => new() { Text = value, FontSize = size, Foreground = Brush(muted ? "Muted" : "Ink"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    public static TextBlock Heading(string value, double size = 15) { var text = Text(value, size); text.FontWeight = FontWeights.SemiBold; return text; }
    public static StackPanel Row(params UIElement[] elements)
    { var panel = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var element in elements) { if (element is FrameworkElement f) f.VerticalAlignment = VerticalAlignment.Center; panel.Children.Add(element); } return panel; }
    public static void Label(Panel panel, string key, UIElement control)
    { var text = Text(L10n.Text(key), 12, true); text.Margin = new Thickness(0, 0, 0, 6); panel.Children.Add(text); if (control is FrameworkElement f) f.Margin = new Thickness(0, 0, 0, 16); AutomationProperties.SetName(control, L10n.Text(key)); panel.Children.Add(control); }
    public static TextBox Input(string value = "") => new() { Text = value };
    public static CheckBox Check(string key, bool enabled) { var check = new CheckBox { Content = L10n.Text(key), IsChecked = enabled, Margin = new Thickness(0, 4, 0, 12) }; AutomationProperties.SetName(check, L10n.Text(key)); return check; }
    public static Border Card(UIElement child, string fill = "Panel", double padding = 16) => new() { Child = child, Background = Brush(fill), BorderBrush = Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(padding) };
    public static Border Divider() => new() { Height = 1, Background = Brush("Line"), Margin = new Thickness(0, 18, 0, 18) };
    public static StackPanel Metric(string label, string value)
    { var panel = new StackPanel(); panel.Children.Add(Text(label, 11, true)); var text = Text(value, 13); text.Margin = new Thickness(0, 5, 0, 0); panel.Children.Add(text); return panel; }
    public static Grid Columns(params UIElement[] children)
    { var grid = new Grid(); foreach (var child in children) { grid.ColumnDefinitions.Add(new()); Grid.SetColumn(child, grid.Children.Count); if (child is FrameworkElement f) f.Margin = new Thickness(0, 0, 20, 0); grid.Children.Add(child); } return grid; }
    public static string KindIcon(BackupKind kind) => kind == BackupKind.Project ? "project" : kind == BackupKind.Files ? "folder" : "database";
    public static Viewbox Icon(string name, double size = 20, string color = "Muted")
    {
        var data = name switch
        {
            "home" => "M3,11 L12,3 21,11 M5,10 L5,21 10,21 10,15 14,15 14,21 19,21 19,10",
            "folder" => "M3,6 L9,6 11,8 21,8 21,20 3,20 Z M3,8 L3,4 9,4 11,6 19,6 19,8",
            "database" => "M3,6 C3,1 21,1 21,6 C21,11 3,11 3,6 Z M3,6 L3,18 C3,23 21,23 21,18 L21,6 M3,12 C3,17 21,17 21,12",
            "project" => "M14,3 L21,10 18,13 11,6 Z M13,11 L3,21 6,24 16,14 M12,4 L9,7",
            "history" => "M3,7 L3,12 8,12 M3,12 C1,1 21,1 21,12 C21,23 5,25 3,17 M12,6 L12,12 16,14",
            "check" => "M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 M7,12 L10,15 17,8",
            "warning" => "M12,3 L22,21 2,21 Z M12,9 L12,14 M12,17 L12,18",
            "error" => "M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 M8,8 L16,16 M16,8 L8,16",
            "clock" => "M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 M12,6 L12,12 16,14",
            "drive" => "M5,4 L19,4 22,15 22,21 2,21 2,15 Z M2,15 L22,15 M17,18 L19,18",
            "settings" => "M9,3 L15,3 16,6 19,7 22,6 24,11 21,13 21,16 22,19 17,22 14,20 11,20 8,22 3,19 4,16 4,13 1,11 3,6 6,7 9,6 Z M16,13 A4,4 0 1 1 8,13 A4,4 0 1 1 16,13",
            "plus" => "M12,4 L12,20 M4,12 L20,12", "chevron" => "M9,5 L16,12 9,19",
            "edit" => "M4,17 L16,5 20,9 8,21 3,22 Z M14,7 L18,11",
            "log" => "M5,2 L15,2 20,7 20,22 5,22 Z M15,2 L15,7 20,7 M8,12 L17,12 M8,16 L17,16",
            "more" => "M3,12 L5,12 M11,12 L13,12 M19,12 L21,12",
            "refresh" => "M20,8 C15,0 3,2 3,12 C3,23 19,25 21,15 M20,3 L20,8 15,8",
            "close" => "M6,6 L18,18 M18,6 L6,18", "search" => "M17,10 A7,7 0 1 1 3,10 A7,7 0 1 1 17,10 M15,15 L22,22",
            _ => "M4,4 L20,4 20,20 4,20 Z"
        };
        return new Viewbox { Width = size, Height = size, Margin = new Thickness(0, 0, 8, 0), Child = new System.Windows.Shapes.Path { Data = Geometry.Parse(data), Stroke = Brush(color), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Width = 26, Height = 26, Stretch = Stretch.Uniform } };
    }
    public static void Error(Window owner, Exception ex) => MessageBox.Show(owner, ex.Message, "BackupCron", MessageBoxButton.OK, MessageBoxImage.Error);
    public static string Date(DateTimeOffset? value) => value?.ToLocalTime().ToString("g") ?? "—";
}
