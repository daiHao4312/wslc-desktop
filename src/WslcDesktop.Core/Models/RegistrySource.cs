namespace WslcDesktop.Core.Models;

/// <summary>
/// 用户对某个镜像源的**个人设置**（随 prefs.json 落盘）。
///
/// 只记"与内置默认值不同的部分"：自定义源必须有记录，内置源只有被停用（或改过）时才有记录。
/// 这样以后往 <c>MirrorPresets.Sites</c> 里加站点时，老用户的列表里会自动多出来，不需要迁移脚本。
/// </summary>
public sealed record RegistrySourcePreference
{
    /// <summary>纯主机名（小写、无协议头）。</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>
    /// 备注。**只有自定义源用这个字段** —— 内置源的备注是实测结论（能不能列 tag 之类），不允许用户改。
    /// </summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>是否出现在「拉取镜像」的镜像源下拉里。</summary>
    public bool Enabled { get; init; }

    /// <summary>是不是用户自己添加的（内置站点不可删除，只能停用）。</summary>
    public bool IsCustom { get; init; }
}

/// <summary>
/// 镜像源的**统一视图**：内置与自定义只有来源不同，字段完全一致。
///
/// 界面只认这个类型。之前 "内置走 MirrorPresets、自定义走 prefs、停用走 HiddenRegistries、
/// 默认又单独一个字符串字段" —— 同一件事四套状态，界面里只能靠 if 分支拼，
/// 于是出现了"两个设置默认的入口""从下拉移除 / 移除两种按钮"这类别扭的设计。
/// </summary>
public sealed record RegistrySource
{
    /// <summary>纯主机名，同时也是这一行的标识（比较一律走 canonical，别名算同一条）。</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>备注：内置源是实测结论，自定义源由用户填写（可为空）。</summary>
    public string Note { get; init; } = string.Empty;

    public bool IsBuiltIn { get; init; }

    /// <summary>是否出现在拉取镜像的镜像源下拉里。</summary>
    public bool Enabled { get; init; }

    public bool IsDefault { get; init; }

    /// <summary>有凭据时的用户名；null = 没配凭据（公开源）。</summary>
    public string? Username { get; init; }

    public bool HasCredential => !string.IsNullOrEmpty(Username);

    /// <summary>
    /// 能不能删除。内置源删不掉（它是常量），只能停用；当前默认源也不允许删
    /// （默认指向一个不存在的源，用户下次拉取只会觉得"我选的那个不见了"）。
    /// </summary>
    public bool CanRemove => !IsBuiltIn && !IsDefault;

    /// <summary>
    /// 列表里的副标题：`来源 · 凭据 · 备注`，**顺序固定、三者必有**。
    ///
    /// 之前内置站有实测备注、自定义源什么说明都没有，同一张表里两种信息结构 ——
    /// 那不是"备注只有内置才有"，是模型没抽干净。现在两者逐字同构，缺的只是值。
    /// </summary>
    public string Subtitle => string.Join("　·　", new[]
    {
        IsBuiltIn ? "内置" : "自定义",
        HasCredential ? $"凭据：{Username}" : "未配置凭据",
        Note
    }.Where(part => part.Length > 0));

    /// <summary>
    /// 搜索匹配（主机名 / 备注 / 用户名）。给设置页的搜索框用：
    /// 源一多，找某个地址只能靠搜，不能靠翻。
    /// </summary>
    public bool Matches(string? keyword)
    {
        var text = (keyword ?? string.Empty).Trim();
        if (text.Length == 0) return true;

        return Host.Contains(text, StringComparison.OrdinalIgnoreCase)
               || Note.Contains(text, StringComparison.OrdinalIgnoreCase)
               || (Username?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}

/// <summary>
/// 「添加镜像源」的结果。
/// <para><see cref="Added"/> 为 false 表示**本来就在列表里**（内置站点，或已经加过的自定义源）——
/// 界面应当如实说明，而不是假装加成功了。</para>
/// </summary>
public sealed record RegistrySourceAddResult(RegistrySource Source, bool Added);
