using System.Runtime.InteropServices;
using System.Text;

namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 敏感串的加解密（目前只用于私有镜像源的密码）。
///
/// 用 <b>Windows DPAPI</b>（<c>crypt32!CryptProtectData</c>，<c>CurrentUser</c> 作用域）：
/// 密文只有**同一个 Windows 账户**能解开 —— 把 <c>prefs.json</c> 拷到别的机器或别的账户上，
/// 那些密码就是一串读不出来的 Base64。
///
/// 两点选型说明：
/// <list type="bullet">
/// <item>不用 <c>System.Security.Cryptography.ProtectedData</c> 包：它不在 .NET 10 的共享框架里
/// （<c>Microsoft.NETCore.App.Ref</c> 里没有这个程序集），为一个几十行的封装多引一个包不划算；
/// 本项目本来就有 P/Invoke 的先例（托盘图标、任务栏图标）。</item>
/// <item>不用 WinRT 的 <c>DataProtectionProvider</c>：它只提供异步 API，
/// 会把"读一条凭据"整条路径染成异步，而读偏好是同步的。</item>
/// </list>
///
/// ⚠️ 熵是**写死**的，改它 = 所有已存密码立刻作废。
/// 真要改时按新版本号加一个新常量，并保留旧值的解密路径，别直接改字面量。
/// </summary>
public static class SecretProtector
{
    /// <summary><c>CRYPTPROTECT_UI_FORBIDDEN</c>：任何情况下都别弹系统对话框（后台线程上会直接挂住）。</summary>
    private const int CryptProtectUiForbidden = 0x1;

    /// <summary>
    /// 额外熵（Optional Entropy）。
    ///
    /// DPAPI 的 CurrentUser 作用域只保证"同一个 Windows 账户"，**不区分程序** ——
    /// 同账户下别的程序拿到 <c>prefs.json</c> 里的密文也能解开。加上这段只有本程序知道的字节后，
    /// 光有密文就解不开了。
    ///
    /// ⚠️ 改动这串字面量 = 已保存的密码全部解不开（用户需重填）。
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WslcDesktop.RegistryCredential.v1");

    /// <summary>加密成 Base64 密文。明文永不出现在返回值里。</summary>
    public static string Protect(string? plaintext)
        => Convert.ToBase64String(ProtectBytes(Encoding.UTF8.GetBytes(plaintext ?? string.Empty)));

    /// <summary>
    /// 把密文解回明文。
    ///
    /// 解不开时返回 <c>null</c> 而不是抛异常 —— 换了 Windows 账户、复制了别处的 prefs.json、
    /// 或者文件被改坏都会走到这里，这属于**预期内**的失败，
    /// 由调用方提示"请重新填写密码"，不该让设置页整页崩掉。
    /// </summary>
    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return null;

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(protectedBase64.Trim());
        }
        catch (FormatException)
        {
            return null;
        }

        var plain = UnprotectBytes(blob);
        return plain is null ? null : Encoding.UTF8.GetString(plain);
    }

    private static byte[] ProtectBytes(byte[] plain)
    {
        // 长度为 0 时也给一个合法指针：cbData=0 + pbData=NULL 在部分 Windows 版本上会被拒
        var inputPtr = Marshal.AllocHGlobal(Math.Max(1, plain.Length));
        var entropyPtr = Marshal.AllocHGlobal(Entropy.Length);

        try
        {
            if (plain.Length > 0) Marshal.Copy(plain, 0, inputPtr, plain.Length);
            Marshal.Copy(Entropy, 0, entropyPtr, Entropy.Length);

            var input = new DataBlob { Length = plain.Length, Data = inputPtr };
            var entropy = new DataBlob { Length = Entropy.Length, Data = entropyPtr };

            if (!CryptProtectData(
                    ref input,
                    IntPtr.Zero,
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out var output))
            {
                throw new InvalidOperationException(
                    $"凭据加密失败（Win32 错误 {Marshal.GetLastWin32Error()}）");
            }

            try
            {
                var result = new byte[output.Length];
                if (output.Length > 0) Marshal.Copy(output.Data, result, 0, output.Length);
                return result;
            }
            finally
            {
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inputPtr);
            Marshal.FreeHGlobal(entropyPtr);
        }
    }

    private static byte[]? UnprotectBytes(byte[] blob)
    {
        if (blob.Length == 0) return null;

        var inputPtr = Marshal.AllocHGlobal(blob.Length);
        var entropyPtr = Marshal.AllocHGlobal(Entropy.Length);

        try
        {
            Marshal.Copy(blob, 0, inputPtr, blob.Length);
            Marshal.Copy(Entropy, 0, entropyPtr, Entropy.Length);

            var input = new DataBlob { Length = blob.Length, Data = inputPtr };
            var entropy = new DataBlob { Length = Entropy.Length, Data = entropyPtr };

            if (!CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out var output))
            {
                return null;
            }

            try
            {
                var result = new byte[output.Length];
                if (output.Length > 0) Marshal.Copy(output.Data, result, 0, output.Length);
                return result;
            }
            finally
            {
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inputPtr);
            Marshal.FreeHGlobal(entropyPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        /// <summary><c>DWORD cbData</c>。</summary>
        public int Length;

        /// <summary><c>BYTE* pbData</c>。输出缓冲由 DPAPI 用 LocalAlloc 分配，必须 LocalFree。</summary>
        public IntPtr Data;
    }

    // 描述（szDataDescr）一律传 NULL：那是给"Windows 里同一账户可见的加密文件"做标注用的，
    // 我们的密文写在自己的 JSON 里，标注没有意义，反而多一份要释放的内存。

    [DllImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr descriptionOut,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);
}
