using BackupCron.Core;
using Microsoft.Data.Sqlite;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BackupCron.Windows;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var smoke = args.Contains("--smoke-test");
        using var mutex = new Mutex(true, smoke ? "BackupCron-smoke-" + Guid.NewGuid() : @"Local\BackupCron.Windows", out var first);
        if (!first) { MessageBox.Show("BackupCron is already running. Open it from the notification area.", "BackupCron"); return 0; }
        var app = new Application(); RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var directory = smoke ? Path.Combine(Path.GetTempPath(), "BackupCron-Windows-smoke-" + Guid.NewGuid()) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BackupCron");
        var store = new BackupStore(directory, new WindowsSecretStore(Path.Combine(directory, "secrets"))); var exit = 0;
        var window = new MainWindow(store, smoke); app.MainWindow = window;
        app.DispatcherUnhandledException += (_, e) => { Ui.Error(window, e.Exception); e.Handled = true; };
        if (smoke)
        {
            app.Startup += async (_, _) =>
            {
                try
                {
                    var output = args.SkipWhile(a => a != "--smoke-test").Skip(1).FirstOrDefault() ?? Path.Combine(directory, "proof"); Directory.CreateDirectory(output);
                    var source = Path.Combine(directory, "Demo files"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "report.txt"), "BackupCron Windows verification fixture");
                    var file = new BackupJob { Name = "Project files", SourcePath = source, DestinationPath = Path.Combine(directory, "Backups"), RetentionCount = 5, Schedule = ScheduleKind.Daily };
                    store.Save(file, "smoke-credential"); if (store.Password(file.Id) != "smoke-credential" || File.ReadAllText(Path.Combine(directory, "state.json")).Contains("smoke-credential")) throw new Exception("Windows credential verification failed.");
                    if (File.ReadAllBytes(Path.Combine(directory, "secrets", file.Id + ".bin")).AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes("smoke-credential")) >= 0) throw new Exception("Credential is not encrypted.");
                    await store.Run(file.Id);
                    var database = Path.Combine(directory, "Demo.sqlite3"); using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString())) { db.Open(); using var sql = db.CreateCommand(); sql.CommandText = "CREATE TABLE customers(id INTEGER PRIMARY KEY,name TEXT); INSERT INTO customers VALUES(1,'Alice'); CREATE TABLE logs(id INTEGER,payload TEXT); INSERT INTO logs VALUES(1,'temporary');"; sql.ExecuteNonQuery(); }
                    var sqlite = new BackupJob { Name = "Customer database", Kind = BackupKind.Sqlite, SourcePath = database, DestinationPath = file.DestinationPath, ExcludedDatabaseTables = [new("main", "logs")] }; store.Save(sqlite); await store.Run(sqlite.Id);
                    if (store.Snapshot().History.Any(r => r.Status != RunStatus.Success)) throw new Exception("Windows backup smoke test failed: " + store.Snapshot().History.First(r => r.Status != RunStatus.Success).Message);
                    _ = new JobEditorWindow(store, sqlite); _ = new SettingsWindow(store);
                    window.Show(); window.Refresh(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                    var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var stream = File.Create(Path.Combine(output, "BackupCron-Windows-preview.png"))) encoder.Save(stream);
                    File.WriteAllText(Path.Combine(output, "smoke-result.txt"), "PASS: WPF startup, editor, settings, DPAPI credentials, file ZIP, SQLite exclusions.\n");
                }
                catch (Exception ex) { exit = 1; var output = args.LastOrDefault() ?? directory; Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "smoke-result.txt"), ex.ToString()); }
                finally { app.Shutdown(exit); }
            };
            app.Run(); try { Directory.Delete(directory, true); } catch { } return exit;
        }
        if (args.Contains("--minimized")) app.Run(); else app.Run(window);
        return exit;
    }
}
