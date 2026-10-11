using System.Windows.Media.Imaging;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Capture;

public sealed record MonitorCapture(
    ScreenMonitor Monitor,
    CapturedBitmap Bitmap,
    BitmapSource Preview);
