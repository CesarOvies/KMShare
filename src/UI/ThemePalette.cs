using System.Runtime.InteropServices;

namespace KMShare.UI;

public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2
}

public sealed record ThemePalette(
    uint ColAppBg,
    uint ColCardBg,
    uint ColCardBorder,
    uint ColHeaderBg,
    uint ColHeaderTitle,
    uint ColHeaderSub,
    uint ColTextPrimary,
    uint ColTextSecondary,
    uint ColTextMuted,
    uint ColPrimaryBlue,
    uint ColPrimaryBlueHover,
    uint ColPrimaryBlueDark,
    uint ColDangerRed,
    uint ColDangerRedHover,
    uint ColDarkAccent,
    uint ColDarkAccentHover,
    uint ColSegmentActiveBg,
    uint ColSecondaryBg,
    uint ColSecondaryHover,
    uint ColLogBg,
    uint ColInputBg,
    uint ColInputBorder,
    uint ColPreviewLocalBg,
    uint ColPreviewRemoteBg,
    uint ColPreviewBorder,
    bool IsDarkTitleBar
)
{
    public static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    public static ThemePalette Light { get; } = new(
        ColAppBg: Rgb(241, 245, 249),              // Slate 100
        ColCardBg: Rgb(255, 255, 255),             // Pure White
        ColCardBorder: Rgb(203, 213, 225),         // Slate 300
        ColHeaderBg: Rgb(255, 255, 255),           // Clean White Header
        ColHeaderTitle: Rgb(15, 23, 42),           // Slate 900
        ColHeaderSub: Rgb(100, 116, 139),          // Slate 500
        ColTextPrimary: Rgb(15, 23, 42),           // Slate 900
        ColTextSecondary: Rgb(71, 85, 105),        // Slate 600
        ColTextMuted: Rgb(100, 116, 139),          // Slate 500
        ColPrimaryBlue: Rgb(37, 99, 235),          // #2563EB
        ColPrimaryBlueHover: Rgb(29, 78, 216),     // #1D4ED8
        ColPrimaryBlueDark: Rgb(30, 64, 175),      // #1E40AF
        ColDangerRed: Rgb(220, 38, 38),            // #DC2626
        ColDangerRedHover: Rgb(185, 28, 28),       // #B91C1C
        ColDarkAccent: Rgb(241, 245, 249),         // Slate 100 Button
        ColDarkAccentHover: Rgb(226, 232, 240),    // Slate 200
        ColSegmentActiveBg: Rgb(239, 246, 255),    // #EFF6FF
        ColSecondaryBg: Rgb(248, 250, 252),        // #F8FAFC
        ColSecondaryHover: Rgb(226, 232, 240),     // #E2E8F0
        ColLogBg: Rgb(248, 250, 252),              // #F8FAFC
        ColInputBg: Rgb(255, 255, 255),            // Pure White
        ColInputBorder: Rgb(203, 213, 225),        // Slate 300
        ColPreviewLocalBg: Rgb(239, 246, 255),     // Blue 50
        ColPreviewRemoteBg: Rgb(248, 250, 252),    // Slate 50
        ColPreviewBorder: Rgb(191, 219, 254),      // Blue 200
        IsDarkTitleBar: false
    );

    public static ThemePalette Dark { get; } = new(
        ColAppBg: Rgb(15, 23, 42),                 // Slate 900
        ColCardBg: Rgb(30, 41, 59),                // Slate 800
        ColCardBorder: Rgb(51, 65, 85),            // Slate 700
        ColHeaderBg: Rgb(11, 15, 25),              // Deep Midnight
        ColHeaderTitle: Rgb(248, 250, 252),        // Slate 50
        ColHeaderSub: Rgb(148, 163, 184),          // Slate 400
        ColTextPrimary: Rgb(248, 250, 252),        // Slate 50
        ColTextSecondary: Rgb(203, 213, 225),      // Slate 300
        ColTextMuted: Rgb(148, 163, 184),          // Slate 400
        ColPrimaryBlue: Rgb(59, 130, 246),         // Blue 500
        ColPrimaryBlueHover: Rgb(37, 99, 235),     // Blue 600
        ColPrimaryBlueDark: Rgb(29, 78, 216),      // Blue 700
        ColDangerRed: Rgb(239, 68, 68),            // Red 500
        ColDangerRedHover: Rgb(220, 38, 38),       // Red 600
        ColDarkAccent: Rgb(51, 65, 85),            // Slate 700
        ColDarkAccentHover: Rgb(71, 85, 105),      // Slate 600
        ColSegmentActiveBg: Rgb(30, 58, 138),      // Blue 900
        ColSecondaryBg: Rgb(30, 41, 59),           // Slate 800
        ColSecondaryHover: Rgb(51, 65, 85),        // Slate 700
        ColLogBg: Rgb(11, 15, 25),                 // Deep Dark Console
        ColInputBg: Rgb(15, 23, 42),               // Slate 900
        ColInputBorder: Rgb(51, 65, 85),           // Slate 700
        ColPreviewLocalBg: Rgb(30, 58, 138),       // Blue 900
        ColPreviewRemoteBg: Rgb(15, 23, 42),       // Slate 900
        ColPreviewBorder: Rgb(59, 130, 246),       // Blue 500
        IsDarkTitleBar: true
    );

    public static ThemePalette Resolve(AppTheme theme)
    {
        if (theme == AppTheme.Light) return Light;
        if (theme == AppTheme.Dark) return Dark;
        return DetectWindowsIsLight() ? Light : Dark;
    }

    public static bool DetectWindowsIsLight()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            int hkeyCurrentUser = unchecked((int)0x80000001);
            const int KEY_READ = 0x20019;
            if (RegOpenKeyExW((IntPtr)hkeyCurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", 0, KEY_READ, out IntPtr hKey) == 0)
            {
                try
                {
                    int val = 0;
                    int size = sizeof(int);
                    if (RegQueryValueExW(hKey, "AppsUseLightTheme", IntPtr.Zero, out int type, ref val, ref size) == 0 && type == 4 /* REG_DWORD */)
                    {
                        return val != 0;
                    }
                }
                finally
                {
                    RegCloseKey(hKey);
                }
            }
        }
        catch
        {
        }
        return false;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyExW(IntPtr hKey, string lpSubKey, int ulOptions, int samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueExW(IntPtr hKey, string lpValueName, IntPtr lpReserved, out int lpType, ref int lpData, ref int lpcbData);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegCloseKey(IntPtr hKey);
}
