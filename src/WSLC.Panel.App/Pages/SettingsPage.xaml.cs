using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WSLC.Panel.Core.Abstractions;
using WSLC.Panel.Core.Parsing;
using WSLC_Panel_App.Helpers;

namespace WSLC_Panel_App.Pages;

/// <summary>
/// 设置页（M9）。
/// 两块配置严格分开：① 面板自身偏好（prefs.json）；② wslc 引擎配置（settings.yaml）。
/// 引擎配置提供"表单 ↔ 原始 YAML"双模，且表单模式走逐行替换以保留注释（见 WslcSettingsEditor）。
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly IPrefsService _prefsService;
    private readonly IWslcSettingsService _engineSettings;

    private readonly Dictionary<string, TextBox> _fieldBoxes = new(StringComparer.OrdinalIgnoreCase);
    private bool _isLoaded;

    public SettingsPage()
    {
        InitializeComponent();

        _prefsService = App.Services.GetRequiredService<IPrefsService>();
        _engineSettings = App.Services.GetRequiredService<IWslcSettingsService>();

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
        RegistryTextBox.Text = prefs.DefaultRegistry;

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

    private async void RegistryTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;

        var registry = RegistryTextBox.Text.Trim();
        if (string.IsNullOrEmpty(registry))
        {
            registry = "docker.io";
            RegistryTextBox.Text = registry;
        }

        await SavePreferenceAsync(_prefsService.GetPreferences() with { DefaultRegistry = registry });
    }

    private async Task SavePreferenceAsync(WSLC.Panel.Core.Models.AppPreferences prefs)
    {
        await _prefsService.SaveAsync(prefs);

        var path = _prefsService is WSLC.Panel.Core.Services.PreferencesService concrete
            ? concrete.PreferencesFilePath
            : "prefs.json";

        ShowSaved($"面板偏好已保存到 {path}");
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
