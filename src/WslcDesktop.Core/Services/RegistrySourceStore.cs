using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Services;

/// <summary>
/// 镜像源与凭据的**持久化层**：只负责把 prefs.json 里的数据读出来、写回去，以及密码的加解密。
///
/// 业务规则（默认唯一、默认不可停用、内置不可删……）一律在
/// <see cref="RegistrySourceService"/> 里，别往这里塞 —— 否则又会长成"一半规则在界面、一半在存储"。
///
/// 两件事只有这里做：
/// <list type="number">
/// <item>**加解密**：写入时明文 → DPAPI 密文，读取时密文 → <see cref="RegistryLogin"/>；</item>
/// <item>**旧格式兜底**：v1.1.1 之前用的是 <c>CustomRegistries</c> + <c>HiddenRegistries</c> 两个列表，
/// 读取时合成到 <see cref="RegistrySourcePreference"/>，任何一次写入都把它清空（即迁移完成）。</item>
/// </list>
/// </summary>
public sealed class RegistrySourceStore
{
    private readonly IPrefsService _prefs;

    public RegistrySourceStore(IPrefsService prefs) => _prefs = prefs;

    /// <summary>镜像源的原始设置（已把旧格式合并进来）。视图合成交给 service。</summary>
    public IReadOnlyList<RegistrySourcePreference> SourcePreferences
        => Normalize(_prefs.GetPreferences());

    /// <summary>保存的默认镜像源（可能指向已不存在的源，service 会自愈）。</summary>
    public string StoredDefaultRegistry => _prefs.GetPreferences().DefaultRegistry;

    /// <summary>全部凭据（密文形态，保持添加次序）。</summary>
    public IReadOnlyList<RegistryCredential> Credentials => _prefs.GetPreferences().RegistryCredentials;

    /// <summary>这个主机名有没有存凭据。</summary>
    public bool Has(string? host) => Find(host) is not null;

    /// <summary>按主机名找一条凭据（别名算同一条）。</summary>
    public RegistryCredential? Find(string? host)
    {
        var canonical = RegistryInput.CanonicalHost(host);
        if (canonical.Length == 0) return null;

        foreach (var credential in Credentials)
        {
            if (RegistryInput.CanonicalHost(credential.Host) == canonical) return credential;
        }

        return null;
    }

    /// <summary>
    /// 取出可用的登录信息。
    ///
    /// <paramref name="error"/> 非空 = **确实存了凭据但解不开**（换了 Windows 账户、
    /// prefs.json 从别的机器拷来、文件被改坏）。这种情况必须让用户看到并重填，
    /// 不能当成"没凭据"静默退回匿名 —— 那会让用户对着一个 401 猜半天。
    /// </summary>
    public bool TryGetLogin(string? host, out RegistryLogin? login, out string error)
    {
        login = null;
        error = string.Empty;

        var found = Find(host);
        if (found is null) return false;

        // 密文为空 = 这条凭据本来就没存密码（手改过 prefs.json 才会这样），不算解不开
        var password = found.ProtectedPassword.Length == 0
            ? string.Empty
            : SecretProtector.Unprotect(found.ProtectedPassword);

        if (password is null)
        {
            error = $"「{found.Host}」的凭据解不开（多半是换了 Windows 账户，或 prefs.json 来自别的机器）。" +
                    "请在「设置 → 镜像源 → 编辑」里重新填写密码。";
            return false;
        }

        login = new RegistryLogin
        {
            Host = found.Host,
            Username = found.Username,
            Password = password
        };

        return true;
    }

    /// <summary>
    /// 写入镜像源设置。
    ///
    /// <param name="defaultHost"/> 一并写入：默认源是这一份列表的派生属性，
    /// 分开写就有机会出现"默认指向一个刚被删掉的源"。
    /// </summary>
    public async Task SaveSourcesAsync(
        IReadOnlyList<RegistrySourcePreference> sources,
        string defaultHost,
        CancellationToken cancellationToken = default)
    {
        var next = _prefs.GetPreferences() with
        {
            RegistrySources = sources,
            DefaultRegistry = defaultHost,

            // 旧字段写空 = 迁移完成，之后它俩不再参与任何判断
            CustomRegistries = Array.Empty<string>(),
            HiddenRegistries = Array.Empty<string>()
        };

        await SaveVerifiedAsync(
            next,
            reloaded => SameSet(reloaded.RegistrySources.Select(s => s.Host), sources.Select(s => s.Host))
                        && string.Equals(reloaded.DefaultRegistry, defaultHost, StringComparison.OrdinalIgnoreCase),
            "镜像源没能写入 prefs.json（写盘失败）。请检查该文件是否被其他程序占用，或目录权限是否足够。",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>写入凭据列表（内部会加密，调用方给的是明文密码）。</summary>
    public async Task SaveCredentialsAsync(
        IReadOnlyList<RegistryCredential> credentials,
        CancellationToken cancellationToken = default)
    {
        var next = _prefs.GetPreferences() with { RegistryCredentials = credentials };

        await SaveVerifiedAsync(
            next,
            reloaded => SameSet(reloaded.RegistryCredentials.Select(c => c.Host), credentials.Select(c => c.Host)),
            "凭据没能写入 prefs.json（写盘失败）。请检查该文件是否被其他程序占用，或目录权限是否足够。",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把明文密码加密成一条凭据记录（唯一的加密入口）。</summary>
    public static RegistryCredential CreateCredential(string host, string username, string password)
        => new()
        {
            Host = RegistryInput.NormalizeHost(host),
            Username = username.Trim(),
            ProtectedPassword = SecretProtector.Protect(password)
        };

    /// <summary>
    /// 写盘 + **回读确认**。
    ///
    /// <c>PreferencesService.SaveAsync</c> 把写盘异常吞掉了（既有行为），对普通偏好只是"设置没生效"，
    /// 对镜像源/密码却是"界面说存好了、其实没落盘 —— 用户每次都要重填还不明所以"。
    /// </summary>
    private async Task SaveVerifiedAsync(
        AppPreferences next,
        Func<AppPreferences, bool> verify,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        await _prefs.SaveAsync(next, cancellationToken).ConfigureAwait(false);

        var reloaded = await _prefs.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!verify(reloaded)) throw new InvalidOperationException(failureMessage);
    }

    private static bool SameSet(IEnumerable<string> left, IEnumerable<string> right)
    {
        var a = new HashSet<string>(left.Select(RegistryInput.CanonicalHost), StringComparer.OrdinalIgnoreCase);
        var b = new HashSet<string>(right.Select(RegistryInput.CanonicalHost), StringComparer.OrdinalIgnoreCase);
        return a.SetEquals(b);
    }

    /// <summary>
    /// 新格式优先；新格式为空时用旧字段合成（一次性兼容）。
    ///
    /// 合成顺序刻意保持"自定义在前、被停用的内置在后"，与用户当初的操作历史一致。
    /// </summary>
    private static IReadOnlyList<RegistrySourcePreference> Normalize(AppPreferences prefs)
    {
        if (prefs.RegistrySources.Count > 0) return prefs.RegistrySources;
        if (prefs.CustomRegistries.Count == 0 && prefs.HiddenRegistries.Count == 0)
        {
            return Array.Empty<RegistrySourcePreference>();
        }

        var list = new List<RegistrySourcePreference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var host in prefs.CustomRegistries)
        {
            var normalized = RegistryInput.NormalizeHost(host);
            if (!seen.Add(RegistryInput.CanonicalHost(normalized))) continue;

            list.Add(new RegistrySourcePreference
            {
                Host = normalized,
                IsCustom = true,
                Enabled = true
            });
        }

        foreach (var host in prefs.HiddenRegistries)
        {
            var normalized = RegistryInput.NormalizeHost(host);
            if (!seen.Add(RegistryInput.CanonicalHost(normalized))) continue;

            list.Add(new RegistrySourcePreference
            {
                Host = normalized,
                IsCustom = !MirrorPresets.IsPreset(normalized),
                Enabled = false
            });
        }

        return list;
    }
}
