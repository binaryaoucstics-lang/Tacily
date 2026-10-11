using System.IO;
using System.Text;

namespace ScreenTranslator.App.Services.Overlays;

/// <summary>Compact local word list keeps common Chinese words and Latin names intact while wrapping.</summary>
public static class ChineseLineTokens
{
    private static readonly Lazy<HashSet<string>> Words=new(()=>
    {
        var file=Path.Combine(AppContext.BaseDirectory,"words","chinese-wrap.txt");
        var words=File.Exists(file)?File.ReadLines(file).Select(w=>w.Trim()).Where(w=>w.Length is >=2 and <=6).ToHashSet(StringComparer.Ordinal):new(StringComparer.Ordinal);
        words.UnionWith(["没关系","工作人员","名字","现在","因为","那边","电子","游戏","钱包","失物","旅程","不要紧","别担心","为什么","怎么回事","没问题","对不起","不好意思","加油","马上","回来","谢谢","你好","小心","落山","投降","绑架","一对二","看不起","瞧不起","打零工","太好了"]);
        return words;
    });
    public static IEnumerable<string> Split(string text)
    {
        for(var index=0;index<text.Length;)
        {
            var found=0;
            for(var length=Math.Min(6,text.Length-index);length>=2;length--)
                if(Words.Value.Contains(text.Substring(index,length))){found=length;break;}
            if(found>0){yield return text.Substring(index,found);index+=found;continue;}
            if(char.IsAsciiLetterOrDigit(text[index]))
            {var end=index+1;while(end<text.Length&&char.IsAsciiLetterOrDigit(text[end]))end++;yield return text[index..end];index=end;continue;}
            var rune=Rune.GetRuneAt(text,index);yield return rune.ToString();index+=rune.Utf16SequenceLength;
        }
    }
}
