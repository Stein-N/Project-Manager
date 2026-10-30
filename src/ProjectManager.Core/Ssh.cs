// SSH access for Git: repositories are always cloned over SSH, so pushes use the SSH key and never ask for a password.

namespace ProjectManager.Core;

public static class Ssh
{
    private static readonly string[] KeyNames = ["id_ed25519", "id_ecdsa", "id_rsa", "id_dsa", "id_ed25519_sk", "id_ecdsa_sk"];

    /// <summary>The private keys in <c>~/.ssh</c> that git's ssh finds by default.</summary>
    public static List<string> Keys(string home)
    {
        var dir = Path.Combine(home, ".ssh");
        return KeyNames.Select(n => Path.Combine(dir, n)).Where(File.Exists).ToList();
    }

    public static bool HasKey(string home) => Keys(home).Count > 0;

    /// <summary>
    /// <c>https://github.com/user/mod.git</c> → <c>git@github.com:user/mod.git</c>. Other URLs (SSH, local paths,
    /// other hosts) stay as they are.
    /// </summary>
    public static string ToSshUrl(string url)
    {
        var u = url.Trim();
        const string prefix = "https://github.com/";
        if (!u.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return u;
        var path = u[prefix.Length..].Trim('/');
        if (path.Count(c => c == '/') != 1) return u;
        return $"git@github.com:{(path.EndsWith(".git") ? path : path + ".git")}";
    }

    /// <summary>Creates <c>~/.ssh/id_ed25519</c> without a passphrase; returns the public key to add on GitHub.</summary>
    public static string Generate(string home, string comment)
    {
        var dir = Path.Combine(home, ".ssh");
        var key = Path.Combine(dir, "id_ed25519");
        if (File.Exists(key)) throw new PmException($"{key} already exists.");
        Directory.CreateDirectory(dir);
        if (!Util.IsWindows) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var r = Proc.Output(Proc.Command("ssh-keygen", ["-t", "ed25519", "-N", "", "-C", comment, "-f", key]));
            if (!r.Success) throw new PmException($"ssh-keygen failed:\n{(r.Stderr + r.Stdout).Trim()}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new PmException("ssh-keygen was not found. Please install OpenSSH.");
        }
        return File.ReadAllText(key + ".pub").Trim();
    }

    /// <summary>
    /// The repositories whose <c>origin</c> is a GitHub HTTPS URL: standalone clones and shared repositories
    /// (linked worktrees use the origin of their repository), with the SSH URL to switch to.
    /// </summary>
    public static List<(string Path, string Label, string Url)> HttpsRemotes(ScanResult scan) =>
        scan.Projects.Where(p => p.StandaloneClone).Select(p => (p.Path, p.Label, p.RemoteUrl))
            .Concat(scan.Repos.Select(r => (r.Path, r.Name, r.Url)))
            .Where(r => r.Item3.Length > 0 && ToSshUrl(r.Item3) != r.Item3.Trim())
            .Select(r => (r.Item1, r.Item2, ToSshUrl(r.Item3))).ToList();
}
