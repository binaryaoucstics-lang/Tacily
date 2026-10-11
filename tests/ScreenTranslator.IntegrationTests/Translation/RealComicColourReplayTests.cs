using System.Text.Json;
using ScreenTranslator.App.Services.Capture;
using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.App.Services.Overlays;
using ScreenTranslator.Core.Models;
using SkiaSharp;

namespace ScreenTranslator.IntegrationTests.Translation;

public sealed class RealComicColourReplayTests
{
    [Theory]
    [InlineData("en-comic")]
    [InlineData("ja-vertical")]
    public async Task SavedRealVisionBlocksRetainBlackInkAndEraseTheirLocatedOriginals(string name)
    {
        var source=CapturedBitmapEncoder.DecodeOriginal(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,"Fixtures","colour-real-"+name+".png")));
        using var json=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Fixtures","colour-real-"+name+".json")));
        var blocks=json.RootElement.GetProperty("Blocks").Deserialize<TranslatedBlock[]>()!;
        var renderer=new InPlaceImageRenderer(source);
        using var composite=LocalTextDetector.ToSkia(source);using var canvas=new SKCanvas(composite);
        var patches=new List<object>();
        foreach(var block in blocks.Where(block=>block.Bounds.IsUsable))
        {
            var patch=Assert.IsType<InPlacePatch>(renderer.Prepare(block));
            var style=ComicStyleAnalyzer.Analyze(source,patch,block);
            Assert.True(patch.Foreground.Red<60&&patch.Foreground.Green<60&&patch.Foreground.Blue<60,
                $"{name}/{block.Id}: original black lettering must not become {patch.Foreground} (background {patch.InkPalette?.Background}).");
            Assert.False(style.Hollow);
            using var repaired=LocalTextDetector.ToSkia(source);
            using(var repairCanvas=new SKCanvas(repaired))using(var background=LocalTextDetector.ToSkia(patch.Background,patch.Transparent))
                repairCanvas.DrawBitmap(background,patch.Bounds.X,patch.Bounds.Y);
            var originalInk=CountInk(source,block.TextBounds,patch.InkPalette!.Background);
            var residual=CountInk(Capture(repaired),block.TextBounds,patch.InkPalette.Background);
            patches.Add(new{block.Id,patch.Bounds,patch.RepairMethod,patch.ErasedPixels,Foreground=patch.Foreground.ToString(),
                Background=patch.InkPalette.Background.ToString(),Outline=patch.InkPalette.Outline?.ToString(),style.Hollow,OriginalInkPixels=originalInk,ResidualInkPixels=residual});
            using var tile=LocalTextDetector.ToSkia(renderer.Render(patch with{Style=style},block),patch.Transparent);
            canvas.DrawBitmap(tile,patch.Bounds.X,patch.Bounds.Y);
        }
        var directory=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../work/typography-colour-qa/real-replay",name));
        Directory.CreateDirectory(directory);
        using var output=composite.Encode(SKEncodedImageFormat.Png,100);
        await File.WriteAllBytesAsync(Path.Combine(directory,"translated.png"),output.ToArray());
        await File.WriteAllTextAsync(Path.Combine(directory,"replay.json"),JsonSerializer.Serialize(new{Offline=true,ApiCalls=0,
            Provenance="Frozen vision-speed run-1 result geometry; no gold coordinates fed to renderer",UnlocatedBlocks=blocks.Count(block=>!block.Bounds.IsUsable),Patches=patches},
            new JsonSerializerOptions{WriteIndented=true}));
        foreach(var patch in patches)
        {
            var item=JsonSerializer.SerializeToElement(patch);
            var original=item.GetProperty("OriginalInkPixels").GetInt32();var remaining=item.GetProperty("ResidualInkPixels").GetInt32();
            Assert.True(remaining<=Math.Max(20,original*.01),$"{name}/{item.GetProperty("Id").GetString()}: {remaining}/{original} original ink pixels remain.");
        }
    }
    private static int CountInk(CapturedBitmap bitmap,IReadOnlyList<PixelRect> words,SKColor background)
    {
        var count=0;var data=bitmap.Pixels.Span;
        foreach(var word in words)
        {
            var box=word.Intersect(new(0,0,bitmap.Width,bitmap.Height));
            for(var y=box.Y;y<box.Bottom;y++)for(var x=box.X;x<box.Right;x++)
            {var p=y*bitmap.Stride+x*4;if(LocalInkColorSampler.Distance(new(data[p+2],data[p+1],data[p]),background)>=12)count++;}
        }
        return count;
    }
    private static CapturedBitmap Capture(SKBitmap bitmap)
    {var bytes=new byte[bitmap.Width*bitmap.Height*4];System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(),bytes,0,bytes.Length);return new(bitmap.Width,bitmap.Height,bitmap.Width*4,bytes);}
}
