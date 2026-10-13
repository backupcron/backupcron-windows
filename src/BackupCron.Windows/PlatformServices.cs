using BackupCron.Core;
using Microsoft.Win32;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Forms = System.Windows.Forms;

namespace BackupCron.Windows;

internal sealed class WindowsSecretStore(string directory) : ISecretStore
{
    private static readonly byte[] entropy = Encoding.UTF8.GetBytes("BackupCron.Windows.Password.v1");
    private string PathFor(Guid id) => Path.Combine(directory, id + ".bin");
    public string Get(Guid id) => File.Exists(PathFor(id)) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(PathFor(id)), entropy, DataProtectionScope.CurrentUser)) : "";
    public void Set(Guid id, string value)
    {
        Directory.CreateDirectory(directory); var path = PathFor(id); var temporary = path + "." + Guid.NewGuid() + ".tmp";
        try { File.WriteAllBytes(temporary, ProtectedData.Protect(Encoding.UTF8.GetBytes(value), entropy, DataProtectionScope.CurrentUser)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Remove(Guid id) { if (File.Exists(PathFor(id))) File.Delete(PathFor(id)); }
}
internal static class WindowsStartup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool Enabled { get { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue("BackupCron") is string; } }
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) key.SetValue("BackupCron", "\"" + Environment.ProcessPath + "\" --minimized"); else key.DeleteValue("BackupCron", false);
    }
}
internal sealed class WindowsFeedback : IDisposable
{
    private readonly Forms.NotifyIcon tray;
    public WindowsFeedback(Action restore, Action quit)
    {
        var icon = Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");
        tray = new Forms.NotifyIcon { Text = "BackupCron", Icon = File.Exists(icon) ? new System.Drawing.Icon(icon) : System.Drawing.SystemIcons.Application, Visible = true };
        tray.DoubleClick += (_, _) => restore(); var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("BackupCron", null, (_, _) => restore()); menu.Items.Add(L10n.Text("windows.quit"), null, (_, _) => quit()); tray.ContextMenuStrip = menu;
    }
    public void Notify(BackupRun run, AppSettings settings, bool foreground)
    {
        if (run.Status == RunStatus.Cancelled) return;
        var shouldNotify = settings.NotificationsEnabled && (run.Status == RunStatus.Success ? settings.NotifySuccess : settings.NotifyFailure);
        if (shouldNotify && !foreground) tray.ShowBalloonTip(6000, run.JobName, run.Message[..Math.Min(220, run.Message.Length)], run.Status == RunStatus.Success ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Error);
        if (settings.SoundEnabled && (foreground || shouldNotify)) Play(run.Status == RunStatus.Success, settings.SoundVolume);
    }
    public void NotifyStorage(StorageAlert alert, AppSettings settings)
    {
        if (!settings.StorageWarningsEnabled) return;
        tray.ShowBalloonTip(10000, L10n.Text("storage.title"), alert.Message[..Math.Min(240, alert.Message.Length)], alert.Level == StorageLevel.Critical ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Warning);
    }
    public static void Play(bool success, double volume)
    {
        volume = double.IsFinite(volume) ? Math.Clamp(volume, 0, 1) : .35;
        var path = Path.Combine(Path.GetTempPath(), "BackupCron-tone-" + Guid.NewGuid() + ".wav");
        using (var output = new BinaryWriter(File.Create(path)))
        {
            const int rate = 22050; const int samples = rate / 2; const int size = samples * 2;
            output.Write(Encoding.ASCII.GetBytes("RIFF")); output.Write(36 + size); output.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); output.Write(16); output.Write((short)1); output.Write((short)1); output.Write(rate); output.Write(rate * 2); output.Write((short)2); output.Write((short)16); output.Write(Encoding.ASCII.GetBytes("data")); output.Write(size);
            for (int i = 0; i < samples; i++) { var t = i / (double)rate; var envelope = Math.Min(t / .015, 1) * Math.Min((.5 - t) / .06, 1); var frequency = success ? (t < .23 ? 660 : 880) : (t < .23 ? 440 : 330); output.Write((short)(Math.Sin(2 * Math.PI * frequency * t) * envelope * volume * 16000)); }
        }
        try { using var player = new System.Media.SoundPlayer(path); player.PlaySync(); } finally { File.Delete(path); }
    }
    public void Dispose() { tray.Visible = false; tray.Icon?.Dispose(); tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
}
internal static class WindowsFiles
{
    public static void Reveal(string path)
    {
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false }; start.ArgumentList.Add("/select,"); start.ArgumentList.Add(path); Process.Start(start);
    }
    public static string? PickFile(System.Windows.Window owner, string filter = "All files|*.*")
    { var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true }; return dialog.ShowDialog(owner) == true ? dialog.FileName : null; }
    public static string? PickFolder(System.Windows.Window owner)
    { var dialog = new OpenFolderDialog { Title = L10n.Text("picker.choose_folder") }; return dialog.ShowDialog(owner) == true ? dialog.FolderName : null; }
}
