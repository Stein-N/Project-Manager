// A backport in progress: picks the selected commits one after the other into the work worktree, stops at conflicts (or
// hands them to Claude), takes over version and changelog at the end and keeps the notes of every step. The UI only
// shows View() and calls the steps; nothing here is committed.

using System.Globalization;
using System.Text;

namespace ProjectManager.Core;

/// <summary>What the steps need from outside.</summary>
public sealed record BackportEnv
{
    public required Git Git { get; init; }
    public required Action<string> Log { get; init; }
    /// <summary>Claude Code CLI, if it was found.</summary>
    public string? Claude { get; init; }
    public string Model { get; init; } = "";
    public double MaxBudgetUsd { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(15);
    public List<string> Tokens { get; init; } = [];
    public ClaudeCancel Cancel { get; init; } = new();
}

public sealed class BackportSession
{
    /// <summary>Upper limit for the original commit in a prompt.</summary>
    private const int MaxCommitChars = 200_000;

    /// <summary>Lines of the build output Claude gets (the errors are at the end).</summary>
    public const int BuildTailLines = 250;

    private sealed record ConflictState(Commit Commit, List<string> Files, string Checkpoint);

    /// <summary>Result of one Claude call on a conflict.</summary>
    private sealed record ClaudeStep(bool Resolved, bool Skip, List<string> Left, string Notes, string? Error);

    public ModProject Target { get; }
    public ModProject Source { get; }
    public string Work { get; }
    private readonly bool _sync;
    private readonly bool _useClaude;
    private string? _baseSha;
    private readonly List<Commit> _ported = [];
    /// <summary>Markdown: what the tool and Claude did, per commit.</summary>
    private readonly List<string> _notes = [];
    /// <summary>Commits after the conflict.</summary>
    private List<Commit> _queue = [];
    private ConflictState? _conflict;
    private string? _versionNote;
    public bool BuildFailed { get; private set; }
    public List<string> BuildLines { get; } = [];
    private List<FileChange> _changes = [];
    public string Status { get; private set; } = "";

    private BackportSession(ModProject target, ModProject source, string work, bool sync, bool useClaude)
    {
        (Target, Source, Work, _sync, _useClaude) = (target, source, work, sync, useClaude);
    }

    /// <summary>Prepares the work worktree of <paramref name="target"/> and ports <paramref name="commits"/> (oldest first).</summary>
    public static (BackportSession Session, Exception? Error) Start(BackportEnv env, string master, ModProject target, ModProject source,
        List<Commit> commits, bool sync, bool useClaude)
    {
        var s = new BackportSession(target, source, Backport.WorkDir(master, target.Name, target.McVersion), sync, useClaude);
        var error = s.Step(env, () =>
        {
            var branch = s.Target.Branch ?? throw new PmException("The target has no branch");
            env.Log($"Backport {commits.Count} commit(s) {s.Source.Branch ?? "?"} → {branch}{(useClaude ? " (conflicts with Claude)" : "")}    (work folder {s.Work})");
            Backport.Prepare(env.Git, s.Target.Path, s.Work, branch, env.Log);
            s._baseSha = env.Git.Output(s.Work, "rev-parse", "HEAD").Trim();
            s.RunQueue(env, commits);
        });
        return (s, error);
    }

    /// <summary>Runs a step, then the status line and the safety check; returns the error of the step.</summary>
    private Exception? Step(BackportEnv env, Action step)
    {
        Exception? error = null;
        try
        {
            step();
        }
        catch (Exception e)
        {
            error = e;
        }
        Finish(env, error);
        return error;
    }

    private static void Throw(Exception? e)
    {
        if (e is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e);
    }

    /// <summary>The user resolved the conflict in the work folder: check the markers, then go on.</summary>
    public void ContinueAfterConflict(BackportEnv env)
    {
        var c = _conflict ?? throw new PmException("No conflict to continue");
        Throw(Step(env, () =>
        {
            var left = Backport.ContinueAfterConflict(env.Git, Work, c.Files);
            if (left.Count > 0) throw new PmException($"Still conflict markers in: {string.Join(", ", left)}");
            _ported.Add(c.Commit);
            _conflict = null;
            RunQueue(env, TakeQueue());
        }));
    }

    private List<Commit> TakeQueue()
    {
        var rest = _queue;
        _queue = [];
        return rest;
    }

    /// <summary>Claude resolves the current conflict.</summary>
    public void ResolveWithClaude(BackportEnv env)
    {
        var c = _conflict ?? throw new PmException("No conflict to resolve");
        Throw(Step(env, () =>
        {
            var step = ClaudeResolve(env, c.Commit, c.Files);
            _notes.Add(step.Notes);
            if (step.Resolved) _ported.Add(c.Commit);
            else if (step.Skip) Backport.Restore(env.Git, Work, c.Checkpoint);
            else
            {
                var open = Backport.Unmerged(env.Git, Work);
                var files = open.Count > 0 ? open : step.Left.Count > 0 ? step.Left : c.Files;
                _conflict = c with { Files = files };
                throw new PmException(step.Error ?? "Claude could not resolve the conflict.");
            }
            _conflict = null;
            RunQueue(env, TakeQueue());
        }));
    }

    /// <summary>Leaves the conflicting commit out: back to the state before it, then the next commits.</summary>
    public void Skip(BackportEnv env)
    {
        var c = _conflict ?? throw new PmException("No conflict to skip");
        Throw(Step(env, () =>
        {
            Backport.Restore(env.Git, Work, c.Checkpoint);
            _notes.Add($"- `{c.Commit.ShortSha}` {c.Commit.Message.Subject}: **skipped**");
            _conflict = null;
            RunQueue(env, TakeQueue());
        }));
    }

    /// <summary>Claude fixes the errors of the last build in the work folder.</summary>
    public void FixBuild(BackportEnv env)
    {
        if (!BuildFailed) throw new PmException("The last build did not fail.");
        var output = BackportClaude.Tail(BuildLines, BuildTailLines);
        Exception? error = null;
        try
        {
            var prompt = BackportClaude.BuildPrompt(Context(), _ported, output);
            var o = CallClaude(env, prompt, "build errors");
            string section;
            if (!o.Ok) section = $"#### Build – Claude: {StatusName(o.Status)} – {o.Text}{DenialNotes(o.Denials)}";
            else
            {
                var (decision, notes) = BackportClaude.Parse(o.Text);
                section = $"#### Build – Claude: **{(decision == Decision.Skip ? "could not fix it" : "fixed")}** (${Money(o.CostUsd)})\n\n{notes}{DenialNotes(o.Denials)}";
            }
            _notes.Add(section);
            _changes = Backport.Changes(env.Git, Work);
            VerifyNoCommit(env);
        }
        catch (Exception e)
        {
            error = e;
        }
        Status = "Claude worked on the build errors – see Notes, then run the build again";
        Throw(error);
    }

    /// <summary>A build in the work folder ended.</summary>
    public void BuildFinished(int exit)
    {
        BuildFailed = exit != 0;
        _notes.Add(exit == 0 ? "- build **passed**" : $"- build **failed** (exit code {exit})");
        Status = exit == 0 ? "Build passed – apply when the review is done" : "Build failed – fix it in the work folder or with Claude";
    }

    /// <summary>Applies the result to the target as uncommitted changes; the session is over afterwards.</summary>
    public BackportApplied Apply(BackportEnv env)
    {
        if (_baseSha is null) throw new PmException("The work folder is no longer at the target commit – discard and start again.");
        var message = Backport.CommitMessage(_ported, Source.Branch ?? "?", _versionNote);
        var conflicts = Backport.Apply(env.Git, Work, Target.Path, env.Log);
        env.Log($"✔ Backport applied to {Target.Label} as uncommitted changes{(conflicts.Count == 0 ? "" : $" – conflicts in {string.Join(", ", conflicts)}")}");
        return new BackportApplied(conflicts, message);
    }

    /// <summary>Throws the result away (only in the work folder).</summary>
    public void Discard(BackportEnv env)
    {
        Backport.Discard(env.Git, Target.Path, Work);
        env.Log("Backport discarded");
    }

    public string Diff(Git git, string path, char status) =>
        _conflict is { } c && status == 'U' ? Backport.ConflictDiff(git, Work, path, c.Commit.Sha) : Backport.Diff(git, Work, path);

    public BackportView View()
    {
        var notes = _notes.Count == 0 ? "_No notes yet. Notes from Claude and from the tool appear here._" : string.Join("\n\n", _notes);
        return new BackportView
        {
            Active = true,
            Status = Status,
            WorkDir = Work,
            Conflict = _conflict is { } c ? new BackportConflict(c.Commit, c.Files, _queue.Count) : null,
            Ported = _ported.Count,
            Notes = notes,
            NotesCount = _notes.Count(n => n.StartsWith("####")),
            Changes = _conflict is { } cf ? cf.Files.Select(f => new FileChange('U', f)).ToList() : _changes.ToList(),
            BuildFailed = BuildFailed,
            CanApply = _baseSha is not null && _conflict is null && _changes.Count > 0,
        };
    }

    // ---------------------------------------------------------------- queue

    /// <summary>Picks the commits in order until one stops the run; at the end version and changelog.</summary>
    private void RunQueue(BackportEnv env, List<Commit> todo)
    {
        for (var i = 0; i < todo.Count; i++)
        {
            var c = todo[i];
            env.Log($"Porting {c.ShortSha} {c.Message.Subject}");
            var checkpoint = Backport.Checkpoint(env.Git, Work);
            var r = Backport.Pick(env.Git, Work, c.Sha, env.Log);
            foreach (var path in r.Reverted)
            {
                _notes.Add($"- `{c.ShortSha}`: kept as in the target: `{path}`");
                if (path.Contains("src/generated/")) _notes.Add("- generated data changed in the source – run `runData` in this version");
            }
            switch (r.State)
            {
                case PickState.Ok:
                    _ported.Add(c);
                    break;
                case PickState.Empty:
                    _notes.Add($"- `{c.ShortSha}` is already contained, nothing to port");
                    break;
                case PickState.Failed:
                    _changes = Backport.Changes(env.Git, Work);
                    throw new PmException($"cherry-pick of {c.ShortSha} failed:\n{r.Output}");
                case PickState.Conflict:
                {
                    var rest = todo.Skip(i + 1).ToList();
                    if (!_useClaude || env.Cancel.IsCancelled)
                    {
                        StopAt(c, r.Conflicts, checkpoint, rest, env);
                        return;
                    }
                    var step = ClaudeResolve(env, c, r.Conflicts);
                    _notes.Add(step.Notes);
                    if (step.Resolved) _ported.Add(c);
                    else if (step.Skip) Backport.Restore(env.Git, Work, checkpoint);
                    else
                    {
                        var open = Backport.Unmerged(env.Git, Work);
                        StopAt(c, open.Count == 0 ? step.Left : open, checkpoint, rest, env);
                        if (step.Error is not null) throw new PmException(step.Error);
                        return;
                    }
                    break;
                }
            }
        }
        // version and changelog only come along when something was actually ported
        if (_sync && _ported.Count > 0) _versionNote = SyncVersionAndChangelog(env);
        _changes = Backport.Changes(env.Git, Work);
    }

    private void StopAt(Commit c, List<string> files, string checkpoint, List<Commit> rest, BackportEnv env)
    {
        env.Log($"⚠ Backport: conflict in {c.ShortSha} – {string.Join(", ", files)}");
        _conflict = new ConflictState(c, files, checkpoint);
        _queue = rest;
    }

    /// <summary>Status line and safety check after every step.</summary>
    private void Finish(BackportEnv env, Exception? error)
    {
        try
        {
            VerifyNoCommit(env);
        }
        catch (PmException e)
        {
            env.Log($"✖ {e.Message}");
        }
        if (_conflict is { } c) Status = $"Conflict in {c.Commit.ShortSha} {c.Commit.Message.Subject} – waiting for it to be resolved";
        else if (error is not null) Status = "Stopped";
        else
        {
            env.Log($"✔ Backport ready: {_ported.Count} commit(s), {_changes.Count} file(s) – nothing committed");
            Status = $"{_ported.Count} commit(s) ported – review, then apply to {Target.McVersion} (uncommitted)";
        }
    }

    /// <summary>Mod part of the version + CHANGELOG.md of the source.</summary>
    private string? SyncVersionAndChangelog(BackportEnv env)
    {
        if (Source.ModVersion is not { } raw)
        {
            _notes.Add("- the source has no mod_version – version not taken over");
            return null;
        }
        var version = Changelog.DisplayVersion(raw, Source.EffectiveMc);
        if (Backport.SyncVersion(Work, Target.EffectiveMc, version) is { } change) _notes.Add($"- version {change}");
        var sourceRef = Source.Branch ?? "HEAD";
        _notes.Add($"- {Backport.SyncChangelog(env.Git, Work, sourceRef, version)}");
        return $"version {version} and its changelog";
    }

    /// <summary>Safety net: the work folder must still be at the target commit – nothing may have been committed.</summary>
    private void VerifyNoCommit(BackportEnv env)
    {
        if (_baseSha is not { } b) return;
        var head = env.Git.Run(Work, "rev-parse", "HEAD").Text;
        if (head.Trim() == b) return;
        _baseSha = null;
        throw new PmException(
            $"The work folder is no longer at the target commit (HEAD {head} instead of {b}). Something was committed there – the result is not applied. Discard and start again.");
    }

    // ---------------------------------------------------------------- Claude

    private BackportContext Context()
    {
        var t = Target;
        return new BackportContext(t.Metadata?.Name ?? t.Name, t.Loader, Source.Branch ?? "", Source.EffectiveMc, t.Branch ?? "",
            t.EffectiveMc, Backport.ClaudeMd(Work));
    }

    /// <summary>Claude resolves the conflicts of <paramref name="c"/>; then the markers are checked.</summary>
    private ClaudeStep ClaudeResolve(BackportEnv env, Commit c, List<string> conflicts)
    {
        var text = env.Git.Run(Work, "show", "--format=fuller", "--no-color", c.Sha).Text;
        if (text.Length > MaxCommitChars) text = text[..MaxCommitChars] + $"\n… (diff cut, use `git show {c.ShortSha}` for the rest)";
        var prompt = BackportClaude.ConflictPrompt(Context(), c, text, conflicts);
        var heading = $"#### `{c.ShortSha}` {c.Message.Subject}";
        var o = CallClaude(env, prompt, $"conflict in {c.ShortSha}");
        if (!o.Ok)
        {
            return new ClaudeStep(false, false, conflicts.ToList(), $"{heading}\nClaude: {StatusName(o.Status)} – {o.Text}{DenialNotes(o.Denials)}",
                o.Status == ClaudeStatus.Cancelled ? "Cancelled." : $"Claude failed:\n{o.Text}");
        }
        var (decision, notes) = BackportClaude.Parse(o.Text);
        var section = $"{heading} – Claude: **{DecisionName(decision)}** (${Money(o.CostUsd)})\n\n{notes}{DenialNotes(o.Denials)}";
        if (decision == Decision.Skip)
        {
            env.Log($"Claude skips {c.ShortSha}");
            return new ClaudeStep(false, true, [], section, null);
        }
        var left = Backport.ContinueAfterConflict(env.Git, Work, conflicts);
        if (left.Count > 0) return new ClaudeStep(false, false, left, section, $"Claude left conflict markers in: {string.Join(", ", left)}");
        if (decision == Decision.Unknown) section += "\n\n_(Claude gave no DECISION line – the conflict is resolved, please review.)_";
        return new ClaudeStep(true, false, [], section, null);
    }

    /// <summary>One Claude call in the work folder with the restricted tools; afterwards HEAD and all branches of the repository must be unchanged.</summary>
    private ClaudeOutcome CallClaude(BackportEnv env, string prompt, string what)
    {
        var exe = env.Claude ?? throw new PmException("Claude Code CLI not found – set it in Settings → Claude Code CLI.");
        env.Log($"> claude -p ({what}, model {env.Model}, restricted tools, {(env.Tokens.Count == 0 ? "Claude Code login" : $"{env.Tokens.Count} token(s) from CLAUDE_API")})");
        var before = Backport.Snapshot(env.Git, Work);
        var request = new ClaudeRequest
        {
            WorkDir = Work,
            Prompt = prompt,
            Tools = BackportClaude.Tools(),
            Model = env.Model,
            MaxBudgetUsd = env.MaxBudgetUsd,
            Timeout = env.Timeout,
            Tokens = env.Tokens,
        };
        var outcome = Claude.Run(exe, request, line => env.Log($"claude: {line.Replace("\n", "\n    claude: ")}"), env.Cancel);
        if (before != Backport.Snapshot(env.Git, Work))
            throw new PmException(
                "Branches or HEAD of the work folder changed during the Claude run – something was committed. The result must not be used: Discard and start again.");
        return outcome;
    }

    /// <summary>Whether <paramref name="dir"/> is a backport work folder (it is hidden from the project list).</summary>
    public static bool IsWorkDir(string dir) => Path.GetDirectoryName(Util.Absolute(dir)) is { } p && Util.FileName(p) == Backport.WorkDirName;

    private static string Money(double usd) => usd.ToString("0.00", CultureInfo.InvariantCulture);

    private static string StatusName(ClaudeStatus s) => s.ToString().ToLowerInvariant();

    private static string DecisionName(Decision d) => d.ToString().ToLowerInvariant();

    private static string DenialNotes(List<string> denials)
    {
        if (denials.Count == 0) return "";
        var sb = new StringBuilder("\n\nDenied by the permission rules:\n");
        foreach (var d in denials) sb.Append($"- `{d.Replace('`', '\'')}`\n");
        return sb.ToString();
    }
}
