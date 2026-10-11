using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.Core.Models;
using SkiaSharp;

namespace ScreenTranslator.App.Services.Capture;

/// <summary>Bounded reading sheets keep small glyphs readable on dense or tall pages.</summary>
public static class VisionReadingSheets
{
    public static IReadOnlyList<byte[]> Build(CapturedBitmap source,IReadOnlyList<ImageAtlasRegion> regions)
    {
        const int edge=1100;
        var batches=new List<List<(ImageAtlasRegion Region,float Scale,int W,int H)>>();
        var current=new List<(ImageAtlasRegion Region,float Scale,int W,int H)>();var area=0L;
        foreach(var region in regions)
        {
            var words=region.TextBounds.Count>0?region.TextBounds:[region.SourceBounds];
            var sizes=words.Select(w=>Math.Min(w.Width,w.Height)).Order().ToArray();
            var scale=(float)Math.Min(Math.Clamp(28d/Math.Max(10,sizes[sizes.Length/2]),1,2.5),
                (edge-24d)/Math.Max(region.SourceBounds.Width,region.SourceBounds.Height));
            var w=Math.Max(1,(int)Math.Ceiling(region.SourceBounds.Width*scale));var h=Math.Max(1,(int)Math.Ceiling(region.SourceBounds.Height*scale));
            var next=(w+24L)*(h+38);
            if(current.Count>0&&(area+next>edge*(long)edge*.7||current.Count>=6))
            {batches.Add(current);current=[];area=0;}
            current.Add((region,scale,w,h));area+=next;
        }
        if(current.Count>0)batches.Add(current);
        using var original=LocalTextDetector.ToSkia(source);using var image=SKImage.FromBitmap(original);
        using var font=new SKFont(SKTypeface.Default,20);using var paint=new SKPaint{Color=SKColors.Black,IsAntialias=true};
        var output=new List<byte[]>();
        foreach(var batch in batches)
        {
            var positions=new List<(ImageAtlasRegion R,SKRect Dest)>();var x=12;var y=12;var row=0;
            foreach(var item in batch)
            {if(x>12&&x+item.W+12>edge){x=12;y+=row+12;row=0;}
                positions.Add((item.Region,new(x,y+28,x+item.W,y+28+item.H)));x+=item.W+12;row=Math.Max(row,item.H+28);}
            using var bitmap=new SKBitmap(edge,y+row+12,SKColorType.Bgra8888,SKAlphaType.Opaque);using var canvas=new SKCanvas(bitmap);canvas.Clear(SKColors.White);
            foreach(var item in positions)
            {var b=item.R.SourceBounds;canvas.DrawText("["+item.R.Id+"]",item.Dest.Left,item.Dest.Top-7,font,paint);
                canvas.DrawImage(image,new SKRect(b.X,b.Y,b.Right,b.Bottom),item.Dest,new SKSamplingOptions(SKFilterMode.Linear));}
            using var png=bitmap.Encode(SKEncodedImageFormat.Png,100);output.Add(png.ToArray());
        }
        return output;
    }
}
