using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Settings;

public enum ApiNetworkMode { SystemProxy, Direct, Clash, ClashNode, CustomProxy }

/// <summary>Routing metadata only; no subscription, controller secret or proxy credentials.</summary>
public sealed record ApiNetworkOptions
{
    public ApiNetworkMode Mode { get; init; }
    public string ClashNode { get; init; } = "";
    public string ProxyUrl { get; init; } = "";

    public ApiNetworkOptions Normalize() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : ApiNetworkMode.SystemProxy,
        ClashNode = ClashNode ?? "", ProxyUrl = ProxyUrl?.Trim() ?? "",
    };

    public void Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new TranslationConfigurationException("网络连接方式无效。");
        if (Mode == ApiNetworkMode.ClashNode && (string.IsNullOrWhiteSpace(ClashNode) || ClashNode.Length > 512))
            throw new TranslationConfigurationException("请读取 Clash 节点列表并选择一个节点。");
        if (Mode == ApiNetworkMode.CustomProxy) _ = CustomProxyUri();
    }

    public Uri CustomProxyUri()
    {
        if (!Uri.TryCreate(ProxyUrl?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "socks5") || uri.Port is < 1 or > 65535
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/")
            throw new TranslationConfigurationException("代理地址须为 http://、https:// 或 socks5://主机:端口，不包含账号、密码或路径。");
        return uri;
    }

    public string Identity => Mode switch
    {
        ApiNetworkMode.ClashNode => $"{Mode}\n{ClashNode}",
        ApiNetworkMode.CustomProxy => $"{Mode}\n{ProxyUrl}",
        _ => Mode.ToString(),
    };
    public override string ToString() => $"API network ({Mode})";
}
