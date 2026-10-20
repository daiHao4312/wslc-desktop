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

    // ---------- 添加发行版（M17）----------

    /// <summary>在线可安装的发行版清单（<c>wsl --list --online</c>）。</summary>
    Task<IReadOnlyList<OnlineDistroModel>> GetOnlineDistrosAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 装前检查 + 等效命令预览。<b>不执行任何命令</b>，只读文件系统做存在性判断，
    /// 供界面在用户填表时即时反馈（命令预览与真实执行共用同一份拼装逻辑）。
    /// </summary>
    Task<DistroInstallPlan> PlanInstallAsync(DistroInstallRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行安装。<paramref name="onOutput"/> 会被逐行回调（stdout + stderr），
    /// 供界面把引擎输出实时显示出来——这个过程可能持续几分钟，不能只给一个转圈。
    /// </summary>
    Task<DistroInstallResult> InstallAsync(
        DistroInstallRequest request,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default);

    // ---------- 发行版详情（M18）----------

    /// <summary>压缩虚拟磁盘，回收已释放的块（<c>--manage &lt;名&gt; --compact</c>）。不破坏数据。</summary>
    Task<DistroOperationResult> CompactAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 开关稀疏盘（<c>--manage &lt;名&gt; --set-sparse true|false</c>）。
    /// 开启时实现内部会自动补 <c>--allow-unsafe</c>——WSL 3.0.1 起不带这个开关一律拒绝。
    /// </summary>
    Task<DistroOperationResult> SetSparseAsync(string name, bool sparse, CancellationToken cancellationToken = default);

    /// <summary>调整磁盘大小（<c>--resize 50GB</c>）。大小格式本地先校验，不规范直接返回失败。</summary>
    Task<DistroOperationResult> ResizeAsync(string name, string size, CancellationToken cancellationToken = default);

    /// <summary>移动安装位置（<c>--move &lt;目录&gt;</c>）。跨盘是真的拷数据，可能很慢，调用方应给可取消的界面。</summary>
    Task<DistroOperationResult> MoveAsync(string name, string targetDirectory, CancellationToken cancellationToken = default);

    /// <summary>设置默认登录用户（<c>--set-default-user</c>）。用户名不存在会让发行版下次启动失败。</summary>
    Task<DistroOperationResult> SetDefaultUserAsync(string name, string user, CancellationToken cancellationToken = default);

    /// <summary>
    /// 导出发行版（<c>--export &lt;名&gt; &lt;文件&gt;</c>，默认 tar 格式）。
    ///
    /// <c>wsl --export</c> 不打百分比，所以 <paramref name="onProgress"/> 的进度是
    /// <b>轮询目标文件大小</b>得来的（真实且连续）；<paramref name="onOutput"/> 收引擎的零星状态行。
    /// </summary>
    Task<DistroOperationResult> ExportAsync(
        string name,
        string filePath,
        Action<DistroExportProgress>? onProgress = null,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default);

    /// <summary>读发行版内的 <c>/etc/wsl.conf</c>；文件不存在时返回空内容且视为成功。</summary>
    Task<WslConfSnapshot> ReadWslConfAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>写发行版内的 <c>/etc/wsl.conf</c>（<b>先备份</b>到 <c>/etc/wsl.conf.bak</c>）。</summary>
    Task<DistroOperationResult> WriteWslConfAsync(string name, string text, CancellationToken cancellationToken = default);
}
