using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenTranslator.App.Pages;
using ScreenTranslator.App.Services.Capture;
using ScreenTranslator.App.ViewModels;
using ScreenTranslator.App.Windows;
using ScreenTranslator.Core.Models;
using Windows.Media.Ocr;
using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.App.Services.Overlays;
using SkiaSharp;
using ScreenTranslator.Core.Translation;
using ScreenTranslator.Core.Authentication;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Browser;
using ScreenTranslator.App.Services.Browser;

namespace ScreenTranslator.App.Infrastructure;

/// <summary>Checks the published runtime without creating a tray icon, registering input hooks, or reading an API key.</summary>
public static class InstallationCheck
{
    public static async Task RunAsync(string outputPath, bool offline = false)
    {
        var monitors = offline ? [] : new GdiScreenCaptureService().GetMonitors();
        var alphaOpaque = true;
        if (!offline)
        {
            var captured = await new GdiScreenCaptureService().CaptureAsync(monitors.First());
            var capturedPixels = captured.Bitmap.Pixels.ToArray();
            for (var index = 3; index < capturedPixels.Length; index += 4) alphaOpaque &= capturedPixels[index] == byte.MaxValue;
        }

        // Construct and render the actual compiled controls off screen. This executes their real XAML and resources.
        var translationModel = new TranslationSettingsViewModel();
        var settingsPage = new TranslationPage(translationModel);
        settingsPage.Measure(new System.Windows.Size(760, 900));
        settingsPage.Arrange(new Rect(0, 0, 760, 900));
        settingsPage.UpdateLayout();
        var folder = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(folder);
        var chatGptOAuthControlsVerified = await VerifyChatGptControlsAsync(folder);
        var generalModel = new GeneralSettingsViewModel();
        var automaticDefaultsOff = !generalModel.BrowserAutoImageTranslationEnabled;
        var automaticPageDefaultsOff = !generalModel.BrowserAutoPageTranslationEnabled;
        var generalPage = new GeneralPage(generalModel);
        Render(generalPage, 760, 2300, Path.Combine(folder, "general-auto-preview.png"));
        var automaticPageSettingsVerified = automaticPageDefaultsOff
            && generalPage.FindName("BrowserAutoPageToggle") is Wpf.Ui.Controls.ToggleSwitch { IsChecked: false }
            && generalPage.FindName("BrowserPageJudgementLabel") is System.Windows.Controls.TextBlock { Text: "程序规则（固定）" }
            && generalPage.FindName("BrowserPageConcurrencyCombo") is System.Windows.Controls.ComboBox concurrencyControl
            && concurrencyControl.Items.Count == 10 && concurrencyControl.SelectedItem is int selectedConcurrency && selectedConcurrency == 10;
        var imageMode = generalPage.FindName("BrowserAutoImageModeCombo") as System.Windows.Controls.ComboBox;
        var automaticImageSettingsVerified = imageMode is { SelectedIndex: 0 } && imageMode.Items.Count == 2;
        generalModel.BrowserAutoPageTranslationEnabled = true;
        generalPage.UpdateLayout();
        automaticImageSettingsVerified &= imageMode?.SelectedIndex == 0 && !generalModel.BrowserAutoImageTranslationEnabled;
        if (imageMode is not null) {
            imageMode.SelectedIndex = 1; generalPage.UpdateLayout();
            automaticImageSettingsVerified &= generalModel.BrowserAutoImageTranslationEnabled;
            imageMode.SelectedIndex = 0; generalPage.UpdateLayout();
            automaticImageSettingsVerified &= !generalModel.BrowserAutoImageTranslationEnabled;
        }
        generalModel.BrowserAutoPageTranslationEnabled = false;
        var whiteBox = generalPage.FindName("BrowserSiteWhitelistBox") as System.Windows.Controls.TextBox;
        var blackBox = generalPage.FindName("BrowserSiteBlacklistBox") as System.Windows.Controls.TextBox;
        var websiteRuleControlsVerified = whiteBox is not null && blackBox is not null;
        if (whiteBox is not null && blackBox is not null) {
            whiteBox.Text = "https://Example.com/path\nnews.example.org";
            whiteBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            blackBox.Text = "blocked.example.com";
            blackBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            generalPage.UpdateLayout();
            websiteRuleControlsVerified &= generalModel.BrowserTranslationWhitelist.SequenceEqual(new[] { "example.com", "news.example.org" })
                && generalModel.BrowserTranslationBlacklist.SequenceEqual(new[] { "blocked.example.com" });
            generalModel.BrowserTranslationWhitelist = []; generalModel.BrowserTranslationBlacklist = [];
        }
        var cacheControlsVerified = generalPage.FindName("BrowserPageCacheToggle") is System.Windows.Controls.CheckBox { IsChecked: true }
            && generalPage.FindName("BrowserPageCacheSizeCombo") is System.Windows.Controls.ComboBox { SelectedItem: 256 };
        var motionToggle = generalPage.FindName("TranslationAnimationsToggle") as System.Windows.Controls.CheckBox;
        var motionControlsVerified = motionToggle is { IsChecked: true } && generalModel.TranslationAnimationsEnabled;
        if (motionToggle is not null) {
            motionToggle.IsChecked = false; generalPage.UpdateLayout(); motionControlsVerified &= !generalModel.TranslationAnimationsEnabled;
            generalModel.TranslationAnimationsEnabled = true; generalPage.UpdateLayout(); motionControlsVerified &= motionToggle.IsChecked == true;
        }
        var cacheSize = (System.Windows.Controls.ComboBox)generalPage.FindName("BrowserPageCacheSizeCombo");
        var cacheToggle = (System.Windows.Controls.CheckBox)generalPage.FindName("BrowserPageCacheToggle");
        cacheSize.SelectedItem = 512;
        cacheToggle.IsChecked = false;
        generalPage.UpdateLayout();
        cacheControlsVerified &= generalModel.BrowserPageCacheMegabytes == 512 && !generalModel.BrowserPageCacheEnabled;
        var clearRequested = false;
        generalModel.ClearPageCacheRequested += (_, _) => clearRequested = true;
        var clearButton = (Wpf.Ui.Controls.Button)generalPage.FindName("ClearBrowserPageCacheButton");
        clearButton.Command.Execute(null);
        cacheControlsVerified &= clearRequested;
        generalModel.BrowserPageCacheEnabled = true;
        generalModel.BrowserPageCacheMegabytes = 256;
        Render(generalPage, 760, 2850, Path.Combine(folder, "general-cache-preview.png"));
        Render(settingsPage, 760, 900, Path.Combine(folder, "settings-preview.png"));
        var translationProviderControlsVerified = settingsPage.FindName("TranslationProviderCombo") is System.Windows.Controls.ComboBox providerControl
            && providerControl.Items.Count == 4 && (string?)providerControl.SelectedItem == "DeepSeek 官方";
        foreach (var provider in new[] { "OpenAI", "Claude" })
        {
            // Actual WPF binding edits, not a separate mock settings UI. No key or network is used.
            ((System.Windows.Controls.ComboBox)settingsPage.FindName("TranslationProviderCombo")).SelectedItem = provider == "OpenAI" ? "OpenAI Responses" : "Anthropic Messages";
            Render(settingsPage, 760, 1750, Path.Combine(folder, provider.ToLowerInvariant() + "-settings-preview.png"));
            var modelControl = (System.Windows.Controls.ComboBox)settingsPage.FindName("TranslationModelCombo");
            translationProviderControlsVerified &= translationModel.SelectedProvider == provider && modelControl.IsEnabled
                && modelControl.Text == translationModel.SelectedModel && modelControl.Items.Count >= 3;
            if (provider == "OpenAI") modelControl.Text = "gpt-6.1-sol";
            else modelControl.Text = "claude-opus-5-5";
            settingsPage.UpdateLayout();
            translationProviderControlsVerified &= translationModel.SelectedModel == modelControl.Text
                && translationModel.ThinkingDescription.Contains("无法关闭", StringComparison.Ordinal);
        }
        translationModel.SelectedProvider = "DeepSeek";
        settingsPage.UpdateLayout();
        translationProviderControlsVerified &= translationModel.SelectedModel == "deepseek-flash"
            && !((System.Windows.Controls.ComboBox)settingsPage.FindName("TranslationModelCombo")).IsEnabled;
        translationModel.AddPresetCommand.Execute(null);
        settingsPage.UpdateLayout();
        var presetControl = (System.Windows.Controls.ComboBox)settingsPage.FindName("TranslationPresetCombo");
        var nameControl = (System.Windows.Controls.TextBox)settingsPage.FindName("TranslationPresetNameBox");
        nameControl.Text = "本机测试预设";
        ((System.Windows.Controls.ComboBox)settingsPage.FindName("TranslationModelCombo")).Text = "local-vision";
        ((System.Windows.Controls.TextBox)settingsPage.FindName("TranslationBaseUrlBox")).Text = "http://127.0.0.1:9999/v1";
        settingsPage.UpdateLayout();
        var presetSettings = translationModel.CollectProviderSettings(new AppSettings());
        translationProviderControlsVerified &= presetControl.Items.Count == 1 && nameControl.IsEnabled
            && translationModel.PresetName == "本机测试预设"
            && TranslationProviderConfiguration.Model(presetSettings) == "local-vision"
            && TranslationProviderConfiguration.Endpoint(TranslationProviderKind.ChatCompletions, TranslationProviderConfiguration.BaseUrl(presetSettings)).AbsolutePath == "/v1/chat/completions";
        Render(settingsPage, 760, 1900, Path.Combine(folder, "custom-preset-settings-preview.png"));
        // Execute the real read command and ComboBox binding with metadata
        // supplied in memory. Never resolve a user key or perform HTTP here.
        var modelListButton = settingsPage.FindName("ReadModelsButton") as Wpf.Ui.Controls.Button;
        var modelListCombo = (System.Windows.Controls.ComboBox)settingsPage.FindName("TranslationModelCombo");
        translationModel.ModelListLoader = (_, _) => Task.FromResult(new TranslationModelList(["local-chat", "local-vision", "test-model"]));
        settingsPage.UpdateLayout();
        var modelListControlsVerified = modelListButton is not null && modelListButton.Visibility == Visibility.Visible
            && ReferenceEquals(modelListButton.Command, translationModel.LoadModelsCommand)
            && translationModel.LoadModelsCommand.CanExecute(null);
        await translationModel.LoadModelsCommand.ExecuteAsync(null);
        translationModel.IsModelListOpen = false; settingsPage.UpdateLayout();
        modelListControlsVerified &= modelListCombo.Items.Count == 3 && modelListCombo.Text == "local-vision"
            && translationModel.SelectedModel == "local-vision" && translationModel.ModelListStatus.Contains("3 个模型", StringComparison.Ordinal);
        modelListCombo.SelectedItem = "test-model"; settingsPage.UpdateLayout();
        modelListCombo.GetBindingExpression(System.Windows.Controls.ComboBox.TextProperty)?.UpdateSource();
        modelListControlsVerified &= translationModel.SelectedModel == "test-model"
            && TranslationProviderConfiguration.Model(translationModel.CollectProviderSettings(new AppSettings())) == "test-model";
        Render(settingsPage, 760, 1900, Path.Combine(folder, "model-list-settings-preview.png"));
        // Native proxy controls are verified with synthetic metadata, without reading Clash or user keys.
        translationModel.ClashInstallationProbe = () => true;
        translationModel.NetworkMode = "Clash 指定节点";
        translationModel.ClashNodesLoader = _ => Task.FromResult<IReadOnlyList<string>>(["测试节点 A", "测试节点 B"]);
        await translationModel.ReadClashNodesCommand.ExecuteAsync(null);
        settingsPage.UpdateLayout();
        var nodeCombo = (System.Windows.Controls.ComboBox)settingsPage.FindName("ClashNodeCombo");
        nodeCombo.SelectedValue = "测试节点 B"; settingsPage.UpdateLayout();
        nodeCombo.GetBindingExpression(System.Windows.Controls.ComboBox.SelectedValueProperty)?.UpdateSource();
        var networkControlsVerified = translationModel.SelectedClashNode == "测试节点 B"
            && TranslationProviderConfiguration.Network(translationModel.CollectProviderSettings(new())).Mode == ApiNetworkMode.ClashNode
            && settingsPage.FindName("ApiNetworkModeCombo") is System.Windows.Controls.ComboBox { IsEnabled: true }
            && nodeCombo.Items.Count == 2 && nodeCombo.Visibility == Visibility.Visible;
        translationModel.ClashNodeTester = (name, _) => Task.FromResult(new ScreenTranslator.App.Services.Networking.ClashNodeProbeResult(name,
            ScreenTranslator.App.Services.Networking.ClashNodeHealth.Available, name.EndsWith("A", StringComparison.Ordinal) ? 150 : 35));
        await translationModel.TestAllClashNodesCommand.ExecuteAsync(null); settingsPage.UpdateLayout();
        networkControlsVerified &= translationModel.ClashNodeChoices[0].Name == "测试节点 B" && translationModel.SelectedClashNode == "测试节点 B";
        Render((FrameworkElement)settingsPage.FindName("ApiNetworkCard"), 680, 375, Path.Combine(folder, "api-network-clash-preview.png"));
        translationModel.ClashInstallationProbe = () => false; settingsPage.UpdateLayout();
        networkControlsVerified &= ((FrameworkElement)settingsPage.FindName("ClashDownloadPanel")).Visibility == Visibility.Visible;
        Render((FrameworkElement)settingsPage.FindName("ApiNetworkCard"), 680, 480, Path.Combine(folder, "api-network-no-clash-preview.png"));
        translationModel.NetworkMode = "直连"; settingsPage.UpdateLayout();
        networkControlsVerified &= nodeCombo.Visibility == Visibility.Collapsed
            && ((FrameworkElement)settingsPage.FindName("ClashDownloadPanel")).Visibility == Visibility.Collapsed;
        translationModel.ApiKey = "synthetic-test-key";
        translationModel.ConnectionTester = (request, _) => Task.FromResult(new ConnectionTestResult(true, "翻译测速完成", TimeSpan.FromSeconds(2.8))
        { Benchmark = new(TranslationBenchmark.SourceSentence, "打开新页面时，翻译器会快速识别文字、保留原始布局，并随着每部分内容返回平滑显示中文译文。", TimeSpan.FromSeconds(2.8), TimeSpan.FromMilliseconds(820), 67.5, 135, 0, BenchmarkRateKind.ContentGeneration) });
        await translationModel.TestConnectionCommand.ExecuteAsync(null); settingsPage.UpdateLayout();
        var benchmarkControlsVerified = translationModel.FirstCharacterMetric.Contains("820", StringComparison.Ordinal)
            && translationModel.ThroughputMetric.Contains("67.5", StringComparison.Ordinal)
            && ((FrameworkElement)settingsPage.FindName("ApiBenchmarkCard")).Visibility == Visibility.Visible;
        var benchmarkPreview = Path.Combine(folder, "benchmark-settings-preview.png");
        Render(settingsPage, 760, 2800, benchmarkPreview);
        // Capture within the page's actual coordinate space; rendering an attached
        // card independently inherits its ScrollViewer clipping and can be transparent.
        var benchmarkCard = (FrameworkElement)settingsPage.FindName("ApiBenchmarkCard");
        var benchmarkOrigin = benchmarkCard.TransformToAncestor(settingsPage).Transform(new System.Windows.Point());
        var benchmarkFrame = BitmapFrame.Create(new Uri(benchmarkPreview), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var benchmarkCrop = new CroppedBitmap(benchmarkFrame, new Int32Rect((int)benchmarkOrigin.X, (int)benchmarkOrigin.Y,
            (int)Math.Ceiling(benchmarkCard.ActualWidth), (int)Math.Ceiling(benchmarkCard.ActualHeight)));
        var benchmarkEncoder = new PngBitmapEncoder(); benchmarkEncoder.Frames.Add(BitmapFrame.Create(benchmarkCrop));
        using (var benchmarkFile = File.Create(Path.Combine(folder, "api-benchmark-preview.png"))) benchmarkEncoder.Save(benchmarkFile);
        var popupModel = new TranslationResultViewModel { IsBusy = true, StatusText = "点击文字或拖框 · Esc 取消" };
        var popup = new SidePanelWindow(popupModel);
        Render((FrameworkElement)popup.Content, 320, 88, Path.Combine(folder, "collapsed-preview.png"));
        popupModel.ApplyStreamUpdate(new TranslationStreamUpdate("译文会在第一段内容返回后展开，并随服务端的流式响应逐步更新。\n\n可以查看和复制原文、重译或重新选择区域。"));
        popupModel.SourceText = "The floating translator expands when the first translation arrives.";
        popupModel.HasTranslationInput = true;
        popupModel.IsBusy = false;
        Render((FrameworkElement)popup.Content, 448, 520, Path.Combine(folder, "expanded-preview.png"));
        popupModel.SetSegments([
            new TranslatedBlock("title", "IMAGE TITLE", "图片标题", new PixelRect(30, 20, 450, 45)),
            new TranslatedBlock("first", "First paragraph in the image.", "图片内的第一段文字。可选择原位显示或展开查看。", new PixelRect(30, 120, 450, 60)),
            new TranslatedBlock("last", "Another paragraph much further below.", "位于下方的另一个段落也会完整翻译。", new PixelRect(30, 300, 450, 60))]);
        var sampleImage = RenderSampleImage();
        popupModel.SourceImage = sampleImage;
        Render((FrameworkElement)popup.Content, 448, 650, Path.Combine(folder, "image-segments-preview.png"));

        // Exercise bundled native libraries and the model using generated pixels, without opening a window or reading the desktop.
        var samplePixels = new byte[sampleImage.PixelWidth * sampleImage.PixelHeight * 4];
        sampleImage.CopyPixels(samplePixels, sampleImage.PixelWidth * 4, 0);
        using var detector = new LocalTextDetector();
        var sample = new CapturedBitmap(sampleImage.PixelWidth, sampleImage.PixelHeight, sampleImage.PixelWidth * 4, samplePixels);
        var detected = await detector.DetectAsync(sample, CancellationToken.None);
        using var automaticJudge = new BrowserImageTranslationJudge();
        var autoDecision = await automaticJudge.AssessAsync(sample, "zh-CN", CancellationToken.None);
        var automaticModelVerified = autoDecision.ReasonCode is not ("local-unavailable" or "local-timeout")
            && autoDecision.RecognizedText.Count > 0 && automaticDefaultsOff;
        var pageLanguageDecision = await new PageLanguageJudge().JudgeAsync(
            [new PageTextSample("This public article explains how visitors can read a research notice, choose a meeting time, and contact the library team. Keep every date and number in the original notice when preparing your visit.")],
            "en", BrowserPageJudgementMode.Rules, CancellationToken.None);
        var pageRulesVerified = !pageLanguageDecision.NeedsTranslation && pageLanguageDecision.DetectedLanguage == "en"
            && !File.Exists(Path.Combine(AppContext.BaseDirectory, "NTextCat.dll"))
            && !Directory.Exists(Path.Combine(AppContext.BaseDirectory, "models", "page-language"));
        var atlas = TextAtlasBuilder.Build(sample, detected);
        await File.WriteAllBytesAsync(Path.Combine(folder, "text-atlas-preview.png"), CapturedBitmapEncoder.ToPng(atlas.Bitmap));

        var positions = await new VisionRegionLocator(detector).LocateAsync(sample, CancellationToken.None);
        const string placementResponse = """
            {"blocks":[
            {"id":"p1","box":[50,48,800,155],"area":[40,34,850,171],"flow":"h","translation":"图片标题"},
            {"id":"p2","box":[50,286,800,429],"area":[40,272,850,443],"flow":"h","translation":"图片内的第一段文字，可以原位显示或展开查看。"},
            {"id":"p3","box":[50,714,940,857],"area":[40,700,960,871],"flow":"h","translation":"位于下方的另一个段落也会完整翻译。"}]}
            """;
        var parsed = DirectVisionTranslation.Parse(placementResponse, sample.Size).Blocks;
        var placed = new VisionPlacementRefiner(sample).Refine(parsed, positions);
        popupModel.SourceText = string.Empty;
        popupModel.SourceBitmap = sample;
        popupModel.SetSegments(placed);
        Render((FrameworkElement)popup.Content, 448, 650, Path.Combine(folder, "source-free-popup-preview.png"));
        popup.Close();
        var renderer = new InPlaceImageRenderer(sample);
        var patchCount = 0;
        using (var bitmap = LocalTextDetector.ToSkia(sample))
        using (var canvas = new SKCanvas(bitmap))
        {
            foreach (var block in placed)
            {
                var patch = renderer.Prepare(block);
                if (patch is null || patch.ErasedPixels == 0) continue;
                using var translated = LocalTextDetector.ToSkia(renderer.Render(patch, block), patch.Transparent);
                canvas.DrawBitmap(translated, patch.Bounds.X, patch.Bounds.Y);
                patchCount++;
            }
            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(Path.Combine(folder, "in-place-preview.png"), png.ToArray());
        }
        var placementVerified = parsed.All(block => block.HasVisionPlacement && block.SourceText.Length == 0 && block.LayoutBounds.IsUsable);
        var rendererVerified = patchCount == parsed.Count;
        using var chineseTypeface = SKTypeface.FromFile(Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSansCJKsc-Regular.otf"));
        using var chineseFont = new SKFont(chineseTypeface, 24);
        var fontVerified = chineseTypeface is not null && chineseFont.GetGlyph('译') != 0 && chineseFont.GetGlyph('︖') != 0;

        bool? accessibleFixture = null;
        var fixtureTrace = new List<string>();
        var fixture = Process.GetProcessesByName("Fixture").FirstOrDefault(process => process.MainWindowTitle == "Translation QA Fixture");
        if (!offline && fixture is not null && GetWindowRect(fixture.MainWindowHandle, out var rect))
        {
            var scale = GetDpiForWindow(fixture.MainWindowHandle) / 96.0;
            var point = new PixelPoint(rect.Left + (int)(80 * scale), rect.Top + (int)(168 * scale));
            fixtureTrace.Add($"pointWindowMatchesFixture={AccessibleTextResolver.FindWindowAt(point) == fixture.MainWindowHandle}");
            var selected = await AccessibleTextResolver.ResolveInWindowAsync(point,
                fixture.MainWindowHandle, CancellationToken.None, message => fixtureTrace.Add(message));
            fixtureTrace.Insert(0, $"bounds={rect.Left},{rect.Top},{rect.Right},{rect.Bottom} point={point.X},{point.Y}");
            accessibleFixture = selected?.Text.Contains("Global translation should select this entire paragraph", StringComparison.Ordinal) == true;
        }
        var report = new
        {
            success = alphaOpaque && detected.Count >= 3 && placementVerified && rendererVerified && fontVerified && automaticModelVerified && automaticImageSettingsVerified && automaticPageSettingsVerified && websiteRuleControlsVerified && pageRulesVerified && translationProviderControlsVerified && modelListControlsVerified && cacheControlsVerified && motionControlsVerified && chatGptOAuthControlsVerified,
            product = "Global AI Translator",
            version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            runtime = Environment.Version.ToString(),
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            operatingSystem = Environment.OSVersion.VersionString,
            monitorCount = monitors.Count,
            screenshotPixelsOpaque = offline ? (bool?)null : alphaOpaque,
            screenCapturePerformed = !offline,
            bundledTextModelVerified = detected.Count >= 3,
            detectedTextRegions = detected.Count,
            sourceFreeVisionSchemaVerified = placementVerified,
            detectedLocalLines = positions.Count,
            localTextRecognitionPerformed = true,
            automaticJudgementModelVerified = automaticModelVerified,
            automaticTranslationDefaultsOff = automaticDefaultsOff,
            automaticPageTranslationDefaultsOff = automaticPageDefaultsOff,
            automaticPageSettingsControlsVerified = automaticPageSettingsVerified,
            automaticImagePolicyControlsVerified = automaticImageSettingsVerified,
            websiteRuleSettingsControlsVerified = websiteRuleControlsVerified,
            translationMotionSettingsControlsVerified = motionControlsVerified,
            translationProviderControlsVerified,
            modelListControlsVerified,
            chatGptOAuthControlsVerified,
            pageCacheControlsVerified = cacheControlsVerified,
            pageCacheMegabytes = generalModel.BrowserPageCacheMegabytes,
            pageCacheDefaultEnabled = generalModel.BrowserPageCacheEnabled,
            translationApiProviders = translationModel.Providers.ToArray(),
            automaticPageJudgementMode = generalModel.BrowserPageJudgementMode,
            automaticPageTranslationConcurrency = generalModel.BrowserPageTranslationConcurrency,
            pageRulesVerified,
            pageLanguageModelBytes = 0,
            pageLanguageModelGpuBytes = 0,
            pageLanguageJudgementMilliseconds = pageLanguageDecision.ElapsedMilliseconds,
            automaticJudgementReason = autoDecision.ReasonCode,
            automaticJudgementMilliseconds = autoDecision.ElapsedMilliseconds,
            automaticJudgementModelFilesBytes = BrowserImageTranslationJudge.InstalledModelBytes,
            inPlaceRendererVerified = rendererVerified,
            inPlacePatchCount = patchCount,
            bundledChineseFontVerified = fontVerified,
            atlasSize = new { atlas.Bitmap.Width, atlas.Bitmap.Height },
            ocrLanguages = OcrEngine.AvailableRecognizerLanguages.Select(language => language.LanguageTag).ToArray(),
            nativeAccessibleParagraphVerified = accessibleFixture,
            nativeFixtureTrace = fixtureTrace,
            controlsRendered = new[] { "General automatic image settings", "General automatic page settings", "Translation settings", "Collapsed floating popup", "Expanded streaming popup", "Image paragraph correspondence" },
            apiKeyRead = false,
            networkCalls = false,
            inputInjected = false,
            apiNetworkControlsVerified = networkControlsVerified,
            apiBenchmarkControlsVerified = benchmarkControlsVerified,
        };
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        if (!alphaOpaque || detected.Count < 3 || !placementVerified || !rendererVerified || !fontVerified || !automaticModelVerified || !automaticImageSettingsVerified || !automaticPageSettingsVerified || !websiteRuleControlsVerified || !pageRulesVerified || !translationProviderControlsVerified || !modelListControlsVerified || !cacheControlsVerified || !networkControlsVerified || !benchmarkControlsVerified || !chatGptOAuthControlsVerified)
            throw new InvalidOperationException("Runtime, local position model, or in-place rendering check failed.");
    }

    private static async Task<bool> VerifyChatGptControlsAsync(string folder)
    {
        var vm = new TranslationSettingsViewModel { SelectedProvider = "OpenAI" };
        var page = new TranslationPage(vm);
        Render(page, 760, 900, Path.Combine(folder, "chatgpt-api-key-preview.png"));
        var authentication = (System.Windows.Controls.ComboBox)page.FindName("OpenAiAuthenticationCombo");
        authentication.SelectedItem = "ChatGPT 登录（使用订阅额度）";
        page.UpdateLayout();
        var verified = vm.UsesChatGptPlan && ((FrameworkElement)page.FindName("ChatGptAccountCard")).Visibility == Visibility.Visible
            && ((FrameworkElement)page.FindName("ApiKeyCard")).Visibility == Visibility.Collapsed && !vm.TestConnectionCommand.CanExecute(null);
        var account = new ChatGptAccount(Guid.NewGuid().ToString("N"), "离线演示账户 · 仅用于界面检查", true, true, false);
        vm.ChatGptAccountsLoader = _ => Task.FromResult<IReadOnlyList<ChatGptAccount>>([account]);
        vm.ChatGptSigner = (_, _) => Task.FromResult(new ChatGptSignInResult(account, false));
        vm.ModelListLoader = (_, _) => Task.FromResult(new TranslationModelList(["offline-model-a", "offline-model-b"])
        { Choices = [new("offline-model-a", "演示模型 A"), new("offline-model-b", "演示模型 B")] });
        await vm.SignInChatGptCommand.ExecuteAsync(null);
        page.UpdateLayout();
        var models = (System.Windows.Controls.ComboBox)page.FindName("ChatGptModelCombo");
        verified &= models.Visibility == Visibility.Visible && models.Items.Count == 2 && (string?)models.SelectedValue == "offline-model-a";
        models.SelectedValue = "offline-model-b";
        page.UpdateLayout();
        verified &= vm.SelectedModel == "offline-model-b" && models.SelectedItem is TranslationModelChoice { DisplayName: "演示模型 B" };
        Render(page, 760, 2450, Path.Combine(folder, "chatgpt-oauth-settings-preview.png"));
        Render((FrameworkElement)page.FindName("ChatGptAccountCard"), 680, 340, Path.Combine(folder, "chatgpt-oauth-card-preview.png"));
        var usageOpened = false;
        vm.ChatGptUsageOpener = () => usageOpened = true;
        ((Wpf.Ui.Controls.Button)page.FindName("ChatGptManageUsageButton")).Command.Execute(null);
        verified &= usageOpened;
        account = account with { UsagePaused = true };
        await vm.RefreshChatGptAccountsAsync(); page.UpdateLayout();
        verified &= ((FrameworkElement)page.FindName("ChatGptResumeUsageButton")).Visibility == Visibility.Visible && !vm.TestConnectionCommand.CanExecute(null);
        Render((FrameworkElement)page.FindName("ChatGptAccountCard"), 680, 340, Path.Combine(folder, "chatgpt-usage-limit-preview.png"));
        account = account with { UsagePaused = false, CanUsePlan = false };
        await vm.RefreshChatGptAccountsAsync(); page.UpdateLayout();
        verified &= ((FrameworkElement)page.FindName("ChatGptAuthorizePlanButton")).Visibility == Visibility.Visible && !vm.CanUseChatGptPlan;
        return verified;
    }

    private static void Render(FrameworkElement control, int width, int height, string path)
    {
        control.Measure(new System.Windows.Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        control.UpdateLayout();
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        if (control is System.Windows.Controls.Page)
        {
            var backdrop = new DrawingVisual();
            using var drawing = backdrop.RenderOpen();
            drawing.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, width, height));
            drawing.Close();
            image.Render(backdrop);
        }
        image.Render(control);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); encoder.Save(file);
    }

    private static BitmapSource RenderSampleImage()
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, 600, 420));
            foreach (var (text, y) in new[] { ("IMAGE TITLE", 20), ("First paragraph in the image.", 120), ("Another paragraph much further below.", 300) })
                drawing.DrawText(new FormattedText(text, System.Globalization.CultureInfo.GetCultureInfo("en-US"), System.Windows.FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 24, System.Windows.Media.Brushes.Black, 1), new System.Windows.Point(30, y));
        }
        var image = new RenderTargetBitmap(600, 420, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze(); return image;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
}
