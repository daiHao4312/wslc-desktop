using System.Text;
using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// 凭据加密（Windows DPAPI）。
///
/// 这几条断言守的是**"存的是密文"这件事本身**：prefs.json 是明文 JSON，
/// 哪天有人图省事把明文写进去，只有这里能发现 —— 界面上完全看不出来。
/// </summary>
public class SecretProtectorTests
{
    [Fact]
    public void 明文加密后能原样解回来()
    {
        const string secret = "P@ssw0rd-中文-测试";

        var protectedText = SecretProtector.Protect(secret);

        Assert.NotEqual(secret, protectedText);
        Assert.Equal(secret, SecretProtector.Unprotect(protectedText));
    }

    [Fact]
    public void 密文里不能出现明文()
    {
        const string secret = "super-secret-token";

        var protectedText = SecretProtector.Protect(secret);
        var raw = Encoding.UTF8.GetString(Convert.FromBase64String(protectedText));

        // 连"先解一层 Base64 就能看到明文"这种假加密也不允许
        Assert.DoesNotContain(secret, raw);
        Assert.DoesNotContain(secret, protectedText);
    }

    [Fact]
    public void 空串也能往返_代表只有用户名没密码()
    {
        var protectedText = SecretProtector.Protect(string.Empty);

        Assert.NotEqual(string.Empty, protectedText); // 非空才说明真的过了一遍加密
        Assert.Equal(string.Empty, SecretProtector.Unprotect(protectedText));
    }

    [Fact]
    public void 同明文两次加密结果不同()
    {
        // DPAPI 每次都带新的随机盐：相同则说明加密被缓存或被换成了固定算法
        Assert.NotEqual(SecretProtector.Protect("same"), SecretProtector.Protect("same"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("不是 base64")]
    [InlineData("AAAA")] // 合法 Base64，但不是 DPAPI 密文
    public void 解不开时返回null而不是抛异常(string damaged)
    {
        // 换 Windows 账户、prefs.json 从别处拷来都会走到这条路：
        // 必须让调用方有机会提示"重填"，而不是把设置页整页炸掉
        Assert.Null(SecretProtector.Unprotect(damaged));
    }
}
