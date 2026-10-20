using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// 官方 HRESULT 的识别（M14 修订）。
///
/// 这些错误码**没有本地化文本**，原样展示就是"无法找到与此错误代码关联的文本"。
/// 认出来才能给一句能行动的话。
/// </summary>
public class ApiErrorCodesTests
{
    [Fact]
    public void 会话被占用这个错误码要认得出来()
    {
        var exception = new InvalidOperationException("x") { HResult = unchecked((int)0x80040607) };

        Assert.True(ApiErrorCodes.IsSessionReserved(exception));
        Assert.Contains("会话名被占用", ApiErrorCodes.Describe(exception.HResult));
    }

    [Fact]
    public void 别的HRESULT不算会话占用()
    {
        var exception = new InvalidOperationException("x") { HResult = unchecked((int)0x8004060F) };

        Assert.False(ApiErrorCodes.IsSessionReserved(exception));
        Assert.Equal("引擎会话不存在", ApiErrorCodes.Describe(exception.HResult));
    }

    [Fact]
    public void 容器不存在与未运行也有短语()
    {
        Assert.Equal("容器不存在", ApiErrorCodes.Describe(ApiErrorCodes.ContainerNotFound));
        Assert.Equal("容器未在运行", ApiErrorCodes.Describe(ApiErrorCodes.ContainerNotRunning));
    }

    [Fact]
    public void 不认识的错误码返回null_调用方保留原文()
        => Assert.Null(ApiErrorCodes.Describe(unchecked((int)0x80004005)));
}
