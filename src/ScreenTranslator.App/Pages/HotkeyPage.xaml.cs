using System.Windows.Input;
using System.Windows.Controls;
using ScreenTranslator.App.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ScreenTranslator.App.Pages;

public partial class HotkeyPage : Page
{
    public HotkeyPage()
        : this(App.Controller?.HotkeySettings ?? new HotkeySettingsViewModel())
    {
    }

    public HotkeyPage(HotkeySettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Page_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not HotkeySettingsViewModel viewModel ||
            !viewModel.IsRecording)
        {
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            viewModel.CancelRecordingCommand.Execute(null);
        }
        else if (key == Key.Back)
        {
            viewModel.UseDefaultCommand.Execute(null);
        }
        else
        {
            viewModel.AcceptKeyboardInput(Keyboard.Modifiers, key);
        }
    }
}
