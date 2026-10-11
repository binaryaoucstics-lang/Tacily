using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Capture;

/// <summary>Keep independently aligned text columns apart even when words nearly touch.</summary>
public static class ParagraphColumnSplitter
{
    public static IReadOnlyList<TextRegionGroup> Split(IReadOnlyList<TextRegionGroup> groups)
    {
        var output=new List<TextRegionGroup>();
        foreach(var group in groups)Visit(group,0);
        return output;
        void Visit(TextRegionGroup group,int depth)
        {
            var box=group.CropBounds;var words=group.TextBounds;
            var sizes=words.Select(w=>Math.Min(w.Width,w.Height)).Order().ToArray();
            var glyph=sizes.Length==0?20:Math.Max(8,sizes[sizes.Length/2]);
            if(depth>=3||group.IsVertical||words.Count<20||box.Width<glyph*18||box.Height<glyph*5)
            {output.Add(group);return;}
            var header=words.Where(w=>w.Y<box.Y+glyph*.6).ToArray();
            var body=words.Except(header).ToArray();
            if(header.Length>=4&&body.Length>=12)
            {
                var heading=header.Aggregate((a,b)=>a.Union(b));var rest=body.Aggregate((a,b)=>a.Union(b));
                if(heading.Width>box.Width*.8&&rest.Y-heading.Bottom>=glyph*.35)
                {output.Add(new(heading,header,false));Visit(new(rest,body,false),depth);return;}
            }
            var coverage=new int[box.Width];
            foreach(var word in words.Where(w=>w.Width<box.Width*.6))
                for(var x=Math.Max(0,word.X-box.X);x<Math.Min(box.Width,word.Right-box.X);x++)coverage[x]+=word.Height;
            var best=-1;var longest=0;var start=-1;
            for(var x=(int)(box.Width*.2);x<(int)(box.Width*.8);x++)
            {
                if(coverage[x]<box.Height*.16){if(start<0)start=x;continue;}
                if(start>=0){if(x-start>longest){longest=x-start;best=(x+start)/2;}start=-1;}
            }
            if(longest<glyph*.6||best<0){output.Add(group);return;}
            var cut=box.X+best;var left=words.Where(w=>w.Center.X<cut).ToArray();var right=words.Where(w=>w.Center.X>=cut).ToArray();
            if(left.Length<6||right.Length<6||left.Any(w=>w.Right>cut+glyph*.25)||right.Any(w=>w.X<cut-glyph*.25))
            {output.Add(group);return;}
            foreach(var part in new[]{left,right})
            {var bounds=part.Aggregate((a,b)=>a.Union(b));Visit(new(bounds,part,false),depth+1);}
        }
    }
}
