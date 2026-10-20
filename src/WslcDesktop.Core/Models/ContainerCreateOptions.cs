namespace WslcDesktop.Core.Models;

/// <summary>创建容器时用户填写的参数（对应 <c>wslc run</c> 的常用旗标）。</summary>
public record ContainerCreateOptions
{
    public string Image { get; init; } = string.Empty;
    public string? Name { get; init; }

    /// <summary>覆盖镜像默认命令（可选）。</summary>
    public string? Command { get; init; }

    /// <summary>后台运行（默认 true，与面板交互预期一致）。</summary>
    public bool Detach { get; init; } = true;

    /// <summary>端口映射，形如 "8080:80"（主机端口:容器端口）。</summary>
    public IReadOnlyList<string> Ports { get; init; } = Array.Empty<string>();

    /// <summary>环境变量，形如 "KEY=VALUE"。</summary>
    public IReadOnlyList<string> EnvironmentVariables { get; init; } = Array.Empty<string>();

    /// <summary>卷挂载，形如 "volName:/data" 或 "C:\data:/data"。</summary>
    public IReadOnlyList<string> Volumes { get; init; } = Array.Empty<string>();

    public string? Network { get; init; }

    /// <summary>内存限制，形如 "512M" / "1G"。</summary>
    public string? Memory { get; init; }

    /// <summary>CPU 数，形如 "0.5" / "2"。</summary>
    public string? Cpus { get; init; }

    public string? Workdir { get; init; }

    /// <summary>主机名（<c>--hostname</c>）。</summary>
    public string? Hostname { get; init; }

    /// <summary>域名（<c>--domainname</c>）。</summary>
    public string? DomainName { get; init; }

    /// <summary>
    /// GPU 直通（<c>--gpus all</c>，M19）。
    ///
    /// 只做"全部 GPU"这一档：<c>--gpus "device=0"</c> 要求把引号带进参数值
    /// （docker 系那边按 JSON 字符串解析），wslc 是否同样对待**没有实测依据**，
    /// 与其给用户一个可能报错的选项，不如只提供有官方示例的那一种写法。
    /// 宿主机没有 GPU 时引擎会在容器启动阶段报错，界面会把引擎原文带出来。
    /// </summary>
    public bool EnableGpu { get; init; }

    /// <summary>镜像拉取策略：always | missing | never。</summary>
    public string PullPolicy { get; init; } = "missing";

    /// <summary>停止后自动删除（--rm）。</summary>
    public bool RemoveOnExit { get; init; }
}

/// <summary>
/// 创建容器的**取值规则**，和引擎的实际约束对齐。
///
/// 放在这里而不是表单里：规则是"引擎要求"，不是"页面要求"，
/// 单测能钉住，换界面也不用重写一遍。
/// </summary>
public static class ContainerOptionRules
{
    /// <summary>
    /// 校验并规范化内存限制。
    ///
    /// ⚠️ 引擎只认**大写**单位：`512M` 可以，`512m` 会被直接拒（实测依据见 PLAN §14）。
    /// 所以这里顺手转成大写，而不是把引擎那句难懂的错误丢给用户。
    /// 空串 = 不限制，算合法。
    /// </summary>
    public static bool TryNormalizeMemory(string? input, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0) return true;

        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            @"^(\d+(?:\.\d+)?)([KMGTP]?)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            error = "内存限制写法不对：只支持数字 + 单位，例如 512M、1G、2G（单位只能是 K/M/G/T/P）。";
            return false;
        }

        normalized = match.Groups[1].Value + match.Groups[2].Value.ToUpperInvariant();
        return true;
    }
}
