using System.Security.Cryptography;

namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 更新资产的摘要工具（纯函数 / 静态方法，便于单测）。
///
/// GitHub 从 2025 年起在 Release 资产的 <c>digest</c> 字段里给出 <c>sha256:&lt;hex&gt;</c>，
/// 本面板把它作为"下载内容可信"的唯一依据：能拿到就强校验，拿不到才退回长度校验。
/// </summary>
public static class AssetDigest
{
    private const string Sha256Prefix = "sha256:";

    /// <summary>sha256 的十六进制长度。</summary>
    private const int Sha256HexLength = 64;

    /// <summary>
    /// 把 GitHub 的 digest 归一化成小写十六进制（去掉 <c>sha256:</c> 前缀）。
    /// 空值、算法不是 sha256、长度不对或含非十六进制字符时返回 null——宁可判为"没有摘要"，
    /// 也不要拿一个错的哈希去校验，那会把好包判成坏包。
    /// </summary>
    public static string? NormalizeSha256(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = raw.Trim();

        // 允许 "sha256:xxx" 与裸 hex 两种写法（不同来源的字段格式不完全一致）
        if (text.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[Sha256Prefix.Length..].Trim();
        }

        if (text.Length != Sha256HexLength) return null;

        foreach (var c in text)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex) return null;
        }

        return text.ToLowerInvariant();
    }

    /// <summary>两个摘要是否等价（任一侧为 null 一律 false，避免"都没摘要"被判成通过）。</summary>
    public static bool AreEqual(string? left, string? right)
    {
        var a = NormalizeSha256(left);
        var b = NormalizeSha256(right);

        return a is not null && b is not null && string.Equals(a, b, StringComparison.Ordinal);
    }

    /// <summary>
    /// 计算文件的 sha256（小写十六进制）。文件不存在或读取失败返回 null。
    /// 用于"包已下载过"的复用判定，以及替换前的二次校验。
    /// </summary>
    public static string? TryComputeFileSha256(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: false);

            using var hash = SHA256.Create();
            var bytes = hash.ComputeHash(stream);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
        catch (Exception)
        {
            // IO 异常/文件被占用都只表示"算不出来"，由调用方决定降级策略
            return null;
        }
    }
}
