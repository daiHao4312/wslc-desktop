namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 导出文件名的生成规则。
///
/// 放在 Core 而不是界面层：文件名非法字符是**平台约束**，不是某个页面的偏好；
/// 容器名里出现 <c>/ : *</c> 时 Windows 会直接报错，这条规则得能被单测钉住。
/// </summary>
public static class ExportName
{
    /// <summary>形如 <c>container-nginx-logs-20261020-160102.txt</c>。</summary>
    public static string Suggest(string prefix, string extension, DateTimeOffset now)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var raw = string.IsNullOrWhiteSpace(prefix) ? "export" : prefix.Trim();
        var safe = new string(raw.Select(c => invalid.Contains(c) ? '-' : c).ToArray());

        var ext = extension.StartsWith('.') ? extension : "." + extension;

        return $"{safe}-{now:yyyyMMdd-HHmmss}{ext}";
    }
}
