using WSLC.Panel.Core.Abstractions;
using WSLC.Panel.Core.Models;
using WSLC.Panel.Core.Parsing;

namespace WSLC.Panel.Core.Services;

/// <summary>
/// 发行版管理：wsl.exe（列表/启停/设默认/注销）+ 注册表 Lxss（安装位置、VHDX 大小）。
/// 列表优先用 wsl.exe，注册表只用于补充 wsl.exe 给不出的信息（安装目录、磁盘占用）。
/// </summary>
public class DistroService : IDistroService
{
    private const string LxssRegistryPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss";

    private readonly IProcessRunner _runner;
    private readonly string _wslExecutable;
    private readonly string _regExecutable;

    public DistroService(
        IProcessRunner? runner = null,
        string wslExecutable = "wsl.exe",
        string regExecutable = "reg.exe")
    {
        _runner = runner ?? new CliProcessRunner();
        _wslExecutable = wslExecutable;
        _regExecutable = regExecutable;
    }

    public async Task<IReadOnlyList<DistroModel>> GetDistrosAsync(CancellationToken cancellationToken = default)
    {
        var listResult = await _runner.RunAsync(_wslExecutable, "--list --verbose", cancellationToken: cancellationToken);
        if (!listResult.IsSuccess && string.IsNullOrWhiteSpace(listResult.StandardOutput))
        {
            return Array.Empty<DistroModel>();
        }

        var distros = DistroOutputParser.ParseList(listResult.StandardOutput);
        if (distros.Count == 0) return distros;

        // 注册表查询失败（企业策略/权限）时仍然返回 wsl.exe 的结果
        var registryResult = await _runner.RunAsync(
            _regExecutable,
            $"query {LxssRegistryPath} /s",
            cancellationToken: cancellationToken);

        if (registryResult.IsSuccess)
        {
            distros = DistroOutputParser.MergeRegistryInfo(distros, registryResult.StandardOutput);
        }

        return distros;
    }

    public async Task<bool> StartAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        // --exec /bin/true：只把发行版拉起来，不驻留交互 shell
        var result = await _runner.RunAsync(
            _wslExecutable,
            $"-d {WslcCommandBuilder.Quote(name)} --exec /bin/true",
            cancellationToken: cancellationToken);

        return result.IsSuccess;
    }

    public async Task<bool> TerminateAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        var result = await _runner.RunAsync(
            _wslExecutable,
            $"--terminate {WslcCommandBuilder.Quote(name)}",
            cancellationToken: cancellationToken);

        return result.IsSuccess;
    }

    public async Task<bool> SetDefaultAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        var result = await _runner.RunAsync(
            _wslExecutable,
            $"--set-default {WslcCommandBuilder.Quote(name)}",
            cancellationToken: cancellationToken);

        return result.IsSuccess;
    }

    public async Task<bool> UnregisterAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        var result = await _runner.RunAsync(
            _wslExecutable,
            $"--unregister {WslcCommandBuilder.Quote(name)}",
            cancellationToken: cancellationToken);

        return result.IsSuccess;
    }

    public async Task<string> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(_wslExecutable, "--status", cancellationToken: cancellationToken);
        return string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
    }
}
