using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Services;

/// <summary>
/// 用 <c>curl.exe</c> 问 Registry V2 的默认实现。
///
/// 两条纪律：
/// <list type="number">
/// <item><b>所有 URL 先过 <see cref="RegistryInput"/> 的校验</b>再拼 ——
/// URL 最终作为裸 argv 交给 curl.exe，这是安全边界，不是格式检查。</item>
/// <item>失败一律转成可读中文（错误信封优先，其次 curl 自己的诊断），
/// 不把原始 JSON 甩给用户。</item>
/// </list>
/// </summary>
public sealed class CurlRegistryClient : IRegistryClient
{
    private readonly IProcessRunner _runner;
    private readonly string _curlExecutable;

    public CurlRegistryClient(IProcessRunner? runner = null, string curlExecutable = "curl.exe")
    {
        _runner = runner ?? new CliProcessRunner();
        _curlExecutable = curlExecutable;
    }

    /// <summary>
    /// 本机架构在 registry 里的写法。<c>arm64</c> 原样，其余一律当 <c>amd64</c>——
    /// registry 只有这两种主流值，把 x86 / arm 之类翻译成错的反而更难查。
    /// </summary>
    public static string CurrentArchitecture => MirrorPresets.CurrentArchitecture;

    public async Task<RegistryTagsResult> GetTagsAsync(
        string host,
        string repository,
        string? bearerToken = null,
        CancellationToken cancellationToken = default)
    {
        if (!RegistryInput.IsValidHost(host))
        {
            return new RegistryTagsResult { Error = $"registry 地址不合法：{host}" };
        }

        if (!RegistryInput.IsValidRepository(repository))
        {
            return new RegistryTagsResult { Error = $"仓库名不合法：{repository}" };
        }

        var auth = await ResolveAuthAsync(host, repository, bearerToken, cancellationToken).ConfigureAwait(false);
        if (auth.Error.Length > 0) return new RegistryTagsResult { Error = auth.Error };

        var url = RegistryInput.TagsUrl(host, repository);
        var response = await FetchAsync(url, auth.Token, cancellationToken).ConfigureAwait(false);

        if (!response.Success) return new RegistryTagsResult { Error = response.Error };

        if (!RegistryResponseParser.TryParseTags(response.Body, out var tags, out var parseError))
        {
            return new RegistryTagsResult { Error = parseError };
        }

        return new RegistryTagsResult
        {
            Success = true,
            Tags = RegistryResponseParser.SortTags(tags)
        };
    }

    public async Task<RegistrySizeResult> GetSizeAsync(
        string host,
        string repository,
        string reference,
        string architecture,
        string? bearerToken = null,
        CancellationToken cancellationToken = default)
    {
        if (!RegistryInput.IsValidHost(host))
        {
            return new RegistrySizeResult { Error = $"registry 地址不合法：{host}" };
        }

        if (!RegistryInput.IsValidRepository(repository))
        {
            return new RegistrySizeResult { Error = $"仓库名不合法：{repository}" };
        }

        if (!RegistryInput.IsValidReference(reference))
        {
            return new RegistrySizeResult { Error = $"tag 不合法：{reference}" };
        }

        var auth = await ResolveAuthAsync(host, repository, bearerToken, cancellationToken).ConfigureAwait(false);
        if (auth.Error.Length > 0) return new RegistrySizeResult { Error = auth.Error };

        // 第一次：可能是多架构 index，也可能是单架构 manifest —— 用"能不能挑出平台"来区分
        var first = await FetchAsync(
            RegistryInput.ManifestUrl(host, repository, reference),
            auth.Token,
            cancellationToken).ConfigureAwait(false);

        if (!first.Success) return new RegistrySizeResult { Error = first.Error };

        // 多架构 index：挑本机那一份，再要一次
        if (RegistryResponseParser.TryPickPlatformManifest(first.Body, architecture, out var digest, out _))
        {
            var second = await FetchAsync(
                RegistryInput.ManifestUrl(host, repository, digest),
                auth.Token,
                cancellationToken).ConfigureAwait(false);

            if (!second.Success) return new RegistrySizeResult { Error = second.Error };

            return RegistryResponseParser.TrySumDownloadBytes(second.Body, out var sized, out var sizeError)
                ? new RegistrySizeResult { Success = true, Bytes = sized }
                : new RegistrySizeResult { Error = sizeError };
        }

        return RegistryResponseParser.TrySumDownloadBytes(first.Body, out var bytes, out var error)
            ? new RegistrySizeResult { Success = true, Bytes = bytes }
            : new RegistrySizeResult { Error = error };
    }

    /// <summary>
    /// 决定这次请求带什么令牌。
    ///
    /// 用户填了私有仓库凭据（<paramref name="provided"/>）就**直接用** —— 不再去换匿名令牌，
    /// 因为私有仓库的匿名令牌根本换不到，多跑一次只会把真正的失败原因盖掉。
    /// </summary>
    private async Task<(string Token, string Error)> ResolveAuthAsync(
        string host,
        string repository,
        string? provided,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(provided)) return (provided.Trim(), string.Empty);

        return await TryResolveAnonymousTokenAsync(host, repository, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 需要令牌的站点先换一个**匿名令牌**（Docker Hub 对公开镜像就是这条路）。
    ///
    /// 只对**端点形状已知**的站点做（见 <see cref="RegistryDefaults.AnonymousTokenEndpointFor"/>）：
    /// 通用做法要先发一次请求、从 <c>WWW-Authenticate</c> 头解析 realm 再重试，
    /// 多一轮往返还要解析响应头；Docker Hub 的端点固定，写死更稳也少一次请求。
    ///
    /// 返回 <c>Token</c> 为空且 <c>Error</c> 为空 = 这个站点不需要令牌。
    /// </summary>
    private async Task<(string Token, string Error)> TryResolveAnonymousTokenAsync(
        string host,
        string repository,
        CancellationToken cancellationToken)
    {
        var template = RegistryDefaults.AnonymousTokenEndpointFor(host);
        if (template is null) return (string.Empty, string.Empty);

        var url = string.Format(template, repository);
        var response = await FetchAsync(url, bearerToken: null, cancellationToken).ConfigureAwait(false);

        if (!response.Success)
        {
            return (string.Empty, $"取 {host} 的匿名令牌失败：{response.Error}");
        }

        return RegistryResponseParser.TryParseAuthToken(response.Body, out var token, out var error)
            ? (token, string.Empty)
            : (string.Empty, error);
    }

    private async Task<(bool Success, string Body, string Error)> FetchAsync(
        string url,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        try
        {
            // --fail-with-body：HTTP 出错时正文照样打出来，所以"失败了"与"为什么"能同时拿到
            var result = await _runner.RunAsync(
                _curlExecutable,
                RegistryInput.CurlJsonArguments(url, bearerToken: bearerToken),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                return (true, result.StandardOutput, string.Empty);
            }

            return (false, result.StandardOutput,
                RegistryResponseParser.ExplainFailure(result.StandardOutput, result.StandardError));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }
}
