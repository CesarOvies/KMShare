using System.Reflection;

namespace MKShare.Platform;

/// <summary>
/// Provedor centralizado da versão do KMShare em tempo de execução.
/// </summary>
public static class AppVersionInfo
{
    public static string CurrentVersion { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var asm = typeof(AppVersionInfo).Assembly;

        var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVer))
        {
            int plus = infoVer.IndexOf('+');
            if (plus > 0) infoVer = infoVer[..plus];
            return infoVer.Trim();
        }

        var ver = asm.GetName().Version;
        if (ver != null && ver != new Version(0, 0, 0, 0))
        {
            return ver.Build >= 0 ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : $"{ver.Major}.{ver.Minor}.0";
        }

        return "1.0.1";
    }
}

