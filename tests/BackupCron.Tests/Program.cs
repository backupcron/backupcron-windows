using BackupCron.Core;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace BackupCron.Tests;

internal static class Program
{
    private static int failures;
    private static int passed;
    private static readonly string root = Path.Combine(Path.GetTempPath(), "BackupCron-fixtures-" + Guid.NewGuid());
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 1 && args[0] == "--host" && args[1].StartsWith("fixture-", StringComparison.Ordinal))
        {
            if (args.Contains("--command")) { Console.Write("{\"sizeBytes\":4096,\"tables\":[]}"); return 0; }
            if (args[1] == "fixture-empty") return 0;
            Console.Write("partial SQL output"); Console.Out.Flush();
            if (args[1] == "fixture-sleep") { await Task.Delay(30000); return 0; }
            Console.Error.WriteLine("failure: fixture-secret"); return 9;
        }
        Directory.CreateDirectory(root);
        try
        {
            await Check("manual, disabled and interval schedules", () =>
            {
                var now = DateTimeOffset.Parse("2026-10-13T12:00:00Z"); var job = new BackupJob { CreatedAt = now.AddHours(-1) };
                Assert(!Scheduling.IsDue(job, now)); job.Schedule = ScheduleKind.Hourly; Assert(Scheduling.IsDue(job, now));
                Assert(Scheduling.NextRun(job, now) == now.AddHours(1)); job.IsEnabled = false; Assert(Scheduling.NextRun(job, now) == null);
                job.IsEnabled = true; job.Schedule = ScheduleKind.Interval; job.IntervalMinutes = 10; Assert(Scheduling.IsDue(job, now));
                job.LastRunAt = now; Assert(!Scheduling.IsDue(job, now)); Assert(Scheduling.NextRun(job, now) == now.AddMinutes(10)); return Task.CompletedTask;
            });
            await Check("daily and weekly schedules run once per slot", () =>
            {
                var now = DateTimeOffset.Parse("2026-10-13T12:00:00Z"); var job = new BackupJob { CreatedAt = now.AddDays(-3), Schedule = ScheduleKind.Daily, ScheduledTime = "02:00" };
                Assert(Scheduling.IsDue(job, now, TimeZoneInfo.Utc)); job.LastRunAt = now; Assert(!Scheduling.IsDue(job, now, TimeZoneInfo.Utc));
                Assert(Scheduling.NextRun(job, now, TimeZoneInfo.Utc) == DateTimeOffset.Parse("2026-10-14T02:00:00Z"));
                job.Schedule = ScheduleKind.Weekly; job.Weekday = (int)DayOfWeek.Tuesday + 1; job.LastRunAt = now.AddDays(-8); Assert(Scheduling.IsDue(job, now, TimeZoneInfo.Utc)); return Task.CompletedTask;
            });
            await Check("DST gap and repeated hour do not duplicate a daily run", () =>
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
                var job = new BackupJob { Schedule = ScheduleKind.Daily, ScheduledTime = "02:30", CreatedAt = DateTimeOffset.Parse("2026-03-07T12:00:00Z") };
                var now = DateTimeOffset.Parse("2026-03-08T08:00:00Z"); Assert(Scheduling.IsDue(job, now, zone)); job.LastRunAt = now; Assert(!Scheduling.IsDue(job, now, zone));
                job.ScheduledTime = "01:30"; job.LastRunAt = DateTimeOffset.Parse("2026-11-01T05:31:00Z"); Assert(!Scheduling.IsDue(job, DateTimeOffset.Parse("2026-11-01T06:31:00Z"), zone)); return Task.CompletedTask;
            });
            await Check("naming treats inserted tokens literally and rejects reserved Windows names", () =>
            {
                var job = new BackupJob { Name = "{date}", NameMask = "{job}-{type}" }; var name = Naming.Render(job, DateTimeOffset.Now);
                Assert(!name.Contains("2026")); job.NameMask = "CON"; Assert(Naming.Render(job, DateTimeOffset.Now) == "backup-CON"); job.NameMask = "../../"; Assert(Naming.Render(job, DateTimeOffset.Now) == "backup"); return Task.CompletedTask;
            });
            await Check("single file copy and ZIP preserve exact bytes", async () =>
            {
                var folder = Fixture(); var source = Path.Combine(folder, "report русский.txt"); var content = RandomNumberGenerator.GetBytes(130123); await File.WriteAllBytesAsync(source, content);
                var engine = new BackupEngine(); var job = Job(source, folder); job.ArchiveOutput = false;
                var copy = await engine.RunAsync(job); Assert((await File.ReadAllBytesAsync(copy.OutputPath)).SequenceEqual(content));
                job.ArchiveOutput = true; var zip = await engine.RunAsync(job); using var archive = ZipFile.OpenRead(zip.OutputPath); using var stream = archive.Entries.Single().Open(); using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer); Assert(buffer.ToArray().SequenceEqual(content));
            });
            await Check("folder and project backups retain empty directories and exclusions", async () =>
            {
                var folder = Fixture(); var source = Path.Combine(folder, "project"); Directory.CreateDirectory(source); Directory.CreateDirectory(Path.Combine(source, "empty")); Directory.CreateDirectory(Path.Combine(source, ".git")); Directory.CreateDirectory(Path.Combine(source, "node_modules"));
                await File.WriteAllTextAsync(Path.Combine(source, "main.txt"), "payload"); await File.WriteAllTextAsync(Path.Combine(source, ".git", "HEAD"), "ref: refs/heads/main"); await File.WriteAllTextAsync(Path.Combine(source, "node_modules", "skip"), "large");
                var engine = new BackupEngine(); var job = Job(source, folder); job.Kind = BackupKind.Project; job.ArchiveOutput = false;
                var copy = await engine.RunAsync(job); Assert(File.Exists(Path.Combine(copy.OutputPath, ".git", "HEAD"))); Assert(Directory.Exists(Path.Combine(copy.OutputPath, "empty"))); Assert(!Directory.Exists(Path.Combine(copy.OutputPath, "node_modules")));
                job.IncludesGitMetadata = false; job.ArchiveOutput = true; var zip = await engine.RunAsync(job); using var archive = ZipFile.OpenRead(zip.OutputPath); Assert(!archive.Entries.Any(e => e.FullName.Contains(".git/"))); Assert(archive.Entries.Any(e => e.FullName.EndsWith("empty/")));
            });
            await Check("destination inside source and through a link is rejected", async () =>
            {
                var folder = Fixture(); var source = Path.Combine(folder, "source"); Directory.CreateDirectory(source); var job = Job(source, folder); job.DestinationPath = Path.Combine(source, "backup");
                await Throws(() => new BackupEngine().RunAsync(job)); Assert(!Directory.Exists(job.DestinationPath));
                if (!OperatingSystem.IsWindows()) { var link = Path.Combine(folder, "link"); Directory.CreateSymbolicLink(link, source); job.DestinationPath = Path.Combine(link, "backup"); await Throws(() => new BackupEngine().RunAsync(job)); }
            });
            await Check("publication never overwrites an existing result", async () =>
            {
                var folder = Fixture(); var source = Path.Combine(folder, "file.txt"); await File.WriteAllTextAsync(source, "old"); var job = Job(source, folder); job.NameMask = "same"; job.ArchiveOutput = false;
                var engine = new BackupEngine(); var a = await engine.RunAsync(job); await File.WriteAllTextAsync(source, "new"); var b = await engine.RunAsync(job);
                Assert(a.OutputPath != b.OutputPath); Assert(await File.ReadAllTextAsync(a.OutputPath) == "old"); Assert(await File.ReadAllTextAsync(b.OutputPath) == "new");
            });
            await Check("cancelled copies remove only their own work files", async () =>
            {
                var folder = Fixture(); var source = Path.Combine(folder, "file.bin"); await File.WriteAllBytesAsync(source, new byte[1024 * 1024]); var job = Job(source, folder); Directory.CreateDirectory(job.DestinationPath); var sentinel = Path.Combine(job.DestinationPath, "keep.txt"); await File.WriteAllTextAsync(sentinel, "keep");
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); await Throws(() => new BackupEngine().RunAsync(job, ct: cancelled.Token)); Assert(await File.ReadAllTextAsync(sentinel) == "keep"); Assert(Directory.GetFileSystemEntries(job.DestinationPath).Length == 1);
                using var cancel = new CancellationTokenSource(); var progress = new ImmediateProgress(p => { if (p.Fraction >= .1) cancel.Cancel(); }); await Throws(() => new BackupEngine().RunAsync(job, progress: progress, ct: cancel.Token)); Assert(Directory.GetFileSystemEntries(job.DestinationPath).Length == 1);
            });
            await Check("SQLite inspection is read only and missing sources are not created", async () =>
            {
                var folder = Fixture(); var source = Path.Combine(folder, "missing.sqlite"); var job = Job(source, folder); job.Kind = BackupKind.Sqlite;
                await Throws(() => new BackupEngine().InspectDatabaseAsync(job, "")); Assert(!File.Exists(source));
                Sql(source, "CREATE TABLE logs(id INTEGER PRIMARY KEY,data TEXT); INSERT INTO logs VALUES(1,'payload');"); var bytes = await File.ReadAllBytesAsync(source); var catalog = await new BackupEngine().InspectDatabaseAsync(job, ""); Assert(catalog.Tables.Single().Name == "logs"); Assert(catalog.SizeBytes > 0); Assert((await File.ReadAllBytesAsync(source)).SequenceEqual(bytes));
            });
            await Check("SQLite full copy includes committed WAL data", async () =>
            {
                var folder = Fixture(); var source = Path.Combine(folder, "wal.sqlite"); using var live = Db(source); Exec(live, "PRAGMA journal_mode=WAL; CREATE TABLE records(id INTEGER PRIMARY KEY,value BLOB); INSERT INTO records VALUES(1,X'000102FF');");
                var job = Job(source, folder); job.Kind = BackupKind.Sqlite; job.ArchiveOutput = false; var result = await new BackupEngine().RunAsync(job); using var copy = Db(result.OutputPath); Assert(Convert.ToInt64(Value(copy, "SELECT count(*) FROM records")) == 1); Assert((byte[])Value(copy, "SELECT value FROM records")! is [0, 1, 2, 255]);
            });
            await Check("SQLite exclusions remove structure and data without changing the source", async () =>
            {
                var folder = Fixture(); var source = Path.Combine(folder, "db.sqlite"); Sql(source, "CREATE TABLE customers(id INTEGER PRIMARY KEY,name TEXT); CREATE TABLE logs(id INTEGER PRIMARY KEY,secret TEXT); INSERT INTO customers VALUES(7,'keep'); INSERT INTO logs VALUES(1,'private payload');");
                var before = await File.ReadAllBytesAsync(source); var job = Job(source, folder); job.Kind = BackupKind.Sqlite; job.ArchiveOutput = false; job.ExcludedDatabaseTables = [new("main", "logs")];
                var result = await new BackupEngine().RunAsync(job); using var copy = Db(result.OutputPath); Assert(Value(copy, "SELECT name FROM customers")!.ToString() == "keep"); Assert(Convert.ToInt64(Value(copy, "SELECT count(*) FROM sqlite_schema WHERE name='logs'")) == 0); Assert((await File.ReadAllBytesAsync(source)).SequenceEqual(before));
                job.ArchiveOutput = true; var zip = await new BackupEngine().RunAsync(job); using var archive = ZipFile.OpenRead(zip.OutputPath); Assert(archive.Entries.Single().FullName.EndsWith("sqlite3"));
            });
            await Check("SQLite foreign keys, views and triggers block broken partial copies", async () =>
            {
                var folder = Fixture(); var source = Path.Combine(folder, "deps.sqlite"); Sql(source, "CREATE TABLE parent(id INTEGER PRIMARY KEY); CREATE TABLE child(id INTEGER, parent_id INTEGER REFERENCES parent(id)); CREATE VIEW nested AS SELECT * FROM parent; CREATE VIEW view2 AS SELECT * FROM nested; CREATE TRIGGER child_insert AFTER INSERT ON child BEGIN INSERT INTO parent VALUES(new.parent_id); END;");
                var job = Job(source, folder); job.Kind = BackupKind.Sqlite; job.ExcludedDatabaseTables = [new("main", "parent")]; var catalog = await new BackupEngine().InspectDatabaseAsync(job, "");
                Assert(catalog.Tables.Single(t => t.Name == "child").RequiredTables.Contains(new("main", "parent"))); Assert(catalog.Tables.Single(t => t.Name == "view2").RequiredTables.Contains(new("main", "parent"))); Assert(catalog.SelectionError(job.ExcludedDatabaseTables) != null); await Throws(() => new BackupEngine().RunAsync(job)); Assert(!Directory.EnumerateFileSystemEntries(job.DestinationPath).Any());
            });
            await Check("SQLite application functions block only uncertain partial copies", async () =>
            {
                var folder = Fixture(); var source = Path.Combine(folder, "func.sqlite"); Sql(source, "CREATE TABLE keep(id INTEGER); CREATE TABLE logs(id INTEGER); CREATE VIEW app_view AS SELECT custom_application_function(id) FROM keep;"); var job = Job(source, folder); job.Kind = BackupKind.Sqlite; job.ArchiveOutput = false;
                var catalog = await new BackupEngine().InspectDatabaseAsync(job, ""); Assert(catalog.Tables.Single(t => t.Name == "app_view").DependencyInspectionError != null); await new BackupEngine().RunAsync(job);
                job.ExcludedDatabaseTables = [new("main", "logs")]; await Throws(() => new BackupEngine().RunAsync(job));
            });
            await Check("SQLite external FTS and vocabulary dependencies are preserved", async () =>
            {
                var folder = Fixture(); var source = Path.Combine(folder, "fts.sqlite"); Sql(source, "CREATE TABLE content(id INTEGER PRIMARY KEY,body TEXT); INSERT INTO content VALUES(1,'hello world'); CREATE VIRTUAL TABLE search USING fts5(body,content='content',content_rowid='id'); INSERT INTO search(search) VALUES('rebuild'); CREATE VIRTUAL TABLE vocabulary USING fts5vocab(search,'row'); CREATE TABLE logs(id INTEGER);");
                var job = Job(source, folder); job.Kind = BackupKind.Sqlite; job.ArchiveOutput = false; var catalog = await new BackupEngine().InspectDatabaseAsync(job, ""); Assert(catalog.Tables.Single(t => t.Name == "search").RequiredTables.Contains(new("main", "content"))); Assert(catalog.Tables.Single(t => t.Name == "vocabulary").RequiredTables.Contains(new("main", "search")));
                job.ExcludedDatabaseTables = [new("main", "logs")]; var result = await new BackupEngine().RunAsync(job); using var copy = Db(result.OutputPath); Assert(Convert.ToInt64(Value(copy, "SELECT count(*) FROM search WHERE search MATCH 'hello'")) == 1);
            });
            await Check("SQLite compaction preserves sparse and extreme row IDs without firing triggers", async () =>
            {
                var folder = Fixture(); var source = Path.Combine(folder, "rowids.sqlite"); Sql(source, "CREATE TABLE keep(name TEXT); INSERT INTO keep(rowid,name) VALUES(-9223372036854775808,'min'),(42,'middle'),(9223372036854775807,'max'); CREATE TABLE audit(id INTEGER); CREATE TRIGGER update_audit AFTER UPDATE ON keep BEGIN INSERT INTO audit VALUES(new.rowid); END; CREATE VIEW ids AS SELECT rowid,name FROM keep; CREATE TABLE logs(id INTEGER); CREATE TABLE without_ids(id TEXT PRIMARY KEY,value TEXT) WITHOUT ROWID; INSERT INTO without_ids VALUES('key','payload');");
                var job = Job(source, folder); job.Kind = BackupKind.Sqlite; job.ArchiveOutput = false; job.ExcludedDatabaseTables = [new("main", "logs")]; var result = await new BackupEngine().RunAsync(job); using var copy = Db(result.OutputPath);
                Assert(Convert.ToInt64(Value(copy, "SELECT rowid FROM keep WHERE name='min'")) == long.MinValue); Assert(Convert.ToInt64(Value(copy, "SELECT rowid FROM keep WHERE name='middle'")) == 42); Assert(Convert.ToInt64(Value(copy, "SELECT rowid FROM keep WHERE name='max'")) == long.MaxValue); Assert(Convert.ToInt64(Value(copy, "SELECT count(*) FROM audit")) == 0); Assert(Value(copy, "SELECT value FROM without_ids")!.ToString() == "payload");
            });
            await Check("table identities distinguish normalization and expand partitions only", () =>
            {
                Assert(new TableReference("main", "é") != new TableReference("main", "e\u0301")); var parent = new TableReference("public", "parent"); var catalog = new DatabaseCatalog { Tables = [new() { Schema = "public", Name = "parent", SizeBytes = 5 }, new() { Schema = "public", Name = "partition", ParentTables = [parent], SizeBytes = 10 }, new() { Schema = "public", Name = "child", RequiredTables = [parent], SizeBytes = 20 }] };
                Assert(catalog.EffectiveExclusions([parent]).Count == 2); Assert(catalog.SelectedSize([parent]) == 20); Assert(catalog.SelectionError([parent]) != null); return Task.CompletedTask;
            });
            await Check("server arguments quote identifiers and keep passwords out of arguments", () =>
            {
                var job = new BackupJob { Kind = BackupKind.Postgres, DatabaseName = "x' sslmode='disable", Host = "localhost", Username = "user", UseSsl = true, ExcludedDatabaseTables = [new("schema.with.dot", "table*name")] }; var args = BackupEngine.PostgresArguments(job); Assert(args.Contains("--exclude-table=\"schema.with.dot\".\"table*name\"")); Assert(args.Any(a => a.Contains("sslmode='require'"))); Assert(args.Any(a => a.Contains("x\\'"))); Assert(ProcessRunner.ShellQuote("a'b") == "'a'\\''b'"); Assert(ProcessRunner.Redact("password=secret", "secret") == "password=••••"); job.Kind = BackupKind.Mysql; job.DatabaseName = "--option"; Assert(BackupEngine.MySqlArguments(job)[^2] == "--"); return Task.CompletedTask;
            });
            await Check("failed, empty and cancelled dump clients never publish partial outputs", async () =>
            {
                var executable = Environment.ProcessPath!; Assert(Path.GetFileNameWithoutExtension(executable) != "dotnet"); var queryClient = Path.Combine(AppContext.BaseDirectory, "psql" + (OperatingSystem.IsWindows() ? ".exe" : "")); File.Copy(executable, queryClient, false);
                try
                {
                    foreach (var host in new[] { "fixture-fail", "fixture-empty", "fixture-sleep" })
                    {
                        var folder = Fixture(); var job = new BackupJob { Kind = BackupKind.Postgres, Name = host, DatabaseName = "backupcron_fixture", Username = "fixture", Host = host, ClientExecutablePath = executable, DestinationPath = Path.Combine(folder, "output"), ArchiveOutput = false }; var logs = new List<string>(); using var cancel = new CancellationTokenSource(); if (host == "fixture-sleep") cancel.CancelAfter(700);
                        await Throws(() => new BackupEngine().RunAsync(job, "fixture-secret", log: logs.Add, ct: cancel.Token)); Assert(!Directory.EnumerateFileSystemEntries(job.DestinationPath).Any()); Assert(logs.All(line => !line.Contains("fixture-secret")));
                    }
                }
                finally { File.Delete(queryClient); }
            });
            await Check("retention protects outputs used as another job's source", async () =>
            {
                var folder = Fixture(); var source = Path.Combine(folder, "input.txt"); File.WriteAllText(source, "payload"); var job = Job(source, folder); job.NameMask = "copy"; job.ArchiveOutput = false; job.RetentionCount = 1; var store = new BackupStore(Path.Combine(folder, "state"), new MemorySecretStore()); store.Save(job); await store.Run(job.Id); var protectedCopy = store.Snapshot().History[0].OutputPath!;
                var consumer = new BackupJob { Name = "consumer", SourcePath = protectedCopy, DestinationPath = Path.Combine(folder, "consumer-output") }; store.Save(consumer); await store.Run(job.Id); Assert(File.Exists(protectedCopy));
            });
            await Check("SQLite disk budget includes the full source before exclusions", () =>
            {
                var job = new BackupJob { Kind = BackupKind.Sqlite, ExcludedDatabaseTables = [new("main", "logs")] }; var catalog = new DatabaseCatalog { SizeBytes = 1000000, Tables = [new() { Name = "keep", SizeBytes = 100 }, new() { Name = "logs", SizeBytes = 999900 }] };
                Assert(catalog.SelectedSize(job.ExcludedDatabaseTables) == 100); Assert(BackupEngine.EstimateSpace(job, catalog) > catalog.SizeBytes); return Task.CompletedTask;
            });
            await Check("retention deletes only previous successful outputs of the same job", async () =>
            {
                var folder = Fixture(); var source = Path.Combine(folder, "file.txt"); await File.WriteAllTextAsync(source, "payload"); var job = Job(source, folder); job.NameMask = "same"; job.ArchiveOutput = false; job.RetentionCount = 2; var store = new BackupStore(Path.Combine(folder, "state"), new MemorySecretStore()); store.Save(job); var paths = new List<string>();
                for (int i = 0; i < 3; i++) { await store.Run(job.Id); paths.Add(store.Snapshot().History[0].OutputPath!); }
                Assert(!File.Exists(paths[0])); Assert(File.Exists(paths[1]) && File.Exists(paths[2])); Assert(store.Snapshot().History.Count == 3);
                var other = job.Clone(); other.Id = Guid.NewGuid(); store.Save(other); await store.Run(other.Id); Assert(File.Exists(paths[1]));
            });
            await Check("jobs beyond former limits persist and passwords are absent from state", () =>
            {
                var folder = Fixture(); var secrets = new MemorySecretStore(); var store = new BackupStore(folder, secrets);
                for (int i = 0; i < 12; i++) store.Save(new BackupJob { Name = "Job " + i }, "password-secret");
                Assert(new BackupStore(folder, secrets).Snapshot().Jobs.Count == 12); Assert(!File.ReadAllText(Path.Combine(folder, "state.json")).Contains("password-secret")); return Task.CompletedTask;
            });
            await Check("corrupt state is preserved and cannot be overwritten", () =>
            {
                var folder = Fixture(); var path = Path.Combine(folder, "state.json"); File.WriteAllText(path, "invalid-json"); var store = new BackupStore(folder, new MemorySecretStore()); Assert(store.ReadOnly);
                try { store.Save(new BackupJob()); throw new Exception("Expected rejection"); } catch (IOException) { } Assert(File.ReadAllText(path) == "invalid-json"); return Task.CompletedTask;
            });
            await Check("all ten localization dictionaries are available", () =>
            {
                Assert(L10n.AvailableLanguages.Count == 10); foreach (var lang in L10n.AvailableLanguages) { L10n.Language = lang; Assert(L10n.Text("common.new_job") != "common.new_job"); Assert(L10n.Text("database.required_table", "a", "b").Contains('a')); } L10n.Language = "en"; return Task.CompletedTask;
            });
            await StorageTests.Run(Check);
            await SettingsBackupTests.Run(Check);
            if (args.Contains("--server-tests")) await ServerTests.Run(Check, root);
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"RESULT: {passed} passed, {failures} failed"); return failures == 0 ? 0 : 1;
    }
    internal static async Task Check(string name, Func<Task> test)
    { try { await test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex); } }
    internal static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    internal static async Task Throws(Func<Task> action) { try { await action(); } catch { return; } throw new Exception("Expected failure"); }
    internal static string Fixture() { var path = Path.Combine(root, Guid.NewGuid().ToString()); Directory.CreateDirectory(path); return path; }
    internal static BackupJob Job(string source, string folder) => new() { Name = "Fixture", SourcePath = source, DestinationPath = Path.Combine(folder, "output") };
    internal static SqliteConnection Db(string path) { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open(); return db; }
    internal static void Exec(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    internal static object? Value(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    internal static void Sql(string path, string sql) { using var db = Db(path); Exec(db, sql); }
    private sealed class ImmediateProgress(Action<BackupProgress> report) : IProgress<BackupProgress> { public void Report(BackupProgress value) => report(value); }
}
