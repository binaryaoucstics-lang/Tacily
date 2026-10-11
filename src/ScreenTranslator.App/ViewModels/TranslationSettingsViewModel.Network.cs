using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;
using System.Diagnostics;

namespace ScreenTranslator.App.ViewModels;

public sealed partial class TranslationSettingsViewModel
{
    private ApiNetworkOptions _defaultNetwork = new();
    private string _networkMode = "系统代理", _clashNode = "", _proxyUrl = "", _clashStatus = "";
    private int _clashRevision;
    private bool _isReadingClash;
    private CancellationTokenSource? _clashCancellation;
    private Func<CancellationToken, Task<IReadOnlyList<string>>>? _clashNodesLoader;
    private Func<bool>? _clashInstallationProbe;
    private bool _clashInstalled = true;
    public Func<bool>? ClashInstallationProbe
    {
        get => _clashInstallationProbe;
        set { _clashInstallationProbe = value; CheckClashInstallation(); }
    }
    public bool ShowClashDownload => ShowClashDiscovery && !_clashInstalled;
    public IRelayCommand OpenClashDownloadCommand { get; private set; } = null!;
    private void CheckClashInstallation()
    {
        if (_clashInstallationProbe is not null) _clashInstalled = _clashInstallationProbe();
        OnPropertyChanged(nameof(ShowClashDownload));
    }
    public ObservableCollection<string> NetworkModes { get; } = ["系统代理", "直连", "Clash 当前路由", "Clash 指定节点", "自定义代理"];
    public ObservableCollection<string> ClashNodes { get; } = [];
    public string NetworkMode
    {
        get => _networkMode;
        set
        {
            if (!NetworkModes.Contains(value) || !SetProperty(ref _networkMode, value)) return;
            NetworkChanged();
            OnPropertyChanged(nameof(ShowClashNode)); OnPropertyChanged(nameof(ShowClashDiscovery));
            OnPropertyChanged(nameof(ShowProxyUrl)); OnPropertyChanged(nameof(NetworkDescription));
            CheckClashInstallation();
        }
    }
    public string SelectedClashNode { get => _clashNode; set { if (_updatingClashChoices) return; if (SetProperty(ref _clashNode, value ?? "")) { NetworkChanged(); NotifyProbeCommands(); } } }
    public string ProxyUrl { get => _proxyUrl; set { if (SetProperty(ref _proxyUrl, value ?? "")) NetworkChanged(); } }
    public bool ShowClashNode => NetworkMode == "Clash 指定节点";
    public bool ShowClashDiscovery => NetworkMode is "Clash 指定节点" or "Clash 当前路由";
    public bool ShowProxyUrl => NetworkMode == "自定义代理";
    public string NetworkDescription => NetworkMode switch
    {
        "直连" => "不使用应用层代理。系统 TUN / VPN 路由仍然生效。",
        "Clash 当前路由" => "自动读取 Clash Verge 端口，遵循当前节点和分流规则。",
        "Clash 指定节点" => "复用 Clash Verge 内核建立独立通道，可与其他预设同时使用不同节点。",
        "自定义代理" => "支持 HTTP、HTTPS、SOCKS5，例如 http://127.0.0.1:7897。",
        _ => "使用 Windows 当前系统代理，每个预设可独立选择网络方式。",
    };
    public string ClashStatus { get => _clashStatus; private set => SetProperty(ref _clashStatus, value); }
    public bool IsReadingClash
    {
        get => _isReadingClash;
        private set { if (SetProperty(ref _isReadingClash, value)) { OnPropertyChanged(nameof(ClashReadButtonText)); ReadClashNodesCommand.NotifyCanExecuteChanged(); NotifyProbeCommands(); } }
    }
    public string ClashReadButtonText => IsReadingClash ? "正在读取…" : "读取 Clash 节点";
    public IAsyncRelayCommand ReadClashNodesCommand { get; private set; } = null!;
    public Func<CancellationToken, Task<IReadOnlyList<string>>>? ClashNodesLoader
    {
        get => _clashNodesLoader;
        set { _clashNodesLoader = value; ReadClashNodesCommand.NotifyCanExecuteChanged(); }
    }
    public ApiNetworkOptions CollectNetwork() => new()
    {
        Mode = (ApiNetworkMode)NetworkModes.IndexOf(NetworkMode),
        ClashNode = SelectedClashNode, ProxyUrl = ProxyUrl.Trim(),
    };
    private void ApplyNetwork(ApiNetworkOptions? options)
    {
        var network = (options ?? new()).Normalize();
        NetworkMode = NetworkModes[(int)network.Mode];
        if (network.ClashNode.Length > 0 && !ClashNodes.Contains(network.ClashNode)) ClashNodes.Add(network.ClashNode);
        SelectedClashNode = network.ClashNode; ProxyUrl = network.ProxyUrl;
        SyncNodeChoices();
        InvalidateClashDiscovery();
    }
    private void InitializeNetwork()
    {
        InitializeNodeTests();
        ReadClashNodesCommand = new AsyncRelayCommand(ReadClashNodesAsync,
            () => !IsSaving && !IsReadingClash && ClashNodesLoader is not null);
        OpenClashDownloadCommand = new RelayCommand(() =>
        {
            try { Process.Start(new ProcessStartInfo("https://github.com/clash-verge-rev/clash-verge-rev/releases/latest") { UseShellExecute = true }); }
            catch { ClashStatus = "请访问 github.com/clash-verge-rev/clash-verge-rev/releases 下载。"; }
        });
    }
    private void NetworkChanged()
    {
        InvalidateConnection(); InvalidateModelList(false); InvalidateClashDiscovery();
    }
    private void InvalidateClashDiscovery()
    { _clashRevision++; _clashCancellation?.Cancel(); ClashStatus = ""; }
    private async Task ReadClashNodesAsync(CancellationToken token)
    {
        var revision = _clashRevision;
        CheckClashInstallation();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _clashCancellation = cancellation; IsReadingClash = true; ClashStatus = "正在连接本机 Clash Verge…";
        try
        {
            var nodes = await ClashNodesLoader!(cancellation.Token);
            if (revision != _clashRevision || cancellation.IsCancellationRequested) return;
            var selected = SelectedClashNode;
            // Updating ComboBox items must not turn a saved selection into an empty draft.
            ClashNodes.Clear(); foreach (var node in nodes) ClashNodes.Add(node);
            if (selected.Length > 0 && !ClashNodes.Contains(selected)) ClashNodes.Add(selected);
            SelectedClashNode = selected;
            SyncNodeChoices(true);
            ClashStatus = nodes.Count == 0 ? "Clash 未返回可用节点，请先更新订阅。"
                : selected.Length > 0 && !nodes.Contains(selected, StringComparer.Ordinal)
                    ? "已保存节点不在当前列表中，请重新选择节点。"
                    : $"已连接 Clash Verge · {nodes.Count} 个节点。请选择后保存。";
        }
        catch (OperationCanceledException) { if (revision == _clashRevision) ClashStatus = "读取已取消，请重试。"; }
        catch (TranslationConfigurationException error) { if (revision == _clashRevision) ClashStatus = error.Message; }
        catch { if (revision == _clashRevision) ClashStatus = "读取 Clash 节点失败，请确认 Clash Verge 已启动。"; }
        finally { if (ReferenceEquals(_clashCancellation, cancellation)) _clashCancellation = null; IsReadingClash = false; }
    }
}
