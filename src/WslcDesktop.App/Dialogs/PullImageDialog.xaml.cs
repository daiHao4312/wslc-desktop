using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;
using WslcDesktop.Core.Services;

namespace WslcDesktop.Dialogs;

/// <summary>
/// 拉取镜像对话框：镜像源 / 仓库 / tag 三段分开填，可列 tag、量体积，
/// 底部实时显示归一化后的**真实拉取引用**。
///
/// 私有仓库的凭据来自「设置 → 私有仓库凭据」（<see cref="RegistryAuthService"/>），
/// 本对话框只负责"按当前镜像源自动取令牌"并回显状态，不再就地填账号密码 ——
/// 凭据只该有一个存放处。
///
/// 只在点「拉取」且校验通过时才把 <see cref="ResultReference"/> 填上；
/// 真正的拉取与进度展示留在镜像页（那里有就地进度面板，不弹第二个窗）。
/// </summary>
public sealed partial class PullImageDialog : ContentDialog
{
    private readonly IRegistryClient _registryClient;
    private readonly RegistryAuthService _auth;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>当前 <see cref="TagCombo"/> 里那批 tag 属于哪个 (registry, 仓库)；没列过就是 null。</summary>
    private string? _loadedTagKey;

    /// <summary>令牌，以及它属于哪个 registry（换源后必须作废）与是用谁换来的。</summary>
    private string? _token;
    private string? _tokenHost;
    private string _tokenKind = string.Empty;

    private bool _isBusy;
    private bool _isReady;
    private bool _tagsDropped;

    public PullImageDialog(
        IRegistryClient registryClient,
        RegistryAuthService auth,
        IReadOnlyList<string>? registryCandidates,
        string defaultRegistry)
    {
        InitializeComponent();

        _registryClient = registryClient;
        _auth = auth;

        // 候选 = 预设站点 + 用户在设置里添加的源。⚠️ 必须是纯主机名（见 MirrorPresets.Hosts 的说明）
        RegistryCombo.ItemsSource = registryCandidates ?? MirrorPresets.Hosts;
        RepositoryCombo.ItemsSource = MirrorPresets.CommonRepositories;

        var host = RegistryInput.NormalizeHost(defaultRegistry);
        RegistryCombo.Text = RegistryInput.IsValidHost(host) ? host : RegistryDefaults.Host;

        Closed += (_, _) => _cts.Cancel();

        _isReady = true;
        UpdatePreview();
    }

    /// <summary>校验通过的拉取引用；只在点了「拉取」时才有值。</summary>
    public string? ResultReference { get; private set; }

    /// <summary>私有仓库令牌；当前镜像源没存凭据就是 null。由调用方传给 <c>PullImageAsync</c>。</summary>
    public string? ResultRegistryAuth { get; private set; }

    private string EffectiveRegistry
    {
        get
        {
            var host = RegistryInput.NormalizeHost(RegistryCombo.Text);
            return RegistryInput.IsValidHost(host) ? host : RegistryDefaults.Host;
        }
    }

    private void PullFormField_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void PullFormField_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args) => UpdatePreview();

    /// <summary>
    /// 刷新归一化预览。**与真正拉取共用 <see cref="RegistryInput"/>**，
    /// 所以这里显示的就是会执行的引用，拼错在点之前就能看见。
    /// </summary>
    private void UpdatePreview()
    {
        if (!_isReady) return;

        _tagsDropped = false;

        // 令牌是发给某个 registry 的：换了源就必须作废，不能拿去问别的站
        DropStaleToken();

        if (!RegistryInput.TrySplitInput(RepositoryCombo.Text, EffectiveRegistry, out var reference, out var error))
        {
            // 仓库名都填不完整了，之前那批 tag 更不可能是当前目标的 —— 一并丢掉
            DropStaleTags(null);

            PreviewText.Text = "（填镜像仓库名后显示）";
            HintText.Text = RepositoryCombo.Text.Trim().Length == 0
                ? "短名（nginx）会自动补成 library/nginx；也可以直接粘一整条引用或 URL。" +
                  $"　可列 tag 的源：{string.Join(" / ", MirrorPresets.Sites.Where(s => s.CanListTags).Select(s => s.Host))}"
                : $"⚠ {error}";
            UpdateCredentialStatus();
            return;
        }

        // 用户粘了带 registry 的整条引用时，把它回填到镜像源框，避免两处不一致。
        // （注意 TrySplitInput 只做形状归一化、不做别名转换，所以这里不会被
        //  "docker.io 被改写成 registry-1.docker.io" 这种内部转换误触发。）
        if (!string.Equals(reference.Host, EffectiveRegistry, StringComparison.OrdinalIgnoreCase))
        {
            RegistryCombo.Text = reference.Host;
        }

        // ⚠️ 换镜像源/仓库之后，上一批 tag 已经不属于当前目标了。
        // 留着的话用户会从一个在新源上并不存在的 tag 里挑，然后拉取失败。
        DropStaleTags(TagKeyOf(reference));

        PreviewText.Text = $"wslc pull {reference.ToPullReference(TagCombo.Text.Trim())}";
        HintText.Text = BuildHint(reference);
        UpdateCredentialStatus();
    }

    private string BuildHint(RegistryReference reference)
    {
        var site = MirrorPresets.Find(reference.Host);
        var hasCredential = _auth.HasCredential(reference.Host);

        var hint = site is { } known
            ? known.CanListTags ? known.Note : $"{known.Note}想列 tag 请换 docker.1panel.live。"
            : hasCredential
                ? "自定义镜像源：拉取与列 tag 都会带上设置里保存的凭据。"
                : "这个镜像源不在已知表里：能拉，但可能列不出 tag。若是私有源，请到「设置 → 私有仓库凭据」添加凭据。";

        // 拉取引用用用户填的名字，真正问 registry 时才换成 API 端点 —— 说一句免得用户困惑
        if (!string.Equals(reference.Host, reference.ApiHost, StringComparison.OrdinalIgnoreCase))
        {
            hint += $"（查询 tag 时实际连的是 {reference.ApiHost}）";
        }

        if (_tagsDropped)
        {
            hint += "　tag 列表已清空（镜像源或仓库变了），请重新「列出 tag」。";
        }

        return hint;
    }

    /// <summary>
    /// tag 列表的归属键。
    ///
    /// 用 <see cref="RegistryReference.ApiHost"/> 而不是用户填的 Host：
    /// <c>docker.io</c> 与 <c>registry-1.docker.io</c> 问的是同一个 registry，
    /// 在两者之间切换不该被当成"换了源"而白白清空一次。
    /// </summary>
    private static string TagKeyOf(RegistryReference reference)
        => $"{reference.ApiHost}/{reference.Repository}";

    /// <summary>
    /// 当前目标的键与已加载 tag 的键不一致时，丢掉那批 tag（列表 + 选中值）。
    ///
    /// 只丢"我们从 registry 取回来的"那批；用户手打的 tag 在切换目标后同样清掉 ——
    /// 它多半也是照着旧源填的，留着比清掉更容易出事（拉一个不存在的 tag）。
    /// </summary>
    private void DropStaleTags(string? currentKey)
    {
        if (_loadedTagKey is null || _loadedTagKey == currentKey) return;

        TagCombo.ItemsSource = null;
        TagCombo.Text = string.Empty;
        _loadedTagKey = null;
        _tagsDropped = true;
    }

    private async void LoadTags_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        if (!RegistryInput.TrySplitInput(RepositoryCombo.Text, EffectiveRegistry, out var reference, out var error))
        {
            HintText.Text = $"⚠ {error}";
            return;
        }

        // 查之前先把不属于当前目标的旧列表丢掉：万一这次请求失败，
        // 界面上也不会留着上一批 tag 让人误以为它们属于当前仓库
        DropStaleTags(TagKeyOf(reference));

        _isBusy = true;
        LoadTagsButton.IsEnabled = false;
        HintText.Text = $"正在向 {reference.Host} 查询 tag…";

        try
        {
            // 私有仓库连 tags/list 都要令牌；存了凭据就必须先换到，换不到就别往下走
            var (token, tokenError) = await EnsureTokenAsync();
            if (tokenError.Length > 0)
            {
                HintText.Text = "⚠ 取令牌失败，没有继续查询。";
                ShowCredentialMessage($"⚠ {tokenError}");
                return;
            }

            // 「列不出 tag」的站点只在**没有**凭据时才提前拦下 ——
            // 存了凭据的私有源完全可能列得出来，拦在前面等于把功能关掉了
            var site = MirrorPresets.Find(reference.Host);
            if (token is null && site is not null && !site.Value.CanListTags)
            {
                HintText.Text = $"⚠ {reference.Host} 需要认证，列不出 tag。可以先在「设置 → 私有仓库凭据」里存凭据，或直接手填 tag 后拉取。";
                return;
            }

            var result = await _registryClient.GetTagsAsync(
                reference.Host,
                reference.Repository,
                token,
                _cts.Token);
            if (!IsLoaded) return;

            if (!result.Success)
            {
                HintText.Text = $"⚠ {result.Error}";
                return;
            }

            TagCombo.ItemsSource = result.Tags;
            TagCombo.Text = result.Tags.Count > 0 ? result.Tags[0] : string.Empty;

            // 记下这批 tag 的归属，镜像源/仓库一变就要丢掉它们
            _loadedTagKey = TagKeyOf(reference);

            HintText.Text = $"共 {result.Tags.Count} 个 tag（latest 优先 → 版本倒序），正在量体积…";
            UpdatePreview();

            await MeasureAsync(reference, token);
        }
        catch (OperationCanceledException)
        {
            // 对话框已关闭
        }
        catch (Exception ex)
        {
            HintText.Text = $"⚠ 查询失败：{ex.Message}";
        }
        finally
        {
            _isBusy = false;
            LoadTagsButton.IsEnabled = true;
        }
    }

    /// <summary>量一次选中 tag 的下载体积（config + 各层压缩后之和）。</summary>
    private async Task MeasureAsync(RegistryReference reference, string? bearerToken)
    {
        var tag = TagCombo.Text.Trim();
        if (tag.Length == 0) tag = reference.Tag ?? "latest";

        try
        {
            var size = await _registryClient.GetSizeAsync(
                reference.Host,
                reference.Repository,
                tag,
                MirrorPresets.CurrentArchitecture,
                bearerToken,
                _cts.Token);

            if (!IsLoaded) return;

            HintText.Text = size.Success
                ? $"下载约 {size.Display}（本机架构），压缩后的大小"
                : $"⚠ {size.Error}";
        }
        catch (OperationCanceledException)
        {
            // 对话框已关闭
        }
        catch
        {
            // 量体积失败不影响拉取，静默保持已有文案
        }
    }

    // ---------------- 私有仓库凭据 ----------------

    /// <summary>镜像源变了 → 令牌作废（它是对着某个 registry 换来的）。</summary>
    private void DropStaleToken()
    {
        if (_token is null) return;

        var host = RegistryDefaults.CanonicalHost(EffectiveRegistry);
        if (string.Equals(_tokenHost, host, StringComparison.OrdinalIgnoreCase)) return;

        _token = null;
        _tokenHost = null;
        _tokenKind = string.Empty;
    }

    /// <summary>
    /// 确保有一个能用于当前镜像源的令牌。
    ///
    /// 返回 <c>Token</c> 为 null 且 <c>Error</c> 为空 = 当前源没存凭据（公开镜像，不需要令牌）；
    /// <c>Error</c> 非空 = 存了凭据但换不到（密码不对、解不开、通道不可用），
    /// 调用方应当停下来而不是继续拿匿名去试。
    /// </summary>
    private async Task<(string? Token, string Error)> EnsureTokenAsync()
    {
        var host = RegistryDefaults.CanonicalHost(EffectiveRegistry);

        if (_token is not null && string.Equals(_tokenHost, host, StringComparison.OrdinalIgnoreCase))
        {
            return (_token, string.Empty);
        }

        var result = await _auth.AcquireAsync(host, _cts.Token);

        if (result.Error.Length > 0) return (null, result.Error);
        if (!result.HasToken) return (null, string.Empty);

        _token = result.Token;
        _tokenHost = host;
        _tokenKind = result.TokenKind;

        return (_token, string.Empty);
    }

    /// <summary>
    /// 回显"这次会用哪个身份"。凭据的增删改一律在设置页，
    /// 这里只说明现状，指路到设置 —— 不给第二个入口。
    /// </summary>
    private void UpdateCredentialStatus()
    {
        var host = EffectiveRegistry;
        var username = _auth.UsernameFor(host);

        if (username is null)
        {
            CredentialStatusText.Text =
                "当前镜像源没有保存凭据：公开镜像不需要。私有仓库请到「设置 → 私有仓库凭据」里添加。";
            return;
        }

        CredentialStatusText.Text = _token is not null
            ? $"已用设置里保存的凭据换到令牌（用户 {username}，类型 {_tokenKind}）。"
            : $"将使用设置里保存的凭据（用户 {username}）向 {host} 换取令牌。";
    }

    /// <summary>取令牌失败时把原因显示在凭据那一行（它才是用户此刻关心的）。</summary>
    private void ShowCredentialMessage(string message) => CredentialStatusText.Text = message;

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!RegistryInput.TrySplitInput(RepositoryCombo.Text, EffectiveRegistry, out var reference, out var error))
        {
            // 校验不过就别关：把原因留在对话框里，用户改一个字就能重试
            args.Cancel = true;
            HintText.Text = $"⚠ {error}";
            return;
        }

        var tag = TagCombo.Text.Trim();

        // 当前源存了凭据就得先换到令牌再关窗：拿不到令牌时留在对话框里，比关掉再报错好定位
        if (_auth.HasCredential(reference.Host) && _token is null)
        {
            var deferral = args.GetDeferral();
            _ = AcquireAndFinishAsync(reference, tag, args, deferral);
            return;
        }

        ResultRegistryAuth = _token;
        ResultReference = reference.ToPullReference(tag.Length > 0 ? tag : null);
    }

    private async Task AcquireAndFinishAsync(
        RegistryReference reference,
        string tag,
        ContentDialogButtonClickEventArgs args,
        ContentDialogButtonClickDeferral deferral)
    {
        try
        {
            var (token, error) = await EnsureTokenAsync();
            if (error.Length > 0)
            {
                args.Cancel = true;
                ShowCredentialMessage($"⚠ {error}");
                return;
            }

            ResultRegistryAuth = token;
            ResultReference = reference.ToPullReference(tag.Length > 0 ? tag : null);
        }
        catch (OperationCanceledException)
        {
            args.Cancel = true;
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            ShowCredentialMessage($"⚠ {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }
}
