// Changelog tab: commit range, drafts (with or without Claude), saving; Backport tab: candidates and the steps of a
// backport (see BackportSession).

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
        var claudeMd = Backport.ClaudeMd(project);
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
            Tools = new ClaudeTools(),
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

    // ---------------------------------------------------------------- backport

    /// <summary>Commits of <paramref name="source"/> the target does not have yet (since the last tag), newest first, with the type rules.</summary>
    public Task<BackportCandidates> BackportCandidates(string target, string source) => Run(() =>
    {
        var t = Project(target);
        var s = Project(source);
        if (s.Branch is not { } sourceBranch || t.Branch is not { } targetBranch) return new BackportCandidates();
        var configured = Config().BackportTypes(t.Name, t.McVersion);
        var types = configured ?? Backport.TypesFromRepo(s.Path, t.McVersion);
        var names = new List<string> { t.McVersion, targetBranch };
        if (t.GradleMcVersion is { } g) names.Add(g);
        var sinceTag = Tags.Latest(Git, t.Path, sourceBranch);
        var candidates = Backport.Candidates(Git, t.Path, sourceBranch, targetBranch, sinceTag, names, types);
        candidates.Reverse();
        return new BackportCandidates
        {
            SinceTag = sinceTag, Candidates = candidates, Types = types, TypesConfigured = configured is not null,
            Primer = Primer.Find(Master(), t.McVersion, s.McVersion)?.Name,
        };
    });

    /// <summary>Commit types ported into the target; empty = all.</summary>
    public Task BackportSetTypes(string target, List<string> types) => Run(() =>
    {
        var t = Project(target);
        UpdateConfig(c => c.SetBackportTypes(t.Name, t.McVersion, types));
    });

    public Task<BackportView> BackportView(string target) => Run(() => ViewOf(BackportSlot(target)));

    private static BackportView ViewOf(BackportSlot slot)
    {
        var busy = slot.Busy;
        BackportView v;
        if (Monitor.TryEnter(slot.Lock))
        {
            try
            {
                v = slot.Session?.View() ?? new BackportView();
            }
            finally
            {
                Monitor.Exit(slot.Lock);
            }
        }
        else v = new BackportView { Active = true };
        if (busy is not null) v = v with { Busy = true, Status = busy };
        v = v with { ClaudeRunning = slot.ClaudeRunning, Building = slot.Building };
        if (v.Building) v = v with { Status = "Building in the work folder… (output in the log window)" };
        return v;
    }

    private BackportEnv EnvFor(BackportSlot slot)
    {
        var config = Config();
        var cancel = new ClaudeCancel();
        slot.Cancel = cancel;
        return new BackportEnv
        {
            Git = Git,
            Log = SubLogger(),
            Claude = Claude.Find(config.Claude),
            Model = config.BackportModel,
            MaxBudgetUsd = config.BackportMaxBudgetUsd,
            Timeout = TimeSpan.FromMinutes(Math.Max(30, config.ClaudeTimeoutMinutes)),
            Tokens = ClaudeTokens(),
            Cancel = cancel,
        };
    }

    /// <summary>Runs one step with the session locked; the busy label is shown meanwhile.</summary>
    private T Step<T>(string target, string label, bool claude, Func<BackportSlot, BackportEnv, T> work)
    {
        var slot = BackportSlot(target);
        lock (slot)
        {
            if (slot.Busy is not null) throw new PmException("A backport step is still running.");
            slot.Busy = label;
        }
        slot.ClaudeRunning = claude;
        try
        {
            var env = EnvFor(slot);
            lock (slot.Lock) return work(slot, env);
        }
        finally
        {
            slot.ClaudeRunning = false;
            slot.Busy = null;
        }
    }

    /// <summary>The part of the primer between both versions that Claude gets, for the target's loader.</summary>
    private static (string Name, string Text)? PrimerFor(string master, ModProject target, ModProject source) =>
        Primer.Find(master, target.McVersion, source.McVersion) is { } f
            ? (f.Name, Primer.Excerpt(Util.ReadOrEmpty(f.Path), target.Loader, Primer.MaxBackportChars))
            : null;

    private static BackportSession Active(BackportSlot slot) => slot.Session ?? throw new PmException("No backport in progress.");

    /// <summary>Ports <paramref name="commits"/> (oldest first) from <paramref name="source"/> into <paramref name="target"/>.</summary>
    public Task<BackportView> BackportStart(string target, string source, List<Commit> commits, bool sync, bool useClaude, bool usePrimer) => Run(() =>
    {
        var t = Project(target);
        var s = Project(source);
        var master = Master();
        var primer = usePrimer ? PrimerFor(master, t, s) : null;
        Step(target, $"Porting {commits.Count} commit(s)… (first time: a full checkout)", useClaude, (slot, env) =>
        {
            var (session, error) = BackportSession.Start(env, master, t, s, commits, sync, useClaude, primer);
            slot.Session = session;
            if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
            return true;
        });
        return ViewOf(BackportSlot(target));
    });

    /// <summary>Brings <paramref name="target"/> to the state of <paramref name="source"/>: all changes since the last sync as one step.</summary>
    public Task<BackportView> BackportSync(string target, string source, bool sync, bool useClaude, bool usePrimer) => Run(() =>
    {
        var t = Project(target);
        var s = Project(source);
        var master = Master();
        var primer = usePrimer ? PrimerFor(master, t, s) : null;
        Step(target, "Syncing… (first time: a full checkout)", useClaude, (slot, env) =>
        {
            var (session, error) = BackportSession.StartSync(env, master, t, s, sync, useClaude, primer);
            slot.Session = session;
            if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
            return true;
        });
        return ViewOf(BackportSlot(target));
    });

    public Task<BackportView> BackportContinue(string target) => Run(() =>
    {
        Step(target, "Continuing…", false, (slot, env) =>
        {
            Active(slot).ContinueAfterConflict(env);
            return true;
        });
        return ViewOf(BackportSlot(target));
    });

    public Task<BackportView> BackportResolve(string target) => Run(() =>
    {
        Step(target, "Claude is resolving the conflict… (output in the log window)", true, (slot, env) =>
        {
            Active(slot).ResolveWithClaude(env);
            return true;
        });
        return ViewOf(BackportSlot(target));
    });

    public Task<BackportView> BackportSkip(string target) => Run(() =>
    {
        Step(target, "Skipping the commit…", false, (slot, env) =>
        {
            Active(slot).Skip(env);
            return true;
        });
        return ViewOf(BackportSlot(target));
    });

    public Task<BackportView> BackportFixBuild(string target) => Run(() =>
    {
        var slot = BackportSlot(target);
        if (slot.Building) throw new PmException("The build is still running.");
        Step(target, "Claude is fixing the build… (output in the log window)", true, (s, env) =>
        {
            Active(s).FixBuild(env);
            return true;
        });
        return ViewOf(slot);
    });

    public void BackportCancel(string target) => BackportSlot(target).Cancel.Cancel();

    /// <summary><c>gradlew build</c> in the work folder; the end arrives as a backport event.</summary>
    public Task<BackportView> BackportBuild(string target) => Run(() =>
    {
        var slot = BackportSlot(target);
        string work, tag;
        lock (slot.Lock)
        {
            var s = slot.Session ?? throw new PmException("No backport in progress.");
            (work, tag) = (s.Work, $"backport {s.Target.McVersion}");
        }
        if (!Gradle.CanStart(work, "build")) throw new PmException("A build is already running in the work folder.");
        Log($"> gradlew build    ({work})");
        lock (slot.BuildLines) slot.BuildLines.Clear();
        slot.Building = true;
        try
        {
            Gradle.Start(work, "build", new Dictionary<string, string>(), line =>
            {
                lock (slot.BuildLines) slot.BuildLines.Add(line);
                Log($"    [{tag}] {line}");
            }, exit =>
            {
                List<string> lines;
                lock (slot.BuildLines)
                {
                    lines = slot.BuildLines.ToList();
                    slot.BuildLines.Clear();
                }
                lock (slot.Lock)
                {
                    if (slot.Session is { } s)
                    {
                        s.BuildLines.Clear();
                        s.BuildLines.AddRange(lines);
                        s.BuildFinished(exit);
                    }
                }
                slot.Building = false;
                Log(exit == 0 ? "✔ backport build passed" : $"✖ backport build failed (exit code {exit})");
                BackportChanged?.Invoke(target);
            });
        }
        catch (Exception)
        {
            slot.Building = false;
            throw;
        }
        return ViewOf(slot);
    });

    /// <summary>Applies the result to the target as uncommitted changes; the session ends.</summary>
    public Task<BackportApplied> BackportApply(string target) => Run(() => Step(target, "Applying…", false, (slot, env) =>
    {
        var applied = Active(slot).Apply(env);
        slot.Session = null;
        return applied;
    }));

    public Task BackportDiscard(string target) => Run(() => Step(target, "Discarding…", false, (slot, env) =>
    {
        slot.Session?.Discard(env);
        slot.Session = null;
        return true;
    }));

    /// <summary>Diff of one file of the result (or the conflict of a file with status U).</summary>
    public Task<string> BackportDiff(string target, string path, char status) => Run(() =>
    {
        var slot = BackportSlot(target);
        lock (slot.Lock) return slot.Session?.Diff(Git, path, status) ?? "";
    });
}
