using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using ScreenTranslator.App.Services.Capture;
using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.App.Services.Overlays;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;
using SkiaSharp;

namespace ScreenTranslator.IntegrationTests.Translation;

public sealed class ProductRenderingTests
{
    [Fact]
    public void SlantedWordsUseTheirBaselineAndRotatedTextMustFit()
    {
        var block=new TranslatedBlock("p1","","Hello world",new(10,10,170,60)){PreciseWords=true,
            TextBounds=[new(10,10,50,20),new(70,21,50,20),new(130,31,50,20)]};
        Assert.InRange(ComicStyleAnalyzer.EstimateWordBaseline(block),8,12);
        using var font=new SKFont(SKTypeface.Default,24);
        Assert.True(InPlaceImageRenderer.Fits("Hello world",font,false,180,30));
        Assert.False(InPlaceImageRenderer.FitsStyled("Hello world",font,false,180,30,20));
    }
    [Fact]
    public void CloseDenseColumnsStaySeparateAndWrappedParagraphStaysWhole()
    {
        var words=Enumerable.Range(0,8).SelectMany(y=>new[]{new PixelRect(10,10+y*22,180,18),new PixelRect(205,10+y*22,180,18),
            new PixelRect(10,10+y*22,60,18),new PixelRect(280,10+y*22,60,18)}).ToArray();
        var box=words.Aggregate((a,b)=>a.Union(b));var split=ParagraphColumnSplitter.Split([new(box,words,false)]);
        Assert.Equal(2,split.Count);Assert.Equal(words.Length,split.Sum(g=>g.TextBounds.Count));
        var one=words.Where(w=>w.Center.X<200).ToArray();Assert.Single(ParagraphColumnSplitter.Split([new(one.Aggregate((a,b)=>a.Union(b)),one,false)]));
    }
    [Fact]
    public void SmallPanelAroundOneCaptionDoesNotEraseItsCharactersAndArtwork()
    {
        using var bitmap=new SKBitmap(600,600,SKColorType.Bgra8888,SKAlphaType.Opaque);
        using(var canvas=new SKCanvas(bitmap))using(var paint=new SKPaint{Color=SKColors.Black,Style=SKPaintStyle.Stroke,StrokeWidth=2})
        {canvas.Clear(SKColors.White);canvas.DrawRect(new SKRect(20,20,160,150),paint);}
        Assert.Null(new BalloonInteriorDetector(Capture(bitmap)).Find(new(50,40,65,20)));
    }

    [Fact]
    public void DenseReadingSheetsRetainAllRegionLabelsAndBoundMemory()
    {
        var image=White(600,4000);
        var regions=Enumerable.Range(0,20).Select(i=>new ImageAtlasRegion("r"+(i+1),new(10,i*190,560,170),default)
            {TextBounds=[new(20,i*190+10,500,18)]}).ToArray();
        var sheets=VisionReadingSheets.Build(image,regions);Assert.True(sheets.Count>1);
        foreach(var png in sheets){using var bitmap=SKBitmap.Decode(png);Assert.InRange(bitmap.Width,1,1100);Assert.InRange(bitmap.Height,1,2200);}
    }
    [Fact]
    public void NonBalloonTileCannotRestoreOriginalPixelsOverAnotherTranslation()
    {
        var source=Letters();var block=new TranslatedBlock("p1","HELLO","你好",new(25,25,150,40))
            {HasVisionPlacement=true,PreciseWords=true,TextBounds=[new(25,25,150,40)]};
        var patch=new InPlaceImageRenderer(source).Prepare(block)!;
        Assert.True(patch.Transparent);
        using var composite=LocalTextDetector.ToSkia(source);
        using(var canvas=new SKCanvas(composite))using(var pen=new SKPaint{Color=SKColors.Red})
        {canvas.DrawRect(new SKRect(patch.Bounds.X,patch.Bounds.Y,patch.Bounds.X+3,patch.Bounds.Y+3),pen);
            using var tile=LocalTextDetector.ToSkia(patch.Background,true);canvas.DrawBitmap(tile,patch.Bounds.X,patch.Bounds.Y);}
        Assert.Equal(SKColors.Red,composite.GetPixel(patch.Bounds.X,patch.Bounds.Y));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BubbleRepairIsTransparentOutsideAndKeepsOriginalBorder(bool dark)
    {
        using var bitmap=new SKBitmap(800,600,SKColorType.Bgra8888,SKAlphaType.Opaque);
        using(var canvas=new SKCanvas(bitmap))using(var fill=new SKPaint{Color=dark?SKColors.Black:SKColors.White})using(var border=new SKPaint{Color=dark?SKColors.White:SKColors.Black,Style=SKPaintStyle.Stroke,StrokeWidth=3})
        using(var font=new SKFont(SKTypeface.Default,24))using(var ink=new SKPaint{Color=dark?SKColors.White:SKColors.Black})
        {canvas.Clear(SKColors.Gray);canvas.DrawOval(new(60,80,230,240),fill);canvas.DrawOval(new(60,80,230,240),border);canvas.DrawText("HELLO",90,150,font,ink);}
        var source=Capture(bitmap);var original=source.Pixels.ToArray();
        var block=new TranslatedBlock("p1","","你好，世界",new(85,120,125,35)){HasVisionPlacement=true};
        var renderer=new InPlaceImageRenderer(source);var patch=renderer.Prepare(block)!;
        Assert.Equal("balloon",patch.RepairMethod);Assert.True(patch.Transparent);
        Assert.Contains((byte)0,patch.Background.Pixels.ToArray().Where((_,i)=>i%4==3));
        var rendered=renderer.Render(patch,block);var png=CapturedBitmapEncoder.ToPng(rendered,true);
        using var decoded=SKBitmap.Decode(png);Assert.Equal(0,decoded.GetPixel(0,0).Alpha);
        Assert.Equal(original,source.Pixels.ToArray());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PanelOutlineDoesNotBecomeADarkOrLightBalloon(bool dark)
    {
        using var bitmap=new SKBitmap(400,300,SKColorType.Bgra8888,SKAlphaType.Opaque);
        using(var canvas=new SKCanvas(bitmap))using(var pen=new SKPaint{Color=dark?SKColors.White:SKColors.Black,Style=SKPaintStyle.Stroke,StrokeWidth=3})
        {canvas.Clear(dark?SKColors.Black:SKColors.White);canvas.DrawRect(new(20,20,380,280),pen);}
        Assert.Null(new BalloonInteriorDetector(Capture(bitmap)).Find(new(60,40,100,30)));
    }
    [Fact]
    public void NativeWordsKeepConnectedHandwritingAndCaptionFrame()
    {
        using var bitmap=new SKBitmap(400,300,SKColorType.Bgra8888,SKAlphaType.Opaque);
        using(var canvas=new SKCanvas(bitmap))using(var pen=new SKPaint{Color=SKColors.Black})using(var font=new SKFont(SKTypeface.Default,24))
        {canvas.Clear(SKColors.White);pen.IsStroke=true;pen.StrokeWidth=3;canvas.DrawRect(new(10,40,390,130),pen);pen.IsStroke=false;canvas.DrawText("HELLO WORLD",35,88,font,pen);}
        var image=Capture(bitmap);var block=new TranslatedBlock("p1","","你好",new(32,65,210,29)){HasVisionPlacement=true,PreciseWords=true,TextBounds=[new(32,65,92,29),new(135,65,107,29)],GlyphSizeHint=24};
        var patch=new InPlaceImageRenderer(image).Prepare(block)!;Assert.True(patch.ErasedPixels>500);
        var original=Pixel(image,15,40);using var canvasBitmap=LocalTextDetector.ToSkia(image);using(var canvas=new SKCanvas(canvasBitmap))using(var tile=LocalTextDetector.ToSkia(patch.Background,patch.Transparent))canvas.DrawBitmap(tile,patch.Bounds.X,patch.Bounds.Y);
        Assert.Equal(original,canvasBitmap.GetPixel(15,40));
    }
    [Fact]
    public void CacheSeparatesImageLanguageContextAndModelReasoning()
    {
        var image=White(16,16);var config=new AppSettings();var key=ImageTranslationCache.Key(image,config);
        Assert.NotEqual(key,ImageTranslationCache.Key(image,config with{TargetLanguage="en"}));
        Assert.NotEqual(key,ImageTranslationCache.Key(image,config with{TranslationContext="medical"}));
        Assert.NotEqual(key,ImageTranslationCache.Key(image,config with{ThinkingLevel=ThinkingLevel.Low}));
        Assert.NotEqual(key,ImageTranslationCache.Key(image,config with{BriefThinkingGuidanceEnabled=false}));
        var pixels=image.Pixels.ToArray();pixels[0]=0;Assert.NotEqual(key,ImageTranslationCache.Key(new(16,16,64,pixels),config));
    }
    [Fact]
    public void CacheEvictsOldestAndRetainsRecentlyUsed()
    {
        var image=White(8,8);var tile=new RenderedImageTile("p1",new(0,0,8,8),new byte[8],image,true);var item=new CachedImageTranslation(null,new([new("p1","","你好",tile.Bounds)]),[tile]);
        var cache=new ImageTranslationCache(item.Bytes*2+1);cache.Set("a",item);cache.Set("b",item);Assert.NotNull(cache.Get("a"));cache.Set("c",item);
        Assert.Null(cache.Get("b"));Assert.NotNull(cache.Get("a"));Assert.NotNull(cache.Get("c"));
    }
    [Fact]
    public void IncompleteImageIsNotCached()
    {
        var cache=new ImageTranslationCache();cache.Set("a",new(null,new([new("p1","","你好",new(1,1,5,5))]),[]));Assert.Null(cache.Get("a"));
    }
    [Fact]
    public async Task StreamPublishesBeforeCompletionAndCachedReuseKeepsIdenticalBytes()
    {
        var image=Letters();using var session=new InPlaceRenderSession(image);var first=new TaskCompletionSource<RenderedImageTile>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TilesChanged+=tiles=>{if(tiles.Count>0)first.TrySetResult(tiles[0]);};
        var block=new TranslatedBlock("p1","HELLO","你",new(25,25,150,40)){HasVisionPlacement=true,PreciseWords=true,TextBounds=[new(25,25,150,40)],IsComplete=false};
        session.Update([block]);var partial=await first.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(partial.Optimized);
        session.Update([block with{Translation="你好，世界",IsComplete=true}],true);var final=await session.FinishAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(final);Assert.True(final[0].Optimized);
        using var cached=new InPlaceRenderSession(image,cached:final);cached.Update([block],true);var reused=await cached.FinishAsync();Assert.Same(final[0].Png,reused[0].Png);
    }
    [Fact]
    public async Task LatestSnapshotReplacesOldStreamingPrefix()
    {
        using var session=new InPlaceRenderSession(Letters());var block=new TranslatedBlock("p1","HELLO","你",new(25,25,150,40)){HasVisionPlacement=true,PreciseWords=true,TextBounds=[new(25,25,150,40)],IsComplete=false};
        for(var i=0;i<30;i++)session.Update([block with{Translation=new string('你',i+1)}]);
        session.Update([block with{Translation="最终译文",IsComplete=true}],true);var tiles=await session.FinishAsync().WaitAsync(TimeSpan.FromSeconds(5));Assert.Single(tiles);Assert.Equal("最终译文",tiles[0].Translation);
    }
    [Fact]
    public void ChineseWrapTokensKeepCommonWordsAndLatinNames()
    {var tokens=ChineseLineTokens.Split("没关系，问工作人员 James").ToArray();Assert.Contains("没关系",tokens);Assert.Contains("工作人员",tokens);Assert.Contains("James",tokens);}
    [Theory]
    [InlineData(ComicFontFamily.Sans)]
    [InlineData(ComicFontFamily.Serif)]
    [InlineData(ComicFontFamily.Handwriting)]
    [InlineData(ComicFontFamily.Brush)]
    [InlineData(ComicFontFamily.Display)]
    public void EveryFamilyHasChineseAndKanaFallback(ComicFontFamily family)
    {Assert.All(ComicFontCatalog.ForGlyph(new(family),"你好").GetGlyphs("你好"),g=>Assert.NotEqual(0,g));Assert.All(ComicFontCatalog.ForGlyph(new(family),"モト子").GetGlyphs("モト子"),g=>Assert.NotEqual(0,g));}
    [Fact]
    public void BridgeRejectsFragmentedWordRowsAcrossDifferentPanels()
    {
        var source=White(600,400);var words=new TextRegion[]{new(new(20,20,80,25),.9),new(new(102,20,40,25),.9),new(new(145,22,40,25),.9),new(new(20,50,120,25),.9),new(new(350,20,120,25),.9)};
        var groups=NativeWordGrouper.Group(words,source);Assert.Equal(2,groups.Count);Assert.Contains(groups,g=>g.TextBounds.Count==4);
    }
    [Fact]
    public void PreciseAlignedFragmentsCombineButDifferentBubblesDoNot()
    {
        ImageAtlasRegion[] regions=[new("r1",new(10,10,100,30),default){PreciseWords=true,IsParagraph=true,TextBounds=[new(15,15,90,20)]},
            new("r2",new(10,38,100,30),default){PreciseWords=true,IsParagraph=true,TextBounds=[new(15,40,90,20)]},
            new("r3",new(350,10,100,30),default){PreciseWords=true,IsParagraph=true,TextBounds=[new(355,15,90,20)]}];
        var good="{\"blocks\":[{\"id\":\"p1\",\"regions\":[\"r1\",\"r2\"],\"translation\":\"你好\"}]}";
        Assert.True(DirectVisionTranslation.Parse(good,new(600,400),regions).Blocks[0].Bounds.IsUsable);
        Assert.Throws<TranslationFormatException>(()=>DirectVisionTranslation.Parse(good.Replace("r2","r3"),new(600,400),regions));
    }
    private static CapturedBitmap Capture(SKBitmap bitmap){var bytes=new byte[bitmap.Width*bitmap.Height*4];Marshal.Copy(bitmap.GetPixels(),bytes,0,bytes.Length);return new(bitmap.Width,bitmap.Height,bitmap.Width*4,bytes);}
    private static CapturedBitmap White(int w,int h)=>new(w,h,w*4,Enumerable.Repeat((byte)255,w*h*4).ToArray());
    private static CapturedBitmap Letters(){using var bitmap=new SKBitmap(400,240,SKColorType.Bgra8888,SKAlphaType.Opaque);using var canvas=new SKCanvas(bitmap);using var paint=new SKPaint{Color=SKColors.Black};using var font=new SKFont(SKTypeface.Default,30);canvas.Clear(SKColors.White);canvas.DrawText("HELLO",30,55,font,paint);return Capture(bitmap);}
    private static SKColor Pixel(CapturedBitmap image,int x,int y){var p=y*image.Stride+x*4;return new(image.Pixels.Span[p+2],image.Pixels.Span[p+1],image.Pixels.Span[p],image.Pixels.Span[p+3]);}
}
