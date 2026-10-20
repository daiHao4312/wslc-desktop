namespace WslcDesktop.Core.Models;

public enum UpdateApplyStatus
{
    /// <summary>新版本已就位并成功拉起。</summary>
    Applied,

    /// <summary>旧进程在超时内没有退出——**没有改动任何文件**，用户下次再试即可。</summary>
    WaitTimedOut,

    /// <summary>更新包文件不在了（被杀软隔离、被手工删掉等）。</summary>
    StagingMissing,

    /// <summary>目标目录不可写（典型：装在了 Program Files）。**没有改动任何文件**。</summary>
    TargetNotWritable,

    /// <summary>替换过程中失败，但旧版本已经恢复回去。</summary>
    RolledBack,

    /// <summary>失败且无法恢复，需要用户手工处理。</summary>
    Failed
}

/// <summary>一次原地替换的结果（在辅助进程里产生，写进日志供排查）。</summary>
public record UpdateApplyResult
{
    public UpdateApplyStatus Status { get; init; }

    public string Message { get; init; } = string.Empty;

    public string TargetPath { get; init; } = string.Empty;

    /// <summary>旧版本备份路径（用于回滚与手工恢复；没有备份时为 null）。</summary>
    public string? BackupPath { get; init; }

    /// <summary>新版是否被成功拉起。</summary>
    public bool NewProcessStarted { get; init; }

    public bool IsSuccess => Status == UpdateApplyStatus.Applied;

    /// <summary>失败时是否"什么都没动"——这种情况可以放心让用户重试。</summary>
    public bool IsNoOp =>
        Status is UpdateApplyStatus.WaitTimedOut
               or UpdateApplyStatus.TargetNotWritable
               or UpdateApplyStatus.StagingMissing;

    public static UpdateApplyResult Failure(UpdateApplyStatus status, string message, string targetPath = "") => new()
    {
        Status = status,
        Message = message,
        TargetPath = targetPath
    };
}
