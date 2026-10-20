using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 为会话补充存储信息。
///
/// 实测（本机）：会话存储固定落在
/// <c>%LOCALAPPDATA%\wslc\sessions\&lt;会话名&gt;\storage.vhdx</c>（另有 <c>swap.vhdx</c>），
/// 目录名即会话显示名。wslc 的 CLI 不返回这些，只能自己扫目录——
/// 但"会话存储占用"是概览页的核心信息（§5.3 会话模型认知），不能省。
/// </summary>
public static class SessionStorageLocator
{
    private const string StorageFileName = "storage.vhdx";
    private const string SwapFileName = "swap.vhdx";

    public static string GetSessionsRoot(string? localAppDataOverride = null)
        => Path.Combine(GetStorageRoot(localAppDataOverride), "wslc", "sessions");

    /// <summary>
    /// **存储根目录**（默认 <c>%LOCALAPPDATA%</c>）—— 这是要交给
    /// <c>SessionSettings.StoragePath</c> 的那个值，**不是**会话目录。
    ///
    /// 实测依据（参考项目 PLAN-v0.2 记录的真机布局）：
    /// <code>
    /// &lt;storagePath&gt;\wslc\sessions\&lt;会话名&gt;\storage.vhdx
    /// storagePath 未设置 → 实际使用 %LOCALAPPDATA%
    /// </code>
    /// 引擎自己在根目录下面拼 <c>wslc\sessions\&lt;会话名&gt;\</c>。
    /// ⚠️ 传成会话目录的话，引擎会去找
    /// <c>…\wslc\sessions\wslc-cli-X\wslc\sessions\wslc-cli-X\storage.vhdx</c> ——
    /// 路径不存在，只会报一个 HRESULT 说不清的错（用户实测踩过）。
    /// </summary>
    public static string GetStorageRoot(string? localAppDataOverride = null)
        => localAppDataOverride
           ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>按会话名匹配目录，补上 VHDX 路径与大小；匹配不到时保留原值。</summary>
    public static List<SessionModel> Enrich(IEnumerable<SessionModel> sessions, string? sessionsRoot = null)
    {
        var root = sessionsRoot ?? GetSessionsRoot();
        var result = new List<SessionModel>();

        foreach (var session in sessions)
        {
            var dir = string.IsNullOrWhiteSpace(session.DisplayName)
                ? null
                : FindSessionDirectory(root, session.DisplayName);

            if (dir is null)
            {
                result.Add(session);
                continue;
            }

            result.Add(session with
            {
                StoragePath = Path.Combine(dir, StorageFileName),
                VhdSizeBytes = TryGetSize(Path.Combine(dir, StorageFileName)),
                SwapSizeBytes = TryGetSize(Path.Combine(dir, SwapFileName))
            });
        }

        return result;
    }

    /// <summary>
    /// 会话目录（<c>…\wslc\sessions\&lt;会话名&gt;</c>）——<b>存在才返回</b>。
    ///
    /// 给官方 API 通道做"只附着、不新建"的前置校验用：目录不存在就说明目标会话
    /// 并非当前引擎在用的那个，此时绝不能把路径交给 <c>Session</c>，
    /// 否则会在磁盘上凭空多出一个 CLI 侧看不见的会话。
    /// </summary>
    public static string? FindExistingSessionDirectory(string sessionName, string? sessionsRoot = null)
        => string.IsNullOrWhiteSpace(sessionName)
            ? null
            : FindSessionDirectory(sessionsRoot ?? GetSessionsRoot(), sessionName);

    /// <summary>
    /// 这个会话是不是真的在磁盘上有存储文件（<c>storage.vhdx</c>）。
    ///
    /// 比"目录存在"更强的判据：会话目录可能是残留的空目录，
    /// 而引擎在用的那个一定有 <c>storage.vhdx</c>。
    /// </summary>
    public static bool HasStorageFile(string sessionDirectory)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory)) return false;

        try
        {
            return File.Exists(Path.Combine(sessionDirectory, StorageFileName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>会话存储总占用（所有会话的 storage.vhdx + swap.vhdx 之和，字节）。</summary>
    public static long GetTotalUsageBytes(IEnumerable<SessionModel> sessions)
    {
        long total = 0;
        foreach (var session in sessions)
        {
            total += session.VhdSizeBytes ?? 0;
            total += session.SwapSizeBytes ?? 0;
        }
        return total;
    }

    public static string FormatBytes(long? bytes) => bytes switch
    {
        null or < 0 => "未知",
        < 1024L => $"{bytes.Value} B",
        < 1024L * 1024 => $"{bytes.Value / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes.Value / (1024.0 * 1024.0):F1} MB",
        _ => $"{bytes.Value / (1024.0 * 1024.0 * 1024.0):F2} GB"
    };

    private static string? FindSessionDirectory(string root, string sessionName)
    {
        try
        {
            if (!Directory.Exists(root)) return null;

            var exact = Path.Combine(root, sessionName);
            if (Directory.Exists(exact)) return exact;

            // 目录名大小写/尾随空格不一致时做一次不区分大小写的匹配
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (string.Equals(Path.GetFileName(dir), sessionName, StringComparison.OrdinalIgnoreCase))
                {
                    return dir;
                }
            }
        }
        catch
        {
            // 目录不可读（权限）时放弃补充，不影响会话列表本身
        }

        return null;
    }

    private static long? TryGetSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch
        {
            return null;
        }
    }
}
