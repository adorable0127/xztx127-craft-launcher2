using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using XCL2.App.Models;

namespace XCL2.App.Views;

public partial class BubbleVm
{
    private readonly List<AiAssistantActivity> _activitySnapshots = new();
    private readonly Dictionary<string, AiActivityVm> _activityViews = new(StringComparer.Ordinal);
    public ObservableCollection<AiActivityVm> ReasoningActivities { get; } = new();
    private bool _showThinking = true;
    public string ThinkingStatusText => ReasoningActivities.Count == 0 ? "等待接口返回公开思考内容…" : "";
    public Visibility ReasoningVisibility => ReasoningActivities.Count > 0 || IsGenerating && _showThinking ? Visibility.Visible : Visibility.Collapsed;
    public ObservableCollection<AiActivityVm> VisibleActivities { get; } = new();
    public Visibility ActivitiesVisibility => VisibleActivities.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string ActivitiesHeader => $"执行过程 · {VisibleActivities.Count} 项";
    public string ReasoningAvailabilityHint { get; private set; } = "";
    public Visibility ReasoningAvailabilityVisibility => string.IsNullOrEmpty(ReasoningAvailabilityHint)
        ? Visibility.Collapsed : Visibility.Visible;

    public void LoadActivities(IEnumerable<AiAssistantActivity>? activities, AiAssistantConfig config)
    {
        _activitySnapshots.Clear();
        _activityViews.Clear();
        VisibleActivities.Clear();
        ReasoningActivities.Clear();
        if (activities != null)
            _activitySnapshots.AddRange(activities.Select(activity => activity.Clone()));
        RefreshActivities(config);
    }

    public void UpdateActivity(AiAssistantActivity activity, AiAssistantConfig config, bool refresh)
    {
        var snapshot = activity.Clone();
        int index = _activitySnapshots.FindIndex(existing => existing.Id == snapshot.Id);
        if (index < 0) _activitySnapshots.Add(snapshot);
        else _activitySnapshots[index] = snapshot;
        if (refresh) RefreshActivities(config);
    }

    public void RefreshActivities(AiAssistantConfig config)
    {
        _showThinking = config.ShowReasoning || config.ShowReasoningSummary;
        var thoughts = _activitySnapshots.Where(activity => activity.IsVisible(config) && activity.Kind is AiActivityKind.Reasoning or AiActivityKind.ReasoningSummary).ToList();
        var thoughtIds = thoughts.Select(a => a.Id).ToHashSet();
        for (int i = ReasoningActivities.Count - 1; i >= 0; i--) if (!thoughtIds.Contains(ReasoningActivities[i].Id)) ReasoningActivities.RemoveAt(i);
        foreach (var snapshot in thoughts)
        {
            if (!_activityViews.TryGetValue(snapshot.Id, out var view)) { view = new AiActivityVm(snapshot); _activityViews[snapshot.Id] = view; }
            else view.Update(snapshot);
            if (!ReasoningActivities.Contains(view)) ReasoningActivities.Add(view);
        }
        Raise(nameof(ReasoningVisibility));
        Raise(nameof(ThinkingStatusText));
        var visible = _activitySnapshots.Where(activity => activity.IsVisible(config) && activity.Kind is not (AiActivityKind.Reasoning or AiActivityKind.ReasoningSummary)).ToList();
        var visibleIds = new HashSet<string>(visible.Select(activity => activity.Id), StringComparer.Ordinal);
        for (int index = VisibleActivities.Count - 1; index >= 0; index--)
            if (!visibleIds.Contains(VisibleActivities[index].Id)) VisibleActivities.RemoveAt(index);

        for (int index = 0; index < visible.Count; index++)
        {
            var snapshot = visible[index];
            if (!_activityViews.TryGetValue(snapshot.Id, out var view))
            {
                view = new AiActivityVm(snapshot);
                _activityViews[snapshot.Id] = view;
            }
            else view.Update(snapshot);
            int existingIndex = VisibleActivities.IndexOf(view);
            if (existingIndex < 0) VisibleActivities.Insert(index, view);
            else if (existingIndex != index) VisibleActivities.Move(existingIndex, index);
        }
        Raise(nameof(ActivitiesVisibility));
        Raise(nameof(ActivitiesHeader));
        var missing = new List<string>();
        if (IsAssistantMessage && OriginalMessage != null && !string.IsNullOrWhiteSpace(OriginalMessage.ModelUsed))
        {
            if (config.ShowReasoningSummary && !_activitySnapshots.Any(activity => activity.Kind == AiActivityKind.ReasoningSummary))
                missing.Add("思考过程摘要");
            if (config.ShowReasoning && !_activitySnapshots.Any(activity => activity.Kind == AiActivityKind.Reasoning))
                missing.Add("思考过程");
        }
        ReasoningAvailabilityHint = missing.Count > 0 ? $"本次接口未返回{string.Join(" / ", missing)}。" : "";
        Raise(nameof(ReasoningAvailabilityHint));
        Raise(nameof(ReasoningAvailabilityVisibility));
    }
}

/// <summary>只呈现服务上报的真实快照；公开思考内容完整保留，使用可滚动抽屉展示。</summary>
public sealed class AiActivityVm : INotifyPropertyChanged
{
    private AiAssistantActivity _snapshot;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id => _snapshot.Id;
    public bool IsExpanded { get; set; }
    public string Title => string.IsNullOrWhiteSpace(_snapshot.Title) ? KindLabel : _snapshot.Title;
    public string KindLabel => _snapshot.Kind switch
    {
        AiActivityKind.SubAgent => "子智能体",
        AiActivityKind.CodeChange => "代码修改",
        AiActivityKind.FileEdit => "修改文件",
        AiActivityKind.Command => "执行命令",
        AiActivityKind.ReasoningSummary => "思考过程摘要 · 供应商公开内容",
        AiActivityKind.Reasoning => "思考过程 · 供应商公开内容",
        _ => "执行记录"
    };
    public bool IsRunning => _snapshot.Status == AiActivityStatus.Running;
    public string StatusLabel => _snapshot.Status switch
    {
        AiActivityStatus.Pending => "等待中",
        AiActivityStatus.Running => "进行中",
        AiActivityStatus.Completed => "已完成",
        AiActivityStatus.Failed => "失败",
        AiActivityStatus.Denied => "未获授权",
        AiActivityStatus.Cancelled => "已停止",
        _ => ""
    };
    public string Details => _snapshot.Kind is AiActivityKind.Reasoning or AiActivityKind.ReasoningSummary
        ? _snapshot.Details ?? "" : Limit(_snapshot.Details, 16000);
    public string FilePath => _snapshot.FilePath ?? "";
    public string Command => Limit(string.IsNullOrEmpty(_snapshot.CommandConversion) ? _snapshot.Command
        : _snapshot.CommandConversion + "\n\n原命令：\n" + _snapshot.OriginalCommand + "\n\nWindows 命令：\n" + _snapshot.Command, 16000);
    public string BeforeCode => Limit(_snapshot.BeforeCode, 24000);
    public string AfterCode => Limit(_snapshot.AfterCode, 24000);
    public bool HasFileDiff => _snapshot.FileDiff != null;
    public AiFileDiff? FileDiff => _snapshot.FileDiff;
    public string FileDiffHeader => _snapshot.FileDiff == null ? "文件变更" :
        $"{(_snapshot.FileDiff.ChangeKind == AiFileChangeKind.Created ? "创建文件" : _snapshot.FileDiff.ChangeKind == AiFileChangeKind.Deleted ? "删除文件" : "文件差异")} · {_snapshot.FileDiff.ModifiedLines} 修改 / +{_snapshot.FileDiff.AddedLines} / −{_snapshot.FileDiff.DeletedLines}";
    public bool HasFileDiffNotice => !string.IsNullOrWhiteSpace(_snapshot.FileDiff?.Notice);
    public string ModelLabel => "模型：" + (_snapshot.Model ?? "");
    public bool HasDetails => !string.IsNullOrWhiteSpace(_snapshot.Details);
    public bool HasFilePath => !string.IsNullOrWhiteSpace(_snapshot.FilePath);
    public bool HasCommand => !string.IsNullOrWhiteSpace(_snapshot.Command);
    public bool HasBeforeCode => _snapshot.BeforeCode != null && !HasFileDiff;
    public bool HasAfterCode => _snapshot.AfterCode != null && !HasFileDiff;
    public bool HasModel => !string.IsNullOrWhiteSpace(_snapshot.Model);
    public string BeforeLabel => _snapshot.BeforeCode?.Length == 0 ? "修改前（空文件 / 新文件）" : "修改前";
    public string AfterLabel => _snapshot.AfterCode?.Length == 0 ? "修改后（空文件）" : "修改后";

    public AiActivityVm(AiAssistantActivity snapshot)
    {
        _snapshot = snapshot;
        IsExpanded = snapshot.Kind is not (AiActivityKind.Reasoning or AiActivityKind.ReasoningSummary or AiActivityKind.CodeChange or AiActivityKind.FileEdit);
    }

    public void Update(AiAssistantActivity snapshot)
    {
        if (ReferenceEquals(_snapshot, snapshot)) return;
        _snapshot = snapshot;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private static string Limit(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLength ? text : text[..maxLength] + "\n…内容较长，此处仅显示前半部分。";
    }
}
