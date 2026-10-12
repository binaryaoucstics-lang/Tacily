using ScreenTranslator.App.ViewModels;

namespace ScreenTranslator.IntegrationTests.ViewModels;

public sealed class GeneralSettingsViewModelTests
{
    [Fact]
    public void Chinese_Prose_Share_Threshold_Defaults_To_50_And_Offers_Every_Allowed_Percent()
    {
        var viewModel = new GeneralSettingsViewModel();

        Assert.Equal(50, viewModel.BrowserChineseProseShareThresholdPercent);
        Assert.Equal(Enumerable.Range(10, 81), viewModel.BrowserChineseProseShareThresholdPercentChoices);
        Assert.Contains(viewModel.BrowserChineseProseShareThresholdPercent, viewModel.BrowserChineseProseShareThresholdPercentChoices);
    }

    [Fact]
    public void Chinese_Prose_Share_Threshold_Notifies_Only_When_The_Value_Changes()
    {
        var viewModel = new GeneralSettingsViewModel();
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        viewModel.BrowserChineseProseShareThresholdPercent = 63;
        viewModel.BrowserChineseProseShareThresholdPercent = 63;

        Assert.Equal(63, viewModel.BrowserChineseProseShareThresholdPercent);
        Assert.Equal([nameof(GeneralSettingsViewModel.BrowserChineseProseShareThresholdPercent)], changes);
    }

    [Theory]
    [InlineData(int.MinValue, 10)]
    [InlineData(9, 10)]
    [InlineData(10, 10)]
    [InlineData(51, 51)]
    [InlineData(90, 90)]
    [InlineData(91, 90)]
    [InlineData(int.MaxValue, 90)]
    public void Chinese_Prose_Share_Threshold_Clamps_Values_And_Notifies_Once(int value, int expected)
    {
        var viewModel = new GeneralSettingsViewModel();
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        viewModel.BrowserChineseProseShareThresholdPercent = value;
        viewModel.BrowserChineseProseShareThresholdPercent = value;

        Assert.Equal(expected, viewModel.BrowserChineseProseShareThresholdPercent);
        Assert.Equal([nameof(GeneralSettingsViewModel.BrowserChineseProseShareThresholdPercent)], changes);
    }

    [Fact]
    public void Chinese_Prose_Share_Threshold_Does_Not_Notify_When_Normalized_Value_Is_Unchanged()
    {
        var viewModel = new GeneralSettingsViewModel();
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        viewModel.BrowserChineseProseShareThresholdPercent = 50;
        Assert.Empty(changes);

        viewModel.BrowserChineseProseShareThresholdPercent = 10;
        changes.Clear();
        viewModel.BrowserChineseProseShareThresholdPercent = 9;
        viewModel.BrowserChineseProseShareThresholdPercent = int.MinValue;
        Assert.Equal(10, viewModel.BrowserChineseProseShareThresholdPercent);
        Assert.Empty(changes);

        viewModel.BrowserChineseProseShareThresholdPercent = 90;
        changes.Clear();
        viewModel.BrowserChineseProseShareThresholdPercent = 91;
        viewModel.BrowserChineseProseShareThresholdPercent = int.MaxValue;
        Assert.Equal(90, viewModel.BrowserChineseProseShareThresholdPercent);
        Assert.Empty(changes);
    }

    [Fact]
    public void Domain_Editors_Normalize_Valid_Entries_And_Report_Invalid_Input_Without_Saving_It()
    {
        var model = new GeneralSettingsViewModel { BrowserTranslationWhitelistText = "https://Example.com/page\nexample.com\nbad domain.com" };
        Assert.Equal(["example.com"], model.BrowserTranslationWhitelist);
        Assert.Contains("无效域名", model.BrowserSiteRulesValidation);
        model.BrowserTranslationBlacklist = ["*.Other.org"];
        Assert.Equal("other.org", model.BrowserTranslationBlacklistText);
        Assert.False(model.BrowserAutoPageTranslationEnabled);
    }
    [Fact]
    public void Full_Page_Translation_Is_OptIn_And_Image_Only_Preference_Remains_Independent()
    {
        var viewModel = new GeneralSettingsViewModel();
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        Assert.False(viewModel.BrowserAutoPageTranslationEnabled);
        Assert.Equal("程序规则", viewModel.BrowserPageJudgementMode);
        Assert.Equal(10, viewModel.BrowserPageTranslationConcurrency);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], viewModel.BrowserPageTranslationConcurrencies);

        viewModel.BrowserAutoPageTranslationEnabled = true;
        viewModel.BrowserAutoPageTranslationEnabled = true;

        Assert.True(viewModel.BrowserAutoPageTranslationEnabled);
        Assert.False(viewModel.BrowserAutoImageTranslationEnabled);
        Assert.Equal([nameof(GeneralSettingsViewModel.BrowserAutoPageTranslationEnabled)], changes);
    }

    [Fact]
    public void Page_Mode_And_Concurrency_Changes_Notify_Once_And_Coerce_Invalid_Values()
    {
        var viewModel = new GeneralSettingsViewModel();
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        viewModel.BrowserPageTranslationConcurrency = 3;
        changes.Clear();
        viewModel.BrowserPageTranslationConcurrency = 99;
        viewModel.BrowserPageTranslationConcurrency = 99;

        Assert.Equal("程序规则", viewModel.BrowserPageJudgementMode);
        Assert.Equal(10, viewModel.BrowserPageTranslationConcurrency);
        Assert.Equal([nameof(GeneralSettingsViewModel.BrowserPageTranslationConcurrency)], changes);

        viewModel.BrowserPageTranslationConcurrency = -5;
        Assert.Equal("程序规则", viewModel.BrowserPageJudgementMode);
        Assert.Equal(1, viewModel.BrowserPageTranslationConcurrency);
    }

    [Fact]
    public void Automatic_Browser_Translation_Is_OptIn_And_Notifies_Changes()
    {
        var viewModel = new GeneralSettingsViewModel();
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        Assert.False(viewModel.BrowserAutoImageTranslationEnabled);
        viewModel.BrowserAutoImageTranslationEnabled = true;
        viewModel.BrowserAutoImageTranslationEnabled = true;

        Assert.True(viewModel.BrowserAutoImageTranslationEnabled);
        Assert.Equal([nameof(GeneralSettingsViewModel.BrowserAutoImageTranslationEnabled), nameof(GeneralSettingsViewModel.BrowserAutoImageModeIndex)], changes);
    }

    [Fact]
    public void Image_Policy_Defaults_To_Off_And_Uses_The_Persisted_Independent_OptIn()
    {
        var model = new GeneralSettingsViewModel();
        Assert.Equal(2, model.BrowserAutoImageModes.Count);
        Assert.Equal(0, model.BrowserAutoImageModeIndex);
        model.BrowserAutoPageTranslationEnabled = true;
        Assert.Equal(0, model.BrowserAutoImageModeIndex);
        Assert.False(model.BrowserAutoImageTranslationEnabled);
        model.BrowserAutoImageModeIndex = 1;
        Assert.True(model.BrowserAutoImageTranslationEnabled);
        model.BrowserAutoImageTranslationEnabled = false;
        Assert.Equal(0, model.BrowserAutoImageModeIndex);
        model.BrowserAutoImageModeIndex = 99;
        Assert.False(model.BrowserAutoImageTranslationEnabled);
    }

    [Fact]
    public void Normal_And_Multiple_Commands_Remain_Distinct_Entries()
    {
        var viewModel = new GeneralSettingsViewModel();
        var normalRequests = 0;
        var multipleRequests = 0;
        viewModel.StartCaptureRequested += (_, _) => normalRequests++;
        viewModel.StartContinuousCaptureRequested +=
            (_, _) => multipleRequests++;

        viewModel.StartCaptureCommand.Execute(null);
        viewModel.StartContinuousCaptureCommand.Execute(null);

        Assert.Equal(1, normalRequests);
        Assert.Equal(1, multipleRequests);
    }

    [Fact]
    public void Continuous_Command_Raises_Request()
    {
        var viewModel = new GeneralSettingsViewModel();
        var raised = false;
        viewModel.StartContinuousCaptureRequested +=
            (_, _) => raised = true;

        viewModel.StartContinuousCaptureCommand.Execute(null);

        Assert.True(raised);
    }

    [Fact]
    public void Active_Continuous_Session_Disables_Both_Start_Commands()
    {
        var viewModel = new GeneralSettingsViewModel
        {
            IsContinuousCaptureActive = true,
        };

        Assert.False(viewModel.StartCaptureCommand.CanExecute(null));
        Assert.False(viewModel.StartContinuousCaptureCommand.CanExecute(null));
    }

    [Fact]
    public void Status_Shows_Pending_Count()
    {
        var viewModel = new GeneralSettingsViewModel
        {
            IsContinuousCaptureActive = true,
            ContinuousPendingCount = 3,
        };

        Assert.Equal(
            "连续框选中 · 待处理 3",
            viewModel.ContinuousCaptureStatusText);
    }
}
