using System.Runtime.InteropServices;

namespace WslcDesktop.Helpers;

/// <summary>
/// 系统托盘图标（Win32 <c>Shell_NotifyIcon</c>）。
///
/// WinUI 3 没有内置托盘 API，只能走 Win32。做法是在 UI 线程上建一个
/// **消息专用窗口**（HWND_MESSAGE）承接托盘回调——它没有可见界面，
/// 消息由 WinUI 自己的消息泵派发，所以不需要另起消息循环。
///
/// 左键单击/双击唤出主窗口，右键弹出菜单；菜单用 Win32 原生弹出菜单实现，
/// 这样主窗口处于隐藏状态时也能正常弹出。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 1;

    private const int WM_COMMAND = 0x0111;
    private const int WM_DESTROY = 0x0002;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_NULL = 0x0000;

    /// <summary>气泡被点击（NIN_BALLOONUSERCLICK）。</summary>
    private const int NIN_BALLOONUSERCLICK = 0x0405;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;

    private const int NIIF_INFO = 0x00000001;
    private const int NIIF_NOSOUND = 0x00000010;

    private const int MF_STRING = 0x00000000;
    private const int MF_SEPARATOR = 0x00000800;

    private const int TPM_RIGHTBUTTON = 0x0002;
    private const int TPM_RETURNCMD = 0x0100;

    private const int IDM_SHOW = 1001;
    private const int IDM_REFRESH = 1002;
    private const int IDM_EXIT = 1003;

    private const uint IDI_APPLICATION = 32512;

    private readonly string _tooltip;
    private readonly Action _onActivate;
    private readonly Action _onRefresh;
    private readonly Action _onExit;

    /// <summary>必须保留委托引用，否则 GC 回收后窗口过程会变成野指针。</summary>
    private readonly WndProc _wndProcDelegate;

    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _icon = IntPtr.Zero;
    private IntPtr _hInstance;
    private ushort _classAtom;
    private bool _iconOwned;

    public TrayIcon(string tooltip, Action onActivate, Action onRefresh, Action onExit)
    {
        _tooltip = tooltip;
        _onActivate = onActivate;
        _onRefresh = onRefresh;
        _onExit = onExit;
        _wndProcDelegate = WndProcImpl;
    }

    public bool IsCreated { get; private set; }

    public IntPtr Handle => _hwnd;

    /// <summary>创建托盘图标。任一环节失败都返回 false（不抛异常——托盘只是锦上添花，不能拦住主流程）。</summary>
    public bool TryCreate()
    {
        try
        {
            if (!CreateMessageWindow()) return false;

            _icon = LoadAppIcon();
            if (_icon == IntPtr.Zero) return false;

            var data = BuildIconData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
            if (!Shell_NotifyIcon(NIM_ADD, ref data))
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
                return false;
            }

            IsCreated = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>弹一个气泡提示（用于"已最小化到托盘，仍可在此退出"这类提醒）。</summary>
    public void ShowBalloon(string title, string message)
    {
        if (!IsCreated) return;

        try
        {
            var data = BuildIconData(NIF_INFO);
            data.szInfo = Truncate(message, 255);
            data.szInfoTitle = Truncate(title, 63);
            data.dwInfoFlags = NIIF_INFO | NIIF_NOSOUND;
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        catch
        {
            // 气泡失败无伤大雅
        }
    }

    /// <summary>更新悬停提示。</summary>
    public void UpdateTooltip(string tooltip)
    {
        if (!IsCreated) return;

        try
        {
            var data = BuildIconData(NIF_TIP);
            data.szTip = Truncate(tooltip, 127);
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            try
            {
                if (IsCreated)
                {
                    var data = BuildIconData(0);
                    Shell_NotifyIcon(NIM_DELETE, ref data);
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                DestroyWindow(_hwnd);
                // 必须用注册时同一个 hInstance：传 IntPtr.Zero 与 GetModuleHandle(null)
                // 不匹配，UnregisterClass 必然失败，窗口类会残留到进程退出
                if (_classAtom != 0) UnregisterClass(_className, _hInstance);
            }
            catch
            {
                // ignore
            }

            _hwnd = IntPtr.Zero;
        }

        if (_iconOwned && _icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
        }

        _icon = IntPtr.Zero;
        IsCreated = false;
    }

    // ---------- 内部 ----------

    private static readonly string _className = "WslcDesktopTrayWindow";

    private bool CreateMessageWindow()
    {
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = _hInstance = GetModuleHandle(null),
            lpszClassName = _className
        };

        _classAtom = RegisterClassEx(ref wc);
        // 已注册过同名类时 RegisterClassEx 返回 0（ERROR_CLASS_ALREADY_EXISTS），仍可继续 CreateWindowEx
        if (_classAtom == 0 && Marshal.GetLastWin32Error() != 1410)
        {
            return false;
        }

        // HWND_MESSAGE (-3)：消息专用窗口，不参与 z-order、不可见
        _hwnd = CreateWindowEx(0, _className, _className, 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        return _hwnd != IntPtr.Zero;
    }

    private NOTIFYICONDATA BuildIconData(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = (uint)flags,
        uCallbackMessage = WM_TRAYICON,
        hIcon = _icon,
        szTip = Truncate(_tooltip, 127)
    };

    private IntPtr LoadAppIcon()
    {
        // 优先取 exe 里嵌入的应用图标（与 exe 图标一致，观感统一）
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var extracted = ExtractIconEx(exe, 0, out var large, out var small, 1);
                if (extracted > 0 && small != IntPtr.Zero)
                {
                    if (large != IntPtr.Zero) DestroyIcon(large);

                    // 自己提取出来的图标归自己所有，退出时要 DestroyIcon
                    _iconOwned = true;
                    return small;
                }

                if (large != IntPtr.Zero) DestroyIcon(large);
            }
        }
        catch
        {
            // 落到系统默认图标
        }

        // 系统共享图标：绝不能 DestroyIcon，否则会影响其它进程
        _iconOwned = false;
        return LoadIcon(IntPtr.Zero, new IntPtr(IDI_APPLICATION));
    }

    private IntPtr WndProcImpl(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch ((int)msg)
        {
            case WM_TRAYICON:
                switch (lParam.ToInt32())
                {
                    case WM_LBUTTONUP:
                    case WM_LBUTTONDBLCLK:
                        _onActivate();
                        return IntPtr.Zero;

                    case WM_RBUTTONUP:
                        ShowContextMenu();
                        return IntPtr.Zero;

                    case NIN_BALLOONUSERCLICK:
                        _onActivate();
                        return IntPtr.Zero;
                }

                break;

            case WM_DESTROY:
                return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            AppendMenu(menu, MF_STRING, IDM_SHOW, "显示主窗口");
            AppendMenu(menu, MF_STRING, IDM_REFRESH, "刷新");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, IDM_EXIT, "退出");

            GetCursorPos(out var pt);

            // 经典坑：不先把窗口设为前台，菜单点外面不会消失
            SetForegroundWindow(_hwnd);

            var command = TrackPopupMenu(
                menu,
                TPM_RIGHTBUTTON | TPM_RETURNCMD,
                pt.X,
                pt.Y,
                0,
                _hwnd,
                IntPtr.Zero);

            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case IDM_SHOW:
                    _onActivate();
                    break;
                case IDM_REFRESH:
                    _onRefresh();
                    break;
                case IDM_EXIT:
                    _onExit();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    // ---------- P/Invoke ----------

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, int uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);
}
