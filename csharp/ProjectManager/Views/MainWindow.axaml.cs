using Avalonia.Controls;

namespace ProjectManager.Views;

public partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow() => InitializeComponent();

    /// <summary>The next close really closes (Exit in the tray menu).</summary>
    public void AllowClose() => _allowClose = true;

    // the close button minimizes to the tray; the app ends with Exit in the tray menu
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
