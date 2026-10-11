using System.Runtime.InteropServices;
using OpenCvSharp;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Overlays;

/// <summary>Expands confident segmentation pixels to complete glyph components; never through a frame/art component.</summary>
public static class ComicInkExtractor
{
    public static IReadOnlyList<PixelRect> Glyphs(CapturedBitmap source,CapturedBitmap? mask,PixelRect region,IReadOnlyList<PixelRect>? trustedLines=null)
    {
        var margin=Math.Clamp(Math.Min(region.Width,region.Height)/4,10,60);
        var context=new PixelRect(region.X-margin,region.Y-margin,region.Width+margin*2,region.Height+margin*2).Intersect(new(0,0,source.Width,source.Height));
        if(!context.IsUsable || (long)context.Width*context.Height>3_000_000) return [];
        var light=new byte[context.Width*context.Height];var values=new List<byte>();
        for(var y=0;y<context.Height;y++) for(var x=0;x<context.Width;x++)
        {var p=(context.Y+y)*source.Stride+(context.X+x)*4;var v=source.Pixels.Span;var luminance=(byte)((v[p+2]*299+v[p+1]*587+v[p]*114)/1000);light[y*context.Width+x]=luminance;
            if(x%3==0&&y%3==0&&region.Intersect(new(context.X+x,context.Y+y,1,1)).IsUsable) values.Add(luminance);}
        var background=values.Count==0?255:values.Order().ElementAt(values.Count/2);
        for(var i=0;i<light.Length;i++) light[i]=(background>=200?light[i]<110:background<80?light[i]>170:Math.Abs(light[i]-background)>=65)?(byte)255:(byte)0;
        var pin=GCHandle.Alloc(light,GCHandleType.Pinned);
        try
        {
            using var ink=Mat.FromPixelData(context.Height,context.Width,MatType.CV_8UC1,pin.AddrOfPinnedObject());
            using var horizontal=new Mat();using var vertical=new Mat();using var rules=new Mat();
            using var kh=Cv2.GetStructuringElement(MorphShapes.Rect,new OpenCvSharp.Size(Math.Max(35,Math.Min(region.Width,region.Height)),1));
            using var kv=Cv2.GetStructuringElement(MorphShapes.Rect,new OpenCvSharp.Size(1,Math.Max(50,Math.Min(region.Width,region.Height)*2)));
            Cv2.MorphologyEx(ink,horizontal,MorphTypes.Open,kh);Cv2.MorphologyEx(ink,vertical,MorphTypes.Open,kv);Cv2.BitwiseOr(horizontal,vertical,rules);Cv2.Subtract(ink,rules,ink);
            using var labels=new Mat();using var stats=new Mat();using var centers=new Mat();
            var count=Cv2.ConnectedComponentsWithStats(ink,labels,stats,centers);
            var sums=new long[count]; var hits=new int[count];var maxima=new int[count];
            var indices=new int[light.Length];Marshal.Copy(labels.Data,indices,0,indices.Length);
            for(var y=0;y<context.Height;y++) for(var x=0;x<context.Width;x++)
            {var id=indices[y*context.Width+x]; if(id==0)continue;var score=mask is null?0:mask.Pixels.Span[(context.Y+y)*mask.Stride+(context.X+x)*4];sums[id]+=score; maxima[id]=Math.Max(maxima[id],score); hits[id]++;}
            var kept=new List<PixelRect>();
            for(var id=1;id<count;id++)
            {
                var x=stats.At<int>(id,0);var y=stats.At<int>(id,1);var w=stats.At<int>(id,2);var h=stats.At<int>(id,3);
                if(x==0||y==0||x+w==context.Width||y+h==context.Height||hits[id]<2)continue;
                var box=new PixelRect(context.X+x,context.Y+y,w,h);
                var trusted=trustedLines?.Any(line=>(mask is null||Math.Max(line.Width,line.Height)>=Math.Min(line.Width,line.Height)*2.5)
                    && line.Intersect(box).Width*(long)line.Intersect(box).Height>=box.Width*(long)box.Height*.8)==true;
                if(!trusted && (maxima[id]<180||sums[id]/(double)hits[id]<55))continue;
                var overlap=box.Intersect(region);
                if(overlap.Width*(long)overlap.Height<box.Width*(long)box.Height*.55)continue;
                if(w>region.Width*.8 && h>region.Height*.8 && w*h>100)continue;
                kept.Add(box);
            }
            return kept;
        } finally {pin.Free();}
    }
}
