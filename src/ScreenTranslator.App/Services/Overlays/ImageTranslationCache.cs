using System.Security.Cryptography;
using System.Text;
using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.App.Services.Overlays;
public sealed record CachedImageTranslation(GroundedVisionDocument? Grounding,TranslationResult Result,IReadOnlyList<RenderedImageTile> Tiles)
{public long Bytes=> (Grounding?.InkMask?.Pixels.Length??0)+(Grounding?.DetailPng?.Length??0)+(Grounding?.ReadingSheets.Sum(s=>(long)s.Length)??0)+Tiles.Sum(t=>(long)t.Png.Length+t.Bitmap.Pixels.Length);}

/// <summary>Bounded process memory only. Image hashes and settings prevent stale translations; no disk history.</summary>
public sealed class ImageTranslationCache(long limitBytes=192L*1024*1024)
{
    private readonly object _gate=new();private readonly Dictionary<string,(CachedImageTranslation Item,LinkedListNode<string> Node)> _items=[];
    private readonly LinkedList<string> _recent=new();private long _bytes;
    public static string Key(CapturedBitmap image,AppSettings settings)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(image.Pixels.Span);hash.AppendData(Encoding.UTF8.GetBytes($"{image.Width}:{image.Height}:{image.Stride}:{settings.SourceLanguage}:{settings.TargetLanguage}:{settings.TranslationStyle}:{settings.TranslationContext}:{TranslationProviderConfiguration.TransportIdentity(settings)}:{settings.ThinkingLevel}:{settings.BriefThinkingGuidanceEnabled}:{settings.BoundedLowThinkingEnabled}:typography-colour-5:direct-vision-local-evidence-1:"+DirectVisionTranslation.Prompt));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    public CachedImageTranslation? Get(string key)
    {lock(_gate){if(!_items.TryGetValue(key,out var entry))return null;_recent.Remove(entry.Node);_recent.AddLast(entry.Node);return entry.Item;}}
    public void Set(string key,CachedImageTranslation item)
    {
        if(item.Result.Blocks.Count==0||item.Tiles.Count!=item.Result.Blocks.Count||item.Bytes>limitBytes)return;
        lock(_gate){if(_items.Remove(key,out var prior)){_bytes-=prior.Item.Bytes;_recent.Remove(prior.Node);}
            var node=_recent.AddLast(key);_items[key]=(item,node);_bytes+=item.Bytes;
            while(_bytes>limitBytes&&_recent.First is {} oldest){var removed=_items[oldest.Value];_bytes-=removed.Item.Bytes;_items.Remove(oldest.Value);_recent.RemoveFirst();}}
    }
    public void Remove(string key){lock(_gate){if(_items.Remove(key,out var entry)){_bytes-=entry.Item.Bytes;_recent.Remove(entry.Node);}}}
    public int Count{get{lock(_gate)return _items.Count;}}
}
