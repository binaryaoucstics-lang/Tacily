using System.Windows.Controls;
using ScreenTranslator.App.ViewModels;

namespace ScreenTranslator.App.Pages;

public partial class PrivacyPage : Page
{
    public PrivacyPage()
        : this(App.Controller?.PrivacySettings ?? new PrivacySettingsViewModel())
    {
    }

    public PrivacyPage(PrivacySettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
