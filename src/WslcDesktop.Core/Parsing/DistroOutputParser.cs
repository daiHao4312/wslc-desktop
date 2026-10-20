using System.Text.RegularExpressions;
using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 解析 wsl.exe 与注册表 Lxss 的原始输出。
///
/// 实测格式（夹具来自参考项目 wslc-panel 的真机抓取，见 distro_list*.txt / lxss_query.txt）：
/// <code>
///   NAME               STATE         VERSION
/// * Ubuntu-26.04      Stopped       2
///   Ubuntu 24.04 LTS  Running       2
/// </code>
/// 注意发行版名称可以含空格（"Ubuntu 24.04 LTS"），所以**不能**按空格切列，
/// 必须从行尾往前切：最后一段是版本号，倒数第二段是状态，其余都是名称。
/// </summary>
public static class DistroOutputParser
{
    private static readonly Regex RegistryValueRegex = new(
        @"^\s{4}(?<key>\S+)\s+REG_(?<type>\w+)\s+(?<value>.*)$", RegexOptions.Compiled);

    private static readonly Regex RegistryKeyRegex = new(
        @"^HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Lxss(?<guid>\\\{[0-9a-fA-F\-]{36}\})?$",
        RegexOptions.Compiled);

    /// <summary>
    /// <c>wsl --list --online</c> 的数据行：ASCII 名 + 空白分隔 + 友好名。
    /// 名里不含空格（引擎侧的标识符），友好名可以含空格。
    /// </summary>
    private static readonly Regex OnlineLineRegex = new(
        @"^(?<name>[A-Za-z0-9][A-Za-z0-9._\-]*)[ \t]+(?<friendly>\S.*)$",
        RegexOptions.Compiled);

    /// <summary>
    /// 解析 <c>wsl --list --verbose</c>。
    /// 调用方注入 WSL_UTF8=1（见 CliProcessRunner.ResolveEncoding），输出为 UTF-8。
    /// 表头跟随英文 NAME 关键字；非英语 Windows 上若出现本地化表头（"名称"），
    /// 同样跳过——数据行以行尾两段（状态 + 版本号）为结构特征，不依赖表头文字。
    /// </summary>
    public static List<DistroModel> ParseList(string? output)
    {
        var result = new List<DistroModel>();
        if (string.IsNullOrWhiteSpace(output)) return result;

        var lines = output.Split('\n');
        var headerSeen = false;

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            var line = rawLine.TrimEnd('\r');

            if (!headerSeen)
            {
                if (line.TrimStart().StartsWith("NAME", StringComparison.OrdinalIgnoreCase) ||
                    line.TrimStart().StartsWith("名称"))
                {
                    headerSeen = true;
                }
                continue;
            }

            var isDefault = false;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('*'))
            {
                isDefault = true;
                trimmed = trimmed[1..].TrimStart();
            }

            if (trimmed.Length == 0) continue;

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

            if (!int.TryParse(parts[^1], out var version)) continue;

            var state = parts[^2];
            var name = string.Join(" ", parts[..^2]);

            result.Add(new DistroModel
            {
                Name = name,
                State = state,
                Version = version,
                IsDefault = isDefault
            });
        }

        return result;
    }

    /// <summary>
    /// 解析 <c>reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss /s</c>，
    /// 把安装目录、默认发行版等挂在 <paramref name="distros"/> 上（按名称匹配）。
    /// </summary>
    public static List<DistroModel> MergeRegistryInfo(List<DistroModel> distros, string? registryOutput)
    {
        if (distros == null || distros.Count == 0) return distros ?? new List<DistroModel>();
        if (string.IsNullOrWhiteSpace(registryOutput)) return distros;

        var perGuid = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string? currentGuid = null;
        var defaultDistributionGuid = string.Empty;
        int? defaultVersion = null;

        foreach (var rawLine in registryOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            var keyMatch = RegistryKeyRegex.Match(line.Trim());
            if (keyMatch.Success)
            {
                // 键行 GUID 含前导反斜杠（Lxss\{guid}）：归一化掉，
                // 否则和 DefaultDistribution 值（不含反斜杠的 {guid}）永远匹配不上
                currentGuid = keyMatch.Groups["guid"].Success
                    ? keyMatch.Groups["guid"].Value.TrimStart('\\')
                    : null;

                if (currentGuid != null)
                {
                    perGuid[currentGuid] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                continue;
            }

            var valueMatch = RegistryValueRegex.Match(line);
            if (!valueMatch.Success) continue;

            var key = valueMatch.Groups["key"].Value;
            var value = valueMatch.Groups["value"].Value.Trim();

            if (currentGuid == null)
            {
                if (key.Equals("DefaultDistribution", StringComparison.OrdinalIgnoreCase)) defaultDistributionGuid = value;
                else if (key.Equals("DefaultVersion", StringComparison.OrdinalIgnoreCase)) defaultVersion = ParseHexDword(value);
                continue;
            }

            perGuid[currentGuid][key] = value;
        }

        var result = new List<DistroModel>(distros.Count);
        foreach (var distro in distros)
        {
            var entry = perGuid.Values.FirstOrDefault(
                e => e.TryGetValue("DistributionName", out var n) &&
                     string.Equals(n, distro.Name, StringComparison.OrdinalIgnoreCase));

            if (entry == null)
            {
                result.Add(distro);
                continue;
            }

            var basePath = entry.GetValueOrDefault("BasePath", string.Empty);
            var vhdFileName = entry.GetValueOrDefault("VhdFileName", "ext4.vhdx");

            var isDefault = distro.IsDefault;
            // DefaultDistribution 的值是不含前导反斜杠的 {guid}（字典 key 已在解析时归一化）
            if (!string.IsNullOrEmpty(defaultDistributionGuid) &&
                perGuid.TryGetValue(defaultDistributionGuid.TrimStart('\\'), out var defEntry))
            {
                isDefault = defEntry.TryGetValue("DistributionName", out var defName) &&
                            string.Equals(defName, distro.Name, StringComparison.OrdinalIgnoreCase);
            }

            var version = distro.Version;
            if (version <= 0 && entry.TryGetValue("Version", out var verRaw))
            {
                version = ParseHexDword(verRaw) ?? version;
            }

            if (version <= 0 && defaultVersion.HasValue) version = defaultVersion.Value;

            result.Add(distro with
            {
                BasePath = basePath,
                VhdFileName = vhdFileName,
                IsDefault = isDefault,
                Version = version,
                VhdSizeBytes = TryGetFileSize(basePath, vhdFileName)
            });
        }

        return result;
    }

    /// <summary>
    /// 解析 <c>wsl --list --online</c>。实测形态（中文 Windows）：
    /// <code>
    /// 以下是可安装的有效分发的列表。
    /// 使用"wsl.exe --install &lt;Distro&gt;"进行安装。
    ///
    /// NAME                                   FRIENDLY NAME
    /// Ubuntu                                 Ubuntu
    /// Ubuntu-24.04                           Ubuntu 24.04 LTS
    /// </code>
    /// 因此**不靠表头文字**（本地化会变）：只认"以 ASCII 名字开头 + 空白 + 友好名"的行，
    /// 中文前言、空行、"使用…进行安装"这类提示行天然被过滤掉。
    /// </summary>
    public static List<OnlineDistroModel> ParseOnlineList(string? output)
    {
        var result = new List<OnlineDistroModel>();
        if (string.IsNullOrWhiteSpace(output)) return result;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var match = OnlineLineRegex.Match(line);
            if (!match.Success) continue;

            var name = match.Groups["name"].Value;
            var friendly = match.Groups["friendly"].Value.Trim();

            if (name.Equals("NAME", StringComparison.OrdinalIgnoreCase)) continue;

            result.Add(new OnlineDistroModel { Name = name, FriendlyName = friendly });
        }

        return result;
    }

    private static long? TryGetFileSize(string basePath, string vhdFileName)
    {
        if (string.IsNullOrWhiteSpace(basePath)) return null;
        try
        {
            var path = Path.Combine(basePath, string.IsNullOrWhiteSpace(vhdFileName) ? "ext4.vhdx" : vhdFileName);
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? ParseHexDword(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex))
        {
            return hex;
        }

        return int.TryParse(value, out var dec) ? dec : null;
    }
}
