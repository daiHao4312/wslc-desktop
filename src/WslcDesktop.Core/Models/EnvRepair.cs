namespace WslcDesktop.Core.Models;

/// <summary>
/// 组件安装进度（界面用）。
///
/// 从官方 <c>InstallProgress{Component, Progress, Total}</c> 映射而来 ——
/// 把 WinRT 类型挡在 Core 的模型之外，界面才能在没有引擎的机器上被单测覆盖。
/// </summary>
public sealed record EnvRepairProgress
{
    public string Component { get; init; } = string.Empty;

    public uint Done { get; init; }

    public uint Total { get; init; }

    /// <summary>百分比；总数未知（0）时返回 0，由界面显示不确定态。</summary>
    public double Percent => Total == 0 ? 0 : Math.Min(100d, Done * 100d / Total);

    public bool IsDeterminate => Total > 0;

    public string Display => IsDeterminate
        ? $"{EnvRepairPlanner.Describe(Component)}：{Done}/{Total}（{Percent:0}%）"
        : $"正在安装 {EnvRepairPlanner.Describe(Component)}…";
}

/// <summary>组件安装/修复的结果。</summary>
public sealed record EnvRepairResult
{
    public bool Success { get; init; }

    /// <summary>给用户看的一句话（成功也说清楚做了什么）。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>本次处理的组件（原名）。</summary>
    public IReadOnlyList<string> Repaired { get; init; } = Array.Empty<string>();

    /// <summary>是否压根不需要修（组件齐全）。</summary>
    public bool Skipped { get; init; }

    public static EnvRepairResult NothingToDo(string message)
        => new() { Success = true, Skipped = true, Message = message };

    public static EnvRepairResult Failed(string message)
        => new() { Success = false, Message = message };
}

/// <summary>
/// 组件名 → 人话。
///
/// 官方枚举只有三个值（VirtualMachinePlatform / WslPackage / SdkNeedsUpdate），
/// 直接把它们丢给用户等于没解释。纯函数，可单测。
/// </summary>
public static class EnvRepairPlanner
{
    public static bool NeedsRepair(IReadOnlyList<string>? missing) => missing is { Count: > 0 };

    public static string Describe(string? component) => component switch
    {
        "VirtualMachinePlatform" => "虚拟机平台（Virtual Machine Platform）",
        "WslPackage" => "WSL 运行时包",
        "SdkNeedsUpdate" => "WSL 容器 SDK（版本过旧，需更新）",
        null or "" => "未知组件",
        _ => component
    };
}
