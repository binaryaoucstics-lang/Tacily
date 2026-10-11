using ScreenTranslator.Core.Models;
using SkiaSharp;
using OpenCvSharp;
using System.Runtime.InteropServices;

namespace ScreenTranslator.App.Services.Overlays;

public sealed record TextInkPalette(SKColor Fill, SKColor? Outline, float OutlineFraction,
    float Confidence, SKColor Background, bool Hollow = false);

/// <summary>CPU sampling of original glyph pixels, before erasure or antialias dilation.</summary>
public static class LocalInkColorSampler
{
    public static TextInkPalette Sample(CapturedBitmap source, IReadOnlyList<PixelRect> words,
        SKColor background, float fontSize, CapturedBitmap? segmentation = null,
        PixelRect? maskBounds = null, byte[]? mask = null)
    {
        var usable = words.Select(word => word.Intersect(new(0, 0, source.Width, source.Height)))
            .Where(word => word.IsUsable).ToArray();
        var fallback = new TextInkPalette(Luma(background) < 128 ? SKColors.White : new(20, 20, 20),
            null, 0, 0, background);
        if (usable.Length == 0) return fallback;
        var region = usable.Aggregate((a, b) => a.Union(b));
        if ((long)region.Width * region.Height > 1_000_000) return fallback;
        var sampled = new SKColor[region.Width * region.Height];
        var selected = new bool[sampled.Length];
        var bins = new Dictionary<int, Bucket>();
        var total = 0;
        for (var y = 0; y < region.Height; y++) for (var x = 0; x < region.Width; x++)
        {
            var gx = region.X + x; var gy = region.Y + y;
            if (!usable.Any(word => gx >= word.X && gx < word.Right && gy >= word.Y && gy < word.Bottom)) continue;
            if (mask is not null && maskBounds is { } mb
                && (gx < mb.X || gx >= mb.Right || gy < mb.Y || gy >= mb.Bottom
                    || mask[(gy - mb.Y) * mb.Width + gx - mb.X] == 0)) continue;
            if (segmentation is not null && segmentation.Pixels.Span[gy * segmentation.Stride + gx * 4] < 65) continue;
            var p = gy * source.Stride + gx * 4; var bytes = source.Pixels.Span;
            var color = new SKColor(bytes[p + 2], bytes[p + 1], bytes[p]);
            if (bytes[p + 3] < 200 || Distance(color, background) < 28) continue;
            var index = y * region.Width + x; selected[index] = true; sampled[index] = color; total++;
            var id = (color.Red >> 4) * 256 + (color.Green >> 4) * 16 + (color.Blue >> 4);
            if (!bins.TryGetValue(id, out var bucket)) bins[id] = bucket = new();
            bucket.Add(color,x,y);
        }
        if (total < 6) return fallback;
        var clusters = new List<Bucket>();
        foreach (var bucket in bins.Values.OrderByDescending(bucket => bucket.Count).Take(32))
        {
            var near = clusters.FirstOrDefault(cluster => Distance(cluster.Color, bucket.Color) < 30);
            if (near is not null) near.Add(bucket); else clusters.Add(bucket);
        }
        var glyphClusters=clusters.Where(cluster=>!(cluster.Width>Math.Max(40,fontSize*4)&&cluster.Height<=Math.Max(3,fontSize*.12)
            ||cluster.Height>Math.Max(40,fontSize*4)&&cluster.Width<=Math.Max(3,fontSize*.12))).ToArray();
        if(glyphClusters.Length==0)return fallback;
        var chromatic = glyphClusters.Where(cluster => Saturation(cluster.Color) >= 45
            && cluster.Count >= Math.Max(8, total * .08)).OrderByDescending(cluster => cluster.Count).FirstOrDefault();
        var fill = chromatic ?? glyphClusters.MaxBy(cluster => cluster.Count)!;
        var fillColor = fill.Color;
        Bucket? outline = null; var bestTouches = 0;
        foreach (var candidate in glyphClusters.Where(cluster => !ReferenceEquals(cluster, fill)
                     && Distance(cluster.Color, fillColor) > 65 && !BackgroundBlend(cluster.Color,fillColor,background)
                     && cluster.Count >= Math.Max(8, total * .07)).Take(4))
        {
            // A second colour is an outline only when it actually borders the
            // fill; unrelated artwork and coloured backgrounds cannot win a vote.
            var touches = 0;
            for (var y = 0; y < region.Height; y++) for (var x = 0; x < region.Width; x++)
            {
                var index = y * region.Width + x;
                if (!selected[index] || Distance(sampled[index], candidate.Color) > 24) continue;
                var adjacent = false;
                for (var dy = -2; dy <= 2 && !adjacent; dy++) for (var dx = -2; dx <= 2; dx++)
                {
                    var nx = x + dx; var ny = y + dy;
                    if (nx < 0 || nx >= region.Width || ny < 0 || ny >= region.Height) continue;
                    var other = ny * region.Width + nx;
                    if (selected[other] && Distance(sampled[other], fillColor) <= 24) { adjacent = true; break; }
                }
                if (adjacent) touches++;
            }
            if (touches >= Math.Max(10, fill.Count * .06) && touches > bestTouches)
            { outline = candidate; bestTouches = touches; }
        }
        if(outline is not null)
        {
            var outerRate=BackgroundTouchRate(sampled,selected,region.Width,region.Height,fillColor);
            var innerRate=BackgroundTouchRate(sampled,selected,region.Width,region.Height,outline.Color);
            // A thick outer stroke can have more pixels than the actual fill.
            // The inner colour touches its enclosing stroke, not the background.
            if(outerRate>.15&&innerRate<outerRate*.55)
            {var previous=fill;fill=outline;outline=previous;fillColor=fill.Color;}
        }
        if(outline is null&&Saturation(fillColor)>=45&&TryHollow(sampled,selected,region.Width,region.Height,fillColor,out var hollowRatio))
            return new(background,fillColor,Math.Clamp((1-hollowRatio)*.17f,.025f,.09f),.9f,background,true);
        var fraction = outline is null ? 0 : Math.Clamp(
            (float)Math.Sqrt(outline.Count / (double)Math.Max(1, fill.Count)) * .045f, .025f, .10f);
        return new(fillColor, outline?.Color, fraction, fill.Count >= 24 ? .95f : .65f, background);
    }

    public static int Distance(SKColor a, SKColor b) => Math.Max(Math.Max(Math.Abs(a.Red - b.Red), Math.Abs(a.Green - b.Green)), Math.Abs(a.Blue - b.Blue));
    private static bool BackgroundBlend(SKColor color,SKColor fill,SKColor background)
    {
        var dr=background.Red-fill.Red;var dg=background.Green-fill.Green;var db=background.Blue-fill.Blue;
        var denominator=dr*dr+dg*dg+db*db;if(denominator==0)return false;
        var amount=((color.Red-fill.Red)*dr+(color.Green-fill.Green)*dg+(color.Blue-fill.Blue)*db)/(double)denominator;
        if(amount<0||amount>1)return false;
        var predicted=new SKColor((byte)Math.Clamp(fill.Red+dr*amount,0,255),(byte)Math.Clamp(fill.Green+dg*amount,0,255),(byte)Math.Clamp(fill.Blue+db*amount,0,255));
        return Distance(color,predicted)<24;
    }
    private static float BackgroundTouchRate(SKColor[] sampled,bool[] selected,int width,int height,SKColor colour)
    {
        var count=0;var touches=0;
        for(var y=0;y<height;y++)for(var x=0;x<width;x++)
        {
            var index=y*width+x;if(!selected[index]||Distance(sampled[index],colour)>24)continue;
            count++;var boundary=false;
            for(var dy=-2;dy<=2&&!boundary;dy++)for(var dx=-2;dx<=2;dx++)
            {var nx=x+dx;var ny=y+dy;if(nx<0||nx>=width||ny<0||ny>=height||!selected[ny*width+nx]){boundary=true;break;}}
            if(boundary)touches++;
        }
        return count==0?0:touches/(float)count;
    }
    private static bool TryHollow(SKColor[] sampled,bool[] selected,int width,int height,SKColor ink,out float ratio)
    {
        ratio=0;var bytes=new byte[selected.Length];
        for(var i=0;i<bytes.Length;i++)if(selected[i]&&Distance(sampled[i],ink)<=35)bytes[i]=255;
        var pin=GCHandle.Alloc(bytes,GCHandleType.Pinned);
        try
        {
            using var mask=Mat.FromPixelData(height,width,MatType.CV_8UC1,pin.AddrOfPinnedObject());
            Cv2.FindContours(mask,out OpenCvSharp.Point[][] contours,out var hierarchy,RetrievalModes.CComp,ContourApproximationModes.ApproxSimple);
            double exterior=0,holes=0;var glyphs=0;
            for(var i=0;i<contours.Length;i++)
            {
                var area=Math.Abs(Cv2.ContourArea(contours[i]));
                if(hierarchy[i].Parent<0){exterior+=area;if(area>40)glyphs++;}else holes+=area;
            }
            ratio=exterior>0?(float)(holes/exterior):0;
            return glyphs>=3&&exterior>150&&ratio>.60f;
        }
        finally{pin.Free();}
    }
    private static int Saturation(SKColor color) => Math.Max(Math.Max(color.Red, color.Green), color.Blue) - Math.Min(Math.Min(color.Red, color.Green), color.Blue);
    private static int Luma(SKColor color) => (color.Red * 299 + color.Green * 587 + color.Blue * 114) / 1000;
    private sealed class Bucket
    {
        private long _r, _g, _b;
        private int _minX=int.MaxValue,_minY=int.MaxValue,_maxX=int.MinValue,_maxY=int.MinValue;
        public int Count { get; private set; }
        public int Width=>_maxX-_minX+1;
        public int Height=>_maxY-_minY+1;
        public SKColor Color => new((byte)(_r / Count), (byte)(_g / Count), (byte)(_b / Count));
        public void Add(SKColor color,int x,int y) { Count++; _r += color.Red; _g += color.Green; _b += color.Blue;
            _minX=Math.Min(_minX,x);_maxX=Math.Max(_maxX,x);_minY=Math.Min(_minY,y);_maxY=Math.Max(_maxY,y);}
        public void Add(Bucket other) { Count += other.Count; _r += other._r; _g += other._g; _b += other._b;
            _minX=Math.Min(_minX,other._minX);_maxX=Math.Max(_maxX,other._maxX);_minY=Math.Min(_minY,other._minY);_maxY=Math.Max(_maxY,other._maxY);}
    }
}
