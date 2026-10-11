using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using OpenCvSharp;
using ScreenTranslator.Core.Models;
using SkiaSharp;

namespace ScreenTranslator.App.Services.Overlays;

public enum ComicFontFamily { Sans, Serif, Handwriting, Brush, Display }
public sealed record ComicTextStyle(ComicFontFamily Family=ComicFontFamily.Sans,bool Bold=false,float Angle=0,float Skew=0,float Outline=0,bool Hollow=false)
{
    public static ComicTextStyle Plain {get;}=new();
    public SKColor? FillColor {get;init;}
    public SKColor? OutlineColor {get;init;}
    public bool? Vertical {get;init;}
}

public static class ComicFontCatalog
{
    private static readonly ConcurrentDictionary<string,Lazy<SKTypeface>> Faces=new();
    public static SKTypeface Get(ComicTextStyle style)
    {
        var file=style.Family switch { ComicFontFamily.Serif=>style.Bold?"NotoSerifCJKsc-Bold.otf":"NotoSerifCJKsc-Regular.otf",
            ComicFontFamily.Handwriting=>"LXGWWenKai-Regular.ttf",ComicFontFamily.Brush=>"MaShanZheng-Regular.ttf",
            ComicFontFamily.Display=>"ZCOOLKuaiLe-Regular.ttf",_=>style.Bold?"NotoSansCJKsc-Bold.otf":"NotoSansCJKsc-Regular.otf" };
        return Faces.GetOrAdd(file,name=>new(()=>File.Exists(Path.Combine(AppContext.BaseDirectory,"fonts",name))
            ?SKTypeface.FromFile(Path.Combine(AppContext.BaseDirectory,"fonts",name)):SKTypeface.FromFamilyName("Microsoft YaHei"))).Value;
    }
    public static SKTypeface ForGlyph(ComicTextStyle style,string glyph)
    {var selected=Get(style);return selected.GetGlyphs(glyph).All(id=>id!=0)?selected:Get(new(Bold:style.Bold));}
    public static void Warmup() {foreach(var family in Enum.GetValues<ComicFontFamily>()) {_=Get(new(family));_=Get(new(family,true));}}
}

/// <summary>Small CPU feature matcher. Fonts load once; no GPU, network request or language generation.</summary>
public static class ComicStyleAnalyzer
{
    public static ComicTextStyle Analyze(CapturedBitmap source,InPlacePatch patch,TranslatedBlock block)
    {
        var region=block.Bounds.Intersect(new(0,0,source.Width,source.Height));
        if(!region.IsUsable || region.Width*(long)region.Height>1_000_000)return ApplyAdvice(ComicTextStyle.Plain,source,patch,block);
        var pixels=new byte[region.Width*region.Height];var foreground=patch.Foreground.Red*.299+patch.Foreground.Green*.587+patch.Foreground.Blue*.114;
        for(var y=0;y<region.Height;y++) for(var x=0;x<region.Width;x++)
        {var p=(region.Y+y)*source.Stride+(region.X+x)*4;var v=source.Pixels.Span;var l=(v[p+2]*299+v[p+1]*587+v[p]*114)/1000;
            var ink=patch.InkPalette is {Confidence:>=.5f} palette
                ?LocalInkColorSampler.Distance(new(v[p+2],v[p+1],v[p]),palette.Background)>=24
                    &&(LocalInkColorSampler.Distance(new(v[p+2],v[p+1],v[p]),palette.Fill)<=55
                        ||palette.Outline is {} edge&&LocalInkColorSampler.Distance(new(v[p+2],v[p+1],v[p]),edge)<=40)
                :foreground<128?l<150:l>180;
            pixels[y*region.Width+x]=ink?(byte)255:(byte)0;}
        var pin=GCHandle.Alloc(pixels,GCHandleType.Pinned);
        try
        {
            using var mask=Mat.FromPixelData(region.Height,region.Width,MatType.CV_8UC1,pin.AddrOfPinnedObject());
            using var distance=new Mat();Cv2.DistanceTransform(mask,distance,DistanceTypes.L2,DistanceTransformMasks.Mask3);
            double stroke=0,variance=0;var n=0;
            for(var y=0;y<mask.Height;y++)for(var x=0;x<mask.Width;x++)if(pixels[y*mask.Width+x]!=0)
            {var d=distance.At<float>(y,x);stroke+=d;variance+=d*d;n++;}
            if(n<10)return ApplyAdvice(ComicTextStyle.Plain,source,patch,block);
            stroke/=n;var cv=Math.Sqrt(Math.Max(0,variance/n-stroke*stroke))/Math.Max(1,stroke);
            var relative=stroke/Math.Max(12,patch.FontSize);
            var bold=relative>.085;
            var sizes=block.TextBounds.Where(b=>b.Width>3&&b.Height>3).Select(b=>Math.Max(b.Width,b.Height)).ToArray();
            var irregular=sizes.Length>3&&sizes.Max()>Math.Max(1,sizes.Order().ElementAt(sizes.Length/2))*1.9;
            var family=irregular?ComicFontFamily.Handwriting:cv>1.1?ComicFontFamily.Serif:ComicFontFamily.Sans;
            var angle=EstimateWordBaseline(block);
            if(angle==0)angle=EstimateAngle(mask,region);
            var shortEffect=block.Translation.EnumerateRunes().Count()<=6&&patch.FontSize>=32&&bold;
            if(shortEffect)family=Math.Abs(angle)>5?ComicFontFamily.Brush:ComicFontFamily.Display;
            // Slant from weighted glyph covariance, after removing the line's baseline rotation.
            using var straight=new Mat();
            if(angle!=0){using var rotation=Cv2.GetRotationMatrix2D(new Point2f(mask.Width/2f,mask.Height/2f),angle,1);
                Cv2.WarpAffine(mask,straight,rotation,new(mask.Width,mask.Height),InterpolationFlags.Nearest,BorderTypes.Constant,Scalar.Black);}
            var skew=EstimateSlant(angle==0?mask:straight);
            Cv2.FindContours(mask,out OpenCvSharp.Point[][] contours,out var hierarchy,RetrievalModes.CComp,ContourApproximationModes.ApproxSimple);
            double exterior=0,holes=0;
            for(var i=0;i<contours.Length;i++){var area=Math.Abs(Cv2.ContourArea(contours[i]));if(hierarchy[i].Parent<0)exterior+=area;else holes+=area;}
            var hollow=patch.InkPalette?.Hollow==true||(shortEffect||block.TypographyHint?.Hollow==true)&&exterior>100&&holes/exterior>.55;
            return ApplyAdvice(new(family,bold,angle,Math.Abs(skew)>.12f?Math.Clamp(skew,-.25f,.25f):0,
                Math.Max(patch.InkPalette?.OutlineFraction??0,hollow?patch.InkPalette?.OutlineFraction??.085f:0),hollow),source,patch,block);
        } finally{pin.Free();}
    }
    private static ComicTextStyle ApplyAdvice(ComicTextStyle local,CapturedBitmap source,InPlacePatch patch,TranslatedBlock block)
    {
        var style=local with {Outline=Math.Max(local.Outline,patch.InkPalette?.OutlineFraction??0)};
        if(block.TypographyHint is not {} hint)return style;
        var family=hint.Family switch
        {
            VisionFontFamily.Serif=>ComicFontFamily.Serif,
            VisionFontFamily.Handwriting=>ComicFontFamily.Handwriting,
            VisionFontFamily.Brush=>ComicFontFamily.Brush,
            VisionFontFamily.Display=>ComicFontFamily.Display,
            _=>ComicFontFamily.Sans,
        };
        var angle=float.IsFinite(hint.Angle)&&Math.Abs(hint.Angle)<=25&&Math.Abs(local.Angle)<3?hint.Angle:local.Angle;
        var skew=float.IsFinite(hint.Skew)&&Math.Abs(hint.Skew)<=.25?hint.Skew:local.Skew;
        var outline=patch.InkPalette?.Outline is not null&&float.IsFinite(hint.Outline)
            ?Math.Max(style.Outline,Math.Clamp(hint.Outline,0,.12f)):style.Outline;
        return style with {Family=family,Bold=hint.Bold,Angle=angle,Skew=skew,Outline=outline,Vertical=hint.Vertical,
            FillColor=ValidatedColour(hint.ColorHex,source,patch,block),
            OutlineColor=patch.InkPalette?.Outline??ValidatedColour(hint.OutlineColorHex,source,patch,block)};
    }
    private static SKColor? ValidatedColour(string? hex,CapturedBitmap source,InPlacePatch patch,TranslatedBlock block)
    {
        if(patch.InkPalette is {Confidence:>=.5f}||hex is null||hex.Length!=7||hex[0]!='#'
            ||!uint.TryParse(hex.AsSpan(1),System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out var rgb))return null;
        var color=new SKColor((byte)(rgb>>16),(byte)(rgb>>8),(byte)rgb);
        if(patch.InkPalette is {} sampled&&LocalInkColorSampler.Distance(color,sampled.Background)<28)return null;
        var region=block.Bounds.Intersect(new(0,0,source.Width,source.Height));var count=0;
        for(var y=region.Y;y<region.Bottom;y+=2)for(var x=region.X;x<region.Right;x+=2)
        {
            var p=y*source.Stride+x*4;var bytes=source.Pixels.Span;
            if(LocalInkColorSampler.Distance(color,new(bytes[p+2],bytes[p+1],bytes[p]))<=24&&++count>=12)return color;
        }
        return null;
    }
    public static float EstimateWordBaseline(TranslatedBlock block)
    {
        if(!block.PreciseWords||block.IsVertical||block.TextBounds.Count<2)return 0;
        var words=block.TextBounds.Where(w=>w.IsUsable).ToArray();var votes=new List<(float Angle,float Weight)>();
        foreach(var a in words)
        {
            var neighbours=words.Where(b=>b.Center.X>a.Center.X+Math.Min(a.Height,b.Height)*.5)
                .Select(b=>(B:b,Dx:b.Center.X-a.Center.X,Dy:b.Center.Y-a.Center.Y))
                .Where(v=>Math.Abs(v.Dy)<=v.Dx*.47+3&&v.B.X-a.Right<=Math.Max(a.Height,v.B.Height)*2)
                .OrderBy(v=>v.Dx).ToArray();
            if(neighbours.Length==0)continue;var nearest=neighbours[0];
            var angle=(float)(Math.Atan2(nearest.Dy,nearest.Dx)*180/Math.PI);
            if(Math.Abs(angle)<=25)votes.Add((angle,Math.Min(120,nearest.Dx)));
        }
        if(votes.Count==0)return 0;
        var best=votes.MaxBy(v=>votes.Where(w=>Math.Abs(v.Angle-w.Angle)<4).Sum(w=>w.Weight));
        var cluster=votes.Where(v=>Math.Abs(v.Angle-best.Angle)<4).ToArray();
        var result=cluster.Sum(v=>v.Angle*v.Weight)/cluster.Sum(v=>v.Weight);
        return Math.Abs(result)>=3?result:0;
    }
    private static float EstimateAngle(Mat mask,PixelRect region)
    {
        if(region.Width<region.Height*3.5 && region.Height<region.Width*3.5)return 0;
        using var labels=new Mat();using var stats=new Mat();using var centers=new Mat();
        var count=Cv2.ConnectedComponentsWithStats(mask,labels,stats,centers);
        var points=new List<Point2f>();
        for(var i=1;i<count;i++) {var w=stats.At<int>(i,2);var h=stats.At<int>(i,3);
            if(w>=3&&h>=4&&w<region.Width*.6&&h<region.Height*.6)points.Add(new((float)centers.At<double>(i,0),(float)centers.At<double>(i,1)));}
        if(points.Count<4)return 0;
        var line=Cv2.FitLine(points,DistanceTypes.Huber,0,.01,.01);
        var residual=points.Select(p=>Math.Abs((p.X-line.X1)*line.Vy-(p.Y-line.Y1)*line.Vx)).Order().ElementAt(points.Count*3/4);
        if(residual>Math.Min(region.Width,region.Height)*.3)return 0;
        var angle=(float)(Math.Atan2(line.Vy,line.Vx)*180/Math.PI);
        if(angle>45)angle-=90;if(angle<-45)angle+=90;
        return Math.Abs(angle)>=3&&Math.Abs(angle)<=18?angle:0;
    }
    private static float EstimateSlant(Mat mask)
    {
        using var labels=new Mat();using var stats=new Mat();using var centers=new Mat();
        var count=Cv2.ConnectedComponentsWithStats(mask,labels,stats,centers);var values=new List<double>();
        for(var id=1;id<count;id++)
        {
            var w=stats.At<int>(id,2);var h=stats.At<int>(id,3);var x=stats.At<int>(id,0);var y=stats.At<int>(id,1);
            if(w<4||h<12||w>h*1.4)continue;
            double cross=0,ys=0;var cx=centers.At<double>(id,0);var cy=centers.At<double>(id,1);
            for(var yy=y;yy<y+h;yy++)for(var xx=x;xx<x+w;xx++)if(labels.At<int>(yy,xx)==id){cross+=(xx-cx)*(yy-cy);ys+=(yy-cy)*(yy-cy);}
            if(ys>0)values.Add(cross/ys);
        }
        return values.Count>=4?(float)values.Order().ElementAt(values.Count/2):0;
    }
}
