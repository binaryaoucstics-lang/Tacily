using System.ComponentModel;
using System.Windows;
using ScreenTranslator.App.Pages;
using ScreenTranslator.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ScreenTranslator.App.Windows;

public partial class MainWindow : FluentWindow
{
    private bool _isThemeWatcherActive;

    public MainWindow()
        : this(App.Controller?.MainWindow ?? new MainWindowViewModel())
    {
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
        _isThemeWatcherActive = true;
    }

    public bool HideOnClose { get; set; } = true;

    public bool IsApplicationShuttingDown { get; set; }

    private void NavigationRoot_OnLoaded(object sender, RoutedEventArgs e)
    {
        NavigationRoot.Navigate(typeof(GeneralPage));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (HideOnClose && !IsApplicationShuttingDown)
        {
            e.Cancel = true;
            base.OnClosing(e);
            if (e.Cancel)
            {
                Hide();
                return;
            }

            StopThemeWatcher();
            return;
        }

        StopThemeWatcher();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        StopThemeWatcher();
        base.OnClosed(e);
    }

    private void StopThemeWatcher()
    {
        if (!_isThemeWatcherActive)
        {
            return;
        }

        _isThemeWatcherActive = false;
        try
        {
            SystemThemeWatcher.UnWatch(this);
        }
        catch (InvalidOperationException)
        {
            // The native handle can already be gone during forced Windows shutdown.
        }
    }
}
