// Version primers: listing, reading and generating them (see Core.Primer) with the backport model and budget.

using ProjectManager.Core;

namespace ProjectManager.Services;

public sealed partial class Backend
{
    private readonly List<ClaudeCancel> _primerRuns = [];
    private volatile bool _primerCancelled;
    private int _primerRunning;

    /// <summary>Mojang's version manifest, loaded once per start.</summary>
    private string? _primerManifest;

    public Task<List<PrimerFile>> Primers() => Run(() => Primer.List(Master()));

    /// <summary>Every Minecraft release since 1.21.1 plus the version folders; only the folders when Mojang cannot be reached.</summary>
    public Task<List<string>> PrimerVersions() => Run(() =>
    {
        var folders = Scan.Versions;
        if (_primerManifest is { } cached) return Primer.OfferedVersions(cached, folders);
        try
        {
            var manifest = Http.GetOk(Primer.VersionManifest);
            var offered = Primer.OfferedVersions(manifest, folders);
            _primerManifest = manifest;
            return offered;
        }
        catch (PmException e)
        {
            Log($"⚠ Minecraft version list: {e.Message}");
            return folders.ToList();
        }
    });

    public Task<string> PrimerText(string path) => Run(() => Util.ReadOrEmpty(path));

    /// <summary>The loaders of the mods in both version folders – preselected for a new primer.</summary>
    public List<string> PrimerLoaders(string from, string to) =>
        Scan.Projects.Where(p => p.McVersion == from || p.McVersion == to)
            .SelectMany(p => Primer.LoaderNames(p.Loader)).Distinct()
            .OrderBy(l => Array.IndexOf(Primer.AllLoaders, l)).ToList();

    /// <summary>Mods that exist in both version folders (same repository, both with a branch).</summary>
    public List<PrimerPort> PrimerPorts(string from, string to) =>
        Scan.Projects.Where(p => p.McVersion == from && p.Branch is not null)
            .SelectMany(a => Scan.Projects.Where(b => b.McVersion == to && b.Branch is not null && a.SameRepo(b))
                .Select(b => new PrimerPort(a.Name, b.Path, a.Branch!, b.Branch!)))
            .ToList();

    /// <summary>Collects the sources and lets Claude write the primer; returns the path of the file.</summary>
    public Task<string> PrimerCreate(string from, string to, List<string> loaders, bool ownPorts) => Run(() =>
    {
        (from, to) = (from.Trim(), to.Trim());
        if (from.Length == 0 || to.Length == 0) throw new PmException("Choose both versions.");
        if (Versions.Compare(from, to) >= 0) throw new PmException($"{to} is not newer than {from}.");
        if (from.Contains("-to-") || to.Contains("-to-") || from.Any(c => Path.GetInvalidFileNameChars().Contains(c)) ||
            to.Any(c => Path.GetInvalidFileNameChars().Contains(c)))
            throw new PmException("Not a valid version name.");
        var config = Config();
        var exe = Claude.Find(config.Claude) ?? throw new PmException("Claude Code CLI not found – set it in Settings → Claude Code CLI.");
        if (Interlocked.Exchange(ref _primerRunning, 1) == 1) throw new PmException("A primer is already being generated.");
        _primerCancelled = false;
        try
        {
            var master = Master();
            var request = new PrimerRequest(from, to, Primer.AllLoaders.Where(loaders.Contains).ToList(), ownPorts ? PrimerPorts(from, to) : []);
            var log = SubLogger();
            Log($"Primer {from} → {to} ({(request.Loaders.Count == 0 ? "vanilla" : $"vanilla, {string.Join(", ", request.Loaders)}")}" +
                $"{(request.Ports.Count > 0 ? $", {request.Ports.Count} own port(s)" : "")}): loading the sources…");
            var token = GithubToken();
            string Get(string url)
            {
                if (_primerCancelled) throw new PmException("Cancelled.");
                var github = url.StartsWith("https://api.github.com/") && token.Length > 0;
                var r = github ? Http.Get(url, ("Authorization", $"Bearer {token}")) : Http.Get(url);
                return r.Status == 200 ? r.Body : throw new PmException($"HTTP {r.Status} ({url})");
            }
            var forge = _versions?.Lists.GetValueOrDefault("forge") ?? [];
            var (sources, missing) = Primer.Collect(request, Get, forge, Git, log);
            if (_primerCancelled) throw new PmException("Cancelled.");
            var chunks = Primer.Chunks(sources).Count;
            Log($"Primer {from} → {to}: {sources.Count} source(s), {sources.Sum(s => s.Text.Length) / 1000}k chars → {chunks} Claude call(s) for the notes + 1 for the primer");
            var tokens = ClaudeTokens();
            ClaudeOutcome Call(string prompt, string what)
            {
                var cancel = new ClaudeCancel();
                lock (_primerRuns)
                {
                    if (_primerCancelled) return new ClaudeOutcome { Status = ClaudeStatus.Cancelled, Text = "Cancelled." };
                    _primerRuns.Add(cancel);
                }
                Log($"> claude -p ({what}, model {config.BackportModel}, {prompt.Length / 1000}k chars, no tools)");
                try
                {
                    return Claude.Run(exe, new ClaudeRequest
                    {
                        WorkDir = Primer.Dir(master),
                        Prompt = prompt,
                        Tools = new ClaudeTools(),
                        Model = config.BackportModel,
                        MaxBudgetUsd = config.BackportMaxBudgetUsd,
                        Timeout = TimeSpan.FromMinutes(Math.Max(30, config.ClaudeTimeoutMinutes)),
                        Tokens = tokens,
                    }, line => log($"claude: {line}"), cancel);
                }
                finally
                {
                    lock (_primerRuns) _primerRuns.Remove(cancel);
                }
            }
            var dir = Primer.Dir(master);
            Directory.CreateDirectory(dir);
            var text = Primer.Generate(request, sources, missing, config.BackportModel, Path.Combine(dir, ".cache"), Call, log,
                () => _primerCancelled, DateTime.Now);
            var file = Path.Combine(dir, Primer.FileName(from, to));
            Util.WriteText(file, text);
            Log($"✔ Primer saved: {file}");
            return file;
        }
        catch (PmException e)
        {
            Log($"✖ Primer {from} → {to}: {e.Message}");
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _primerRunning, 0);
        }
    });

    public void PrimerCancel()
    {
        _primerCancelled = true;
        lock (_primerRuns)
            foreach (var c in _primerRuns)
                c.Cancel();
    }
}
