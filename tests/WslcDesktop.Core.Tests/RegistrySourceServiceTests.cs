using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;
using WslcDesktop.Core.Services;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// 镜像源的**业务规则**（M16d）。这一组测试就是"什么能做什么不能做"的规格说明：
///
/// ① 默认源必须存在且启用，且全局唯一；
/// ② 默认源不能停用、不能删除；
/// ③ 至少保留一个启用的源；
/// ④ 内置源不可删、备注不可改；
/// ⑤ 删除自定义源要连带删凭据（不留孤儿）。
///
/// 这些规则以前散在设置页的 if 分支里，只能靠点界面才发现漏了哪条。
/// </summary>
public class RegistrySourceServiceTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"prefs_svc_{Guid.NewGuid():N}.json");

    private RegistrySourceService NewService()
        => new(new RegistrySourceStore(new PreferencesService(_path)));

    public void Dispose()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch
        {
            // 清理失败无所谓
        }
    }

    [Fact]
    public void 默认视图等于内置站点_第一个是默认()
    {
        var sources = NewService().GetAll();

        Assert.Equal(MirrorPresets_Count(), sources.Count);
        Assert.All(sources, s => Assert.True(s.IsBuiltIn));
        Assert.All(sources, s => Assert.True(s.Enabled));

        // 预设表第一个就是默认站（docker.1panel.live）
        Assert.Equal(RegistryDefaults.Host, sources[0].Host);
        Assert.True(sources[0].IsDefault);
        Assert.Single(sources, s => s.IsDefault);
    }

    [Fact]
    public async Task 设为默认后只有一个默认_且下拉次序不变()
    {
        var service = NewService();
        var before = service.EnabledHosts();

        await service.SetDefaultAsync("docker.io");

        Assert.Equal("docker.io", service.EffectiveDefaultHost);
        Assert.Single(service.GetAll(), s => s.IsDefault);
        Assert.Equal(before, service.EnabledHosts()); // 次序是"内置表次序"，不因默认而重排
    }

    [Fact]
    public async Task 默认源不能被停用()
    {
        var service = NewService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetEnabledAsync(RegistryDefaults.Host, enabled: false));

        Assert.Contains("默认", ex.Message);
        Assert.True(service.Find(RegistryDefaults.Host)!.Enabled);
    }

    [Fact]
    public async Task 至少保留一个启用的源()
    {
        var service = NewService();

        // 先把默认换掉，再逐个关掉，最后一个必须被拒绝
        await service.SetDefaultAsync("docker.io");

        foreach (var host in MirrorPresets.Sites.Select(s => s.Host).Where(h => h != "docker.io").ToArray())
        {
            await service.SetEnabledAsync(host, enabled: false);
        }

        // 现在只剩 docker.io（它是默认，被默认规则挡住）→ 直接的"最后一个"分支由它先挡
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetEnabledAsync("docker.io", enabled: false));
        Assert.Contains("默认", ex.Message);
    }

    [Fact]
    public async Task 内置源不能删除_但可以停用()
    {
        var service = NewService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RemoveAsync("docker.1ms.run"));
        Assert.Contains("内置", ex.Message);

        await service.SetEnabledAsync("docker.1ms.run", enabled: false);
        Assert.DoesNotContain("docker.1ms.run", service.EnabledHosts());

        // 停用后仍在列表里（能恢复），只是不在下拉候选里
        Assert.NotNull(service.Find("docker.1ms.run"));
    }

    [Fact]
    public async Task 自定义源可以删除_并且连带删掉凭据()
    {
        var service = NewService();

        await service.AddAsync("git.example.com", "公司 Harbor", "alice", "pw");
        Assert.True(service.Find("git.example.com")!.HasCredential);

        await service.SetDefaultAsync("git.example.com");
        await service.SetDefaultAsync(RegistryDefaults.Host); // 默认换回去才能删

        await service.RemoveAsync("git.example.com");

        Assert.Null(service.Find("git.example.com"));
        Assert.Null(new RegistrySourceStore(new PreferencesService(_path)).Find("git.example.com"));
    }

    [Fact]
    public async Task 默认源不能删除()
    {
        var service = NewService();
        await service.AddAsync("git.example.com");
        await service.SetDefaultAsync("git.example.com");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RemoveAsync("git.example.com"));

        Assert.Contains("默认", ex.Message);
        Assert.NotNull(service.Find("git.example.com"));
    }

    [Fact]
    public async Task 添加内置站点不会重复添加_只恢复启用()
    {
        var service = NewService();
        await service.SetDefaultAsync("docker.io");
        await service.SetEnabledAsync("docker.1ms.run", enabled: false);

        var result = await service.AddAsync("docker.1ms.run");

        Assert.False(result.Added);
        Assert.True(result.Source.Enabled); // 顺手恢复了
        Assert.Single(service.GetAll(), s => s.Host == "docker.1ms.run");
    }

    [Fact]
    public async Task 自定义源带备注_内置备注不可改()
    {
        var service = NewService();

        await service.AddAsync("git.example.com", "  公司 Harbor  ");
        var added = service.Find("git.example.com")!;

        Assert.Equal("公司 Harbor", added.Note);          // 去掉首尾空白
        Assert.StartsWith("自定义", added.Subtitle);      // 行副标题结构对内置/自定义一致
        Assert.Contains("未配置凭据", added.Subtitle);

        await service.UpdateNoteAsync("git.example.com", "内网镜像库");
        Assert.Equal("内网镜像库", service.Find("git.example.com")!.Note);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateNoteAsync(RegistryDefaults.Host, "乱改"));
    }

    [Fact]
    public async Task 备注超长会被拒绝()
    {
        var service = NewService();
        await service.AddAsync("git.example.com");

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateNoteAsync("git.example.com", new string('x', RegistrySourceService.NoteMaxLength + 1)));
    }

    [Fact]
    public async Task 停用的源不能设为默认()
    {
        var service = NewService();
        await service.SetEnabledAsync("docker.1ms.run", enabled: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetDefaultAsync("docker.1ms.run"));

        Assert.Contains("不在下拉里", ex.Message);
    }

    [Fact]
    public async Task 默认源指向不存在的站点时自愈到第一个启用的源()
    {
        // 手改 prefs.json 或删源都可能留下这种悬空的默认值
        await File.WriteAllTextAsync(_path, """
            { "DefaultRegistry": "已删除.example.com" }
            """);

        var service = NewService();

        Assert.Equal(RegistryDefaults.Host, service.EffectiveDefaultHost);
        Assert.Single(service.GetAll(), s => s.IsDefault);
    }

    [Fact]
    public async Task 不允许给列表里没有的源配凭据()
    {
        var service = NewService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetCredentialAsync("ghost.example.com", "alice", "pw"));

        Assert.Contains("先在列表里添加", ex.Message);
    }

    [Fact]
    public async Task 清除凭据后源仍然在列表里()
    {
        var service = NewService();
        await service.AddAsync("git.example.com", username: "alice", password: "pw");

        await service.ClearCredentialAsync("git.example.com");

        Assert.False(service.Find("git.example.com")!.HasCredential);
        Assert.NotNull(service.Find("git.example.com"));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("有 空格", false)]
    [InlineData("docker.io（可列 tag）", false)]
    [InlineData("ghcr.io", true)]
    [InlineData("harbor.example.com:5000", true)]
    public void 主机名校验(string input, bool expected)
    {
        Assert.Equal(expected, RegistrySourceService.TryValidateHost(input, out _, out var error));
        Assert.Equal(expected, error.Length == 0);
    }

    private static int MirrorPresets_Count() => WslcDesktop.Core.Parsing.MirrorPresets.Sites.Count;
}
