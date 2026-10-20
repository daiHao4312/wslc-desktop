using Microsoft.WSL.Containers;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Services;

/// <summary>默认探测实现：直接问官方 <c>WslcService</c>，把异常收敛成结果。</summary>
public sealed class WslcSdkProbe : IWslcSdkProbe
{
    public WslcSdkInfo Query()
    {
        try
        {
            var version = WslcService.GetVersion();
            var missing = WslcService.GetMissingComponents()
                .Select(component => component.ToString())
                .ToList();

            return new WslcSdkInfo
            {
                Available = true,
                SessionManagerVersion = $"{version.Major}.{version.Minor}.{version.Revision}",
                MissingComponents = missing
            };
        }
        catch (Exception ex)
        {
            return new WslcSdkInfo { Available = false, Error = ex.Message };
        }
    }
}
