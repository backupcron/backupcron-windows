using BackupCron.Core;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BackupCron.Windows;

internal sealed class MainWindow : Window
{
    private readonly BackupStore store;
    private readonly StackPanel sidebarJobs = new();
    private readonly StackPanel body = new();
    private readonly ScrollViewer scroll;
    private readonly TextBox search = Ui.Id(Ui.Input(), "job-search");
    private readonly Button overview, history;
    private readonly DispatcherTimer timer;
    private readonly WindowsFeedback? feedback;
    private Guid? selectedId;
    private string page = "overview", renderStamp = "", sidebarStamp = "";
    private int historyFilter;
    private bool logVisible, quitting, storageRefreshing;
    private DateTimeOffset storageDue = DateTimeOffset.MinValue;
    internal string CurrentPage => page;
    internal Guid? CurrentJob => selectedId;

    public MainWindow(BackupStore store, bool smokeTest = false)
    {
        this.store = store; Title = "BackupCron"; Width = 1160; Height = 850; MinWidth = 900; MinHeight = 620; Ui.Window(this);
        var outer = new Grid(); outer.ColumnDefinitions.Add(new() { Width = new GridLength(220) }); outer.ColumnDefinitions.Add(new()); Content = outer;
        var sidebar = new DockPanel { Margin = new Thickness(12, 20, 12, 16) };
        var shell = new Border { Background = Ui.Brush("Sidebar"), BorderBrush = Ui.Brush("Line"), BorderThickness = new Thickness(0, 0, 1, 0), Child = sidebar }; outer.Children.Add(shell);
        var head = new StackPanel(); DockPanel.SetDock(head, Dock.Top); sidebar.Children.Add(head);
        var brand = Ui.Heading("BackupCron", 21); brand.Margin = new Thickness(10, 0, 0, 22); head.Children.Add(brand);
        overview = Nav("common.overview", "home", () => Navigate("overview"), "nav-overview"); head.Children.Add(overview);
        history = Nav("common.history", "history", () => Navigate("history"), "nav-history"); head.Children.Add(history);
        var jobsLabel = Ui.Text(L10n.Text("common.jobs"), 11, true); jobsLabel.Margin = new Thickness(10, 24, 0, 8); head.Children.Add(jobsLabel);
        search.Tag = L10n.Text("ui.search_jobs"); search.ToolTip = L10n.Text("ui.search_jobs"); AutomationProperties.SetName(search, L10n.Text("ui.search_jobs")); search.Margin = new Thickness(4, 0, 4, 8); search.TextChanged += (_, _) => { sidebarStamp = ""; renderStamp = ""; Refresh(); }; head.Children.Add(search);
        var foot = new StackPanel(); DockPanel.SetDock(foot, Dock.Bottom); sidebar.Children.Add(foot);
        foot.Children.Add(Nav("common.new_job", "plus", NewJob, "nav-new-job")); foot.Children.Add(Nav("windows.settings", "settings", Settings, "nav-settings"));
        var note = Ui.Text(L10n.Text("ux.schedule_open"), 11, true); note.Margin = new Thickness(10, 14, 10, 0); foot.Children.Add(note);
        sidebar.Children.Add(new ScrollViewer { Content = sidebarJobs, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(24) }; Grid.SetColumn(scroll, 1); outer.Children.Add(scroll);
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; int seconds = 0;
        timer.Tick += (_, _) => { if (++seconds >= 30) { seconds = 0; Try(() => store.Tick(DateTimeOffset.Now)); } if (DateTimeOffset.Now >= storageDue) _ = RefreshStorageAsync(); Refresh(); };
        if (!smokeTest)
        {
            feedback = new WindowsFeedback(Restore, Quit);
            store.Finished += run => Dispatcher.InvokeAsync(() => { Try(() => feedback.Notify(run, store.Snapshot().Settings, IsActive)); Refresh(); _ = RefreshStorageAsync(); });
            store.Storage.Warning += alert => Dispatcher.InvokeAsync(() => Try(() => feedback.NotifyStorage(alert, store.Snapshot().Settings))); timer.Start();
            Closing += (_, e) => { if (!quitting) { e.Cancel = true; Hide(); } };
        }
        Closed += (_, _) => { timer.Stop(); feedback?.Dispose(); };
        Refresh(); if (!smokeTest) _ = RefreshStorageAsync();
    }
    private Button Nav(string key, string icon, Action action, string id)
    { var b = Ui.Id(Ui.Button(key, action, icon: icon), id); b.HorizontalAlignment = HorizontalAlignment.Stretch; b.HorizontalContentAlignment = HorizontalAlignment.Left; b.Background = Brushes.Transparent; b.Margin = new Thickness(0, 1, 0, 1); b.Padding = new Thickness(10, 8, 6, 8); return b; }
    private static void NavState(Button button, bool selected)
    {
        button.Background = Ui.Brush(selected ? "Accent" : "Sidebar"); button.Foreground = Ui.Brush(selected ? "AccentText" : "Ink");
        if (button.Content is StackPanel content) { foreach (var text in content.Children.OfType<TextBlock>()) text.Foreground = button.Foreground; foreach (var icon in content.Children.OfType<Viewbox>()) ((System.Windows.Shapes.Path)icon.Child).Stroke = button.Foreground; }
    }
    internal void Navigate(string target, Guid? id = null)
    { page = target; selectedId = id; logVisible = false; renderStamp = ""; sidebarStamp = ""; Refresh(); scroll.ScrollToTop(); }
    public void Restore() { Show(); WindowState = WindowState.Normal; Activate(); _ = RefreshStorageAsync(); }
    private void NewJob() { Try(() => { var editor = new JobEditorWindow(store, null) { Owner = this }; if (editor.ShowDialog() == true) { Navigate("job", editor.SavedJobId); _ = RefreshStorageAsync(); } }); }
    private void EditSelected()
    { if (selectedId is not { } id) return; Try(() => { if (store.Running.ContainsKey(id)) throw new InvalidOperationException(L10n.Text("job.running")); var editor = new JobEditorWindow(store, store.Snapshot().Jobs.First(j => j.Id == id)) { Owner = this }; if (editor.ShowDialog() == true) { Refresh(); _ = RefreshStorageAsync(); } }); }
    private void RunSelected() { if (selectedId is { } id) Try(() => _ = store.Run(id)); }
    private void CancelSelected() { if (selectedId is { } id) store.Cancel(id); }
    private void ToggleSelected() { if (selectedId is { } id) Try(() => { store.SetEnabled(id, !store.Snapshot().Jobs.First(j => j.Id == id).IsEnabled); Refresh(); }); }
    private void DeleteSelected()
    {
        if (selectedId is not { } id) return; var job = store.Snapshot().Jobs.First(j => j.Id == id);
        if (MessageBox.Show(this, L10n.Text("job.delete_message", job.Name), L10n.Text("job.delete_title"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) Try(() => { store.Delete(id); Navigate("overview"); });
    }
    private void Settings() { Try(() => { new SettingsWindow(store) { Owner = this }.ShowDialog(); renderStamp = ""; Refresh(); _ = RefreshStorageAsync(); }); }
    public void Refresh()
    {
        var snapshot = store.Snapshot();
        if (page == "job" && !snapshot.Jobs.Any(j => j.Id == selectedId)) { page = "overview"; selectedId = null; }
        var side = System.Text.Json.JsonSerializer.Serialize(new { snapshot.Jobs, page, selectedId, search.Text }, Json.Options);
        if (side != sidebarStamp)
        {
            sidebarStamp = side; sidebarJobs.Children.Clear(); NavState(overview, page == "overview"); NavState(history, page == "history");
            foreach (var job in snapshot.Jobs.Where(Matches))
            {
                var b = Ui.Id(Ui.Button("ux.open_job", () => Navigate("job", job.Id)), "job-" + job.Id);
                var label = Ui.Text(job.Name); label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis; label.MaxWidth = 145;
                b.Content = Ui.Row(Ui.Icon(Ui.KindIcon(job.Kind), 17), label); b.ToolTip = job.Name; AutomationProperties.SetName(b, job.Name); b.HorizontalAlignment = HorizontalAlignment.Stretch; b.HorizontalContentAlignment = HorizontalAlignment.Left; b.Padding = new Thickness(10, 8, 6, 8); b.Margin = new Thickness(0, 1, 0, 1); b.Background = Ui.Brush(selectedId == job.Id ? "Raised" : "Sidebar"); sidebarJobs.Children.Add(b);
            }
        }
        var stamp = side + System.Text.Json.JsonSerializer.Serialize(new { snapshot.History, snapshot.Settings, store.Error, Storage = store.Storage.Current, storageRefreshing, historyFilter, logVisible, Running = store.Running.Select(r => new { r.Key, r.Value.Progress, Logs = r.Value.Logs.ToArray() }).ToArray() }, Json.Options);
        if (stamp == renderStamp) return; renderStamp = stamp; var offset = scroll.VerticalOffset; body.Children.Clear();
        if (store.Error != null) body.Children.Add(Ui.Card(Ui.Text(store.Error), "DangerFill"));
        if (page == "job") JobPage(snapshot, snapshot.Jobs.First(j => j.Id == selectedId)); else if (page == "history") HistoryPage(snapshot); else OverviewPage(snapshot);
        scroll.UpdateLayout(); scroll.ScrollToVerticalOffset(offset);
    }
    private bool Matches(BackupJob job) => search.Text.Trim().Length == 0 || job.Name.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) || L10n.Kind(job.Kind).Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);
    private void Header(string title, string? subtitle, UIElement[] actions, string? icon = null)
    {
        var text = new StackPanel(); text.Children.Add(icon == null ? Ui.Heading(title, 26) : Ui.Row(Ui.Icon(icon, 32), Ui.Heading(title, 26))); if (subtitle != null) { var sub = Ui.Text(subtitle, 13, true); sub.Margin = new Thickness(0, 7, 0, 0); text.Children.Add(sub); }
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 24) }; grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.Children.Add(text); var row = Ui.Row(actions); row.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(row, 1); grid.Children.Add(row); body.Children.Add(grid);
    }
    private static (string Label, string Icon, string Color, string Fill) Status(BackupRun? run, bool running = false) => running ? (L10n.Text("job.running"), "clock", "Accent", "Selection") : run?.Status switch
    { RunStatus.Success => (L10n.Text("ux.backup_created"), "check", "Success", "SuccessFill"), RunStatus.Failure => (L10n.Text("ux.last_failed"), "error", "Danger", "DangerFill"), RunStatus.Cancelled => (L10n.Text("ux.last_cancelled"), "clock", "Muted", "Panel"), _ => (L10n.Text("ux.not_run"), "clock", "Muted", "Panel") };
    private static StackPanel StatusLine(BackupRun? run, bool running = false)
    { var status = Status(run, running); var label = Ui.Text(status.Label, 12); label.Foreground = Ui.Brush(status.Color); return Ui.Row(Ui.Icon(status.Icon, 14, status.Color), label); }
    private static string Next(BackupJob job, AppSettings settings) => !settings.SchedulerEnabled ? L10n.Text("status.scheduler_paused") : !job.IsEnabled ? L10n.Text("job.paused") : Scheduling.NextRun(job, DateTimeOffset.Now) is { } date ? Ui.Date(date) : L10n.Text("status.no_automatic_runs");
    private void OverviewPage(PersistedState snapshot)
    {
        Header(L10n.Text("dashboard.subtitle"), L10n.Text("ux.overview_hint"), [Ui.Id(Ui.Button("common.new_job", NewJob, true, "plus"), "overview-new-job")]);
        var heading = Ui.Heading(L10n.Text("ux.backups")); heading.Margin = new Thickness(0, 22, 0, 12); body.Children.Add(heading);
        var list = new StackPanel(); foreach (var job in snapshot.Jobs.Where(Matches))
        {
            var last = snapshot.History.FirstOrDefault(r => r.JobId == job.Id); var success = snapshot.History.FirstOrDefault(r => r.JobId == job.Id && r.Status == RunStatus.Success);
            var name = new StackPanel(); name.Children.Add(Ui.Heading(job.Name)); var state = StatusLine(last, store.Running.ContainsKey(job.Id)); state.Margin = new Thickness(0, 6, 0, 0); name.Children.Add(state);
            var row = new DockPanel(); var arrow = Ui.Icon("chevron", 14); DockPanel.SetDock(arrow, Dock.Right); row.Children.Add(arrow); var icon = Ui.Icon(Ui.KindIcon(job.Kind), 29); icon.Margin = new Thickness(0, 0, 12, 0); DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon); row.Children.Add(name);
            var item = new StackPanel(); item.Children.Add(row); var metrics = Ui.Columns(Ui.Metric(L10n.Text("ux.last_success"), Ui.Date(success?.FinishedAt)), Ui.Metric(L10n.Text("job.next_run"), Next(job, snapshot.Settings))); metrics.Margin = new Thickness(0, 14, 0, 0); item.Children.Add(metrics);
            var button = Ui.Id(Ui.Button("ux.open_job", () => Navigate("job", job.Id)), "overview-job-" + job.Id); button.Content = item; button.Background = Ui.Brush("Panel"); button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(16); button.Margin = new Thickness(0); AutomationProperties.SetName(button, job.Name + " · " + Status(last).Label);
            if (list.Children.Count > 0) list.Children.Add(new Border { Background = Ui.Brush("Line"), Height = 1, Margin = new Thickness(16, 0, 16, 0) }); list.Children.Add(button);
        }
        if (list.Children.Count == 0) list.Children.Add(Ui.Text(L10n.Text(snapshot.Jobs.Count == 0 ? "dashboard.create_first_job" : "ui.no_matches"), 13, true)); body.Children.Add(Ui.Card(list, padding: 0));
        var note = Ui.Row(Ui.Icon("clock", 14), Ui.Text(L10n.Text("ux.schedule_open"), 12, true)); note.Margin = new Thickness(0, 12, 0, 20); body.Children.Add(note);
        StorageSection();
        if (snapshot.History.Count > 0) { body.Children.Add(Ui.Heading(L10n.Text("dashboard.recent_runs"))); foreach (var run in snapshot.History.Take(3)) body.Children.Add(RunRow(run)); }
    }
    private void StorageSection()
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) }; var refresh = Ui.Id(Ui.Button("storage.refresh", async () => await RefreshStorageAsync(), icon: "refresh"), "storage-refresh"); refresh.IsEnabled = !storageRefreshing; DockPanel.SetDock(refresh, Dock.Right); header.Children.Add(refresh); header.Children.Add(Ui.Heading(L10n.Text("storage.title"))); body.Children.Add(header);
        var data = store.Storage.Current;
        if (data.Volumes.Count == 0) body.Children.Add(Ui.Card(Ui.Text(L10n.Text("storage.checking"), 12, true)));
        else { var cards = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 }; body.Children.Add(cards); foreach (var volume in data.Volumes)
        {
            var reading = volume.Reading; var color = volume.Level is StorageLevel.Critical ? "Danger" : volume.Level is StorageLevel.Healthy ? "Success" : "Warning";
            var root = Ui.Heading(reading.Root, 13); root.TextWrapping = TextWrapping.NoWrap; root.TextTrimming = TextTrimming.CharacterEllipsis;
            var title = new DockPanel(); var status = Ui.Text(LevelText(volume.Level), 12); status.Foreground = Ui.Brush(color); DockPanel.SetDock(status, Dock.Right); title.Children.Add(status); var icon = Ui.Icon("drive", 18); DockPanel.SetDock(icon, Dock.Left); title.Children.Add(icon); title.Children.Add(root);
            var metrics = Ui.Columns(Ui.Metric(L10n.Text("storage.capacity"), reading.Known ? L10n.Text("storage.used_total", L10n.Size(reading.TotalBytes - reading.AvailableBytes), L10n.Size(reading.TotalBytes)) + (reading.TotalBytes > 0 ? $" ({100 * (1 - reading.AvailableBytes!.Value / (double)reading.TotalBytes.Value):0.#}%)" : "") : "—"), Ui.Metric(L10n.Text("storage.available"), L10n.Size(reading.AvailableBytes)), Ui.Metric(L10n.Text("storage.next_peak"), L10n.Size(volume.NextPeakBytes))); metrics.Margin = new Thickness(0, 10, 0, 0);
            var panel = new StackPanel(); panel.Children.Add(title); panel.Children.Add(metrics);
            var roles = Ui.Text(string.Join(" · ", volume.Roles.Select(role => L10n.Text(role))), 11, true); roles.Margin = new Thickness(0, 9, 0, 0); panel.Children.Add(roles);
            var card = Ui.Card(panel); card.Margin = new Thickness(0, 0, 8, 8); card.ToolTip = string.Join(Environment.NewLine, volume.Paths) + "\n" + (reading.Error ?? L10n.Text("storage.quota_note")) + "\n" + L10n.Text("storage.checked", Ui.Date(volume.LastSuccessfulCheck)); cards.Children.Add(card);
        } }
        body.Children.Add(Ui.Text(storageRefreshing ? L10n.Text("storage.checking") : L10n.Text("storage.checked", Ui.Date(data.CheckedAt == DateTimeOffset.MinValue ? null : data.CheckedAt)) + " · " + L10n.Text("storage.serial_note"), 11, true));
    }
    private void JobPage(PersistedState snapshot, BackupJob job)
    {
        var running = store.Running.TryGetValue(job.Id, out var runningInfo); var run = snapshot.History.FirstOrDefault(r => r.JobId == job.Id); var success = snapshot.History.FirstOrDefault(r => r.JobId == job.Id && r.Status == RunStatus.Success); var status = Status(run, running);
        var edit = Ui.Id(Ui.Button("common.edit", EditSelected, icon: "edit"), "job-edit"); edit.IsEnabled = !running;
        var start = Ui.Id(Ui.Button(running ? "common.cancel" : "ux.create_backup", running ? CancelSelected : RunSelected, !running, running ? "close" : "plus"), running ? "job-cancel" : "job-run");
        Header(job.Name, L10n.Kind(job.Kind), [edit, start], Ui.KindIcon(job.Kind));
        var result = new StackPanel(); var label = Ui.Heading(status.Label, 17); label.Foreground = Ui.Brush(status.Color); result.Children.Add(Ui.Row(Ui.Icon(status.Icon, 30, status.Color), label));
        var message = Ui.Text(running ? runningInfo!.Progress.Message : run == null ? L10n.Text("ux.first_backup_hint") : Ui.Date(run.FinishedAt) + (run.Status == RunStatus.Success ? " · " + L10n.Size(run.OutputSizeBytes) : "\n" + run.Message), 12, true); message.Margin = new Thickness(38, 9, 0, 14); result.Children.Add(message);
        var log = Ui.Id(Ui.Button(logVisible ? "ux.hide_log" : "ux.show_log", () => { logVisible = !logVisible; Refresh(); }, icon: "log"), "job-log");
        var reveal = Ui.Button("common.show_in_finder", () => Try(() => WindowsFiles.Reveal(run!.OutputPath!)), icon: "folder"); reveal.IsEnabled = run?.OutputPath != null;
        result.Children.Add(Ui.Row(log, reveal)); body.Children.Add(Ui.Card(result, status.Fill, 20));
        if (running) body.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 12, 0, 0), Foreground = Ui.Accent });
        if (logVisible) { var logs = Ui.Id(Ui.Input(), "job-log-text"); logs.IsReadOnly = true; logs.AcceptsReturn = true; logs.TextWrapping = TextWrapping.Wrap; logs.FontFamily = new FontFamily("Consolas"); logs.Height = 150; logs.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; logs.Text = running ? string.Join(Environment.NewLine, runningInfo!.Logs) : string.Join(Environment.NewLine, run?.Logs ?? []); logs.Margin = new Thickness(0, 12, 0, 0); body.Children.Add(logs); }
        var dates = Ui.Columns(Ui.Metric(L10n.Text("ux.last_success"), Ui.Date(success?.FinishedAt)), Ui.Metric(L10n.Text("job.next_run"), Next(job, snapshot.Settings))); dates.Margin = new Thickness(0, 22, 0, 0); body.Children.Add(dates); body.Children.Add(Ui.Divider());
        body.Children.Add(Ui.Heading(L10n.Text("storage.title"))); var budget = Ui.Card(Ui.Text(SpaceText(store.Storage.Current.Jobs.FirstOrDefault(j => j.Id == job.Id)), 12)); budget.Margin = new Thickness(0, 12, 0, 12); body.Children.Add(budget); body.Children.Add(Ui.Button("storage.refresh", async () => await RefreshStorageAsync(), icon: "refresh"));
        body.Children.Add(Ui.Divider()); body.Children.Add(Ui.Heading(L10n.Text("dashboard.recent_runs"))); foreach (var r in snapshot.History.Where(r => r.JobId == job.Id).Take(3)) body.Children.Add(RunRow(r));
        var parameters = new StackPanel(); void Param(string key, string value) { var name = Ui.Text(L10n.Text(key), 12, true); name.Width = 160; var val = Ui.Text(value, 12); val.FontFamily = new FontFamily("Consolas"); var row = new Grid { Margin = new Thickness(0, 6, 0, 6) }; row.ColumnDefinitions.Add(new() { Width = new GridLength(160) }); row.ColumnDefinitions.Add(new()); row.Children.Add(name); Grid.SetColumn(val, 1); row.Children.Add(val); parameters.Children.Add(row); }
        Param("common.source", job.HasDatabaseSource && job.Kind != BackupKind.Sqlite ? job.DatabaseName + " · " + job.Host + ":" + job.Port : job.SourcePath); Param("editor.destination", job.DestinationPath); Param("editor.output_name_mask", job.NameMask); Param("editor.retention", job.RetentionCount.ToString());
        Param("editor.create_archive", L10n.Text(job.ArchiveOutput ? "ui.yes" : "ui.no")); Param("common.schedule", L10n.Text("schedule." + job.Schedule.ToString().ToLowerInvariant()));
        if (job.Kind == BackupKind.Project) Param("editor.project_exclusions", job.ProjectExclusions);
        if (job.HasDatabaseSource) Param("database.tables_title", string.Join(", ", job.ExcludedDatabaseTables.Select(t => t.DisplayName)));
        var expander = new Expander { Header = Ui.Heading(L10n.Text("ux.backup_parameters")), Content = parameters, Margin = new Thickness(0, 22, 0, 12) }; body.Children.Add(expander);
        var toggle = Ui.Id(Ui.Button(job.IsEnabled ? "ui.pause" : "ui.resume", ToggleSelected), "job-toggle"); toggle.IsEnabled = !running;
        var delete = Ui.Button("common.delete", DeleteSelected); delete.Foreground = Ui.Brush("Danger"); delete.IsEnabled = !running; body.Children.Add(Ui.Row(toggle, delete));
    }
    private Border RunRow(BackupRun run)
    {
        var name = new StackPanel(); name.Children.Add(Ui.Heading(run.JobName, 13)); var date = Ui.Text(Ui.Date(run.FinishedAt) + " · " + L10n.Size(run.OutputSizeBytes), 11, true); date.Margin = new Thickness(0, 5, 0, 0); name.Children.Add(date);
        var panel = new DockPanel(); var log = Ui.Button("ux.show_log", () => ShowLog(run), icon: "log"); var reveal = Ui.Button("common.show_in_finder", () => Try(() => WindowsFiles.Reveal(run.OutputPath!)), icon: "folder"); reveal.IsEnabled = run.OutputPath != null;
        var actions = Ui.Row(log, reveal); DockPanel.SetDock(actions, Dock.Right); panel.Children.Add(actions); var state = Ui.Icon(Status(run).Icon, 22, Status(run).Color); DockPanel.SetDock(state, Dock.Left); panel.Children.Add(state); panel.Children.Add(name);
        var card = Ui.Card(panel, padding: 12); card.Margin = new Thickness(0, 10, 0, 0); card.ToolTip = run.Message; return card;
    }
    private void ShowLog(BackupRun run)
    { var logs = Ui.Input(string.Join(Environment.NewLine, run.Logs)); logs.IsReadOnly = true; logs.AcceptsReturn = true; logs.TextWrapping = TextWrapping.Wrap; logs.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; logs.FontFamily = new FontFamily("Consolas"); logs.Margin = new Thickness(20); var window = new Window { Title = run.JobName + " · " + L10n.Text("ux.show_log"), Owner = this, Width = 780, Height = 480, Content = logs, WindowStartupLocation = WindowStartupLocation.CenterOwner }; Ui.Window(window); window.Show(); }
    private void HistoryPage(PersistedState snapshot)
    {
        Header(L10n.Text("common.history"), L10n.Text("ui.history_hint"), []);
        var filter = Ui.Id(new ComboBox { ItemsSource = new[] { L10n.Text("history.all"), L10n.Text("history.successful"), L10n.Text("history.failed"), L10n.Text("ux.last_cancelled") }, SelectedIndex = historyFilter, Width = 230, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) }, "history-filter");
        filter.SelectionChanged += (_, _) => { historyFilter = filter.SelectedIndex; Refresh(); }; body.Children.Add(filter);
        var filtered = snapshot.History.Where(r => historyFilter switch { 1 => r.Status == RunStatus.Success, 2 => r.Status == RunStatus.Failure, 3 => r.Status == RunStatus.Cancelled, _ => true }).ToArray();
        if (filtered.Length == 0) body.Children.Add(Ui.Card(Ui.Text(L10n.Text("history.empty_description"), 13, true))); else foreach (var run in filtered) body.Children.Add(RunRow(run));
    }
    public async Task RefreshStorageAsync()
    {
        if (storageRefreshing || quitting) return; storageRefreshing = true; storageDue = DateTimeOffset.Now.AddMinutes(1); Refresh();
        try { await store.Storage.RefreshAsync(store.Snapshot()); } catch (Exception ex) { Try(() => throw ex); } finally { storageRefreshing = false; Refresh(); }
    }
    internal static string LevelText(StorageLevel level) => L10n.Text("storage." + level.ToString().ToLowerInvariant());
    internal static string SpaceText(StorageJobView? view)
    {
        if (view == null) return L10n.Text("storage.checking");
        var text = L10n.Text("storage.job_summary", L10n.Size(view.Reading.AvailableBytes), L10n.Size(view.Estimate?.OutputBytes), L10n.Size(view.Estimate?.PeakBytes), view.AfterOutputBytes is < 0 ? L10n.Text("storage.not_enough") : L10n.Size(view.AfterOutputBytes));
        return text + "\n" + (view.Estimate == null ? L10n.Text("storage.estimate_unknown") : L10n.Text("storage.estimate_checked", Ui.Date(view.Estimate.MeasuredAt))) + " · " + LevelText(view.Level);
    }
    private void Try(Action action) { try { action(); } catch (Exception ex) { Ui.Error(this, ex); } }
    private async void Quit()
    { quitting = true; timer.Stop(); store.CancelAll(); await Task.WhenAll(store.Running.Values.Select(r => r.Task ?? Task.CompletedTask)); Close(); Application.Current.Shutdown(); }
}
