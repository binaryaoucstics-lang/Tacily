using OpenCvSharp;
using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.Core.Models;
using SkiaSharp;

namespace ScreenTranslator.App.Services.Capture;

public sealed record BalloonInterior(PixelRect Bounds,OpenCvSharp.Point[] Polygon,SKColor Background,PixelRect Layout);

/// <summary>Closed light/dark balloon interiors. Page backgrounds and large panels are excluded.</summary>
public sealed class BalloonInteriorDetector(CapturedBitmap source)
{
    private readonly Lazy<IReadOnlyList<BalloonInterior>> _interiors=new(()=>Detect(source));
    public IReadOnlyList<BalloonInterior> Interiors=>_interiors.Value;
    public BalloonInterior? Find(PixelRect ink,IReadOnlyList<PixelRect>? words=null)
    {
        if(!ink.IsUsable)return null;
        if(ink.Width>=ink.Height*3 && FindRuledCaption(ink) is {} caption)return caption;
        return _interiors.Value.Where(b=>Cv2.PointPolygonTest(b.Polygon,new Point2f(ink.Center.X,ink.Center.Y),false)>=0
            && (b.Background.Red>=128||b.Bounds.Width*(long)b.Bounds.Height<=ink.Width*(long)ink.Height*8)
            && b.Bounds.Intersect(ink).Width*(long)b.Bounds.Intersect(ink).Height>=ink.Width*(long)ink.Height*.9
            && (words is {Count:>0}?words.All(word=>Coverage(b.Polygon,word)>=.92):Coverage(b.Polygon,ink)>=.92))
            .OrderBy(b=>b.Bounds.Width*(long)b.Bounds.Height).FirstOrDefault();
    }
    private static double Coverage(OpenCvSharp.Point[] polygon,PixelRect rectangle)
    {
        var inside=0;
        for(var y=0;y<9;y++)for(var x=0;x<9;x++)
            if(Cv2.PointPolygonTest(polygon,new Point2f(rectangle.X+(x+.5f)*rectangle.Width/9,rectangle.Y+(y+.5f)*rectangle.Height/9),false)>=0)inside++;
        return inside/81d;
    }
    private BalloonInterior? FindRuledCaption(PixelRect ink)
    {
        var context=new PixelRect(ink.X-16,ink.Y-18,ink.Width+32,ink.Height+36).Intersect(new(0,0,source.Width,source.Height));
        if(context.Width*(long)context.Height>200_000)return null;
        int Luma(int x,int y){var p=y*source.Stride+x*4;var data=source.Pixels.Span;return(data[p+2]*299+data[p+1]*587+data[p]*114)/1000;}
        var rows=new List<(int Y,int Left,int Right)>();
        for(var y=context.Y;y<context.Bottom;y++)
        {var start=-1;for(var x=context.X;x<=context.Right;x++){if(x<context.Right&&Luma(x,y)<100){if(start<0)start=x;}
            else if(start>=0){if(x-start>=ink.Width*.7)rows.Add((y,start,x));start=-1;}}}
        var upper=rows.Where(r=>r.Y<ink.Y).OrderByDescending(r=>r.Y).FirstOrDefault();
        var lower=rows.Where(r=>r.Y>=ink.Bottom).OrderBy(r=>r.Y).FirstOrDefault();
        if(upper.Right==0||lower.Right==0||lower.Y-upper.Y>Math.Min(80,ink.Height*2+12)||lower.Y-upper.Y<12)return null;
        var left=Math.Max(upper.Left,lower.Left)+3;var right=Math.Min(upper.Right,lower.Right)-3;
        var box=new PixelRect(left,upper.Y+3,right-left,lower.Y-upper.Y-5);
        if(!box.IsUsable||box.Intersect(ink).Width*(long)box.Intersect(ink).Height<ink.Width*(long)ink.Height*.9)return null;
        var sidePixels=0;
        for(var y=upper.Y+1;y<lower.Y;y++)
            if(Enumerable.Range(-3,7).Any(dx=>left+dx>=0&&Luma(left+dx,y)<100)
                &&Enumerable.Range(-3,7).Any(dx=>right+dx<source.Width&&Luma(right+dx,y)<100))sidePixels++;
        if(sidePixels<(lower.Y-upper.Y-1)*.7)return null;
        var whites=0;var total=0;for(var y=box.Y;y<box.Bottom;y+=2)for(var x=box.X;x<box.Right;x+=2){if(Luma(x,y)>210)whites++;total++;}
        if(whites<total*.55)return null;
        OpenCvSharp.Point[] polygon=[new(box.X,box.Y),new(box.Right-1,box.Y),new(box.Right-1,box.Bottom-1),new(box.X,box.Bottom-1)];
        return new(box,polygon,SKColors.White,box);
    }
    private static IReadOnlyList<BalloonInterior> Detect(CapturedBitmap source)
    {
        using var bitmap=LocalTextDetector.ToSkia(source);
        using var bgra=Mat.FromPixelData(source.Height,source.Width,MatType.CV_8UC4,bitmap.GetPixels(),bitmap.RowBytes);
        using var gray=new Mat();Cv2.CvtColor(bgra,gray,ColorConversionCodes.BGRA2GRAY);
        var results=new List<BalloonInterior>();
        foreach(var dark in new[]{false,true})
        {
            using var binary=new Mat();Cv2.Threshold(gray,binary,dark?35:225,255,dark?ThresholdTypes.BinaryInv:ThresholdTypes.Binary);
            using var closing=Cv2.GetStructuringElement(MorphShapes.Ellipse,new OpenCvSharp.Size(3,3));Cv2.MorphologyEx(binary,binary,MorphTypes.Close,closing);
            Cv2.FindContours(binary,out OpenCvSharp.Point[][] polygons,out var hierarchy,RetrievalModes.CComp,ContourApproximationModes.ApproxSimple);
            for(var index=0;index<polygons.Length;index++)
            {
                if(hierarchy[index].Parent>=0)continue;
                var polygon=polygons[index];
                var area=Math.Abs(Cv2.ContourArea(polygon));var box=Cv2.BoundingRect(polygon);
                if(area<500||area>source.Width*(long)source.Height*.07||box.Width<25||box.Height<25||area<box.Width*(long)box.Height*.45)continue;
                // A small rectangular panel is still a panel. Its whole contents
                // must never become an erase canvas just because text lies inside.
                if(!dark&&area>box.Width*(long)box.Height*.9)continue;
                if(box.X==0||box.Y==0||box.Right>=source.Width||box.Bottom>=source.Height)continue;
                var local=polygon.Select(p=>new OpenCvSharp.Point(p.X-box.X,p.Y-box.Y)).ToArray();
                using var interior=Mat.Zeros(box.Height,box.Width,MatType.CV_8UC1).ToMat();Cv2.FillPoly(interior,[local],Scalar.White);
                using var kernel=Cv2.GetStructuringElement(MorphShapes.Rect,new OpenCvSharp.Size(7,7));Cv2.Erode(interior,interior,kernel);
                var layout=LargestRectangle(interior).Translate(new(box.X,box.Y));
                if(!layout.IsUsable)continue;
                var levels=new List<byte>();for(var y=box.Y;y<box.Bottom;y+=3)for(var x=box.X;x<box.Right;x+=3)if(interior.At<byte>(y-box.Y,x-box.X)>0)levels.Add(gray.At<byte>(y,x));
                var tone=levels.Count==0?(byte)(dark?0:255):levels.Order().ElementAt(levels.Count/2);
                if(dark?tone>55:tone<220)continue;
                results.Add(new(new(box.X,box.Y,box.Width,box.Height),polygon,new(tone,tone,tone),layout));
            }
        }
        return results;
    }
    public static PixelRect LargestRectangle(Mat mask)
    {
        var heights=new int[mask.Width+1];var stack=new int[mask.Width+1];var best=default(PixelRect);
        for(var y=0;y<mask.Height;y++) {for(var x=0;x<mask.Width;x++)heights[x]=mask.At<byte>(y,x)==0?0:heights[x]+1;
            var top=-1;for(var x=0;x<=mask.Width;x++){while(top>=0&&heights[stack[top]]>heights[x]){var h=heights[stack[top--]];var left=top<0?0:stack[top]+1;
                if((x-left)*(long)h>best.Width*(long)best.Height)best=new(left,y+1-h,x-left,h);}stack[++top]=x;}}
        return best;
    }
}
