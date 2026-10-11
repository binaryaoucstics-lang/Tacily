using System.Windows.Controls;
using ScreenTranslator.App.ViewModels;

namespace ScreenTranslator.App.Pages;

public partial class AppearancePage : Page
{
    public AppearancePage()
        : this(App.Controller?.AppearanceSettings ?? new AppearanceSettingsViewModel())
    {
    }

    public AppearancePage(AppearanceSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
