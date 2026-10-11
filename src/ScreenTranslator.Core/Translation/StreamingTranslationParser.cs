using System.Globalization;
using System.Text;

namespace ScreenTranslator.Core.Translation;

/// <summary>Decodes visible translation fields from an incomplete JSON response without leaking reasoning or source fields.</summary>
public static class StreamingTranslationParser
{
    public static string ReadTranslation(string json)
    {
        var translations = new List<string>();
        var index = 0;
        while (index < json.Length)
        {
            if (json[index] != '"') { index++; continue; }
            var key = ReadString(json, ref index, out var complete);
            if (!complete) break;
            while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
            if (index >= json.Length || json[index] != ':') continue;
            index++;
            while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
            if (index >= json.Length || json[index] != '"') continue;
            var value = ReadString(json, ref index, out complete);
            if (key == "translation") translations.Add(value);
            if (!complete) break;
        }
        return string.Join("\n", translations);
    }

    internal static string ReadString(string json, ref int index, out bool complete)
    {
        complete = false;
        index++;
        var text = new StringBuilder();
        while (index < json.Length)
        {
            var character = json[index++];
            if (character == '"') { complete = true; break; }
            if (character != '\\') { text.Append(character); continue; }
            if (index >= json.Length) break;
            var escape = json[index++];
            if (escape == 'u')
            {
                if (index + 4 > json.Length) break;
                if (!ushort.TryParse(json.AsSpan(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var unicode)) break;
                text.Append((char)unicode);
                index += 4;
            }
            else
            {
                text.Append(escape switch
                {
                    '"' => '"', '\\' => '\\', '/' => '/', 'b' => '\b', 'f' => '\f',
                    'n' => '\n', 'r' => '\r', 't' => '\t', _ => escape,
                });
            }
        }
        if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text.Length--;
        return text.ToString();
    }
}
