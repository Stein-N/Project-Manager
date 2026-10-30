using ProjectManager.Core;

namespace ProjectManager.Core.Tests;

public class SshTests
{
    [Theory]
    [InlineData("https://github.com/user/mod.git", "git@github.com:user/mod.git")]
    [InlineData("https://github.com/user/mod/", "git@github.com:user/mod.git")]
    [InlineData("git@github.com:user/mod.git", "git@github.com:user/mod.git")]
    [InlineData("https://example.com/user/mod.git", "https://example.com/user/mod.git")]
    public void ToSshUrl(string input, string expected) => Assert.Equal(expected, Ssh.ToSshUrl(input));

    [Fact]
    public void SameUrl_ignores_protocol() =>
        Assert.True(Worktrees.SameUrl("https://github.com/User/Mod.git", "git@github.com:user/mod.git"));

    [Fact]
    public void HttpsRemotes_lists_only_github_https_repos()
    {
        var scan = new ScanResult([], [], [
            new SharedRepo("a", "/r/a", "https://github.com/u/a.git", []),
            new SharedRepo("b", "/r/b", "git@github.com:u/b.git", []),
        ]);
        var r = Assert.Single(Ssh.HttpsRemotes(scan));
        Assert.Equal(("/r/a", "a", "git@github.com:u/a.git"), r);
    }
}
