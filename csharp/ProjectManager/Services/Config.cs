namespace ProjectManager.Services;

/// <summary>
/// Reads the settings of the Rust/Java version (<c>~/.mc-project-manager/config.properties</c>), so the example shows
/// the same master folder.
/// </summary>
public static class Config
{
    public static string File =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mc-project-manager",
            "config.properties");

    public static string? Master()
    {
        var value = Read().GetValueOrDefault("master")?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Java properties: <c>key=value</c>, <c>#</c> comments, backslash escapes.</summary>
    private static Dictionary<string, string> Read()
    {
        var props = new Dictionary<string, string>();
        if (!System.IO.File.Exists(File)) return props;
        foreach (var raw in System.IO.File.ReadAllLines(File))
        {
            var line = raw.TrimStart();
            if (line.Length == 0 || line[0] is '#' or '!') continue;
            var split = line.IndexOfAny(['=', ':']);
            if (split < 0) continue;
            props[Unescape(line[..split].Trim())] = Unescape(line[(split + 1)..].TrimStart());
        }
        return props;
    }

    private static string Unescape(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\\(.)", m => m.Groups[1].Value switch
        {
            "n" => "\n",
            "t" => "\t",
            var c => c,
        });
}
