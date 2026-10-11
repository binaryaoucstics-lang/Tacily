using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTranslator.App.Services.Networking;

namespace ScreenTranslator.App.ViewModels;

public sealed class ClashNodeChoice(string name) : ObservableObject
{
    public string Name { get; } = name;
    public ClashNodeHealth Health { get; private set; }
    public int? DelayMilliseconds { get; private set; }
    public string StateText => Health switch
    {
        ClashNodeHealth.Testing => "测速中…", ClashNodeHealth.Available => "可用", ClashNodeHealth.Reachable => "可连接",
        ClashNodeHealth.Unavailable => "不可用", ClashNodeHealth.Cancelled => "已取消", _ => "未测试",
    };
    public string DelayText => DelayMilliseconds is { } delay ? $"{delay} ms" : "—";
    public string StateColor => Health switch
    { ClashNodeHealth.Available => "#20845B", ClashNodeHealth.Unavailable => "#C74444", ClashNodeHealth.Testing => "#1671B4", _ => "#7A8491" };
    public int Rank => Health switch { ClashNodeHealth.Available => 0, ClashNodeHealth.Reachable => 1, ClashNodeHealth.Unavailable => 4, _ => 2 };
    public void Apply(ClashNodeProbeResult result)
    {
        Health = result.Health; DelayMilliseconds = result.DelayMilliseconds;
        OnPropertyChanged(nameof(Health)); OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(DelayText));
        OnPropertyChanged(nameof(StateColor)); OnPropertyChanged(nameof(Rank)); OnPropertyChanged(nameof(DelayMilliseconds));
    }
    public override string ToString() => Name;
}

public sealed partial class TranslationSettingsViewModel
{
    private CancellationTokenSource? _nodeProbeCancellation;
    private Func<string, CancellationToken, Task<ClashNodeProbeResult>>? _clashNodeTester;
    private int _nodeProbeRevision;
    private bool _testingClashNodes, _updatingClashChoices, _sortClashNodesByDelay = true;
    private string _clashProbeStatus = "";
    public ObservableCollection<ClashNodeChoice> ClashNodeChoices { get; } = [];
    public Func<string, CancellationToken, Task<ClashNodeProbeResult>>? ClashNodeTester
    {
        get => _clashNodeTester;
        set { _clashNodeTester = value; NotifyProbeCommands(); }
    }
    public bool SortClashNodesByDelay
    {
        get => _sortClashNodesByDelay;
        set { if (SetProperty(ref _sortClashNodesByDelay, value)) SortNodeChoices(); }
    }
    public bool IsTestingClashNodes { get => _testingClashNodes; private set { if (SetProperty(ref _testingClashNodes, value)) NotifyProbeCommands(); } }
    public string ClashProbeStatus { get => _clashProbeStatus; private set => SetProperty(ref _clashProbeStatus, value); }
    public IAsyncRelayCommand TestAllClashNodesCommand { get; private set; } = null!;
    public IAsyncRelayCommand TestSelectedClashNodeCommand { get; private set; } = null!;
    public IRelayCommand CancelClashNodeTestsCommand { get; private set; } = null!;
    private void InitializeNodeTests()
    {
        TestAllClashNodesCommand = new AsyncRelayCommand(token => TestNodesAsync(true, token),
            () => !IsSaving && !IsReadingClash && !IsTestingClashNodes && ClashNodeTester is not null && ClashNodes.Count > 0);
        TestSelectedClashNodeCommand = new AsyncRelayCommand(token => TestNodesAsync(false, token),
            () => !IsSaving && !IsReadingClash && !IsTestingClashNodes && ClashNodeTester is not null && SelectedClashNode.Length > 0);
        CancelClashNodeTestsCommand = new RelayCommand(() => _nodeProbeCancellation?.Cancel(), () => IsTestingClashNodes);
    }
    private void NotifyProbeCommands()
    { TestAllClashNodesCommand?.NotifyCanExecuteChanged(); TestSelectedClashNodeCommand?.NotifyCanExecuteChanged(); CancelClashNodeTestsCommand?.NotifyCanExecuteChanged(); }
    private void SyncNodeChoices(bool resetHealth = false)
    {
        _nodeProbeRevision++; _nodeProbeCancellation?.Cancel();
        var saved = SelectedClashNode; _updatingClashChoices = true;
        try
        {
            var existing = ClashNodeChoices.ToDictionary(n => n.Name, StringComparer.Ordinal);
            ClashNodeChoices.Clear();
            foreach (var name in ClashNodes)
            {
                var choice = existing.GetValueOrDefault(name) ?? new(name);
                if (resetHealth) choice.Apply(new(name, ClashNodeHealth.Untested));
                else if (choice.Health == ClashNodeHealth.Testing) choice.Apply(new(name, ClashNodeHealth.Cancelled));
                ClashNodeChoices.Add(choice);
            }
        }
        finally { _updatingClashChoices = false; }
        SelectedClashNode = saved; OnPropertyChanged(nameof(SelectedClashNode)); SortNodeChoices(); NotifyProbeCommands();
    }
    private void SortNodeChoices()
    {
        var ordered = SortClashNodesByDelay ? ClashNodeChoices.OrderBy(n => n.Rank).ThenBy(n => n.DelayMilliseconds ?? int.MaxValue).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToArray()
            : ClashNodeChoices.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        { var index = ClashNodeChoices.IndexOf(ordered[i]); if (index != i) ClashNodeChoices.Move(index, i); }
    }
    private async Task TestNodesAsync(bool all, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _nodeProbeCancellation = cancellation; var revision = _nodeProbeRevision;
        var choices = (all ? ClashNodeChoices : ClashNodeChoices.Where(n => n.Name == SelectedClashNode)).ToArray();
        var done = 0; var available = 0; IsTestingClashNodes = true; ClashProbeStatus = $"正在测速 0/{choices.Length}…";
        foreach (var choice in choices) choice.Apply(new(choice.Name, ClashNodeHealth.Testing));
        async Task<(ClashNodeChoice Choice, ClashNodeProbeResult Result)> Check(ClashNodeChoice choice)
        {
            try { return (choice, await ClashNodeTester!(choice.Name, cancellation.Token).ConfigureAwait(false)); }
            catch (OperationCanceledException) { return (choice, new(choice.Name, ClashNodeHealth.Cancelled)); }
            catch { return (choice, new(choice.Name, ClashNodeHealth.Unavailable, Reason: "检测失败")); }
        }
        var pending = choices.Select(Check).ToList();
        try
        {
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending); pending.Remove(completed); var result = await completed;
                if (revision != _nodeProbeRevision) continue;
                result.Choice.Apply(result.Result); done++;
                if (result.Result.Health is ClashNodeHealth.Available or ClashNodeHealth.Reachable) available++;
                SortNodeChoices(); ClashProbeStatus = $"已测 {done}/{choices.Length} · 可用/可连接 {available}";
            }
            if (revision == _nodeProbeRevision && cancellation.IsCancellationRequested) ClashProbeStatus = "已停止测速，已完成结果保留。";
        }
        finally
        {
            if (ReferenceEquals(_nodeProbeCancellation, cancellation)) _nodeProbeCancellation = null;
            IsTestingClashNodes = false;
        }
    }
}
