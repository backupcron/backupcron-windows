using BackupCron.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace BackupCron.Windows;

internal sealed class TableChoice(TableInfo table, bool included) : INotifyPropertyChanged
{
    public TableInfo Table { get; } = table;
    public string Schema => Table.Schema;
    public string Name => Table.Name;
    public string Type => Table.ObjectType;
    public string Size => L10n.Size(Table.SizeBytes);
    public long? Bytes => Table.SizeBytes;
    private bool parentExcluded;
    public bool RequestedInclude => include;
    public bool ParentExcluded { get => parentExcluded; set { if (parentExcluded == value) return; parentExcluded = value; PropertyChanged?.Invoke(this, new(nameof(Include))); PropertyChanged?.Invoke(this, new(nameof(ParentExcluded))); } }
    private bool include = included;
    public bool Include { get => include && !parentExcluded; set { if (include == value) return; include = value; PropertyChanged?.Invoke(this, new(nameof(Include))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
internal sealed class JobEditorWindow : Window
{
    private readonly BackupStore store;
    private readonly BackupJob original;
    private BackupJob draft;
    private readonly bool newJob;
    private readonly Dictionary<string, List<TableReference>> selections = new();
    private readonly TextBox name, source, destination, host, port, database, user, client, container, sshHost, sshPort, sshUser, sshKey, mask, retention, exclusions, interval, time;
    private readonly PasswordBox password = new() { Padding = new Thickness(7), MinHeight = 32 };
    private readonly ComboBox kind, mode, schedule, weekday;
    private readonly CheckBox ssl, archive, git, enabled;
    private readonly TextBlock catalogSummary = Ui.Text("");
    private readonly TextBlock selectionError = Ui.Text("");
    private readonly DataGrid tableGrid = new() { AutoGenerateColumns = false, CanUserAddRows = false, MinHeight = 200, MaxHeight = 380 };
    private readonly ObservableCollection<TableChoice> choices = [];
    private readonly StackPanel localSource = new(), serverSource = new(), direct = new(), docker = new(), ssh = new(), project = new();
    private readonly TabItem tablesTab;
    private DatabaseCatalog? catalog;
    private string? inspectedIdentity;
    private string? inspectedPassword;
    private bool passwordEdited;
    private bool initializing = true;
    private bool inspecting;
    private bool synchronizingTables;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Button save;

    public JobEditorWindow(BackupStore store, BackupJob? job)
    {
        this.store = store; newJob = job == null; original = job?.Clone() ?? new(); draft = original.Clone(); selections[draft.TargetIdentity()] = draft.ExcludedDatabaseTables.ToList();
        Title = L10n.Text("editor.configure_title", draft.Name); Width = 900; Height = 780; MinWidth = 730; MinHeight = 620; Background = Ui.Background; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var outer = new DockPanel { Margin = new Thickness(18) }; Content = outer;
        save = Ui.Button("common.save", Save, true); var bottom = Ui.Row(Ui.Button("common.cancel", () => { lifetime.Cancel(); Close(); }), save); DockPanel.SetDock(bottom, Dock.Bottom); outer.Children.Add(bottom);
        outer.Children.Add(Ui.Text(L10n.Text("ux.backup_parameters"), 23)); DockPanel.SetDock(outer.Children[^1], Dock.Top);
        var tabs = new TabControl(); outer.Children.Add(tabs);
        var input = PanelTab(tabs, "ux.source_title"); name = Ui.Input(draft.Name); Ui.Label(input, "editor.name", name);
        kind = EnumCombo(Enum.GetValues<BackupKind>(), draft.Kind, L10n.Kind); Ui.Label(input, "editor.data_type", kind);
        source = Ui.Input(draft.SourcePath); Ui.Label(localSource, "editor.file_or_folder", source);
        localSource.Children.Add(Ui.Row(Ui.Button("editor.file_or_folder", () => { var value = WindowsFiles.PickFile(this); if (value != null) source.Text = value; }), Ui.Button("editor.choose_folder", () => { var value = WindowsFiles.PickFolder(this); if (value != null) source.Text = value; }))); input.Children.Add(localSource);
        mode = EnumCombo(Enum.GetValues<ConnectionMode>(), draft.ConnectionMode, x => L10n.Text(x == ConnectionMode.Direct ? "connection.direct" : x == ConnectionMode.Ssh ? "connection.ssh" : "windows.docker")); Ui.Label(serverSource, "common.connection", mode);
        host = Ui.Input(draft.Host); port = Ui.Input(draft.Port.ToString()); database = Ui.Input(draft.DatabaseName); user = Ui.Input(draft.Username); client = Ui.Input(draft.ClientExecutablePath);
        Ui.Label(serverSource, "editor.db_host", host); Ui.Label(serverSource, "editor.db_port", port); Ui.Label(serverSource, "editor.database_name", database); Ui.Label(serverSource, "editor.db_user", user); Ui.Label(serverSource, "editor.password", password); serverSource.Children.Add(Ui.Text(L10n.Text("editor.keychain_hint"), 12));
        ssl = Ui.Check("editor.require_secure_connection", draft.UseSsl); serverSource.Children.Add(ssl); Ui.Label(direct, "job.client", client); direct.Children.Add(Ui.Button("picker.choose_utility", () => { var value = WindowsFiles.PickFile(this, "Executable|*.exe|All files|*.*"); if (value != null) client.Text = value; })); serverSource.Children.Add(direct);
        container = Ui.Input(draft.DockerContainer); Ui.Label(docker, "editor.container", container); serverSource.Children.Add(docker);
        sshHost = Ui.Input(draft.SshHost); sshPort = Ui.Input(draft.SshPort.ToString()); sshUser = Ui.Input(draft.SshUsername); sshKey = Ui.Input(draft.SshIdentityPath);
        Ui.Label(ssh, "editor.ssh_server", sshHost); Ui.Label(ssh, "editor.ssh_port", sshPort); Ui.Label(ssh, "editor.ssh_user", sshUser); Ui.Label(ssh, "editor.ssh_key", sshKey); ssh.Children.Add(Ui.Button("picker.choose_key", () => { var value = WindowsFiles.PickFile(this); if (value != null) sshKey.Text = value; })); ssh.Children.Add(Ui.Text(L10n.Text("editor.ssh_auth_hint"), 12)); serverSource.Children.Add(ssh);
        serverSource.Children.Add(Ui.Button("editor.test_connection", async () => await TestConnection())); input.Children.Add(serverSource);
        exclusions = Ui.Input(draft.ProjectExclusions); Ui.Label(project, "editor.project_exclusions", exclusions); git = Ui.Check("editor.include_git", draft.IncludesGitMetadata); project.Children.Add(git); input.Children.Add(project);
        var output = PanelTab(tabs, "ux.destination_title"); destination = Ui.Input(draft.DestinationPath); Ui.Label(output, "editor.destination", destination); output.Children.Add(Ui.Button("editor.choose_folder", () => { var value = WindowsFiles.PickFolder(this); if (value != null) destination.Text = value; })); archive = Ui.Check("editor.create_archive", draft.ArchiveOutput); output.Children.Add(archive); output.Children.Add(Ui.Text(L10n.Text("editor.create_archive_hint"), 12)); mask = Ui.Input(draft.NameMask); Ui.Label(output, "editor.output_name_mask", mask); output.Children.Add(Ui.Text("{job} · {type} · {date} · {time}", 12)); retention = Ui.Input(draft.RetentionCount.ToString()); Ui.Label(output, "editor.retention", retention); output.Children.Add(Ui.Text(L10n.Text("editor.retention_hint"), 12));
        var timing = PanelTab(tabs, "ux.schedule_title"); schedule = EnumCombo(Enum.GetValues<ScheduleKind>(), draft.Schedule, x => L10n.Text("schedule." + x.ToString().ToLowerInvariant())); Ui.Label(timing, "editor.repeat", schedule); interval = Ui.Input(draft.IntervalMinutes.ToString()); Ui.Label(timing, "schedule.interval", interval); time = Ui.Input(draft.ScheduledTime); Ui.Label(timing, "editor.time", time); weekday = new ComboBox { ItemsSource = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.DayNames, SelectedIndex = Math.Clamp(draft.Weekday - 1, 0, 6), Padding = new Thickness(7) }; Ui.Label(timing, "editor.weekday", weekday); enabled = Ui.Check("editor.job_active", draft.IsEnabled); timing.Children.Add(enabled); timing.Children.Add(Ui.Text(L10n.Text("ux.schedule_open"), 12));
        var tablePanel = new StackPanel { Margin = new Thickness(12) }; tablesTab = new TabItem { Header = L10n.Text("database.tables_title"), Content = new ScrollViewer { Content = tablePanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }; tabs.Items.Add(tablesTab);
        tablePanel.Children.Add(Ui.Text(L10n.Text("database.exclusion_note"), 12)); tablePanel.Children.Add(Ui.Button("database.load", async () => await Inspect())); tablePanel.Children.Add(catalogSummary);
        var search = Ui.Input(); search.TextChanged += (_, _) => tableGrid.ItemsSource = choices.Where(c => (c.Schema + "." + c.Name).Contains(search.Text, StringComparison.CurrentCultureIgnoreCase)).ToList(); Ui.Label(tablePanel, "database.search", search);
        tableGrid.Columns.Add(new DataGridCheckBoxColumn { Header = L10n.Text("windows.include_table"), Binding = new Binding(nameof(TableChoice.Include)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } });
        foreach (var c in new[] { ("database.schema", "Schema"), ("database.table", "Name"), ("editor.data_type", "Type"), ("database.size", "Size") }) tableGrid.Columns.Add(new DataGridTextColumn { Header = L10n.Text(c.Item1), Binding = new Binding(c.Item2), SortMemberPath = c.Item2 == "Size" ? "Bytes" : c.Item2, IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        tableGrid.ItemsSource = choices; tablePanel.Children.Add(tableGrid); tablePanel.Children.Add(Ui.Row(Ui.Button("database.include_all", () => SetAll(true)), Ui.Button("windows.exclude_all", () => SetAll(false)))); tablePanel.Children.Add(selectionError);
        foreach (var field in new[] { source, host, port, database, user, client, container, sshHost, sshPort, sshUser, sshKey }) field.TextChanged += (_, _) => ContextChanged();
        kind.SelectionChanged += (_, _) => { if (initializing) return; if (newJob) port.Text = Selected<BackupKind>(kind) == BackupKind.Mysql ? "3306" : "5432"; ContextChanged(); UpdateVisibility(); }; mode.SelectionChanged += (_, _) => { ContextChanged(); UpdateVisibility(); }; ssl.Checked += (_, _) => ContextChanged(); ssl.Unchecked += (_, _) => ContextChanged(); password.PasswordChanged += (_, _) => { passwordEdited = true; UpdateSummary(); };
        git.Checked += (_, _) => SyncGit(true); git.Unchecked += (_, _) => SyncGit(false); exclusions.TextChanged += (_, _) => { if (initializing) return; var check = draft.Clone(); check.ProjectExclusions = exclusions.Text; git.IsChecked = check.IncludesGitMetadata; };
        destination.TextChanged += (_, _) => UpdateSummary(); archive.Checked += (_, _) => UpdateSummary(); archive.Unchecked += (_, _) => UpdateSummary();
        initializing = false; UpdateVisibility(); UpdateSummary(); Closed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
    }
    private static StackPanel PanelTab(TabControl tabs, string key)
    { var panel = new StackPanel { Margin = new Thickness(12) }; tabs.Items.Add(new TabItem { Header = L10n.Text(key), Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }); return panel; }
    private sealed record Option<T>(T Value, string Label) { public override string ToString() => Label; }
    private static ComboBox EnumCombo<T>(IEnumerable<T> values, T selected, Func<T, string> label)
    { var options = values.Select(v => new Option<T>(v, label(v))).ToList(); return new ComboBox { ItemsSource = options, SelectedItem = options.First(o => EqualityComparer<T>.Default.Equals(o.Value, selected)), Padding = new Thickness(7) }; }
    private static T Selected<T>(ComboBox combo) => ((Option<T>)combo.SelectedItem).Value;
    private void SyncGit(bool include) { if (initializing) return; var check = draft.Clone(); check.ProjectExclusions = exclusions.Text; check.IncludesGitMetadata = include; exclusions.Text = check.ProjectExclusions; }
    private BackupJob Gather()
    {
        var job = draft.Clone(); job.Name = name.Text.Trim(); job.Kind = Selected<BackupKind>(kind); job.SourcePath = source.Text.Trim(); job.DestinationPath = destination.Text.Trim(); job.Host = host.Text.Trim(); job.DatabaseName = database.Text; job.Username = user.Text; job.ClientExecutablePath = client.Text.Trim(); job.ConnectionMode = Selected<ConnectionMode>(mode); job.DockerContainer = container.Text.Trim(); job.SshHost = sshHost.Text.Trim(); job.SshUsername = sshUser.Text.Trim(); job.SshIdentityPath = sshKey.Text.Trim(); job.UseSsl = ssl.IsChecked == true;
        job.Port = int.Parse(port.Text); job.SshPort = int.Parse(sshPort.Text); job.ArchiveOutput = archive.IsChecked == true; job.NameMask = mask.Text; job.RetentionCount = int.Parse(retention.Text); job.ProjectExclusions = exclusions.Text; job.Schedule = Selected<ScheduleKind>(schedule); job.IntervalMinutes = int.Parse(interval.Text); job.ScheduledTime = time.Text; job.Weekday = weekday.SelectedIndex + 1; job.IsEnabled = enabled.IsChecked == true;
        return job;
    }
    private string CurrentPassword() => passwordEdited ? password.Password : store.Password(draft.Id);
    private void ContextChanged()
    {
        if (initializing) return;
        try
        {
            var next = Gather(); if (next.TargetIdentity() != draft.TargetIdentity()) { selections[draft.TargetIdentity()] = draft.ExcludedDatabaseTables.ToList(); next.ExcludedDatabaseTables = selections.GetValueOrDefault(next.TargetIdentity())?.ToList() ?? []; }
            if (next.ConnectionIdentity() != draft.ConnectionIdentity()) { catalog = null; inspectedIdentity = null; choices.Clear(); tableGrid.ItemsSource = choices; }
            draft = next; UpdateSummary();
        }
        catch (FormatException) { }
    }
    private void UpdateVisibility()
    {
        var type = Selected<BackupKind>(kind); var server = type is BackupKind.Postgres or BackupKind.Mysql; localSource.Visibility = server ? Visibility.Collapsed : Visibility.Visible; serverSource.Visibility = server ? Visibility.Visible : Visibility.Collapsed; project.Visibility = type == BackupKind.Project ? Visibility.Visible : Visibility.Collapsed; tablesTab.Visibility = type is BackupKind.Files or BackupKind.Project ? Visibility.Collapsed : Visibility.Visible;
        var connection = Selected<ConnectionMode>(mode); direct.Visibility = connection == ConnectionMode.Direct ? Visibility.Visible : Visibility.Collapsed; docker.Visibility = connection == ConnectionMode.Docker ? Visibility.Visible : Visibility.Collapsed; ssh.Visibility = connection == ConnectionMode.Ssh ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task Inspect()
    {
        if (inspecting) return; inspecting = true; save.IsEnabled = false;
        try
        {
            var job = Gather(); var value = CurrentPassword(); catalogSummary.Text = L10n.Text("database.loading"); var result = await new BackupEngine().InspectDatabaseAsync(job, value, lifetime.Token);
            if (Gather().ConnectionIdentity() != job.ConnectionIdentity() || CurrentPassword() != value) return;
            catalog = result; inspectedIdentity = job.ConnectionIdentity(); inspectedPassword = value; choices.Clear(); var excluded = job.ExcludedDatabaseTables.ToHashSet();
            foreach (var table in result.Tables.OrderByDescending(t => t.SizeBytes).ThenBy(t => t.Schema).ThenBy(t => t.Name)) { var choice = new TableChoice(table, !excluded.Contains(table.Id)); choice.PropertyChanged += (_, _) => { if (synchronizingTables) return; draft.ExcludedDatabaseTables = choices.Where(c => !c.RequestedInclude).Select(c => c.Table.Id).ToList(); UpdateSummary(); }; choices.Add(choice); }
            tableGrid.ItemsSource = choices; draft = job; UpdateSummary();
        }
        catch (OperationCanceledException) { } catch (Exception ex) { catalogSummary.Text = ex.Message; }
        finally { inspecting = false; save.IsEnabled = true; }
    }
    private async Task TestConnection()
    { try { var result = await new BackupEngine().TestConnectionAsync(Gather(), CurrentPassword(), lifetime.Token); MessageBox.Show(this, result, "BackupCron"); } catch (OperationCanceledException) { } catch (Exception ex) { Ui.Error(this, ex); } }
    private void SetAll(bool include) { foreach (var choice in choices) choice.Include = include; UpdateSummary(); }
    private void UpdateSummary()
    {
        if (initializing) return;
        try { draft = Gather(); } catch (FormatException) { return; }
        var error = catalog?.SelectionError(draft.ExcludedDatabaseTables); selectionError.Text = error ?? (catalog == null && draft.ExcludedDatabaseTables.Count > 0 ? L10n.Text("database.offline_edit") : "");
        if (catalog != null)
        {
            var peak = BackupEngine.EstimateSpace(draft, catalog);
            synchronizingTables = true;
            try { var effective = catalog.EffectiveExclusions(draft.ExcludedDatabaseTables); foreach (var choice in choices) choice.ParentExcluded = choice.Table.ParentTables.Any(effective.Contains); }
            finally { synchronizingTables = false; }
            catalogSummary.Text = $"{L10n.Text("database.total_size")}: {L10n.Size(catalog.SizeBytes)}  ·  {L10n.Text("database.selected_size")}: {L10n.Size(catalog.SelectedSize(draft.ExcludedDatabaseTables))}";
            if (!string.IsNullOrWhiteSpace(destination.Text)) catalogSummary.Text += "\n" + L10n.Text("database.space_summary", L10n.Text("database.space_destination"), L10n.Size(peak), L10n.Size(BackupPaths.AvailableBytes(destination.Text)));
        }
    }
    private void Save()
    {
        try
        {
            var job = Gather(); BackupEngine.Validate(job);
            if (job.HasDatabaseSource && job.ExcludedDatabaseTables.Count > 0)
            {
                var unchanged = !newJob && !passwordEdited && job.ConnectionIdentity() == original.ConnectionIdentity() && job.ExcludedDatabaseTables.ToHashSet().SetEquals(original.ExcludedDatabaseTables);
                if (!unchanged && (catalog == null || inspectedIdentity != job.ConnectionIdentity() || inspectedPassword != CurrentPassword())) throw new InvalidOperationException(L10n.Text("database.load_hint"));
                if (catalog?.SelectionError(job.ExcludedDatabaseTables) is { } error) throw new InvalidOperationException(error);
            }
            store.Save(job, passwordEdited ? password.Password : null); DialogResult = true;
        }
        catch (Exception ex) { Ui.Error(this, ex); }
    }
}
