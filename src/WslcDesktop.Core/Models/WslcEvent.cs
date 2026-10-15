namespace WslcDesktop.Core.Models;

/// <summary>
/// 一条引擎事件，对应 <c>wslc events</c> 的一条输出。
///
/// 引擎 JSON 实测形态：
/// <code>
/// {"Action":"start","Type":"container",
///  "Actor":{"ID":"&lt;64 位十六进制&gt;","Attributes":{"image":"...","name":"mysql"}},
///  "from":"...","id":"&lt;64 位十六进制&gt;","scope":"local","status":"start",
///  "time":1791962211,"timeNano":1791962211131552884}
/// </code>
///
/// 三个易错点（都由实测确认）：
/// ① <c>Action</c> 与 <c>status</c> 同值，类别要看大写 <c>Type</c>；
/// ② 时间优先取 <c>timeNano</c>（纳秒），<c>time</c> 只是整秒，同一秒内多条事件会撞车；
/// ③ 网络事件的 <c>name</c> 是<b>网络名</b>，容器 ID 在 <c>Attributes.container</c> 里。
/// </summary>
public record WslcEvent
{
    /// <summary>事件时间（本地时区）。</summary>
    public DateTimeOffset Time { get; init; }

    /// <summary>引擎给的纳秒时间戳原值；缺失（table 通道）时为 0。去重与排序用它保精度。</summary>
    public long TimeNano { get; init; }

    /// <summary>对象类别：container / network / volume / image / daemon ...。</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>动作：create / start / stop / die / destroy / kill / connect / disconnect ...。</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>被作用对象的 ID（容器与网络均为 64 位十六进制）。</summary>
    public string ActorId { get; init; } = string.Empty;

    /// <summary>对象名称：容器名，或网络名。</summary>
    public string ActorName { get; init; } = string.Empty;

    /// <summary>镜像引用（仅容器事件有）。</summary>
    public string Image { get; init; } = string.Empty;

    public string Scope { get; init; } = string.Empty;

    /// <summary>原始附加属性（exitCode / signal / type / container ...），保留供详情与摘要使用。</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = EmptyAttributes;

    private static readonly IReadOnlyDictionary<string, string> EmptyAttributes
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // ---------- 派生展示字段（放在模型里，便于单测锁定文案） ----------

    /// <summary>12 位短 ID，列表里显示用。</summary>
    public string ActorShortId
        => ActorId.Length > 12 ? ActorId[..12] : ActorId;

    /// <summary>列表里显示的时间。</summary>
    public string TimeDisplay => Time.ToString("HH:mm:ss");

    /// <summary>过滤键，形如 <c>container.start</c>。</summary>
    public string FilterKey
        => string.IsNullOrEmpty(Action) ? Type : $"{Type}.{Action}";

    /// <summary>去重键：同一事件在重连补拉时可能重复送达。</summary>
    public string DedupKey
        => TimeNano > 0
            ? $"{TimeNano}|{Type}|{Action}|{ActorId}"
            : $"{Time.ToUnixTimeMilliseconds()}|{Type}|{Action}|{ActorId}|{ActorName}";

    public string TypeDisplay => Type switch
    {
        "container" => "容器",
        "network" => "网络",
        "volume" => "数据卷",
        "image" => "镜像",
        "daemon" => "引擎",
        "plugin" => "插件",
        _ => string.IsNullOrEmpty(Type) ? "未知" : Type
    };

    public string ActionDisplay => Action switch
    {
        "create" => "创建",
        "start" => "启动",
        "stop" => "停止",
        "restart" => "重启",
        "die" => "退出",
        "destroy" => "销毁",
        "kill" => "强杀",
        "pause" => "暂停",
        "unpause" => "恢复",
        "rename" => "重命名",
        "update" => "更新",
        "connect" => "接入",
        "disconnect" => "断开",
        "mount" => "挂载",
        "unmount" => "卸载",
        "pull" => "拉取",
        "push" => "推送",
        "tag" => "打标签",
        "untag" => "移除标签",
        "delete" => "删除",
        "remove" => "删除",
        "prune" => "清理",
        "import" => "导入",
        "export" => "导出",
        "commit" => "提交",
        "oom" => "内存溢出",
        _ => string.IsNullOrEmpty(Action) ? "事件" : Action
    };

    /// <summary>展示用主体名：有名字用名字，没有就退回短 ID。</summary>
    public string DisplayName
        => !string.IsNullOrWhiteSpace(ActorName) ? ActorName
         : !string.IsNullOrWhiteSpace(ActorShortId) ? ActorShortId
         : "—";

    /// <summary>容器退出码（仅 die 事件有）；非 0 表示异常退出。</summary>
    public int? ExitCode
        => Attributes.TryGetValue("exitCode", out var raw) && int.TryParse(raw, out var code)
            ? code
            : null;

    /// <summary>强杀信号（仅 kill 事件有）。</summary>
    public string Signal
        => Attributes.TryGetValue("signal", out var sig) ? sig : string.Empty;

    /// <summary>网络事件里，发起连接的容器 ID。</summary>
    public string RelatedContainerId
        => Attributes.TryGetValue("container", out var cid) ? cid : string.Empty;

    /// <summary>严重度：critical / caution / success / neutral —— 界面据此上色，模型不做 UI 决策。</summary>
    public string Severity
    {
        get
        {
            if (Action == "oom") return "critical";
            if (Action == "die" && ExitCode is int code && code != 0) return "critical";
            // 注意 stop 也必须在这里：停止是"操作成功但对象停摆"，与 start 的 success 区分开
            if (Action is "stop" or "kill" or "destroy" or "delete" or "remove" or "prune"
                or "untag" or "unmount" or "disconnect" or "die")
            {
                return "caution";
            }
            if (Action is "start" or "create" or "connect" or "mount" or "pull"
                or "import" or "tag" or "restart" or "unpause")
            {
                return "success";
            }
            return "neutral";
        }
    }

    /// <summary>是否与容器状态相关（决定要不要刷新容器页/概览页）。</summary>
    public bool TouchesContainers => Type is "container" or "network" or "volume";

    /// <summary>一句话摘要，供活动记录页与状态栏显示。</summary>
    public string Summary
    {
        get
        {
            if (Type == "container")
            {
                return Action switch
                {
                    "create" => string.IsNullOrWhiteSpace(Image)
                        ? $"创建容器 {DisplayName}"
                        : $"创建容器 {DisplayName}（镜像 {Image}）",
                    "start" => $"启动容器 {DisplayName}",
                    "stop" => $"停止容器 {DisplayName}",
                    "restart" => $"重启容器 {DisplayName}",
                    "die" => ExitCode is int code
                        ? code == 0
                            ? $"容器 {DisplayName} 正常退出"
                            : $"容器 {DisplayName} 异常退出（退出码 {code}）"
                        : $"容器 {DisplayName} 已退出",
                    "kill" => string.IsNullOrWhiteSpace(Signal)
                        ? $"强制终止容器 {DisplayName}"
                        : $"强制终止容器 {DisplayName}（信号 {Signal}）",
                    "destroy" => $"销毁容器 {DisplayName}",
                    _ => $"{ActionDisplay}容器 {DisplayName}"
                };
            }

            if (Type == "network")
            {
                var who = string.IsNullOrWhiteSpace(RelatedContainerId)
                    ? DisplayName
                    : (RelatedContainerId.Length > 12 ? RelatedContainerId[..12] : RelatedContainerId);

                return Action switch
                {
                    "connect" => $"容器 {who} 接入网络 {ActorName}",
                    "disconnect" => $"容器 {who} 断开网络 {ActorName}",
                    "create" => $"创建网络 {ActorName}",
                    "destroy" or "remove" => $"删除网络 {ActorName}",
                    _ => $"{ActionDisplay}网络 {ActorName}"
                };
            }

            return $"{ActionDisplay}{TypeDisplay} {DisplayName}";
        }
    }
}
