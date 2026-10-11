using System.Text;
using System.IO;
using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Browser;

/// <summary>Bounded, ordered chunks keep every native message below Chrome's 1 MiB limit.</summary>
public sealed class BrowserImageTransfer
{
    public const int MaximumBase64Length = 24 * 1024 * 1024;
    private readonly StringBuilder _data = new();
    private int _next;
    public void Append(int index, string data)
    {
        if (index != _next || data.Length is <= 0 or > 262144 || _data.Length + data.Length > MaximumBase64Length)
            throw new InvalidDataException("图片传输无效。");
        _data.Append(data); _next++;
    }
    public byte[] Complete(int length)
    {
        if (length <= 0 || length != _data.Length) throw new InvalidDataException("图片传输不完整。");
        return Convert.FromBase64String(_data.ToString());
    }
}

public sealed record BrowserImageReply(string? RequestId, bool Found, string? ImageId, string? DocumentToken,
    CssSize Natural, CssRect Display, CssRect Clip, double DevicePixelRatio, CssSize ViewportSize, int Base64Length)
{
    public bool? AutoImageAllowed { get; init; }
    public bool? AutoImageAdmitted { get; init; }

    public SourceImageMapping? CreateMapping(PixelSize actualSize, PixelRect viewport)
    {
        var values = new[] { Display.Left, Display.Top, Display.Width, Display.Height, Clip.Left, Clip.Top, Clip.Width, Clip.Height,
            DevicePixelRatio, ViewportSize.Width, ViewportSize.Height };
        if (!Found || string.IsNullOrEmpty(ImageId) || ImageId.Length > 64 || string.IsNullOrEmpty(DocumentToken) || DocumentToken.Length > 256
            || values.Any(value => !double.IsFinite(value) || Math.Abs(value) > 1_000_000)
            || DevicePixelRatio is < .5 or > 8 || Display.Width <= 0 || Display.Height <= 0 || Clip.Width <= 0 || Clip.Height <= 0
            || Math.Abs(ViewportSize.Width * DevicePixelRatio - viewport.Width) > 32
            || Math.Abs(ViewportSize.Height * DevicePixelRatio - viewport.Height) > 32) return null;
        var display = new DipRect(viewport.X + Display.Left * DevicePixelRatio, viewport.Y + Display.Top * DevicePixelRatio,
            Display.Width * DevicePixelRatio, Display.Height * DevicePixelRatio);
        var left = (int)Math.Ceiling(viewport.X + Clip.Left * DevicePixelRatio);
        var top = (int)Math.Ceiling(viewport.Y + Clip.Top * DevicePixelRatio);
        var right = (int)Math.Floor(viewport.X + (Clip.Left + Clip.Width) * DevicePixelRatio);
        var bottom = (int)Math.Floor(viewport.Y + (Clip.Top + Clip.Height) * DevicePixelRatio);
        var clip = new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top)).Intersect(viewport);
        return clip.IsUsable ? new SourceImageMapping(actualSize, display, clip) : null;
    }
}
