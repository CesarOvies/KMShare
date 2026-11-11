using System.Globalization;
using MKShare.Platform;

namespace MKShare.Localization;

public enum AppLanguage
{
    Auto = 0,
    Portuguese = 1,
    English = 2,
    Spanish = 3
}

public enum LocKey
{
    AppTitle,
    HeaderSubtitle,
    CardRoleTitle,
    RoleSenderButton,
    RoleReceiverButton,
    PinLabel,
    PhysicalNicLabel,
    RefreshButton,
    NewPinButton,
    CopyDataButton,
    AutostartCheckbox,
    CardScannerTitle,
    ScanPeersButton,
    ColHost,
    ColIp,
    ColBtMac,
    ColArpMac,
    ColDiag,
    ScanSummaryInitial,
    CardTargetTitle,
    TargetIpLabel,
    ProbeVpnButton,
    TargetBtLabel,
    TargetBtNote,
    ConnModeLabel,
    ModeAuto,
    ModeA,
    ModeB,
    ModeC,
    CardEdgeTitle,
    EdgeLeft,
    EdgeTop,
    EdgeBottom,
    EdgeRight,
    MonitorLocal,
    MonitorRemote,
    CardActionsTitle,
    ConnectButton,
    DisconnectButton,
    StartReceiverButton,
    StopReceiverButton,
    ApplyModeBButton,
    SendFileButton,
    SelfTestButton,
    CardLogTitle,
    ClearLogButton,
    BadgeReceiverReady,
    BadgeSenderReady,
    BadgeReceiverActive,
    BadgeSenderActive,
    BadgeControllingRemote,
    ThemeLight,
    ThemeDark,
    ThemeSystem,
    LangPt,
    LangEn,
    LangEs
}

public static class LocalizationManager
{
    private static AppLanguage _configuredLanguage = AppLanguage.Auto;
    private static AppLanguage _activeResolvedLanguage = AppLanguage.Portuguese;

    private static readonly Dictionary<AppLanguage, Dictionary<LocKey, string>> Catalogs = new()
    {
        [AppLanguage.Portuguese] = new()
        {
            [LocKey.AppTitle] = "KMShare — Central de Conexão Multi-Dispositivo",
            [LocKey.HeaderSubtitle] = "LAN Física: {0}   •   Bluetooth Local: {1}   •   Criptografia: ECDH P-256 + AES-256-GCM (Zero-Admin)",
            [LocKey.CardRoleTitle] = "1. Modo de Operação deste Dispositivo (Selecione em cada computador)",
            [LocKey.RoleSenderButton] = "Controlador (Principal) — Enviar Mouse e Teclado",
            [LocKey.RoleReceiverButton] = "Receptor (Secundário) — Receber Controle neste PC",
            [LocKey.PinLabel] = "PIN:",
            [LocKey.PhysicalNicLabel] = "Placa Física:",
            [LocKey.RefreshButton] = "Atualizar",
            [LocKey.NewPinButton] = "Novo PIN",
            [LocKey.CopyDataButton] = "Copiar Meus Dados",
            [LocKey.AutostartCheckbox] = "Iniciar com o Windows (User-Space)",
            [LocKey.CardScannerTitle] = "2. Dispositivos na Rede Local (Beacon UDP + Camada 2 ARP) e Bluetooth",
            [LocKey.ScanPeersButton] = "Buscar Dispositivos na Rede e Bluetooth",
            [LocKey.ColHost] = "Dispositivo / Hostname",
            [LocKey.ColIp] = "IP LAN Física",
            [LocKey.ColBtMac] = "MAC Bluetooth (Modo C)",
            [LocKey.ColArpMac] = "MAC Físico (ARP L2)",
            [LocKey.ColDiag] = "Diagnóstico / Modo Recomendado",
            [LocKey.ScanSummaryInitial] = "Clique em 'Buscar na Rede' para descobrir computadores via LAN / ARP L2 e Bluetooth.",
            [LocKey.CardTargetTitle] = "3A. Destino e Modo de Conexão",
            [LocKey.TargetIpLabel] = "IP ou Hostname:",
            [LocKey.ProbeVpnButton] = "Diagnosticar Rota / VPN",
            [LocKey.TargetBtLabel] = "MAC Bluetooth:",
            [LocKey.TargetBtNote] = "(Usado no Modo C Out-of-Band)",
            [LocKey.ConnModeLabel] = "Modo de Operação:",
            [LocKey.ModeAuto] = "Automático Inteligente (LAN Direta -> ARP Probe -> Fallback)",
            [LocKey.ModeA] = "Modo A — LAN Física Direta (UDP :51820 | 0.8ms | 1000Hz | Sem Admin)",
            [LocKey.ModeB] = "Modo B — LAN com Step-Up Auth (Rota /32 + Firewall WFP | 1000Hz)",
            [LocKey.ModeC] = "Modo C — Bluetooth Out-of-Band (RFCOMM Canal 5 | 133Hz | Ignora VPN)",
            [LocKey.CardEdgeTitle] = "3B. Borda do Monitor ({0} -> {1})",
            [LocKey.EdgeLeft] = "Esquerda",
            [LocKey.EdgeTop] = "Topo",
            [LocKey.EdgeBottom] = "Base",
            [LocKey.EdgeRight] = "Direita",
            [LocKey.MonitorLocal] = "Este PC",
            [LocKey.MonitorRemote] = "PC Remoto",
            [LocKey.CardActionsTitle] = "",
            [LocKey.ConnectButton] = "Conectar e Compartilhar Mouse / Teclado",
            [LocKey.DisconnectButton] = "Desconectar e Liberar Mouse / Teclado",
            [LocKey.StartReceiverButton] = "Iniciar Modo Receptor neste PC",
            [LocKey.StopReceiverButton] = "Parar Modo Receptor (Desconectar)",
            [LocKey.ApplyModeBButton] = "Liberar Rota /32 (Modo B)",
            [LocKey.SendFileButton] = "Enviar Arquivo Seguro...",
            [LocKey.SelfTestButton] = "Autoteste",
            [LocKey.CardLogTitle] = "4. Console de Eventos, Criptografia e Diagnóstico em Tempo Real",
            [LocKey.ClearLogButton] = "Limpar Log",
            [LocKey.BadgeReceiverReady] = "MODO RECEPTOR (PRONTO)  •  PIN {0}",
            [LocKey.BadgeSenderReady] = "MODO CONTROLADOR (PRONTO)  •  PIN {0}",
            [LocKey.BadgeReceiverActive] = "RECEPTOR ATIVO  •  PIN {0}",
            [LocKey.BadgeSenderActive] = "CONTROLADOR ATIVO  •  Borda {0}",
            [LocKey.BadgeControllingRemote] = "CONTROLANDO REMOTO ({0})  •  [F12] Voltar",
            [LocKey.ThemeLight] = "Claro",
            [LocKey.ThemeDark] = "Escuro",
            [LocKey.ThemeSystem] = "Sistema",
            [LocKey.LangPt] = "Português",
            [LocKey.LangEn] = "Inglês",
            [LocKey.LangEs] = "Espanhol"
        },
        [AppLanguage.English] = new()
        {
            [LocKey.AppTitle] = "KMShare — Multi-Device Control Center",
            [LocKey.HeaderSubtitle] = "Physical LAN: {0}   •   Local Bluetooth: {1}   •   Encryption: ECDH P-256 + AES-256-GCM (Zero-Admin)",
            [LocKey.CardRoleTitle] = "1. Device Operation Mode (Select on each computer)",
            [LocKey.RoleSenderButton] = "Controller (Primary) — Send Mouse & Keyboard",
            [LocKey.RoleReceiverButton] = "Receiver (Secondary) — Receive Control on this PC",
            [LocKey.PinLabel] = "PIN:",
            [LocKey.PhysicalNicLabel] = "Physical Adapter:",
            [LocKey.RefreshButton] = "Refresh",
            [LocKey.NewPinButton] = "New PIN",
            [LocKey.CopyDataButton] = "Copy My Info",
            [LocKey.AutostartCheckbox] = "Start with Windows (User-Space)",
            [LocKey.CardScannerTitle] = "2. Local Network (UDP Beacon + L2 ARP) & Bluetooth Devices",
            [LocKey.ScanPeersButton] = "Scan Network & Bluetooth Devices",
            [LocKey.ColHost] = "Device / Hostname",
            [LocKey.ColIp] = "Physical LAN IP",
            [LocKey.ColBtMac] = "Bluetooth MAC (Mode C)",
            [LocKey.ColArpMac] = "Physical MAC (L2 ARP)",
            [LocKey.ColDiag] = "Diagnosis / Recommended Mode",
            [LocKey.ScanSummaryInitial] = "Click 'Scan Network' to discover computers via LAN / L2 ARP and Bluetooth.",
            [LocKey.CardTargetTitle] = "3A. Target & Connection Mode",
            [LocKey.TargetIpLabel] = "IP or Hostname:",
            [LocKey.ProbeVpnButton] = "Probe Route / VPN",
            [LocKey.TargetBtLabel] = "Bluetooth MAC:",
            [LocKey.TargetBtNote] = "(Used in Mode C Out-of-Band)",
            [LocKey.ConnModeLabel] = "Operation Mode:",
            [LocKey.ModeAuto] = "Smart Automatic (Direct LAN -> ARP Probe -> Fallback)",
            [LocKey.ModeA] = "Mode A — Direct Physical LAN (UDP :51820 | 0.8ms | 1000Hz | No Admin)",
            [LocKey.ModeB] = "Mode B — LAN with Step-Up Auth (/32 Route + WFP Firewall | 1000Hz)",
            [LocKey.ModeC] = "Mode C — Bluetooth Out-of-Band (RFCOMM Channel 5 | 133Hz | Bypasses VPN)",
            [LocKey.CardEdgeTitle] = "3B. Monitor Edge ({0} -> {1})",
            [LocKey.EdgeLeft] = "Left",
            [LocKey.EdgeTop] = "Top",
            [LocKey.EdgeBottom] = "Bottom",
            [LocKey.EdgeRight] = "Right",
            [LocKey.MonitorLocal] = "This PC",
            [LocKey.MonitorRemote] = "Remote PC",
            [LocKey.CardActionsTitle] = "",
            [LocKey.ConnectButton] = "Connect and Share Mouse / Keyboard",
            [LocKey.DisconnectButton] = "Disconnect & Unlock Mouse / Keyboard",
            [LocKey.StartReceiverButton] = "Start Receiver Mode on this PC",
            [LocKey.StopReceiverButton] = "Stop Receiver Mode (Disconnect)",
            [LocKey.ApplyModeBButton] = "Allow /32 Route (Mode B)",
            [LocKey.SendFileButton] = "Send Secure File...",
            [LocKey.SelfTestButton] = "Self-Test",
            [LocKey.CardLogTitle] = "4. Real-Time Events, Cryptography & Diagnostics Console",
            [LocKey.ClearLogButton] = "Clear Log",
            [LocKey.BadgeReceiverReady] = "RECEIVER MODE (READY)  •  PIN {0}",
            [LocKey.BadgeSenderReady] = "CONTROLLER MODE (READY)  •  PIN {0}",
            [LocKey.BadgeReceiverActive] = "RECEIVER ACTIVE  •  PIN {0}",
            [LocKey.BadgeSenderActive] = "CONTROLLER ACTIVE  •  Edge {0}",
            [LocKey.BadgeControllingRemote] = "CONTROLLING REMOTE ({0})  •  [F12] Return",
            [LocKey.ThemeLight] = "Light",
            [LocKey.ThemeDark] = "Dark",
            [LocKey.ThemeSystem] = "System",
            [LocKey.LangPt] = "Portuguese",
            [LocKey.LangEn] = "English",
            [LocKey.LangEs] = "Spanish"
        },
        [AppLanguage.Spanish] = new()
        {
            [LocKey.AppTitle] = "KMShare — Centro de Conexión Multi-Dispositivo",
            [LocKey.HeaderSubtitle] = "LAN Física: {0}   •   Bluetooth Local: {1}   •   Cifrado: ECDH P-256 + AES-256-GCM (Zero-Admin)",
            [LocKey.CardRoleTitle] = "1. Modo de Operación de este Dispositivo (Seleccione en cada PC)",
            [LocKey.RoleSenderButton] = "Controlador (Principal) — Enviar Ratón y Teclado",
            [LocKey.RoleReceiverButton] = "Receptor (Secundario) — Recibir Control en este PC",
            [LocKey.PinLabel] = "PIN:",
            [LocKey.PhysicalNicLabel] = "Adaptador Físico:",
            [LocKey.RefreshButton] = "Actualizar",
            [LocKey.NewPinButton] = "Nuevo PIN",
            [LocKey.CopyDataButton] = "Copiar Mis Datos",
            [LocKey.AutostartCheckbox] = "Iniciar con Windows (User-Space)",
            [LocKey.CardScannerTitle] = "2. Dispositivos en Red Local (Beacon UDP + Capa 2 ARP) y Bluetooth",
            [LocKey.ScanPeersButton] = "Buscar Dispositivos en Red y Bluetooth",
            [LocKey.ColHost] = "Dispositivo / Hostname",
            [LocKey.ColIp] = "IP LAN Física",
            [LocKey.ColBtMac] = "MAC Bluetooth (Modo C)",
            [LocKey.ColArpMac] = "MAC Físico (ARP L2)",
            [LocKey.ColDiag] = "Diagnóstico / Modo Recomendado",
            [LocKey.ScanSummaryInitial] = "Haga clic en 'Buscar' para descubrir equipos por LAN / ARP L2 y Bluetooth.",
            [LocKey.CardTargetTitle] = "3A. Destino y Modo de Conexión",
            [LocKey.TargetIpLabel] = "IP o Hostname:",
            [LocKey.ProbeVpnButton] = "Diagnosticar Ruta / VPN",
            [LocKey.TargetBtLabel] = "MAC Bluetooth:",
            [LocKey.TargetBtNote] = "(Usado en Modo C Out-of-Band)",
            [LocKey.ConnModeLabel] = "Modo de Operación:",
            [LocKey.ModeAuto] = "Automático Inteligente (LAN Directa -> Sonda ARP -> Fallback)",
            [LocKey.ModeA] = "Modo A — LAN Física Directa (UDP :51820 | 0.8ms | 1000Hz | Sin Admin)",
            [LocKey.ModeB] = "Modo B — LAN con Step-Up Auth (Ruta /32 + Firewall WFP | 1000Hz)",
            [LocKey.ModeC] = "Modo C — Bluetooth Out-of-Band (RFCOMM Canal 5 | 133Hz | Ignora VPN)",
            [LocKey.CardEdgeTitle] = "3B. Borde del Monitor ({0} -> {1})",
            [LocKey.EdgeLeft] = "Izquierda",
            [LocKey.EdgeTop] = "Superior",
            [LocKey.EdgeBottom] = "Inferior",
            [LocKey.EdgeRight] = "Derecha",
            [LocKey.MonitorLocal] = "Este PC",
            [LocKey.MonitorRemote] = "PC Remoto",
            [LocKey.CardActionsTitle] = "",
            [LocKey.ConnectButton] = "Conectar y Compartir Ratón / Teclado",
            [LocKey.DisconnectButton] = "Desconectar y Liberar Ratón / Teclado",
            [LocKey.StartReceiverButton] = "Iniciar Modo Receptor en este PC",
            [LocKey.StopReceiverButton] = "Detener Modo Receptor (Desconectar)",
            [LocKey.ApplyModeBButton] = "Habilitar Ruta /32 (Modo B)",
            [LocKey.SendFileButton] = "Enviar Archivo Seguro...",
            [LocKey.SelfTestButton] = "Autotest",
            [LocKey.CardLogTitle] = "4. Consola de Eventos, Criptografía y Diagnóstico en Tiempo Real",
            [LocKey.ClearLogButton] = "Limpiar Log",
            [LocKey.BadgeReceiverReady] = "MODO RECEPTOR (LISTO)  •  PIN {0}",
            [LocKey.BadgeSenderReady] = "MODO CONTROLADOR (LISTO)  •  PIN {0}",
            [LocKey.BadgeReceiverActive] = "RECEPTOR ACTIVO  •  PIN {0}",
            [LocKey.BadgeSenderActive] = "CONTROLADOR ACTIVO  •  Borde {0}",
            [LocKey.BadgeControllingRemote] = "CONTROLANDO REMOTO ({0})  •  [F12] Volver",
            [LocKey.ThemeLight] = "Claro",
            [LocKey.ThemeDark] = "Oscuro",
            [LocKey.ThemeSystem] = "Sistema",
            [LocKey.LangPt] = "Portugués",
            [LocKey.LangEn] = "Inglés",
            [LocKey.LangEs] = "Español"
        }
    };

    public static AppLanguage ConfiguredLanguage => _configuredLanguage;
    public static AppLanguage ActiveLanguage => _activeResolvedLanguage;

    public static void SetLanguage(AppLanguage language)
    {
        _configuredLanguage = language;
        _activeResolvedLanguage = ResolveLanguage(language);
    }

    public static AppLanguage ResolveLanguage(AppLanguage language)
    {
        if (language != AppLanguage.Auto)
            return language;

        string iso = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        return iso switch
        {
            "pt" => AppLanguage.Portuguese,
            "es" => AppLanguage.Spanish,
            _ => AppLanguage.English
        };
    }

    public static string Get(LocKey key)
    {
        if (Catalogs.TryGetValue(_activeResolvedLanguage, out var dict) && dict.TryGetValue(key, out string? value))
            return value;

        if (Catalogs[AppLanguage.English].TryGetValue(key, out string? fallback))
            return fallback;

        return key.ToString();
    }

    public static string Format(LocKey key, params object[] args)
    {
        string pattern = Get(key);
        try
        {
            return string.Format(pattern, args);
        }
        catch
        {
            return pattern;
        }
    }

    public static string GetEdgeName(ScreenEdge edge)
    {
        return edge switch
        {
            ScreenEdge.Left => Get(LocKey.EdgeLeft),
            ScreenEdge.Top => Get(LocKey.EdgeTop),
            ScreenEdge.Bottom => Get(LocKey.EdgeBottom),
            ScreenEdge.Right => Get(LocKey.EdgeRight),
            _ => edge.ToString()
        };
    }
}
