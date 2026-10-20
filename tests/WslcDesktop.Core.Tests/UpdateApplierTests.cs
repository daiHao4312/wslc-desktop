using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Services;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// M13 在线更新：辅助进程协议 + 原地替换流程。
///
/// 这里用真实临时目录做文件交换（备份/复制/回滚都是真实文件操作），
/// 只把"等进程退出"和"拉起进程"换成假实现——这两件事在单测里没法真做，
/// 但它们恰好也是替换流程里仅有的两个外部副作用。
/// </summary>
public class UpdateApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "wslc-apply-tests", Guid.NewGuid().ToString("N"));

    private readonly string _target;
    private readonly string _staging;

    private static readonly byte[] OldPayload = "old-version"u8.ToArray();
    private static readonly byte[] NewPayload = "new-version-with-different-content"u8.ToArray();

    public UpdateApplierTests()
    {
        Directory.CreateDirectory(_root);
        _target = Path.Combine(_root, "wslc-desktop.exe");
        _staging = Path.Combine(_root, "update", "wslc-desktop-1.2.0.exe");

        Directory.CreateDirectory(Path.GetDirectoryName(_staging)!);
        File.WriteAllBytes(_target, OldPayload);
        File.WriteAllBytes(_staging, NewPayload);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 忽略
        }

        GC.SuppressFinalize(this);
    }

    // ---------- 假实现 ----------

    private sealed class StubWaiter : IProcessWaiter
    {
        private readonly bool _result;
        private readonly bool _throw;

        public int Calls { get; private set; }
        public int LastProcessId { get; private set; }
        public string? LastExpectedName { get; private set; }

        public StubWaiter(bool result = true, bool toThrow = false)
        {
            _result = result;
            _throw = toThrow;
        }

        public bool WaitForExit(int processId, string? expectedProcessName, TimeSpan timeout)
        {
            Calls++;
            LastProcessId = processId;
            LastExpectedName = expectedProcessName;

            if (_throw) throw new InvalidOperationException("不该抛出来的：替换流程必须自己兜住等待失败");
            return _result;
        }
    }

    private sealed class StubLauncher : IProcessLauncher
    {
        private readonly bool _result;

        public List<string> Started { get; } = new();

        public StubLauncher(bool result = true) => _result = result;

        public bool Start(string executablePath)
        {
            Started.Add(executablePath);
            return _result;
        }
    }

    private UpdateApplier Applier(IProcessWaiter waiter, IProcessLauncher launcher, List<string>? log = null)
        => new(waiter, launcher, log is null ? null : log.Add, TimeSpan.Zero);

    // ---------- 协议 ----------

    [Fact]
    public void TestHelperArguments_RoundTrip()
    {
        const string target = @"C:\Users\张三\桌面\wslc-desktop.exe";
        var commandLine = UpdateHelperArguments.Build(4321, target);

        Assert.StartsWith("--apply-update 4321 ", commandLine);

        // 按命令行的实际切分方式还原（引号内视为一个参数）
        var args = new[] { "--apply-update", "4321", target };

        Assert.True(UpdateHelperArguments.TryParse(args, out var parsed));
        Assert.Equal(4321, parsed.OldProcessId);
        Assert.Equal(target, parsed.TargetPath);
    }

    [Fact]
    public void TestHelperArguments_UnquotesTargetWithSpaces()
    {
        Assert.True(UpdateHelperArguments.TryParse(
            new[] { "--apply-update", "99", "\"C:\\Program Files\\x\\wslc-desktop.exe\"" }, out var parsed));

        Assert.Equal(@"C:\Program Files\x\wslc-desktop.exe", parsed.TargetPath);
    }

    public static TheoryData<string[]> IncompleteInvocations => new()
    {
        Array.Empty<string>(),                                  // 空
        new[] { "--other" },                                    // 别的开关
        new[] { "--apply-update" },                             // 缺参数
        new[] { "--apply-update", "12" },                       // 缺路径
        new[] { "--apply-update", "abc", @"C:\x.exe" },         // PID 不是数字
        new[] { "--apply-update", "0", @"C:\x.exe" },           // PID 不合法
        new[] { "--apply-update", "-5", @"C:\x.exe" },          // 负 PID
        new[] { "--apply-update", "12", "   " }                 // 路径为空
    };

    [Theory]
    [MemberData(nameof(IncompleteInvocations))]
    public void TestHelperArguments_RejectsIncompleteInvocation(string[] args)
        => Assert.False(UpdateHelperArguments.TryParse(args, out _));

    // ---------- 可写探测 ----------

    [Fact]
    public void TestDirectoryWriteProbe()
    {
        Assert.True(DirectoryWriteProbe.CanWrite(_root));
        Assert.False(DirectoryWriteProbe.CanWrite(Path.Combine(_root, "not-there")));
        Assert.False(DirectoryWriteProbe.CanWrite(null));
        Assert.False(DirectoryWriteProbe.CanWrite("   "));

        // 探针文件必须清干净，别在用户目录里留垃圾
        Assert.Empty(Directory.GetFiles(_root, ".wslc-write-probe-*"));
    }

    // ---------- 替换 ----------

    [Fact]
    public void TestApply_SuccessReplacesTargetKeepsBackupAndStartsNewVersion()
    {
        var waiter = new StubWaiter();
        var launcher = new StubLauncher();
        var log = new List<string>();

        var result = Applier(waiter, launcher, log).Apply(_staging, _target, 4321, "wslc-desktop");

        Assert.True(result.IsSuccess);
        Assert.Equal(UpdateApplyStatus.Applied, result.Status);
        Assert.True(result.NewProcessStarted);

        // 目标已是新内容，旧内容进了备份
        Assert.Equal(NewPayload, File.ReadAllBytes(_target));
        Assert.Equal(OldPayload, File.ReadAllBytes(_target + UpdateApplier.BackupSuffix));
        Assert.Equal(_target + UpdateApplier.BackupSuffix, result.BackupPath);

        // 拉起的是替换后的目标路径，而不是更新包
        Assert.Equal(new[] { _target }, launcher.Started);

        // 等待时把 PID 与进程名都传下去了（进程名用于防 PID 复用）
        Assert.Equal(1, waiter.Calls);
        Assert.Equal(4321, waiter.LastProcessId);
        Assert.Equal("wslc-desktop", waiter.LastExpectedName);

        Assert.Contains(log, line => line.Contains("已备份旧版本"));
        Assert.Contains(log, line => line.Contains("更新完成"));
    }

    [Fact]
    public void TestApply_WaiterTimeoutChangesNothing()
    {
        var result = Applier(new StubWaiter(result: false), new StubLauncher())
            .Apply(_staging, _target, 4321, "wslc-desktop");

        Assert.Equal(UpdateApplyStatus.WaitTimedOut, result.Status);
        Assert.True(result.IsNoOp); // 可以放心让用户重试
        Assert.Equal(OldPayload, File.ReadAllBytes(_target));
        Assert.False(File.Exists(_target + UpdateApplier.BackupSuffix));
    }

    [Fact]
    public void TestApply_MissingStagingChangesNothing()
    {
        File.Delete(_staging);

        var result = Applier(new StubWaiter(), new StubLauncher())
            .Apply(_staging, _target, 1, null);

        Assert.Equal(UpdateApplyStatus.StagingMissing, result.Status);
        Assert.True(result.IsNoOp);
        Assert.Equal(OldPayload, File.ReadAllBytes(_target));
    }

    [Fact]
    public void TestApply_UnwritableDirectoryChangesNothing()
    {
        var target = Path.Combine(_root, "missing-folder", "wslc-desktop.exe");

        var result = Applier(new StubWaiter(), new StubLauncher()).Apply(_staging, target, 1, null);

        Assert.Equal(UpdateApplyStatus.TargetNotWritable, result.Status);
        Assert.True(result.IsNoOp);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void TestApply_LaunchFailureRollsBackToPreviousVersion()
    {
        var result = Applier(new StubWaiter(), new StubLauncher(result: false))
            .Apply(_staging, _target, 1, "wslc-desktop");

        Assert.Equal(UpdateApplyStatus.RolledBack, result.Status);
        Assert.False(result.NewProcessStarted);

        // 旧版本回来了，没留下 .old 残骸
        Assert.Equal(OldPayload, File.ReadAllBytes(_target));
        Assert.False(File.Exists(_target + UpdateApplier.BackupSuffix));
    }

    [Fact]
    public void TestApply_OverwritesStaleBackupFromPreviousUpdate()
    {
        var stale = _target + UpdateApplier.BackupSuffix;
        File.WriteAllBytes(stale, "stale-backup"u8.ToArray());

        var result = Applier(new StubWaiter(), new StubLauncher()).Apply(_staging, _target, 1, null);

        Assert.True(result.IsSuccess);
        Assert.Equal(OldPayload, File.ReadAllBytes(stale));
    }

    [Fact]
    public void TestApply_FirstInstallWithoutExistingTargetStillWorks()
    {
        File.Delete(_target);

        var result = Applier(new StubWaiter(), new StubLauncher()).Apply(_staging, _target, 1, null);

        Assert.True(result.IsSuccess);
        Assert.Equal(NewPayload, File.ReadAllBytes(_target));
        Assert.Null(result.BackupPath);
        Assert.False(File.Exists(_target + UpdateApplier.BackupSuffix));
    }

    [Fact]
    public void TestApply_LockedTargetFailsWithoutTouchingFiles()
    {
        UpdateApplyResult result;

        // 独占打开目标文件，模拟"进程还没完全释放句柄"
        using (new FileStream(_target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = Applier(new StubWaiter(), new StubLauncher()).Apply(_staging, _target, 1, null);
        }

        Assert.Equal(UpdateApplyStatus.Failed, result.Status);
        Assert.Contains("备份", result.Message);
        // 备份没成功，所以文件必须原样还在（这是"改不动就别改"的底线）
        Assert.Equal(OldPayload, File.ReadAllBytes(_target));
    }

    // ---------- 启动器 ----------

    [Fact]
    public void TestSystemProcessLauncher_RejectsMissingAndNonExecutableFiles()
    {
        var launcher = new SystemProcessLauncher();

        Assert.False(launcher.Start(Path.Combine(_root, "no-such.exe")));
        Assert.False(launcher.Start("   "));

        // 非 PE 内容：CreateProcess 会拒绝
        var fake = Path.Combine(_root, "fake.exe");
        File.WriteAllText(fake, "this is definitely not a PE file");
        Assert.False(launcher.Start(fake));
    }

    [Fact]
    public void TestSystemProcessLauncher_TreatsFastExitAsFailure()
    {
        // "启动成功然后立刻退出"必须判为失败：否则新版起不来会被当成更新成功，
        // 那样 Applier 就不会回滚，用户手里只剩一个打不开的程序。
        var where = Path.Combine(Environment.SystemDirectory, "where.exe");
        if (!File.Exists(where)) return; // 环境不具备就跳过

        // 无参数的 where.exe 打印用法后立即退出
        Assert.False(new SystemProcessLauncher().Start(where));
    }

    // ---------- 落点 ----------

    [Fact]
    public void TestUpdateLocations_AreUnderAppData()
    {
        var fake = Path.Combine(_root, "localappdata");

        Assert.Equal(Path.Combine(fake, "WslcDesktop"), UpdateLocations.DataDirectory(fake));
        Assert.Equal(Path.Combine(fake, "WslcDesktop", "update"), UpdateLocations.UpdatesDirectory(fake));
        Assert.Equal(Path.Combine(fake, "WslcDesktop", "update.log"), UpdateLocations.LogPath(fake));
    }
}
