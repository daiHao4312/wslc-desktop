using WSLC.Panel.Core.Services;

namespace WSLC.Panel.Core.Tests;

/// <summary>
/// settings.yaml 读写服务测试。
/// 核心验收点：① 原子写（不留 .tmp 残留）；② 备份只保留最近 N 份，不无限堆积。
/// </summary>
public class WslcSettingsServiceTests
{
    [Fact]
    public async Task TestSave_IsAtomicAndLeavesNoTempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"wslc_settings_{Guid.NewGuid():N}");
        var file = Path.Combine(dir, "settings.yaml");
        try
        {
            var svc = new WslcSettingsService(file);
            var backup = await svc.SaveAsync("session:\n  cpuCount: 2\n");

            Assert.True(File.Exists(file));
            Assert.False(File.Exists(file + ".tmp"), "原子写必须把临时文件替换掉，不能留残留");
            Assert.Equal("session:\n  cpuCount: 2\n", await File.ReadAllTextAsync(file));

            // 首次保存（原文件不存在）不产生备份
            Assert.Equal(string.Empty, backup);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TestSave_CreatesTimestampedBackupOnOverwrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"wslc_settings_{Guid.NewGuid():N}");
        var file = Path.Combine(dir, "settings.yaml");
        try
        {
            var svc = new WslcSettingsService(file);
            await svc.SaveAsync("v1\n");

            var backup = await svc.SaveAsync("v2\n");

            Assert.False(string.IsNullOrEmpty(backup));
            Assert.True(File.Exists(backup));
            Assert.Equal("v1\n", await File.ReadAllTextAsync(backup));
            Assert.Equal("v2\n", await File.ReadAllTextAsync(file));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TestSave_KeepsOnlyMostRecentBackups()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"wslc_settings_{Guid.NewGuid():N}");
        var file = Path.Combine(dir, "settings.yaml");
        try
        {
            // 手工造 12 份不同时间戳的备份（时间戳字典序 = 时间序）
            Directory.CreateDirectory(dir);
            for (var i = 0; i < 12; i++)
            {
                await File.WriteAllTextAsync($"{file}.bak-20260101-0000{i:D2}", "old");
            }

            var svc = new WslcSettingsService(file);
            await svc.SaveAsync("current\n");

            var backups = svc.GetBackups();

            // 12 份手工 + 1 份本次 = 13 → 只留最近 10 份
            Assert.Equal(10, backups.Count);
            Assert.Contains(backups, b => b.EndsWith("20260101-000011", StringComparison.Ordinal));
            Assert.DoesNotContain(backups, b => b.EndsWith("20260101-000000", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
