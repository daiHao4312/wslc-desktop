using WslcDesktop.Core.Models;
using WslcDesktop.Core.Services;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// 镜像源/凭据的**持久化层**（M16d）。
///
/// 这一层只该管两件事：加解密、旧格式兼容。业务规则在
/// <see cref="RegistrySourceServiceTests"/> 里，不要混进来。
/// </summary>
public class RegistrySourceStoreTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"prefs_src_{Guid.NewGuid():N}.json");

    private RegistrySourceStore NewStore() => new(new PreferencesService(_path));

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
    public async Task 落盘的密码是密文_明文不出现在文件里()
    {
        var store = NewStore();

        await store.SaveCredentialsAsync(new[]
        {
            RegistrySourceStore.CreateCredential("git.example.com", "alice", "s3cr3t-pw")
        });

        var text = await File.ReadAllTextAsync(_path);
        Assert.Contains("git.example.com", text);
        Assert.Contains("alice", text);
        Assert.DoesNotContain("s3cr3t-pw", text);
    }

    [Fact]
    public async Task 按别名能取到同一条凭据()
    {
        var store = NewStore();
        await store.SaveCredentialsAsync(new[]
        {
            RegistrySourceStore.CreateCredential("docker.io", "alice", "pw")
        });

        Assert.True(store.TryGetLogin("registry-1.docker.io", out var login, out var error), error);
        Assert.NotNull(login);
        Assert.Equal("alice", login!.Username);
        Assert.Equal("pw", login.Password);
    }

    [Fact]
    public async Task 解不开的凭据要报错而不是当成没凭据()
    {
        var store = NewStore();
        await store.SaveCredentialsAsync(new[]
        {
            new RegistryCredential
            {
                Host = "git.example.com",
                Username = "alice",
                ProtectedPassword = "AAAA" // 合法 Base64，但不是能解开的 DPAPI 密文
            }
        });

        Assert.False(store.TryGetLogin("git.example.com", out var login, out var error));
        Assert.Null(login);
        Assert.Contains("解不开", error);
        Assert.Contains("重新填写", error);
    }

    [Fact]
    public void 没有凭据时不报错_由调用方决定走匿名()
    {
        var store = NewStore();

        Assert.False(store.TryGetLogin("git.example.com", out var login, out var error));
        Assert.Null(login);
        Assert.Equal(string.Empty, error); // 空 = "就是没凭据"，与"解不开"不是一回事
    }

    [Fact]
    public async Task 镜像源与默认源一起写入并读回()
    {
        var store = NewStore();

        await store.SaveSourcesAsync(
            new[]
            {
                new RegistrySourcePreference
                {
                    Host = "git.example.com",
                    Note = "公司 Harbor",
                    Enabled = true,
                    IsCustom = true
                }
            },
            defaultHost: "git.example.com");

        var reloaded = NewStore();
        var source = Assert.Single(reloaded.SourcePreferences);
        Assert.Equal("git.example.com", source.Host);
        Assert.Equal("公司 Harbor", source.Note);
        Assert.True(source.IsCustom);
        Assert.True(source.Enabled);
        Assert.Equal("git.example.com", reloaded.StoredDefaultRegistry);
    }

    /// <summary>
    /// 旧版本（v1.1.1 之前）把"自定义源"和"被移除的内置源"分成两个列表存。
    /// 读的时候必须合成到一个模型里，否则升级后用户的源会集体消失。
    /// </summary>
    [Fact]
    public async Task 旧格式的自定义源与被移除的内置源都能读出来()
    {
        await File.WriteAllTextAsync(_path, """
            {
              "CustomRegistries": ["git.example.com"],
              "HiddenRegistries": ["docker.1ms.run"]
            }
            """);

        var preferences = NewStore().SourcePreferences;

        var custom = Assert.Single(preferences, p => p.IsCustom);
        Assert.Equal("git.example.com", custom.Host);
        Assert.True(custom.Enabled);

        var hidden = Assert.Single(preferences, p => !p.IsCustom);
        Assert.Equal("docker.1ms.run", hidden.Host);
        Assert.False(hidden.Enabled);
    }

    [Fact]
    public async Task 写过一次新格式后旧字段被清空()
    {
        await File.WriteAllTextAsync(_path, """
            {
              "CustomRegistries": ["git.example.com"],
              "HiddenRegistries": ["docker.1ms.run"]
            }
            """);

        var store = NewStore();
        await store.SaveSourcesAsync(store.SourcePreferences, defaultHost: "docker.1panel.live");

        var reloaded = new PreferencesService(_path).GetPreferences();
        Assert.Empty(reloaded.CustomRegistries);
        Assert.Empty(reloaded.HiddenRegistries);
        Assert.Equal(2, reloaded.RegistrySources.Count);
    }
}
