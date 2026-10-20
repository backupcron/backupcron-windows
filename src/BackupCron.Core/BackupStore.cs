using System.Collections.Concurrent;
using System.Text.Json;

namespace BackupCron.Core;

public interface ISecretStore
{
    string Get(Guid id);
    void Set(Guid id, string value);
    void Remove(Guid id);
}
public sealed class MemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<Guid, string> values = new();
    public string Get(Guid id) => values.GetValueOrDefault(id) ?? "";
    public void Set(Guid id, string value) => values[id] = value;
    public void Remove(Guid id) => values.TryRemove(id, out _);
}
public sealed class RunningBackup : IDisposable
{
    public CancellationTokenSource Cancellation { get; } = new();
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public BackupProgress Progress { get; set; } = new(0, "");
    public ConcurrentQueue<string> Logs { get; } = new();
    public Task? Task { get; set; }
    public void Dispose() => Cancellation.Dispose();
}
public sealed class BackupStore
{
    private readonly object sync = new();
    private PersistedState state;
    private readonly string path;
    private readonly ISecretStore secrets;
    private readonly BackupEngine engine;
    public StorageMonitor Storage { get; }
    public SettingsBackupQueue SettingsBackups { get; } = new();
    public ConcurrentDictionary<Guid, RunningBackup> Running { get; } = new();
    private string? stateError;
    public string? Error { get => stateError ?? SettingsBackups.Error; private set => stateError = value; }
    public bool ReadOnly { get; }
    public event Action<BackupRun>? Finished;
    public BackupStore(string directory, ISecretStore secrets, IStorageProbe? storageProbe = null)
    {
        this.secrets = secrets; Storage = new(storageProbe, directory); engine = new(Storage); Directory.CreateDirectory(directory); path = Path.Combine(directory, "state.json");
        if (File.Exists(path))
        {
            try
            {
                state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(path), Json.Options) ?? throw new InvalidDataException("State is empty.");
                ValidateStorageSettings(state.Settings);
                if (state.Version > 1 || state.Jobs == null || state.History == null || state.Settings == null || state.Jobs.Select(j => j.Id).Distinct().Count() != state.Jobs.Count) throw new InvalidDataException("Unsupported or malformed application state.");
            }
            catch (Exception ex) { state = new(); ReadOnly = true; Error = L10n.Text("store.state_save_failed", ex.Message); }
        }
        else state = new();
        L10n.Language = state.Settings.Language;
        if (!ReadOnly) SettingsBackups.Schedule(SettingsArchive.Create(state.Jobs, state.Settings));
    }
    public PersistedState Snapshot()
    { lock (sync) return new() { Jobs = state.Jobs.Select(j => j.Clone()).ToList(), History = state.History.ToList(), Settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(state.Settings, Json.Options), Json.Options)! }; }
    public string Password(Guid id) => secrets.Get(id);
    public Task<string> BackupSettingsAsync(string directory, AppSettings? settings = null)
    {
        SettingsArchive archive;
        lock (sync) { EnsureWritable(); archive = SettingsArchive.Create(state.Jobs, settings ?? state.Settings); }
        return Task.Run(() => archive.Write(directory));
    }

    public void RestoreSettings(SettingsArchive archive)
    {
        archive.Validate();
        lock (sync)
        {
            EnsureWritable();
            if (!Running.IsEmpty) throw new InvalidOperationException(L10n.Text("settings_backup.running"));
            var restored = SettingsArchive.Create(archive.Jobs, archive.Settings);
            restored.Settings.SettingsBackupEnabled = state.Settings.SettingsBackupEnabled;
            restored.Settings.SettingsBackupDirectory = state.Settings.SettingsBackupDirectory;
            restored.Settings.SchedulerEnabled = false;
            var recovery = Path.Combine(Path.GetDirectoryName(path)!, "Settings recovery");
            Directory.CreateDirectory(recovery);
            SettingsArchive.Create(state.Jobs, state.Settings).Write(recovery);
            Persist(restored.Jobs, state.History, restored.Settings);
            state.Jobs = restored.Jobs; state.Settings = restored.Settings; L10n.Language = state.Settings.Language;
        }
    }
    public void Save(BackupJob job, string? password = null)
    {
        lock (sync)
        {
            EnsureWritable(); if (Running.ContainsKey(job.Id)) throw new InvalidOperationException(L10n.Text("job.running"));
            var jobs = state.Jobs.Select(j => j.Clone()).ToList(); var index = jobs.FindIndex(j => j.Id == job.Id);
            if (index < 0) jobs.Add(job.Clone()); else jobs[index] = job.Clone();
            var oldPassword = password == null ? "" : secrets.Get(job.Id);
            if (password != null) secrets.Set(job.Id, password);
            try { Persist(jobs, state.History, state.Settings); state.Jobs = jobs; }
            catch { if (password != null) secrets.Set(job.Id, oldPassword); throw; }
        }
    }
    public void SaveSettings(AppSettings settings)
    { lock (sync) { EnsureWritable(); ValidateStorageSettings(settings); if (settings.SettingsBackupEnabled && (string.IsNullOrWhiteSpace(settings.SettingsBackupDirectory) || !Path.IsPathFullyQualified(settings.SettingsBackupDirectory))) throw new ArgumentException(L10n.Text("settings_backup.choose_folder")); settings.SoundVolume = double.IsFinite(settings.SoundVolume) ? Math.Clamp(settings.SoundVolume, 0, 1) : .35; Persist(state.Jobs, state.History, settings); state.Settings = settings; L10n.Language = settings.Language; } }
    public void Delete(Guid id)
    {
        lock (sync)
        {
            EnsureWritable(); if (Running.ContainsKey(id)) throw new InvalidOperationException(L10n.Text("job.running"));
            var jobs = state.Jobs.Where(j => j.Id != id).ToList(); Persist(jobs, state.History, state.Settings); state.Jobs = jobs; secrets.Remove(id);
        }
    }
    public void SetEnabled(Guid id, bool enabled)
    { lock (sync) { var job = state.Jobs.First(j => j.Id == id).Clone(); job.IsEnabled = enabled; Save(job); } }
    public void Tick(DateTimeOffset now)
    {
        var snapshot = Snapshot(); if (ReadOnly || !snapshot.Settings.SchedulerEnabled) return;
        foreach (var job in snapshot.Jobs.Where(j => Scheduling.IsDue(j, now))) Run(job.Id);
    }
    public Task Run(Guid id)
    {
        BackupJob job; RunningBackup activity;
        lock (sync)
        {
            EnsureWritable(); job = state.Jobs.First(j => j.Id == id).Clone(); activity = new();
            if (!Running.TryAdd(id, activity)) { activity.Dispose(); return Running.TryGetValue(id, out var existing) ? existing.Task ?? Task.CompletedTask : Task.CompletedTask; }
        }
        activity.Task = ExecuteAsync(job, activity); return activity.Task;
    }
    private async Task ExecuteAsync(BackupJob job, RunningBackup activity)
    {
        string? output = null; long? size = null; RunStatus status; string message;
        try
        {
            var password = secrets.Get(job.Id); var progress = new InlineProgress(value => activity.Progress = value);
            var result = await engine.RunAsync(job, password, progress, text => { activity.Logs.Enqueue(text); while (activity.Logs.Count > 500) activity.Logs.TryDequeue(out _); }, activity.Cancellation.Token);
            output = result.OutputPath; size = result.SizeBytes; message = result.Message; status = RunStatus.Success;
        }
        catch (OperationCanceledException) { message = L10n.Text("ux.last_cancelled"); status = RunStatus.Cancelled; }
        catch (Exception ex) { message = ex.Message; status = RunStatus.Failure; }
        var run = new BackupRun(Guid.NewGuid(), job.Id, job.Name, activity.StartedAt, DateTimeOffset.Now, status, output, size, message, activity.Logs.ToList());
        lock (sync)
        {
            try
            {
                var jobs = state.Jobs.Select(j => j.Clone()).ToList(); var current = jobs.FirstOrDefault(j => j.Id == job.Id); if (current != null) { current.LastRunAt = run.FinishedAt; var estimate = Storage.Remembered(job); if (estimate?.Matches(current) == true) current.LastSpaceEstimate = estimate; }
                var history = state.History.Prepend(run).Take(10000).ToList(); Persist(jobs, history, state.Settings); state.Jobs = jobs; state.History = history;
                if (status == RunStatus.Success) Prune(job, history, jobs);
            }
            catch (Exception ex) { Error = L10n.Text("store.state_save_failed", ex.Message); }
        }
        Running.TryRemove(job.Id, out _); activity.Dispose(); Finished?.Invoke(run);
    }
    public void Cancel(Guid id) { if (Running.TryGetValue(id, out var activity)) { try { activity.Cancellation.Cancel(); } catch (ObjectDisposedException) { } } }
    public void CancelAll() { foreach (var id in Running.Keys) Cancel(id); }
    private static void Prune(BackupJob job, List<BackupRun> history, List<BackupJob> jobs)
    {
        if (job.RetentionCount <= 0) return;
        var destination = BackupPaths.Resolve(job.DestinationPath);
        var copies = history.Where(r => r.JobId == job.Id && r.Status == RunStatus.Success && r.OutputPath != null)
            .Where(r => string.Equals(Path.GetDirectoryName(Path.GetFullPath(r.OutputPath!)), Path.GetFullPath(job.DestinationPath), BackupPaths.Comparison))
            .Select(r => r.OutputPath!).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Where(BackupPaths.Exists).ToList();
        foreach (var old in copies.Skip(job.RetentionCount))
        {
            if (!BackupPaths.Inside(BackupPaths.Resolve(old), destination) || BackupPaths.Resolve(old).Equals(destination, BackupPaths.Comparison)) continue;
            if (jobs.Where(j => j.SourcePath.Length > 0 && j.Kind is BackupKind.Files or BackupKind.Project or BackupKind.Sqlite).Any(j => BackupPaths.Inside(BackupPaths.Resolve(j.SourcePath), BackupPaths.Resolve(old)))) continue;
            FileSystemInfo info = Directory.Exists(old) ? new DirectoryInfo(old) : new FileInfo(old);
            if (info.LinkTarget != null) continue;
            BackupPaths.DeleteOwnWork(old);
        }
    }
    private static void ValidateStorageSettings(AppSettings settings)
    {
        if (!double.IsFinite(settings.StorageWarningGiB) || settings.StorageWarningGiB < 0 || settings.StorageWarningGiB > 1000000 || !double.IsFinite(settings.StorageWarningPercent) || settings.StorageWarningPercent < 0 || settings.StorageWarningPercent > 100) throw new ArgumentException(L10n.Text("storage.invalid_threshold"));
    }
    private void EnsureWritable() { if (ReadOnly) throw new IOException(Error); }
    private void Persist(List<BackupJob> jobs, List<BackupRun> history, AppSettings settings)
    {
        var temporary = path + "." + Guid.NewGuid() + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(file, new PersistedState { Jobs = jobs, History = history, Settings = settings }, Json.Options); file.Flush(true); }
            File.Move(temporary, path, overwrite: true);
            SettingsBackups.Schedule(SettingsArchive.Create(jobs, settings));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private sealed class InlineProgress(Action<BackupProgress> report) : IProgress<BackupProgress>
    { public void Report(BackupProgress value) => report(value); }
}
