using BackupCron.Core;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

namespace BackupCron.Windows;

internal sealed class SettingsWindow : Window
{
    internal TabControl Tabs { get; } = Ui.Id(new TabControl(), "settings-tabs");
    public SettingsWindow(BackupStore store)
    {
        Title = "BackupCron · " + L10n.Text("windows.settings"); Width = 700; Height = 670; MinWidth = 620; MinHeight = 520; Ui.Window(this); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var settings = store.Snapshot().Settings; var outer = new DockPanel { Margin = new Thickness(24) }; Content = outer;
        var heading = Ui.Heading(L10n.Text("windows.settings"), 24); heading.Margin = new Thickness(0, 0, 0, 18); DockPanel.SetDock(heading, Dock.Top); outer.Children.Add(heading);
        StackPanel Tab(string key) { var panel = new StackPanel { Margin = new Thickness(0, 8, 12, 0) }; Tabs.Items.Add(new TabItem { Header = L10n.Text(key), Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }); return panel; }
        void Note(Panel panel, string key) { var text = Ui.Text(L10n.Text(key), 12, true); text.Margin = new Thickness(0, 0, 0, 20); panel.Children.Add(text); }
        var general = Tab("ui.general"); var scheduler = Ui.Id(Ui.Check("settings.run_automatically", settings.SchedulerEnabled), "settings-scheduler"); general.Children.Add(scheduler); Note(general, "settings.schedule_note");
        general.Children.Add(Ui.Divider()); general.Children.Add(Ui.Heading(L10n.Text("settings_backup.startup")));
        var startup = Ui.Id(Ui.Check("settings.launch_at_login", WindowsStartup.Enabled), "settings-startup"); general.Children.Add(startup); general.Children.Add(Ui.Divider()); general.Children.Add(Ui.Heading(L10n.Text("settings.passwords"))); Note(general, "settings.keychain");
        var notify = Tab("settings.notifications"); var notifications = Ui.Check("settings.notifications", settings.NotificationsEnabled); var success = Ui.Check("settings.notify_success", settings.NotifySuccess); var failure = Ui.Check("settings.notify_failure", settings.NotifyFailure); notify.Children.Add(notifications); notify.Children.Add(success); notify.Children.Add(failure); notify.Children.Add(Ui.Divider());
        var sound = Ui.Check("sound.enabled", settings.SoundEnabled); notify.Children.Add(sound); var volume = new Slider { Minimum = 0, Maximum = 1, Value = settings.SoundVolume, TickFrequency = .05, IsSnapToTickEnabled = true, Margin = new Thickness(0, 10, 0, 20) }; Ui.Label(notify, "sound.volume", volume);
        async void Preview(bool successful) { var level = volume.Value; try { await Task.Run(() => WindowsFeedback.Play(successful, level)); } catch (Exception ex) { Ui.Error(this, ex); } }
        notify.Children.Add(Ui.Row(Ui.Button("sound.preview_success", () => Preview(true)), Ui.Button("sound.preview_failure", () => Preview(false))));
        var storage = Tab("storage.title"); storage.Children.Add(Ui.Heading(L10n.Text("storage.title"), 17)); Note(storage, "storage.quota_note"); var storageWarnings = Ui.Id(Ui.Check("storage.notify", settings.StorageWarningsEnabled), "settings-storage-warnings"); storage.Children.Add(storageWarnings);
        var warningGiB = Ui.Id(Ui.Input(settings.StorageWarningGiB.ToString(System.Globalization.CultureInfo.CurrentCulture)), "settings-storage-gb"); var warningPercent = Ui.Id(Ui.Input(settings.StorageWarningPercent.ToString(System.Globalization.CultureInfo.CurrentCulture)), "settings-storage-percent");
        var gbPanel = new StackPanel(); var percentPanel = new StackPanel(); Ui.Label(gbPanel, "storage.threshold_gib", warningGiB); Ui.Label(percentPanel, "storage.threshold_percent", warningPercent); storage.Children.Add(Ui.Columns(gbPanel, percentPanel)); storage.Children.Add(Ui.Card(Ui.Text(L10n.Text("storage.threshold_note"), 12, true)));
        var lang = Tab("windows.language"); var language = new ComboBox { ItemsSource = new[] { "auto" }.Concat(L10n.AvailableLanguages.Order()).ToList(), SelectedItem = settings.Language }; Ui.Label(lang, "windows.language", language); Note(lang, "windows.restart_language");
        var backup = Tab("settings_backup.title");
        var automatic = Ui.Id(Ui.Check("settings_backup.automatic", settings.SettingsBackupEnabled), "settings-backup-enabled"); backup.Children.Add(automatic);
        var backupDirectory = Ui.Id(Ui.Input(settings.SettingsBackupDirectory), "settings-backup-folder"); Ui.Label(backup, "editor.destination", backupDirectory);
        var backupStatus = Ui.Text(store.SettingsBackups.Error ?? "", 12, true);
        var choose = Ui.Button("settings_backup.choose_folder", () =>
        {
            var picker = new OpenFolderDialog { Multiselect = false, Title = L10n.Text("settings_backup.choose_folder") };
            if (Directory.Exists(backupDirectory.Text)) picker.InitialDirectory = backupDirectory.Text;
            if (picker.ShowDialog(this) == true) backupDirectory.Text = picker.FolderName;
        });
        AppSettings Gather()
        {
            var draft = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(settings, Json.Options), Json.Options)!;
            draft.SchedulerEnabled = scheduler.IsChecked == true; draft.NotificationsEnabled = notifications.IsChecked == true; draft.NotifySuccess = success.IsChecked == true; draft.NotifyFailure = failure.IsChecked == true; draft.SoundEnabled = sound.IsChecked == true; draft.SoundVolume = volume.Value; draft.Language = language.SelectedItem as string ?? "auto";
            draft.StorageWarningsEnabled = storageWarnings.IsChecked == true; draft.StorageWarningGiB = double.Parse(warningGiB.Text, System.Globalization.CultureInfo.CurrentCulture); draft.StorageWarningPercent = double.Parse(warningPercent.Text, System.Globalization.CultureInfo.CurrentCulture);
            draft.SettingsBackupEnabled = automatic.IsChecked == true; draft.SettingsBackupDirectory = backupDirectory.Text.Trim();
            return draft;
        }
        async void BackupNow()
        {
            try
            {
                outer.IsEnabled = false;
                await store.BackupSettingsAsync(backupDirectory.Text.Trim(), Gather());
                backupStatus.Text = L10n.Text("settings_backup.saved", DateTime.Now.ToString("g"));
            }
            catch (Exception ex) { Ui.Error(this, ex); }
            finally { outer.IsEnabled = true; }
        }
        async void Restore()
        {
            var picker = new OpenFileDialog { Filter = "BackupCron (*.json)|*.json", Multiselect = false, CheckFileExists = true };
            if (Directory.Exists(backupDirectory.Text)) picker.InitialDirectory = backupDirectory.Text;
            if (picker.ShowDialog(this) != true) return;
            try
            {
                outer.IsEnabled = false;
                var archive = await Task.Run(() => SettingsArchive.Read(picker.FileName));
                if (MessageBox.Show(this, L10n.Text("settings_backup.confirm_restore"), L10n.Text("settings_backup.restore"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                store.RestoreSettings(archive);
                MessageBox.Show(this, L10n.Text("settings_backup.restored"), "BackupCron");
                DialogResult = true;
            }
            catch (Exception ex) { Ui.Error(this, ex); }
            finally { outer.IsEnabled = true; }
        }
        backup.Children.Add(Ui.Row(choose, Ui.Id(Ui.Button("settings_backup.backup_now", BackupNow), "settings-backup-now")));
        backup.Children.Add(Ui.Id(Ui.Button("settings_backup.restore", Restore), "settings-restore"));
        backup.Children.Add(backupStatus);
        var save = Ui.Id(Ui.Button("common.save", () =>
        {
            var previousStartup = WindowsStartup.Enabled;
            try
            {
                var draft = Gather();
                WindowsStartup.Set(startup.IsChecked == true); store.SaveSettings(draft); DialogResult = true;
            }
            catch (Exception ex) { try { WindowsStartup.Set(previousStartup); } catch { } Ui.Error(this, ex); }
        }, true), "settings-save"); save.IsDefault = true; var cancel = Ui.Button("common.cancel", Close); cancel.IsCancel = true;
        var footer = Ui.Row(cancel, save); footer.HorizontalAlignment = HorizontalAlignment.Right; footer.Margin = new Thickness(0, 18, 0, 0); DockPanel.SetDock(footer, Dock.Bottom); outer.Children.Add(footer); outer.Children.Add(Tabs);
    }
}
