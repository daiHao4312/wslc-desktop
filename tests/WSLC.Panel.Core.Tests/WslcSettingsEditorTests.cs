using WSLC.Panel.Core.Parsing;

namespace WSLC.Panel.Core.Tests;

/// <summary>
/// settings.yaml 编辑器测试。
/// 核心验收点：① 能读懂模板里被注释掉的键；② 写值时**保留注释与排版**；③ 不误伤其它键。
/// 夹具为参考项目真机抓取的官方模板 settings.yaml。
/// </summary>
public class WslcSettingsEditorTests
{
    private static string Template()
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "settings.yaml"));

    [Fact]
    public void TestRead_CommentedTemplateHasNoActiveKeys()
    {
        var values = WslcSettingsEditor.Read(Template());

        // 官方模板把键全注释了，因此生效键为空
        Assert.Empty(values);
    }

    [Fact]
    public void TestRead_ParsesActiveKeys()
    {
        var yaml = "session:\n  cpuCount: 4\n  memorySize: 2GB\n# comment\ncredentialStore: file\n";

        var values = WslcSettingsEditor.Read(yaml);

        Assert.Equal("4", values["session.cpuCount"]);
        Assert.Equal("2GB", values["session.memorySize"]);
        Assert.Equal("file", values["credentialStore"]);
    }

    [Fact]
    public void TestSet_UncommentsTemplateLineAndKeepsComments()
    {
        var yaml = WslcSettingsEditor.Set(Template(), WslcSettingsKeys.SessionCpuCount, "4");

        var values = WslcSettingsEditor.Read(yaml);
        Assert.Equal("4", values[WslcSettingsKeys.SessionCpuCount]);

        // 注释与说明必须原样保留
        Assert.Contains("# wslc user settings", yaml);
        Assert.Contains("# Number of virtual CPUs allocated to the session", yaml);
        Assert.Contains("# Credential storage backend", yaml);

        // 其它未改动的键仍然是注释状态
        Assert.Contains("# memorySize: default", yaml);
    }

    [Fact]
    public void TestSet_ReplacesExistingValue()
    {
        var yaml = "session:\n  cpuCount: 2\n";

        var updated = WslcSettingsEditor.Set(yaml, WslcSettingsKeys.SessionCpuCount, "8");

        Assert.Equal("8", WslcSettingsEditor.Read(updated)["session.cpuCount"]);
        Assert.Single(updated.Trim().Split('\n'), l => l.Contains("cpuCount"));
    }

    [Fact]
    public void TestSet_NullValueRestoresDefault()
    {
        var yaml = "session:\n  cpuCount: 8\n";

        var updated = WslcSettingsEditor.Set(yaml, WslcSettingsKeys.SessionCpuCount, null);

        Assert.Equal("default", WslcSettingsEditor.Read(updated)["session.cpuCount"]);
    }

    [Fact]
    public void TestSet_MultipleKeysAccumulate()
    {
        var yaml = Template();

        yaml = WslcSettingsEditor.Set(yaml, WslcSettingsKeys.SessionCpuCount, "4");
        yaml = WslcSettingsEditor.Set(yaml, WslcSettingsKeys.SessionMemorySize, "2GB");
        yaml = WslcSettingsEditor.Set(yaml, WslcSettingsKeys.CredentialStore, "file");

        var values = WslcSettingsEditor.Read(yaml);

        Assert.Equal("4", values[WslcSettingsKeys.SessionCpuCount]);
        Assert.Equal("2GB", values[WslcSettingsKeys.SessionMemorySize]);
        Assert.Equal("file", values[WslcSettingsKeys.CredentialStore]);
        Assert.Contains("# wslc user settings", yaml);
    }

    [Fact]
    public void TestSet_AppendsWhenTemplateLineMissing()
    {
        var yaml = "session:\n  cpuCount: 1\n";

        var updated = WslcSettingsEditor.Set(yaml, WslcSettingsKeys.SessionHostLoopback, "none");

        Assert.Equal("none", WslcSettingsEditor.Read(updated)[WslcSettingsKeys.SessionHostLoopback]);

        var lines = updated.Split('\n');
        var sessionIndex = Array.FindIndex(lines, l => l.StartsWith("session:"));
        var hostIndex = Array.FindIndex(lines, l => l.Contains("hostLoopback"));
        Assert.True(hostIndex > sessionIndex, "新键必须写在所属段落内");
    }

    [Fact]
    public void TestSet_TopLevelKeyNotNestedUnderSection()
    {
        var yaml = WslcSettingsEditor.Set("session:\n  cpuCount: 1\n", WslcSettingsKeys.CredentialStore, "wincred");

        Assert.Equal("wincred", WslcSettingsEditor.Read(yaml)[WslcSettingsKeys.CredentialStore]);
        Assert.Contains("credentialStore: wincred", yaml);
    }

    [Fact]
    public void TestSet_ValueWithNewlineIsFlattened()
    {
        // 值里的换行会凭空造出一个新行、破坏 YAML 结构：必须压成空格
        var yaml = "session:\n  cpuCount: default\n";

        var updated = WslcSettingsEditor.Set(yaml, WslcSettingsKeys.SessionCpuCount, "2\nfoo: bar");

        Assert.Equal("2 foo: bar", WslcSettingsEditor.Read(updated)[WslcSettingsKeys.SessionCpuCount]);

        // 不能出现独立的 "foo: bar" 行——那说明换行把值拆成了新键
        var lines = updated.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith("foo:", StringComparison.Ordinal));
    }

    [Fact]
    public void TestFormFields_CoverSessionSectionAndFlagStoragePath()
    {
        var keys = WslcSettingsKeys.FormFields.Select(f => f.Key).ToList();

        Assert.Contains(WslcSettingsKeys.SessionCpuCount, keys);
        Assert.Contains(WslcSettingsKeys.CredentialStore, keys);

        var storagePath = WslcSettingsKeys.FormFields.Single(f => f.Key == WslcSettingsKeys.SessionStoragePath);
        Assert.True(storagePath.IsRisky, "改 storagePath 会新建空会话，必须标记为危险项");
    }
}
