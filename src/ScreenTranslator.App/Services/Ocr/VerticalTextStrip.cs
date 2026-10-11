using SkiaSharp;

namespace ScreenTranslator.App.Services.Ocr;

/// <summary>Move upright vertical glyphs into a horizontal recognition strip without rotating them.</summary>
public static class VerticalTextStrip
{
    public static SKBitmap Build(SKBitmap crop, double pitchScale = 1.2)
    {
        if (crop.Width < 4 || crop.Height < 9) return crop.Copy();
        var samples = new List<byte>();
        for (var y = 0; y < crop.Height; y += Math.Max(1, crop.Height / 64))
            for (var x = 0; x < crop.Width; x++) samples.Add(Luma(crop.GetPixel(x, y)));
        samples.Sort();
        var dark = samples[samples.Count / 2] < 128;
        if (dark)
        {
            using var inverted = crop.Copy();
            for (var y = 0; y < inverted.Height; y++)
                for (var x = 0; x < inverted.Width; x++)
                {
                    var color = inverted.GetPixel(x, y);
                    inverted.SetPixel(x, y, new SKColor((byte)(255 - color.Red), (byte)(255 - color.Green), (byte)(255 - color.Blue), color.Alpha));
                }
            return Build(inverted, pitchScale);
        }
        var rows = new bool[crop.Height];
        // DB detection is expanded around a line. Neighbouring columns can appear in that
        // padding, so retain the central glyph column before splitting its rows.
        var side = Math.Max(1, crop.Width / 4);
        for (var y = 0; y < crop.Height; y++)
        {
            var ink = 0;
            for (var x = side; x < crop.Width - side; x++)
            {
                var value = Luma(crop.GetPixel(x, y));
                if (dark ? value > 150 : value < 160) ink++;
            }
            rows[y] = ink >= Math.Max(1, crop.Width / 20);
        }
        var top = Array.FindIndex(rows, 4, Math.Max(0, rows.Length - 8), value => value);
        var bottom = Array.FindLastIndex(rows, Math.Max(0, rows.Length - 5), value => value);
        if (top < 0 || bottom <= top) return crop.Copy();
        var unit = Math.Max(12, crop.Width - side * 2);
        var count = Math.Clamp((int)Math.Round((bottom - top + 1) / (unit * pitchScale)), 1, 100);
        var glyphs = new List<(int Start, int End)>();
        for (var index = 0; index < count; index++) glyphs.Add((top + (bottom - top + 1) * index / count,
            top + (bottom - top + 1) * (index + 1) / count));
        var size = unit + 4;
        var strip = new SKBitmap(size * glyphs.Count + 8, size + 8, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(strip))
        {
            canvas.Clear(dark ? SKColors.Black : SKColors.White);
            for (var index = 0; index < glyphs.Count; index++)
            {
                var glyph = glyphs[index];
                var width = crop.Width - side * 2;
                var height = glyph.End - glyph.Start;
                var scale = Math.Min(unit / (double)width, unit / (double)height);
                var w = (float)(width * scale); var h = (float)(height * scale);
                var x = 4 + index * size + (size - w) / 2;
                var y = 4 + (size - h) / 2;
                canvas.DrawBitmap(crop, new SKRect(side, glyph.Start, crop.Width - side, glyph.End), new SKRect(x, y, x + w, y + h));
            }
        }
        return strip;
    }

    private static byte Luma(SKColor color) => (byte)((color.Red * 299 + color.Green * 587 + color.Blue * 114) / 1000);
}
