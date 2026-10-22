namespace ProjectManager.Core.Tests;

public class LogFilesTests
{
    private static readonly DateOnly Day = new(2026, 10, 22);

    [Fact]
    public void LinesAreWrittenPerDayAndReadBack()
    {
        using var tmp = new TempDir();
        using (var files = new LogFiles(tmp["logs"]))
        {
            files.Append(Day, new LogLine("10:00:00", "✔ done"));
            files.Append(Day, new LogLine("10:00:01", "claude: first\nsecond\n  indented"));
            files.Append(Day.AddDays(1), new LogLine("00:00:01", "next day"));
        }
        Assert.Equal("[10:00:00] ✔ done\n[10:00:01] claude: first\n           second\n             indented\n",
            File.ReadAllText(Path.Combine(tmp["logs"], "2026-10-22.log")));
        using var again = new LogFiles(tmp["logs"]);
        Assert.Equal([new LogLine("10:00:00", "✔ done"), new LogLine("10:00:01", "claude: first\nsecond\n  indented")], again.Load(Day, 1000));
        Assert.Equal([new LogLine("00:00:01", "next day")], again.Load(Day.AddDays(1), 1000));
        Assert.Empty(again.Load(Day.AddDays(2), 1000));
    }

    [Fact]
    public void LoadKeepsTheNewestEntries()
    {
        using var tmp = new TempDir();
        using var files = new LogFiles(tmp.Path);
        foreach (var i in Enumerable.Range(0, 5)) files.Append(Day, new LogLine($"10:00:0{i}", $"line {i}"));
        Assert.Equal(["line 3", "line 4"], files.Load(Day, 13).Select(l => l.Text));
    }

    [Fact]
    public void CleanUpDeletesOldFilesOnly()
    {
        using var tmp = new TempDir();
        foreach (var name in new[] { "2026-10-22.log", "2026-10-09.log", "2026-10-08.log", "notes.log" }) T.Write(tmp.Path, name, "x");
        using var files = new LogFiles(tmp.Path);
        Assert.Equal(1, files.CleanUp(Day, 14));
        Assert.Equal(["2026-10-09.log", "2026-10-22.log", "notes.log"], Directory.GetFiles(tmp.Path).Select(Path.GetFileName).Order());
        Assert.Equal(1, files.CleanUp(Day, 1));
        Assert.False(File.Exists(Path.Combine(tmp.Path, "2026-10-09.log")));
    }
}
