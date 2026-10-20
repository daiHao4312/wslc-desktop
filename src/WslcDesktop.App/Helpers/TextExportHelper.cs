using Windows.Storage;
using Windows.Storage.Pickers;

namespace WslcDesktop.Helpers;

/// <summary>
/// 把界面上的文本（日志 / 命令输出）另存为文件。
///
/// ⚠️ 非打包应用（WindowsPackageType=None）**必须**把 picker 绑到窗口 HWND，
/// 否则会以 COM 失败告终、连对话框都不弹 —— 和发行版导出是同一个坑。
/// </summary>
public static class TextExportHelper
{
    /// <summary>弹出"另存为"并写入内容。用户取消返回 null。</summary>
    public static async Task<string?> SaveTextAsync(
        string suggestedFileName,
        string fileTypeLabel,
        string extension,
        string content)
    {
        var window = App.MainWindow;
        if (window is null) return null;

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedFileName
        };

        picker.FileTypeChoices.Add(fileTypeLabel, new List<string> { extension });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

        var file = await picker.PickSaveFileAsync();
        if (file is null) return null;

        // 走 FileIO 而不是 File.WriteAllText：picker 授予的访问权限只对 StorageFile 有效
        await FileIO.WriteTextAsync(file, content);

        return file.Path;
    }

    /// <summary>
    /// 建议文件名：<c>前缀-yyyyMMdd-HHmmss.txt</c>，并去掉文件名非法字符
    /// （规则在 Core 的 <see cref="WslcDesktop.Core.Parsing.ExportName"/>，那边有单测）。
    /// </summary>
    public static string SuggestName(string prefix, string extension)
        => WslcDesktop.Core.Parsing.ExportName.Suggest(prefix, extension, DateTimeOffset.Now);
}
