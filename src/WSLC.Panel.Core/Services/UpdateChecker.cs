using System.Net;
using System.Text.RegularExpressions;
using WSLC.Panel.Core.Abstractions;
using WSLC.Panel.Core.Models;
using WSLC.Panel.Core.Parsing;

namespace WSLC.Panel.Core.Services;

/// <summary>
/// 基于 GitHub Releases 的更新检查。
///
/// 设计取舍：
///   * **只检查、不自动下载替换**——非 MSIX 的自更新要处理占用中的 exe 替换、权限、签名，
///     风险远大于收益；这里给出结论 + 可点开的下载落点，由用户自己决定。
///   * 一切失败都降级成可读文案，不抛异常：更新检查失败不该影响面板其它功能。
///   * <see cref="HttpClient"/> 可注入，便于用假 handler 做单测（不联网）。
/// </summary>
public sealed class UpdateChecker : IUpdateChecker, IDisposable
{
    private const string ApiRoot = "https://api.github.com/repos/";

    private static readonly Regex RepoSlugPattern = new(
        @"^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public UpdateChecker(HttpClient? httpClient = null)
    {
        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _ownsClient = true;
        }
        else
        {
            _http = httpClient;
            _ownsClient = false;
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string? repo,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var slug = (repo ?? string.Empty).Trim().Trim('/');
        if (slug.Length == 0) return UpdateCheckResult.NotConfigured(currentVersion);

        if (!RepoSlugPattern.IsMatch(slug))
        {
            return UpdateCheckResult.Failure(currentVersion, "更新源格式应为 owner/repo（例如 octocat/Hello-World）。");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiRoot}{slug}/releases/latest");
            // GitHub API 强制要求 UA，缺失会直接 403
            request.Headers.UserAgent.ParseAdd($"WSLC-Panel/{currentVersion}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            using var response = await _http.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return UpdateCheckResult.Failure(currentVersion,
                    $"找不到仓库 {slug} 的发布（仓库名是否写错？私有仓库或无 Release 时也会这样）。");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return UpdateCheckResult.Failure(currentVersion,
                    "GitHub 拒绝了请求（多半是 API 频率限制），稍后再试。");
            }

            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failure(currentVersion, $"GitHub 返回 {(int)response.StatusCode}，检查更新失败。");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var release = GitHubReleaseParser.Parse(json);
            if (release is null)
            {
                return UpdateCheckResult.Failure(currentVersion, "无法解析 GitHub 返回的发布信息。");
            }

            var latest = release.VersionDisplay;
            if (!VersionCompare.TryParse(latest, out _, out _))
            {
                return UpdateCheckResult.Failure(currentVersion, $"发布标签 “{release.TagName}” 不是可识别的版本号。");
            }

            if (VersionCompare.IsNewer(currentVersion, latest))
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.UpdateAvailable,
                    CurrentVersion = currentVersion,
                    LatestVersion = latest,
                    DownloadUrl = release.DownloadUrl,
                    Notes = release.NotesSummary,
                    PublishedAt = release.PublishedAt,
                    Message = $"发现新版本 {latest}（当前 {currentVersion}）。"
                };
            }

            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.UpToDate,
                CurrentVersion = currentVersion,
                LatestVersion = latest,
                PublishedAt = release.PublishedAt,
                Message = $"已是最新版本（{currentVersion}）。"
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Failure(currentVersion, "检查更新超时（10 秒），请检查网络后重试。");
        }
        catch (HttpRequestException ex)
        {
            return UpdateCheckResult.Failure(currentVersion, $"无法连接 GitHub：{ex.Message}");
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Failure(currentVersion, $"检查更新失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
