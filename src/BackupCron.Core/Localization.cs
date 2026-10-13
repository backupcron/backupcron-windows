using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BackupCron.Core;

public static class L10n
{
    private static readonly Dictionary<string, Dictionary<string, string>> Languages = Load();
    public static string Language { get; set; } = "auto";
    public static IReadOnlyCollection<string> AvailableLanguages => Languages.Keys;
    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        var assembly = typeof(L10n).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith("localizations.json")))!;
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)!;
    }
    public static string Text(string key, params object?[] args)
    {
        var language = Language == "auto" ? CultureInfo.CurrentUICulture.Name : Language;
        if (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) language = "zh-Hans";
        if (!Languages.ContainsKey(language)) language = language.Split('-')[0];
        if (!Languages.TryGetValue(language, out var dictionary)) dictionary = Languages["en"];
        var value = dictionary.GetValueOrDefault(key) ?? Languages["en"].GetValueOrDefault(key) ?? key;
        var position = 0;
        return Regex.Replace(value, @"%(?:(\d+)\$)?(?:lld|ld|lu|zu|d|f|@|s)", m =>
        {
            var index = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) - 1 : position++;
            return index >= 0 && index < args.Length ? Convert.ToString(args[index], CultureInfo.CurrentCulture) ?? "" : m.Value;
        });
    }
    public static string Kind(BackupKind kind) => kind switch { BackupKind.Files => Text("kind.files"), BackupKind.Project => Text("kind.project"), BackupKind.Sqlite => "SQLite", BackupKind.Postgres => "PostgreSQL", _ => "MySQL" };
    public static string Size(long? bytes)
    {
        if (bytes is not >= 0) return "—";
        var units = new[] { "B", "KB", "MB", "GB", "TB" }; double size = bytes.Value; int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.##} {units[unit]}";
    }
}
