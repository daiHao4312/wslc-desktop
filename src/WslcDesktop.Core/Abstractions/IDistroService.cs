using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Abstractions;

/// <summary>WSL 发行版（wsl.exe 侧）操作。与容器通道 <see cref="IWslcClient"/> 相互独立。</summary>
public interface IDistroService
{
    Task<IReadOnlyList<DistroModel>> GetDistrosAsync(CancellationToken cancellationToken = default);

    Task<bool> StartAsync(string name, CancellationToken cancellationToken = default);

    Task<bool> TerminateAsync(string name, CancellationToken cancellationToken = default);

    Task<bool> SetDefaultAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>注销（删除）发行版。不可撤销：VHDX 与其中数据一并消失。</summary>
    Task<bool> UnregisterAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>WSL 引擎状态文本（<c>wsl --status</c>），用于诊断。</summary>
    Task<string> GetStatusAsync(CancellationToken cancellationToken = default);
}
