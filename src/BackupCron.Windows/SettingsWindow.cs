using BackupCron.Core;
using System.Windows;
using System.Windows.Controls;

namespace BackupCron.Windows;

internal sealed class SettingsWindow : Window
{
    public SettingsWindow(BackupStore store)
    {
        Title = "BackupCron · " + L10n.Text("windows.settings"); Width = 570; Height = 670; Background = Ui.Background; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var settings = store.Snapshot().Settings; var panel = new StackPanel { Margin = new Thickness(20) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(Ui.Text(L10n.Text("windows.settings"), 24));
        var scheduler = Ui.Check("settings.run_automatically", settings.SchedulerEnabled); panel.Children.Add(scheduler); panel.Children.Add(Ui.Text(L10n.Text("settings.schedule_note"), 12));
        var startup = Ui.Check("settings.launch_at_login", WindowsStartup.Enabled); panel.Children.Add(startup); panel.Children.Add(Ui.Text(L10n.Text("settings.launch_at_login_note"), 12));
        var notifications = Ui.Check("settings.notifications", settings.NotificationsEnabled); var success = Ui.Check("settings.notify_success", settings.NotifySuccess); var failure = Ui.Check("settings.notify_failure", settings.NotifyFailure); panel.Children.Add(notifications); panel.Children.Add(success); panel.Children.Add(failure);
        panel.Children.Add(Ui.Text(L10n.Text("storage.title"), 18));
        var storageWarnings = Ui.Check("storage.notify", settings.StorageWarningsEnabled); panel.Children.Add(storageWarnings);
        var warningGiB = Ui.Input(settings.StorageWarningGiB.ToString(System.Globalization.CultureInfo.CurrentCulture)); Ui.Label(panel, "storage.threshold_gib", warningGiB);
        var warningPercent = Ui.Input(settings.StorageWarningPercent.ToString(System.Globalization.CultureInfo.CurrentCulture)); Ui.Label(panel, "storage.threshold_percent", warningPercent); panel.Children.Add(Ui.Text(L10n.Text("storage.threshold_note"), 12));
        var sound = Ui.Check("sound.enabled", settings.SoundEnabled); panel.Children.Add(sound); var volume = new Slider { Minimum = 0, Maximum = 1, Value = settings.SoundVolume, TickFrequency = .05, IsSnapToTickEnabled = true, Margin = new Thickness(10) }; Ui.Label(panel, "sound.volume", volume);
        async void Preview(bool successful) { var level = volume.Value; try { await Task.Run(() => WindowsFeedback.Play(successful, level)); } catch (Exception ex) { Ui.Error(this, ex); } }
        panel.Children.Add(Ui.Row(Ui.Button("sound.preview_success", () => Preview(true)), Ui.Button("sound.preview_failure", () => Preview(false))));
        var language = new ComboBox { ItemsSource = new[] { "auto" }.Concat(L10n.AvailableLanguages.Order()).ToList(), SelectedItem = settings.Language, Padding = new Thickness(7) }; Ui.Label(panel, "windows.language", language); panel.Children.Add(Ui.Text(L10n.Text("windows.restart_language"), 12));
        panel.Children.Add(Ui.Text(L10n.Text("settings.keychain"), 12));
        panel.Children.Add(Ui.Row(Ui.Button("common.cancel", Close), Ui.Button("common.save", () =>
        {
            var previousStartup = WindowsStartup.Enabled;
            try
            {
                settings.SchedulerEnabled = scheduler.IsChecked == true; settings.NotificationsEnabled = notifications.IsChecked == true; settings.NotifySuccess = success.IsChecked == true; settings.NotifyFailure = failure.IsChecked == true; settings.SoundEnabled = sound.IsChecked == true; settings.SoundVolume = volume.Value; settings.Language = language.SelectedItem as string ?? "auto";
                settings.StorageWarningsEnabled = storageWarnings.IsChecked == true; settings.StorageWarningGiB = double.Parse(warningGiB.Text, System.Globalization.CultureInfo.CurrentCulture); settings.StorageWarningPercent = double.Parse(warningPercent.Text, System.Globalization.CultureInfo.CurrentCulture);
                WindowsStartup.Set(startup.IsChecked == true); store.SaveSettings(settings); DialogResult = true;
            }
            catch (Exception ex) { try { WindowsStartup.Set(previousStartup); } catch { } Ui.Error(this, ex); }
        }, true)));
    }
}
