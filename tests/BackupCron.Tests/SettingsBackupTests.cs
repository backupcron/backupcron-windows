using BackupCron.Core;
using System.Text.Json;

namespace BackupCron.Tests;

internal static class SettingsBackupTests
{
    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        await check("settings backup round trip excludes passwords and run history", async () =>
        {
            var folder = Program.Fixture(); var secrets = new MemorySecretStore(); var store = new BackupStore(Path.Combine(folder, "state"), secrets);
            var job = Program.Job(Path.Combine(folder, "source"), folder); job.LastRunAt = DateTimeOffset.UtcNow;
            store.Save(job, "backup-secret"); var settings = store.Snapshot().Settings; settings.SoundEnabled = true; settings.SoundVolume = .7; store.SaveSettings(settings);
            var path = await store.BackupSettingsAsync(folder); var archive = SettingsArchive.Read(path);
            Program.Assert(archive.Jobs.Single().Id == job.Id && archive.Jobs[0].LastRunAt == null);
            Program.Assert(archive.Settings.SoundEnabled && archive.Settings.SoundVolume == .7);
            var json = File.ReadAllText(path); Program.Assert(!json.Contains("backup-secret") && !json.Contains("\"history\""));
            var second = await store.BackupSettingsAsync(folder); Program.Assert(second != path && File.Exists(path));
        });
        await check("automatic settings copies include later job changes and survive restart", async () =>
        {
            var folder = Program.Fixture(); var directory = Path.Combine(folder, "state"); var store = new BackupStore(directory, new MemorySecretStore());
            var settings = store.Snapshot().Settings; settings.SettingsBackupEnabled = true; settings.SettingsBackupDirectory = folder; store.SaveSettings(settings);
            await store.SettingsBackups.Completion;
            Program.Assert(store.SettingsBackups.Error == null && File.Exists(store.SettingsBackups.LastPath));
            var job = Program.Job(Path.Combine(folder, "source"), folder); store.Save(job); await store.SettingsBackups.Completion;
            Program.Assert(SettingsArchive.Read(store.SettingsBackups.LastPath!).Jobs.Single().Id == job.Id);
            var restarted = new BackupStore(directory, new MemorySecretStore()); await restarted.SettingsBackups.Completion;
            Program.Assert(SettingsArchive.Read(restarted.SettingsBackups.LastPath!).Jobs.Single().Id == job.Id);
            settings.SettingsBackupEnabled = false; store.SaveSettings(settings); await store.SettingsBackups.Completion;
            var count = Directory.GetFiles(folder, "*.json").Length; store.Delete(job.Id); await store.SettingsBackups.Completion;
            Program.Assert(Directory.GetFiles(folder, "*.json").Length == count);
        });
        await check("unavailable backup folder does not lose locally saved settings", async () =>
        {
            var folder = Program.Fixture(); var directory = Path.Combine(folder, "state"); var store = new BackupStore(directory, new MemorySecretStore());
            var settings = store.Snapshot().Settings; settings.SettingsBackupEnabled = true; settings.SettingsBackupDirectory = Path.Combine(folder, "offline"); settings.SoundVolume = .8;
            store.SaveSettings(settings); await store.SettingsBackups.Completion;
            Program.Assert(store.SettingsBackups.Error != null);
            var restarted = new BackupStore(directory, new MemorySecretStore()); await restarted.SettingsBackups.Completion;
            Program.Assert(restarted.Snapshot().Settings.SoundVolume == .8);
            settings.SettingsBackupEnabled = false; store.SaveSettings(settings); await store.SettingsBackups.Completion;
            Program.Assert(store.Error == null && store.SettingsBackups.Error == null);
            settings.SettingsBackupEnabled = true; Directory.CreateDirectory(settings.SettingsBackupDirectory); store.SaveSettings(settings); await store.SettingsBackups.Completion;
            Program.Assert(store.SettingsBackups.Error == null && File.Exists(store.SettingsBackups.LastPath));
        });
        await check("restore preserves recovery copy, credentials and this PC backup location", async () =>
        {
            var folder = Program.Fixture(); var directory = Path.Combine(folder, "state"); var secrets = new MemorySecretStore(); var store = new BackupStore(directory, secrets);
            var original = Program.Job(Path.Combine(folder, "original"), folder); store.Save(original, "kept-password");
            var settings = store.Snapshot().Settings; settings.SettingsBackupDirectory = folder; store.SaveSettings(settings);
            var importedJob = Program.Job(Path.Combine(folder, "imported"), folder);
            var imported = SettingsArchive.Create([importedJob], new AppSettings { SoundEnabled = true, SoundVolume = .6, SettingsBackupEnabled = true, SettingsBackupDirectory = "X:\\other-pc" });
            store.RestoreSettings(imported); await store.SettingsBackups.Completion;
            var state = store.Snapshot(); Program.Assert(state.Jobs.Single().Id == importedJob.Id && !state.Settings.SchedulerEnabled);
            Program.Assert(state.Settings.SoundEnabled && state.Settings.SoundVolume == .6 && !state.Settings.SettingsBackupEnabled && state.Settings.SettingsBackupDirectory == folder);
            Program.Assert(secrets.Get(original.Id) == "kept-password");
            var recovery = Directory.GetFiles(Path.Combine(directory, "Settings recovery"), "*.json").Single();
            Program.Assert(SettingsArchive.Read(recovery).Jobs.Single().Id == original.Id);
            var restarted = new BackupStore(directory, secrets); Program.Assert(restarted.Snapshot().Jobs.Single().Id == importedJob.Id && !restarted.Snapshot().Settings.SchedulerEnabled);
        });
        await check("malformed and unsupported imports preserve current settings", async () =>
        {
            var folder = Program.Fixture(); var directory = Path.Combine(folder, "state"); var store = new BackupStore(directory, new MemorySecretStore());
            var job = Program.Job(Path.Combine(folder, "source"), folder); store.Save(job); var original = File.ReadAllText(Path.Combine(directory, "state.json"));
            var archive = SettingsArchive.Create([job], new AppSettings()); archive.Version = 2;
            await Program.Throws(() => { store.RestoreSettings(archive); return Task.CompletedTask; });
            archive.Version = 1; archive.Jobs.Add(job);
            await Program.Throws(() => { store.RestoreSettings(archive); return Task.CompletedTask; });
            var invalid = Path.Combine(folder, "invalid.json"); File.WriteAllText(invalid, "{}");
            await Program.Throws(() => { SettingsArchive.Read(invalid); return Task.CompletedTask; });
            archive.Jobs = [job]; var json = JsonSerializer.Serialize(archive, Json.Options).Replace("\"jobs\":", "\"missingJobs\":"); File.WriteAllText(invalid, json);
            await Program.Throws(() => { SettingsArchive.Read(invalid); return Task.CompletedTask; });
            Program.Assert(File.ReadAllText(Path.Combine(directory, "state.json")) == original);
        });
        await check("restore stops when its recovery copy cannot be saved", async () =>
        {
            var folder = Program.Fixture(); var store = new BackupStore(folder, new MemorySecretStore()); var job = Program.Job(Path.Combine(folder, "source"), folder); store.Save(job);
            File.WriteAllText(Path.Combine(folder, "Settings recovery"), "blocked");
            var original = File.ReadAllText(Path.Combine(folder, "state.json"));
            await Program.Throws(() => { store.RestoreSettings(SettingsArchive.Create([], new AppSettings())); return Task.CompletedTask; });
            Program.Assert(File.ReadAllText(Path.Combine(folder, "state.json")) == original);
        });
    }
}
