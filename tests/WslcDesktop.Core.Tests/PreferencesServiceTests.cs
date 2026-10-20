using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;
using WslcDesktop.Core.Services;

namespace WslcDesktop.Core.Tests;

public class PreferencesServiceTests
{
    [Fact]
    public async Task TestLoadAndSavePreferences()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"prefs_test_{Guid.NewGuid():N}.json");
        try
        {
            var svc = new PreferencesService(tempFile);
            var initial = await svc.LoadAsync();
            Assert.Equal("Default", initial.Theme);
            Assert.Equal(3, initial.RefreshIntervalSeconds);

            var updated = initial with { Theme = "Dark", RefreshIntervalSeconds = 5 };
            await svc.SaveAsync(updated);

            var svc2 = new PreferencesService(tempFile);
            var loaded = await svc2.LoadAsync();
            Assert.Equal("Dark", loaded.Theme);
            Assert.Equal(5, loaded.RefreshIntervalSeconds);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// 自定义镜像源与凭据要能真正往返。
    ///
    /// ⚠️ 这条测试不是"顺手加的"：<c>PreferencesService</c> 的反序列化失败被 catch 吞掉了
    /// （读坏了就退回默认值），所以**字段名/类型不匹配时界面只会"设置莫名其妙消失"**，
    /// 没有任何报错。只有这种往返断言能发现。
    /// </summary>
    [Fact]
    public async Task 自定义镜像源与凭据能往返()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"prefs_test_{Guid.NewGuid():N}.json");
        try
        {
            var svc = new PreferencesService(tempFile);
            var initial = await svc.LoadAsync();

            // 按真实路径加密一次：这条断言的意义是"读回来还能解开"
            var protectedPassword = SecretProtector.Protect("pw-123");

            await svc.SaveAsync(initial with
            {
                RegistrySources = new[]
                {
                    new RegistrySourcePreference
                    {
                        Host = "git.example.com",
                        Note = "公司 Harbor",
                        Enabled = true,
                        IsCustom = true
                    },
                    new RegistrySourcePreference
                    {
                        Host = "docker.1ms.run",
                        Enabled = false
                    }
                },
                RegistryCredentials = new[]
                {
                    new RegistryCredential
                    {
                        Host = "git.example.com",
                        Username = "alice",
                        ProtectedPassword = protectedPassword
                    }
                }
            });

            var reloaded = await new PreferencesService(tempFile).LoadAsync();

            Assert.Equal(2, reloaded.RegistrySources.Count);

            var custom = Assert.Single(reloaded.RegistrySources, s => s.IsCustom);
            Assert.Equal("git.example.com", custom.Host);
            Assert.Equal("公司 Harbor", custom.Note);
            Assert.True(custom.Enabled);

            var builtIn = Assert.Single(reloaded.RegistrySources, s => !s.IsCustom);
            Assert.Equal("docker.1ms.run", builtIn.Host);
            Assert.False(builtIn.Enabled);

            var credential = Assert.Single(reloaded.RegistryCredentials);
            Assert.Equal("git.example.com", credential.Host);
            Assert.Equal("alice", credential.Username);
            Assert.Equal("pw-123", SecretProtector.Unprotect(credential.ProtectedPassword));

            // 明文不能出现在文件里
            Assert.DoesNotContain("pw-123", await File.ReadAllTextAsync(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
