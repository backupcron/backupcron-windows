using System.Collections.Concurrent;

namespace BackupCron.Core;

public enum StorageLevel { Healthy, Warning, Critical, Unknown }
public sealed record BackupSpaceEstimate(string Identity, long? OutputBytes, long? PeakBytes, DateTimeOffset MeasuredAt)
{
    public bool Matches(BackupJob job) => Identity == job.SpaceIdentity() && OutputBytes is >= 0 && PeakBytes >= OutputBytes;
}
public sealed record StorageReading(string Identity, string Root, long? TotalBytes, long? FreeBytes, long? AvailableBytes, bool Network, DateTimeOffset CheckedAt, string? Error = null)
{
    public bool Known => Error == null && TotalBytes is >= 0 && FreeBytes is >= 0 && AvailableBytes is >= 0 && AvailableBytes <= TotalBytes;
    public static StorageReading Unknown(string path, string? error = null) => new(path, path, null, null, null, path.StartsWith(@"\\"), DateTimeOffset.Now, error ?? L10n.Text("storage.unavailable"));
}
public interface IStorageProbe
{
    IEnumerable<string> MachinePaths();
    StorageReading Read(string path);
}
public sealed class DriveStorageProbe : IStorageProbe
{
    public IEnumerable<string> MachinePaths() => [Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) ?? Path.GetPathRoot(Path.GetTempPath())!];
    public StorageReading Read(string path)
    {
        try
        {
            var full = BackupPaths.Resolve(path);
            var drive = DriveInfo.GetDrives().Where(d => full.Equals(Path.TrimEndingDirectorySeparator(d.Name), BackupPaths.Comparison) || full.StartsWith(d.Name, BackupPaths.Comparison)).OrderByDescending(d => d.Name.Length).First();
            return new(drive.Name, drive.Name, drive.TotalSize, drive.TotalFreeSpace, drive.AvailableFreeSpace, drive.DriveType == DriveType.Network, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return StorageReading.Unknown(path, ex.Message); }
    }
}
public sealed record StorageJobView(Guid Id, string Name, string Destination, StorageReading Reading, BackupSpaceEstimate? Estimate, StorageLevel Level, long? AfterOutputBytes);
public sealed record StorageVolumeView(StorageReading Reading, IReadOnlyList<string> Roles, IReadOnlyList<string> Paths, StorageLevel Level, long? NextPeakBytes, long? AfterOutputBytes, DateTimeOffset? LastSuccessfulCheck);
public sealed record StorageSnapshot(DateTimeOffset CheckedAt, IReadOnlyList<StorageVolumeView> Volumes, IReadOnlyList<StorageJobView> Jobs)
{
    public static StorageSnapshot Empty => new(DateTimeOffset.MinValue, [], []);
}
public sealed record StorageAlert(string Identity, StorageLevel Level, string Message);

// All filesystem and native calls happen on workers; a stuck network call is reused,
// rather than starting another native call on every timer tick.
public sealed class StorageMonitor
{
    private readonly IStorageProbe probe;
    private readonly string dataDirectory;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim refreshGate = new(1), probeSlots = new(6), estimateSlots = new(2);
    private readonly ConcurrentDictionary<string, Lazy<Task<StorageReading>>> reads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, StorageReading> successful = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<BackupSpaceEstimate?>> estimates = new();
    private readonly ConcurrentDictionary<Guid, BackupSpaceEstimate> observed = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> volumeLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly StorageAlertTracker alerts = new();
    public StorageSnapshot Current { get; private set; } = StorageSnapshot.Empty;
    public event Action<StorageSnapshot>? Changed;
    public event Action<StorageAlert>? Warning;
    public StorageMonitor(IStorageProbe? probe = null, string dataDirectory = "", TimeSpan? timeout = null)
    { this.probe = probe ?? new DriveStorageProbe(); this.dataDirectory = dataDirectory; this.timeout = timeout ?? TimeSpan.FromSeconds(3); }

    public async Task<StorageReading> ReadAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return StorageReading.Unknown(path);
        if (reads.TryGetValue(path, out var old) && old.Value.IsCompleted) reads.TryRemove(new(path, old));
        var task = reads.GetOrAdd(path, p => new Lazy<Task<StorageReading>>(() => ReadWorker(p))).Value;
        try { return await task.WaitAsync(timeout, ct); }
        catch (TimeoutException) { var last = successful.GetValueOrDefault(path); return StorageReading.Unknown(path, L10n.Text("storage.timeout")) with { Identity = last?.Identity ?? path, Root = last?.Root ?? path }; }
    }
    private async Task<StorageReading> ReadWorker(string path)
    {
        if (!await probeSlots.WaitAsync(timeout)) return StorageReading.Unknown(path, L10n.Text("storage.timeout"));
        try
        {
            var value = await Task.Run(() => probe.Read(path));
            if (!value.Known) { var last = successful.GetValueOrDefault(path); return value with { Identity = last?.Identity ?? value.Identity, Root = last?.Root ?? value.Root, TotalBytes = null, FreeBytes = null, AvailableBytes = null, Error = value.Error ?? L10n.Text("storage.unavailable") }; }
            successful[path] = value; return value;
        }
        catch (Exception ex) { return StorageReading.Unknown(path, ex.Message); }
        finally { probeSlots.Release(); }
    }
    public void Observe(BackupJob job, BackupSpaceEstimate estimate) { if (!estimate.Matches(job)) return; observed[job.Id] = estimate; if (!job.HasDatabaseSource) estimates[job.SpaceIdentity()] = Task.FromResult<BackupSpaceEstimate?>(estimate); }
    public BackupSpaceEstimate? Remembered(BackupJob job)
    {
        var candidates = new[] { observed.GetValueOrDefault(job.Id), job.LastSpaceEstimate };
        return candidates.Where(v => v?.Matches(job) == true).OrderByDescending(v => v!.MeasuredAt).FirstOrDefault();
    }
    public async Task<BackupSpaceEstimate?> EstimateAsync(BackupJob job, CancellationToken ct = default)
    {
        if (job.HasDatabaseSource) return Remembered(job);
        var key = job.SpaceIdentity();
        if (estimates.TryGetValue(key, out var old) && old.IsCompletedSuccessfully && old.Result is { } cached && DateTimeOffset.Now - cached.MeasuredAt > TimeSpan.FromMinutes(5)) estimates.TryRemove(new(key, old));
        var task = estimates.GetOrAdd(key, _ => EstimateWorker(job.Clone()));
        try
        {
            var result = await task.WaitAsync(timeout, ct);
            if (result == null) estimates.TryRemove(new(key, task));
            return result;
        }
        catch (TimeoutException) { return null; }
    }
    private async Task<BackupSpaceEstimate?> EstimateWorker(BackupJob job)
    {
        if (!await estimateSlots.WaitAsync(timeout)) return null;
        try { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); return await Task.Run(() => BackupFiles.Measure(job, deadline.Token)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or OperationCanceledException) { return null; }
        finally { estimateSlots.Release(); }
    }
    public async Task RefreshAsync(PersistedState state, CancellationToken ct = default)
    {
        if (!await refreshGate.WaitAsync(0, ct)) return;
        try
        {
            var targets = new List<(string Path, string Role)>();
            foreach (var path in await Task.Run(() => probe.MachinePaths().ToArray(), ct)) targets.Add((path, "storage.machine"));
            if (dataDirectory.Length > 0) targets.Add((dataDirectory, "storage.application"));
            targets.Add((Path.GetTempPath(), "storage.temporary"));
            foreach (var job in state.Jobs)
            {
                if (job.Kind is BackupKind.Files or BackupKind.Project or BackupKind.Sqlite && job.SourcePath.Length > 0) targets.Add((job.SourcePath, "storage.source"));
                if (job.DestinationPath.Length > 0) targets.Add((job.DestinationPath, "storage.destination"));
            }
            var paths = targets.Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var values = await Task.WhenAll(paths.Select(async p => (Path: p, Value: await ReadAsync(p, ct))));
            var lookup = values.ToDictionary(x => x.Path, x => x.Value, StringComparer.OrdinalIgnoreCase);
            var jobs = await Task.WhenAll(state.Jobs.Select(async j => EvaluateJob(j, lookup.GetValueOrDefault(j.DestinationPath) ?? StorageReading.Unknown(j.DestinationPath), await EstimateAsync(j, ct), state.Settings)));
            var volumes = values.GroupBy(x => x.Value.Identity, StringComparer.OrdinalIgnoreCase).Select(group =>
            {
                // Different paths to one volume can have different access rights/quotas.
                // If one relevant path fails, do not label the whole group healthy.
                var reading = group.Any(x => !x.Value.Known) ? group.First(x => !x.Value.Known).Value : group.OrderBy(x => x.Value.AvailableBytes).First().Value;
                var relevant = jobs.Where(j => j.Reading.Identity.Equals(group.Key, StringComparison.OrdinalIgnoreCase) && state.Jobs.Any(s => s.Id == j.Id && s.IsEnabled)).ToArray();
                var level = CapacityLevel(reading, state.Settings);
                foreach (var j in relevant) if (j.Level == StorageLevel.Critical || j.Level == StorageLevel.Warning && level == StorageLevel.Healthy) level = j.Level;
                var members = group.Select(x => x.Path).ToArray();
                var peak = relevant.Where(j => j.Estimate?.PeakBytes != null).Select(j => j.Estimate!.PeakBytes).DefaultIfEmpty(null).Max();
                var after = relevant.Select(j => j.AfterOutputBytes).Where(v => v != null).DefaultIfEmpty(null).Min();
                var last = members.Select(p => successful.GetValueOrDefault(p)?.CheckedAt).Where(v => v != null).DefaultIfEmpty(null).Max();
                return new StorageVolumeView(reading, targets.Where(t => members.Contains(t.Path, StringComparer.OrdinalIgnoreCase)).Select(t => t.Role).Distinct().ToArray(), members, level, peak, after, last);
            }).OrderBy(v => v.Reading.Root, StringComparer.CurrentCultureIgnoreCase).ToArray();
            Current = new(DateTimeOffset.Now, volumes, jobs); Changed?.Invoke(Current);
            foreach (var warning in alerts.Update(Current, state.Settings.StorageWarningsEnabled)) Warning?.Invoke(warning);
        }
        finally { refreshGate.Release(); }
    }
    public static StorageJobView EvaluateJob(BackupJob job, StorageReading reading, BackupSpaceEstimate? estimate, AppSettings settings)
    {
        if (estimate?.Matches(job) != true) estimate = null;
        long? after = reading.Known && estimate?.OutputBytes is { } size ? reading.AvailableBytes!.Value - size : null;
        var level = CapacityLevel(reading, settings);
        if (reading.Known && estimate?.PeakBytes is { } peak && reading.AvailableBytes < peak) level = StorageLevel.Critical;
        else if (after != null && BelowThreshold(after.Value, reading.TotalBytes!.Value, settings) && level == StorageLevel.Healthy) level = StorageLevel.Warning;
        if (level == StorageLevel.Healthy && estimate == null) level = StorageLevel.Unknown;
        return new(job.Id, job.Name, job.DestinationPath, reading, estimate, level, after);
    }
    public static bool BelowThreshold(long available, long total, AppSettings settings) => available < settings.StorageWarningGiB * 1073741824d || total > 0 && available / (double)total * 100 < settings.StorageWarningPercent;
    public static StorageLevel CapacityLevel(StorageReading value, AppSettings settings) => !value.Known ? StorageLevel.Unknown : value.AvailableBytes == 0 ? StorageLevel.Critical : BelowThreshold(value.AvailableBytes!.Value, value.TotalBytes!.Value, settings) ? StorageLevel.Warning : StorageLevel.Healthy;

    public async Task<IDisposable> AcquireAsync(string destination, CancellationToken ct = default)
    {
        var reading = await ReadAsync(destination, ct);
        var key = reading.Known || reading.Identity != destination ? reading.Identity : Path.GetPathRoot(Path.GetFullPath(destination)) ?? destination;
        var gate = volumeLocks.GetOrAdd(key, _ => new(1)); await gate.WaitAsync(ct);
        return new Lease(gate);
    }
    public async Task RequireAsync(string destination, long? bytes, Action<string>? log, CancellationToken ct)
    {
        var reading = await ReadAsync(destination, ct);
        if (!reading.Known) { log?.Invoke(L10n.Text("storage.check_unavailable", destination)); return; }
        if (bytes is > 0 && reading.AvailableBytes < bytes) throw new IOException(L10n.Text("database.space_insufficient", destination, L10n.Size(bytes), L10n.Size(reading.AvailableBytes)));
    }
    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    { private int disposed; public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) gate.Release(); } }
}

public sealed class StorageAlertTracker
{
    private readonly Dictionary<string, StorageLevel> levels = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<StorageAlert> Update(StorageSnapshot snapshot, bool enabled)
    {
        if (!enabled) { levels.Clear(); return []; }
        var result = new List<StorageAlert>(); var present = snapshot.Volumes.Select(v => v.Reading.Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var old in levels.Keys.Where(k => !present.Contains(k)).ToArray()) levels.Remove(old);
        foreach (var volume in snapshot.Volumes)
        {
            var value = volume.Reading; var level = volume.Level;
            var hadPrevious = levels.TryGetValue(value.Identity, out var previous); levels[value.Identity] = level;
            var worsened = !hadPrevious || previous == StorageLevel.Healthy || level == StorageLevel.Critical && previous != StorageLevel.Critical || level == StorageLevel.Unknown && previous != StorageLevel.Unknown || level == StorageLevel.Warning && previous == StorageLevel.Unknown;
            if (level == StorageLevel.Healthy || !worsened) continue;
            var key = level == StorageLevel.Unknown ? "storage.alert_unavailable" : level == StorageLevel.Critical ? "storage.alert_critical" : "storage.alert_low";
            result.Add(new(value.Identity, level, L10n.Text(key, value.Root, L10n.Size(value.AvailableBytes), L10n.Size(volume.NextPeakBytes))));
        }
        return result;
    }
}
