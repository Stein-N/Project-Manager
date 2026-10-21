// Persistent settings in ~/.mc-project-manager/config.properties – the same file and keys as the Java and Rust
// versions, so an existing setup is taken over unchanged.

using System.Globalization;
using System.Text;

namespace ProjectManager.Core;

public sealed class Config
{
    /// <summary>Changelogs: Sonnet writes them as good as Opus for this task, faster and with less usage.</summary>
    public const string DefaultClaudeModel = "sonnet";

    /// <summary>Backports are harder than changelogs: Opus by default.</summary>
    public const string DefaultBackportModel = "claude-opus-5-5";

    /// <summary>Folder of the settings and other per-user files.</summary>
    public static string Dir => Path.Combine(Util.Home, ".mc-project-manager");

    internal SortedDictionary<string, string> Props { get; } = new(StringComparer.Ordinal);

    public static Config Load()
    {
        var c = new Config();
        foreach (var (k, v) in Util.ReadProperties(Path.Combine(Dir, "config.properties"))) c.Props[k] = v;
        return c;
    }

    public Config Clone()
    {
        var c = new Config();
        foreach (var (k, v) in Props) c.Props[k] = v;
        return c;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var text = new StringBuilder("#MC Mod Project Manager\n");
        foreach (var (k, v) in Props) text.Append($"{Util.EscapeProperty(k, true)}={Util.EscapeProperty(v, false)}\n");
        Util.WriteText(Path.Combine(Dir, "config.properties"), text.ToString());
    }

    private string String(string key) => Props.TryGetValue(key, out var v) ? v.Trim() : "";

    private void SetString(string key, string value)
    {
        if (value.Trim().Length == 0) Props.Remove(key);
        else Props[key] = value.Trim();
    }

    private string? PathValue(string key) => String(key) is { Length: > 0 } s ? s : null;

    private bool Bool(string key, bool fallback) =>
        Props.TryGetValue(key, out var v) ? v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) : fallback;

    private int Int(string key, int fallback) =>
        Props.TryGetValue(key, out var v) && int.TryParse(v.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : fallback;

    private double Double(string key, double fallback) =>
        Props.TryGetValue(key, out var v) && double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n
            : fallback;

    private static string Num(double d) => d.ToString(CultureInfo.InvariantCulture);

    public string? Master => PathValue("master");

    public void SetMaster(string? path) => SetString("master", path is null ? "" : Util.Absolute(path));

    public string? Idea => PathValue("idea");

    public void SetIdea(string? path) => SetString("idea", path is null ? "" : Util.Absolute(path));

    public string Git => String("git") is { Length: > 0 } g ? g : "git";

    public string Claude => String("claude");

    public string ClaudeModel => String("claudeModel") is { Length: > 0 } m ? m : DefaultClaudeModel;

    public string BackportModel => String("backportModel") is { Length: > 0 } m ? m : DefaultBackportModel;

    public int ClaudeTimeoutMinutes => Math.Max(1, Int("claudeTimeoutMinutes", 15));

    public int AutoFetchMinutes => Int("autoFetchMinutes", 10);

    public bool MinimizeToTray => Bool("minimizeToTray", true);

    public CloseAction CloseAction => String("closeAction").ToUpperInvariant() switch
    {
        "MINIMIZE" => CloseAction.Minimize,
        "EXIT" => CloseAction.Exit,
        _ => CloseAction.Ask,
    };

    public void SetCloseAction(CloseAction action) => Props["closeAction"] = action.ToString().ToUpperInvariant();

    public bool Autostart => Bool("autostart", false);

    public void SetAutostart(bool on) => Props["autostart"] = on ? "true" : "false";

    public bool CheckUpdates => Bool("checkUpdates", true);

    public string SkippedVersion => String("skippedVersion");

    public void SetSkippedVersion(string version) => SetString("skippedVersion", version);

    public double ClaudeMaxBudgetUsd => Math.Max(0, Double("claudeMaxBudgetUsd", 1.0));

    public double BackportMaxBudgetUsd => Math.Max(0, Double("backportMaxBudgetUsd", 5.0));

    /// <summary>Commit types backported from <paramref name="modName"/> into <paramref name="target"/>; empty = all types, null = not configured.</summary>
    public List<string>? BackportTypes(string modName, string target)
    {
        if (!Props.TryGetValue(BackportKey(modName, target), out var value)) return null;
        var v = value.Trim();
        if (v.Length == 0 || v == "*") return [];
        return v.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
    }

    /// <summary><paramref name="types"/> empty = all types.</summary>
    public void SetBackportTypes(string modName, string target, IReadOnlyCollection<string> types) =>
        Props[BackportKey(modName, target)] = types.Count == 0 ? "*" : string.Join(",", types);

    /// <summary>All settings at once (settings dialog).</summary>
    public Settings Settings() => new()
    {
        Master = Master,
        Idea = Idea,
        Git = Git,
        AutoFetchMinutes = AutoFetchMinutes,
        MinimizeToTray = MinimizeToTray,
        CloseAction = CloseAction,
        Autostart = Autostart,
        CheckUpdates = CheckUpdates,
        SkippedVersion = SkippedVersion,
        Claude = Claude,
        ClaudeModel = ClaudeModel,
        BackportModel = BackportModel,
        ClaudeMaxBudgetUsd = ClaudeMaxBudgetUsd,
        BackportMaxBudgetUsd = BackportMaxBudgetUsd,
        ClaudeTimeoutMinutes = ClaudeTimeoutMinutes,
    };

    public void Apply(Settings s)
    {
        SetMaster(s.Master);
        SetIdea(s.Idea);
        SetString("git", s.Git.Trim() == "git" ? "" : s.Git);
        Props["autoFetchMinutes"] = s.AutoFetchMinutes.ToString(CultureInfo.InvariantCulture);
        Props["minimizeToTray"] = s.MinimizeToTray ? "true" : "false";
        SetCloseAction(s.CloseAction);
        SetAutostart(s.Autostart);
        Props["checkUpdates"] = s.CheckUpdates ? "true" : "false";
        SetSkippedVersion(s.SkippedVersion);
        SetString("claude", s.Claude);
        SetString("claudeModel", s.ClaudeModel.Trim() == DefaultClaudeModel ? "" : s.ClaudeModel);
        SetString("backportModel", s.BackportModel.Trim() == DefaultBackportModel ? "" : s.BackportModel);
        Props["claudeMaxBudgetUsd"] = Num(Math.Max(0, s.ClaudeMaxBudgetUsd));
        Props["backportMaxBudgetUsd"] = Num(Math.Max(0, s.BackportMaxBudgetUsd));
        Props["claudeTimeoutMinutes"] = Math.Max(1, s.ClaudeTimeoutMinutes).ToString(CultureInfo.InvariantCulture);
    }

    private static string BackportKey(string modName, string target)
    {
        static string Clean(string s) =>
            new(s.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '_').ToArray());
        return $"backport.types.{Clean(modName)}.{Clean(target)}";
    }
}
