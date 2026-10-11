using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ScreenTranslator.App.Services.Capture;
using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.App.Services.Overlays;
using ScreenTranslator.App.ViewModels;
using ScreenTranslator.Core.Models;
using SkiaSharp;

namespace ScreenTranslator.IntegrationTests.Translation;

public sealed class ColourTypographyTests
{
    [Theory]
    [InlineData("#E52B36", "#FFFFFF", true)]
    [InlineData("#195DCB", "#FFF4D5", true)]
    [InlineData("#FFDE15", "#16243B", true)]
    [InlineData("#FFFFFF", "#154736", true)]
    [InlineData("#FFEE00", "#FFFFFF", false)]
    public void DraftAndOptimizedGlyphPixelsKeepTheirOriginalColour(string fillHex, string backgroundHex, bool balloon)
    {
        var fill = SKColor.Parse(fillHex); var background = SKColor.Parse(backgroundHex);
        var source = Source(fill, background, balloon);
        var block = Block();
        var renderer = new InPlaceImageRenderer(source);
        var patch = Assert.IsType<InPlacePatch>(renderer.Prepare(block));
        Assert.True(Count(patch.Background, fill) < 8); // Repaired source does not retain original coloured glyphs.
        var draft = renderer.Render(patch, block with { Translation = "保", IsComplete = false });
        Assert.True(Count(draft, fill) > 30, $"Draft must draw {fillHex} ink, sampled {patch.Foreground}.");
        var optimized = renderer.Render(patch with { Style = ComicStyleAnalyzer.Analyze(source, patch, block) }, block);
        Assert.True(Count(optimized, fill) > 50, $"Final glyph pixels must retain {fillHex}.");
    }

    [Theory]
    [InlineData("#E52B36", "#181818")]
    [InlineData("#FFE329", "#1B4ABD")]
    public void TwoColourLetteringKeepsFillAndOutlineInRenderedPixels(string fillHex, string outlineHex)
    {
        var fill = SKColor.Parse(fillHex); var outline = SKColor.Parse(outlineHex);
        var source = Source(fill, SKColors.White, false, outline);
        var block = Block(); var renderer = new InPlaceImageRenderer(source);
        var patch = Assert.IsType<InPlacePatch>(renderer.Prepare(block));
        var draft = renderer.Render(patch, block with { IsComplete = false });
        Assert.True(Count(draft, fill) > 50, $"Fill pixels missing, sampled {patch.Foreground}.");
        Assert.True(Count(draft, outline) > 20, $"Outline pixels missing, sampled {patch.InkPalette?.Outline}.");
        var final = renderer.Render(patch with { Style = ComicStyleAnalyzer.Analyze(source, patch, block) }, block);
        Assert.True(Count(final, fill) > 50);
        Assert.True(Count(final, outline) > 20);
    }

    [Fact]
    public void WrongVisionColourCannotReplaceConfidentLocalRedInk()
    {
        var source = Source(SKColor.Parse("#E52B36"), SKColors.White, false);
        var block = Block() with { TypographyHint = new(VisionFontFamily.Handwriting, true, 8, .15f)
            { ColorHex = "#193ADB", OutlineColorHex = "#FFFF00" } };
        var renderer = new InPlaceImageRenderer(source); var patch = renderer.Prepare(block)!;
        var final = renderer.Render(patch with { Style = ComicStyleAnalyzer.Analyze(source, patch, block) }, block);
        Assert.True(Count(final, SKColor.Parse("#E52B36")) > 50);
        Assert.Equal(0, Count(final, SKColor.Parse("#193ADB")));
        Assert.Equal(0, Count(final, SKColors.Yellow));
    }

    [Fact]
    public void HollowLetteringUsesItsActualCreamInteriorAndBlueOutlineFromTheFirstDraft()
    {
        var background=SKColor.Parse("#FFF4D5");var outline=SKColor.Parse("#142E68");
        var source=Source(background,background,false,outline,hollow:true);var block=Block();
        var renderer=new InPlaceImageRenderer(source);var patch=renderer.Prepare(block)!;
        Assert.True(patch.InkPalette!.Hollow);
        Assert.True(LocalInkColorSampler.Distance(patch.Foreground,background)<24);
        var draft=renderer.Render(patch,block with{IsComplete=false});
        Assert.True(Count(draft,outline)>50);
        Assert.Equal(0,Count(draft,SKColors.Black));
        var optimized=renderer.Render(patch with{Style=ComicStyleAnalyzer.Analyze(source,patch,block)},block);
        Assert.True(Count(optimized,outline)>50);
        Assert.Equal(0,Count(optimized,SKColors.Black));
    }

    [Fact]
    public async Task LateTypographyAdviceChangesActualStreamPixelsWithoutChangingTranslation()
    {
        var source = Source(SKColor.Parse("#195DCB"), SKColors.White, false);
        var block = Block(); using var session = new InPlaceRenderSession(source);
        session.Update([block], final: true);
        var first = Assert.Single(await session.FinishAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        var changed = new TaskCompletionSource<RenderedImageTile>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TilesChanged += tiles =>
        {
            if (tiles.Count == 1 && tiles[0].Optimized && !ReferenceEquals(first.Png, tiles[0].Png)) changed.TrySetResult(tiles[0]);
        };
        var advised = block with { TypographyHint = new(VisionFontFamily.Brush, true, 10) };
        Assert.False(new TranslationSegmentViewModel(block).Matches(advised));
        session.Update([advised]);
        var second = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(first.Translation, second.Translation);
        Assert.False(first.Png.SequenceEqual(second.Png));
        Assert.True(Count(second.Bitmap, SKColor.Parse("#195DCB")) > 30);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateUnsafePlacementOrEmptyTextWithdrawsVisibleTileBeforeFinal(bool emptyText)
    {
        var source=Source(SKColor.Parse("#195DCB"),SKColors.White,false);
        var block=Block() with{IsComplete=false};using var session=new InPlaceRenderSession(source);
        var shown=new TaskCompletionSource<RenderedImageTile>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TilesChanged+=tiles=>{if(tiles.Count==1)shown.TrySetResult(tiles[0]);};
        session.Update([block]);
        Assert.True(Count((await shown.Task.WaitAsync(TimeSpan.FromSeconds(5))).Bitmap,SKColor.Parse("#195DCB"))>30);
        var withdrawn=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TilesChanged+=tiles=>{if(tiles.Count==0)withdrawn.TrySetResult(true);};
        var unsafeBlock=emptyText?block with{Translation=" "}:block with{Bounds=default};
        session.Update([unsafeBlock]);
        await withdrawn.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.FinishAsync().IsCompleted); // Withdrawal is immediate, not deferred to final publication.
        session.Update([unsafeBlock with{IsComplete=true}],final:true);
        Assert.Empty(await session.FinishAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ColourSamplesAndRealRenderingCreateInspectableContactSheetAndTiming()
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../work/typography-colour-qa"));
        Directory.CreateDirectory(directory);
        (string Fill, string Background, string? Outline, bool Balloon,bool Hollow)[] cases =
        [ ("#E52B36", "#FFFFFF", null, true,false), ("#195DCB", "#FFF4D5", null, true,false),
            ("#FFDE15", "#16243B", null, true,false), ("#FFFFFF", "#154736", null, true,false),
            ("#E52B36", "#FFFFFF", "#181818", false,false), ("#FFE329", "#FFFFFF", "#1B4ABD", false,false),
            ("#FFF4D5","#FFF4D5","#142E68",false,true) ];
        using var sheet = new SKBitmap(1800, cases.Length * 260, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(sheet); canvas.Clear(SKColors.LightGray);
        var timings = new List<double>();
        for (var index = 0; index < cases.Length; index++)
        {
            var item = cases[index]; var image = Source(SKColor.Parse(item.Fill), SKColor.Parse(item.Background), item.Balloon,
                item.Outline is null ? null : SKColor.Parse(item.Outline),item.Hollow);
            var renderer = new InPlaceImageRenderer(image); var block = Block();
            var timer = Stopwatch.StartNew(); var patch = renderer.Prepare(block)!;
            var draft = renderer.Render(patch, block with { Translation = "保持", IsComplete = false });
            var styled = renderer.Render(patch with { Style = ComicStyleAnalyzer.Analyze(image, patch, block) }, block);
            timer.Stop(); timings.Add(timer.Elapsed.TotalMilliseconds);
            using var original = LocalTextDetector.ToSkia(image); canvas.DrawBitmap(original, 0, index * 260);
            canvas.DrawBitmap(original, 600, index * 260); canvas.DrawBitmap(original, 1200, index * 260);
            using var draftBitmap = LocalTextDetector.ToSkia(draft, patch.Transparent);
            using var styledBitmap = LocalTextDetector.ToSkia(styled, patch.Transparent);
            canvas.DrawBitmap(draftBitmap, 600 + patch.Bounds.X, index * 260 + patch.Bounds.Y);
            canvas.DrawBitmap(styledBitmap, 1200 + patch.Bounds.X, index * 260 + patch.Bounds.Y);
        }
        using var encoded = sheet.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(directory, "source-draft-optimized.png"), encoded.ToArray());
        File.WriteAllText(Path.Combine(directory, "timing.json"), JsonSerializer.Serialize(new
        {
            Measurement = "Full local prepare, one streaming render, style analysis and final render per paragraph; API/network excluded",
            Count = timings.Count, AverageMilliseconds = timings.Average(), MaximumMilliseconds = timings.Max(), SamplesMilliseconds = timings,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.All(timings, value => Assert.True(value > 0));
    }

    private static TranslatedBlock Block() => new("p1", "", "保持原来的字色", new(170, 90, 260, 70))
    { HasVisionPlacement = true, PreciseWords = true, GlyphSizeHint = 48, SourceIds = ["r1"], TextBounds = [new(170, 90, 260, 70)] };

    private static CapturedBitmap Source(SKColor fill, SKColor background, bool balloon, SKColor? outline = null,bool hollow=false)
    {
        using var bitmap = new SKBitmap(600, 260, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bitmap); canvas.Clear(balloon ? SKColors.Gray : background);
        using var paint = new SKPaint { Color = background, IsAntialias = true };
        if (balloon) { canvas.DrawOval(new SKRect(60, 35, 540, 225), paint); paint.Color = SKColors.Black; paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = 2; canvas.DrawOval(new SKRect(60, 35, 540, 225), paint); }
        using var font = new SKFont(SKTypeface.Default, 54);
        if (outline is { } edge) { paint.Color = edge; paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = hollow?2:5; paint.StrokeJoin = SKStrokeJoin.Round; canvas.DrawText("COLOR", 180, 145, font, paint); }
        if(!hollow){paint.Style = SKPaintStyle.Fill; paint.Color = fill; canvas.DrawText("COLOR", 180, 145, font, paint);}
        var bytes = new byte[bitmap.Width * bitmap.Height * 4]; Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        return new(bitmap.Width, bitmap.Height, bitmap.Width * 4, bytes);
    }
    private static int Count(CapturedBitmap bitmap, SKColor colour)
    {
        var count = 0; var pixels = bitmap.Pixels.Span;
        for (var y = 0; y < bitmap.Height; y++) for (var x = 0; x < bitmap.Width; x++)
        { var p = y * bitmap.Stride + x * 4; if (pixels[p + 3] > 180 && LocalInkColorSampler.Distance(new(pixels[p + 2], pixels[p + 1], pixels[p]), colour) <= 24) count++; }
        return count;
    }
}
