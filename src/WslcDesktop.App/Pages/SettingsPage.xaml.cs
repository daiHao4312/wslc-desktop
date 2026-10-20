using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;
using WslcDesktop.Core.Services;
using WslcDesktop.Dialogs;
using WslcDesktop.Helpers;

namespace WslcDesktop.Pages;

/// <summary>
/// 设置页（M9）。
/// 三块配置严格分开：① 面板自身偏好（prefs.json）；② 镜像源与私有仓库凭据（同在 prefs.json，
/// 但密码是 DPAPI 密文）；③ wslc 引擎配置（settings.yaml）。
/// 引擎配置提供"表单 ↔ 原始 YAML"双模，且表单模式走逐行替换以保留注释（见 WslcSettingsEditor）。
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly IPrefsService _prefsService;
    private readonly IWslcSettingsService _engineSettings;

    /// <summary>镜像源与凭据的业务规则层（默认唯一、内置不可删、删源连带删凭据…都在 Core 里）。</summary>
    private readonly RegistrySourceService _registrySources;

    /// <summary>保存凭据后顺手换一次令牌，把"密码对不对"当场问清楚。</summary>
    private readonly RegistryAuthService _registryAuth;

    /// <summary>官方 API 通道：只用于给设置页显示"现在到底能不能用"。</summary>
    private readonly IWslcApiChannel? _apiChannel;

    private readonly Dictionary<string, TextBox> _fieldBoxes = new(StringComparer.OrdinalIgnoreCase);
    private bool _isLoaded;

    public SettingsPage()
    {
        InitializeComponent();

        _prefsService = App.Services.GetRequiredService<IPrefsService>();
        _engineSettings = App.Services.GetRequiredService<IWslcSettingsService>();
        _registrySources = App.Services.GetRequiredService<RegistrySourceService>();
        _registryAuth = App.Services.GetRequiredService<RegistryAuthService>();

        // API 通道只用来"显示当前可用性"，取不到就让状态行说明不可用
        _apiChannel = App.Services.GetService<IWslcApiChannel>();

        EngineConfigSelector.SelectedItem = FormModeTab;
        BuildFormFields();
    }

    /// <summary>
    /// 「表单模式 / 原始 YAML」切换：两块内容叠在同一个容器里只切 Visibility，
    /// 和容器详情弹窗同一套做法。
    /// </summary>
    private void EngineConfigSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var rawYaml = ReferenceEquals(sender.SelectedItem, RawYamlTab);

        FormModePane.Visibility = rawYaml ? Visibility.Collapsed : Visibility.Visible;
        RawYamlPane.Visibility = rawYaml ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>从本页资源里取样式；取不到返回 null，让控件走默认外观而不是抛异常。</summary>
    private Style? RowStyle(string key)
        => Resources.TryGetValue(key, out var value) && value is Style style ? style : null;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadPreferences();
        await LoadEngineConfigAsync();
    }

    // ---------- 面板自身偏好 ----------

    private void LoadPreferences()
    {
        _isLoaded = false;
        var prefs = _prefsService.GetPreferences();

        // 没有预设匹配项时，把实际生效值动态加一项并选中：直接 fallback 到默认项会让
        // 用户看到的值和真实生效值不一致（且因 _isLoaded=false 不会被保存纠正），直到下次改动
        if (string.IsNullOrEmpty(prefs.Theme))
        {
            ThemeComboBox.SelectedItem = ThemeComboBox.Items[0];
        }
        else
        {
            SelectOrAdd(ThemeComboBox, prefs.Theme);
        }

        SelectOrAdd(RefreshIntervalComboBox, prefs.RefreshIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        AutoRefreshToggle.IsOn = prefs.AutoRefresh;
        EventStreamToggle.IsOn = prefs.UseEventStream;
        CloseToTrayToggle.IsOn = prefs.CloseToTray;
        ApiChannelToggle.IsOn = prefs.UseApiChannel;
        UpdateRepoTextBox.Text = prefs.UpdateRepo;

        ReloadRegistryRows();

        // 探测可能要几百毫秒：不阻塞页面其余部分（状态行自己从"正在探测…"变成结果）
        _ = RefreshApiChannelStatusAsync();

        _isLoaded = true;
    }

    /// <summary>按 Tag 选中；没有匹配项时追加一项"自定义值"并选中它。</summary>
    private static void SelectOrAdd(ComboBox combo, string tagValue)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && string.Equals(tag, tagValue, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        var extra = new ComboBoxItem { Content = $"{tagValue}（自定义）", Tag = tagValue };
        combo.Items.Add(extra);
        combo.SelectedItem = extra;
    }

    private async void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded || ThemeComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string theme) return;

        await SavePreferenceAsync(_prefsService.GetPreferences() with { Theme = theme });
        App.ApplyTheme(theme);
    }

    private async void RefreshIntervalComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded || RefreshIntervalComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string intervalText) return;
        if (!int.TryParse(intervalText, out var interval)) return;

        await SavePreferenceAsync(_prefsService.GetPreferences() with { RefreshIntervalSeconds = interval });
        App.MainWindow?.ApplyRefreshSettings();
    }

    private async void AutoRefreshToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;

        await SavePreferenceAsync(_prefsService.GetPreferences() with { AutoRefresh = AutoRefreshToggle.IsOn });
    }

    private async void EventStreamToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;

        var enabled = EventStreamToggle.IsOn;

        await SavePreferenceAsync(_prefsService.GetPreferences() with { UseEventStream = enabled });

        // 立即生效：开则拉起长驻订阅、关则收掉并把轮询恢复成用户设定的周期
        App.MainWindow?.ApplyEventStreamEnabled(enabled);
    }

    private async void CloseToTrayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;

        await SavePreferenceAsync(_prefsService.GetPreferences() with { CloseToTray = CloseToTrayToggle.IsOn });
    }

    private async void ApiChannelToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;

        // 通道侧每轮探测都会重新读这个开关（关闭态不缓存），所以这里只需落盘即可即时生效
        await SavePreferenceAsync(_prefsService.GetPreferences() with { UseApiChannel = ApiChannelToggle.IsOn });

        await RefreshApiChannelStatusAsync();
    }

    /// <summary>
    /// 显示 API 通道的真实可用性。
    ///
    /// 为什么必须有这一行：会话被 CLI 进程独占时（0x80040607 SessionReserved）通道只能"只读附着"，
    /// 而回落到 CLI 是**静默**的 —— 不显示状态，用户会一直以为自己在用 API 加速。
    /// </summary>
    private async Task RefreshApiChannelStatusAsync()
    {
        if (ApiChannelStatusText is null) return;

        if (_apiChannel is null)
        {
            ApiChannelStatusText.Text = "状态：通道未注册（始终走 CLI）";
            return;
        }

        try
        {
            var capabilities = await _apiChannel.ProbeAsync();

            ApiChannelStatusText.Text = capabilities.Enabled && capabilities.AttachedSessionName is not null
                ? $"状态：可用 · 会话「{capabilities.AttachedSessionName}」" +
                  (capabilities.AttachedReadOnly ? "（只读附着：会话由 CLI 进程独占）" : string.Empty)
                : $"状态：不可用 → 走 CLI（{capabilities.Reason ?? "原因未知"}）";
        }
        catch (Exception ex)
        {
            ApiChannelStatusText.Text = $"状态：探测失败 → 走 CLI（{ex.Message}）";
        }
    }

    /// <summary>
    /// 更新检查仓库：留空即关闭检查（不像 Registry 那样强行回填默认值——
    /// "不想连网检查"是合法选择）。格式不对不在这里拦，关于页检查时会给出可读提示。
    /// </summary>
    private async void UpdateRepoTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;

        var repo = UpdateRepoTextBox.Text.Trim().Trim('/');
        UpdateRepoTextBox.Text = repo;

        await SavePreferenceAsync(_prefsService.GetPreferences() with { UpdateRepo = repo });
    }

    private async Task SavePreferenceAsync(WslcDesktop.Core.Models.AppPreferences prefs)
    {
        await _prefsService.SaveAsync(prefs);

        var path = _prefsService is WslcDesktop.Core.Services.PreferencesService concrete
            ? concrete.PreferencesFilePath
            : "prefs.json";

        ShowSaved($"面板偏好已保存到 {path}");
    }

    // ---------- 镜像源 ----------

    /// <summary>
    /// 重新装载列表。
    ///
    /// 业务规则全在 <see cref="RegistrySourceService"/> 里，这里只做三段：
    /// 取视图 → 按搜索词过滤 → 交给 ListView。**不在这里判断"能不能删""是不是内置"** ——
    /// 那些判断写在 code-behind 里，就是上一版变成屎山的直接原因。
    /// </summary>
    private void ReloadRegistryRows()
    {
        var rows = _registrySources.GetAll()
            .Where(source => source.Matches(RegistrySearchBox.Text))
            .ToList();

        RegistryListView.ItemsSource = rows;
        RegistryEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RegistrySearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // ProgrammaticChange 是代码赋值触发的（例如页面装载），没必要重排一次
        if (args.Reason == AutoSuggestionBoxTextChangeReason.ProgrammaticChange) return;

        ReloadRegistryRows();
    }

    private async void AddRegistry_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RegistrySourceDialog(source: null) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await ApplyRegistryChangeAsync(
            async () =>
            {
                var result = await _registrySources.AddAsync(
                    dialog.ResultHost,
                    dialog.ResultNote,
                    dialog.HasUsername ? dialog.ResultUsername : null,
                    dialog.ResultPassword);

                return result.Added
                    ? $"已添加镜像源 {result.Source.Host}。"
                    : $"「{result.Source.Host}」本来就在列表里，已恢复显示。";
            },
            testCredentialFor: dialog.HasUsername ? dialog.ResultHost : null);
    }

    private async void EditRegistry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RegistrySource source }) return;

        var dialog = new RegistrySourceDialog(source) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await ApplyRegistryChangeAsync(
            async () =>
            {
                if (!source.IsBuiltIn) await _registrySources.UpdateNoteAsync(source.Host, dialog.ResultNote);

                // 用户名清空 = 这个源不要凭据了（对话框已保证"原本就没有凭据"时不会误清）
                if (!dialog.HasUsername)
                {
                    if (!source.HasCredential) return $"已保存 {source.Host} 的设置。";

                    await _registrySources.ClearCredentialAsync(source.Host);
                    return $"已保存 {source.Host} 的设置（凭据已清除）。";
                }

                // 密码留空 = 保持原密码（对话框在"原本没有凭据"时会拦下留空）
                if (dialog.ResultPassword.Length == 0) return $"已保存 {source.Host} 的设置。";

                await _registrySources.SetCredentialAsync(source.Host, dialog.ResultUsername, dialog.ResultPassword);
                return $"已保存 {source.Host} 的设置。";
            },
            testCredentialFor: dialog.HasUsername && dialog.ResultPassword.Length > 0 ? source.Host : null);
    }

    private async void RegistryEnabled_Checked(object sender, RoutedEventArgs e)
        => await ToggleRegistryAsync(sender, enabled: true);

    private async void RegistryEnabled_Unchecked(object sender, RoutedEventArgs e)
        => await ToggleRegistryAsync(sender, enabled: false);

    private async Task ToggleRegistryAsync(object sender, bool enabled)
    {
        if (sender is not FrameworkElement { DataContext: RegistrySource source }) return;

        // ⚠️ OneWay 绑定初始化时也会触发 Checked/Unchecked。
        // 数据已经是这个值 = 这是回填而不是用户操作，直接返回（否则会自我循环写盘）。
        if (source.Enabled == enabled) return;

        await ApplyRegistryChangeAsync(async () =>
        {
            await _registrySources.SetEnabledAsync(source.Host, enabled);

            return enabled
                ? $"{source.Host} 已回到「拉取镜像」的下拉里。"
                : $"{source.Host} 已从下拉里移除（地址仍可手填拉取）。";
        });
    }

    private async void RegistryDefault_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RegistrySource source }) return;

        if (source.IsDefault) return; // 同上：绑定回填不算操作

        await ApplyRegistryChangeAsync(async () =>
        {
            await _registrySources.SetDefaultAsync(source.Host);
            return $"默认镜像源已改为 {source.Host}。";
        });
    }

    private async void RemoveRegistry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RegistrySource source }) return;

        var confirmed = await DialogHelper.ConfirmDestructiveAsync(
            XamlRoot,
            $"删除镜像源 {source.Host}？",
            "会连同它的凭据一起删掉。内置站点不能删除 —— 取消「显示」就能把它从下拉里去掉。",
            "删除");

        if (!confirmed) return;

        await ApplyRegistryChangeAsync(async () =>
        {
            await _registrySources.RemoveAsync(source.Host);
            return $"已删除镜像源 {source.Host}。";
        });
    }

    /// <summary>
    /// 执行一次镜像源改动：统一处理"业务拒绝"与"保存后自检"。
    ///
    /// 业务层用异常表达拒绝（默认源不能停用、内置不能删…），消息本身就是给用户看的，
    /// 所以这里原样展示；<paramref name="testCredentialFor"/> 非空时顺手换一次令牌，
    /// 把"密码对不对"当场问清楚 —— 不用等下一次拉取失败才发现。
    /// </summary>
    private async Task ApplyRegistryChangeAsync(Func<Task<string>> action, string? testCredentialFor = null)
    {
        try
        {
            var message = await action();
            var severity = InfoBarSeverity.Success;

            if (testCredentialFor is not null)
            {
                var probe = await _registryAuth.AcquireAsync(testCredentialFor);

                if (probe.HasToken)
                {
                    message += $" 已换到令牌（类型 {probe.TokenKind}），凭据可用。";
                }
                else
                {
                    message += $" 但凭据暂时换不到令牌：{probe.Error}";
                    severity = InfoBarSeverity.Warning;
                }
            }

            ReloadRegistryRows();
            ShowRegistryMessage(message, severity);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // 业务规则的拒绝（默认源不能停用、内置不能删…）：消息本身就是给用户看的，原样展示
            ReloadRegistryRows();
            ShowRegistryMessage(ex.Message, InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            // 兜底：设置页不该因为一次写盘/IO 失败把整个应用带崩
            ReloadRegistryRows();
            ShowRegistryMessage($"操作失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void ShowRegistryMessage(string message, InfoBarSeverity severity)
    {
        RegistryInfoBar.Severity = severity;
        RegistryInfoBar.Message = message;
        RegistryInfoBar.IsOpen = true;
    }

    // ---------- 引擎配置 ----------

    private void BuildFormFields()
    {
        var isFirst = true;

        foreach (var (key, label, hint, isRisky) in WslcSettingsKeys.FormFields)
        {
            var row = new Grid { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });

            // 样式全部取自本页资源：之前这里自带一套 FontSize/Opacity/硬编码 OrangeRed，
            // 上半页的偏好卡片用的是另一套，同一个页面两种提示文案外观。
            var left = new StackPanel { Spacing = 4 };
            left.Children.Add(new TextBlock { Text = label, Style = RowStyle("RowTitleStyle") });
            left.Children.Add(new TextBlock { Text = hint, Style = RowStyle("RowHintStyle") });

            if (isRisky)
            {
                left.Children.Add(new TextBlock
                {
                    Text = "⚠ 该项会影响既有数据",
                    Style = RowStyle("RiskyHintStyle")
                });
            }

            Grid.SetColumn(left, 0);
            row.Children.Add(left);

            var box = new TextBox { PlaceholderText = "default", VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);

            _fieldBoxes[key] = box;

            FormFieldsPanel.Children.Add(new Border
            {
                Style = RowStyle(isFirst ? "SettingsRowFirstStyle" : "SettingsRowStyle"),
                Child = row
            });

            isFirst = false;
        }
    }

    private async Task LoadEngineConfigAsync()
    {
        EngineConfigPathText.Text = _engineSettings.SettingsFilePath;

        var yaml = await _engineSettings.ReadAsync();

        RawYamlBox.Text = yaml;

        var values = WslcSettingsEditor.Read(yaml);
        foreach (var (key, box) in _fieldBoxes)
        {
            box.Text = values.TryGetValue(key, out var value) && !string.Equals(value, "default", StringComparison.OrdinalIgnoreCase)
                ? value
                : string.Empty;
        }
    }

    private async void ReloadEngineConfig_Click(object sender, RoutedEventArgs e)
        => await LoadEngineConfigAsync();

    private async void SaveForm_Click(object sender, RoutedEventArgs e)
    {
        var yaml = await _engineSettings.ReadAsync();
        if (string.IsNullOrEmpty(yaml))
        {
            ShowEngineMessage("读取 settings.yaml 失败，无法保存。", InfoBarSeverity.Error);
            return;
        }

        var original = WslcSettingsEditor.Read(yaml);
        var changedKeys = new List<string>();

        foreach (var (key, box) in _fieldBoxes)
        {
            var newValue = box.Text.Trim();
            original.TryGetValue(key, out var oldValue);
            oldValue ??= "default";

            var normalizedNew = string.IsNullOrEmpty(newValue) ? "default" : newValue;
            if (string.Equals(normalizedNew, oldValue, StringComparison.OrdinalIgnoreCase)) continue;

            yaml = WslcSettingsEditor.Set(yaml, key, newValue);
            changedKeys.Add(key);
        }

        if (changedKeys.Count == 0)
        {
            ShowEngineMessage("没有需要保存的改动。", InfoBarSeverity.Informational);
            return;
        }

        if (changedKeys.Contains(WslcSettingsKeys.SessionStoragePath))
        {
            var confirmed = await DialogHelper.ConfirmAsync(
                XamlRoot,
                "修改会话存储目录？",
                "该改动不会迁移已有会话数据：新会话会建在新路径下，旧目录与其内容原样保留（磁盘占用不会释放）。确认继续？",
                "确认修改");

            if (!confirmed)
            {
                await LoadEngineConfigAsync();
                return;
            }
        }

        await SaveEngineConfigAsync(yaml, changedKeys);
    }

    private async void SaveRaw_Click(object sender, RoutedEventArgs e)
        => await SaveEngineConfigAsync(RawYamlBox.Text, new List<string> { "原始 YAML 全文" });

    private async Task SaveEngineConfigAsync(string yaml, IReadOnlyList<string> changedKeys)
    {
        try
        {
            var backupPath = await _engineSettings.SaveAsync(yaml);

            var message = $"已保存：{string.Join("、", changedKeys)}。";
            if (!string.IsNullOrEmpty(backupPath)) message += $"\n备份：{backupPath}";

            ShowEngineMessage(message, InfoBarSeverity.Success);
            await LoadEngineConfigAsync();
        }
        catch (Exception ex)
        {
            ShowEngineMessage($"保存失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void ShowBackups_Click(object sender, RoutedEventArgs e)
    {
        var backups = _engineSettings.GetBackups();
        var text = backups.Count == 0
            ? "还没有备份。首次保存时会自动生成。"
            : string.Join(Environment.NewLine, backups);

        _ = DialogHelper.ShowContentAsync(XamlRoot, "settings.yaml 备份记录", text);
    }

    private void ShowEngineMessage(string message, InfoBarSeverity severity)
    {
        EngineInfoBar.Severity = severity;
        EngineInfoBar.Message = message;
        EngineInfoBar.IsOpen = true;
    }

    private void ShowSaved(string message)
    {
        SavedInfoBar.Message = message;
        SavedInfoBar.IsOpen = true;
    }
}
