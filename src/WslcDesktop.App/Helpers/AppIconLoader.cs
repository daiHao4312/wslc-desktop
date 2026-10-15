using System.Runtime.InteropServices;

namespace WslcDesktop.Helpers;

/// <summary>
/// 从**当前进程 exe 自身的 Win32 图标资源**里取图标。
///
/// 为什么不继续用 <c>AppWindow.SetIcon("Assets/AppIcon.ico")</c>：
/// 那个重载把参数当**相对路径**、按**当前工作目录**解析，而单文件发布下
/// Assets 是被解压到临时目录的，工作目录又是启动时所在目录——两者对不上，
/// SetIcon 会**静默失败**（实测：双击发布版 exe，任务栏一片空白；从 bin 目录启动却正常）。
///
/// exe 自身通过 csproj 的 <c>ApplicationIcon</c> 嵌了图标，<c>ExtractIconEx</c>
/// 直接读 PE 资源，因此任何工作目录、任何发布形态（Debug 目录 / 单文件）都能拿到。
/// </summary>
internal static class AppIconLoader
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// 取 exe 里的大图标（HICON）。取不到时返回 <see cref="IntPtr.Zero"/>。
    /// **调用方负责在窗口销毁时调用 <see cref="Release"/>**——这是自己提取出来的图标，
    /// 归自己所有；不释放会一直占着一个 GDI 句柄。
    /// </summary>
    public static IntPtr LoadLarge()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return IntPtr.Zero;

        try
        {
            var count = ExtractIconEx(exe, 0, out var large, out var small, 1);
            if (count <= 0) return IntPtr.Zero;

            // 只要大的（任务栏 / Alt+Tab 用大图标）。小的必须就地销毁，
            // 否则每次调用都漏一个 GDI 句柄。
            if (small != IntPtr.Zero) DestroyIcon(small);

            return large;
        }
        catch
        {
            // 拿不到就让调用方退回默认图标，不要因为图标拖垮窗口初始化
            return IntPtr.Zero;
        }
    }

    /// <summary>释放 <see cref="LoadLarge"/> 返回的句柄。</summary>
    public static void Release(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return;

        try
        {
            DestroyIcon(hIcon);
        }
        catch
        {
            // 进程退出路径上，失败无所谓
        }
    }
}
