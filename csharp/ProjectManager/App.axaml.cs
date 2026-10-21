using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ProjectManager.ViewModels;
using ProjectManager.Views;

namespace ProjectManager;

public partial class App : Application
{
    private MainWindow? _window;
    private MainViewModel? _model;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _model = new MainViewModel();
            _window = new MainWindow { DataContext = _model };
            desktop.MainWindow = _window;
            _window.Show();
            _ = _model.RescanAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void TrayClicked(object? sender, EventArgs e) => ShowWindow();

    private void ShowClicked(object? sender, EventArgs e) => ShowWindow();

    private void RescanClicked(object? sender, EventArgs e) => _ = _model?.RescanAsync();

    private void ExitClicked(object? sender, EventArgs e)
    {
        _window?.AllowClose();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
