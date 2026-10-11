using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ScreenTranslator.App.ViewModels;

namespace ScreenTranslator.App.Pages;

public partial class TranslationPage : Page
{
    public TranslationPage()
        : this(App.Controller?.TranslationSettings ?? new TranslationSettingsViewModel())
    {
    }

    public TranslationPage(TranslationSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        PropertyChangedEventManager.AddHandler(
            viewModel,
            ViewModel_OnPropertyChanged,
            nameof(TranslationSettingsViewModel.ApiKey));
    }

    private void ApiKeyBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is TranslationSettingsViewModel viewModel &&
            sender is PasswordBox passwordBox)
        {
            viewModel.ApiKey = passwordBox.Password;
        }
    }

    private void ViewModel_OnPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (sender is TranslationSettingsViewModel viewModel
            && string.IsNullOrEmpty(viewModel.ApiKey)
            && ApiKeyBox.Password.Length > 0)
        {
            ApiKeyBox.Clear();
        }
    }
}
