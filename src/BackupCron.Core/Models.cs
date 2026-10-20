using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackupCron.Core;

public enum BackupKind { Files, Project, Sqlite, Postgres, Mysql }
public enum ConnectionMode { Direct, Docker, Ssh }
public enum ScheduleKind { Manual, Interval, Hourly, Daily, Weekly }
public enum RunStatus { Success, Failure, Cancelled }

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

public sealed class BackupJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = L10n.Text("job.default_name");
    public BackupKind Kind { get; set; }
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public bool ArchiveOutput { get; set; } = true;
    public string NameMask { get; set; } = "{job}-{date}-{time}";
    public int RetentionCount { get; set; }
    public string ProjectExclusions { get; set; } = "node_modules, vendor, .build, DerivedData";
    public string DatabaseName { get; set; } = "";
    public List<TableReference> ExcludedDatabaseTables { get; set; } = [];
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Username { get; set; } = "";
    public ConnectionMode ConnectionMode { get; set; }
    public bool UseSsl { get; set; }
    public string ClientExecutablePath { get; set; } = "";
    public string DockerContainer { get; set; } = "";
    public string SshHost { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string SshUsername { get; set; } = "";
    public string SshIdentityPath { get; set; } = "";
    public ScheduleKind Schedule { get; set; }
    public int IntervalMinutes { get; set; } = 60;
    public string ScheduledTime { get; set; } = "02:00";
    public int Weekday { get; set; } = 2;
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastRunAt { get; set; }
    public BackupSpaceEstimate? LastSpaceEstimate { get; set; }
    [JsonIgnore] public bool HasDatabaseSource => Kind is BackupKind.Sqlite or BackupKind.Postgres or BackupKind.Mysql;
    [JsonIgnore] public string[] ExclusionNames => ProjectExclusions.Split([',', ';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    [JsonIgnore] public bool IncludesGitMetadata
    {
        get => !ExclusionNames.Contains(".git", StringComparer.OrdinalIgnoreCase);
        set => ProjectExclusions = string.Join(", ", value ? ExclusionNames.Where(x => !x.Equals(".git", StringComparison.OrdinalIgnoreCase)) : ExclusionNames.Append(".git").Distinct(StringComparer.OrdinalIgnoreCase));
    }
    public BackupJob Clone() => JsonSerializer.Deserialize<BackupJob>(JsonSerializer.Serialize(this, Json.Options), Json.Options)!;
    public string TargetIdentity() => Kind == BackupKind.Sqlite ? $"{Kind}|{SourcePath}" : JsonSerializer.Serialize(new object[] { Kind, ConnectionMode, Host, Port, DatabaseName, DockerContainer, SshHost, SshPort, SshUsername });
    public string SpaceIdentity() => JsonSerializer.Serialize(new object[] { ConnectionIdentity(), SourcePath, ArchiveOutput, ProjectExclusions, ExcludedDatabaseTables.OrderBy(t => t.Schema, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ToArray() });
    public string ConnectionIdentity() => Kind == BackupKind.Sqlite ? TargetIdentity() : TargetIdentity() + JsonSerializer.Serialize(new object[] { Username, UseSsl, ClientExecutablePath, SshIdentityPath });
}

// C# ordinal string equality preserves distinct Unicode-normalized SQL identifiers.
public sealed record TableReference(string Schema, string Name)
{
    [JsonIgnore] public string DisplayName => Schema + "." + Name;
}
public sealed class TableInfo
{
    public string Schema { get; set; } = "main";
    public string Name { get; set; } = "";
    public string ObjectType { get; set; } = "table";
    public long? SizeBytes { get; set; }
    public List<TableReference> RequiredTables { get; set; } = [];
    public List<TableReference> ParentTables { get; set; } = [];
    public string? DependencyInspectionError { get; set; }
    [JsonIgnore] public TableReference Id => new(Schema, Name);
}
public sealed class DatabaseCatalog
{
    public long? SizeBytes { get; set; }
    public List<TableInfo> Tables { get; set; } = [];
    public HashSet<TableReference> EffectiveExclusions(IEnumerable<TableReference> exclusions)
    {
        var result = exclusions.ToHashSet(); bool changed;
        do { changed = false; foreach (var t in Tables) if (!result.Contains(t.Id) && t.ParentTables.Any(result.Contains)) changed |= result.Add(t.Id); } while (changed);
        return result;
    }
    public string? SelectionError(IEnumerable<TableReference> exclusions)
    {
        var excluded = EffectiveExclusions(exclusions); var kept = Tables.Where(t => !excluded.Contains(t.Id)).ToList();
        if (Tables.Count > 0 && kept.Count == 0) return L10n.Text("database.select_one");
        foreach (var t in kept)
        {
            var dependency = t.RequiredTables.FirstOrDefault(excluded.Contains);
            if (dependency != null) return L10n.Text("database.required_table", dependency.DisplayName, t.Id.DisplayName);
            if (excluded.Count > 0 && t.DependencyInspectionError != null) return L10n.Text("database.dependencies_unknown", t.Id.DisplayName);
        }
        return null;
    }
    public long? SelectedSize(IEnumerable<TableReference> exclusions)
    {
        var excluded = EffectiveExclusions(exclusions); long sum = 0;
        try { foreach (var t in Tables.Where(t => !excluded.Contains(t.Id))) { if (t.SizeBytes is not >= 0) return null; sum = checked(sum + t.SizeBytes.Value); } } catch (OverflowException) { return null; }
        return sum;
    }
    public void Validate()
    {
        if (SizeBytes < 0 || Tables.Select(t => t.Id).Distinct().Count() != Tables.Count || Tables.Any(t => string.IsNullOrEmpty(t.Schema) || string.IsNullOrEmpty(t.Name) || t.Schema.Contains('\0') || t.Name.Contains('\0') || t.SizeBytes < 0)) throw new InvalidDataException(L10n.Text("database.invalid_response"));
    }
}

public sealed record BackupRun(Guid Id, Guid JobId, string JobName, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, RunStatus Status, string? OutputPath, long? OutputSizeBytes, string Message, List<string> Logs);
public sealed record BackupResult(string OutputPath, long SizeBytes, string Message);
public sealed record BackupProgress(double Fraction, string Message);
public sealed class AppSettings
{
    public bool SettingsBackupEnabled { get; set; }
    public string SettingsBackupDirectory { get; set; } = "";
    public bool SchedulerEnabled { get; set; } = true;
    public bool StorageWarningsEnabled { get; set; } = true;
    public double StorageWarningGiB { get; set; } = 20;
    public double StorageWarningPercent { get; set; } = 10;
    public bool NotificationsEnabled { get; set; }
    public bool NotifySuccess { get; set; } = true;
    public bool NotifyFailure { get; set; } = true;
    public bool SoundEnabled { get; set; }
    public double SoundVolume { get; set; } = .35;
    public string Language { get; set; } = "auto";
}
public sealed class PersistedState
{
    public int Version { get; set; } = 1;
    public List<BackupJob> Jobs { get; set; } = [];
    public List<BackupRun> History { get; set; } = [];
    public AppSettings Settings { get; set; } = new();
}
