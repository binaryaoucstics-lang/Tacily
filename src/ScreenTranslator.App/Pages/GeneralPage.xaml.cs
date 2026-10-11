using System.Windows.Controls;
using ScreenTranslator.App.ViewModels;

namespace ScreenTranslator.App.Pages;

public partial class GeneralPage : Page
{
    public GeneralPage()
        : this(App.Controller?.GeneralSettings ?? new GeneralSettingsViewModel())
    {
    }

    public GeneralPage(GeneralSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
