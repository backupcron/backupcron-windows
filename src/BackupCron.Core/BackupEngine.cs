using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BackupCron.Core;

public sealed class BackupEngine
{
    private readonly ProcessRunner runner = new();
    public async Task<DatabaseCatalog> InspectDatabaseAsync(BackupJob job, string password, CancellationToken ct = default)
    {
        ValidateDatabase(job); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        if (job.Kind == BackupKind.Sqlite)
        {
            try { return await Task.Run(() => SqliteBackup.Inspect(job.SourcePath, timeout.Token), timeout.Token); }
            catch when (timeout.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); throw new TimeoutException(L10n.Text("database.timeout")); }
        }
        var work = Path.Combine(Path.GetTempPath(), "BackupCron-catalog-" + Guid.NewGuid()); Directory.CreateDirectory(work);
        try
        {
            var output = Path.Combine(work, "catalog.json"); var command = QueryCommand(job, password);
            await runner.RunAsync(command, output, password, null, timeout.Token);
            if (new FileInfo(output).Length > 32 * 1024 * 1024) throw new InvalidDataException(L10n.Text("database.invalid_response"));
            var text = (await File.ReadAllTextAsync(output, Encoding.UTF8, timeout.Token)).Trim().TrimStart('\uFEFF');
            var catalog = JsonSerializer.Deserialize<DatabaseCatalog>(text, Json.Options) ?? throw new InvalidDataException(L10n.Text("database.invalid_response"));
            catalog.Validate(); return catalog;
        }
        catch when (timeout.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); throw new TimeoutException(L10n.Text("database.timeout")); }
        finally { BackupPaths.DeleteOwnWork(work); }
    }
    public async Task<string> TestConnectionAsync(BackupJob job, string password, CancellationToken ct = default)
    {
        ValidateDatabase(job);
        if (job.Kind == BackupKind.Sqlite) { await InspectDatabaseAsync(job, password, ct); return L10n.Text("engine.connection_ok"); }
        var work = Path.Combine(Path.GetTempPath(), "BackupCron-test-" + Guid.NewGuid()); Directory.CreateDirectory(work);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await runner.RunAsync(DumpCommand(job, password, true), Path.Combine(work, "schema.sql"), password, null, timeout.Token); return L10n.Text("engine.connection_ok"); }
        finally { BackupPaths.DeleteOwnWork(work); }
    }
    public Task<BackupResult> RunAsync(BackupJob job, string password = "", IProgress<BackupProgress>? progress = null, Action<string>? log = null, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            Validate(job); ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(job.DestinationPath);
            var work = Path.Combine(job.DestinationPath, ".BackupCron-work-" + Guid.NewGuid()); Directory.CreateDirectory(work);
            try
            {
                string staged; string extension; long size;
                progress?.Report(new(.02, L10n.Text("job.running")));
                if (!job.HasDatabaseSource) (staged, extension, size) = await BackupFiles.StageAsync(job, work, progress, log, ct);
                else
                {
                    DatabaseCatalog? catalog = null;
                    try { catalog = await InspectDatabaseAsync(job, password, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (job.ExcludedDatabaseTables.Count == 0) { log?.Invoke(ProcessRunner.Redact(ex.Message, password)); }
                    if (catalog != null)
                    {
                        if (catalog.SelectionError(job.ExcludedDatabaseTables) is { } error) throw new InvalidDataException(error);
                        BackupPaths.RequireSpace(job.DestinationPath, EstimateSpace(job, catalog));
                        job = job.Clone(); job.ExcludedDatabaseTables = catalog.EffectiveExclusions(job.ExcludedDatabaseTables).ToList();
                    }
                    extension = job.Kind == BackupKind.Sqlite ? "sqlite3" : "sql"; staged = Path.Combine(work, "snapshot." + extension);
                    if (job.Kind == BackupKind.Sqlite) await SqliteBackup.CreateAsync(job, staged, progress, log, ct);
                    else
                    {
                        await runner.RunAsync(DumpCommand(job, password), staged, password, log, ct);
                        if (new FileInfo(staged).Length == 0) throw new InvalidDataException(L10n.Text("engine.empty_dump"));
                    }
                    if (job.ArchiveOutput)
                    { var zip = Path.Combine(work, "snapshot.zip"); await BackupFiles.ZipDatabaseAsync(staged, zip, ct); File.Delete(staged); staged = zip; extension = "zip"; }
                    size = new FileInfo(staged).Length;
                }
                ct.ThrowIfCancellationRequested(); progress?.Report(new(.95, L10n.Text("log.verifying")));
                var published = BackupPaths.Publish(staged, job.DestinationPath, Naming.Render(job, DateTimeOffset.Now), extension, ct);
                progress?.Report(new(1, L10n.Text("ux.backup_created"))); return new BackupResult(published, size, L10n.Text("ux.backup_created"));
            }
            catch when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            finally { BackupPaths.DeleteOwnWork(work); }
        }, ct);

    public static void Validate(BackupJob job)
    {
        if (string.IsNullOrWhiteSpace(job.Name)) throw new ArgumentException(L10n.Text("validation.enter_name"));
        if (string.IsNullOrWhiteSpace(job.NameMask)) throw new ArgumentException(L10n.Text("validation.name_mask"));
        if (string.IsNullOrWhiteSpace(job.DestinationPath)) throw new ArgumentException(L10n.Text("validation.choose_destination"));
        if (job.RetentionCount < 0 || job.RetentionCount > 100000) throw new ArgumentException("Invalid retention count.");
        if (job.Schedule is ScheduleKind.Daily or ScheduleKind.Weekly && !TimeOnly.TryParseExact(job.ScheduledTime, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)) throw new ArgumentException("Use HH:mm for the schedule time.");
        if (job.Kind is BackupKind.Files or BackupKind.Project)
        {
            if (!BackupPaths.Exists(job.SourcePath)) throw new FileNotFoundException(L10n.Text("engine.source_not_found"));
            if (job.Kind == BackupKind.Project && !Directory.Exists(job.SourcePath)) throw new ArgumentException(L10n.Text("engine.project_source_required"));
            if (BackupPaths.Inside(BackupPaths.Resolve(job.DestinationPath), BackupPaths.Resolve(job.SourcePath))) throw new ArgumentException(L10n.Text("engine.destination_inside_source"));
        }
        if (job.HasDatabaseSource) ValidateDatabase(job);
    }
    public static long? EstimateSpace(BackupJob job, DatabaseCatalog catalog)
    {
        var size = job.Kind == BackupKind.Sqlite ? catalog.SizeBytes : catalog.SelectedSize(job.ExcludedDatabaseTables);
        var factor = job.Kind == BackupKind.Sqlite ? job.ExcludedDatabaseTables.Count > 0 ? 4 : job.ArchiveOutput ? 3 : 2 : job.ArchiveOutput ? 5 : 2;
        try { return size is { } value ? checked(value * factor + 65536) : null; } catch (OverflowException) { return null; }
    }
    public static void ValidateDatabase(BackupJob job)
    {
        if (!job.HasDatabaseSource) throw new ArgumentException(L10n.Text("engine.connection_test_database_only"));
        if (job.Kind == BackupKind.Sqlite) { if (!File.Exists(job.SourcePath)) throw new FileNotFoundException(L10n.Text("engine.source_not_found")); return; }
        if (string.IsNullOrWhiteSpace(job.DatabaseName) || string.IsNullOrWhiteSpace(job.Host) || string.IsNullOrWhiteSpace(job.Username)) throw new ArgumentException(L10n.Text("validation.database_parameters"));
        if (job.Port is < 1 or > 65535 || job.SshPort is < 1 or > 65535) throw new ArgumentException(L10n.Text("ux.invalid_port"));
        if (job.ConnectionMode == ConnectionMode.Docker && !Regex.IsMatch(job.DockerContainer, @"^[A-Za-z0-9][A-Za-z0-9_.-]*$")) throw new ArgumentException(L10n.Text("validation.docker_container"));
        if (job.ConnectionMode == ConnectionMode.Ssh && (string.IsNullOrWhiteSpace(job.SshHost) || string.IsNullOrWhiteSpace(job.SshUsername))) throw new ArgumentException(L10n.Text("validation.ssh_connection"));
        var values = new[] { job.DatabaseName, job.Host, job.Username, job.SshHost, job.SshUsername, job.SshIdentityPath };
        if (values.Any(v => v.Contains('\0') || v.Contains('\r') || v.Contains('\n'))) throw new ArgumentException("Connection values contain invalid characters.");
    }
    public static List<string> PostgresArguments(BackupJob job, bool schemaOnly = false)
    {
        var name = job.DatabaseName.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
        var args = new List<string> { "--host", job.Host, "--port", job.Port.ToString(), "--username", job.Username, "--dbname", $"dbname='{name}' sslmode='{(job.UseSsl ? "require" : "prefer")}' connect_timeout='15'", "--no-password" };
        if (schemaOnly) args.Add("--schema-only"); else args.AddRange(job.ExcludedDatabaseTables.Select(t => "--exclude-table=" + SqliteBackup.Quote(t.Schema) + "." + SqliteBackup.Quote(t.Name)));
        return args;
    }
    public static List<string> MySqlArguments(BackupJob job, bool schemaOnly = false)
    {
        var args = new List<string> { "--host=" + job.Host, "--port=" + job.Port, "--user=" + job.Username, "--single-transaction" };
        if (job.UseSsl) args.Add("--ssl-mode=REQUIRED");
        if (schemaOnly) args.Add("--no-data"); else args.AddRange(job.ExcludedDatabaseTables.Select(t => "--ignore-table=" + t.Schema + "." + t.Name));
        args.AddRange(["--", job.DatabaseName]); return args;
    }
    public static Command DumpCommand(BackupJob job, string password, bool schemaOnly = false)
    {
        var name = job.Kind == BackupKind.Postgres ? "pg_dump" : "mysqldump";
        var args = job.Kind == BackupKind.Postgres ? PostgresArguments(job, schemaOnly) : MySqlArguments(job, schemaOnly);
        return Wrap(job, password, name, args, customPath: job.ClientExecutablePath);
    }
    private static Command QueryCommand(BackupJob job, string password)
    {
        string name; List<string> args;
        if (job.Kind == BackupKind.Postgres)
        { name = "psql"; args = PostgresArguments(job, true); args.Remove("--schema-only"); args.AddRange(["--no-psqlrc", "--quiet", "--no-align", "--tuples-only", "--set=ON_ERROR_STOP=1", "--command", "BEGIN READ ONLY; SET LOCAL statement_timeout='25000'; " + CatalogQueries.Postgres + "; COMMIT;"]); }
        else
        { name = "mysql"; args = ["--host=" + job.Host, "--port=" + job.Port, "--user=" + job.Username, "--database=" + job.DatabaseName, "--batch", "--raw", "--skip-column-names", "--default-character-set=utf8mb4", "--connect-timeout=15"]; if (job.UseSsl) args.Add("--ssl-mode=REQUIRED"); args.AddRange(["--execute", "SET SESSION MAX_EXECUTION_TIME=25000; START TRANSACTION READ ONLY; " + CatalogQueries.MySql + "; COMMIT;"]); }
        var path = string.IsNullOrWhiteSpace(job.ClientExecutablePath) ? "" : Path.Combine(Path.GetDirectoryName(job.ClientExecutablePath)!, name + (OperatingSystem.IsWindows() ? ".exe" : ""));
        return Wrap(job, password, name, args, File.Exists(path) ? path : "");
    }
    private static Command Wrap(BackupJob job, string password, string name, List<string> args, string customPath)
    {
        var key = job.Kind == BackupKind.Postgres ? "PGPASSWORD" : "MYSQL_PWD"; var env = new Dictionary<string, string> { [key] = password };
        if (job.ConnectionMode == ConnectionMode.Direct) return new(ProcessRunner.ResolveTool(name, customPath), args, env);
        if (job.ConnectionMode == ConnectionMode.Docker) return new(ProcessRunner.ResolveTool("docker"), new[] { "exec", "-e", key, job.DockerContainer, name }.Concat(args).ToArray(), env);
        var ssh = new List<string> { "-T", "-p", job.SshPort.ToString(), "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=15" };
        if (job.SshIdentityPath.Length > 0) ssh.AddRange(["-i", job.SshIdentityPath]);
        ssh.AddRange(["--", job.SshUsername + "@" + job.SshHost, string.Join(' ', new[] { name }.Concat(args).Select(ProcessRunner.ShellQuote))]);
        // SSH uses .pgpass/.my.cnf on the server, never a password in its command line.
        return new(ProcessRunner.ResolveTool("ssh"), ssh);
    }
}
