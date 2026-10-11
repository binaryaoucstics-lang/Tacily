using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;
using OpenCvSharp;
using ScreenTranslator.App.Services.Ocr;

namespace ScreenTranslator.App.Services.Capture;

/// <summary>Join words on one baseline, then aligned wrapped lines. Speaker changes with large indentation stay separate.</summary>
public static class NativeWordGrouper
{
    public static IReadOnlyList<TextRegionGroup> Group(IReadOnlyList<TextRegion> words,CapturedBitmap source)
    {
        var individual=words.Select(w=>new TextRegionGroup(w.Bounds,[w.Bounds],w.Bounds.Height>w.Bounds.Width*1.7)).ToArray();
        var containers=MergeDarkContainers(individual,source);
        var dark=containers.Where(g=>g.TextBounds.Count>1).ToArray();
        var included=dark.SelectMany(g=>g.TextBounds).ToHashSet();
        var remaining=words.Where(w=>!included.Contains(w.Bounds)).ToArray();
        var grouped=dark.Concat(remaining.Length==0?[]:GroupPlain(remaining,source)).ToArray();
        return ComicReadingOrder.Sort(Consolidate(grouped,source),ComicPanelDetector.Detect(source));
    }
    private static IReadOnlyList<TextRegionGroup> Consolidate(IReadOnlyList<TextRegionGroup> input,CapturedBitmap source)
    {
        var groups=input.ToList();
        int Light(int x,int y){var p=y*source.Stride+x*4;var b=source.Pixels.Span;return (b[p+2]*299+b[p+1]*587+b[p]*114)/1000;}
        bool SameDarkBaseline(TextRegionGroup a,TextRegionGroup b)
        {
            if(a.IsVertical||b.IsVertical)return false;
            foreach(var x in a.TextBounds)foreach(var y in b.TextBounds)
            {
                var dx=y.Center.X-x.Center.X;var dy=y.Center.Y-x.Center.Y;var glyph=Math.Min(x.Height,y.Height);
                var gap=Math.Max(x.X,y.X)-Math.Min(x.Right,y.Right);
                if(Math.Abs(dx)<glyph*.5||Math.Abs(dy)>Math.Abs(dx)*.47||gap>glyph*2)continue;
                var dark=0;
                for(var k=1;k<10;k++)
                {var px=(int)Math.Round(x.Center.X+dx*k/10d);var py=(int)Math.Round(x.Center.Y+dy*k/10d);
                    if(px>=0&&py>=0&&px<source.Width&&py<source.Height&&Light(px,py)<100)dark++;}
                if(dark>=6)return true;
            }
            return false;
        }
        for(var i=0;i<groups.Count;i++)for(var j=i+1;j<groups.Count;j++)
        {
            var a=groups[i];var b=groups[j];var overlap=a.CropBounds.Intersect(b.CropBounds);
            var contained=overlap.Width*(long)overlap.Height>=Math.Min(a.CropBounds.Width*(long)a.CropBounds.Height,b.CropBounds.Width*(long)b.CropBounds.Height)*.8;
            if(!contained&&!SameDarkBaseline(a,b))continue;
            groups[i]=a with{CropBounds=a.CropBounds.Union(b.CropBounds),TextBounds=a.TextBounds.Concat(b.TextBounds).Distinct().ToArray()};groups.RemoveAt(j);j=i;
        }
        return groups;
    }
    private static IReadOnlyList<TextRegionGroup> GroupPlain(IReadOnlyList<TextRegion> words,CapturedBitmap source)
    {
        var horizontal=words.Count(w=>w.Bounds.Width>w.Bounds.Height)*2>=words.Count;
        if(!horizontal)return GroupVertical(words,source);
        var boxes=words.Select(w=>w.Bounds).ToArray();var parents=Enumerable.Range(0,boxes.Length).ToArray();
        var panels=ComicPanelDetector.Detect(source);
        bool SamePanel(PixelRect a,PixelRect b)=>!panels.Any(panel=>Contains(panel,a.Center)!=Contains(panel,b.Center));
        int Root(int i){while(parents[i]!=i){parents[i]=parents[parents[i]];i=parents[i];}return i;}
        for(var i=0;i<boxes.Length;i++)for(var j=i+1;j<boxes.Length;j++)
        {
            var a=boxes[i];var b=boxes[j];
            if(Math.Abs(a.Center.Y-b.Center.Y)<=Math.Min(a.Height,b.Height)*.5
                && Math.Min(a.Bottom,b.Bottom)-Math.Max(a.Y,b.Y)>=Math.Min(a.Height,b.Height)*.5
                && Math.Max(a.X,b.X)-Math.Min(a.Right,b.Right)<=Math.Max(a.Height,b.Height)*1.25
                && Math.Max(a.Height,b.Height)<=Math.Min(a.Height,b.Height)*2
                && SamePanel(a,b))parents[Root(j)]=Root(i);
        }
        var rows=boxes.Select((box,index)=>(box,root:Root(index))).GroupBy(item=>item.root).Select(g=>g.Select(i=>i.box).ToList()).ToArray();
        var paragraphs=new List<List<List<PixelRect>>>();
        foreach(var row in rows.OrderBy(r=>r.Min(w=>w.Y)))
        {
            var box=Union(row);var paragraph=paragraphs.LastOrDefault(p=>
            {var last=Union(p[^1]);var gap=box.Y-last.Bottom;var size=Math.Min(last.Height,box.Height);
                var aligned=Math.Abs(last.X-box.X)<=Math.Max(12,size*.7)
                    ||Math.Abs(last.Right-box.Right)<=Math.Max(12,size*.8)
                    ||Math.Abs(last.Center.X-box.Center.X)<=size*.8&&Math.Max(last.Width,box.Width)<=Math.Min(last.Width,box.Width)*2.2;
                return gap>=-size*.65&&gap<=size*.75&&aligned
                    && SamePanel(last,box);});
            if(paragraph is null)paragraphs.Add([row]);else paragraph.Add(row);
        }
        var limits=new PixelRect(0,0,source.Width,source.Height);
        var groups=paragraphs.Select(p=>{var glyphs=p.SelectMany(r=>r).ToArray();var box=Union(glyphs);
            return new TextRegionGroup(new PixelRect(box.X-5,box.Y-5,box.Width+10,box.Height+10).Intersect(limits),glyphs,false);}).ToList();
        for(var i=0;i<groups.Count;i++)for(var j=i+1;j<groups.Count;j++)
        {
            var a=groups[i].CropBounds;var b=groups[j].CropBounds;
            var sharedGlyph=groups[i].TextBounds.Any(x=>groups[j].TextBounds.Any(y=>x.Intersect(y).Width*(long)x.Intersect(y).Height>=Math.Min(x.Width*(long)x.Height,y.Width*(long)y.Height)*.35));
            if(!SamePanel(a,b)||!sharedGlyph&&!Contains(a,b.Center)&&!Contains(b,a.Center))continue;
            var overlap=a.Intersect(b);if(overlap.Width*(long)overlap.Height<Math.Min(a.Width*(long)a.Height,b.Width*(long)b.Height)*.2)continue;
            groups[i]=groups[i] with{CropBounds=a.Union(b),TextBounds=groups[i].TextBounds.Concat(groups[j].TextBounds).Distinct().ToArray()};groups.RemoveAt(j);j=i;
        }
        for(var i=0;i<groups.Count;i++)for(var j=i+1;j<groups.Count;j++)
        {
            var a=groups[i].CropBounds;var b=groups[j].CropBounds;var overlap=a.Intersect(b);
            if(!SamePanel(a,b)||overlap.Width*(long)overlap.Height<Math.Min(a.Width*(long)a.Height,b.Width*(long)b.Height)*.12
                ||overlap.Width<Math.Min(a.Width,b.Width)*.6)continue;
            groups[i]=groups[i] with{CropBounds=a.Union(b),TextBounds=groups[i].TextBounds.Concat(groups[j].TextBounds).Distinct().ToArray()};groups.RemoveAt(j);j=i;
        }
        return ComicReadingOrder.Sort(MergeDarkContainers(groups,source),panels);
    }
    private static PixelRect Union(IEnumerable<PixelRect> boxes)=>boxes.Aggregate((a,b)=>a.Union(b));
    private static bool Contains(PixelRect box,PixelPoint p)=>p.X>=box.X&&p.X<box.Right&&p.Y>=box.Y&&p.Y<box.Bottom;

    private static IReadOnlyList<TextRegionGroup> GroupVertical(IReadOnlyList<TextRegion> words,CapturedBitmap source)
    {
        var boxes=words.Select(w=>w.Bounds).ToArray();var panels=ComicPanelDetector.Detect(source);
        bool SamePanel(PixelRect a,PixelRect b)=>!panels.Any(p=>Contains(p,a.Center)!=Contains(p,b.Center));
        var parents=Enumerable.Range(0,boxes.Length).ToArray();
        int Root(int i){while(parents[i]!=i){parents[i]=parents[parents[i]];i=parents[i];}return i;}
        // Single kana/punctuation detections are still part of their column. The
        // aspect ratio of a single glyph must not decide the page's direction.
        for(var i=0;i<boxes.Length;i++)for(var j=i+1;j<boxes.Length;j++)
        {
            var a=boxes[i];var b=boxes[j];var width=Math.Min(a.Width,b.Width);
            var gap=Math.Max(a.Y,b.Y)-Math.Min(a.Bottom,b.Bottom);
            if(SamePanel(a,b)&&Math.Abs(a.Center.X-b.Center.X)<=Math.Max(width*.4,8)
                &&gap<=Math.Max(width*.8,10)&&gap>=-Math.Max(a.Height,b.Height))parents[Root(j)]=Root(i);
        }
        var columns=boxes.Select((b,i)=>(b,root:Root(i))).GroupBy(v=>v.root).Select(g=>g.Select(v=>v.b).ToList()).ToList();
        for(var i=0;i<columns.Count;i++)for(var j=i+1;j<columns.Count;j++)
        {
            var a=Union(columns[i]);var b=Union(columns[j]);
            var overlap=Math.Min(a.Bottom,b.Bottom)-Math.Max(a.Y,b.Y);
            var gap=Math.Max(a.X,b.X)-Math.Min(a.Right,b.Right);
            var glyph=Math.Min(columns[i].Max(v=>v.Width),columns[j].Max(v=>v.Width));
            if(!SamePanel(a,b)||gap>glyph*.8||overlap<Math.Min(a.Height,b.Height)*.5)continue;
            columns[i].AddRange(columns[j]);columns.RemoveAt(j);j=i;
        }
        var limits=new PixelRect(0,0,source.Width,source.Height);
        return ComicReadingOrder.Sort(MergeDarkContainers(columns.Select(c=>{var box=Union(c);return new TextRegionGroup(
            new PixelRect(box.X-6,box.Y-6,box.Width+12,box.Height+12).Intersect(limits),c.ToArray(),true);}).ToArray(),source),panels);
    }

    private static IReadOnlyList<TextRegionGroup> MergeDarkContainers(IReadOnlyList<TextRegionGroup> input,CapturedBitmap source)
    {
        using var bitmap=LocalTextDetector.ToSkia(source);
        using var bgra=Mat.FromPixelData(source.Height,source.Width,MatType.CV_8UC4,bitmap.GetPixels(),bitmap.RowBytes);
        using var gray=new Mat();using var dark=new Mat();using var labels=new Mat();using var stats=new Mat();using var centers=new Mat();
        Cv2.CvtColor(bgra,gray,ColorConversionCodes.BGRA2GRAY);Cv2.Threshold(gray,dark,55,255,ThresholdTypes.BinaryInv);
        using var core=Cv2.GetStructuringElement(MorphShapes.Ellipse,new OpenCvSharp.Size(5,5));Cv2.Erode(dark,dark,core);
        Cv2.ConnectedComponentsWithStats(dark,labels,stats,centers);
        int Container(TextRegionGroup group)
        {
            var votes=new Dictionary<int,int>();var total=0;
            foreach(var word in group.TextBounds)for(var y=word.Y;y<word.Bottom;y+=2)for(var x=word.X;x<word.Right;x+=2)
            {if(x>word.X+3&&x<word.Right-4&&y>word.Y+3&&y<word.Bottom-4)continue;total++;var id=labels.At<int>(y,x);if(id>0)votes[id]=votes.GetValueOrDefault(id)+1;}
            var best=votes.OrderByDescending(v=>v.Value).FirstOrDefault();
            if(best.Value<total*.38||stats.At<int>(best.Key,4)>source.Width*(long)source.Height*.12)return 0;
            return best.Key;
        }
        var groups=input.ToList();var containers=groups.Select(Container).ToList();
        for(var i=0;i<groups.Count;i++)for(var j=i+1;j<groups.Count;j++)
        {
            if(containers[i]==0||containers[i]!=containers[j])continue;
            var a=groups[i];var b=groups[j];var size=a.TextBounds.Concat(b.TextBounds).Select(r=>Math.Min(r.Width,r.Height)).Order().ToArray();
            var joined=a.CropBounds.Union(b.CropBounds);
            if(joined.Height>size[size.Length/2]*9&&joined.Width>size[size.Length/2]*3)continue;
            groups[i]=a with{CropBounds=joined,TextBounds=a.TextBounds.Concat(b.TextBounds).Distinct().ToArray()};groups.RemoveAt(j);containers.RemoveAt(j);j=i;
        }
        return groups;
    }
}
