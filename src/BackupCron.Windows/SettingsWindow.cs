using BackupCron.Core;
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
        var startup = Ui.Check("settings.launch_at_login", WindowsStartup.Enabled); general.Children.Add(startup); Note(general, "settings.launch_at_login_note"); general.Children.Add(Ui.Divider()); general.Children.Add(Ui.Heading(L10n.Text("settings.passwords"))); Note(general, "settings.keychain");
        var notify = Tab("settings.notifications"); var notifications = Ui.Check("settings.notifications", settings.NotificationsEnabled); var success = Ui.Check("settings.notify_success", settings.NotifySuccess); var failure = Ui.Check("settings.notify_failure", settings.NotifyFailure); notify.Children.Add(notifications); notify.Children.Add(success); notify.Children.Add(failure); notify.Children.Add(Ui.Divider());
        var sound = Ui.Check("sound.enabled", settings.SoundEnabled); notify.Children.Add(sound); var volume = new Slider { Minimum = 0, Maximum = 1, Value = settings.SoundVolume, TickFrequency = .05, IsSnapToTickEnabled = true, Margin = new Thickness(0, 10, 0, 20) }; Ui.Label(notify, "sound.volume", volume);
        async void Preview(bool successful) { var level = volume.Value; try { await Task.Run(() => WindowsFeedback.Play(successful, level)); } catch (Exception ex) { Ui.Error(this, ex); } }
        notify.Children.Add(Ui.Row(Ui.Button("sound.preview_success", () => Preview(true)), Ui.Button("sound.preview_failure", () => Preview(false))));
        var storage = Tab("storage.title"); storage.Children.Add(Ui.Heading(L10n.Text("storage.title"), 17)); Note(storage, "storage.quota_note"); var storageWarnings = Ui.Id(Ui.Check("storage.notify", settings.StorageWarningsEnabled), "settings-storage-warnings"); storage.Children.Add(storageWarnings);
        var warningGiB = Ui.Id(Ui.Input(settings.StorageWarningGiB.ToString(System.Globalization.CultureInfo.CurrentCulture)), "settings-storage-gb"); var warningPercent = Ui.Id(Ui.Input(settings.StorageWarningPercent.ToString(System.Globalization.CultureInfo.CurrentCulture)), "settings-storage-percent");
        var gbPanel = new StackPanel(); var percentPanel = new StackPanel(); Ui.Label(gbPanel, "storage.threshold_gib", warningGiB); Ui.Label(percentPanel, "storage.threshold_percent", warningPercent); storage.Children.Add(Ui.Columns(gbPanel, percentPanel)); storage.Children.Add(Ui.Card(Ui.Text(L10n.Text("storage.threshold_note"), 12, true)));
        var lang = Tab("windows.language"); var language = new ComboBox { ItemsSource = new[] { "auto" }.Concat(L10n.AvailableLanguages.Order()).ToList(), SelectedItem = settings.Language }; Ui.Label(lang, "windows.language", language); Note(lang, "windows.restart_language");
        var save = Ui.Id(Ui.Button("common.save", () =>
        {
            var previousStartup = WindowsStartup.Enabled;
            try
            {
                settings.SchedulerEnabled = scheduler.IsChecked == true; settings.NotificationsEnabled = notifications.IsChecked == true; settings.NotifySuccess = success.IsChecked == true; settings.NotifyFailure = failure.IsChecked == true; settings.SoundEnabled = sound.IsChecked == true; settings.SoundVolume = volume.Value; settings.Language = language.SelectedItem as string ?? "auto";
                settings.StorageWarningsEnabled = storageWarnings.IsChecked == true; settings.StorageWarningGiB = double.Parse(warningGiB.Text, System.Globalization.CultureInfo.CurrentCulture); settings.StorageWarningPercent = double.Parse(warningPercent.Text, System.Globalization.CultureInfo.CurrentCulture);
                WindowsStartup.Set(startup.IsChecked == true); store.SaveSettings(settings); DialogResult = true;
            }
            catch (Exception ex) { try { WindowsStartup.Set(previousStartup); } catch { } Ui.Error(this, ex); }
        }, true), "settings-save"); save.IsDefault = true; var cancel = Ui.Button("common.cancel", Close); cancel.IsCancel = true;
        var footer = Ui.Row(cancel, save); footer.HorizontalAlignment = HorizontalAlignment.Right; footer.Margin = new Thickness(0, 18, 0, 0); DockPanel.SetDock(footer, Dock.Bottom); outer.Children.Add(footer); outer.Children.Add(Tabs);
    }
}
