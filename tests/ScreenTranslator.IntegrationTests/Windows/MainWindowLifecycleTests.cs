using ScreenTranslator.App.ViewModels;
using ScreenTranslator.App.Windows;
using ScreenTranslator.IntegrationTests.TestInfrastructure;
using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests.Windows;

public sealed class MainWindowLifecycleTests
{
    [Fact]
    public async Task Close_Hides_To_Tray_And_Shutdown_Closes_Window()
    {
        await StaTest.RunAsync(() =>
        {
            var application = new ScreenTranslator.App.App();
            application.InitializeComponent();
            var window = new MainWindow(new MainWindowViewModel())
            {
                HideOnClose = true,
            };
            var wasClosed = false;
            window.Closed += (_, _) => wasClosed = true;

            window.Show();
            window.Close();

            Assert.True(window.IsLoaded);
            Assert.False(window.IsVisible);
            Assert.False(wasClosed);

            window.IsApplicationShuttingDown = true;
            window.Close();

            Assert.True(wasClosed);

            var overlayViewModel = new TranslationResultViewModel();
            var overlay = new TextOverlayWindow(overlayViewModel);
            var clearAllCount = 0;
            overlayViewModel.ClearAllRequested += (_, _) => clearAllCount++;

            overlay.Show();
            overlay.SetSourceWindowActive(false);
            Assert.False(overlay.IsVisible);

            overlay.SetSourceWindowActive(true);
            Assert.True(overlay.IsVisible);

            overlayViewModel.ClearAllCommand.Execute(null);
            Assert.Equal(1, clearAllCount);
            overlay.Close();

            var floatingModel = new TranslationResultViewModel { IsBusy = true };
            var floating = new SidePanelWindow(floatingModel);
            var workArea = new DipRect(0, 0, 1920, 1080);
            floating.ApplyPlacement(SidePanelBoundsService.Place(new DipRect(100, 100, 1, 1), workArea,
                new DipSize(320, 88), null), workArea);
            Assert.Equal(88, floating.Height);
            Assert.Equal(320, floating.Width);
            floatingModel.ApplyStreamUpdate(new TranslationStreamUpdate("译"));
            Assert.Equal(520, floating.Height);
            Assert.Equal(448, floating.Width);
            floating.Close();

            application.Shutdown();
        });
    }
}
