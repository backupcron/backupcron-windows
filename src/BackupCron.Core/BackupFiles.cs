using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace BackupCron.Core;

public static class BackupPaths
{
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static string Resolve(string path)
    {
        var full = Path.GetFullPath(path); var root = Path.GetPathRoot(full)!; var current = root;
        foreach (var component in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget != null) current = Resolve(info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Unresolved source or destination link."));
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
    public static bool Inside(string child, string parent)
    {
        parent = Path.TrimEndingDirectorySeparator(parent);
        var prefix = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return child.Equals(parent, Comparison) || child.StartsWith(prefix, Comparison);
    }
    public static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; } catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
    }
    public static void DeleteOwnWork(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true); else if (Exists(path)) File.Delete(path);
    }
    public static long? AvailableBytes(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Resolve(path))!).AvailableFreeSpace; } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; } catch (ArgumentException) { return null; }
    }
    public static void RequireSpace(string path, long? bytes)
    {
        if (bytes is > 0 && AvailableBytes(path) is { } available && available < bytes) throw new IOException(L10n.Text("database.space_insufficient", path, L10n.Size(bytes), L10n.Size(available)));
    }
    public static string Publish(string staged, string destination, string basename, string extension, CancellationToken ct)
    {
        // Move within the destination volume. Neither files nor directories are overwritten.
        for (var suffix = 0; suffix < 10000; suffix++)
        {
            ct.ThrowIfCancellationRequested();
            var name = basename + (suffix == 0 ? "" : $"-{suffix}") + (extension.Length == 0 ? "" : "." + extension);
            var path = Path.Combine(destination, name);
            if (Exists(path)) continue;
            try { if (Directory.Exists(staged)) Directory.Move(staged, path); else File.Move(staged, path, overwrite: false); return path; }
            catch (IOException) when (Exists(path)) { }
        }
        throw new IOException("No available output name.");
    }
}

internal sealed record FileEntry(string Relative, string Source, long Size, string? LinkTarget, bool Directory);
internal sealed record FileDigest(string Relative, string Hash, string? LinkTarget, bool Directory);

internal static class BackupFiles
{
    public static async Task<(string Path, string Extension, long Size)> StageAsync(BackupJob job, string work, StorageMonitor storage, IProgress<BackupProgress>? progress, Action<string>? log, CancellationToken ct)
    {
        var source = BackupPaths.Resolve(job.SourcePath); var isDirectory = Directory.Exists(source);
        var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(source)); if (rootName.Length == 0) rootName = "files";
        var entries = Entries(job, ct);
        long total = 0; foreach (var entry in entries) total = checked(total + entry.Size);
        var estimate = Measure(job, entries); storage.Observe(job, estimate);
        await storage.RequireAsync(job.DestinationPath, estimate.PeakBytes, log, ct);
        var output = Path.Combine(work, job.ArchiveOutput ? "output.zip" : "payload");
        var digests = new List<FileDigest>(); long copied = 0;
        if (job.ArchiveOutput)
        {
            using (var archive = new ZipArchive(new FileStream(output, FileMode.CreateNew), ZipArchiveMode.Create))
            {
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested(); var name = entry.Relative.Replace(Path.DirectorySeparatorChar, '/');
                    var zip = archive.CreateEntry(entry.Directory && entry.LinkTarget == null ? name + "/" : name, CompressionLevel.Optimal);
                    if (entry.LinkTarget != null)
                    {
                        zip.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
                        using var dest = zip.Open(); await dest.WriteAsync(Encoding.UTF8.GetBytes(entry.LinkTarget), ct);
                        digests.Add(new(name, "", entry.LinkTarget, entry.Directory));
                    }
                    else if (entry.Directory) digests.Add(new(name + "/", "", null, true));
                    else { using var dest = zip.Open(); var hash = await CopyHash(entry.Source, dest, ct); digests.Add(new(name, hash, null, false)); }
                    copied += entry.Size; progress?.Report(new(.1 + .7 * copied / Math.Max(total, 1d), entry.Relative));
                }
            }
            using var check = ZipFile.OpenRead(output);
            if (check.Entries.Count != digests.Count) throw new InvalidDataException("ZIP verification failed.");
            foreach (var digest in digests)
            {
                ct.ThrowIfCancellationRequested(); var entry = check.GetEntry(digest.Relative) ?? throw new InvalidDataException("ZIP entry missing.");
                using var stream = entry.Open();
                if (digest.LinkTarget != null)
                { using var reader = new StreamReader(stream); if (await reader.ReadToEndAsync(ct) != digest.LinkTarget) throw new InvalidDataException("ZIP link verification failed."); }
                else if (!digest.Directory && Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)) != digest.Hash) throw new InvalidDataException("ZIP content verification failed.");
            }
            log?.Invoke(L10n.Text("engine.archive_created"));
            return (output, "zip", new FileInfo(output).Length);
        }
        if (!isDirectory)
        {
            using (var dest = new FileStream(output, FileMode.CreateNew)) digests.Add(new("", await CopyHash(source, dest, ct), null, false));
            using var verify = File.OpenRead(output);
            if (Convert.ToHexString(await SHA256.HashDataAsync(verify, ct)) != digests[0].Hash) throw new InvalidDataException("File verification failed.");
            return (output, Path.GetExtension(source).TrimStart('.'), new FileInfo(output).Length);
        }
        Directory.CreateDirectory(output);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested(); var relative = Path.GetRelativePath(rootName, entry.Relative); var target = relative == "." ? output : Path.Combine(output, relative);
            if (entry.LinkTarget != null)
            { if (entry.Directory) Directory.CreateSymbolicLink(target, entry.LinkTarget); else File.CreateSymbolicLink(target, entry.LinkTarget); digests.Add(new(relative, "", entry.LinkTarget, entry.Directory)); }
            else if (entry.Directory) Directory.CreateDirectory(target);
            else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); using var dest = new FileStream(target, FileMode.CreateNew); var hash = await CopyHash(entry.Source, dest, ct); digests.Add(new(relative, hash, null, false)); File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(entry.Source)); }
            copied += entry.Size; progress?.Report(new(.1 + .7 * copied / Math.Max(total, 1d), entry.Relative));
        }
        foreach (var digest in digests)
        {
            ct.ThrowIfCancellationRequested(); var path = Path.Combine(output, digest.Relative);
            if (digest.LinkTarget != null)
            { FileSystemInfo info = digest.Directory ? new DirectoryInfo(path) : new FileInfo(path); if (info.LinkTarget != digest.LinkTarget) throw new InvalidDataException("Link verification failed."); }
            else { using var stream = File.OpenRead(path); if (Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)) != digest.Hash) throw new InvalidDataException("Folder content verification failed."); }
        }
        return (output, "", total);
    }
    public static BackupSpaceEstimate Measure(BackupJob job, CancellationToken ct) => Measure(job, Entries(job, ct));
    private static BackupSpaceEstimate Measure(BackupJob job, List<FileEntry> entries)
    {
        long size = 0; foreach (var entry in entries) size = checked(size + entry.Size + (entry.LinkTarget == null ? 0 : Encoding.UTF8.GetByteCount(entry.LinkTarget)));
        var overhead = checked(65536L + entries.Count * (job.ArchiveOutput ? 2048L : 65536L));
        // Compression is never assumed to save space. Include ZIP headers and directory allocation.
        var peak = checked(size + size / 100 + overhead);
        return new(job.SpaceIdentity(), peak, peak, DateTimeOffset.Now);
    }
    private static List<FileEntry> Entries(BackupJob job, CancellationToken ct)
    {
        var source = BackupPaths.Resolve(job.SourcePath); var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(source)); if (rootName.Length == 0) rootName = "files";
        var exclusions = job.Kind == BackupKind.Project ? job.ExclusionNames.ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(); var entries = new List<FileEntry>();
        if (Directory.Exists(source)) Walk(new DirectoryInfo(source), rootName, exclusions, entries, ct);
        else entries.Add(new(rootName, source, new FileInfo(source).Length, null, false));
        return entries;
    }
    private static void Walk(DirectoryInfo directory, string relative, HashSet<string> exclusions, List<FileEntry> entries, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); entries.Add(new(relative, directory.FullName, 0, null, true));
        foreach (var info in directory.EnumerateFileSystemInfos().OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested(); if (exclusions.Contains(info.Name)) continue;
            var name = Path.Combine(relative, info.Name); var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (info.LinkTarget == null) throw new IOException($"Unsupported reparse point: {info.FullName}");
                entries.Add(new(name, info.FullName, 0, info.LinkTarget, isDirectory));
            }
            else if (isDirectory) Walk((DirectoryInfo)info, name, exclusions, entries, ct);
            else entries.Add(new(name, info.FullName, ((FileInfo)info).Length, null, false));
        }
    }
    private static async Task<string> CopyHash(string source, Stream destination, CancellationToken ct)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0) { await destination.WriteAsync(buffer.AsMemory(0, count), ct); hash.AppendData(buffer, 0, count); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    public static async Task ZipDatabaseAsync(string source, string output, CancellationToken ct)
    {
        string digest;
        using (var archive = new ZipArchive(new FileStream(output, FileMode.CreateNew), ZipArchiveMode.Create))
        { using var stream = archive.CreateEntry(Path.GetFileName(source), CompressionLevel.Optimal).Open(); digest = await CopyHash(source, stream, ct); }
        using var check = ZipFile.OpenRead(output); using var verify = check.Entries.Single().Open();
        if (Convert.ToHexString(await SHA256.HashDataAsync(verify, ct)) != digest) throw new InvalidDataException("Database archive verification failed.");
    }
}
