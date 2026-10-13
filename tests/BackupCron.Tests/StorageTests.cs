using BackupCron.Core;
using System.Text.Json;

namespace BackupCron.Tests;

internal static class StorageTests
{
    private const long GiB = 1073741824;
    private static readonly AppSettings NoThresholds = new() { StorageWarningGiB = 0, StorageWarningPercent = 0 };
    private static StorageReading Reading(long available = 80 * GiB) => new("volume-a", "Volume A", 100 * GiB, available, available, false, DateTimeOffset.Now);
    private sealed class Probe(Func<string, StorageReading> read, params string[] machine) : IStorageProbe
    {
        public int Calls;
        public IEnumerable<string> MachinePaths() => machine;
        public StorageReading Read(string path) { Interlocked.Increment(ref Calls); return read(path); }
    }
    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        await check("root-directory containment covers UNC share roots and local roots", () =>
        {
            var root = Path.GetPathRoot(Path.GetTempPath())!; Program.Assert(BackupPaths.Inside(Path.Combine(root, "child"), root));
            if (OperatingSystem.IsWindows()) Program.Assert(BackupPaths.Inside(@"\\server\share\nested", @"\\server\share\"));
            Program.Assert(!BackupPaths.Inside(Path.Combine(root, "child-other"), Path.Combine(root, "child"))); return Task.CompletedTask;
        });
        await check("storage thresholds use both absolute and percentage space, including zero settings", () =>
        {
            var settings = new AppSettings { StorageWarningGiB = 20, StorageWarningPercent = 10 };
            Program.Assert(StorageMonitor.CapacityLevel(Reading(15 * GiB), settings) == StorageLevel.Warning);
            settings.StorageWarningGiB = 0; Program.Assert(StorageMonitor.CapacityLevel(Reading(9 * GiB), settings) == StorageLevel.Warning);
            Program.Assert(StorageMonitor.CapacityLevel(Reading(10 * GiB), settings) == StorageLevel.Healthy);
            settings.StorageWarningPercent = 0; Program.Assert(StorageMonitor.CapacityLevel(Reading(GiB), settings) == StorageLevel.Healthy);
            Program.Assert(StorageMonitor.CapacityLevel(Reading(0), settings) == StorageLevel.Critical); return Task.CompletedTask;
        });
        await check("storage next-backup budget checks peak and projected remaining capacity", () =>
        {
            var job = new BackupJob(); var estimate = new BackupSpaceEstimate(job.SpaceIdentity(), 5 * GiB, 15 * GiB, DateTimeOffset.Now);
            var reading = Reading(10 * GiB); var view = StorageMonitor.EvaluateJob(job, reading, estimate, NoThresholds);
            Program.Assert(view.Level == StorageLevel.Critical && view.AfterOutputBytes == 5 * GiB);
            view = StorageMonitor.EvaluateJob(job, Reading(30 * GiB), estimate, new() { StorageWarningGiB = 26, StorageWarningPercent = 0 });
            Program.Assert(view.Level == StorageLevel.Warning && view.AfterOutputBytes == 25 * GiB);
            job.ArchiveOutput = false; view = StorageMonitor.EvaluateJob(job, reading, estimate, NoThresholds); Program.Assert(view.Estimate == null && view.AfterOutputBytes == null); return Task.CompletedTask;
        });
        await check("quota availability controls capacity checks instead of physical free bytes", () =>
        {
            var reading = Reading(2 * GiB) with { FreeBytes = 80 * GiB, TotalBytes = 20 * GiB };
            Program.Assert(StorageMonitor.CapacityLevel(reading, new() { StorageWarningGiB = 3, StorageWarningPercent = 0 }) == StorageLevel.Warning);
            Program.Assert(!StorageReading.Unknown("offline").Known); Program.Assert(StorageMonitor.CapacityLevel(Reading(0) with { TotalBytes = 0 }, NoThresholds) == StorageLevel.Critical); return Task.CompletedTask;
        });
        await check("storage merges machine and multiple destination paths on one actual volume", async () =>
        {
            var folder = Program.Fixture(); var source = Path.Combine(folder, "source"); File.WriteAllText(source, "payload");
            var a = Program.Job(source, folder); var b = a.Clone(); b.Id = Guid.NewGuid(); b.DestinationPath = Path.Combine(folder, "other");
            var monitor = new StorageMonitor(new Probe(_ => Reading(), folder), folder);
            await monitor.RefreshAsync(new() { Jobs = [a, b], Settings = NoThresholds });
            Program.Assert(monitor.Current.Volumes.Count == 1 && monitor.Current.Jobs.Count == 2);
            Program.Assert(monitor.Current.Volumes[0].Roles.Contains("storage.machine") && monitor.Current.Volumes[0].Roles.Contains("storage.destination"));
            Program.Assert(monitor.Current.Jobs.All(j => j.Estimate?.PeakBytes > 0));
        });
        await check("an unavailable destination discards old free space and retains last-check identity", async () =>
        {
            var folder = Program.Fixture(); var job = new BackupJob { Kind = BackupKind.Postgres, DestinationPath = folder };
            var offline = false; var monitor = new StorageMonitor(new Probe(_ => offline ? StorageReading.Unknown(folder) : Reading(), folder)); var state = new PersistedState { Jobs = [job], Settings = NoThresholds };
            await monitor.RefreshAsync(state); offline = true; await monitor.RefreshAsync(state);
            Program.Assert(monitor.Current.Volumes.Single().Level == StorageLevel.Unknown && monitor.Current.Volumes.Single().Reading.AvailableBytes == null);
            Program.Assert(monitor.Current.Volumes.Single().LastSuccessfulCheck != null && monitor.Current.Jobs.Single().AfterOutputBytes == null);
        });
        await check("storage alerts deduplicate stable warnings, escalate and reset after recovery", () =>
        {
            var tracker = new StorageAlertTracker();
            StorageSnapshot Snapshot(StorageLevel level) => new(DateTimeOffset.Now, [new(Reading(), [], [], level, 10 * GiB, null, DateTimeOffset.Now)], []);
            Program.Assert(tracker.Update(Snapshot(StorageLevel.Warning), true).Count == 1);
            Program.Assert(tracker.Update(Snapshot(StorageLevel.Warning), true).Count == 0);
            Program.Assert(tracker.Update(Snapshot(StorageLevel.Critical), true).Count == 1);
            Program.Assert(tracker.Update(Snapshot(StorageLevel.Warning), true).Count == 0);
            Program.Assert(tracker.Update(Snapshot(StorageLevel.Healthy), true).Count == 0);
            Program.Assert(tracker.Update(Snapshot(StorageLevel.Warning), true).Count == 1);
            Program.Assert(tracker.Update(Snapshot(StorageLevel.Unknown), false).Count == 0); return Task.CompletedTask;
        });
        await check("a timed-out storage call is bounded and reused while the native call remains pending", async () =>
        {
            using var release = new ManualResetEventSlim(); var probe = new Probe(_ => { release.Wait(); return Reading(); }); var monitor = new StorageMonitor(probe, timeout: TimeSpan.FromMilliseconds(40));
            try
            {
                var first = await monitor.ReadAsync("slow"); var second = await monitor.ReadAsync("slow");
                Program.Assert(!first.Known && !second.Known && probe.Calls == 1);
            }
            finally { release.Set(); }
        });
        await check("same-volume backups wait and cancelled waiters do not release another run's lease", async () =>
        {
            var monitor = new StorageMonitor(new Probe(_ => Reading())); var folder = Program.Fixture(); using var first = await monitor.AcquireAsync(folder);
            using var cancelled = new CancellationTokenSource(); var pending = monitor.AcquireAsync(Path.Combine(folder, "second"), cancelled.Token);
            await Task.Delay(40); Program.Assert(!pending.IsCompleted); cancelled.Cancel(); await Program.Throws(async () => { using var ignored = await pending; });
            var next = monitor.AcquireAsync(Path.Combine(folder, "third")); await Task.Delay(40); Program.Assert(!next.IsCompleted); first.Dispose(); using var second = await next.WaitAsync(TimeSpan.FromSeconds(1));
        });
        await check("preflight refuses insufficient capacity without deleting existing backup outputs", async () =>
        {
            var folder = Program.Fixture(); var source = Path.Combine(folder, "source"); File.WriteAllText(source, "payload"); var job = Program.Job(source, folder); Directory.CreateDirectory(job.DestinationPath); var keep = Path.Combine(job.DestinationPath, "keep"); File.WriteAllText(keep, "old backup");
            var engine = new BackupEngine(new StorageMonitor(new Probe(_ => Reading(0)))); await Program.Throws(() => engine.RunAsync(job));
            Program.Assert(File.ReadAllText(keep) == "old backup" && Directory.GetFileSystemEntries(job.DestinationPath).Length == 1);
        });
        await check("file and project estimates respect exclusions without assuming ZIP compression", async () =>
        {
            var folder = Program.Fixture(); var source = Path.Combine(folder, "project"); Directory.CreateDirectory(source); Directory.CreateDirectory(Path.Combine(source, "node_modules")); File.WriteAllBytes(Path.Combine(source, "keep.bin"), new byte[1000]); File.WriteAllBytes(Path.Combine(source, "node_modules", "skip.bin"), new byte[1000000]);
            var job = Program.Job(source, folder); job.Kind = BackupKind.Project; var monitor = new StorageMonitor(new Probe(_ => Reading())); var estimate = await monitor.EstimateAsync(job);
            Program.Assert(estimate?.OutputBytes >= 1000 && estimate.OutputBytes < 1000000 && estimate.PeakBytes >= estimate.OutputBytes);
            job.ProjectExclusions = ""; var full = await monitor.EstimateAsync(job); Program.Assert(full?.OutputBytes > 1000000);
        });
        await check("database budget metadata persists across upgrade and invalidates changed selections", async () =>
        {
            var folder = Program.Fixture(); var path = Path.Combine(folder, "fixture.sqlite"); Program.Sql(path, "CREATE TABLE keep(id INTEGER); INSERT INTO keep VALUES(1);"); var job = Program.Job(path, folder); job.Kind = BackupKind.Sqlite;
            var store = new BackupStore(Path.Combine(folder, "state"), new MemorySecretStore(), new Probe(_ => Reading())); store.Save(job); await store.Run(job.Id);
            Program.Assert(store.Snapshot().History[0].Status == RunStatus.Success); var restored = new BackupStore(Path.Combine(folder, "state"), new MemorySecretStore()).Snapshot().Jobs.Single();
            Program.Assert(restored.LastSpaceEstimate?.Matches(restored) == true); restored.ExcludedDatabaseTables = [new("main", "keep")]; Program.Assert(!restored.LastSpaceEstimate!.Matches(restored));
            var legacy = JsonSerializer.Deserialize<PersistedState>("{\"version\":1,\"jobs\":[],\"history\":[],\"settings\":{\"schedulerEnabled\":true}}", Json.Options)!; Program.Assert(legacy.Settings.StorageWarningsEnabled && legacy.Settings.StorageWarningGiB == 20);
        });
        await check("storage labels and notification parameters exist in all ten languages", () =>
        {
            foreach (var language in L10n.AvailableLanguages)
            {
                L10n.Language = language; Program.Assert(L10n.Text("storage.critical") != "storage.critical");
                var text = L10n.Text("storage.job_summary", "one", "two", "three", "four"); Program.Assert(new[] { "one", "two", "three", "four" }.All(text.Contains));
            }
            L10n.Language = "en"; return Task.CompletedTask;
        });
        await check("invalid storage thresholds cannot be persisted", async () =>
        {
            var store = new BackupStore(Program.Fixture(), new MemorySecretStore()); var settings = store.Snapshot().Settings; settings.StorageWarningPercent = 101;
            await Program.Throws(() => { store.SaveSettings(settings); return Task.CompletedTask; }); Program.Assert(store.Snapshot().Settings.StorageWarningPercent == 10);
        });
    }
}
