// Changelog tab: commit range, drafts (with or without Claude), saving.

using System.Globalization;
using ProjectManager.Core;

namespace ProjectManager.Services;

public sealed partial class Backend
{
    /// <summary>Upper limit for the diff in the prompt (about 100k tokens).</summary>
    private const int MaxDiffChars = 400_000;

    public Task<ChangelogInfo> ChangelogInfo(string project) => Run(() =>
    {
        var p = Project(project);
        var version = p.ModVersion is { } m ? Core.Changelog.DisplayVersion(m, p.EffectiveMc) : null;
        var existing = Util.ReadOrEmpty(Path.Combine(project, Core.Changelog.File)).Replace("\r\n", "\n");
        return new ChangelogInfo
        {
            Version = version,
            Heading = version is null ? null : Core.Changelog.Heading(version),
            Starts = p.ModVersion is { } raw && version is not null ? Core.Changelog.Starts(Git, project, raw, version) : [],
            Existing = existing,
            ExistingVersion = Core.Changelog.DetectVersion(existing),
        };
    });

    public Task<List<Commit>> ChangelogCommits(string project, string? start) => Run(() => Core.Changelog.Commits(Git, project, start));

    /// <summary>Claude writes the entry from the commits and the net diff (no tools).</summary>
    public Task<ChangelogDraft> ChangelogGenerate(string project, string? start, List<Commit> commits) => Run(() =>
    {
        var p = Project(project);
        var config = Config();
        var exe = Claude.Find(config.Claude) ?? throw new PmException(
            "Claude Code CLI not found – install Claude Code or set the path in Settings → Claude Code CLI.\nThe draft without Claude is always available.");
        var version = p.ModVersion is { } m ? Core.Changelog.DisplayVersion(m, p.EffectiveMc) : throw new PmException("No mod_version in gradle.properties");
        var heading = Core.Changelog.Heading(version);
        var claudeMd = Util.ReadOrEmpty(Path.Combine(project, "CLAUDE.md")) is { } md && md.Trim().Length > 0 ? md : null;
        var name = p.Metadata?.Name ?? p.Name;
        var diff = Core.Changelog.Diff(Git, project, start, MaxDiffChars);
        var prompt = Core.Changelog.Prompt(name, p.Loader, p.EffectiveMc, heading, commits, diff, Core.Changelog.AiStyleSample(project, 60),
            claudeMd is null ? null : Core.Changelog.RulesFromClaudeMd(claudeMd));
        var tokens = ClaudeTokens();
        Log($"> claude -p (changelog {version}, {commits.Count} commits + diff, {prompt.Length / 1000}k chars, no tools, " +
            $"{(tokens.Count == 0 ? "Claude Code login" : $"{tokens.Count} token(s) from CLAUDE_API")})    ({project})");
        var cancel = new ClaudeCancel();
        lock (_changelogRuns) _changelogRuns[project] = cancel;
        var request = new ClaudeRequest
        {
            WorkDir = project,
            Prompt = prompt,
            Model = config.ClaudeModel,
            MaxBudgetUsd = config.ClaudeMaxBudgetUsd,
            Timeout = TimeSpan.FromMinutes(config.ClaudeTimeoutMinutes),
            Tokens = tokens,
        };
        ClaudeOutcome outcome;
        try
        {
            outcome = Claude.Run(exe, request, TaggedLogger(p.Label), cancel);
        }
        finally
        {
            lock (_changelogRuns) _changelogRuns.Remove(project);
        }
        var tag = p.Label;
        if (!outcome.Ok)
        {
            Log($"✖ [{tag}] Claude: {outcome.Status}");
            throw new PmException(outcome.Status == ClaudeStatus.Cancelled ? "Cancelled." : $"Claude failed:\n{outcome.Text}");
        }
        var cost = $"${outcome.CostUsd.ToString("0.0000", CultureInfo.InvariantCulture)}";
        if (Core.Changelog.CleanAnswer(outcome.Text, version) is { } clean)
        {
            Log($"✔ [{tag}] Changelog draft by Claude ({cost})");
            return new ChangelogDraft(Core.Changelog.WithAiNote(clean), true, outcome.CostUsd);
        }
        Log($"⚠ [{tag}] Claude's answer does not start with the version heading");
        return new ChangelogDraft(outcome.Text, false, outcome.CostUsd);
    });

    public void ChangelogCancel(string project)
    {
        lock (_changelogRuns) _changelogRuns.GetValueOrDefault(project)?.Cancel();
    }

    /// <summary>What saving will do, shown before saving.</summary>
    public Task<ChangelogPlan> ChangelogPlan(string project, string version) => Run(() => Core.Changelog.Plan(project, version));

    public Task<ChangelogPlan> ChangelogSave(string project, string version, string text, bool moveUnknown) => Run(() =>
    {
        var done = Core.Changelog.Save(project, version, text, moveUnknown);
        Log($"✔ [{LabelOf(project)}] Saved CHANGELOG.md for {version}" +
            (done.Action == ChangelogAction.MoveOldVersion || moveUnknown ? " (previous entry moved to CHANGELOG_FULL.md)" : ""));
        return done;
    });
}
