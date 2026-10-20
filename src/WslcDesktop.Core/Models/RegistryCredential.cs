using System.Text.Json.Serialization;

namespace WslcDesktop.Core.Models;

/// <summary>
/// 用户为某个私有镜像源保存的登录凭据（随 <see cref="AppPreferences"/> 落盘）。
///
/// ⚠️ <see cref="ProtectedPassword"/> 是 **DPAPI 密文**（Base64），不是明文：
/// 明文只在内存里存在，加解密的唯一入口是 <c>SecretProtector</c>。
/// 别处不许自己解，更不许往这里塞明文 —— prefs.json 是明文 JSON，
/// 写进明文密码等于把它交给任何能读到该文件的进程。
/// </summary>
public sealed record RegistryCredential
{
    /// <summary>纯主机名（小写、无协议头），与"镜像源"栏里的写法一致。</summary>
    public string Host { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    /// <summary>DPAPI 密文（Base64）。空串表示这条凭据没存密码。</summary>
    public string ProtectedPassword { get; init; } = string.Empty;

    /// <summary>
    /// 界面上的回显：只有主机名与用户名。
    ///
    /// 刻意**不**给密码位掩码（<c>••••••</c>）：那会让人以为"密码存着了、只是看不见"，
    /// 而它本来就是密文，长度也不是秘密 —— 少一个容易误解的 UI。
    /// </summary>
    [JsonIgnore]
    public string Display => string.IsNullOrEmpty(Username) ? Host : $"{Host}（{Username}）";
}

/// <summary>
/// 解密后的登录信息。只在内存里活着，用完即弃，绝不写回任何文件。
/// </summary>
public sealed record RegistryLogin
{
    /// <summary>凭据里记着的主机名（与用户当初填的一致）。</summary>
    public string Host { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;
}
