namespace ScreenTranslator.App.Services.Overlays;

public sealed class OverlayVisibilityState
{
    public bool UserVisible { get; set; } = true;

    public bool SourceWindowActive { get; set; } = true;

    public bool TrackingVisible { get; set; } = true;

    public bool ContextMenuOpen { get; set; }

    public bool ShouldShow =>
        UserVisible
        && TrackingVisible
        && (SourceWindowActive || ContextMenuOpen);
}
