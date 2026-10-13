using BackupCron.Core;
using System.Diagnostics;

namespace BackupCron.Tests;

internal static class ServerTests
{
    public static async Task Run(Func<string, Func<Task>, Task> check, string root)
    {
        foreach (var kind in new[] { BackupKind.Postgres, BackupKind.Mysql })
        {
            await check(kind + " catalog, exclusions and restored dump", async () =>
            {
                // Opt-in CI fixtures use localhost and these fixed disposable database names.
                var postgres = kind == BackupKind.Postgres; var password = "backupcron-fixture";
                var job = new BackupJob { Kind = kind, Name = kind.ToString(), Host = "127.0.0.1", Port = postgres ? 5432 : 3306, Username = postgres ? "postgres" : "root", DatabaseName = "backupcron_fixture", DestinationPath = Path.Combine(root, kind.ToString()), ArchiveOutput = false };
                var client = ProcessRunner.ResolveTool(postgres ? "psql" : "mysql");
                List<string> args = postgres ? ["--host=127.0.0.1", "--port=5432", "--username=postgres", "--dbname=postgres", "--set=ON_ERROR_STOP=1"] : ["--host=127.0.0.1", "--port=3306", "--user=root"];
                if (postgres)
                {
                    await Sql(client, args, "DROP DATABASE IF EXISTS backupcron_fixture;", password, true); await Sql(client, args, "CREATE DATABASE backupcron_fixture;", password, true);
                    await Sql(client, args, "DROP DATABASE IF EXISTS backupcron_restore;", password, true); await Sql(client, args, "CREATE DATABASE backupcron_restore;", password, true);
                }
                else await Sql(client, args, "DROP DATABASE IF EXISTS backupcron_fixture; CREATE DATABASE backupcron_fixture; DROP DATABASE IF EXISTS backupcron_restore; CREATE DATABASE backupcron_restore;", password, false);
                if (postgres) args[3] = "--dbname=backupcron_fixture"; else args.Add("backupcron_fixture");
                await Sql(client, args, "CREATE TABLE customers(id INTEGER PRIMARY KEY, name VARCHAR(100)); CREATE TABLE orders(id INTEGER PRIMARY KEY, customer_id INTEGER REFERENCES customers(id)); CREATE TABLE logs(id INTEGER PRIMARY KEY, payload VARCHAR(100)); INSERT INTO customers VALUES(1,'Alice'); INSERT INTO orders VALUES(9,1); INSERT INTO logs VALUES(3,'exclude');", password, postgres);
                var engine = new BackupEngine(); await engine.TestConnectionAsync(job, password); var catalog = await engine.InspectDatabaseAsync(job, password);
                Program.Assert(catalog.Tables.Count == 3 && catalog.Tables.All(t => t.SizeBytes >= 0)); job.ExcludedDatabaseTables = [new(postgres ? "public" : "backupcron_fixture", "logs")];
                var result = await engine.RunAsync(job, password); var dump = await File.ReadAllTextAsync(result.OutputPath); Program.Assert(!dump.Contains(password));
                if (postgres) args[3] = "--dbname=backupcron_restore"; else args[^1] = "backupcron_restore";
                await Sql(client, args, dump, password, postgres);
                var restored = await Sql(client, args, "SELECT name FROM customers; SELECT customer_id FROM orders;", password, postgres); Program.Assert(restored.Contains("Alice") && restored.Contains('1'));
                var catalogJob = job.Clone(); catalogJob.DatabaseName = "backupcron_restore"; var after = await engine.InspectDatabaseAsync(catalogJob, password); Program.Assert(after.Tables.All(t => t.Name != "logs") && after.Tables.Count == 2);
            });
        }
    }
    private static async Task<string> Sql(string client, List<string> args, string sql, string password, bool postgres)
    {
        var start = new ProcessStartInfo(client) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg); start.Environment[postgres ? "PGPASSWORD" : "MYSQL_PWD"] = password;
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); await process.StandardInput.WriteAsync(sql); process.StandardInput.Close(); await process.WaitForExitAsync(); var stderr = await error;
        if (process.ExitCode != 0) throw new IOException(ProcessRunner.Redact(stderr, password)); return await output;
    }
}
