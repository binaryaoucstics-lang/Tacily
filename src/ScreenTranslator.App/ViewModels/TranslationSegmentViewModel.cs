using CommunityToolkit.Mvvm.ComponentModel;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.ViewModels;

public sealed class TranslationSegmentViewModel(TranslatedBlock block) : ObservableObject
{
    private bool _isActive;
    public string Id => block.Id;
    public string SourceText => block.SourceText;
    public string Translation => block.Translation;
    public bool HasSourceText => !string.IsNullOrWhiteSpace(block.SourceText);
    public IReadOnlyList<string> SourceIds => block.SourceIds;
    public bool IsVertical => block.IsVertical;
    public bool IsComplete => block.IsComplete;
    public bool HasVisionPlacement => block.HasVisionPlacement;
    public PixelRect LayoutBounds => block.LayoutBounds;
    public TranslatedBlock ToBlock() => block;
    public PixelRect Bounds => block.Bounds;
    public IReadOnlyList<PixelRect> TextBounds => block.TextBounds.Count > 0 ? block.TextBounds : [block.Bounds];
    public bool Matches(TranslatedBlock other) => block.Id == other.Id && block.SourceText == other.SourceText
        && block.Translation == other.Translation && block.Bounds == other.Bounds && block.TextBounds.SequenceEqual(other.TextBounds)
        && block.SourceIds.SequenceEqual(other.SourceIds) && block.IsVertical == other.IsVertical && block.IsComplete == other.IsComplete
        && block.HasVisionPlacement == other.HasVisionPlacement && block.LayoutBounds == other.LayoutBounds
        && block.GlyphSizeHint == other.GlyphSizeHint && block.VisionInkBounds == other.VisionInkBounds
        && block.TypographyHint == other.TypographyHint;
    public bool Contains(double x, double y) => TextBounds.Any(bounds => x >= bounds.X && x < bounds.Right && y >= bounds.Y && y < bounds.Bottom);
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
}
