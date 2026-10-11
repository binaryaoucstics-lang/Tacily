using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using OpenCvSharp;
using SkiaSharp;
using ScreenTranslator.Core.Models;
using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.App.Services.Capture;

namespace ScreenTranslator.App.Services.Overlays;

public sealed record InPlacePatch(PixelRect Bounds, CapturedBitmap Background, SKColor Foreground, float FontSize, bool IsVertical,
    string RepairMethod, int ErasedPixels)
{
    public PixelRect LayoutBounds { get; init; }
    public ComicTextStyle Style { get; init; }=ComicTextStyle.Plain;
    public bool Transparent {get;init;}
    public IReadOnlyList<PixelPoint> ClipPolygon {get;init;}=[];
    public TextInkPalette? InkPalette {get;init;}
}

/// <summary>Local stroke removal and typesetting. The original image is immutable.</summary>
public sealed class InPlaceImageRenderer(CapturedBitmap source,CapturedBitmap? segmentation=null,Action<PixelRect,byte[]>? diagnosticMask=null)
{
    private readonly BalloonInteriorDetector _balloons=new(source);
    private static readonly Lazy<SKTypeface> ChineseTypeface = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSansCJKsc-Regular.otf");
        return File.Exists(path) ? SKTypeface.FromFile(path) : SKTypeface.FromFamilyName("Microsoft YaHei", SKFontStyle.Normal);
    });
    public InPlacePatch? Prepare(TranslatedBlock block)
    {
        if (!block.Bounds.IsUsable || !block.HasVisionPlacement && string.IsNullOrWhiteSpace(block.SourceText)) return null;
        if(block.HasVisionPlacement&&_balloons.Find(block.Bounds,block.TextBounds) is {} balloon)return PrepareBalloon(block,balloon);
        var text = (block.TextBounds.Count > 0 ? block.TextBounds : [block.Bounds]).Where(bounds => bounds.IsUsable).ToArray();
        if (text.Length == 0) return null;
        var sourceVertical = text.Count(rect => rect.Height >= rect.Width * 1.7) * 2 >= text.Length;
        var vertical = block.HasVisionPlacement ? block.IsVertical : block.IsVertical || sourceVertical;
        var thickness = text.Select(rect => sourceVertical ? rect.Width : rect.Height).Order().ElementAt(text.Length / 2);
        if (block.HasVisionPlacement) thickness = block.GlyphSizeHint>0?(int)block.GlyphSizeHint:EstimateGlyphSize(block.Bounds);
        var strokeMargin = Math.Clamp(thickness / 10, 3, 8);
        var padding = Math.Max(strokeMargin + 1, Math.Clamp(thickness / 8, 2, 10));
        var original = block.HasVisionPlacement ? block.Bounds.Union(text.Aggregate((a, b) => a.Union(b))) : text.Aggregate((a, b) => a.Union(b));
        var layout = block.LayoutBounds.IsUsable ? block.LayoutBounds.Intersect(new(0, 0, source.Width, source.Height)) : original;
        var envelope = original.Union(layout);
        var bounds = new PixelRect(envelope.X - padding, envelope.Y - padding, envelope.Width + padding * 2, envelope.Height + padding * 2)
            .Intersect(new(0, 0, source.Width, source.Height));
        if (!bounds.IsUsable || (long)bounds.Width * bounds.Height > 3_000_000) return null;
        var pixels = new byte[bounds.Width * bounds.Height * 4];
        for (var y = 0; y < bounds.Height; y++) source.Pixels.Span.Slice((bounds.Y + y) * source.Stride + bounds.X * 4, bounds.Width * 4)
            .CopyTo(pixels.AsSpan(y * bounds.Width * 4));
        var ring = new List<SKColor>();
        for (var y = 0; y < bounds.Height; y++)
            for (var x = 0; x < bounds.Width; x++)
                if (x < 2 || y < 2 || x >= bounds.Width - 2 || y >= bounds.Height - 2) ring.Add(Color(pixels, (y * bounds.Width + x) * 4));
        var hasWordBackground=TryWordEdgeBackground(pixels,bounds,text,out var wordBackground,out var flatWordBackground);
        var background = hasWordBackground?wordBackground:block.TextBounds.Count>0
            ?WordBackground(pixels,bounds,text,ring):block.SourceIds.Count>0?WordBackground(pixels,bounds,[block.Bounds],ring):Median(ring);
        var darkBackground = Luma(background) < 128;
        var artwork = ProtectedArtwork(bounds, background, thickness, darkBackground);
        // Large effect lettering often overlaps drawing. A white median alone is not
        // evidence that its bounding rectangle is safe to paint over.
        var rectangular=block.PreciseWords&&(Luma(background)>220||Luma(background)<40)
            && thickness<=Math.Clamp(source.Width*.05,48,100);
        if(rectangular)
        {
            var structural=StructuralBorders(bounds,thickness,darkBackground);
            for(var y=0;y<bounds.Height;y++)for(var x=0;x<bounds.Width;x++)
            {
                var index=y*bounds.Width+x;
                if(structural[index]){artwork[index]=true;continue;}
                if(text.Any(word=>x+bounds.X>=word.X&&x+bounds.X<word.Right&&y+bounds.Y>=word.Y&&y+bounds.Y<word.Bottom))artwork[index]=false;
            }
        }
        var mask = new byte[bounds.Width * bounds.Height];
        var count = 0;
        foreach (var line in block.HasVisionPlacement && (block.SourceIds.Count == 0||block.PreciseWords) ? text.Append(block.Bounds) : text)
        {
            var local = new PixelRect(line.X - bounds.X - strokeMargin, line.Y - bounds.Y - strokeMargin,
                line.Width + strokeMargin * 2, line.Height + strokeMargin * 2)
                .Intersect(new(0, 0, bounds.Width, bounds.Height));
            for (var y = local.Y; y < local.Bottom; y++)
                for (var x = local.X; x < local.Right; x++)
                {
                    var index = y * bounds.Width + x;
                    var predicted=segmentation is not null && segmentation.Pixels.Span[(bounds.Y+y)*segmentation.Stride+(bounds.X+x)*4]>=65;
                    if(rectangular&&artwork[index])continue;
                    if(artwork[index] && segmentation is null&&!rectangular)continue;
                    if(segmentation is not null && !predicted && !text.Any(word=>word.Width<Math.Max(10,thickness*2)&&word.Height<Math.Max(15,thickness*2)
                        && x+bounds.X>=word.X&&x+bounds.X<word.Right&&y+bounds.Y>=word.Y&&y+bounds.Y<word.Bottom))continue;
                    if(artwork[index] && text.Length>0 && !text.Any(word=>(rectangular||word.Width<Math.Max(10,thickness*2)&&word.Height<Math.Max(15,thickness*2))
                        && x+bounds.X>=word.X&&x+bounds.X<word.Right&&y+bounds.Y>=word.Y&&y+bounds.Y<word.Bottom))continue;
                    if(segmentation is not null)artwork[index]=false;
                    var color = Color(pixels, index * 4);
                    var contrast = Luma(color) - Luma(background);
                    // Trusted word boxes remove their ink, not every background pixel.
                    // This retains graph-paper lines and avoids visible rectangular patches.
                    var insideWord=text.Any(word=>x+bounds.X>=word.X&&x+bounds.X<word.Right&&y+bounds.Y>=word.Y&&y+bounds.Y<word.Bottom);
                    var minimumContrast=flatWordBackground&&insideWord?4:rectangular||hasWordBackground&&insideWord?15:35;
                    var chromatic=Math.Max(Math.Max(color.Red,color.Green),color.Blue)-Math.Min(Math.Min(color.Red,color.Green),color.Blue)>35
                        &&Distance(color,background)>Math.Max(45,minimumContrast);
                    if (!chromatic&&(darkBackground ? contrast < minimumContrast : contrast > -minimumContrast)) continue;
                    if(rectangular && artwork[index])continue;
                    if (mask[index] != 0) continue;
                    mask[index] = 255; count++;
                }
        }
        if (count < 3 || count > mask.Length * (rectangular?.98:.5)) return null;
        // Sampling uses original glyph boxes. Artwork-protection guards may
        // temporarily exclude a letter touching a rule; they must not change
        // that letter's colour or prevent the existing grey-ink recovery.
        var palette=LocalInkColorSampler.Sample(source,text,background,Math.Clamp(thickness*.88f,9,96),segmentation);
        var foreground=palette.Fill;
        // Grey screenshot lettering can touch a black bubble border. Reclaim pixels matching
        // the lettering's colour while retaining the darker border's core.
        if (block.HasVisionPlacement && !darkBackground && Luma(foreground) is > 80 and < 200)
        {
            var originalInk = block.Bounds.Translate(new(-bounds.X, -bounds.Y)).Intersect(new(0, 0, bounds.Width, bounds.Height));
            for (var y = originalInk.Y; y < originalInk.Bottom; y++)
                for (var x = originalInk.X; x < originalInk.Right; x++)
                {
                    var index = y * bounds.Width + x;
                    var color = Color(pixels, index * 4);
                    var neutral = Math.Max(Math.Max(color.Red, color.Green), color.Blue) - Math.Min(Math.Min(color.Red, color.Green), color.Blue) < 15;
                    if (artwork[index] && (Distance(color, foreground) < 25 || neutral && Luma(color) >= Luma(foreground) - 25 && Luma(color) < Luma(background) - 45))
                    { artwork[index] = false; mask[index] = 255; }
                }
        }
        var pixelPin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        var maskPin = GCHandle.Alloc(mask, GCHandleType.Pinned);
        var method = "flat";
        try
        {
            using var bgra = Mat.FromPixelData(bounds.Height, bounds.Width, MatType.CV_8UC4, pixelPin.AddrOfPinnedObject());
            using var ink = Mat.FromPixelData(bounds.Height, bounds.Width, MatType.CV_8UC1, maskPin.AddrOfPinnedObject());
            // Preserve long panel/caption borders included by an expanded OCR rectangle.
            using (var labels = new Mat())
            using (var statistics = new Mat())
            using (var centers = new Mat())
            {
                var components = Cv2.ConnectedComponentsWithStats(ink, labels, statistics, centers);
                var borders = new HashSet<int>();
                for (var component = 1; component < components; component++)
                {
                    var w = statistics.At<int>(component, 2); var h = statistics.At<int>(component, 3);
                    if (segmentation is null && !block.PreciseWords && (w <= 3 && h > Math.Max(24, thickness * 1.3) || h <= 3 && w > Math.Max(30, thickness * 2))) borders.Add(component);
                }
                if (borders.Count > 0)
                {
                    var indices = new int[mask.Length]; Marshal.Copy(labels.Data, indices, 0, indices.Length);
                    for (var index = 0; index < indices.Length; index++) if (borders.Contains(indices[index])) mask[index] = 0;
                }
            }
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
            Cv2.Dilate(ink, ink, kernel);
            for (var index = 0; index < mask.Length; index++) if (artwork[index]) mask[index] = 0;
            count = Cv2.CountNonZero(ink);
            if(diagnosticMask is not null){var actual=new byte[mask.Length];Marshal.Copy(ink.Data,actual,0,actual.Length);diagnosticMask(bounds,actual);}
            if (count < 3) return null;
            var flat = rectangular || ring.Count(color => Distance(color, background) < 25) >= ring.Count * .9;
            if (flat)
            {
                for (var index = 0; index < mask.Length; index++)
                    if (mask[index] != 0)
                    {
                        pixels[index * 4] = background.Blue; pixels[index * 4 + 1] = background.Green;
                        pixels[index * 4 + 2] = background.Red; pixels[index * 4 + 3] = 255;
                    }
                if(rectangular&&!darkBackground)RestoreRuling(pixels,mask,bounds);
            }
            else
            {
                method = "telea";
                using var bgr = new Mat(); using var repaired = new Mat();
                Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
                Cv2.Inpaint(bgr, ink, repaired, 3, InpaintTypes.Telea);
                Cv2.CvtColor(repaired, bgra, ColorConversionCodes.BGR2BGRA);
                Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
            }
        }
        finally { pixelPin.Free(); maskPin.Free(); }
        if(flatWordBackground)
        {
            // Telea can reuse an original letter's pale antialias fringe and
            // reproduce its silhouette. Only the independently verified flat
            // glyph neighbourhood gets a solid repair; surrounding art keeps
            // its existing mask, protection and inpainting result.
            for(var y=0;y<bounds.Height;y++)for(var x=0;x<bounds.Width;x++)
            {
                var index=y*bounds.Width+x;if(mask[index]==0||!text.Any(word=>x+bounds.X>=word.X&&x+bounds.X<word.Right&&y+bounds.Y>=word.Y&&y+bounds.Y<word.Bottom))continue;
                pixels[index*4]=background.Blue;pixels[index*4+1]=background.Green;pixels[index*4+2]=background.Red;pixels[index*4+3]=255;
            }
            if(rectangular&&!darkBackground)RestoreRuling(pixels,mask,bounds);
            if(method=="telea")method="telea+flat-words";
        }
        // Each tile contains only its repair and newly drawn glyphs. Original
        // pixels outside the repair remain transparent, so neighbouring tiles
        // cannot restore untranslated text over an earlier translated block.
        for(var index=0;index<mask.Length;index++)if(mask[index]==0)
        {pixels[index*4]=pixels[index*4+1]=pixels[index*4+2]=pixels[index*4+3]=0;}
        return new(bounds, new(bounds.Width, bounds.Height, bounds.Width * 4, pixels), foreground,
            Math.Clamp(thickness * .88f, 9, 96), vertical, method, count)
            { LayoutBounds = layout.Intersect(bounds),Transparent=true,InkPalette=palette,Style=new(Outline:palette.OutlineFraction,Hollow:palette.Hollow) };
    }

    private void RestoreRuling(byte[] repaired,byte[] mask,PixelRect bounds)
    {
        var rows=new Dictionary<int,SKColor>();var columns=new Dictionary<int,SKColor>();
        bool Cyan(SKColor c)=>c.Red>=125&&Luma(c)>=150&&Math.Min(c.Green,c.Blue)-c.Red>=10&&Math.Max(c.Green,c.Blue)-c.Red>=22;
        for(var y=0;y<bounds.Height;y++)
        {
            var samples=new List<SKColor>();
            for(var x=0;x<bounds.Width;x++){var i=y*bounds.Width+x;if(mask[i]!=0)continue;var p=(bounds.Y+y)*source.Stride+(bounds.X+x)*4;var s=source.Pixels.Span;
                var c=new SKColor(s[p+2],s[p+1],s[p]);if(Cyan(c))samples.Add(c);}
            if(samples.Count>=Math.Max(4,bounds.Width*.05))rows[y]=Median(samples);
        }
        for(var x=0;x<bounds.Width;x++)
        {
            var samples=new List<SKColor>();
            for(var y=0;y<bounds.Height;y++){var i=y*bounds.Width+x;if(mask[i]!=0)continue;var p=(bounds.Y+y)*source.Stride+(bounds.X+x)*4;var s=source.Pixels.Span;
                var c=new SKColor(s[p+2],s[p+1],s[p]);if(Cyan(c))samples.Add(c);}
            if(samples.Count>=Math.Max(4,bounds.Height*.1))columns[x]=Median(samples);
        }
        foreach(var (y,color) in rows)for(var x=0;x<bounds.Width;x++)if(mask[y*bounds.Width+x]!=0)Put(y*bounds.Width+x,color);
        foreach(var (x,color) in columns)for(var y=0;y<bounds.Height;y++)if(mask[y*bounds.Width+x]!=0)Put(y*bounds.Width+x,color);
        void Put(int index,SKColor color){repaired[index*4]=color.Blue;repaired[index*4+1]=color.Green;repaired[index*4+2]=color.Red;repaired[index*4+3]=255;}
    }

    private bool[] StructuralBorders(PixelRect bounds,int thickness,bool dark)
    {
        using var bitmap=LocalTextDetector.ToSkia(source);
        using var bgra=Mat.FromPixelData(source.Height,source.Width,MatType.CV_8UC4,bitmap.GetPixels(),bitmap.RowBytes);
        using var gray=new Mat();using var ink=new Mat();using var h=new Mat();using var v=new Mat();using var rules=new Mat();
        Cv2.CvtColor(bgra,gray,ColorConversionCodes.BGRA2GRAY);Cv2.Threshold(gray,ink,dark?180:100,255,dark?ThresholdTypes.Binary:ThresholdTypes.BinaryInv);
        using var kh=Cv2.GetStructuringElement(MorphShapes.Rect,new OpenCvSharp.Size(Math.Max(120,thickness*6),1));
        using var kv=Cv2.GetStructuringElement(MorphShapes.Rect,new OpenCvSharp.Size(1,Math.Max(120,thickness*6)));
        Cv2.MorphologyEx(ink,h,MorphTypes.Open,kh);Cv2.MorphologyEx(ink,v,MorphTypes.Open,kv);Cv2.BitwiseOr(h,v,rules);
        var result=new bool[bounds.Width*bounds.Height];
        for(var y=0;y<bounds.Height;y++)for(var x=0;x<bounds.Width;x++)result[y*bounds.Width+x]=!dark&&rules.At<byte>(y+bounds.Y,x+bounds.X)!=0;
        return result;
    }

    private InPlacePatch PrepareBalloon(TranslatedBlock block,BalloonInterior balloon)
    {
        var bounds=balloon.Bounds;
        using var bitmap=new SKBitmap(bounds.Width,bounds.Height,SKColorType.Bgra8888,SKAlphaType.Premul);
        using var canvas=new SKCanvas(bitmap);canvas.Clear(SKColors.Transparent);
        using var path=new SKPath();path.MoveTo(balloon.Polygon[0].X-bounds.X,balloon.Polygon[0].Y-bounds.Y);
        foreach(var point in balloon.Polygon.Skip(1))path.LineTo(point.X-bounds.X,point.Y-bounds.Y);path.Close();
        using var paint=new SKPaint{Color=balloon.Background,IsAntialias=true};canvas.DrawPath(path,paint);
        var pixels=new byte[bitmap.Width*bitmap.Height*4];Marshal.Copy(bitmap.GetPixels(),pixels,0,pixels.Length);
        var size=Math.Max(EstimateGlyphSize(block.Bounds),Math.Min(balloon.Layout.Width,balloon.Layout.Height)/4);
        var dark=Luma(balloon.Background)<128;
        var layout=dark&&block.PreciseWords?block.Bounds.Intersect(bounds):balloon.Layout;
        var palette=LocalInkColorSampler.Sample(source,block.TextBounds.Count>0?block.TextBounds:[block.Bounds],balloon.Background,size,segmentation);
        return new(bounds,new(bounds.Width,bounds.Height,bounds.Width*4,pixels),palette.Fill,
            Math.Clamp(size*.95f,14,96),balloon.Layout.Height>balloon.Layout.Width*.55&&block.IsVertical,"balloon",bounds.Width*bounds.Height)
            {LayoutBounds=layout,Transparent=true,ClipPolygon=balloon.Polygon.Select(p=>new PixelPoint(p.X,p.Y)).ToArray(),InkPalette=palette,Style=new(Outline:palette.OutlineFraction,Hollow:palette.Hollow)};
    }

    private int EstimateGlyphSize(PixelRect rectangle)
    {
        rectangle = rectangle.Intersect(new(0, 0, source.Width, source.Height));
        if (!rectangle.IsUsable || (long)rectangle.Width * rectangle.Height > 3_000_000) return 24;
        var pixels = new byte[rectangle.Width * rectangle.Height];
        for (var y = 0; y < rectangle.Height; y++)
            for (var x = 0; x < rectangle.Width; x++)
            {
                var offset = (rectangle.Y + y) * source.Stride + (rectangle.X + x) * 4;
                var value = source.Pixels.Span;
                pixels[y * rectangle.Width + x] = (byte)((value[offset + 2] * 299 + value[offset + 1] * 587 + value[offset] * 114) / 1000);
            }
        var background = pixels.Where((_, index) => index % 17 == 0).Order().ElementAt((pixels.Length + 16) / 17 / 2);
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var gray = Mat.FromPixelData(rectangle.Height, rectangle.Width, MatType.CV_8UC1, pin.AddrOfPinnedObject());
            using var ink = new Mat(); using var labels = new Mat(); using var statistics = new Mat(); using var centers = new Mat();
            Cv2.Threshold(gray, ink, background < 128 ? 150 : 160, 255, background < 128 ? ThresholdTypes.Binary : ThresholdTypes.BinaryInv);
            var count = Cv2.ConnectedComponentsWithStats(ink, labels, statistics, centers);
            var sizes = new List<int>();
            for (var label = 1; label < count; label++)
            {
                var w = statistics.At<int>(label, 2); var h = statistics.At<int>(label, 3); var area = statistics.At<int>(label, 4);
                var size = Math.Max(w, h);
                if (w >= 2 && h >= 4 && area >= 8 && size < Math.Min(rectangle.Width, rectangle.Height) * 1.5 && size <= 128) sizes.Add(size);
            }
            sizes.Sort();
            return sizes.Count > 0 ? Math.Clamp(sizes[(int)((sizes.Count - 1) * .7)], 12, 96)
                : Math.Clamp(Math.Min(rectangle.Width, rectangle.Height) / 4, 12, 48);
        }
        finally { pin.Free(); }
    }

    private static SKColor WordBackground(byte[] pixels, PixelRect patch, IReadOnlyList<PixelRect> words, List<SKColor> ring)
    {
        var samples = new List<SKColor>(); var counts = new int[512];
        foreach (var word in words)
        {
            var local = word.Intersect(patch).Translate(new(-patch.X, -patch.Y));
            for (var y = local.Y; y < local.Bottom; y += Math.Max(1, local.Height / 80))
                for (var x = local.X; x < local.Right; x += Math.Max(1, local.Width / 80))
                {
                    var color = Color(pixels, (y * patch.Width + x) * 4);
                    samples.Add(color); counts[(color.Red >> 5) * 64 + (color.Green >> 5) * 8 + (color.Blue >> 5)]++;
                }
        }
        if (samples.Count == 0) return Median(ring);
        var bucket = Array.IndexOf(counts, counts.Max());
        var dominant = samples.Where(color => (color.Red >> 5) * 64 + (color.Green >> 5) * 8 + (color.Blue >> 5) == bucket).ToList();
        return dominant.Count >= samples.Count * .3 ? Median(dominant) : Median(ring);
    }
    private static bool TryWordEdgeBackground(byte[] pixels,PixelRect patch,IReadOnlyList<PixelRect> words,out SKColor background,out bool flat)
    {
        var samples=new List<SKColor>();var counts=new int[512];background=default;flat=false;
        void Add(int gx,int gy)
        {
            if(gx<patch.X||gx>=patch.Right||gy<patch.Y||gy>=patch.Bottom)return;
            var color=Color(pixels,((gy-patch.Y)*patch.Width+gx-patch.X)*4);
            samples.Add(color);counts[(color.Red>>5)*64+(color.Green>>5)*8+(color.Blue>>5)]++;
        }
        foreach(var word in words)
        {
            for(var x=word.X;x<word.Right;x+=Math.Max(1,word.Width/100))for(var offset=1;offset<=3;offset++)
            {Add(x,word.Y-offset);Add(x,word.Bottom+offset-1);}
            for(var y=word.Y;y<word.Bottom;y+=Math.Max(1,word.Height/100))for(var offset=1;offset<=3;offset++)
            {Add(word.X-offset,y);Add(word.Right+offset-1,y);}
        }
        if(samples.Count<24)return false;
        var bucket=Array.IndexOf(counts,counts.Max());
        if(counts[bucket]<samples.Count*.65)return false;
        var dominant=samples.Where(color=>(color.Red>>5)*64+(color.Green>>5)*8+(color.Blue>>5)==bucket).ToList();
        var candidate=Median(dominant);var inside=0;var matching=0;
        foreach(var word in words)
        {
            var local=word.Intersect(patch).Translate(new(-patch.X,-patch.Y));
            for(var y=local.Y;y<local.Bottom;y+=Math.Max(1,local.Height/50))for(var x=local.X;x<local.Right;x+=Math.Max(1,local.Width/50))
            {inside++;if(Distance(Color(pixels,(y*patch.Width+x)*4),candidate)<28)matching++;}
        }
        // A rim lying in neighbouring artwork is not the glyph background.
        if(inside==0||matching<inside*.12)return false;
        background=candidate;flat=dominant.Count>=samples.Count*.90&&samples.Count(color=>Distance(color,candidate)<=12)>=samples.Count*.90;return true;
    }

    private bool[] ProtectedArtwork(PixelRect patch, SKColor background, int thickness, bool darkBackground)
    {
        var margin = Math.Clamp(thickness * 2, 20, 160);
        var context = new PixelRect(patch.X - margin, patch.Y - margin, patch.Width + margin * 2, patch.Height + margin * 2)
            .Intersect(new(0, 0, source.Width, source.Height));
        var ink = new byte[context.Width * context.Height];
        for (var y = 0; y < context.Height; y++)
            for (var x = 0; x < context.Width; x++)
            {
                var offset = (context.Y + y) * source.Stride + (context.X + x) * 4;
                var bytes = source.Pixels.Span;
                var color = new SKColor(bytes[offset + 2], bytes[offset + 1], bytes[offset]);
                var contrast = Luma(color) - Luma(background);
                if (darkBackground ? contrast >= 45 : contrast <= -45) ink[y * context.Width + x] = 255;
            }
        var pin = GCHandle.Alloc(ink, GCHandleType.Pinned);
        try
        {
            using var input = Mat.FromPixelData(context.Height, context.Width, MatType.CV_8UC1, pin.AddrOfPinnedObject());
            using var horizontal = new Mat(); using var vertical = new Mat(); using var rules = new Mat();
            using var horizontalKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(Math.Max(30, thickness * 3), 1));
            using var verticalKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(1, Math.Max(40, thickness * 2)));
            Cv2.MorphologyEx(input, horizontal, MorphTypes.Open, horizontalKernel);
            Cv2.MorphologyEx(input, vertical, MorphTypes.Open, verticalKernel);
            Cv2.BitwiseOr(horizontal, vertical, rules);
            using (var curves = Mat.Zeros(context.Height, context.Width, MatType.CV_8UC1).ToMat())
            {
                Cv2.FindContours(input, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
                for (var index = 0; index < contours.Length; index++)
                    if (Math.Abs(Cv2.ContourArea(contours[index])) > thickness * thickness * 4)
                    {
                        var rect = Cv2.BoundingRect(contours[index]);
                        if (Math.Abs(Cv2.ContourArea(contours[index])) > (long)rect.Width * rect.Height * .85)
                            Cv2.Rectangle(curves, rect, Scalar.White, 2);
                        else Cv2.DrawContours(curves, contours, index, Scalar.White, 2);
                    }
                Cv2.BitwiseOr(rules, curves, rules);
            }
            // Disconnect text that happens to touch a ruled caption border, then classify
            // the remaining connected curves/artwork. Do not protect the attached glyph too.
            Cv2.Subtract(input, rules, input);
            using var labels = new Mat(); using var stats = new Mat(); using var centers = new Mat();
            var count = Cv2.ConnectedComponentsWithStats(input, labels, stats, centers);
            var protectedLabels = new HashSet<int>();
            for (var label = 1; label < count; label++)
            {
                var x = stats.At<int>(label, 0); var y = stats.At<int>(label, 1);
                var w = stats.At<int>(label, 2); var h = stats.At<int>(label, 3);
                var area = stats.At<int>(label, 4);
                var edge = x == 0 || y == 0 || x + w == context.Width || y + h == context.Height;
                if (w > thickness * 1.5 && h > thickness * 1.5 || edge && w > thickness * .8 && h > thickness * .8
                    || w <= 3 && h > thickness * 2 || h <= 3 && w > thickness * 3) protectedLabels.Add(label);
            }
            var indices = new int[ink.Length]; Marshal.Copy(labels.Data, indices, 0, indices.Length);
            var rulePixels = new byte[ink.Length]; Marshal.Copy(rules.Data, rulePixels, 0, rulePixels.Length);
            var result = new bool[patch.Width * patch.Height];
            for (var y = 0; y < patch.Height; y++)
                for (var x = 0; x < patch.Width; x++)
                {
                    var index = (patch.Y - context.Y + y) * context.Width + patch.X - context.X + x;
                    result[y * patch.Width + x] = rulePixels[index] != 0 || protectedLabels.Contains(indices[index]);
                }
            return result;
        }
        finally { pin.Free(); }
    }

    public CapturedBitmap Render(InPlacePatch patch, TranslatedBlock block)
    {
        using var bitmap = LocalTextDetector.ToSkia(patch.Background,patch.Transparent);
        using var canvas = new SKCanvas(bitmap);
        if(patch.ClipPolygon.Count>2)
        {
            using var clip=new SKPath();var first=patch.ClipPolygon[0];clip.MoveTo(first.X-patch.Bounds.X,first.Y-patch.Bounds.Y);
            foreach(var point in patch.ClipPolygon.Skip(1))clip.LineTo(point.X-patch.Bounds.X,point.Y-patch.Bounds.Y);clip.Close();canvas.ClipPath(clip,SKClipOperation.Intersect,true);
        }
        using var font = new SKFont(ComicFontCatalog.Get(patch.Style), patch.FontSize) {SkewX=patch.Style.Skew,
            Embolden=patch.Style.Bold&&patch.Style.Family is ComicFontFamily.Handwriting or ComicFontFamily.Brush or ComicFontFamily.Display};
        using var paint = new SKPaint { Color = patch.Style.FillColor??(patch.Style.Hollow?patch.InkPalette?.Background??SKColors.White:patch.Foreground), IsAntialias = true };
        var vertical=patch.Style.Vertical??patch.IsVertical;
        var inset = Math.Clamp(patch.FontSize / 10, 2, 5);
        var layout = patch.LayoutBounds.IsUsable ? patch.LayoutBounds.Translate(new(-patch.Bounds.X, -patch.Bounds.Y))
            : new PixelRect(0, 0, bitmap.Width, bitmap.Height);
        canvas.ClipRect(new SKRect(layout.X, layout.Y, layout.Right, layout.Bottom));
        canvas.Translate(layout.X, layout.Y);
        if(patch.Style.Angle!=0){canvas.Translate(layout.Width/2f,layout.Height/2f);canvas.RotateDegrees(patch.Style.Angle);canvas.Translate(-layout.Width/2f,-layout.Height/2f);}
        var width = Math.Max(1, layout.Width - inset * 2); var height = Math.Max(1, layout.Height - inset * 2);
        while (font.Size > 8 && !FitsStyled(block.Translation, font, vertical, width, height,patch.Style.Angle)) font.Size *= .94f;
        if(block.IsComplete && block.HasVisionPlacement) {var cap=Math.Min(patch.FontSize*(patch.RepairMethod=="balloon"?1.6f:1.08f),Math.Min(width,height)*.72f);
            using var candidate=new SKFont(font.Typeface,font.Size);
            while(font.Size*1.04f<=cap) {candidate.Size=font.Size*1.04f;if(!FitsStyled(block.Translation,candidate,vertical,width,height,patch.Style.Angle))break;font.Size=candidate.Size;} }
        var centered = patch.Style.Angle!=0||patch.RepairMethod=="balloon" || !block.PreciseWords&&(block.HasVisionPlacement||block.IsComplete);
        if(patch.Style.Outline>0) {using var outline=new SKPaint{IsAntialias=true,Color=patch.InkPalette?.Outline??patch.Style.OutlineColor
                ??(patch.Style.Hollow?patch.Foreground:Luma(paint.Color)<128?SKColors.White:SKColors.Black),Style=SKPaintStyle.Stroke,
                StrokeWidth=Math.Max(2,font.Size*patch.Style.Outline*2),StrokeJoin=SKStrokeJoin.Round};
            if(vertical)DrawVertical(canvas,block.Translation,font,outline,inset,width,height,centered,patch.Style);else DrawHorizontal(canvas,block.Translation,font,outline,inset,width,height,centered,patch.Style);}
        if (vertical) DrawVertical(canvas, block.Translation, font, paint, inset, width, height, centered,patch.Style);
        else DrawHorizontal(canvas, block.Translation, font, paint, inset, width, height, centered,patch.Style);
        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
        Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        return new(bitmap.Width, bitmap.Height, bitmap.Width * 4, pixels);
    }

    public static bool Fits(string text, SKFont font, bool vertical, float width, float height) => vertical
        ? text.EnumerateRunes().Count(rune => !Rune.IsWhiteSpace(rune)) <= Math.Max(1, (int)(width / (font.Size * 1.2))) * Math.Max(1, (int)(height / (font.Size * 1.08)))
        : Wrap(text, font, width).Count * font.Size * 1.18 <= height;
    public static bool FitsStyled(string text,SKFont font,bool vertical,float width,float height,float angle)
    {
        if(!Fits(text,font,vertical,width,height))return false;if(angle==0)return true;
        double textWidth,textHeight;
        if(vertical){var count=text.EnumerateRunes().Count(r=>!Rune.IsWhiteSpace(r));var rows=Math.Max(1,(int)(height/(font.Size*1.08)));
            var columns=(count+rows-1)/rows;textWidth=columns*font.Size*1.2;textHeight=Math.Min(count,rows)*font.Size*1.08;}
        else{var lines=Wrap(text,font,width);textWidth=lines.Max(line=>font.MeasureText(line));textHeight=lines.Count*font.Size*1.18;}
        var radians=Math.Abs(angle)*Math.PI/180;var cos=Math.Cos(radians);var sin=Math.Sin(radians);
        return textWidth*cos+textHeight*sin<=width&&textWidth*sin+textHeight*cos<=height;
    }

    private static void DrawHorizontal(SKCanvas canvas, string text, SKFont font, SKPaint paint, float inset, float width, float height, bool complete,ComicTextStyle style)
    {
        var lines = Wrap(text, font, width);
        var metrics = font.Metrics;
        var lineHeight = font.Size * 1.18f;
        var top = inset + (complete ? Math.Max(0, (height - lines.Count * lineHeight) / 2) : 0);
        for (var index = 0; index < lines.Count; index++)
        {
            var x = inset + (complete ? Math.Max(0, (width - font.MeasureText(lines[index])) / 2) : 0);
            font.MeasureText(lines[index],out var ink);
            DrawGlyphRun(canvas,lines[index],x,top+index*lineHeight+(lineHeight-ink.Height)/2-ink.Top,font,paint,style);
        }
    }

    private static void DrawVertical(SKCanvas canvas, string text, SKFont font, SKPaint paint, float inset, float width, float height, bool complete,ComicTextStyle style)
    {
        var glyphs = text.EnumerateRunes().Where(rune => !Rune.IsWhiteSpace(rune)).ToArray();
        var pitch = font.Size * 1.08f; var columnWidth = font.Size * 1.2f;
        var rows = Math.Max(1, (int)(height / pitch));
        var columns = (glyphs.Length + rows - 1) / rows;
        if(complete&&columns>0)rows=(glyphs.Length+columns-1)/columns;
        var right = inset + width - (complete ? Math.Max(0, (width - columns * columnWidth) / 2) : 0);
        for (var index = 0; index < glyphs.Length; index++)
        {
            var glyph = VerticalGlyph(glyphs[index].ToString());
            var column = index / rows; var row = index % rows;
            var usedRows = Math.Min(rows, glyphs.Length - column * rows);
            var y = inset + row * pitch + (complete ? Math.Max(0, (height - usedRows * pitch) / 2) : 0);
            font.MeasureText(glyph, out var bounds);
            DrawGlyphRun(canvas,glyph, right - (column + .5f) * columnWidth - bounds.MidX, y + (pitch - bounds.Height) / 2 - bounds.Top, font, paint,style);
        }
    }
    private static void DrawGlyphRun(SKCanvas canvas,string text,float x,float y,SKFont font,SKPaint paint,ComicTextStyle style)
    {
        foreach(var rune in text.EnumerateRunes()) {var glyph=rune.ToString();var face=ComicFontCatalog.ForGlyph(style,glyph);
            using var selected=new SKFont(face,font.Size){SkewX=font.SkewX,Embolden=font.Embolden};canvas.DrawText(glyph,x,y,selected,paint);x+=selected.MeasureText(glyph);}
    }

    private static List<string> Wrap(string text, SKFont font, float width)
    {
        const string closing = "，。！？：；、）》」』】….,!?;:";
        var result = new List<string>(); var current = new StringBuilder();
        foreach (var atom in ChineseLineTokens.Split(text))
        {
            var glyph = atom;
            if (glyph == "\r") continue;
            if (glyph == "\n") { result.Add(current.ToString()); current.Clear(); continue; }
            if(font.MeasureText(glyph)>width)
            {
                foreach(var piece in glyph.EnumerateRunes())
                {var value=piece.ToString();if(current.Length>0&&font.MeasureText(current.ToString()+value)>width){result.Add(current.ToString());current.Clear();}current.Append(value);}
                continue;
            }
            if (current.Length > 0 && font.MeasureText(current.ToString() + glyph) > width)
            {
                var previous = current.ToString().EnumerateRunes().Last().ToString();
                if (closing.Contains(glyph, StringComparison.Ordinal) || "（《「『【(\"".Contains(previous, StringComparison.Ordinal))
                {
                    current.Length -= previous.Length;
                    if (current.Length > 0) result.Add(current.ToString().TrimEnd());
                    current.Clear(); current.Append(previous);
                }
                else { result.Add(current.ToString().TrimEnd()); current.Clear(); }
            }
            if (current.Length > 0 || !string.IsNullOrWhiteSpace(glyph)) current.Append(glyph);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    private static string VerticalGlyph(string glyph) => glyph switch
    { "，" => "︐", "。" => "︒", "！" => "︕", "？" => "︖", "：" => "︓", "；" => "︔", "「" => "﹁", "」" => "﹂", "『" => "﹃", "』" => "﹄", "（" => "︵", "）" => "︶", "—" => "︱", _ => glyph };
    private static SKColor Median(List<SKColor> colors) => new(colors.Select(color => color.Red).Order().ElementAt(colors.Count / 2),
        colors.Select(color => color.Green).Order().ElementAt(colors.Count / 2), colors.Select(color => color.Blue).Order().ElementAt(colors.Count / 2));
    private static SKColor Color(byte[] pixels, int index) => new(pixels[index + 2], pixels[index + 1], pixels[index], pixels[index + 3]);
    private static int Luma(SKColor color) => (color.Red * 299 + color.Green * 587 + color.Blue * 114) / 1000;
    private static int Distance(SKColor left, SKColor right) => Math.Max(Math.Max(Math.Abs(left.Red - right.Red), Math.Abs(left.Green - right.Green)), Math.Abs(left.Blue - right.Blue));
}
