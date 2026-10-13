using System.Globalization;
using System.Text.RegularExpressions;

namespace BackupCron.Core;

public static partial class Naming
{
    public static string Render(BackupJob job, DateTimeOffset now)
    {
        var expanded = Tokens().Replace(job.NameMask, m => m.Value switch
        {
            "{job}" => job.Name, "{type}" => job.Kind.ToString().ToLowerInvariant(),
            "{date}" => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "{time}" => now.ToString("HH-mm-ss", CultureInfo.InvariantCulture), _ => m.Value
        });
        var clean = string.Concat(expanded.Select(c => char.IsLetterOrDigit(c) || "-_.".Contains(c) ? c : '-'));
        clean = Regex.Replace(clean, "-+", "-").Trim('.', '-', '_');
        if (clean.Length == 0) clean = "backup";
        clean = clean[..Math.Min(clean.Length, 120)].TrimEnd('.', ' ');
        if (Reserved().IsMatch(clean.Split('.')[0])) clean = "backup-" + clean;
        return clean;
    }
    [GeneratedRegex(@"\{(?:job|type|date|time)\}")] private static partial Regex Tokens();
    [GeneratedRegex(@"^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)] private static partial Regex Reserved();
}
