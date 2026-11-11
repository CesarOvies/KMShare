using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KMShare.FileTransfer;
using KMShare.Localization;
using KMShare.Network;
using KMShare.Platform;

namespace KMShare.UI;

public enum DeviceOperationRole
{
    SenderController = 0,
    ReceiverControlled = 1
}

public sealed record KMShareUserConfig(
    ScreenEdge MonitorEdge,
    VpnFallbackStrategy PreferredVpnMode,
    string PairingPin,
    bool RunAtUserLogin,
    DeviceOperationRole DeviceRole = DeviceOperationRole.SenderController,
    string LastTargetIp = "",
    string LastTargetBtMac = "",
    AppLanguage Language = AppLanguage.Auto,
    AppTheme Theme = AppTheme.System,
    string? GitHubToken = null)
{
    public static KMShareUserConfig CreateDefault() => new(
        MonitorEdge: ScreenEdge.Right,
        PreferredVpnMode: VpnFallbackStrategy.ModeA_ZeroAdminDiagnostic,
        PairingPin: RandomNumberGenerator.GetInt32(100000, 999999).ToString(),
        RunAtUserLogin: false,
        DeviceRole: DeviceOperationRole.SenderController,
        LastTargetIp: "",
        LastTargetBtMac: "",
        Language: AppLanguage.Auto,
        Theme: AppTheme.System,
        GitHubToken: null);
}

public static class TrayAndEdgeManager
{
    private const string DefaultAutostartEntryName = "KMShare";

    public static string GetDefaultConfigFilePath()
    {
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return Path.Combine(baseDir, "KMShare", "config.json");
    }

    public static KMShareUserConfig LoadOrCreateConfig(string? customConfigPath = null)
    {
        string path = customConfigPath ?? GetDefaultConfigFilePath();
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<KMShareUserConfig>(File.ReadAllText(path));
                if (loaded != null)
                {
                    HidPlatformFactory.GetCurrent().ActiveCrossingEdge = loaded.MonitorEdge;
                    return loaded;
                }
            }
        }
        catch
        {
        }

        var cfg = KMShareUserConfig.CreateDefault();
        SaveConfig(cfg, path);
        return cfg;
    }

    public static void SaveConfig(KMShareUserConfig config, string? customConfigPath = null)
    {
        string path = customConfigPath ?? GetDefaultConfigFilePath();
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        HidPlatformFactory.GetCurrent().ActiveCrossingEdge = config.MonitorEdge;
    }

    public static string FormatLiveStatusBanner(KMShareUserConfig config, bool isBluetoothTransport, string localLanIp, string localBtMac)
    {
        string transportLabel = isBluetoothTransport
            ? "Bluetooth Out-of-Band RFCOMM (7.5ms | 133Hz | Zero-Admin)"
            : "LAN Física Direta UDP (0.8ms | 1000Hz | ~1 Gbps)";

        return $"""
            =======================================================================
            [KMShare v{AppVersionInfo.CurrentVersion} — Painel de Controle & Bandeja do Sistema (100% User-Space)]
              Status de Transporte:   {transportLabel}
              Borda Ativa do Monitor: {config.MonitorEdge} (Cruza p/ {EdgeCoordinateMapper.GetOppositeEdge(config.MonitorEdge)} no Peer)
              Modo VPN Preferido:     {config.PreferredVpnMode}
              PIN de Pareamento:      {config.PairingPin}
              Atalho de Emergência:   [F12] (Retorno imediato + Watchdog 2.5s)
              LAN Física / BT Local:  {localLanIp} | {localBtMac}
              Autostart no Login:     {(config.RunAtUserLogin ? "ATIVADO (User-Space)" : "DESATIVADO")}
            =======================================================================
            """;
    }

    public static bool SetUserSpaceAutostart(bool enabled, string? executablePath = null, string entryName = DefaultAutostartEntryName)
    {
        string exe = executablePath ?? Environment.ProcessPath ?? AppContext.BaseDirectory;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return SetWindowsHkcuRunEntry(entryName, enabled ? $"\"{exe}\" --tray" : null);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            string autostartDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config",
                "autostart");
            string desktopFile = Path.Combine(autostartDir, $"{entryName.ToLowerInvariant()}.desktop");

            if (!enabled)
            {
                if (File.Exists(desktopFile)) File.Delete(desktopFile);
                return true;
            }

            Directory.CreateDirectory(autostartDir);
            string content = $"""
                [Desktop Entry]
                Type=Application
                Name={entryName}
                Exec="{exe}" --tray
                Terminal=false
                X-GNOME-Autostart-enabled=true
                """;
            File.WriteAllText(desktopFile, content);
            return true;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            string agentsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "LaunchAgents");
            string plistFile = Path.Combine(agentsDir, $"com.kmshare.{entryName.ToLowerInvariant()}.plist");

            if (!enabled)
            {
                if (File.Exists(plistFile)) File.Delete(plistFile);
                return true;
            }

            Directory.CreateDirectory(agentsDir);
            string plist = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>Label</key>
                    <string>com.kmshare.{entryName.ToLowerInvariant()}</string>
                    <key>ProgramArguments</key>
                    <array>
                        <string>{exe}</string>
                        <string>--tray</string>
                    </array>
                    <key>RunAtLoad</key>
                    <true/>
                </dict>
                </plist>
                """;
            File.WriteAllText(plistFile, plist);
            return true;
        }

        return false;
    }

    public static bool IsUserSpaceAutostartRegistered(string entryName = DefaultAutostartEntryName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return QueryWindowsHkcuRunEntry(entryName) != null;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            string desktopFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config",
                "autostart",
                $"{entryName.ToLowerInvariant()}.desktop");
            return File.Exists(desktopFile);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            string plistFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "LaunchAgents",
                $"com.kmshare.{entryName.ToLowerInvariant()}.plist");
            return File.Exists(plistFile);
        }

        return false;
    }

    public static async Task RunInteractiveMenuAsync(
        IPAddress localLanIp,
        string localBtMac,
        Func<Task>? onRunDiagnostics,
        CancellationToken ct)
    {
        var config = LoadOrCreateConfig();
        using var trayIcon = TryStartWindowsTrayNotification(config);

        while (!ct.IsCancellationRequested)
        {
            Console.WriteLine();
            Console.WriteLine(FormatLiveStatusBanner(
                config,
                config.PreferredVpnMode == VpnFallbackStrategy.ModeC_BluetoothOutOfBand,
                localLanIp.ToString(),
                localBtMac));
            Console.WriteLine("  [G] Abrir Interface Gráfica (GUI Nativa)");
            Console.WriteLine("  [D] Buscar Dispositivos na Rede / Bluetooth (Scanner Automático)");
            Console.WriteLine("  [1] Iniciar Receptor neste PC (Dual-Stack LAN UDP + Bluetooth RFCOMM)");
            Console.WriteLine("  [2] Conectar a outro PC via LAN Física (Sender)");
            Console.WriteLine("  [3] Conectar a outro PC via Bluetooth Out-of-Band (Sender - Modo C)");
            Console.WriteLine("  [4] Enviar Arquivo para o outro PC (Step-Up Auth por Transação)");
            Console.WriteLine("  [5] Alterar Borda do Monitor (Left / Right / Top / Bottom)");
            Console.WriteLine("  [6] Alterar Modo de Resiliência VPN (Modo A: LAN 0.8ms | Modo B: Step-Up /32 | Modo C: Bluetooth 7.5ms)");
            Console.WriteLine("  [7] Gerar Novo PIN de Pareamento");
            Console.WriteLine("  [8] Alternar Inicialização no Login do Usuário (Sem Admin)");
            Console.WriteLine("  [9] Executar Autoteste de Segurança e Integridade do Sistema");
            Console.WriteLine("  [0] Sair");
            Console.Write("Escolha uma opção: ");

            string? choice = Console.ReadLine()?.Trim();
            if (choice == "0" || choice == null) break;

            switch (choice.ToUpperInvariant())
            {
                case "G":
                {
                    await KMShareGuiWindow.RunAsync(localLanIp, localBtMac, onRunDiagnostics, ct);
                    config = LoadOrCreateConfig();
                    break;
                }
                case "D":
                {
                    var found = await NetworkPeerDiscovery.ScanNetworkAndBluetoothPeersAsync(
                        localLanIp,
                        msg => Console.WriteLine($"  {msg}"),
                        ct);
                    foreach (var p in found)
                    {
                        Console.WriteLine($"  -> {p.Hostname,-26} | IP: {(string.IsNullOrEmpty(p.IpAddress) ? "N/A" : p.IpAddress),-15} | BT: {(string.IsNullOrEmpty(p.BluetoothMac) ? "N/A" : p.BluetoothMac),-17} | {p.StatusSummary}");
                    }
                    break;
                }
                case "1":
                {
                    await KMSharePeerNode.RunReceiverAsync(localLanIp, KMSharePeerNode.DefaultUdpPort, config.PairingPin, ct);
                    break;
                }
                case "2":
                {
                    Console.Write("IP LAN do PC receptor: ");
                    string? ipStr = Console.ReadLine()?.Trim();
                    Console.Write($"PIN de pareamento [{config.PairingPin}]: ");
                    string? pinInput = Console.ReadLine()?.Trim();
                    string activePin = string.IsNullOrWhiteSpace(pinInput) ? config.PairingPin : pinInput;
                    if (IPAddress.TryParse(ipStr, out IPAddress? targetIp))
                    {
                        await KMSharePeerNode.RunSenderAsync(localLanIp, targetIp, KMSharePeerNode.DefaultUdpPort, activePin, ct);
                    }
                    break;
                }
                case "3":
                {
                    Console.Write("MAC Bluetooth do PC receptor (ex: AA:BB:CC:DD:EE:FF): ");
                    string? btTarget = Console.ReadLine()?.Trim();
                    Console.Write($"PIN de pareamento [{config.PairingPin}]: ");
                    string? pinInput = Console.ReadLine()?.Trim();
                    string activePin = string.IsNullOrWhiteSpace(pinInput) ? config.PairingPin : pinInput;
                    if (!string.IsNullOrWhiteSpace(btTarget))
                    {
                        await BluetoothRfcommNode.RunBluetoothSenderAsync(btTarget, activePin, ct);
                    }
                    break;
                }
                case "4":
                {
                    Console.Write("IP LAN do PC receptor: ");
                    string? ipStr = Console.ReadLine()?.Trim();
                    Console.Write("Caminho do arquivo a enviar: ");
                    string? filePath = Console.ReadLine()?.Trim()?.Trim('"');
                    if (IPAddress.TryParse(ipStr, out IPAddress? targetIp) && !string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
                    {
                        var res = await SecureFileChannel.SendSingleFileOverTcpAsync(
                            localLanIp,
                            new IPEndPoint(targetIp, SecureFileChannel.DefaultTcpFilePort),
                            config.PairingPin,
                            filePath,
                            (sent, total) => Console.Write($"\r  -> Enviando: {sent}/{total} bytes..."),
                            ct: ct);
                        Console.WriteLine($"\n[+] Resultado do envio: {res.Status}");
                    }
                    break;
                }
                case "5":
                {
                    var nextEdge = (ScreenEdge)(((int)config.MonitorEdge + 1) % 4);
                    config = config with { MonitorEdge = nextEdge };
                    SaveConfig(config);
                    Console.WriteLine($"[+] Borda do monitor atualizada para: {config.MonitorEdge}");
                    break;
                }
                case "6":
                {
                    var nextMode = config.PreferredVpnMode switch
                    {
                        VpnFallbackStrategy.ModeA_ZeroAdminDiagnostic => VpnFallbackStrategy.ModeB_StepUpWfpLanPermit,
                        VpnFallbackStrategy.ModeB_StepUpWfpLanPermit => VpnFallbackStrategy.ModeC_BluetoothOutOfBand,
                        _ => VpnFallbackStrategy.ModeA_ZeroAdminDiagnostic
                    };
                    config = config with { PreferredVpnMode = nextMode };
                    SaveConfig(config);
                    Console.WriteLine($"[+] Modo VPN atualizado para: {config.PreferredVpnMode}");
                    break;
                }
                case "7":
                {
                    string newPin = RandomNumberGenerator.GetInt32(100000, 999999).ToString();
                    config = config with { PairingPin = newPin };
                    SaveConfig(config);
                    Console.WriteLine($"[+] Novo PIN gerado: {config.PairingPin}");
                    break;
                }
                case "8":
                {
                    bool nextState = !config.RunAtUserLogin;
                    if (SetUserSpaceAutostart(nextState))
                    {
                        config = config with { RunAtUserLogin = nextState };
                        SaveConfig(config);
                        Console.WriteLine($"[+] Inicialização com o login do usuário: {(nextState ? "ATIVADA" : "DESATIVADA")}");
                    }
                    break;
                }
                case "9":
                {
                    if (onRunDiagnostics != null)
                    {
                        await onRunDiagnostics();
                    }
                    break;
                }
            }
        }
    }

    private static IDisposable? TryStartWindowsTrayNotification(KMShareUserConfig config)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        try
        {
            return new WindowsSystemTrayBadge(
                $"KMShare v{AppVersionInfo.CurrentVersion} | Edge:{config.MonitorEdge} | PIN:{config.PairingPin} | [F12] Emergência");
        }
        catch
        {
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Windows User-Space Registry (HKCU\Software\Microsoft\Windows\CurrentVersion\Run)
    // -------------------------------------------------------------------------
    private static readonly IntPtr HKEY_CURRENT_USER = unchecked((IntPtr)0x80000001);
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const int KEY_READ = 0x20019;
    private const int KEY_WRITE = 0x20006;
    private const uint REG_SZ = 1;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyExW(IntPtr hKey, string lpSubKey, uint ulOptions, int samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyExW(
        IntPtr hKey,
        string lpSubKey,
        uint Reserved,
        string? lpClass,
        uint dwOptions,
        int samDesired,
        IntPtr lpSecurityAttributes,
        out IntPtr phkResult,
        out uint lpdwDisposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueExW(IntPtr hKey, string lpValueName, uint Reserved, uint dwType, byte[] lpData, int cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegDeleteValueW(IntPtr hKey, string lpValueName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueExW(IntPtr hKey, string lpValueName, IntPtr lpReserved, out uint lpType, byte[]? lpData, ref uint lpcbData);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);

    private static bool SetWindowsHkcuRunEntry(string valueName, string? commandLine)
    {
        if (RegCreateKeyExW(HKEY_CURRENT_USER, RunSubKey, 0, null, 0, KEY_WRITE, IntPtr.Zero, out IntPtr hKey, out _) != 0)
            return false;

        try
        {
            if (commandLine == null)
            {
                int del = RegDeleteValueW(hKey, valueName);
                return del == 0 || del == 2; // 2 = ERROR_FILE_NOT_FOUND
            }

            byte[] bytes = Encoding.Unicode.GetBytes(commandLine + "\0");
            return RegSetValueExW(hKey, valueName, 0, REG_SZ, bytes, bytes.Length) == 0;
        }
        finally
        {
            RegCloseKey(hKey);
        }
    }

    private static string? QueryWindowsHkcuRunEntry(string valueName)
    {
        if (RegOpenKeyExW(HKEY_CURRENT_USER, RunSubKey, 0, KEY_READ, out IntPtr hKey) != 0)
            return null;

        try
        {
            uint size = 0;
            if (RegQueryValueExW(hKey, valueName, IntPtr.Zero, out uint type, null, ref size) != 0 || size == 0 || type != REG_SZ)
                return null;

            byte[] buf = new byte[size];
            if (RegQueryValueExW(hKey, valueName, IntPtr.Zero, out _, buf, ref size) != 0)
                return null;

            return Encoding.Unicode.GetString(buf).TrimEnd('\0');
        }
        finally
        {
            RegCloseKey(hKey);
        }
    }

    // -------------------------------------------------------------------------
    // Lightweight Win32 Shell_NotifyIconW Wrapper (Zero WinForms/WPF overhead)
    // -------------------------------------------------------------------------
    private sealed class WindowsSystemTrayBadge : IDisposable
    {
        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_DELETE = 0x00000002;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;
        private const int IDI_APPLICATION = 32512;

        private NOTIFYICONDATAW _nid;
        private readonly bool _added;

        public WindowsSystemTrayBadge(string tooltip)
        {
            IntPtr hExe = GetModuleHandleW(null);
            IntPtr hIcon = LoadIconW(hExe, (IntPtr)IDI_APPLICATION);
            if (hIcon == IntPtr.Zero)
            {
                hIcon = LoadIconW(hExe, (IntPtr)1);
            }
            if (hIcon == IntPtr.Zero)
            {
                hIcon = LoadIconW(IntPtr.Zero, (IntPtr)IDI_APPLICATION);
            }
            _nid = new NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = GetConsoleWindow(),
                uID = 1001,
                uFlags = NIF_ICON | NIF_TIP,
                hIcon = hIcon,
                szTip = tooltip.Length > 120 ? tooltip[..120] : tooltip
            };

            if (_nid.hWnd != IntPtr.Zero)
            {
                _added = Shell_NotifyIconW(NIM_ADD, ref _nid);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string? lpModuleName);

        public void Dispose()
        {
            if (_added)
            {
                Shell_NotifyIconW(NIM_DELETE, ref _nid);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATAW
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpdata);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();
    }
}
