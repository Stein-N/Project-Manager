using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProjectManager.Models;
using ProjectManager.Services;

namespace ProjectManager.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public ObservableCollection<McVersion> Versions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Project), nameof(HasProject))]
    private object? _selected;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _busy;

    public string Master { get; } = Config.Master() ?? "";

    /// <summary>The selected project (null when a version or nothing is selected).</summary>
    public Project? Project => Selected as Project;

    public bool HasProject => Project is not null;

    [RelayCommand]
    public async Task RescanAsync()
    {
        if (Busy) return;
        if (Master.Length == 0)
        {
            Status = $"No master folder set ({Config.File}).";
            return;
        }
        Busy = true;
        Status = "Scanning…";
        var watch = Stopwatch.StartNew();
        var path = Project?.Path;
        var versions = await Scanner.ScanAsync(Master);
        Versions.Clear();
        foreach (var v in versions) Versions.Add(v);
        Selected = versions.SelectMany(v => v.Projects).FirstOrDefault(p => p.Path == path);
        var count = versions.Sum(v => v.Projects.Count);
        Status = $"{count} projects in {versions.Count} versions · scanned in {watch.ElapsedMilliseconds} ms";
        Busy = false;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Project is { } p) Open(p.Path);
    }

    [RelayCommand]
    private void OpenTerminal()
    {
        if (Project is not { } p) return;
        if (OperatingSystem.IsWindows())
        {
            Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = p.Path, UseShellExecute = true });
        }
        else
        {
            // the desktop's terminal: KDE, GNOME, then the Debian alternative
            foreach (var terminal in new[] { "konsole", "gnome-terminal", "x-terminal-emulator" })
            {
                if (Start(new ProcessStartInfo(terminal) { WorkingDirectory = p.Path, UseShellExecute = false })) return;
            }
            Status = "No terminal found.";
        }
    }

    private void Open(string path) =>
        Start(OperatingSystem.IsWindows()
            ? new ProcessStartInfo("explorer.exe", $"\"{path}\"")
            : new ProcessStartInfo("xdg-open", [path]) { UseShellExecute = false });

    private bool Start(ProcessStartInfo start)
    {
        try
        {
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Exception e)
        {
            Status = $"✖ {start.FileName}: {e.Message}";
            return false;
        }
    }
}
