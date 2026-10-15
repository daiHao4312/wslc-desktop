using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Helpers;

namespace WslcDesktop.Pages;

/// <summary>
/// 活动记录页（M11）：引擎事件的实时流水。
///
/// 数据源就是 <see cref="IEventStreamService"/> 的环形缓冲，本页不再自己拉引擎命令。
/// 页面只在可见期间订阅事件（离开就退订），离开期间的事件靠服务缓冲，重进时回填。
/// </summary>
public sealed partial class ActivityPage : Page, IRefreshable
{
    private const int MaxItems = 500;

    private readonly IEventStreamService _eventStream;
    private readonly IPrefsService _prefsService;

    /// <summary>完整缓冲（时间升序，与引擎一致）。</summary>
    private readonly List<WslcEvent> _all = new();

    /// <summary>过滤后的展示集合（时间降序，新增在最上）。</summary>
    private readonly ObservableCollection<WslcEvent> _visible = new();

    private bool _subscribed;
    private string _typeFilter = "*";
    private int _pendingWhilePaused;

    public ActivityPage()
    {
        InitializeComponent();

        _eventStream = App.Services.GetRequiredService<IEventStreamService>();
        _prefsService = App.Services.GetRequiredService<IPrefsService>();

        EventsListView.ItemsSource = _visible;
        TypeFilterComboBox.SelectedIndex = 0;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Subscribe();
        _ = RefreshAsync(userInitiated: false);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Unsubscribe();
    }

    public Task RefreshAsync(bool userInitiated)
    {
        // 暂停期间轮询 Tick 与事件防抖 Tick 仍会触发刷新：只更新状态文案，
        // 不动列表、不清积压计数——否则"暂停"连一个轮询周期（≤3s）都撑不过
        if (PauseToggle.IsChecked == true)
        {
            UpdateStreamStatus();
            return Task.CompletedTask;
        }

        // 服务缓冲是唯一真源：重进页面时用它回填（含离开期间收到的事件）
        _all.Clear();
        _all.AddRange(_eventStream.RecentEvents);
        if (_all.Count > MaxItems)
        {
            _all.RemoveRange(0, _all.Count - MaxItems);
        }

        _pendingWhilePaused = 0;

        RebuildVisible();
        UpdateCountText();
        UpdateStreamStatus();

        return Task.CompletedTask;
    }

    // ---------- 事件接入 ----------

    private void Subscribe()
    {
        if (_subscribed) return;

        _eventStream.EventReceived += OnEventReceived;
        _eventStream.StatusChanged += OnStreamStatusChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;

        _eventStream.EventReceived -= OnEventReceived;
        _eventStream.StatusChanged -= OnStreamStatusChanged;
        _subscribed = false;
    }

    private void OnEventReceived(object? sender, WslcEvent evt)
        // 事件在后台线程触发：切回 UI 线程再动集合，否则会打崩列表
        => DispatcherQueue?.TryEnqueue(() => AppendEvent(evt));

    private void OnStreamStatusChanged(object? sender, EventStreamStatus status)
        => DispatcherQueue?.TryEnqueue(UpdateStreamStatus);

    private void AppendEvent(WslcEvent evt)
    {
        _all.Add(evt);
        if (_all.Count > MaxItems)
        {
            _all.RemoveAt(0);
        }

        if (PauseToggle.IsChecked == true)
        {
            // 暂停只是不往列表里加，事件本身照收，恢复时一次性补上
            _pendingWhilePaused++;
            UpdateCountText();
            return;
        }

        if (!Matches(evt)) return;

        _visible.Insert(0, evt);
        while (_visible.Count > MaxItems)
        {
            _visible.RemoveAt(_visible.Count - 1);
        }

        UpdateEmptyState();
        UpdateCountText();
    }

    private bool Matches(WslcEvent evt)
        => _typeFilter == "*" || string.Equals(evt.Type, _typeFilter, StringComparison.OrdinalIgnoreCase);

    private void RebuildVisible()
    {
        _visible.Clear();

        // 倒序：最新在最上，符合"流水"的阅读方向
        for (var i = _all.Count - 1; i >= 0; i--)
        {
            if (Matches(_all[i]))
            {
                _visible.Add(_all[i]);
            }
        }

        UpdateEmptyState();
    }

    // ---------- 交互 ----------

    private void TypeFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TypeFilterComboBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;

        _typeFilter = tag;
        RebuildVisible();
        UpdateCountText();
    }

    private void PauseToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (PauseToggle.IsChecked == true) return;

        // 恢复：暂停期间的事件已在缓冲里，直接重建列表即可
        _pendingWhilePaused = 0;
        RebuildVisible();
        UpdateCountText();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _eventStream.Clear();
        _all.Clear();
        _visible.Clear();
        _pendingWhilePaused = 0;

        UpdateEmptyState();
        UpdateCountText();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshAsync(userInitiated: true);

    // ---------- 状态展示 ----------

    private void UpdateStreamStatus()
    {
        var prefs = _prefsService.GetPreferences();
        var state = _eventStream.State;

        string text;
        string dotResource;
        string detail;

        if (!prefs.UseEventStream)
        {
            text = "事件流已关闭";
            dotResource = "SystemFillColorNeutralBrush";
            detail = "在「设置 → 引擎事件流」开启后，操作一到就刷新，不必等轮询";
        }
        else
        {
            switch (state)
            {
                case EventStreamState.Running:
                    text = "事件流已连接";
                    dotResource = "SystemFillColorSuccessBrush";
                    detail = "事件一到即刷新；轮询已降级为兜底";
                    break;

                case EventStreamState.Connecting:
                    text = "正在建立订阅…";
                    dotResource = "SystemFillColorCautionBrush";
                    detail = "首次订阅会回补最近 6 小时的事件";
                    break;

                // 订阅建好了但回补窗口内没有事件：这是正常状态，不能落到 default 显示"未启动"
                case EventStreamState.Subscribed:
                    text = "事件流已订阅";
                    dotResource = "SystemFillColorSuccessBrush";
                    detail = "订阅正常，等待容器事件；轮询已降级为兜底";
                    break;

                case EventStreamState.Reconnecting:
                    text = "事件流重连中";
                    dotResource = "SystemFillColorCautionBrush";
                    detail = "期间由轮询兜底，恢复后按上一条事件时间续接，不漏事件";
                    break;

                default:
                    text = "事件流未启动";
                    dotResource = "SystemFillColorNeutralBrush";
                    detail = string.Empty;
                    break;
            }
        }

        StreamStateText.Text = text;
        StreamDetailText.Text = detail;
        ApplyDotBrush(dotResource);

        // 关闭时把"该怎么开"写清楚，而不是让用户面对一个永远空着的页面
        HintInfoBar.IsOpen = !prefs.UseEventStream;
        if (!prefs.UseEventStream)
        {
            HintInfoBar.Message = "事件流已关闭，本页不会实时更新。可在「设置」中开启「引擎事件流」。";
        }

        UpdateEmptyState();
    }

    private void ApplyDotBrush(string resourceKey)
    {
        try
        {
            if (Application.Current?.Resources?.TryGetValue(resourceKey, out var found) == true && found is Brush brush)
            {
                StreamStateDot.Fill = brush;
                return;
            }
        }
        catch
        {
            // 资源查不到就走下面的固定灰
        }

        StreamStateDot.Fill = new SolidColorBrush(Colors.Gray);
    }

    private void UpdateEmptyState()
    {
        var isEmpty = _visible.Count == 0;

        EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        EventsListView.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;

        if (!isEmpty) return;

        var prefs = _prefsService.GetPreferences();

        if (!prefs.UseEventStream)
        {
            EmptyStateText.Text = "事件流已关闭，因此没有事件可显示。\n在「设置」中开启「引擎事件流」后回到本页即可。";
            return;
        }

        EmptyStateText.Text = _eventStream.State switch
        {
            EventStreamState.Connecting or EventStreamState.Reconnecting
                => "正在建立事件流订阅，稍候…",
            _ => "还没有事件。启动或停止一个容器试试。"
        };
    }

    private void UpdateCountText()
    {
        CountText.Text = _pendingWhilePaused > 0
            ? $"已暂停（积压 {_pendingWhilePaused} 条）· 显示 {_visible.Count}"
            : $"显示 {_visible.Count} / 缓冲 {_all.Count}";
    }
}
