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
        var app = new Application(); Ui.Install(app); RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var directory = smoke ? Path.Combine(Path.GetTempPath(), "BackupCron-Windows-smoke-" + Guid.NewGuid()) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BackupCron");
        var store = new BackupStore(directory, new WindowsSecretStore(Path.Combine(directory, "secrets")), new WindowsStorageProbe()); var exit = 0;
        var window = new MainWindow(store, smoke); app.MainWindow = window;
        app.DispatcherUnhandledException += (_, e) => { Ui.Error(window, e.Exception); e.Handled = true; };
        if (smoke)
        {
            app.Startup += async (_, _) =>
            {
                var output = args.SkipWhile(a => a != "--smoke-test").Skip(1).FirstOrDefault() ?? Path.Combine(directory, "proof"); Directory.CreateDirectory(output);
                try
                {
                    var source = Path.Combine(directory, "Demo files"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "report.txt"), "BackupCron Windows verification fixture");
                    var file = new BackupJob { Name = "Project files", SourcePath = source, DestinationPath = Path.Combine(directory, "Backups"), RetentionCount = 5, Schedule = ScheduleKind.Daily };
                    store.Save(file, "smoke-credential"); if (store.Password(file.Id) != "smoke-credential" || File.ReadAllText(Path.Combine(directory, "state.json")).Contains("smoke-credential")) throw new Exception("Windows credential verification failed.");
                    if (File.ReadAllBytes(Path.Combine(directory, "secrets", file.Id + ".bin")).AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes("smoke-credential")) >= 0) throw new Exception("Credential is not encrypted.");
                    await store.Run(file.Id);
                    var database = Path.Combine(directory, "Demo.sqlite3"); using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString())) { db.Open(); using var sql = db.CreateCommand(); sql.CommandText = "CREATE TABLE customers(id INTEGER PRIMARY KEY,name TEXT); INSERT INTO customers VALUES(1,'Alice'); CREATE TABLE logs(id INTEGER,payload TEXT); INSERT INTO logs VALUES(1,'temporary');"; sql.ExecuteNonQuery(); }
                    var sqlite = new BackupJob { Name = "Customer database", Kind = BackupKind.Sqlite, SourcePath = database, DestinationPath = file.DestinationPath, ExcludedDatabaseTables = [new("main", "logs")] }; store.Save(sqlite); await store.Run(sqlite.Id);
                    var share = args.SkipWhile(a => a != "--storage-share").Skip(1).FirstOrDefault();
                    if (share != null)
                    {
                        var reading = await store.Storage.ReadAsync(share);
                        if (!reading.Known || !reading.Network) throw new Exception("Native UNC capacity query failed: " + reading.Error);
                        var contained = new BackupJob { Name = "Invalid nested destination", SourcePath = share, DestinationPath = Path.Combine(share, "inside-source") };
                        var rejected = false; try { BackupEngine.Validate(contained); } catch (ArgumentException) { rejected = true; }
                        if (!rejected) throw new Exception("UNC source containment was not rejected.");
                        var networkJob = new BackupJob { Name = "Network files", SourcePath = source, DestinationPath = share, RetentionCount = 5 };
                        store.Save(networkJob); await store.Run(networkJob.Id);
                    }
                    var refused = false;
                    try { await store.Storage.RequireAsync(directory, long.MaxValue, null, CancellationToken.None); }
                    catch (IOException) { refused = true; }
                    if (!refused) throw new Exception("Native storage preflight did not reject an impossible space requirement.");
                    using (var feedback = new WindowsFeedback(() => { }, () => { })) feedback.NotifyStorage(new("fixture", StorageLevel.Warning, "BackupCron storage notification verification"), store.Snapshot().Settings);
                    if (store.Snapshot().History.Any(r => r.Status != RunStatus.Success)) throw new Exception("Windows backup smoke test failed: " + store.Snapshot().History.First(r => r.Status != RunStatus.Success).Message);
                    
                    window.Show(); window.Refresh(); await window.RefreshStorageAsync();
                    if (store.Storage.Current.Volumes.Count == 0 || store.Storage.Current.Jobs.Count != (share == null ? 2 : 3) || store.Storage.Current.Jobs.Any(j => j.Estimate == null || !j.Reading.Known)) throw new Exception("Windows storage monitor smoke test failed.");
                    File.WriteAllText(Path.Combine(output, "storage-result.json"), System.Text.Json.JsonSerializer.Serialize(store.Storage.Current, Json.Options));
                    await UiSmokeTests.RunAsync(store, window, file, sqlite, output);
                    File.WriteAllText(Path.Combine(output, "smoke-result.txt"), "PASS: WPF navigation, search, job actions, editor save, database table selection, history filters, settings persistence, keyboard focus, compact layout, real window renders, DPAPI credentials, file ZIP, SQLite exclusions, native storage capacity, UNC backup, tray notification and next-backup budgets.\n");
                }
                catch (Exception ex) { exit = 1; Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "smoke-result.txt"), ex.ToString()); }
                finally { app.Shutdown(exit); }
            };
            app.Run(); try { Directory.Delete(directory, true); } catch { } return exit;
        }
        if (args.Contains("--minimized")) app.Run(); else app.Run(window);
        return exit;
    }
}
