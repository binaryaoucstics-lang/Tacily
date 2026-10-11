using System.Text.Json;

namespace ScreenTranslator.App.Services.Browser;

public sealed record BrowserManualImageRequest(string RequestId, string ImageId, string DocumentToken, int TabId, int WindowId, long Generation)
{
    public static bool TryRead(JsonElement root, out BrowserManualImageRequest? request)
    {
        request = null;
        try
        {
            var id = root.GetProperty("requestId").GetString(); var image = root.GetProperty("imageId").GetString();
            var document = root.GetProperty("documentToken").GetString(); var tab = root.GetProperty("tabId").GetInt32();
            var window = root.GetProperty("browserWindowId").GetInt32(); var generation = root.GetProperty("generation").GetInt64();
            if (root.GetProperty("frameId").GetInt32() != 0 || !Bounded(id, 64) || !Bounded(image, 64) || !Bounded(document, 256)
                || tab < 0 || window < 0 || generation < 1) return false;
            request = new(id!, image!, document!, tab, window, generation); return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { return false; }
    }
    public bool Matches(BrowserImageReply? image, bool requireVisible = false) => image is { Found: true }
        && image.ImageId == ImageId && image.DocumentToken == DocumentToken
        && double.IsFinite(image.Natural.Width) && double.IsFinite(image.Natural.Height) && image.Natural.Width > 0 && image.Natural.Height > 0
        && (!requireVisible || image.Clip.Width > 0 && image.Clip.Height > 0);
    private static bool Bounded(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum && !value.Any(char.IsControl);
}
