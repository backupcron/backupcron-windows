using System.Diagnostics;
using System.Text;

namespace BackupCron.Core;

public sealed record Command(string Executable, IReadOnlyList<string> Arguments, Dictionary<string, string>? Environment = null);

public sealed class ProcessRunner
{
    public async Task RunAsync(Command command, string outputPath, string password, Action<string>? log, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(command.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in command.Arguments) start.ArgumentList.Add(arg);
        if (command.Environment != null) foreach (var pair in command.Environment) start.Environment[pair.Key] = pair.Value;
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        using var process = new Process { StartInfo = start };
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("Could not start the database client.");
        using var registration = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var errors = new StringBuilder();
        var stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                var safe = Redact(line, password); log?.Invoke(safe);
                if (errors.Length < 32768) errors.AppendLine(safe[..Math.Min(safe.Length, 2048)]);
            }
        }, CancellationToken.None);
        try
        {
            // Copy raw bytes; text redirection corrupts some MySQL dumps on Windows.
            await process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
            await process.WaitForExitAsync(cancellationToken); await stderr;
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException(errors.Length > 0 ? errors.ToString().Trim() : $"Client exited with code {process.ExitCode}.");
            await output.FlushAsync(cancellationToken);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None); await stderr; throw;
        }
    }
    public static string Redact(string text, string password) => string.IsNullOrEmpty(password) ? text : text.Replace(password, "••••", StringComparison.Ordinal);
    public static string ResolveTool(string name, string customPath = "")
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            if (!File.Exists(customPath)) throw new FileNotFoundException($"Client not found: {customPath}");
            return Path.GetFullPath(customPath);
        }
        var executable = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var dirs = (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(d => d.Length > 0).ToList();
        if (OperatingSystem.IsWindows())
        {
            var programs = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles);
            var postgres = Path.Combine(programs, "PostgreSQL");
            if (Directory.Exists(postgres)) dirs.AddRange(Directory.GetDirectories(postgres).OrderDescending().Select(d => Path.Combine(d, "bin")));
            var mysql = Path.Combine(programs, "MySQL");
            if (Directory.Exists(mysql)) dirs.AddRange(Directory.GetDirectories(mysql).OrderDescending().Select(d => Path.Combine(d, "bin")));
            dirs.Add(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "OpenSSH"));
        }
        var found = dirs.Select(d => Path.Combine(d.Trim('"'), executable)).FirstOrDefault(File.Exists);
        return found ?? throw new FileNotFoundException($"{executable}: {L10n.Text("editor.automatic_search")}. Install the client or select its executable.");
    }
    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
