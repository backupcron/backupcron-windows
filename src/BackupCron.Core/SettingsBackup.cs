using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackupCron.Core;

public sealed class SettingsArchive
{
    public const string FormatName = "BackupCron.Windows.settings";
    [JsonRequired] public string Format { get; set; } = FormatName;
    [JsonRequired] public int Version { get; set; } = 1;
    [JsonRequired] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonRequired] public List<BackupJob> Jobs { get; set; } = [];
    [JsonRequired] public AppSettings Settings { get; set; } = new();

    public static SettingsArchive Create(IEnumerable<BackupJob> jobs, AppSettings settings) => new()
    {
        Jobs = jobs.Select(j => { var copy = j.Clone(); copy.LastRunAt = null; copy.LastSpaceEstimate = null; return copy; }).ToList(),
        Settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, Json.Options), Json.Options)!
    };

    public void Validate()
    {
        if (Format != FormatName || Version != 1 || Jobs == null || Settings == null
            || Jobs.Any(j => j == null) || Jobs.Select(j => j.Id).Distinct().Count() != Jobs.Count
            || Jobs.Any(j => j.Id == Guid.Empty || !Enum.IsDefined(j.Kind) || !Enum.IsDefined(j.Schedule)
                || !Enum.IsDefined(j.ConnectionMode) || string.IsNullOrWhiteSpace(j.Name)
                || string.IsNullOrWhiteSpace(j.DestinationPath) || string.IsNullOrWhiteSpace(j.NameMask)
                || j.SourcePath == null || j.Host == null || j.Username == null || j.DatabaseName == null
                || j.ProjectExclusions == null || j.SshHost == null || j.SshUsername == null || j.SshIdentityPath == null
                || j.ClientExecutablePath == null || j.DockerContainer == null || j.ExcludedDatabaseTables == null
                || j.ExcludedDatabaseTables.Any(t => t == null || string.IsNullOrEmpty(t.Schema) || string.IsNullOrEmpty(t.Name))
                || j.RetentionCount is < 0 or > 100000
                || j.Port is < 1 or > 65535 || j.SshPort is < 1 or > 65535
                || (j.Schedule is ScheduleKind.Daily or ScheduleKind.Weekly && !TimeOnly.TryParseExact(j.ScheduledTime, "HH:mm", out _)))
            || !double.IsFinite(Settings.SoundVolume) || Settings.SoundVolume is < 0 or > 1
            || !double.IsFinite(Settings.StorageWarningGiB) || Settings.StorageWarningGiB is < 0 or > 1000000
            || !double.IsFinite(Settings.StorageWarningPercent) || Settings.StorageWarningPercent is < 0 or > 100
            || Settings.Language == null || (Settings.Language != "auto" && !L10n.AvailableLanguages.Contains(Settings.Language)))
            throw new InvalidDataException(L10n.Text("settings_backup.invalid"));
    }

    public static SettingsArchive Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 20 * 1024 * 1024) throw new InvalidDataException(L10n.Text("settings_backup.invalid"));
        var archive = JsonSerializer.Deserialize<SettingsArchive>(file, Json.Options)
            ?? throw new InvalidDataException(L10n.Text("settings_backup.invalid"));
        archive.Validate();
        return archive;
    }

    public string Write(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new IOException(L10n.Text("settings_backup.choose_folder"));
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var path = Path.Combine(directory, $"BackupCron-settings-{CreatedAt:yyyy-MM-ddTHH-mm-ss}-{Guid.NewGuid():N}.json");
        var temporary = path + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(file, this, Json.Options); file.Flush(true); }
            File.Move(temporary, path);
            return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class SettingsBackupQueue
{
    private readonly object sync = new();
    private Task pending = Task.CompletedTask;
    private long revision;
    private string? lastContent;
    private string? lastDirectory;
    public string? Error { get; private set; }
    public string? LastPath { get; private set; }
    public Task Completion { get { lock (sync) return pending; } }

    public void Schedule(SettingsArchive archive)
    {
        var directory = archive.Settings.SettingsBackupDirectory;
        var content = JsonSerializer.Serialize(new { archive.Jobs, archive.Settings }, Json.Options);
        lock (sync)
        {
            var current = ++revision;
            if (!archive.Settings.SettingsBackupEnabled) { Error = null; return; }
            pending = pending.ContinueWith(_ =>
            {
                lock (sync) { if (current != revision || (lastDirectory == directory && lastContent == content)) return; }
                try
                {
                    var saved = archive.Write(directory);
                    lock (sync)
                    {
                        if (current != revision) return;
                        LastPath = saved; lastContent = content; lastDirectory = directory; Error = null;
                    }
                }
                catch (Exception ex) { lock (sync) { if (current == revision) Error = ex.Message; } }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }
}
