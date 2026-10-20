using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WslcDesktop.Helpers;

/// <summary>
/// 对话框纪律（§7.2）：破坏性操作一律说明影响对象 + 明示不可撤销 + 默认焦点落在"取消"。
/// 全部集中在这里，避免各页面各写一套。
///
/// XamlRoot 判空是公共防线：定时器刷新期间切页后，await 续延仍在已卸载页面上跑，
/// 此时 XamlRoot 已置空，ContentDialog.ShowAsync 会抛异常并升级为全局未处理错误框。
/// 判空后静默返回，调用方（含 fire-and-forget 的 _ = ShowErrorAsync(...)）零改动即受保护。
/// </summary>
public static class DialogHelper
{
    /// <summary>破坏性操作确认。返回 true 表示用户明确确认。</summary>
    public static async Task<bool> ConfirmDestructiveAsync(
        XamlRoot? xamlRoot,
        string title,
        string message,
        string confirmText = "确认删除")
    {
        if (xamlRoot is null) return false;

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>普通确认（非破坏性：重启、启停等）。</summary>
    public static async Task<bool> ConfirmAsync(
        XamlRoot? xamlRoot,
        string title,
        string message,
        string confirmText = "确定")
    {
        if (xamlRoot is null) return false;

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>单行文本输入。取消或空输入返回 null。</summary>
    public static async Task<string?> PromptTextAsync(
        XamlRoot? xamlRoot,
        string title,
        string placeholder,
        string confirmText = "确定",
        string? helperText = null)
    {
        if (xamlRoot is null) return null;

        var input = new TextBox { PlaceholderText = placeholder };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(input);

        if (!string.IsNullOrWhiteSpace(helperText))
        {
            panel.Children.Add(new TextBlock
            {
                Text = helperText,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.7
            });
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = confirmText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;

        var text = input.Text.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>只读内容展示（日志、inspect JSON、等效命令预览）。</summary>
    public static async Task ShowContentAsync(
        XamlRoot? xamlRoot,
        string title,
        string content,
        string closeText = "关闭")
    {
        if (xamlRoot is null) return;

        var box = new ScrollViewer
        {
            MaxHeight = 460,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock
            {
                Text = content,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 12,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.NoWrap
            }
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot
        };

        await dialog.ShowAsync();
    }

    /// <summary>
    /// 多行文本编辑（如发行版内的 <c>/etc/wsl.conf</c>）。取消返回 null。
    ///
    /// 内容面板给**固定宽度**：ContentDialog 自己不吃 MinWidth/MaxWidth，
    /// 靠内容撑开才是可靠的（见项目里"ContentDialog 宽度"那条踩坑记录）。
    /// </summary>
    public static async Task<string?> PromptMultilineAsync(
        XamlRoot? xamlRoot,
        string title,
        string text,
        string? helperText = null,
        string confirmText = "保存")
    {
        if (xamlRoot is null) return null;

        var input = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            Height = 280
        };

        ScrollViewer.SetHorizontalScrollBarVisibility(input, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(input, ScrollBarVisibility.Auto);

        var panel = new StackPanel { Spacing = 8, Width = 640 };
        panel.Children.Add(input);

        if (!string.IsNullOrWhiteSpace(helperText))
        {
            panel.Children.Add(new TextBlock
            {
                Text = helperText,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.7
            });
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = confirmText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text : null;
    }

    /// <summary>错误提示（引擎不可用、命令失败）。</summary>
    public static async Task ShowErrorAsync(XamlRoot? xamlRoot, string title, string message)
    {
        if (xamlRoot is null) return;

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot
        };

        await dialog.ShowAsync();
    }
}
