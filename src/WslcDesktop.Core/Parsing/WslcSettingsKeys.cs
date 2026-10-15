namespace WslcDesktop.Core.Parsing;

/// <summary>settings.yaml 里面板会编辑的键路径（集中一处，避免各处手写字符串拼错）。</summary>
public static class WslcSettingsKeys
{
    public const string SessionCpuCount = "session.cpuCount";
    public const string SessionMemorySize = "session.memorySize";
    public const string SessionMaxStorageSize = "session.maxStorageSize";
    public const string SessionStoragePath = "session.storagePath";
    public const string SessionDefaultBindingAddress = "session.defaultBindingAddress";
    public const string SessionHostLoopback = "session.hostLoopback";
    public const string SessionIdleTimeout = "session.idleTimeout";
    public const string CredentialStore = "credentialStore";

    /// <summary>面板设置页展示顺序（键路径、界面标签、说明、是否危险项）。</summary>
    public static readonly (string Key, string Label, string Hint, bool IsRisky)[] FormFields =
    {
        (SessionCpuCount, "会话 CPU 核数", "例如 4；留空表示使用全部可用 CPU。", false),
        (SessionMemorySize, "会话内存上限", "例如 2GB；留空表示默认（可用内存的一半）。", false),
        (SessionMaxStorageSize, "会话磁盘上限", "例如 500GB；留空表示默认 1TB。", false),
        (SessionIdleTimeout, "空闲会话超时（秒）", "会话空闲多久后被回收，默认 30。", false),
        (SessionDefaultBindingAddress, "默认端口绑定地址", "端口映射未写 IP 时使用的绑定地址，默认 127.0.0.1。", false),
        (SessionHostLoopback, "主机回环域名", "解析到主机回环地址的 DNS 名；填 none 可关闭该条目。", false),
        (CredentialStore, "凭据存储后端", "wincred 或 file。", false),
        (SessionStoragePath, "会话存储根目录", "必须是绝对路径。修改后不会迁移已有数据，会新建一个空会话；旧目录原样保留。", true)
    };
}
