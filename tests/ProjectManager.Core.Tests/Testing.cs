// Temporary folders and repositories for the tests.

namespace ProjectManager.Core.Tests;

/// <summary>A temporary folder that is deleted afterwards.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pm-test-{Guid.NewGuid():N}");

    public TempDir() => Directory.CreateDirectory(Path);

    public string this[string relative] => System.IO.Path.Combine(Path, relative);

    public void Dispose()
    {
        try
        {
            Util.DeleteRecursively(Path);
        }
        catch (Exception)
        {
        }
    }
}

public static class T
{
    public static Git Git() => new("git");

    /// <summary><c>git</c> with a fixed identity, failing the test on an error.</summary>
    public static string Sh(string dir, params string[] args)
    {
        string[] all =
        [
            "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "-c", "init.defaultBranch=main",
            "-c", "core.autocrlf=false", .. args,
        ];
        return Git().RunOk(dir, Core.Git.NoLog, all).Text;
    }

    public static void Write(string dir, string file, string content)
    {
        var p = Path.Combine(dir, file);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    public static string Read(string dir, string file) => File.ReadAllText(Path.Combine(dir, file)).Replace("\r\n", "\n");

    public static string Commit(string dir, string file, string content, string message)
    {
        Write(dir, file, content);
        Sh(dir, "add", "-A");
        Sh(dir, "commit", "-q", "-m", message);
        return Sh(dir, "rev-parse", "HEAD");
    }

    public static string CommitAll(string dir, string message)
    {
        Sh(dir, "add", "-A");
        Sh(dir, "commit", "-q", "-m", message);
        return Sh(dir, "rev-parse", "HEAD");
    }

    /// <summary>A new repository with one commit.</summary>
    public static string Repo(string parent, string name)
    {
        var dir = Path.Combine(parent, name);
        Directory.CreateDirectory(dir);
        Sh(dir, "init", "-q");
        // commands of the code under test (annotated tags, commits) need an identity; CI runners have none
        Sh(dir, "config", "user.name", "Test");
        Sh(dir, "config", "user.email", "test@example.com");
        Commit(dir, "README.md", "hello\n", "initial");
        return dir;
    }

    public static SortedDictionary<string, string> Props(params (string Key, string Value)[] pairs)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) map[k] = v;
        return map;
    }
}
