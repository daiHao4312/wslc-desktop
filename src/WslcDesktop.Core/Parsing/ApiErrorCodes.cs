namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 官方 <c>Microsoft.WSL.Containers</c> 的自定义 HRESULT（来自 <c>wslcsdk.h</c> 的 <c>WSLC_E_*</c>）。
///
/// 为什么值得单独建个类型：这些 HRESULT **没有本地化文本**，直接展示给用户就是
/// "无法找到与此错误代码关联的文本"这种废话。认出来之后再给一句人话，
/// 就能把"神秘失败"变成"可以行动的信息"。
/// </summary>
public static class ApiErrorCodes
{
    /// <summary>会话名被占用/保留：引擎规定会话名是机器级唯一键，且记录创建者 SID 与 PID。</summary>
    public const int SessionReserved = unchecked((int)0x80040607);

    /// <summary>会话不存在。</summary>
    public const int SessionNotFound = unchecked((int)0x8004060F);

    /// <summary>容器不存在。</summary>
    public const int ContainerNotFound = unchecked((int)0x80040603);

    /// <summary>容器未运行（需要运行态的操作）。</summary>
    public const int ContainerNotRunning = unchecked((int)0x80040605);

    public static bool IsSessionReserved(Exception exception) => exception.HResult == SessionReserved;

    public static bool IsContainerNotFound(Exception exception) => exception.HResult == ContainerNotFound;

    /// <summary>把已知 HRESULT 翻成人话；不认识就返回 null（调用方保留原文）。</summary>
    public static string? Describe(int hresult) => hresult switch
    {
        SessionReserved => "引擎会话名被占用（会话名是机器级唯一键，CLI 的会话由 wslc.exe 进程独占）",
        SessionNotFound => "引擎会话不存在",
        ContainerNotFound => "容器不存在",
        ContainerNotRunning => "容器未在运行",
        _ => null
    };
}
