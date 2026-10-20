using System.Runtime.InteropServices;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;
using WslcDesktop.Core.Services;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// M14 官方 API 通道：能力探测、会话附着、拉取路由与回落策略。
///
/// 这些断言的价值在于——它们全部<b>不需要引擎</b>就能跑（沙箱与 CI 里都能验证），
/// 而恰恰是"引擎不可用时会不会误报可用""失败后会不会白重下"这类最容易出错的判断。
/// </summary>
public class WslcApiChannelTests
{
    // ---------------- 替身 ----------------

    private sealed class FakeApiSession : IWslcApiSession
    {
        public string SessionName { get; init; } = "wslc-cli-1234";
        public string StorageRoot { get; init; } = @"C:\Users\demo\AppData\Local";
        public bool ReadOnlyAttach { get; init; }
        public IReadOnlyList<string> ImageNames { get; init; } = new[] { "alpine:latest" };

        public Func<string, string?, Action<ApiImageProgress>?, CancellationToken, Task>? PullBehavior { get; set; }

        public int PullCalls { get; private set; }
        public string? LastRegistryAuth { get; private set; }
        public bool Disposed { get; private set; }

        public IReadOnlyList<string> GetImageNames() => ImageNames;

        /// <summary>测试注入：模拟"在容器里起进程"。默认判为起不来（走回落分支）。</summary>
        public Func<string, string, Action<string>, IApiContainerProcess?>? StartProcessBehavior { get; set; }

        public IApiContainerProcess TryStartProcess(string containerIdOrName, string command, Action<string> onOutputLine)
            => StartProcessBehavior?.Invoke(containerIdOrName, command, onOutputLine)
               ?? throw new InvalidOperationException("测试替身：不实现进程启动");

        public Task PullImageAsync(string imageUri, string? registryAuth, Action<ApiImageProgress>? onProgress, CancellationToken cancellationToken = default)
        {
            PullCalls++;
            LastRegistryAuth = registryAuth;
            return PullBehavior is null
                ? Task.CompletedTask
                : PullBehavior(imageUri, registryAuth, onProgress, cancellationToken);
        }

        public Func<string, string, string, RegistryCredentialResult>? AuthBehavior { get; set; }

        /// <summary>按调用顺序记录收到的 (server, 用户名, 密码)。</summary>
        public List<(string Server, string User, string Password)> AuthCalls { get; } = new();

        public RegistryCredentialResult Authenticate(string serverAddress, string username, string password)
        {
            AuthCalls.Add((serverAddress, username, password));

            return AuthBehavior?.Invoke(serverAddress, username, password)
                ?? new RegistryCredentialResult { Success = true, Token = "tok-from-engine", TokenKind = "Token" };
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeSessionFactory : IWslcApiSessionFactory
    {
        public Func<SessionModel, IWslcApiSession?> Behavior { get; set; } = _ => null;
        public string FailReason { get; set; } = "测试：附着失败";
        public SessionModel? LastTarget { get; private set; }
        public int Calls { get; private set; }

        public IWslcApiSession? TryAttach(SessionModel target, out string? reason)
        {
            Calls++;
            LastTarget = target;
            var session = Behavior(target);
            reason = session is null ? FailReason : null;
            return session;
        }
    }

    private sealed class FakeSdkProbe : IWslcSdkProbe
    {
        public WslcSdkInfo Result { get; set; } = new() { Available = true, SessionManagerVersion = "3.0.3" };
        public int Calls { get; private set; }

        public WslcSdkInfo Query()
        {
            Calls++;
            return Result;
        }
    }

    // ---------------- 工具 ----------------

    private static SessionModel CliSession() => new() { Id = 1, DisplayName = "wslc-cli-1234", CreatorPid = 999 };
    private static SessionModel OtherSession() => new() { Id = 2, DisplayName = "某个自定义会话", CreatorPid = 888 };

    /// <summary>造一个"探测能通过"的通道，返回用到的各替身。</summary>
    private static (WslcApiChannel Channel, FakeApiSession Session, FakeSessionFactory Factory, FakeSdkProbe Probe)
        ReadyChannel(Func<bool>? isEnabled = null, params SessionModel[] sessions)
    {
        var session = new FakeApiSession();
        var factory = new FakeSessionFactory { Behavior = _ => session };
        var probe = new FakeSdkProbe();
        var list = sessions.Length > 0 ? sessions : new[] { CliSession() };

        var channel = new WslcApiChannel(
            _ => Task.FromResult<IReadOnlyList<SessionModel>>(list),
            factory,
            probe,
            isEnabled);

        return (channel, session, factory, probe);
    }

    // ---------------- 探测 ----------------

    [Fact]
    public async Task 设置里关掉通道_不启用且每次询问都重新判断()
    {
        var enabled = false;
        var (channel, _, _, _) = ReadyChannel(() => enabled);

        var first = await channel.ProbeAsync();
        Assert.False(first.Enabled);
        Assert.False(first.CanPullWithProgress);
        Assert.Contains("UseApiChannel", first.Reason);

        // 关闭状态**不能**被缓存：用户在设置里打开后必须立刻生效
        enabled = true;
        var second = await channel.ProbeAsync();
        Assert.True(second.Enabled);
        Assert.True(second.CanPullWithProgress);
    }

    [Fact]
    public async Task 引擎不可用_通道不可用且原因可读()
    {
        var factory = new FakeSessionFactory();
        var probe = new FakeSdkProbe { Result = new WslcSdkInfo { Available = false, Error = "找不到 wslc" } };
        var channel = new WslcApiChannel(_ => Task.FromResult<IReadOnlyList<SessionModel>>(Array.Empty<SessionModel>()), factory, probe);

        var capabilities = await channel.ProbeAsync();

        Assert.False(capabilities.SdkAvailable);
        Assert.False(capabilities.CanPullWithProgress);
        Assert.Contains("找不到 wslc", capabilities.Reason);
    }

    [Fact]
    public async Task 组件缺失_通道不可用并报告缺哪些组件()
    {
        var probe = new FakeSdkProbe
        {
            Result = new WslcSdkInfo { Available = true, SessionManagerVersion = "3.0.3", MissingComponents = new[] { "VirtualMachinePlatform" } }
        };
        var channel = new WslcApiChannel(_ => Task.FromResult<IReadOnlyList<SessionModel>>(Array.Empty<SessionModel>()), new FakeSessionFactory(), probe);

        var capabilities = await channel.ProbeAsync();

        Assert.True(capabilities.SdkAvailable);
        Assert.False(capabilities.CanPullWithProgress);
        Assert.Equal(new[] { "VirtualMachinePlatform" }, capabilities.MissingComponents);
        Assert.Contains("VirtualMachinePlatform", capabilities.Reason);
    }

    [Fact]
    public async Task 没有会话_通道不可用_且不会去附着()
    {
        var factory = new FakeSessionFactory();
        var channel = new WslcApiChannel(
            _ => Task.FromResult<IReadOnlyList<SessionModel>>(Array.Empty<SessionModel>()),
            factory,
            new FakeSdkProbe());

        var capabilities = await channel.ProbeAsync();

        Assert.False(capabilities.CanPullWithProgress);
        Assert.Equal("未发现可附着的引擎会话", capabilities.Reason);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task 附着失败_通道不可用且不假装成功()
    {
        var factory = new FakeSessionFactory { FailReason = "未找到会话在磁盘上的存储目录" };
        var channel = new WslcApiChannel(_ => Task.FromResult<IReadOnlyList<SessionModel>>(new[] { CliSession() }), factory, new FakeSdkProbe());

        var capabilities = await channel.ProbeAsync();

        Assert.Null(capabilities.AttachedSessionName);
        Assert.False(capabilities.CanPullWithProgress);
        Assert.Contains("存储目录", capabilities.Reason);
    }

    [Fact]
    public async Task 优先附着引擎CLI自己的会话()
    {
        var factory = new FakeSessionFactory();
        var channel = new WslcApiChannel(
            _ => Task.FromResult<IReadOnlyList<SessionModel>>(new[] { OtherSession(), CliSession() }),
            factory,
            new FakeSdkProbe());

        await channel.ProbeAsync();

        // 面板拉取的镜像必须落进 CLI 也看得见的那个会话
        Assert.Equal("wslc-cli-1234", factory.LastTarget?.DisplayName);
    }

    [Fact]
    public async Task 只有自定义会话时也接受它()
    {
        var factory = new FakeSessionFactory { Behavior = _ => new FakeApiSession { SessionName = "某个自定义会话" } };
        var channel = new WslcApiChannel(_ => Task.FromResult<IReadOnlyList<SessionModel>>(new[] { OtherSession() }), factory, new FakeSdkProbe());

        var capabilities = await channel.ProbeAsync();

        Assert.Equal("某个自定义会话", capabilities.AttachedSessionName);
        Assert.True(capabilities.CanPullWithProgress);
    }

    [Fact]
    public async Task 探测结果会缓存_不会每次刷新都重开会话()
    {
        var probe = new FakeSdkProbe();
        var factory = new FakeSessionFactory { Behavior = _ => new FakeApiSession() };
        var listCalls = 0;
        var channel = new WslcApiChannel(
            _ => { listCalls++; return Task.FromResult<IReadOnlyList<SessionModel>>(new[] { CliSession() }); },
            factory,
            probe);

        await channel.ProbeAsync();
        await channel.ProbeAsync();
        await channel.ProbeAsync();

        Assert.Equal(1, probe.Calls);
        Assert.Equal(1, factory.Calls);
        Assert.Equal(1, listCalls);
    }

    // ---------------- 拉取路由 ----------------

    [Fact]
    public async Task 通道不可用时拉取返回null_交给调用方回落CLI()
    {
        // 探测通不过（附着一律失败）→ 通道不可用
        var factory = new FakeSessionFactory();
        var channel = new WslcApiChannel(
            _ => Task.FromResult<IReadOnlyList<SessionModel>>(new[] { CliSession() }),
            factory,
            new FakeSdkProbe());

        var result = await channel.TryPullImageAsync("alpine", null, null);

        Assert.Null(result);
        Assert.Equal(1, factory.Calls);   // 确实探测过（尝试附着但失败）
    }

    [Fact]
    public async Task 通道可用时走API_并把真实字节映射成确定进度()
    {
        var (channel, session, _, _) = ReadyChannel();
        session.PullBehavior = (_, _, onProgress, _) =>
        {
            onProgress?.Invoke(new ApiImageProgress
            {
                ImageId = "9a02b1d1b3f5",
                Status = "Downloading",
                DownloadedBytes = 50,
                TotalBytes = 200
            });
            return Task.CompletedTask;
        };

        var seen = new List<PullProgress>();
        var result = await channel.TryPullImageAsync("alpine:3.20", null, seen.Add);

        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal("alpine:3.20", result.ImageReference);
        Assert.Equal(1, session.PullCalls);

        var progress = Assert.Single(seen);
        Assert.Equal("alpine:3.20", progress.ImageReference);
        Assert.Equal("9a02b1d1b3f5", progress.LayerId);
        Assert.Equal("Downloading", progress.Status);
        Assert.Equal(50UL, progress.DownloadedBytes);
        Assert.Equal(200UL, progress.TotalBytes);
        // 关键：有百分比，界面才会从不确定进度条切成确定进度条
        Assert.Equal(25d, progress.Percent);
    }

    [Fact]
    public async Task 私有仓库凭据透传到API通道()
    {
        var (channel, session, _, _) = ReadyChannel();

        await channel.TryPullImageAsync("registry.corp.local/app:1.0", "Bearer abc", null);

        Assert.Equal("Bearer abc", session.LastRegistryAuth);
    }

    [Fact]
    public async Task 一行进度都没出来就失败_返回null以便安全回落CLI()
    {
        var (channel, session, _, _) = ReadyChannel();
        session.PullBehavior = (_, _, _, _) => Task.FromException(new InvalidOperationException("引擎拒绝"));

        var result = await channel.TryPullImageAsync("alpine", null, null);

        Assert.Null(result);
        Assert.Equal(1, session.PullCalls);
    }

    [Fact]
    public async Task 已经下过一段才失败_返回失败结果而不是回落_避免白重下()
    {
        var (channel, session, _, _) = ReadyChannel();
        session.PullBehavior = (_, _, onProgress, _) =>
        {
            onProgress?.Invoke(new ApiImageProgress { Status = "Downloading", DownloadedBytes = 10, TotalBytes = 100 });
            return Task.FromException(new InvalidOperationException("网络中断"));
        };

        var result = await channel.TryPullImageAsync("alpine", null, null);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Contains("网络中断", result.Message);
    }

    [Fact]
    public async Task 取消异常向上传递_不被当成通道不可用()
    {
        var (channel, session, _, _) = ReadyChannel();
        using var cts = new CancellationTokenSource();
        session.PullBehavior = (_, _, _, _) =>
        {
            cts.Cancel();
            return Task.FromException(new OperationCanceledException());
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => channel.TryPullImageAsync("alpine", null, null, cts.Token));
    }

    [Fact]
    public async Task 空引用直接返回null_不触碰引擎()
    {
        var (channel, session, _, _) = ReadyChannel();

        Assert.Null(await channel.TryPullImageAsync("   ", null, null));
        Assert.Equal(0, session.PullCalls);
    }

    // ---------------- 私有仓库认证（M16b） ----------------

    [Fact]
    public async Task 认证走引擎的_Authenticate()
    {
        var (channel, session, _, _) = ReadyChannel();

        var result = await channel.AuthenticateAsync("docker.io", "alice", "s3cret");

        Assert.True(result.Success, result.Error);
        Assert.Equal("tok-from-engine", result.Token);
        Assert.Equal("Token", result.TokenKind);

        // 认证要发给 API 端点，不是发给网站
        var call = Assert.Single(session.AuthCalls);
        Assert.Equal("registry-1.docker.io", call.Server);
        Assert.Equal("alice", call.User);
        Assert.Equal("s3cret", call.Password);
    }

    [Fact]
    public async Task 通道不可用时认证如实报错而不是静默失败()
    {
        var factory = new FakeSessionFactory();   // 附着一律失败 → 通道不可用
        var channel = new WslcApiChannel(
            _ => Task.FromResult<IReadOnlyList<SessionModel>>(new[] { CliSession() }),
            factory,
            new FakeSdkProbe());

        var result = await channel.AuthenticateAsync("docker.io", "alice", "s3cret");

        Assert.False(result.Success);
        Assert.Contains("官方 API 通道", result.Error);
    }

    [Fact]
    public async Task 引擎没给令牌时提示可能是凭据不对()
    {
        var (channel, session, _, _) = ReadyChannel();
        session.AuthBehavior = (_, _, _) => new RegistryCredentialResult
        {
            Success = false,
            Error = "引擎没有返回身份令牌（用户名或密码可能不对）"
        };

        var result = await channel.AuthenticateAsync("docker.io", "alice", "wrong");

        Assert.False(result.Success);
        Assert.Contains("用户名或密码", result.Error);
    }

    [Fact]
    public async Task 组合通道把私有仓库令牌交给_API_而不是_CLI()
    {
        var cli = new FakeWslcClient();
        var (channel, session, _, _) = ReadyChannel();
        var composite = new CompositeWslcClient(cli, channel);

        await composite.PullImageAsync("myregistry.local/team/app:1.0", "secret-token");

        Assert.Equal("secret-token", session.LastRegistryAuth);
        Assert.Empty(cli.PullRequests);   // 通道能用就不回落 CLI
    }

    [Fact]
    public async Task 令牌不会被拼进_CLI_命令行()
    {
        // CLI 的 pull 没有凭据参数：令牌只能走 API 通道。
        // 这条断言守的是"别把密钥泄进进程命令行"（进程列表里谁都能看见）。
        var runner = new RecordingProcessRunner();
        var cli = new CliWslcClient(runner);

        await cli.PullImageAsync("myregistry.local/team/app:1.0", "secret-token");

        var call = Assert.Single(runner.Calls);
        Assert.DoesNotContain("secret-token", call);
        Assert.Contains("pull myregistry.local/team/app:1.0", call);
    }

    // ---------------- 模型映射 ----------------

    [Fact]
    public void 总量未知时不给百分比_界面保持不确定态()
    {
        var progress = WslcApiChannel.ToPullProgress(
            "alpine",
            new ApiImageProgress { Status = "Pulling", DownloadedBytes = 1234, TotalBytes = 0 });

        Assert.Null(progress.Percent);
        Assert.False(progress.IsTerminal);
        Assert.Equal("Pulling", progress.Status);
    }

    [Fact]
    public void 百分比按总量收敛在0到100之间()
    {
        var over = new ApiImageProgress { Status = "Downloading", DownloadedBytes = 300, TotalBytes = 200 };
        var under = new ApiImageProgress { Status = "Downloading", DownloadedBytes = 0, TotalBytes = 200 };

        Assert.Equal(100d, over.Percent);
        Assert.Equal(0d, under.Percent);
    }

    [Fact]
    public void 终态判定只认Complete()
    {
        Assert.True(new ApiImageProgress { Status = "Complete" }.IsTerminal);
        Assert.True(new ApiImageProgress { Status = "complete" }.IsTerminal);
        Assert.False(new ApiImageProgress { Status = "Extracting" }.IsTerminal);
    }

    // ---------------- 组合通道路由 ----------------

    [Fact]
    public async Task 组合通道_API拿不到时回落CLI()
    {
        var cli = new FakeWslcClient
        {
            PullBehavior = (reference, _, _) => Task.FromResult(new PullResult { Success = true, Message = $"CLI 拉的 {reference}" })
        };
        var factory = new FakeSessionFactory();   // 附着一律失败
        var channel = new WslcApiChannel(_ => Task.FromResult<IReadOnlyList<SessionModel>>(new[] { CliSession() }), factory, new FakeSdkProbe());
        var composite = new CompositeWslcClient(cli, channel);

        var result = await composite.PullImageAsync("alpine");

        Assert.True(result.Success);
        Assert.Contains("CLI 拉的", result.Message);
        Assert.Equal(new[] { "alpine" }, cli.PullRequests);
    }

    [Fact]
    public async Task 组合通道_API拿得到时不再走CLI()
    {
        var cli = new FakeWslcClient
        {
            PullBehavior = (_, _, _) => Task.FromResult(new PullResult { Success = true, Message = "CLI" })
        };
        var (channel, session, _, _) = ReadyChannel();
        var composite = new CompositeWslcClient(cli, channel);

        var result = await composite.PullImageAsync("alpine");

        Assert.True(result.Success);
        Assert.Equal(1, session.PullCalls);
        Assert.Empty(cli.PullRequests);
    }

    [Fact]
    public async Task 组合通道_没接API通道时行为与以前一致()
    {
        var cli = new FakeWslcClient
        {
            PullBehavior = (_, _, _) => Task.FromResult(new PullResult { Success = true, Message = "CLI" })
        };
        var composite = new CompositeWslcClient(cli);

        var result = await composite.PullImageAsync("alpine");

        Assert.True(result.Success);
        Assert.Equal(new[] { "alpine" }, cli.PullRequests);
    }

    // ---------------- 会话附着（默认实现） ----------------

    [Fact]
    public void 目录存在但没有_storage_vhdx_时拒绝附着()
    {
        // 只存在空目录说明那不是引擎在用的会话（可能是残留），交给 Session 会造出新会话
        var name = "wslc-cli-empty-" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "wslcdesktop-tests-" + Guid.NewGuid().ToString("N"));
        var sessionDir = Path.Combine(root, name);
        Directory.CreateDirectory(sessionDir);

        try
        {
            var created = 0;
            var factory = new WslcApiSessionFactory((_, _) =>
            {
                created++;
                return new FakeApiSession();
            });

            var session = factory.TryAttach(
                new SessionModel { Id = 1, DisplayName = name, StoragePath = Path.Combine(sessionDir, "storage.vhdx") },
                out var reason);

            Assert.Null(session);
            Assert.Equal(0, created);
            Assert.Contains("storage.vhdx", reason);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 清理失败无所谓 */ }
        }
    }

    [Fact]
    public void 附着失败时把_HRESULT_带进原因里()
    {
        // 排障需要：以前只显示"无法找到与此错误代码关联的文本"，没有码就无从下手
        var name = "wslc-cli-test-" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "wslcdesktop-tests-" + Guid.NewGuid().ToString("N"));
        var sessionDir = Path.Combine(root, name);
        Directory.CreateDirectory(sessionDir);
        File.WriteAllBytes(Path.Combine(sessionDir, "storage.vhdx"), new byte[16]);

        try
        {
            // 形状照着真机那次的来：WinRT 投影抛的是带 HRESULT 的异常，
            // 真正的原因往往在内层（投影异常/引擎内部异常）。
            var factory = new WslcApiSessionFactory((_, _) => throw new EngineFailureException(
                "引擎拒绝了",
                unchecked((int)0x80070005),
                new InvalidOperationException("底层拒绝")));

            Assert.Null(factory.TryAttach(
                new SessionModel { Id = 1, DisplayName = name, StoragePath = Path.Combine(sessionDir, "storage.vhdx") },
                out var reason));

            Assert.Contains("引擎拒绝了", reason);
            Assert.Contains("HRESULT 0x80070005", reason);
            Assert.Contains("内层：底层拒绝", reason);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 清理失败无所谓 */ }
        }
    }
    /// <summary>
    /// 形状照着真机那次的来：引擎/投影层抛出来的异常带 HRESULT 与内层异常。
    ///
    /// 用自定义类型而不是 <c>COMException</c>：后者没有 (消息, 错误码, 内层) 这个重载，
    /// 而 <c>HResult</c> 只有派生类能写。
    /// </summary>
    private sealed class EngineFailureException : Exception
    {
        public EngineFailureException(string message, int hresult, Exception? inner = null)
            : base(message, inner)
        {
            HResult = hresult;
        }
    }

    [Fact]
    public void 找不到会话目录时不构造Session_确保绝不新建会话()
    {
        var created = 0;
        var factory = new WslcApiSessionFactory((_, _) =>
        {
            created++;
            return new FakeApiSession();
        });

        var session = factory.TryAttach(
            new SessionModel { Id = 9, DisplayName = "不存在的会话-请勿创建-7f3a", StoragePath = string.Empty },
            out var reason);

        Assert.Null(session);
        Assert.Equal(0, created);
        Assert.NotNull(reason);
        Assert.Contains("不存在的会话-请勿创建-7f3a", reason);
    }

    [Fact]
    public void 交给引擎的是存储根目录而不是会话目录()
    {
        // 用随机会话名，保证 %LOCALAPPDATA%\wslc\sessions 里不可能有同名目录，
        // 从而稳定走到"按 StoragePath 取目录"的兜底分支
        var name = "wslc-cli-test-" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "wslcdesktop-tests-" + Guid.NewGuid().ToString("N"));
        var sessionDir = Path.Combine(root, name);
        Directory.CreateDirectory(sessionDir);
        File.WriteAllBytes(Path.Combine(sessionDir, "storage.vhdx"), new byte[16]);

        try
        {
            string? capturedStorageRoot = null;
            var factory = new WslcApiSessionFactory((_, storageRoot) =>
            {
                capturedStorageRoot = storageRoot;
                return new FakeApiSession { SessionName = name };
            });

            var session = factory.TryAttach(
                new SessionModel
                {
                    Id = 1,
                    DisplayName = name,
                    StoragePath = Path.Combine(sessionDir, "storage.vhdx")
                },
                out var reason);

            Assert.NotNull(session);
            Assert.Null(reason);

            // ⚠️ 关键：交给引擎的是**存储根目录**。传会话目录的话引擎会去找
            // …\<会话目录>\wslc\sessions\<会话名>\storage.vhdx（不存在），
            // 报一个 HRESULT 说不清的错 —— 用户实测踩过。
            Assert.Equal(SessionStorageLocator.GetStorageRoot(), capturedStorageRoot);
            Assert.NotEqual(sessionDir, capturedStorageRoot);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 清理失败无所谓 */ }
        }
    }

    [Fact]
    public void 存储目录布局辅助函数_不存在时返回null()
    {
        Assert.Null(SessionStorageLocator.FindExistingSessionDirectory(string.Empty));
        Assert.Null(SessionStorageLocator.FindExistingSessionDirectory("几乎不可能存在的会话名-8c41d0"));
    }
}
