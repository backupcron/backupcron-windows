using BackupCron.Core;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace BackupCron.Windows;

internal sealed record JobRow(Guid Id, string Name, string Kind, string Status, string LastSuccess, string NextRun);
internal sealed class MainWindow : Window
{
    private readonly BackupStore store;
    private readonly ObservableCollection<JobRow> rows = [];
    private readonly DataGrid jobs = new() { IsReadOnly = true, AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Single, CanUserAddRows = false, MinHeight = 200, Background = Brushes.White };
    private readonly TextBox search = Ui.Input();
    private readonly TextBlock summary = Ui.Text("", 14);
    private readonly TextBlock details = Ui.Text("", 14);
    private readonly TextBox logs = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), Padding = new Thickness(8), Height = 140 };
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 1, Height = 8, Margin = new Thickness(4, 8, 4, 8) };
    private readonly DispatcherTimer timer;
    private bool quitting;
    private readonly WindowsFeedback? feedback;
    public MainWindow(BackupStore store, bool smokeTest = false)
    {
        this.store = store; Title = "BackupCron"; Width = 1180; Height = 790; MinWidth = 950; MinHeight = 650; Background = Ui.Background; FontFamily = new FontFamily("Segoe UI");
        var outer = new Grid(); outer.ColumnDefinitions.Add(new() { Width = new GridLength(185) }); outer.ColumnDefinitions.Add(new()); Content = outer;
        var sidebar = new StackPanel { Margin = new Thickness(14), Background = Ui.Background };
        sidebar.Children.Add(Ui.Text("BackupCron", 25)); sidebar.Children.Add(Ui.Text(L10n.Text("app.tagline"))); sidebar.Children.Add(Ui.Button("common.overview", () => { Refresh(); })); sidebar.Children.Add(Ui.Button("common.history", ShowHistory)); sidebar.Children.Add(Ui.Button("common.new_job", NewJob, true)); sidebar.Children.Add(Ui.Button("windows.settings", Settings)); sidebar.Children.Add(Ui.Text(L10n.Text("ux.schedule_open"), 12)); outer.Children.Add(sidebar);
        var content = new Grid { Margin = new Thickness(15) }; Grid.SetColumn(content, 1); outer.Children.Add(content);
        content.RowDefinitions.Add(new() { Height = GridLength.Auto }); content.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var top = new StackPanel(); top.Children.Add(Ui.Text(L10n.Text("common.overview"), 25)); top.Children.Add(summary); search.ToolTip = L10n.Text("database.search"); search.TextChanged += (_, _) => Refresh(); top.Children.Add(search); content.Children.Add(top);
        foreach (var column in new[] { ("editor.name", "Name", 200d), ("editor.data_type", "Kind", 105d), ("editor.state", "Status", 150d), ("ux.last_success", "LastSuccess", 150d), ("job.next_run", "NextRun", 150d) }) jobs.Columns.Add(new DataGridTextColumn { Header = L10n.Text(column.Item1), Binding = new Binding(column.Item2), Width = column.Item3 });
        jobs.ItemsSource = rows; jobs.SelectionChanged += (_, _) => UpdateDetails(); jobs.MouseDoubleClick += (_, _) => EditSelected(); Grid.SetRow(jobs, 1); content.Children.Add(jobs);
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; bottom.Children.Add(Ui.Row(Ui.Button("common.run_now", RunSelected, true), Ui.Button("common.edit", EditSelected), Ui.Button("job.active", ToggleSelected), Ui.Button("common.cancel", CancelSelected), Ui.Button("common.delete", DeleteSelected)));
        bottom.Children.Add(details); bottom.Children.Add(progress); bottom.Children.Add(logs); Grid.SetRow(bottom, 2); content.Children.Add(bottom);
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; int seconds = 0; timer.Tick += (_, _) => { if (++seconds >= 30) { seconds = 0; Try(() => store.Tick(DateTimeOffset.Now)); } Refresh(); };
        if (!smokeTest)
        {
            feedback = new WindowsFeedback(Restore, Quit); store.Finished += run => Dispatcher.InvokeAsync(() => { try { feedback.Notify(run, store.Snapshot().Settings, IsActive); } catch (Exception ex) { summary.Text = ex.Message; } Refresh(); }); timer.Start();
            Closing += (_, e) => { if (!quitting) { e.Cancel = true; Hide(); } };
        }
        Closed += (_, _) => { timer.Stop(); feedback?.Dispose(); };
        Refresh();
    }
    public void Restore() { Show(); WindowState = WindowState.Normal; Activate(); }
    private Guid? SelectedId => (jobs.SelectedItem as JobRow)?.Id;
    private void NewJob() { var editor = new JobEditorWindow(store, null) { Owner = this }; if (editor.ShowDialog() == true) Refresh(); }
    private void EditSelected() { if (SelectedId is not { } id) return; Try(() => { if (store.Running.ContainsKey(id)) throw new InvalidOperationException(L10n.Text("job.running")); var editor = new JobEditorWindow(store, store.Snapshot().Jobs.First(j => j.Id == id)) { Owner = this }; if (editor.ShowDialog() == true) Refresh(); }); }
    private void RunSelected() { if (SelectedId is { } id) Try(() => _ = store.Run(id)); }
    private void CancelSelected() { if (SelectedId is { } id) store.Cancel(id); }
    private void ToggleSelected() { if (SelectedId is { } id) Try(() => store.SetEnabled(id, !store.Snapshot().Jobs.First(j => j.Id == id).IsEnabled)); }
    private void DeleteSelected()
    {
        if (SelectedId is not { } id) return; var job = store.Snapshot().Jobs.First(j => j.Id == id);
        if (MessageBox.Show(this, L10n.Text("job.delete_message", job.Name), L10n.Text("job.delete_title"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) Try(() => { store.Delete(id); Refresh(); });
    }
    private void Settings() { Try(() => { new SettingsWindow(store) { Owner = this }.ShowDialog(); Refresh(); }); }
    private void ShowHistory()
    {
        var snapshot = store.Snapshot(); var window = new Window { Title = L10n.Text("common.history"), Owner = this, Width = 1000, Height = 620, Background = Ui.Background };
        var panel = new DockPanel { Margin = new Thickness(15) }; var filter = new ComboBox { ItemsSource = new[] { L10n.Text("history.all"), L10n.Text("history.successful"), L10n.Text("history.failed"), L10n.Text("ux.last_cancelled") }, SelectedIndex = 0, Padding = new Thickness(7) }; DockPanel.SetDock(filter, Dock.Top); panel.Children.Add(filter);
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false };
        foreach (var c in new[] { ("editor.name", "JobName"), ("job.last_run", "FinishedAt"), ("editor.state", "Status"), ("job.parameters", "Message"), ("common.source", "OutputPath") }) grid.Columns.Add(new DataGridTextColumn { Header = L10n.Text(c.Item1), Binding = new Binding(c.Item2), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        void ApplyFilter() => grid.ItemsSource = snapshot.History.Where(r => filter.SelectedIndex switch { 1 => r.Status == RunStatus.Success, 2 => r.Status == RunStatus.Failure, 3 => r.Status == RunStatus.Cancelled, _ => true }).ToList();
        filter.SelectionChanged += (_, _) => ApplyFilter(); ApplyFilter(); panel.Children.Add(grid);
        var actions = Ui.Row(Ui.Button("common.show_in_finder", () => { if (grid.SelectedItem is BackupRun r && r.OutputPath != null) Try(() => WindowsFiles.Reveal(r.OutputPath)); }), Ui.Button("ux.show_log", () => { if (grid.SelectedItem is BackupRun r) MessageBox.Show(window, string.Join(Environment.NewLine, r.Logs), r.JobName); })); DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Insert(1, actions); window.Content = panel; window.ShowDialog();
    }
    public void Refresh()
    {
        var selected = SelectedId; var snapshot = store.Snapshot(); var query = search.Text.Trim();
        var latest = snapshot.History.GroupBy(r => r.JobId).ToDictionary(g => g.Key, g => g.First()); var successes = snapshot.History.Where(r => r.Status == RunStatus.Success).GroupBy(r => r.JobId).ToDictionary(g => g.Key, g => g.First());
        var fresh = snapshot.Jobs.Where(j => query.Length == 0 || j.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || L10n.Kind(j.Kind).Contains(query, StringComparison.CurrentCultureIgnoreCase)).Select(j => new JobRow(j.Id, j.Name, L10n.Kind(j.Kind), store.Running.ContainsKey(j.Id) ? L10n.Text("job.running") : latest.TryGetValue(j.Id, out var r) ? L10n.Text(r.Status == RunStatus.Success ? "status.success" : r.Status == RunStatus.Failure ? "status.failure" : "ux.last_cancelled") : L10n.Text("ux.not_run"), Ui.Date(successes.GetValueOrDefault(j.Id)?.FinishedAt), snapshot.Settings.SchedulerEnabled ? Ui.Date(Scheduling.NextRun(j, DateTimeOffset.Now)) : L10n.Text("status.scheduler_paused"))).ToList();
        if (!rows.SequenceEqual(fresh)) { rows.Clear(); foreach (var row in fresh) rows.Add(row); if (selected != null) jobs.SelectedItem = rows.FirstOrDefault(r => r.Id == selected); }
        summary.Text = store.Error ?? $"{L10n.Text("common.jobs")}: {snapshot.Jobs.Count}   ·   {L10n.Text("dashboard.successful")}: {snapshot.History.Count(r => r.Status == RunStatus.Success)}   ·   {L10n.Text("dashboard.errors")}: {snapshot.History.Count(r => r.Status == RunStatus.Failure)}";
        UpdateDetails();
    }
    private void UpdateDetails()
    {
        if (SelectedId is not { } id) { details.Text = L10n.Text(rows.Count == 0 ? "dashboard.create_first_job" : "ux.open_job"); logs.Text = ""; progress.Value = 0; return; }
        var snapshot = store.Snapshot(); var job = snapshot.Jobs.First(j => j.Id == id); var last = snapshot.History.FirstOrDefault(r => r.JobId == id);
        details.Text = $"{L10n.Text("common.source")}: {(job.HasDatabaseSource && job.Kind != BackupKind.Sqlite ? job.DatabaseName : job.SourcePath)}\n{L10n.Text("editor.destination")}: {job.DestinationPath}";
        if (store.Running.TryGetValue(id, out var running)) { progress.Value = running.Progress.Fraction; logs.Text = running.Progress.Message + Environment.NewLine + string.Join(Environment.NewLine, running.Logs); }
        else { progress.Value = last?.Status == RunStatus.Success ? 1 : 0; logs.Text = last == null ? L10n.Text("job.history_empty") : last.Message + Environment.NewLine + (last.OutputPath ?? "") + Environment.NewLine + string.Join(Environment.NewLine, last.Logs); }
    }
    private void Try(Action action) { try { action(); } catch (Exception ex) { Ui.Error(this, ex); } }
    private async void Quit()
    {
        quitting = true; timer.Stop(); store.CancelAll(); var pending = store.Running.Values.Select(r => r.Task ?? Task.CompletedTask).ToArray(); await Task.WhenAll(pending); Close(); System.Windows.Application.Current.Shutdown();
    }
}
