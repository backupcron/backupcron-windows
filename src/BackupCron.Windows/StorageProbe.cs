using BackupCron.Core;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace BackupCron.Windows;

internal sealed class WindowsStorageProbe : IStorageProbe
{
    public IEnumerable<string> MachinePaths() => DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.Name);
    public StorageReading Read(string path)
    {
        try
        {
            var full = Path.GetFullPath(path); var root = Path.GetPathRoot(full)!;
            var existing = full;
            if (File.Exists(existing)) existing = Path.GetDirectoryName(existing)!;
            while (!Directory.Exists(existing))
            {
                var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(existing));
                if (parent == null || existing.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)) break;
                existing = parent;
            }
            var directory = Path.TrimEndingDirectorySeparator(existing) + Path.DirectorySeparatorChar;
            var mount = new StringBuilder(32768);
            if (GetVolumePathNameW(directory, mount, mount.Capacity)) root = mount.ToString();
            var identity = root; var volume = new StringBuilder(64);
            if (GetVolumeNameForVolumeMountPointW(root, volume, volume.Capacity)) identity = volume.ToString();
            else if (root.Length >= 2 && root[1] == ':')
            {
                var connection = new StringBuilder(32768); var length = connection.Capacity;
                if (WNetGetConnectionW(root[..2], connection, ref length) == 0) identity = Path.GetPathRoot(connection.ToString()) ?? connection.ToString();
            }
            var network = identity.StartsWith(@"\\") && !identity.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase);
            // Query the actual directory, including a mounted volume or a UNC share.
            // Available is the caller's usable space; Total can also reflect a user quota.
            if (!GetDiskFreeSpaceExW(directory, out var available, out var total, out var free))
                return new(identity, root, null, null, null, network, DateTimeOffset.Now, new Win32Exception(Marshal.GetLastWin32Error()).Message);
            if (total > long.MaxValue || free > long.MaxValue || available > long.MaxValue) return StorageReading.Unknown(root);
            return new(identity, root, (long)total, (long)free, (long)available, network, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return StorageReading.Unknown(path, ex.Message); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string path, StringBuilder volumePath, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mount, StringBuilder volumeName, int length);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnectionW(string localName, StringBuilder remoteName, ref int length);
}
