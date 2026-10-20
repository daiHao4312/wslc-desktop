using Microsoft.WSL.Containers;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Services;

/// <summary>
/// <see cref="IWslcApiSession"/> 的真实实现：官方 <c>Session</c> 的唯一包装点。
///
/// 整个仓库只有这一个文件直接接触 WinRT 投影类型（<c>Session</c> / <c>ImageProgress</c> /
/// <c>PullImageOptions</c>）——SDK 升级时投影可能变，改动面被钉死在这里。
/// </summary>
public sealed class WslcApiSession : IWslcApiSession
{
    private readonly Session _session;
    private bool _disposed;

    public WslcApiSession(Session session, string sessionName, string storageRoot, bool readOnlyAttach = false)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        SessionName = sessionName;
        StorageRoot = storageRoot;
        ReadOnlyAttach = readOnlyAttach;
    }

    public bool ReadOnlyAttach { get; }

    public string SessionName { get; }

    public string StorageRoot { get; }

    public IReadOnlyList<string> GetImageNames()
        => _session.GetImages().Select(image => image.Name).ToList();

    public async Task PullImageAsync(
        string imageUri,
        string? registryAuth,
        Action<ApiImageProgress>? onProgress,
        CancellationToken cancellationToken = default)
    {
        var options = new PullImageOptions(imageUri);
        if (!string.IsNullOrWhiteSpace(registryAuth))
        {
            // 私有仓库凭据：只有 API 通道能带（CLI 的 pull 没有这个参数）
            options.RegistryAuth = registryAuth;
        }

        var operation = _session.PullImageAsync(options);
        if (onProgress is not null)
        {
            operation.Progress = (_, progress) => onProgress(new ApiImageProgress
            {
                ImageId = progress.Id ?? string.Empty,
                Status = progress.Status.ToString(),
                DownloadedBytes = progress.CurrentBytes,
                TotalBytes = progress.TotalBytes
            });
        }

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                operation.Cancel();
            }
            catch
            {
                // 操作已结束或投影不支持取消：忽略，让 await 那边给出真实结果
            }
        });

        await operation.AsTask();
    }

    public RegistryCredentialResult Authenticate(string serverAddress, string username, string password)
    {
        // 只做形状校验，不猜存在性：真正的失败由引擎给
        if (!RegistryInput.IsValidHost(serverAddress))
        {
            return new RegistryCredentialResult { Error = $"registry 地址不合法：{serverAddress}" };
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            return new RegistryCredentialResult { Error = "用户名不能为空" };
        }

        try
        {
            // 用 API 端点（docker.io → registry-1.docker.io）：认证是发给 API 的，不是发给网站的
            var endpoint = new Uri($"https://{RegistryInput.CanonicalHost(serverAddress)}");
            var result = _session.Authenticate(endpoint, username, password);

            var token = result?.IdentityToken ?? string.Empty;
            if (token.Length == 0)
            {
                // 引擎没抛异常但也没给令牌：多半是用户名/密码不对
                return new RegistryCredentialResult { Error = "引擎没有返回身份令牌（用户名或密码可能不对）" };
            }

            return new RegistryCredentialResult
            {
                Success = true,
                Token = token,
                TokenKind = result!.TokenType.ToString()
            };
        }
        catch (Exception ex)
        {
            return new RegistryCredentialResult { Error = ex.Message };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _session.Dispose();
        }
        catch
        {
            // 释放失败不影响退出
        }
    }

    /// <summary>
    /// 起容器内进程（M19）。全部 WinRT 类型只在这个文件里出现。
    ///
    /// ⚠️ 引擎给的是**字节片段**（<c>byte[]</c>），可能在一行中间、甚至一个 UTF-8 字符中间断开，
    /// 所以 stdout / stderr 各用一个 <see cref="Utf8LineSplitter"/> 保留跨块状态。
    /// </summary>
    public IApiContainerProcess TryStartProcess(string containerIdOrName, string command, Action<string> onOutputLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerIdOrName);
        ArgumentNullException.ThrowIfNull(onOutputLine);

        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("命令不能为空。", nameof(command));
        }

        // Event 模式：输出通过事件推给我们（Stream 模式要自己拉流，这里不需要）
        var container = _session.OpenContainer(containerIdOrName, ProcessOutputMode.Event);

        var settings = new ProcessSettings
        {
            CommandLine = new List<string> { "/bin/sh", "-c", command },
            OutputMode = ProcessOutputMode.Event
        };

        var process = container.CreateProcess(settings);
        return new WinrtContainerProcess(container, process, onOutputLine);
    }

    /// <summary>
    /// 容器内进程的句柄。事件回调在引擎线程上，所以输出转发加锁（两路共用一把锁保证行不乱序）。
    /// </summary>
    private sealed class WinrtContainerProcess : IApiContainerProcess
    {
        private readonly Container _container;
        private readonly Process _process;
        private readonly Action<string> _onOutputLine;

        private readonly Utf8LineSplitter _stdout = new();
        private readonly Utf8LineSplitter _stderr = new();
        private readonly object _outputGate = new();

        private readonly TaskCompletionSource<int> _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private bool _disposed;

        public WinrtContainerProcess(Container container, Process process, Action<string> onOutputLine)
        {
            _container = container;
            _process = process;
            _onOutputLine = onOutputLine;

            // 先订阅再启动：否则极短命的进程可能在 Start 之后立刻结束，首屏输出就丢了
            _process.OutputReceived += data => Emit(_stdout, data);
            _process.ErrorReceived += data => Emit(_stderr, data);
            _process.Exited += exitCode =>
            {
                FlushTail();
                _exit.TrySetResult(exitCode);
            };

            _process.Start();
        }

        public int Pid => (int)_process.Pid;

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
            => _exit.Task.WaitAsync(cancellationToken);

        public void Terminate() => SendSignal(Signal.SIGTERM);

        public void Kill() => SendSignal(Signal.SIGKILL);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _process.Dispose();
            }
            catch
            {
                // 忽略
            }

            try
            {
                _container.Dispose();
            }
            catch
            {
                // 忽略
            }
        }

        // 方法名不能叫 Signal：会把 Signal 枚举名遮住，Signal.SIGTERM 直接编译不过
        private void SendSignal(Signal signal)
        {
            try
            {
                _process.Signal(signal);
            }
            catch
            {
                // 进程已经退出（或投影拒绝重复发信号）：忽略，界面以 Exited 事件为准
            }
        }

        private void Emit(Utf8LineSplitter splitter, byte[]? data)
        {
            // 回调里抛异常会顺着引擎的事件线程冒出去，绝不能让它影响进程本身
            try
            {
                lock (_outputGate)
                {
                    foreach (var line in splitter.Push(data)) _onOutputLine(line);
                }
            }
            catch
            {
                // 界面回调出错（例如页面已卸载）：忽略
            }
        }

        private void FlushTail()
        {
            try
            {
                lock (_outputGate)
                {
                    if (_stdout.Flush() is { } outTail) _onOutputLine(outTail);
                    if (_stderr.Flush() is { } errTail) _onOutputLine(errTail);
                }
            }
            catch
            {
                // 同上
            }
        }
    }
}
