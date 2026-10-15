using System.Text.Json;
using WSLC.Panel.Core.Models;

namespace WSLC.Panel.Core.Parsing;

public static class JsonLinesParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true
    };

    public static List<ContainerModel> ParseContainers(string? jsonLines)
    {
        var result = new List<ContainerModel>();
        if (string.IsNullOrWhiteSpace(jsonLines)) return result;

        using var reader = new StringReader(jsonLines);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var id = GetString(root, "ID");
                var name = OutputSanitizer.NormalizeContainerName(GetString(root, "Names"));
                var image = GetString(root, "Image");
                var command = GetString(root, "Command");
                var state = GetString(root, "State");
                var status = GetString(root, "Status");
                var portsRaw = GetString(root, "Ports");
                var networks = GetString(root, "Networks");
                var createdAtRaw = GetString(root, "CreatedAt");
                var runningFor = GetString(root, "RunningFor");
                var size = GetString(root, "Size");

                result.Add(new ContainerModel
                {
                    Id = id,
                    Name = name,
                    Image = image,
                    Command = command,
                    State = state,
                    Status = status,
                    PortsRaw = portsRaw,
                    PortMappings = OutputSanitizer.ParsePorts(portsRaw),
                    Networks = networks,
                    CreatedAtRaw = createdAtRaw,
                    CreatedAt = OutputSanitizer.ParseDateTime(createdAtRaw),
                    RunningFor = runningFor,
                    Size = size
                });
            }
            catch
            {
                // Tolerant: ignore malformed line
            }
        }
        return result;
    }

    public static List<ContainerStatsModel> ParseStats(string? jsonLines)
    {
        var result = new List<ContainerStatsModel>();
        if (string.IsNullOrWhiteSpace(jsonLines)) return result;

        using var reader = new StringReader(jsonLines);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var id = GetString(root, "ID");
                var name = OutputSanitizer.NormalizeContainerName(GetString(root, "Name"));
                var cpuPercRaw = GetString(root, "CPUPerc");
                var memUsage = GetString(root, "MemUsage");
                var memPercRaw = GetString(root, "MemPerc");
                var netIO = GetString(root, "NetIO");
                var blockIO = GetString(root, "BlockIO");
                var pids = GetInt(root, "PIDs");

                result.Add(new ContainerStatsModel
                {
                    ContainerId = id,
                    Name = name,
                    CpuPercent = OutputSanitizer.ParsePercentage(cpuPercRaw),
                    CpuPercentRaw = string.IsNullOrEmpty(cpuPercRaw) ? "0.00%" : cpuPercRaw,
                    MemoryUsage = memUsage,
                    MemoryPercent = OutputSanitizer.ParsePercentage(memPercRaw),
                    MemoryPercentRaw = string.IsNullOrEmpty(memPercRaw) ? "0.00%" : memPercRaw,
                    NetIO = netIO,
                    BlockIO = blockIO,
                    Pids = pids
                });
            }
            catch
            {
                // Tolerant: ignore malformed line
            }
        }
        return result;
    }

    public static List<ImageModel> ParseImages(string? jsonLines)
    {
        var result = new List<ImageModel>();
        if (string.IsNullOrWhiteSpace(jsonLines)) return result;

        using var reader = new StringReader(jsonLines);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var id = GetString(root, "ID");
                var repo = GetString(root, "Repository");
                var tag = GetString(root, "Tag");
                var size = GetString(root, "Size");
                var createdAtRaw = GetString(root, "CreatedAt");
                var digest = GetString(root, "Digest");

                result.Add(new ImageModel
                {
                    Id = id,
                    Repository = repo,
                    Tag = tag,
                    Size = size,
                    CreatedAtRaw = createdAtRaw,
                    CreatedAt = OutputSanitizer.ParseDateTime(createdAtRaw),
                    Digest = digest
                });
            }
            catch
            {
                // Tolerant
            }
        }
        return result;
    }

    public static List<NetworkModel> ParseNetworks(string? jsonLines)
    {
        var result = new List<NetworkModel>();
        if (string.IsNullOrWhiteSpace(jsonLines)) return result;

        using var reader = new StringReader(jsonLines);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var id = GetString(root, "ID");
                var name = GetString(root, "Name");
                var driver = GetString(root, "Driver");
                var scope = GetString(root, "Scope");
                var ipv4 = GetBool(root, "IPv4");
                var ipv6 = GetBool(root, "IPv6");
                var internalNet = GetBool(root, "Internal");
                var createdAt = GetString(root, "CreatedAt");

                result.Add(new NetworkModel
                {
                    Id = id,
                    Name = name,
                    Driver = driver,
                    Scope = string.IsNullOrEmpty(scope) ? "local" : scope,
                    IPv4 = ipv4,
                    IPv6 = ipv6,
                    Internal = internalNet,
                    CreatedAt = createdAt
                });
            }
            catch
            {
                // Tolerant
            }
        }
        return result;
    }

    /// <summary>
    /// 解析 <c>wslc volume list --format json</c>（JSON Lines）。
    /// 实测字段：Availability/Driver/Group/Labels/Links/Mountpoint/Name/Scope/Size/Status，
    /// 其中 Size、Availability 等常为字符串 "N/A"，统一归一为空串交由界面判断。
    /// </summary>
    public static List<VolumeModel> ParseVolumes(string? jsonLines)
    {
        var result = new List<VolumeModel>();
        if (string.IsNullOrWhiteSpace(jsonLines)) return result;

        using var reader = new StringReader(jsonLines);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var name = GetString(root, "Name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                result.Add(new VolumeModel
                {
                    Name = name,
                    Driver = NormalizeUnknown(GetString(root, "Driver"), "local"),
                    Scope = NormalizeUnknown(GetString(root, "Scope"), "local"),
                    Size = NormalizeUnknown(GetString(root, "Size"), string.Empty),
                    Mountpoint = NormalizeUnknown(GetString(root, "Mountpoint"), string.Empty),
                    Labels = NormalizeUnknown(GetString(root, "Labels"), string.Empty)
                });
            }
            catch
            {
                // Tolerant: ignore malformed line
            }
        }
        return result;
    }

    private static string NormalizeUnknown(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) || value == "N/A" ? fallback : value;

    public static SystemInfoModel ParseSystemInfo(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new SystemInfoModel();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string wslVer = "", kernelVer = "", d3dVer = "", dxCoreVer = "", winVer = "", settingsFile = "";
            if (root.TryGetProperty("Client", out var client))
            {
                wslVer = GetString(client, "Version");
                kernelVer = GetString(client, "KernelVersion");
                d3dVer = GetString(client, "Direct3DVersion");
                dxCoreVer = GetString(client, "DxCoreVersion");
                winVer = GetString(client, "WindowsVersion");
                settingsFile = GetString(client, "SettingsFile");
            }

            string sessionMgrVer = "";
            var sessions = new List<SessionModel>();
            if (root.TryGetProperty("Server", out var server))
            {
                sessionMgrVer = GetString(server, "SessionManagerVersion");
                if (server.TryGetProperty("Sessions", out var sessArray) && sessArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in sessArray.EnumerateArray())
                    {
                        var sid = GetInt(s, "ID") ?? 0;
                        var pid = GetInt(s, "CreatorPid") ?? 0;
                        var sname = GetString(s, "Name");
                        sessions.Add(new SessionModel
                        {
                            Id = sid,
                            CreatorPid = pid,
                            DisplayName = sname
                        });
                    }
                }
            }

            return new SystemInfoModel
            {
                WslVersion = wslVer,
                KernelVersion = kernelVer,
                Direct3DVersion = d3dVer,
                DxCoreVersion = dxCoreVer,
                WindowsVersion = winVer,
                SettingsFilePath = settingsFile,
                SessionManagerVersion = sessionMgrVer,
                ActiveSessions = sessions
            };
        }
        catch
        {
            return new SystemInfoModel();
        }
    }

    private static string GetString(JsonElement elem, string propName)
    {
        if (elem.TryGetProperty(propName, out var p))
        {
            return p.ValueKind switch
            {
                JsonValueKind.String => p.GetString() ?? string.Empty,
                JsonValueKind.Number => p.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => string.Empty
            };
        }
        return string.Empty;
    }

    private static int? GetInt(JsonElement elem, string propName)
    {
        if (elem.TryGetProperty(propName, out var p))
        {
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i)) return i;
            if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var si)) return si;
        }
        return null;
    }

    private static bool GetBool(JsonElement elem, string propName)
    {
        if (elem.TryGetProperty(propName, out var p))
        {
            if (p.ValueKind == JsonValueKind.True) return true;
            if (p.ValueKind == JsonValueKind.False) return false;
            if (p.ValueKind == JsonValueKind.String && bool.TryParse(p.GetString(), out var b)) return b;
        }
        return false;
    }
}
