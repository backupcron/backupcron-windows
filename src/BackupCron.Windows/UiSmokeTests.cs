using BackupCron.Core;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BackupCron.Windows;

// Exercises the installed WPF controls against isolated, real backup fixtures on Windows CI.
internal static class UiSmokeTests
{
    internal static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>(); var queue = new Queue<DependencyObject>(); queue.Enqueue(root);
        while (queue.TryDequeue(out var item))
        {
            if (!seen.Add(item)) continue; yield return item;
            foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>()) queue.Enqueue(child);
            if (item is Visual) for (var i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++) queue.Enqueue(VisualTreeHelper.GetChild(item, i));
        }
    }
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => Tree(root).OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static void Click(DependencyObject root, string id)
    { var button = Find<Button>(root, id); Require(button.IsEnabled, "Disabled action: " + id); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("UI operation did not complete."); await Task.Delay(50); } }
    private static async Task Render(Window window, string output, string name)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        // Render application client content, excluding operating-system chrome and automation overlays.
        var visual = (FrameworkElement)window.Content; var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(output, name)); encoder.Save(stream);
    }
    private static void Dialog(Window dialog, Func<Task> test)
    {
        Exception? failure = null;
        dialog.Loaded += async (_, _) => { try { await test(); } catch (Exception ex) { failure = ex; dialog.Close(); } };
        dialog.ShowDialog(); if (failure != null) throw failure;
    }
    internal static async Task RunAsync(BackupStore store, MainWindow window, BackupJob file, BackupJob sqlite, string output)
    {
        await Render(window, output, "BackupCron-Windows-preview.png");
        Click(window, "overview-job-" + file.Id); Require(window.CurrentPage == "job" && window.CurrentJob == file.Id, "Overview does not open its job.");
        Click(window, "job-log"); Require(Find<TextBox>(window, "job-log-text").Text.Length > 0, "Successful backup logs are missing."); Click(window, "job-log");
        await Render(window, output, "job-details.png");
        var oldEnabled = file.IsEnabled; Click(window, "job-toggle"); Require(store.Snapshot().Jobs.Single(j => j.Id == file.Id).IsEnabled != oldEnabled, "Pause action did not save."); Click(window, "job-toggle");
        await Task.Delay(1100); var count = store.Snapshot().History.Count; Click(window, "job-run"); await Until(() => !store.Running.ContainsKey(file.Id)); window.Refresh(); Require(store.Snapshot().History.Count == count + 1 && store.Snapshot().History[0].Status == RunStatus.Success, "UI run action did not create a verified backup.");
        Click(window, "nav-overview"); var search = Find<TextBox>(window, "job-search"); search.Text = "no-such-job"; Require(!Tree(window).OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b).StartsWith("overview-job-")), "Job search did not filter."); search.Text = "";
        search.Focus(); Require(search.IsKeyboardFocusWithin, "Search cannot receive keyboard focus."); Require(search.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)), "Keyboard navigation has no next target.");
        Click(window, "nav-history"); Require(window.CurrentPage == "history", "History navigation failed."); Find<ComboBox>(window, "history-filter").SelectedIndex = 2; Require(Tree(window).OfType<TextBlock>().Any(t => t.Text == L10n.Text("history.empty_description")), "Failure filter included successful runs."); Find<ComboBox>(window, "history-filter").SelectedIndex = 1; await Render(window, output, "history.png");
        window.Width = window.MinWidth; window.Height = window.MinHeight; Click(window, "nav-overview"); await Render(window, output, "compact-overview.png"); Require(Find<Button>(window, "overview-new-job").ActualWidth > 0, "Compact layout hid the create action."); window.Width = 1160; window.Height = 850;
        var editor = new JobEditorWindow(store, file) { Owner = window };
        Dialog(editor, async () =>
        {
            await Render(editor, output, "editor-source.png");
            Find<TextBox>(editor, "editor-name").Text = "Project files updated"; editor.Tabs.SelectedIndex = 1;
            var zip = Find<CheckBox>(editor, "editor-archive"); zip.IsChecked = false; await Render(editor, output, "editor-destination.png");
            editor.Tabs.SelectedIndex = 2; Find<ComboBox>(editor, "editor-schedule").SelectedIndex = (int)ScheduleKind.Weekly; await Render(editor, output, "editor-schedule.png"); Click(editor, "editor-save");
        });
        var saved = store.Snapshot().Jobs.Single(j => j.Id == file.Id); Require(saved.Name == "Project files updated" && !saved.ArchiveOutput && saved.Schedule == ScheduleKind.Weekly && saved.RetentionCount == file.RetentionCount && saved.DestinationPath == file.DestinationPath && store.Password(file.Id) == "smoke-credential", "Editor save lost backup options or credentials.");
        var databaseEditor = new JobEditorWindow(store, sqlite) { Owner = window };
        Dialog(databaseEditor, async () =>
        {
            databaseEditor.Tabs.SelectedIndex = 3; Click(databaseEditor, "editor-load-tables"); var grid = Find<DataGrid>(databaseEditor, "editor-table-grid"); await Until(() => grid.Items.Count == 2 && Find<Button>(databaseEditor, "editor-save").IsEnabled);
            var choices = grid.Items.Cast<TableChoice>().ToArray(); Require(choices.Single(c => c.Name == "customers").Include && !choices.Single(c => c.Name == "logs").Include, "Database exclusions changed while loading sizes."); var measured = await new BackupEngine().InspectDatabaseAsync(sqlite, store.Password(sqlite.Id));
            Require(measured.SizeBytes > 0 && choices.All(c => c.Bytes == measured.Tables.Single(t => t.Id == c.Table.Id).SizeBytes), "Displayed table sizes differ from the database inspection.");
            Require(choices.Where(c => c.Bytes == null).All(c => c.Size == "—"), "Unknown table sizes are presented as known.");
            Require(Tree(databaseEditor).OfType<TextBlock>().Any(t => t.Text.Contains(L10n.Text("database.total_size")) && t.Text.Contains(L10n.Size(measured.SizeBytes))), "Total database size is missing from the editor.");
            Find<TextBox>(databaseEditor, "editor-table-search").Text = "customers"; Require(grid.Items.Count == 1, "Table search failed."); Find<TextBox>(databaseEditor, "editor-table-search").Text = "";
            await Render(databaseEditor, output, "database-tables.png"); Click(databaseEditor, "editor-save");
        });
        Require(store.Snapshot().Jobs.Single(j => j.Id == sqlite.Id).ExcludedDatabaseTables.SequenceEqual(sqlite.ExcludedDatabaseTables), "Editor save lost complete table exclusions.");
        var settings = new SettingsWindow(store) { Owner = window }; var prior = store.Snapshot().Settings;
        Dialog(settings, async () =>
        {
            await Render(settings, output, "settings-general.png"); settings.Tabs.SelectedIndex = 2; Find<TextBox>(settings, "settings-storage-gb").Text = "25"; Find<TextBox>(settings, "settings-storage-percent").Text = "15";
            await Render(settings, output, "settings-storage.png"); Click(settings, "settings-save");
        });
        var updated = store.Snapshot().Settings; Require(updated.StorageWarningGiB == 25 && updated.StorageWarningPercent == 15 && updated.SchedulerEnabled == prior.SchedulerEnabled && updated.Language == prior.Language && updated.NotificationsEnabled == prior.NotificationsEnabled, "Settings save lost preferences.");
        await store.Run(file.Id); Require(store.Snapshot().History[0].Status == RunStatus.Success && Directory.Exists(store.Snapshot().History[0].OutputPath), "Edited ordinary folder copy failed.");
        var create = new JobEditorWindow(store, null) { Owner = window };
        Dialog(create, async () =>
        {
            Find<TextBox>(create, "editor-name").Text = "Single file"; Find<TextBox>(create, "editor-source").Text = Path.Combine(file.SourcePath, "report.txt"); create.Tabs.SelectedIndex = 1;
            Find<TextBox>(create, "editor-destination").Text = file.DestinationPath; Find<CheckBox>(create, "editor-archive").IsChecked = false; await Task.CompletedTask; Click(create, "editor-save");
        });
        await store.Run(create.SavedJobId); Require(store.Snapshot().History[0].Status == RunStatus.Success && File.Exists(store.Snapshot().History[0].OutputPath), "New single-file job failed.");
        window.Navigate("overview"); await window.RefreshStorageAsync();
        File.WriteAllText(Path.Combine(output, "ui-result.txt"), "PASS: overview/detail/history navigation; search; pause/resume; backup from UI; logs; keyboard focus; compact window; file editor and schedule persistence; SQLite sizes/search/exclusions; settings persistence; edited folder backup; new single-file job and backup. Screenshots rendered from the installed WPF application.\n");
    }
}
