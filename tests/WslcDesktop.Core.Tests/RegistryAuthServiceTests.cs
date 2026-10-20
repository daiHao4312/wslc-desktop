using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Services;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// 「主机名 → 令牌」的路由（M16c）。
///
/// 重点是**公开镜像与私有镜像的分支必须分得干净**：
/// 没凭据要走匿名且不报错；有凭据但换不到要**停下**，不能退回匿名去撞（撞出来的 401 没法解释）。
/// </summary>
public class RegistryAuthServiceTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"prefs_auth_{Guid.NewGuid():N}.json");

    private RegistrySourceStore NewStore() => new(new PreferencesService(_path));

    public void Dispose()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch
        {
            // 清理失败无所谓
        }
    }

    private static Task SaveCredentialAsync(RegistrySourceStore store, string host, string user, string password)
        => store.SaveCredentialsAsync(new[] { RegistrySourceStore.CreateCredential(host, user, password) });

    [Fact]
    public async Task 没存凭据时不取令牌也不报错()
    {
        var channel = new FakeApiChannel();
        var service = new RegistryAuthService(NewStore(), channel);

        var result = await service.AcquireAsync("docker.1panel.live");

        Assert.Null(result.Token);
        Assert.Equal(string.Empty, result.Error);
        Assert.Null(result.Username);
        Assert.False(result.UsedCredential);
        Assert.Null(channel.LastUsername); // 连一次认证请求都不该发
    }

    [Fact]
    public async Task 存了凭据就用解密出来的用户名密码去换令牌()
    {
        var channel = new FakeApiChannel
        {
            OnAuthenticate = (host, user, password) => new RegistryCredentialResult
            {
                Success = true,
                Token = $"{host}|{user}|{password}",
                TokenKind = "Token"
            }
        };

        var store = NewStore();
        await SaveCredentialAsync(store, "git.example.com", "alice", "pw-123");

        var service = new RegistryAuthService(store, channel);
        var result = await service.AcquireAsync("git.example.com");

        Assert.True(result.HasToken);
        Assert.Equal("git.example.com|alice|pw-123", result.Token); // 密码真的被解出来了
        Assert.Equal("Token", result.TokenKind);
        Assert.Equal("alice", result.Username);
        Assert.True(result.UsedCredential);
    }

    [Fact]
    public async Task 凭据解不开时明确报错()
    {
        var prefs = new PreferencesService(_path);
        await prefs.SaveAsync(prefs.GetPreferences() with
        {
            RegistryCredentials = new[]
            {
                new RegistryCredential { Host = "git.example.com", Username = "alice", ProtectedPassword = "AAAA" }
            }
        });

        var channel = new FakeApiChannel();
        var result = await new RegistryAuthService(NewStore(), channel).AcquireAsync("git.example.com");

        Assert.False(result.HasToken);
        Assert.Contains("解不开", result.Error);
        Assert.Null(channel.LastUsername); // 解不开就别拿空密码去试
    }

    [Fact]
    public async Task 通道拒绝时把原因原样带出来_不退回匿名()
    {
        var channel = new FakeApiChannel
        {
            OnAuthenticate = (_, _, _) => new RegistryCredentialResult { Error = "用户名或密码不正确" }
        };

        var store = NewStore();
        await SaveCredentialAsync(store, "git.example.com", "alice", "wrong");

        var result = await new RegistryAuthService(store, channel).AcquireAsync("git.example.com");

        Assert.False(result.HasToken);
        Assert.Equal("用户名或密码不正确", result.Error);
        Assert.Equal("alice", result.Username);
    }

    [Fact]
    public async Task 通道缺失时明确说不可用而不是拿匿名去撞()
    {
        var store = NewStore();
        await SaveCredentialAsync(store, "git.example.com", "alice", "pw");

        var result = await new RegistryAuthService(store, channel: null).AcquireAsync("git.example.com");

        Assert.False(result.HasToken);
        Assert.Contains("官方 API 通道", result.Error);
    }

    [Fact]
    public async Task 换令牌时抛异常也带上原因而不是让界面崩掉()
    {
        var channel = new FakeApiChannel
        {
            OnAuthenticate = (_, _, _) => throw new InvalidOperationException("引擎会话已失效")
        };

        var store = NewStore();
        await SaveCredentialAsync(store, "git.example.com", "alice", "pw");

        var result = await new RegistryAuthService(store, channel).AcquireAsync("git.example.com");

        Assert.False(result.HasToken);
        Assert.Contains("引擎会话已失效", result.Error);
    }

    private sealed class FakeApiChannel : IWslcApiChannel
    {
        public Func<string, string, string, RegistryCredentialResult>? OnAuthenticate { get; init; }

        public string? LastUsername { get; private set; }

        public Task<WslcApiCapabilities> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WslcApiCapabilities { SdkAvailable = true });

        public Task<ApiProcessStartResult> TryStartProcessAsync(
            string containerIdOrName,
            string command,
            Action<string> onOutputLine,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ApiProcessStartResult.Unavailable("测试替身：不实现进程启动"));

        public Task<PullResult?> TryPullImageAsync(
            string imageUri,
            string? registryAuth,
            Action<PullProgress>? onProgress,
            CancellationToken cancellationToken = default)
            => Task.FromResult<PullResult?>(null);

        public Task<RegistryCredentialResult> AuthenticateAsync(
            string serverAddress,
            string username,
            string password,
            CancellationToken cancellationToken = default)
        {
            LastUsername = username;

            return Task.FromResult(OnAuthenticate?.Invoke(serverAddress, username, password)
                ?? new RegistryCredentialResult { Success = true, Token = "token", TokenKind = "Token" });
        }

        public void Dispose()
        {
        }
    }
}
