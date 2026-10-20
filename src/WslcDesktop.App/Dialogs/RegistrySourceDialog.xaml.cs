using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Services;

namespace WslcDesktop.Dialogs;

/// <summary>
/// 添加 / 编辑一个镜像源：主机名、备注、凭据。
///
/// 一个弹窗管一个实体（镜像源），凭据是它的一个属性 —— 所以不再有单独的「凭据」对话框，
/// 也就不会出现"凭据卡里能加一个列表里没有的源"这种不一致。
///
/// 只负责收集与**本地格式校验**；业务规则（默认唯一、内置不可删…）在
/// <see cref="RegistrySourceService"/> 里，加密在 <c>RegistrySourceStore</c> 里。
/// </summary>
public sealed partial class RegistrySourceDialog : ContentDialog
{
    private readonly bool _isNew;
    private readonly bool _hasExistingCredential;

    /// <param name="source">传 null = 新增；传现有行 = 编辑。</param>
    public RegistrySourceDialog(RegistrySource? source)
    {
        InitializeComponent();

        _isNew = source is null;
        _hasExistingCredential = source?.HasCredential ?? false;

        Title = _isNew ? "添加镜像源" : "编辑镜像源";
        PrimaryButtonText = "保存";

        if (source is not null)
        {
            // 主机名是这一行的标识，改了等于换一个源 —— 编辑态一律只读，要换地址请新增
            HostBox.Text = source.Host;
            HostBox.IsReadOnly = true;
            NoteBox.Text = source.Note;
            UsernameBox.Text = source.Username ?? string.Empty;
        }

        if (source?.IsBuiltIn == true)
        {
            NoteBox.IsReadOnly = true;
            BuiltInNoteHint.Visibility = Visibility.Visible;
        }

        PasswordHint.Text = _hasExistingCredential
            ? "已配置凭据。密码留空表示保持原密码；用户名留空表示清除凭据。"
            : "公开镜像不需要填。密码用 Windows DPAPI 加密后存在本机 prefs.json，明文不落盘。";
    }

    public string ResultHost { get; private set; } = string.Empty;

    public string ResultNote { get; private set; } = string.Empty;

    public string ResultUsername { get; private set; } = string.Empty;

    public string ResultPassword { get; private set; } = string.Empty;

    /// <summary>用户填了用户名 = 要保留/设置凭据；留空 = 清除（或本来就没有）。</summary>
    public bool HasUsername => ResultUsername.Length > 0;

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var username = UsernameBox.Text.Trim();
        var password = PasswordInput.Password;
        var note = NoteBox.Text.Trim();

        if (note.Length > RegistrySourceService.NoteMaxLength)
        {
            args.Cancel = true;
            ShowError($"备注最多 {RegistrySourceService.NoteMaxLength} 个字符（现在 {note.Length} 个）。");
            return;
        }

        // 只有"本来没有凭据"时留空密码才算错；编辑已有凭据时留空 = 不改密码
        if (username.Length > 0 && password.Length == 0 && !_hasExistingCredential)
        {
            args.Cancel = true;
            ShowError("填了用户名就要同时填密码（或访问令牌）。只想用公开镜像，把用户名清空即可。");
            return;
        }

        if (_isNew)
        {
            if (!RegistrySourceService.TryValidateHost(HostBox.Text, out var host, out var error))
            {
                args.Cancel = true;
                ShowError(error);
                return;
            }

            ResultHost = host;
        }
        else
        {
            ResultHost = HostBox.Text.Trim();
        }

        ResultNote = note;
        ResultUsername = username;
        ResultPassword = password;
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
