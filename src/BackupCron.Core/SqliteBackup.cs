using Microsoft.Data.Sqlite;
using SQLitePCL;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace BackupCron.Core;

internal static class SqliteBackup
{
    public static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    public static SqliteConnection Open(string path, bool writable = false)
    {
        if (!writable && !File.Exists(path)) throw new FileNotFoundException(L10n.Text("engine.source_not_found"), path);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = writable ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
        db.Open(); Execute(db, "PRAGMA trusted_schema=OFF; PRAGMA foreign_keys=OFF;"); return db;
    }
    private static void Execute(SqliteConnection db, string sql)
    { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection db, string sql)
    { using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private static List<string[]> Rows(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand(); command.CommandText = sql; using var reader = command.ExecuteReader(); var rows = new List<string[]>();
        while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!).ToArray());
        return rows;
    }
    private static void WatchCancellation(SqliteConnection db, CancellationToken ct) => raw.sqlite3_progress_handler(db.Handle, 1000, _ => ct.IsCancellationRequested ? 1 : 0, null);

    public static DatabaseCatalog Inspect(string path, CancellationToken ct)
    {
        using var db = Open(path); WatchCancellation(db, ct); ct.ThrowIfCancellationRequested();
        var catalog = new DatabaseCatalog { SizeBytes = checked(Convert.ToInt64(Scalar(db, "PRAGMA page_count")) * Convert.ToInt64(Scalar(db, "PRAGMA page_size"))) };
        bool hasDbStat = Rows(db, "SELECT name FROM pragma_module_list WHERE name='dbstat'").Count != 0;
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        if (hasDbStat) foreach (var row in Rows(db, "SELECT s.tbl_name, sum(b.pgsize) FROM dbstat b JOIN sqlite_schema s ON s.name=b.name GROUP BY s.tbl_name")) sizes[row[0]] = long.Parse(row[1], CultureInfo.InvariantCulture);
        var declarations = Rows(db, "SELECT name,sql FROM sqlite_schema WHERE type='table' AND rootpage=0").ToDictionary(r => r[0], r => r[1], StringComparer.Ordinal);
        foreach (var row in Rows(db, "SELECT s.name,s.type,p.type FROM sqlite_schema s JOIN pragma_table_list p ON p.name=s.name AND p.schema='main' WHERE s.type IN ('table','view') AND p.type<>'shadow' AND substr(s.name,1,7)<>'sqlite_' ORDER BY s.name"))
        {
            ct.ThrowIfCancellationRequested();
            var info = new TableInfo { Name = row[0], ObjectType = row[1], SizeBytes = row[1] == "view" ? 0 : row[2] == "virtual" || !hasDbStat ? null : sizes.GetValueOrDefault(row[0]) };
            var literal = row[0].Replace("'", "''", StringComparison.Ordinal);
            foreach (var fk in Rows(db, $"SELECT \"table\" FROM pragma_foreign_key_list('{literal}')")) info.RequiredTables.Add(new("main", fk[0]));
            catalog.Tables.Add(info);
        }
        var owners = Rows(db, "SELECT tbl_name FROM sqlite_schema WHERE type='trigger'").Select(r => r[0]).ToHashSet(StringComparer.Ordinal);
        foreach (var table in catalog.Tables)
        {
            ct.ThrowIfCancellationRequested();
            table.RequiredTables = table.RequiredTables.Select(r => catalog.Tables.FirstOrDefault(t => AsciiFold(t.Name) == AsciiFold(r.Name))?.Id ?? r).ToList();
            if (declarations.TryGetValue(table.Name, out var sql))
            {
                var spec = VirtualDeclaration(sql);
                if (!spec.Known) table.DependencyInspectionError = "Unknown virtual table dependencies";
                if (spec.Dependency is { } dependency) table.RequiredTables.Add(catalog.Tables.FirstOrDefault(t => AsciiFold(t.Name) == AsciiFold(dependency.Name) && AsciiFold(t.Schema) == AsciiFold(dependency.Schema))?.Id ?? dependency);
            }
            var statements = new List<string>(); var name = Quote(table.Schema) + "." + Quote(table.Name);
            if (table.ObjectType == "view") statements.Add($"SELECT * FROM {name} LIMIT 0");
            if (owners.Contains(table.Name))
            {
                List<string[]> columns;
                try { columns = Rows(db, $"SELECT name FROM pragma_table_xinfo('{table.Name.Replace("'", "''")}') WHERE hidden=0"); }
                catch (SqliteException ex) { table.DependencyInspectionError = ex.Message; columns = []; }
                statements.Add($"EXPLAIN INSERT INTO {name} DEFAULT VALUES"); statements.Add($"EXPLAIN DELETE FROM {name} WHERE 0");
                if (columns.Count > 0) statements.Add($"EXPLAIN UPDATE {name} SET {string.Join(',', columns.Select(c => Quote(c[0]) + "=" + Quote(c[0])))} WHERE 0");
            }
            var captured = new HashSet<TableReference>();
            raw.sqlite3_set_authorizer(db.Handle, (_, action, first, second, database, source) =>
            {
                if (source.utf8_to_string() != null && action is raw.SQLITE_READ or raw.SQLITE_INSERT or raw.SQLITE_UPDATE or raw.SQLITE_DELETE)
                { var target = first.utf8_to_string(); if (target != null) captured.Add(new(database.utf8_to_string() ?? "main", target)); }
                return raw.SQLITE_OK;
            }, null);
            try
            {
                foreach (var statement in statements)
                {
                    ct.ThrowIfCancellationRequested();
                    try { using var command = db.CreateCommand(); command.CommandText = statement; command.Prepare(); }
                    catch (SqliteException ex) { if (!(table.ObjectType == "view" && ex.Message.Contains("because it is a view", StringComparison.Ordinal))) table.DependencyInspectionError = ex.Message; }
                }
            }
            finally { raw.sqlite3_set_authorizer(db.Handle, (delegate_authorizer)null!, null); }
            table.RequiredTables = table.RequiredTables.Concat(captured).Where(r => r != table.Id).Distinct().ToList();
        }
        catalog.Validate(); return catalog;
    }

    public static async Task CreateAsync(BackupJob job, string target, IProgress<BackupProgress>? progress, Action<string>? log, CancellationToken ct)
    {
        using (var source = Open(job.SourcePath))
        using (var destination = Open(target, writable: true))
        using (var backup = raw.sqlite3_backup_init(destination.Handle, "main", source.Handle, "main"))
        {
            if (backup == null) throw new IOException("SQLite could not initialize its consistent snapshot.");
            int retries = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested(); var code = raw.sqlite3_backup_step(backup, 256);
                if (code == raw.SQLITE_DONE) break;
                if (code is raw.SQLITE_BUSY or raw.SQLITE_LOCKED)
                { if (++retries > 50) throw new IOException("SQLite remained locked for more than five seconds."); await Task.Delay(100, ct); }
                else if (code != raw.SQLITE_OK) throw new IOException($"SQLite snapshot failed: {code}");
                else retries = 0;
                var pages = raw.sqlite3_backup_pagecount(backup); var remaining = raw.sqlite3_backup_remaining(backup);
                progress?.Report(new(.1 + .45 * (pages - remaining) / Math.Max(pages, 1d), L10n.Text("engine.sqlite_created")));
            }
        }
        ct.ThrowIfCancellationRequested();
        if (job.ExcludedDatabaseTables.Count > 0)
        {
            var catalog = Inspect(target, ct); var error = catalog.SelectionError(job.ExcludedDatabaseTables); if (error != null) throw new InvalidDataException(error);
            var excluded = catalog.EffectiveExclusions(job.ExcludedDatabaseTables); var kept = catalog.Tables.Where(t => t.ObjectType == "table" && !excluded.Contains(t.Id)).ToList();
            using var db = Open(target, true); WatchCancellation(db, ct);
            var rowIds = new Dictionary<string, (string Alias, string File)>();
            foreach (var table in kept)
            {
                var alias = RowIdAlias(db, table.Name);
                if (alias != null)
                {
                    var file = Path.Combine(Path.GetDirectoryName(target)!, "rowids-" + Guid.NewGuid() + ".bin");
                    WriteRowIds(db, table.Name, alias, file, ct); rowIds.Add(table.Name, (alias, file));
                }
            }
            var before = kept.ToDictionary(t => t.Id, t => Fingerprint(db, t.Name, ct));
            using (var transaction = db.BeginTransaction())
            {
                foreach (var table in catalog.Tables.Where(t => excluded.Contains(t.Id)).OrderBy(t => t.ObjectType == "view" ? 0 : 1))
                { ct.ThrowIfCancellationRequested(); using var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = $"DROP { (table.ObjectType == "view" ? "VIEW" : "TABLE") } {Quote(table.Schema)}.{Quote(table.Name)}"; command.ExecuteNonQuery(); }
                transaction.Commit();
            }
            Execute(db, "VACUUM;"); ct.ThrowIfCancellationRequested();
            foreach (var pair in rowIds)
            {
                RestoreRowIds(db, pair.Key, pair.Value.Alias, pair.Value.File, ct);
                File.Delete(pair.Value.File);
            }
            foreach (var table in kept)
            {
                if (Fingerprint(db, table.Name, ct) != before[table.Id]) throw new InvalidDataException("Retained SQLite table data changed.");
                if (Rows(db, $"PRAGMA foreign_key_check({Quote(table.Name)})").Count > 0) throw new InvalidDataException("Retained SQLite foreign-key dependency is invalid.");
            }
            log?.Invoke($"{L10n.Text("database.excluded_tables")}: {excluded.Count}");
        }
        using (var verify = Open(target))
        {
            WatchCancellation(verify, ct); var result = Convert.ToString(Scalar(verify, "PRAGMA integrity_check"));
            if (result != "ok") throw new InvalidDataException("SQLite integrity check failed: " + result);
        }
        ct.ThrowIfCancellationRequested(); log?.Invoke(L10n.Text("engine.sqlite_created"));
    }

    private static string Fingerprint(SqliteConnection db, string table, CancellationToken ct)
    {
        var rowid = RowIdAlias(db, table);
        using var command = db.CreateCommand(); command.CommandText = $"SELECT {(rowid == null ? "" : Quote(rowid) + ",")}* FROM {Quote(table)}"; using var reader = command.ExecuteReader();
        long count = 0; BigInteger sum = 0; var modulus = BigInteger.One << 256;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested(); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.GetValue(i); byte marker; byte[] bytes;
                switch (value)
                {
                    case DBNull: marker = 0; bytes = []; break;
                    case long integer: marker = 1; bytes = BitConverter.GetBytes(integer); break;
                    case double real: marker = 2; bytes = BitConverter.GetBytes(real); break;
                    case byte[] blob: marker = 3; bytes = blob; break;
                    default: marker = 4; bytes = Encoding.UTF8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture)!); break;
                }
                hash.AppendData([marker]); hash.AppendData(BitConverter.GetBytes((long)bytes.Length)); hash.AppendData(bytes);
            }
            sum = (sum + new BigInteger(hash.GetHashAndReset(), isUnsigned: true)) % modulus; count++;
        }
        return count.ToString(CultureInfo.InvariantCulture) + ":" + sum.ToString("X", CultureInfo.InvariantCulture);
    }
    private static string? RowIdAlias(SqliteConnection db, string table)
    {
        var literal = table.Replace("'", "''");
        if (Convert.ToInt64(Scalar(db, $"SELECT wr FROM pragma_table_list WHERE schema='main' AND name='{literal}'")) != 0) return null;
        var names = Rows(db, $"SELECT name FROM pragma_table_xinfo('{literal}')").Select(r => AsciiFold(r[0])).ToHashSet();
        return new[] { "rowid", "_rowid_", "oid" }.FirstOrDefault(name => !names.Contains(name));
    }
    private static void WriteRowIds(SqliteConnection db, string table, string alias, string path, CancellationToken ct)
    {
        using var output = new BinaryWriter(File.Create(path)); using var command = db.CreateCommand(); command.CommandText = $"SELECT {Quote(alias)} FROM {Quote(table)} ORDER BY {Quote(alias)}"; using var reader = command.ExecuteReader();
        while (reader.Read()) { ct.ThrowIfCancellationRequested(); output.Write(reader.GetInt64(0)); }
    }
    private static void RestoreRowIds(SqliteConnection db, string table, string alias, string originalFile, CancellationToken ct)
    {
        var currentFile = originalFile + ".current"; WriteRowIds(db, table, alias, currentFile, ct);
        try
        {
            var count = new FileInfo(originalFile).Length / 8;
            if (new FileInfo(currentFile).Length / 8 != count) throw new InvalidDataException("SQLite row count changed during compaction.");
            using var old = new BinaryReader(File.OpenRead(originalFile)); using var current = new BinaryReader(File.OpenRead(currentFile));
            bool equal = true; for (long i = 0; i < count; i++) { ct.ThrowIfCancellationRequested(); if (old.ReadInt64() != current.ReadInt64()) equal = false; }
            if (equal) return;
            old.BaseStream.Position = current.BaseStream.Position = 0;
            // Find an unused contiguous range outside both the original and current IDs.
            BigInteger previous = (BigInteger)long.MinValue - 1; long? a = count > 0 ? old.ReadInt64() : null; long? b = count > 0 ? current.ReadInt64() : null; BigInteger first = 0;
            while (a != null || b != null)
            {
                var value = a == null ? b!.Value : b == null ? a.Value : Math.Min(a.Value, b.Value);
                if ((BigInteger)value - previous - 1 >= count) { first = previous + 1; break; }
                previous = value;
                if (a == value) a = old.BaseStream.Position < old.BaseStream.Length ? old.ReadInt64() : null;
                if (b == value) b = current.BaseStream.Position < current.BaseStream.Length ? current.ReadInt64() : null;
                if (a == null && b == null) { first = previous + 1; if (first + count - 1 > long.MaxValue) throw new InvalidDataException("Cannot preserve SQLite row IDs."); }
            }
            old.BaseStream.Position = current.BaseStream.Position = 0;
            raw.sqlite3_db_config(db.Handle, raw.SQLITE_DBCONFIG_ENABLE_TRIGGER, 0, out int triggerSetting);
            try
            {
                using var transaction = db.BeginTransaction(); using var update = db.CreateCommand(); update.Transaction = transaction; update.CommandText = $"UPDATE {Quote(table)} SET {Quote(alias)}=$new WHERE {Quote(alias)}=$old"; var target = update.Parameters.Add("$new", SqliteType.Integer); var source = update.Parameters.Add("$old", SqliteType.Integer);
                for (long i = 0; i < count; i++) { ct.ThrowIfCancellationRequested(); source.Value = current.ReadInt64(); target.Value = checked((long)(first + i)); if (update.ExecuteNonQuery() != 1) throw new InvalidDataException("Row-ID preservation failed."); }
                for (long i = 0; i < count; i++) { ct.ThrowIfCancellationRequested(); source.Value = checked((long)(first + i)); target.Value = old.ReadInt64(); if (update.ExecuteNonQuery() != 1) throw new InvalidDataException("Row-ID restoration failed."); }
                transaction.Commit();
            }
            finally { raw.sqlite3_db_config(db.Handle, raw.SQLITE_DBCONFIG_ENABLE_TRIGGER, 1, out _); }
        }
        finally { File.Delete(currentFile); }
    }
    private static string AsciiFold(string value) => string.Concat(value.Select(c => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c));
    private sealed record Token(string Value, bool Punctuation = false);
    private static (bool Known, TableReference? Dependency) VirtualDeclaration(string sql)
    {
        var tokens = new List<Token>(); int pos = 0;
        while (pos < sql.Length)
        {
            var c = sql[pos]; if (char.IsWhiteSpace(c)) { pos++; continue; }
            if (pos + 1 < sql.Length && sql.AsSpan(pos, 2).SequenceEqual("--")) { while (pos < sql.Length && sql[pos] != '\n') pos++; continue; }
            if (pos + 1 < sql.Length && sql.AsSpan(pos, 2).SequenceEqual("/*")) { var end = sql.IndexOf("*/", pos + 2, StringComparison.Ordinal); pos = end < 0 ? sql.Length : end + 2; continue; }
            if ("\"'`[".Contains(c))
            {
                var end = c == '[' ? ']' : c; pos++; var value = new StringBuilder();
                while (pos < sql.Length) { c = sql[pos++]; if (c == end) { if (end != ']' && pos < sql.Length && sql[pos] == end) pos++; else break; } value.Append(c); }
                tokens.Add(new(value.ToString())); continue;
            }
            if ("(),=.".Contains(c)) { tokens.Add(new(c.ToString(), true)); pos++; continue; }
            var start = pos;
            while (pos < sql.Length && !char.IsWhiteSpace(sql[pos]) && !"(),=.\"'`[".Contains(sql[pos]))
            { if (pos + 1 < sql.Length && (sql.AsSpan(pos, 2).SequenceEqual("--") || sql.AsSpan(pos, 2).SequenceEqual("/*"))) break; pos++; }
            if (pos == start) pos++; tokens.Add(new(sql[start..pos]));
        }
        var usingIndex = tokens.FindIndex(t => t.Value.Equals("USING", StringComparison.OrdinalIgnoreCase));
        if (usingIndex < 0 || usingIndex + 2 >= tokens.Count || tokens[usingIndex + 2] != new Token("(", true)) return (false, null);
        var module = tokens[usingIndex + 1].Value.ToLowerInvariant(); var args = new List<List<Token>>(); var current = new List<Token>(); int depth = 0;
        foreach (var token in tokens.Skip(usingIndex + 3))
        {
            if (token == new Token("(", true)) depth++;
            if (token == new Token(")", true)) { if (depth == 0) { args.Add(current); break; } depth--; }
            if (token == new Token(",", true) && depth == 0) { args.Add(current); current = []; } else current.Add(token);
        }
        if (module is "fts3" or "fts4" or "fts5")
        {
            var content = args.FirstOrDefault(a => a.Count >= 3 && a[0].Value.Equals("content", StringComparison.OrdinalIgnoreCase) && a[1] == new Token("=", true));
            if (content == null) return (true, null);
            return content.Count == 3 ? (true, content[2].Value.Length == 0 ? null : new("main", content[2].Value)) : (false, null);
        }
        if (module is "fts5vocab" or "fts4aux")
        {
            var usesDatabase = args.Count == (module == "fts5vocab" ? 3 : 2); var tableIndex = usesDatabase ? 1 : 0;
            if (args.Count <= tableIndex || args[tableIndex].Count != 1 || (usesDatabase && args[0].Count != 1)) return (false, null);
            return (true, new(usesDatabase ? args[0][0].Value : "main", args[tableIndex][0].Value));
        }
        return (module is "rtree" or "rtree_i32" or "fts3tokenize", null);
    }
}
