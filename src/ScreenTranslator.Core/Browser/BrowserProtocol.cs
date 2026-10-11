using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenTranslator.Core.Browser;

public enum BrowserKind
{
    Chrome,
    Edge,
}

public readonly record struct CssSize(double Width, double Height);

public readonly record struct CssRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BrowserHello), "hello")]
[JsonDerivedType(typeof(BrowserScroll), "scroll")]
[JsonDerivedType(typeof(BrowserInvalidated), "invalidated")]
public abstract record BrowserMessage;

public sealed record BrowserHello(
    BrowserKind Browser,
    int BrowserWindowId,
    int TabId,
    string DocumentToken,
    long NavigationGeneration,
    double DevicePixelRatio,
    CssSize ViewportSize,
    CssRect BrowserWindowBounds,
    int FrameId = 0) : BrowserMessage;

public sealed record BrowserScroll(
    int BrowserWindowId,
    int TabId,
    string DocumentToken,
    long NavigationGeneration,
    double DeltaXCss,
    double DeltaYCss,
    double DevicePixelRatio,
    CssRect? ScrollContainer,
    string TargetId = BrowserProtocol.RootTargetId,
    int FrameId = 0) : BrowserMessage;

public sealed record BrowserInvalidated(
    int BrowserWindowId,
    int TabId,
    string DocumentToken,
    long NavigationGeneration,
    string Reason,
    int FrameId = 0) : BrowserMessage;

public sealed class BrowserProtocolException : FormatException
{
    public BrowserProtocolException(string message)
        : base(message)
    {
    }

    public BrowserProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class BrowserProtocol
{
    public const string RootTargetId = "root";

    public const double MinimumDevicePixelRatio = 0.5;

    public const double MaximumDevicePixelRatio = 8;

    public const double MaximumAbsoluteDeltaCss = 100_000;

    private const double MaximumGeometryMagnitude = 1_000_000;
    private const int MaximumTokenLength = 256;
    private const int MaximumTargetLength = 256;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static BrowserMessage Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new BrowserProtocolException("浏览器消息不能为空。");
        }

        BrowserMessage message;
        try
        {
            message = JsonSerializer.Deserialize<BrowserMessage>(json, SerializerOptions)
                ?? throw new BrowserProtocolException("浏览器消息为空。");
        }
        catch (BrowserProtocolException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new BrowserProtocolException("浏览器消息不是有效的协议 JSON。", exception);
        }

        Validate(message);
        return message;
    }

    public static string Serialize(BrowserMessage message)
    {
        Validate(message);
        return JsonSerializer.Serialize(message, SerializerOptions);
    }

    public static bool TryValidate(BrowserMessage? message, out string? error)
    {
        try
        {
            Validate(message);
            error = null;
            return true;
        }
        catch (BrowserProtocolException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public static void Validate(BrowserMessage? message)
    {
        if (message is null)
        {
            throw new BrowserProtocolException("浏览器消息不能为空。");
        }

        switch (message)
        {
            case BrowserHello hello:
                ValidateIdentity(
                    hello.BrowserWindowId,
                    hello.TabId,
                    hello.DocumentToken,
                    hello.NavigationGeneration,
                    hello.FrameId);
                if (!Enum.IsDefined(hello.Browser))
                {
                    throw new BrowserProtocolException("浏览器类型无效。");
                }

                ValidateDevicePixelRatio(hello.DevicePixelRatio);
                ValidateSize(hello.ViewportSize, "浏览器视口");
                ValidateRect(hello.BrowserWindowBounds, "浏览器窗口");
                break;

            case BrowserScroll scroll:
                ValidateIdentity(
                    scroll.BrowserWindowId,
                    scroll.TabId,
                    scroll.DocumentToken,
                    scroll.NavigationGeneration,
                    scroll.FrameId);
                ValidateDevicePixelRatio(scroll.DevicePixelRatio);
                ValidateDelta(scroll.DeltaXCss, nameof(scroll.DeltaXCss));
                ValidateDelta(scroll.DeltaYCss, nameof(scroll.DeltaYCss));
                ValidateBoundedText(scroll.TargetId, MaximumTargetLength, "滚动目标 ID");

                if (scroll.ScrollContainer is { } container)
                {
                    ValidateRect(container, "滚动容器");
                    if (string.Equals(
                            scroll.TargetId,
                            RootTargetId,
                            StringComparison.Ordinal))
                    {
                        throw new BrowserProtocolException("根滚动不能携带嵌套容器。");
                    }
                }
                else if (!string.Equals(
                             scroll.TargetId,
                             RootTargetId,
                             StringComparison.Ordinal))
                {
                    throw new BrowserProtocolException("嵌套滚动必须携带容器矩形。");
                }

                break;

            case BrowserInvalidated invalidated:
                ValidateIdentity(
                    invalidated.BrowserWindowId,
                    invalidated.TabId,
                    invalidated.DocumentToken,
                    invalidated.NavigationGeneration,
                    invalidated.FrameId);
                ValidateBoundedText(invalidated.Reason, MaximumTokenLength, "失效原因");
                break;

            default:
                throw new BrowserProtocolException("浏览器消息类型不受支持。");
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static void ValidateIdentity(
        int browserWindowId,
        int tabId,
        string documentToken,
        long navigationGeneration,
        int frameId)
    {
        if (browserWindowId < 0)
        {
            throw new BrowserProtocolException("浏览器窗口 ID 无效。");
        }

        if (tabId < 0)
        {
            throw new BrowserProtocolException("浏览器标签 ID 无效。");
        }

        if (frameId < 0)
        {
            throw new BrowserProtocolException("浏览器 frame ID 无效。");
        }

        if (navigationGeneration < 0)
        {
            throw new BrowserProtocolException("页面导航代次无效。");
        }

        ValidateBoundedText(documentToken, MaximumTokenLength, "文档令牌");
    }

    private static void ValidateDevicePixelRatio(double value)
    {
        if (!double.IsFinite(value)
            || value < MinimumDevicePixelRatio
            || value > MaximumDevicePixelRatio)
        {
            throw new BrowserProtocolException(
                $"devicePixelRatio 必须位于 {MinimumDevicePixelRatio} 到 {MaximumDevicePixelRatio} 之间。");
        }
    }

    private static void ValidateDelta(double value, string name)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > MaximumAbsoluteDeltaCss)
        {
            throw new BrowserProtocolException($"{name} 超出允许范围。");
        }
    }

    private static void ValidateSize(CssSize size, string name)
    {
        if (!double.IsFinite(size.Width)
            || !double.IsFinite(size.Height)
            || size.Width <= 0
            || size.Height <= 0
            || size.Width > MaximumGeometryMagnitude
            || size.Height > MaximumGeometryMagnitude)
        {
            throw new BrowserProtocolException($"{name}尺寸无效。");
        }
    }

    private static void ValidateRect(CssRect rectangle, string name)
    {
        if (!double.IsFinite(rectangle.Left)
            || !double.IsFinite(rectangle.Top)
            || !double.IsFinite(rectangle.Width)
            || !double.IsFinite(rectangle.Height)
            || Math.Abs(rectangle.Left) > MaximumGeometryMagnitude
            || Math.Abs(rectangle.Top) > MaximumGeometryMagnitude
            || rectangle.Width <= 0
            || rectangle.Height <= 0
            || rectangle.Width > MaximumGeometryMagnitude
            || rectangle.Height > MaximumGeometryMagnitude)
        {
            throw new BrowserProtocolException($"{name}矩形无效。");
        }
    }

    private static void ValidateBoundedText(string? value, int maximumLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new BrowserProtocolException($"{name}无效。");
        }
    }
}
