namespace WslcDesktop.Core.Services;

/// <summary>
/// 目录可写探测：替换前先问一句"这里真的写得进去吗"。
///
/// 为什么要提前问：用户很可能把单文件 exe 放在 <c>C:\Program Files\</c> 或别的受保护位置，
/// 那时 <c>File.Move</c>/<c>File.Copy</c> 会在替换中途抛 Access Denied——
/// 与其下载 96 MB 之后才失败，不如在下载前就判定并引导用户改用「打开下载页」。
/// </summary>
public static class DirectoryWriteProbe
{
    /// <summary>
    /// 目录存在且能创建/删除文件时返回 true。
    /// 任何异常都当作"不可写"（这正是我们要识别的结果），不向上抛。
    /// </summary>
    public static bool CanWrite(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) return false;

        try
        {
            if (!Directory.Exists(directoryPath)) return false;

            // 用随机名避免与真实文件撞名；写完立刻删，不留痕
            var probe = Path.Combine(directoryPath, $".wslc-write-probe-{Guid.NewGuid():N}.tmp");

            try
            {
                using (var stream = new FileStream(
                    probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, useAsync: false))
                {
                    stream.WriteByte(0);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(probe)) File.Delete(probe);
                }
                catch
                {
                    // 删不掉不影响结论：我们已经证明过"写得进去"
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
