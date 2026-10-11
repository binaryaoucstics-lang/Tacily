using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Layout;

namespace ScreenTranslator.App.ViewModels;

public sealed class TranslationResultViewModel : ObservableObject
{
    private string _sourceText = string.Empty;
    private string _translatedText = string.Empty;
    private string _sourceLanguageLabel = "自动检测";
    private string _targetLanguageLabel = "简体中文";
    private bool _isPinned;
    private bool _isSourceVisible;
    private bool _isBusy;
    private string? _errorMessage;
    private System.Windows.Media.ImageSource? _sourceImage;
    private bool _isExpanded;
    private string _statusText = "等待选择内容";
    private bool _hasTranslationInput;
    private string _streamingTail = string.Empty;
    private CapturedBitmap? _sourceBitmap;
    private bool _isInPlaceEnabled;
    private int _inPlaceRenderedCount;

    public TranslationResultViewModel()
    {
        CopyCommand = new RelayCommand(
            () => CopyRequested?.Invoke(this, TranslatedText),
            () => !IsBusy && !string.IsNullOrWhiteSpace(TranslatedText));
        TogglePinCommand = new RelayCommand(() => IsPinned = !IsPinned);
        ToggleSourceCommand = new RelayCommand(() => IsSourceVisible = !IsSourceVisible);
        RetryCommand = new RelayCommand(
            () => RetryRequested?.Invoke(this, EventArgs.Empty),
            () => !IsBusy && HasTranslationInput);
        SwitchModeCommand = new RelayCommand(
            () => SwitchModeRequested?.Invoke(this, EventArgs.Empty));
        CloseCommand = new RelayCommand(
            () => CloseRequested?.Invoke(this, EventArgs.Empty));
        ClearAllCommand = new RelayCommand(
            () => ClearAllRequested?.Invoke(this, EventArgs.Empty));
        CopySourceCommand = new RelayCommand(() => CopyRequested?.Invoke(this, SourceText),
            () => !string.IsNullOrWhiteSpace(SourceText));
        ReselectCommand = new RelayCommand(() => ReselectRequested?.Invoke(this, EventArgs.Empty), () => !IsBusy);
        SettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        ToggleInPlaceCommand = new RelayCommand(() => IsInPlaceEnabled = !IsInPlaceEnabled);
    }

    public string SourceText
    {
        get => _sourceText;
        set { if (SetProperty(ref _sourceText, value)) { CopySourceCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(HasSourceText)); } }
    }

    public System.Windows.Media.ImageSource? SourceImage
    {
        get => _sourceImage;
        set { if (SetProperty(ref _sourceImage, value) && value is not null) IsSourceVisible = true; }
    }

    public PixelRect SourceRegion { get; set; }
    public CapturedBitmap? SourceBitmap
    {
        get => _sourceBitmap;
        set { if (SetProperty(ref _sourceBitmap, value)) { OnPropertyChanged(nameof(CanRenderInPlace)); OnPropertyChanged(nameof(SourceDisplayLabel)); } }
    }
    public bool CanRenderInPlace => SourceBitmap is not null;
    public bool HasSourceText => !string.IsNullOrWhiteSpace(SourceText);
    public string SourceDisplayLabel => CanRenderInPlace ? "原图" : "原文";
    public bool IsInPlaceEnabled { get => _isInPlaceEnabled; set => SetProperty(ref _isInPlaceEnabled, value); }
    public int InPlaceRenderedCount { get => _inPlaceRenderedCount; set => SetProperty(ref _inPlaceRenderedCount, value); }
    public nint SourceWindowHandle { get; set; }
    public PixelRect? SourceWindowBounds { get; set; }
    public SourceImageMapping? SourceImageMapping { get; set; }
    public Func<Task<SourceImageMapping?>>? RefreshSourceImageMapping { get; set; }
    public CapturedBitmap? SourceInkMask {get;set;}
    public Services.Browser.BrowserOverlayTransport? BrowserOverlay {get;set;}
    public IReadOnlyList<Services.Overlays.RenderedImageTile>? CachedTiles {get;set;}
    public Action<IReadOnlyList<Services.Overlays.RenderedImageTile>>? TilesRendered {get;set;}
    public ObservableCollection<TranslationSegmentViewModel> Segments { get; } = [];
    public bool HasSegments => Segments.Count > 0;
    public bool IsFlatTranslation => !HasSegments;
    public string StreamingTail { get => _streamingTail; private set => SetProperty(ref _streamingTail, value); }
    public void SetSegments(IReadOnlyList<TranslatedBlock> blocks)
    {
        Segments.Clear();
        foreach (var block in blocks) Segments.Add(new TranslationSegmentViewModel(block));
        OnPropertyChanged(nameof(HasSegments));
        OnPropertyChanged(nameof(IsFlatTranslation));
        StreamingTail = string.Empty;
    }

    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public bool HasTranslationInput
    {
        get => _hasTranslationInput;
        set { if (SetProperty(ref _hasTranslationInput, value)) RetryCommand.NotifyCanExecuteChanged(); }
    }
    public bool IsCollapsed => !IsExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (SetProperty(ref _isExpanded, value)) OnPropertyChanged(nameof(IsCollapsed)); }
    }

    public void ApplyStreamUpdate(ScreenTranslator.Core.Models.TranslationStreamUpdate update)
    {
        if (!IsBusy)
        {
            // A queued position update may contain an earlier text prefix. Enrich geometry only.
            if (update.IsLayoutUpdate && update.ImageSegments is { } mapped && ErrorMessage is null && HasSegments)
                SetSegments(Segments.Select(segment =>
                {
                    var match = mapped.FirstOrDefault(block => block.Id == segment.Id && block.SourceText == segment.SourceText);
                    var current = segment.ToBlock();
                    return current with { Bounds = match?.Bounds ?? segment.Bounds,
                            TextBounds = match?.TextBounds ?? segment.TextBounds, SourceIds = match?.SourceIds ?? segment.SourceIds,
                            IsVertical = match?.IsVertical ?? segment.IsVertical, IsComplete = segment.IsComplete,
                            HasVisionPlacement = match?.HasVisionPlacement ?? segment.HasVisionPlacement,
                            LayoutBounds = match?.LayoutBounds ?? segment.LayoutBounds,
                            TypographyHint = match?.TypographyHint ?? current.TypographyHint,
                            GlyphSizeHint = match?.GlyphSizeHint ?? current.GlyphSizeHint,
                            VisionInkBounds = match?.VisionInkBounds ?? current.VisionInkBounds };
                }).ToArray());
            return;
        }
        TranslatedText = update.Translation;
        if (update.Translation.Length == 0 && update.ImageSegments is null) SetSegments([]);
        if (update.ImageSegments is { } segments)
        {
            if (segments.Count != Segments.Count || Segments.Zip(segments).Any(pair => !pair.First.Matches(pair.Second))) SetSegments(segments);
            var completed = string.Join("\n", segments.Select(segment => segment.Translation));
            StreamingTail = update.StreamingTail ?? (completed.Length > 0 && update.Translation.StartsWith(completed, StringComparison.Ordinal)
                ? update.Translation[completed.Length..].TrimStart('\n') : string.Empty);
            TranslatedText = string.Join("\n", new[] { completed, StreamingTail }.Where(text => text.Length > 0));
            if (segments.Count > 0) SourceText = string.Join("\n", segments.Select(segment => segment.SourceText));
        }
        if (!string.IsNullOrWhiteSpace(update.Translation))
        {
            if (!update.IsLayoutUpdate && !IsInPlaceEnabled) IsExpanded = true;
            if (IsBusy) StatusText = IsInPlaceEnabled ? "正在原位翻译…" : "正在翻译…";
        }
    }

    public string TranslatedText
    {
        get => _translatedText;
        set
        {
            if (SetProperty(ref _translatedText, value))
            {
                CopyCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SourceLanguageLabel
    {
        get => _sourceLanguageLabel;
        set => SetProperty(ref _sourceLanguageLabel, value);
    }

    public string TargetLanguageLabel
    {
        get => _targetLanguageLabel;
        set => SetProperty(ref _targetLanguageLabel, value);
    }

    public bool IsPinned
    {
        get => _isPinned;
        set => SetProperty(ref _isPinned, value);
    }

    public bool IsSourceVisible
    {
        get => _isSourceVisible;
        set => SetProperty(ref _isSourceVisible, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RetryCommand.NotifyCanExecuteChanged();
                ReselectCommand.NotifyCanExecuteChanged();
                CopyCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public RelayCommand CopyCommand { get; }
    public RelayCommand CopySourceCommand { get; }
    public RelayCommand ReselectCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand ToggleExpandedCommand { get; }
    public RelayCommand ToggleInPlaceCommand { get; }
    public event EventHandler? ReselectRequested;
    public event EventHandler? SettingsRequested;

    public RelayCommand TogglePinCommand { get; }

    public RelayCommand ToggleSourceCommand { get; }

    public RelayCommand RetryCommand { get; }

    public RelayCommand SwitchModeCommand { get; }

    public RelayCommand CloseCommand { get; }

    public RelayCommand ClearAllCommand { get; }

    public event EventHandler<string>? CopyRequested;

    public event EventHandler? RetryRequested;

    public event EventHandler? SwitchModeRequested;

    public event EventHandler? CloseRequested;

    public event EventHandler? ClearAllRequested;
}
