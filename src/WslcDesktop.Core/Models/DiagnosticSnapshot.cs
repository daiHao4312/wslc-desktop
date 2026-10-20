namespace WslcDesktop.Core.Models;

/// <summary>诊断包里的会话条目（只放"排障有用且不敏感"的字段）。</summary>
public sealed record DiagnosticSession
{
    public string Name { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string StoragePath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public bool IsDefault { get; init; }
}

/// <summary>
/// 一次诊断采集的快照。
///
/// 刻意做成"纯数据 record"：采集（App 层，要起进程/读注册表）与排版（Core 层，可单测）分开，
/// 报告格式才能在没有引擎的 CI 上验证。
/// </summary>
public sealed record DiagnosticSnapshot
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;

    public string AppVersion { get; init; } = string.Empty;
    public string Runtime { get; init; } = string.Empty;
    public string OsDescription { get; init; } = string.Empty;

    public string EngineVersion { get; init; } = string.Empty;
    public string SessionManagerVersion { get; init; } = string.Empty;
    public IReadOnlyList<string> MissingComponents { get; init; } = Array.Empty<string>();

    /// <summary>API 通道状态（人话，例如「可用 · 会话 wslc-cli-UMS（只读附着）」）。</summary>
    public string ApiChannelState { get; init; } = string.Empty;

    /// <summary>API 通道不可用时的原因。</summary>
    public string? ApiChannelReason { get; init; }

    public IReadOnlyList<DiagnosticSession> Sessions { get; init; } = Array.Empty<DiagnosticSession>();

    public int ContainerTotal { get; init; }
    public int ContainerRunning { get; init; }
    public int ImageTotal { get; init; }
    public int VolumeTotal { get; init; }
    public int NetworkTotal { get; init; }
}
