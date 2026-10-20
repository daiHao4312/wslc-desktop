using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// 诊断包里的 prefs 脱敏。
///
/// 这一组测试守的是一条底线：**诊断包默认会被贴给别人**，所以凭据类字段一个都不能出去。
/// </summary>
public class DiagnosticsRedactorTests
{
    private const string PrefsWithCredentials = """
    {
      "Theme": "Dark",
      "DefaultRegistry": "docker.1panel.live",
      "RegistryCredentials": [
        {
          "Host": "git.example.com",
          "Username": "alice",
          "ProtectedPassword": "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA"
        }
      ]
    }
    """;

    [Fact]
    public void 凭据整项被删除_用户名与密文都不出现()
    {
        var redacted = DiagnosticsRedactor.RedactPreferences(PrefsWithCredentials);

        Assert.DoesNotContain("RegistryCredentials", redacted);
        Assert.DoesNotContain("alice", redacted);
        Assert.DoesNotContain("AQAAANCMnd8BFdERjHoAwE", redacted);
    }

    [Fact]
    public void 非敏感字段要保留()
    {
        var redacted = DiagnosticsRedactor.RedactPreferences(PrefsWithCredentials);

        Assert.Contains("\"Theme\": \"Dark\"", redacted);
        Assert.Contains("docker.1panel.live", redacted);
    }

    [Fact]
    public void 嵌套的敏感键也要删()
    {
        const string json = """{ "Outer": { "MyPassword": "hunter2", "Keep": "ok" } }""";

        var redacted = DiagnosticsRedactor.RedactPreferences(json);

        Assert.DoesNotContain("hunter2", redacted);
        Assert.Contains("\"Keep\": \"ok\"", redacted);
    }

    [Fact]
    public void 解析失败时整份省略_而不是原样带出()
    {
        var redacted = DiagnosticsRedactor.RedactPreferences("{ 不是合法 JSON Secret=abc");

        Assert.Contains("已省略", redacted);
        Assert.DoesNotContain("Secret=abc", redacted);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void 空输入返回空对象(string? json)
        => Assert.Equal("{}", DiagnosticsRedactor.RedactPreferences(json));
}
