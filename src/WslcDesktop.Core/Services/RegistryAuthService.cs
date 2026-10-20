using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Services;

/// <summary>
/// 「某个镜像源 → 一个能用的身份令牌」这一件事的唯一入口。
///
/// 凭据由设置页维护（<see cref="RegistrySourceStore"/> 负责加解密），真正换令牌要经官方 API 通道
/// （<see cref="IWslcApiChannel.AuthenticateAsync"/>）。把这两步收在一处，
/// 是为了让"公开镜像/私有镜像"的分支只在一处判断 —— 之前它散在拉取对话框里，
/// 多一个入口就会多一处"忘了先换令牌"。
///
/// ⚠️ 令牌**不能**做成"进站先登录一次"的登录态：参考项目实测同一个站点
/// <c>tags/list</c> 免认证而 <c>manifests/&lt;tag&gt;</c> 要认证，
/// 所以每次操作现取、随请求带（这条结论见 PLAN-v2.0 §5）。
/// </summary>
public sealed class RegistryAuthService
{
    private readonly RegistrySourceStore _store;
    private readonly IWslcApiChannel? _channel;

    /// <param name="channel">
    /// 官方 API 通道。可为 null（测试替身或未注册时），此时私有仓库会明确报"通道不可用"，
    /// 而不是悄悄拿匿名去撞 —— 撞出来的 401 比一句说明难查得多。
    /// </param>
    public RegistryAuthService(RegistrySourceStore store, IWslcApiChannel? channel = null)
    {
        _store = store;
        _channel = channel;
    }

    /// <summary>这个镜像源有没有保存凭据（设置页与拉取对话框都用它决定文案）。</summary>
    public bool HasCredential(string? host) => _store.Has(host);

    /// <summary>保存的凭据用的用户名；没有凭据返回 null。只用于界面回显。</summary>
    public string? UsernameFor(string? host) => _store.Find(host)?.Username;

    /// <summary>
    /// 为 <paramref name="host"/> 准备令牌。
    ///
    /// 三种结果要分清楚（调用方的分支完全取决于它们）：
    /// <list type="bullet">
    /// <item>没有凭据 → <c>Token</c> 为 null 且 <c>Error</c> 为空：公开镜像，继续走匿名；</item>
    /// <item>有凭据且换到了 → <c>Token</c> 非空；</item>
    /// <item>有凭据但换不到（密码错、通道不可用）→ <c>Error</c> 非空：**应当停下**，
    /// 别退回匿名 —— 那只会让用户拿到一个 401 而不是"密码不对"。</item>
    /// </list>
    /// </summary>
    public async Task<RegistryTokenResult> AcquireAsync(string host, CancellationToken cancellationToken = default)
    {
        if (!_store.TryGetLogin(host, out var login, out var error) || login is null)
        {
            return error.Length > 0
                ? new RegistryTokenResult { Error = error }
                : new RegistryTokenResult();
        }

        if (_channel is null)
        {
            return new RegistryTokenResult
            {
                Username = login.Username,
                Error = "私有仓库认证需要官方 API 通道，但当前没有注册这个通道。"
            };
        }

        try
        {
            var result = await _channel
                .AuthenticateAsync(login.Host, login.Username, login.Password, cancellationToken)
                .ConfigureAwait(false);

            return result.Success
                ? new RegistryTokenResult
                {
                    Token = result.Token,
                    TokenKind = result.TokenKind,
                    Username = login.Username
                }
                : new RegistryTokenResult
                {
                    Username = login.Username,
                    Error = result.Error
                };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 换令牌失败也要能排障：带上 HRESULT，别只留一句"失败了"
            return new RegistryTokenResult
            {
                Username = login.Username,
                Error = $"换取令牌时出错：{ExceptionText.Describe(ex)}"
            };
        }
    }
}
