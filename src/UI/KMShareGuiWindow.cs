using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using KMShare;
using MKShare.FileTransfer;
using MKShare.Network;
using MKShare.Localization;
using MKShare.Platform;
using MKShare.Security;

namespace MKShare.UI;

public static partial class KMShareGuiWindow
{
    public static async Task RunAsync(
        IPAddress initialLanIp,
        string localBtMac,
        Func<Task>? onRunDiagnostics,
        CancellationToken appCt)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await TrayAndEdgeManager.RunInteractiveMenuAsync(initialLanIp, localBtMac, onRunDiagnostics, appCt);
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = new Thread(() =>
        {
            try
            {
                using var window = new Win32ControlCenterWindow(initialLanIp, localBtMac, onRunDiagnostics, appCt);
                window.RunMessageLoop();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = false,
            Name = "KMShare-Win32GuiThread"
        };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        await tcs.Task;
    }

    private enum ButtonStyleVariant
    {
        PrimaryAction,
        DangerAction,
        DarkAccent,
        SecondaryOutline,
        SegmentToggle
    }

    private sealed class OwnerDrawButtonState
    {
        public required int Id { get; init; }
        public required IntPtr Hwnd { get; init; }
        public required string Label { get; set; }
        public required ButtonStyleVariant Variant { get; set; }
        public bool IsSelected { get; set; }
        public bool IsHovered { get; set; }
    }

    private sealed partial class Win32ControlCenterWindow : IDisposable
    {
        private const string WindowClassName = "KMShareModernFluentWndClass";

        private const uint WM_CREATE = 0x0001;
        private const uint WM_DESTROY = 0x0002;
        private const uint WM_SIZE = 0x0005;
        private const uint WM_PAINT = 0x000F;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_ERASEBKGND = 0x0014;
        private const uint WM_GETMINMAXINFO = 0x0024;
        private const uint WM_DRAWITEM = 0x002B;
        private const uint WM_SETFONT = 0x0030;
        private const uint WM_NOTIFY = 0x004E;
        private const uint WM_SETICON = 0x0080;
        private const uint WM_COMMAND = 0x0111;
        private const uint WM_CTLCOLOREDIT = 0x0133;
        private const uint WM_CTLCOLORLISTBOX = 0x0134;
        private const uint WM_CTLCOLORBTN = 0x0135;
        private const uint WM_CTLCOLORSTATIC = 0x0138;
        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_MOUSELEAVE = 0x02A3;
        private const uint WM_SETTINGCHANGE = 0x001A;
        private const uint WM_THEMECHANGED = 0x031A;
        private const uint WM_APP_UI_DISPATCH = 0x8001;
        private const uint WM_APP_TRAYICON = 0x8002;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CHILD = 0x40000000;
        private const int WS_TABSTOP = 0x00010000;
        private const int WS_BORDER = 0x00800000;
        private const int WS_VSCROLL = 0x00200000;

        private const int BS_AUTOCHECKBOX = 0x00000003;
        private const int BS_OWNERDRAW = 0x0000000B;

        private const int ES_AUTOHSCROLL = 0x0080;
        private const int ES_MULTILINE = 0x0004;
        private const int ES_AUTOVSCROLL = 0x0040;
        private const int ES_READONLY = 0x0800;
        private const int CBS_DROPDOWNLIST = 0x0003;

        private const int LVS_REPORT = 0x0001;
        private const int LVS_SINGLESEL = 0x0004;
        private const int LVS_SHOWSELALWAYS = 0x0008;
        private const uint LVM_SETEXTENDEDLISTVIEWSTYLE = 0x1036;
        private const uint LVM_INSERTCOLUMNW = 0x1061;
        private const uint LVM_INSERTITEMW = 0x104D;
        private const uint LVM_SETITEMW = 0x104C;
        private const uint LVM_DELETEALLITEMS = 0x1009;
        private const uint LVM_GETNEXTITEM = 0x100C;
        private const uint LVM_SETCOLUMNWIDTH = 0x101E;
        private const int LVNI_SELECTED = 0x0002;
        private const uint LVS_EX_FULLROWSELECT = 0x00000020;
        private const uint LVS_EX_GRIDLINES = 0x00000001;
        private const uint LVS_EX_DOUBLEBUFFER = 0x00010000;

        private const uint BM_GETCHECK = 0x00F0;
        private const uint BM_SETCHECK = 0x00F1;
        private const int BST_CHECKED = 1;
        private const int BST_UNCHECKED = 0;

        private const uint CB_ADDSTRING = 0x0143;
        private const uint CB_RESETCONTENT = 0x014B;
        private const uint CB_SETCURSEL = 0x014E;
        private const uint CB_GETCURSEL = 0x0147;
        private const uint EM_SETSEL = 0x00B1;
        private const uint EM_SCROLLCARET = 0x00B7;
        private const uint EM_SETMARGINS = 0x00D3;

        private const int CBN_SELCHANGE = 1;
        private const int LVN_ITEMCHANGED = -101;
        private const int NM_CLICK = -2;
        private const int NM_DBLCLK = -3;

        // Control IDs
        private const int IDC_BTN_ROLE_SENDER = 101;
        private const int IDC_BTN_ROLE_RECEIVER = 102;
        private const int IDC_CMB_LOCAL_NIC = 103;
        private const int IDC_BTN_REFRESH_NICS = 104;
        private const int IDC_TXT_PIN = 105;
        private const int IDC_BTN_NEW_PIN = 106;
        private const int IDC_BTN_COPY_MY_INFO = 107;
        private const int IDC_CHK_AUTOSTART = 108;

        private const int IDC_BTN_SCAN_PEERS = 201;
        private const int IDC_LV_PEERS = 202;

        private const int IDC_TXT_TARGET_IP = 301;
        private const int IDC_BTN_PROBE_VPN = 302;
        private const int IDC_TXT_TARGET_BT = 303;
        private const int IDC_CMB_CONN_MODE = 304;

        private const int IDC_BTN_EDGE_LEFT = 401;
        private const int IDC_BTN_EDGE_TOP = 402;
        private const int IDC_BTN_EDGE_BOTTOM = 403;
        private const int IDC_BTN_EDGE_RIGHT = 404;

        private const int IDC_BTN_START_STOP = 501;
        private const int IDC_BTN_APPLY_MODE_B = 502;
        private const int IDC_BTN_SEND_FILE = 503;
        private const int IDC_BTN_SELF_TEST = 504;
        private const int IDC_BTN_CLEAR_LOG = 601;

        private const int IDC_BTN_LANG_PT = 701;
        private const int IDC_BTN_LANG_EN = 702;
        private const int IDC_BTN_LANG_ES = 703;
        private const int IDC_BTN_THEME_LIGHT = 711;
        private const int IDC_BTN_THEME_DARK = 712;
        private const int IDC_BTN_THEME_SYSTEM = 713;

        // Modern Dynamic Color Palette
        private static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));
        private ThemePalette _currentPalette = ThemePalette.Dark;

        private uint ColAppBg => _currentPalette.ColAppBg;
        private uint ColCardBg => _currentPalette.ColCardBg;
        private uint ColCardBorder => _currentPalette.ColCardBorder;
        private uint ColHeaderBg => _currentPalette.ColHeaderBg;
        private uint ColTextPrimary => _currentPalette.ColTextPrimary;
        private uint ColTextSecondary => _currentPalette.ColTextSecondary;
        private uint ColTextMuted => _currentPalette.ColTextMuted;
        private uint ColPrimaryBlue => _currentPalette.ColPrimaryBlue;
        private uint ColPrimaryBlueHover => _currentPalette.ColPrimaryBlueHover;
        private uint ColPrimaryBlueDark => _currentPalette.ColPrimaryBlueDark;
        private uint ColDangerRed => _currentPalette.ColDangerRed;
        private uint ColDangerRedHover => _currentPalette.ColDangerRedHover;
        private uint ColDarkAccent => _currentPalette.ColDarkAccent;
        private uint ColDarkAccentHover => _currentPalette.ColDarkAccentHover;
        private uint ColSegmentActiveBg => _currentPalette.ColSegmentActiveBg;
        private uint ColSecondaryBg => _currentPalette.ColSecondaryBg;
        private uint ColSecondaryHover => _currentPalette.ColSecondaryHover;
        private uint ColLogBg => _currentPalette.ColLogBg;
        private uint ColInputBg => _currentPalette.ColInputBg;

        private readonly ConcurrentQueue<Action> _uiDispatchQueue = new();
        private readonly Dictionary<int, OwnerDrawButtonState> _buttonsById = [];
        private readonly Dictionary<IntPtr, SubclassProcDelegate> _buttonSubclasses = [];
        private readonly WndProcDelegate _wndProcDelegate;
        private readonly CancellationToken _appCt;
        private readonly CancellationTokenSource _discoveryResponderCts;
        private readonly Func<Task>? _onRunDiagnostics;
        private readonly TextWriter _originalConsoleOut;
        private readonly GuiLogTeeWriter _logTeeWriter;

        private IPAddress _localLanIp;
        private readonly string _localBtMac;
        private KMShareUserConfig _config;
        private List<NicInfo> _physicalNics = [];
        private List<DiscoveredNetworkPeer> _discoveredPeers = [];

        private CancellationTokenSource? _activeSessionCts;
        private bool _isSessionRunning;
        private bool _isRemoteControllingNow;
        private bool _isScanning;
        private string _scanSummaryText = "Clique em 'Buscar na Rede' para descobrir computadores via LAN / ARP L2 e Bluetooth.";

        private double _dpiScale = 1.0;
        private int _clientWidth = 1020;
        private int _clientHeight = 760;

        // Cached layout rectangles for custom card painting
        private RECT _rcHeader;
        private RECT _rcCardRole;
        private RECT _rcCardScanner;
        private RECT _rcCardConn;
        private RECT _rcCardEdge;
        private RECT _rcMonitorPreview;
        private RECT _rcCardActions;
        private RECT _rcCardLog;

        private IntPtr _hwndMain;
        private IntPtr _fontHeaderTitle;
        private IntPtr _fontHeaderSub;
        private IntPtr _fontSectionTitle;
        private IntPtr _fontLabelBold;
        private IntPtr _fontRegular;
        private IntPtr _fontSmall;
        private IntPtr _fontButtonBold;
        private IntPtr _fontMono;

        private IntPtr _brushCardBg;
        private IntPtr _brushEditBg;
        private IntPtr _brushLogBg;

        private IntPtr _btnRoleSender;
        private IntPtr _btnRoleReceiver;
        private IntPtr _cmbLocalNic;
        private IntPtr _btnRefreshNics;
        private IntPtr _txtPin;
        private IntPtr _btnNewPin;
        private IntPtr _btnCopyMyInfo;
        private IntPtr _chkAutostart;

        private IntPtr _btnScanPeers;
        private IntPtr _lvPeers;

        private IntPtr _txtTargetIp;
        private IntPtr _btnProbeVpn;
        private IntPtr _txtTargetBtMac;
        private IntPtr _cmbConnMode;

        private IntPtr _btnEdgeLeft;
        private IntPtr _btnEdgeTop;
        private IntPtr _btnEdgeBottom;
        private IntPtr _btnEdgeRight;

        private IntPtr _btnStartStop;
        private IntPtr _btnApplyModeB;
        private IntPtr _btnSendFile;
        private IntPtr _btnSelfTest;
        private IntPtr _btnClearLog;

        private IntPtr _btnLangPt;
        private IntPtr _btnLangEn;
        private IntPtr _btnLangEs;
        private IntPtr _btnThemeLight;
        private IntPtr _btnThemeDark;
        private IntPtr _btnThemeSystem;
        private IntPtr _txtLog;

        private NOTIFYICONDATAW _trayNid;
        private bool _trayAdded;
        private readonly StringBuilder _logBuffer = new();

        public Win32ControlCenterWindow(
            IPAddress initialLanIp,
            string localBtMac,
            Func<Task>? onRunDiagnostics,
            CancellationToken appCt)
        {
            _localLanIp = initialLanIp;
            _localBtMac = localBtMac;
            _onRunDiagnostics = onRunDiagnostics;
            _appCt = appCt;
            _config = TrayAndEdgeManager.LoadOrCreateConfig();
            LocalizationManager.SetLanguage(_config.Language);
            _currentPalette = ThemePalette.Resolve(_config.Theme);
            _wndProcDelegate = WndProc;
            _discoveryResponderCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);

            _originalConsoleOut = Console.Out;
            _logTeeWriter = new GuiLogTeeWriter(_originalConsoleOut, AppendLogLineThreadSafe);
            Console.SetOut(_logTeeWriter);

            MKSharePeerNode.CustomVpnFallbackPrompt = PromptVpnFallbackDialog;
            MKSharePeerNode.CustomBluetoothTargetSelector = GetSelectedBluetoothTarget;
        }

        private int S(int px) => (int)Math.Round(px * _dpiScale);

        public void RunMessageLoop()
        {
            try { SetProcessDPIAware(); } catch { }

            IntPtr hdcScreen = GetDC(IntPtr.Zero);
            if (hdcScreen != IntPtr.Zero)
            {
                int dpiX = GetDeviceCaps(hdcScreen, 88 /* LOGPIXELSX */);
                _ = ReleaseDC(IntPtr.Zero, hdcScreen);
                if (dpiX >= 96)
                {
                    _dpiScale = Math.Clamp(dpiX / 96.0, 1.0, 2.25);
                }
            }

            var icc = new INITCOMMONCONTROLSEX
            {
                dwSize = Marshal.SizeOf<INITCOMMONCONTROLSEX>(),
                dwICC = 0x000040FF
            };
            InitCommonControlsEx(ref icc);

            IntPtr hInstance = GetModuleHandleW(null);
            IntPtr hAppIcon = LoadIconW(hInstance, 1);
            if (hAppIcon == IntPtr.Zero)
            {
                hAppIcon = LoadIconW(IntPtr.Zero, 32512);
            }
            IntPtr classNamePtr = Marshal.StringToHGlobalUni(WindowClassName);
            try
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    style = 0x0003,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                    hInstance = hInstance,
                    hIcon = hAppIcon,
                    hCursor = LoadCursorW(IntPtr.Zero, 32512),
                    hbrBackground = IntPtr.Zero,
                    lpszClassName = classNamePtr
                };
                RegisterClassExW(ref wc);
            }
            finally
            {
                Marshal.FreeHGlobal(classNamePtr);
            }

            int screenW = GetSystemMetrics(0);
            int screenH = GetSystemMetrics(1);
            int winW = Math.Min(S(1040), Math.Max(S(900), screenW - S(80)));
            int winH = Math.Min(S(790), Math.Max(S(700), screenH - S(80)));
            int posX = Math.Max(16, (screenW - winW) / 2);
            int posY = Math.Max(16, (screenH - winH) / 2);

            _hwndMain = CreateWindowExW(
                0,
                WindowClassName,
                $"{LocalizationManager.Get(LocKey.AppTitle)} ({Environment.MachineName})",
                WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
                posX, posY, winW, winH,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            SendMessageW(_hwndMain, WM_SETICON, 1 /* ICON_BIG */, hAppIcon);
            SendMessageW(_hwndMain, WM_SETICON, 0 /* ICON_SMALL */, hAppIcon);

            int isDarkInit = _currentPalette.IsDarkTitleBar ? 1 : 0;
            _ = DwmSetWindowAttribute(_hwndMain, DWMWA_USE_IMMERSIVE_DARK_MODE, ref isDarkInit, sizeof(int));

            if (_hwndMain == IntPtr.Zero)
                return;

            if (GetClientRect(_hwndMain, out RECT rcInit))
            {
                _clientWidth = rcInit.right - rcInit.left;
                _clientHeight = rcInit.bottom - rcInit.top;
                LayoutControls(_clientWidth, _clientHeight);
            }

            ShowWindow(_hwndMain, 5);
            UpdateWindow(_hwndMain);
            SetForegroundWindow(_hwndMain);

            _ = Task.Run(() => NetworkPeerDiscovery.RunBackgroundDiscoveryResponderAsync(
                () => _localLanIp,
                () => _localBtMac,
                () => _isSessionRunning
                    ? (_config.DeviceRole == DeviceOperationRole.ReceiverControlled ? "Receptor Ativo" : "Controlador Ativo")
                    : (_config.DeviceRole == DeviceOperationRole.ReceiverControlled ? "Receptor (Pronto)" : "Controlador (Pronto)"),
                () => _config.MonitorEdge,
                _discoveryResponderCts.Token));

            IHidPlatform hid = HidPlatformFactory.GetCurrent();
            void remoteStateSubscriber(bool isRemote)
            {
                InvokeOnUi(() =>
                {
                    _isRemoteControllingNow = isRemote;
                    InvalidateRect(_hwndMain, IntPtr.Zero, false);
                });
            }
            hid.OnRemoteStateChanged += remoteStateSubscriber;

            using var ctReg = _appCt.Register(() => InvokeOnUi(() => DestroyWindow(_hwndMain)));

            if (!System.Diagnostics.Debugger.IsAttached)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(300);
                    InvokeOnUi(StartNetworkScan);
                });
            }

            try
            {
                while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
            }
            finally
            {
                hid.OnRemoteStateChanged -= remoteStateSubscriber;
                StopActiveSession();
            }
        }

        private void BuildChildControls(IntPtr hWnd)
        {
            _fontHeaderTitle = CreateCleanFont(S(18), 600, "Segoe UI Semibold");
            _fontHeaderSub = CreateCleanFont(S(12), 400, "Segoe UI");
            _fontSectionTitle = CreateCleanFont(S(13), 600, "Segoe UI Semibold");
            _fontLabelBold = CreateCleanFont(S(12), 600, "Segoe UI Semibold");
            _fontRegular = CreateCleanFont(S(12), 400, "Segoe UI");
            _fontSmall = CreateCleanFont(S(11), 400, "Segoe UI");
            _fontButtonBold = CreateCleanFont(S(12), 600, "Segoe UI Semibold");
            _fontMono = CreateCleanFont(S(12), 400, "Consolas");

            _brushCardBg = CreateSolidBrush(ColCardBg);
            _brushEditBg = CreateSolidBrush(ColInputBg);
            _brushLogBg = CreateSolidBrush(ColLogBg);

            // Header Controls: Language & Theme
            _btnLangPt = CreateOwnerDrawButton(hWnd, "PT", IDC_BTN_LANG_PT, ButtonStyleVariant.SegmentToggle);
            _btnLangEn = CreateOwnerDrawButton(hWnd, "EN", IDC_BTN_LANG_EN, ButtonStyleVariant.SegmentToggle);
            _btnLangEs = CreateOwnerDrawButton(hWnd, "ES", IDC_BTN_LANG_ES, ButtonStyleVariant.SegmentToggle);

            _btnThemeLight = CreateOwnerDrawButton(hWnd, "☀️ " + LocalizationManager.Get(LocKey.ThemeLight), IDC_BTN_THEME_LIGHT, ButtonStyleVariant.SegmentToggle);
            _btnThemeDark = CreateOwnerDrawButton(hWnd, "🌙 " + LocalizationManager.Get(LocKey.ThemeDark), IDC_BTN_THEME_DARK, ButtonStyleVariant.SegmentToggle);
            _btnThemeSystem = CreateOwnerDrawButton(hWnd, "💻 " + LocalizationManager.Get(LocKey.ThemeSystem), IDC_BTN_THEME_SYSTEM, ButtonStyleVariant.SegmentToggle);

            // Card 1 Controls: Role Segmented Buttons, NIC Combo, PIN, Autostart
            _btnRoleSender = CreateOwnerDrawButton(hWnd,
                LocalizationManager.Get(LocKey.RoleSenderButton),
                IDC_BTN_ROLE_SENDER, ButtonStyleVariant.SegmentToggle);
            _btnRoleReceiver = CreateOwnerDrawButton(hWnd,
                LocalizationManager.Get(LocKey.RoleReceiverButton),
                IDC_BTN_ROLE_RECEIVER, ButtonStyleVariant.SegmentToggle);

            _cmbLocalNic = CreateControl(hWnd, "COMBOBOX", "",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | CBS_DROPDOWNLIST | WS_VSCROLL,
                IDC_CMB_LOCAL_NIC, _fontRegular);
            _btnRefreshNics = CreateOwnerDrawButton(hWnd, LocalizationManager.Get(LocKey.RefreshButton), IDC_BTN_REFRESH_NICS, ButtonStyleVariant.SecondaryOutline);

            _txtPin = CreateEditControl(hWnd, _config.PairingPin, IDC_TXT_PIN, _fontLabelBold);
            _btnNewPin = CreateOwnerDrawButton(hWnd, LocalizationManager.Get(LocKey.NewPinButton), IDC_BTN_NEW_PIN, ButtonStyleVariant.SecondaryOutline);
            _btnCopyMyInfo = CreateOwnerDrawButton(hWnd, LocalizationManager.Get(LocKey.CopyDataButton), IDC_BTN_COPY_MY_INFO, ButtonStyleVariant.SecondaryOutline);

            _chkAutostart = CreateControl(hWnd, "BUTTON",
                LocalizationManager.Get(LocKey.AutostartCheckbox),
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                IDC_CHK_AUTOSTART, _fontRegular);

            // Card 2 Controls: Network & Bluetooth Scanner
            _btnScanPeers = CreateOwnerDrawButton(hWnd, LocalizationManager.Get(LocKey.ScanPeersButton), IDC_BTN_SCAN_PEERS, ButtonStyleVariant.DarkAccent);

            _lvPeers = CreateControl(hWnd, "SysListView32", "",
                WS_CHILD | WS_VISIBLE | WS_BORDER | WS_TABSTOP | LVS_REPORT | LVS_SINGLESEL | LVS_SHOWSELALWAYS,
                IDC_LV_PEERS, _fontRegular);

            uint exStyles = LVS_EX_FULLROWSELECT | LVS_EX_GRIDLINES | LVS_EX_DOUBLEBUFFER;
            SendMessageW(_lvPeers, LVM_SETEXTENDEDLISTVIEWSTYLE, (IntPtr)exStyles, (IntPtr)exStyles);

            InsertListViewColumn(_lvPeers, 0, LocalizationManager.Get(LocKey.ColHost), S(190));
            InsertListViewColumn(_lvPeers, 1, LocalizationManager.Get(LocKey.ColIp), S(125));
            InsertListViewColumn(_lvPeers, 2, LocalizationManager.Get(LocKey.ColBtMac), S(150));
            InsertListViewColumn(_lvPeers, 3, LocalizationManager.Get(LocKey.ColArpMac), S(140));
            InsertListViewColumn(_lvPeers, 4, LocalizationManager.Get(LocKey.ColDiag), S(340));

            // Card 3A Controls: Target Endpoints & Connection Mode
            _txtTargetIp = CreateEditControl(hWnd, _config.LastTargetIp, IDC_TXT_TARGET_IP, _fontRegular);
            _btnProbeVpn = CreateOwnerDrawButton(hWnd, LocalizationManager.Get(LocKey.ProbeVpnButton), IDC_BTN_PROBE_VPN, ButtonStyleVariant.SecondaryOutline);
            _txtTargetBtMac = CreateEditControl(hWnd, _config.LastTargetBtMac, IDC_TXT_TARGET_BT, _fontRegular);

            _cmbConnMode = CreateControl(hWnd, "COMBOBOX", "",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | CBS_DROPDOWNLIST | WS_VSCROLL,
                IDC_CMB_CONN_MODE, _fontRegular);

            PopulateConnModesCombo();

            // Card 3B Controls: Edge Segmented Pills
            _btnEdgeLeft = CreateOwnerDrawButton(hWnd, LocalizationManager.Get(LocKey.EdgeLeft), IDC_BTN_EDGE_LEFT, ButtonStyleVariant.SegmentToggle);
            _btnEdgeTop = CreateOwnerDrawButton(hWnd, LocalizationManager.Get(LocKey.EdgeTop), IDC_BTN_EDGE_TOP, ButtonStyleVariant.SegmentToggle);
            _btnEdgeBottom = CreateOwnerDrawButton(hWnd, LocalizationManager.Get(LocKey.EdgeBottom), IDC_BTN_EDGE_BOTTOM, ButtonStyleVariant.SegmentToggle);
            _btnEdgeRight = CreateOwnerDrawButton(hWnd, LocalizationManager.Get(LocKey.EdgeRight), IDC_BTN_EDGE_RIGHT, ButtonStyleVariant.SegmentToggle);

            // Card 4 Controls: Session Action Bar
            _btnStartStop = CreateOwnerDrawButton(hWnd, LocalizationManager.Get(LocKey.ConnectButton), IDC_BTN_START_STOP, ButtonStyleVariant.PrimaryAction);
            _btnApplyModeB = CreateOwnerDrawButton(hWnd, LocalizationManager.Get(LocKey.ApplyModeBButton), IDC_BTN_APPLY_MODE_B, ButtonStyleVariant.SecondaryOutline);
            _btnSendFile = CreateOwnerDrawButton(hWnd, LocalizationManager.Get(LocKey.SendFileButton), IDC_BTN_SEND_FILE, ButtonStyleVariant.SecondaryOutline);
            _btnSelfTest = CreateOwnerDrawButton(hWnd, LocalizationManager.Get(LocKey.SelfTestButton), IDC_BTN_SELF_TEST, ButtonStyleVariant.SecondaryOutline);

            // Card 5 Controls: Event Console
            _btnClearLog = CreateOwnerDrawButton(hWnd, LocalizationManager.Get(LocKey.ClearLogButton), IDC_BTN_CLEAR_LOG, ButtonStyleVariant.SecondaryOutline);
            _txtLog = CreateControl(hWnd, "EDIT", "",
                WS_CHILD | WS_VISIBLE | WS_BORDER | WS_VSCROLL | ES_MULTILINE | ES_AUTOVSCROLL | ES_READONLY,
                0, _fontMono);
            SendMessageW(_txtLog, EM_SETMARGINS, 3, (S(6) << 16) | S(6));

            // Initialize state
            RefreshPhysicalNicsCombo();
            SendMessageW(_chkAutostart, BM_SETCHECK, _config.RunAtUserLogin ? BST_CHECKED : BST_UNCHECKED, IntPtr.Zero);

            int modeIdx = _config.PreferredVpnMode switch
            {
                VpnFallbackStrategy.ModeA_ZeroAdminDiagnostic => 1,
                VpnFallbackStrategy.ModeB_StepUpWfpLanPermit => 2,
                VpnFallbackStrategy.ModeC_BluetoothOutOfBand => 3,
                _ => 0
            };
            SendMessageW(_cmbConnMode, CB_SETCURSEL, modeIdx, IntPtr.Zero);

            SyncVisualStates();
            InitSystemTrayIcon(hWnd);

            if (GetClientRect(hWnd, out RECT rc))
            {
                _clientWidth = rc.right - rc.left;
                _clientHeight = rc.bottom - rc.top;
                LayoutControls(_clientWidth, _clientHeight);
            }

            AppendLogLine($"[KMShare] Interface inicializada em User-Space (Sem Admin) — Máquina: {Environment.MachineName} | LAN: {_localLanIp} | BT: {_localBtMac}");
            AppendLogLine("[Guia Rápido] No PC secundário, selecione 'Receptor (Secundário)' e clique em 'Iniciar Modo Receptor'. No PC principal, selecione 'Controlador', escolha o dispositivo na tabela e clique em 'Conectar'.");
        }

        private void LayoutControls(int width, int height)
        {
            if (_hwndMain == IntPtr.Zero || width <= 100 || height <= 100)
                return;

            int margin = S(14);
            int gap = S(10);
            int contentW = Math.Max(S(820), width - margin * 2);

            int headerH = S(68);
            _rcHeader = new RECT { left = 0, top = 0, right = width, bottom = headerH };

            int y = headerH + S(8);

            // Card 1: Device Role & Local Identity
            int cardRoleH = S(98);
            _rcCardRole = new RECT { left = margin, top = y, right = margin + contentW, bottom = y + cardRoleH };
            y += cardRoleH + gap;

            // Card 3A & 3B height, Card 4 height, Card 5 min height -> distribute remaining height between Scanner and Log
            int card3H = S(124);
            int card4H = S(54);
            int fixedTotal = y + card3H + gap + card4H + gap + margin + gap * 2;
            int flexSpace = Math.Max(S(240), height - fixedTotal);

            int cardScannerH = Math.Max(S(155), (int)(flexSpace * 0.56));
            int cardLogH = Math.Max(S(95), flexSpace - cardScannerH);

            _rcCardScanner = new RECT { left = margin, top = y, right = margin + contentW, bottom = y + cardScannerH };
            y += cardScannerH + gap;

            int card3RightW = Math.Clamp((int)(contentW * 0.37), S(320), S(420));
            int card3LeftW = contentW - card3RightW - gap;

            _rcCardConn = new RECT { left = margin, top = y, right = margin + card3LeftW, bottom = y + card3H };
            _rcCardEdge = new RECT { left = margin + card3LeftW + gap, top = y, right = margin + contentW, bottom = y + card3H };
            y += card3H + gap;

            _rcCardActions = new RECT { left = margin, top = y, right = margin + contentW, bottom = y + card4H };
            y += card4H + gap;

            _rcCardLog = new RECT { left = margin, top = y, right = margin + contentW, bottom = Math.Max(y + cardLogH, height - margin) };

            IntPtr hdwp = BeginDeferWindowPos(30);
            int pad = S(14);

            // --- Position Header Controls (top right) ---
            int hdrRight = width - pad;
            int btnY = _rcHeader.top + S(36);
            int langW = S(32);
            int themeW = S(58);
            int btnH = S(24);
            int sGap = S(3);

            int tX = hdrRight - (themeW * 3 + sGap * 2);
            DeferControl(ref hdwp, _btnThemeLight, tX, btnY, themeW, btnH);
            DeferControl(ref hdwp, _btnThemeDark, tX + themeW + sGap, btnY, themeW, btnH);
            DeferControl(ref hdwp, _btnThemeSystem, tX + (themeW + sGap) * 2, btnY, themeW, btnH);

            int lX = tX - S(10) - (langW * 3 + sGap * 2);
            DeferControl(ref hdwp, _btnLangPt, lX, btnY, langW, btnH);
            DeferControl(ref hdwp, _btnLangEn, lX + langW + sGap, btnY, langW, btnH);
            DeferControl(ref hdwp, _btnLangEs, lX + (langW + sGap) * 2, btnY, langW, btnH);

            // --- Position Card 1 Controls ---
            int r1Y = _rcCardRole.top + S(30);
            int roleAreaW = contentW - pad * 2 - S(340);
            int singleRoleBtnW = (roleAreaW - S(8)) / 2;
            DeferControl(ref hdwp, _btnRoleSender, _rcCardRole.left + pad, r1Y, singleRoleBtnW, S(28));
            DeferControl(ref hdwp, _btnRoleReceiver, _rcCardRole.left + pad + singleRoleBtnW + S(8), r1Y, singleRoleBtnW, S(28));

            int pinGroupX = _rcCardRole.right - pad - S(322);
            DeferControl(ref hdwp, _txtPin, pinGroupX + S(42), r1Y + S(1), S(68), S(26));
            DeferControl(ref hdwp, _btnNewPin, pinGroupX + S(116), r1Y, S(74), S(28));
            DeferControl(ref hdwp, _btnCopyMyInfo, pinGroupX + S(196), r1Y, S(126), S(28));

            int r2Y = _rcCardRole.top + S(64);
            int nicComboX = _rcCardRole.left + pad + S(92);
            int nicComboW = Math.Max(S(240), roleAreaW - S(182));
            DeferControl(ref hdwp, _cmbLocalNic, nicComboX, r2Y, nicComboW, S(220));
            DeferControl(ref hdwp, _btnRefreshNics, nicComboX + nicComboW + S(8), r2Y - S(1), S(82), S(26));
            DeferControl(ref hdwp, _chkAutostart, pinGroupX, r2Y + S(2), S(320), S(22));

            // --- Position Card 2 Controls (Scanner) ---
            int scanTopY = _rcCardScanner.top + S(28);
            DeferControl(ref hdwp, _btnScanPeers, _rcCardScanner.left + pad, scanTopY, S(245), S(28));

            int lvTop = scanTopY + S(34);
            int lvW = contentW - pad * 2;
            int lvH = Math.Max(S(70), _rcCardScanner.bottom - lvTop - S(10));
            DeferControl(ref hdwp, _lvPeers, _rcCardScanner.left + pad, lvTop, lvW, lvH);

            // --- Position Card 3A Controls (Target & Mode) ---
            int c3LabelW = S(116);
            int c3InputX = _rcCardConn.left + pad + c3LabelW;
            int c3AvailW = (_rcCardConn.right - pad) - c3InputX;
            int probeBtnW = S(156);
            int ipBoxW = Math.Max(S(140), c3AvailW - probeBtnW - S(8));

            int rowIpY = _rcCardConn.top + S(30);
            DeferControl(ref hdwp, _txtTargetIp, c3InputX, rowIpY + S(1), ipBoxW, S(25));
            DeferControl(ref hdwp, _btnProbeVpn, c3InputX + ipBoxW + S(8), rowIpY, probeBtnW, S(27));

            int rowBtY = _rcCardConn.top + S(61);
            DeferControl(ref hdwp, _txtTargetBtMac, c3InputX, rowBtY + S(1), ipBoxW, S(25));

            int rowModeY = _rcCardConn.top + S(92);
            DeferControl(ref hdwp, _cmbConnMode, c3InputX, rowModeY, c3AvailW, S(200));

            // --- Position Card 3B Controls (Monitor Preview + Edge Pills) ---
            int edgeCardInnerW = (_rcCardEdge.right - _rcCardEdge.left) - pad * 2;
            _rcMonitorPreview = new RECT
            {
                left = _rcCardEdge.left + pad,
                top = _rcCardEdge.top + S(28),
                right = _rcCardEdge.right - pad,
                bottom = _rcCardEdge.top + S(82)
            };

            int pillY = _rcCardEdge.top + S(88);
            int pillGap = S(6);
            int pillW = (edgeCardInnerW - pillGap * 3) / 4;
            DeferControl(ref hdwp, _btnEdgeLeft, _rcCardEdge.left + pad, pillY, pillW, S(26));
            DeferControl(ref hdwp, _btnEdgeTop, _rcCardEdge.left + pad + (pillW + pillGap), pillY, pillW, S(26));
            DeferControl(ref hdwp, _btnEdgeBottom, _rcCardEdge.left + pad + (pillW + pillGap) * 2, pillY, pillW, S(26));
            DeferControl(ref hdwp, _btnEdgeRight, _rcCardEdge.left + pad + (pillW + pillGap) * 3, pillY, pillW, S(26));

            // --- Position Card 4 Controls (Action Bar) ---
            int actY = _rcCardActions.top + S(11);
            int actH = S(32);
            int btnSelfW = S(105);
            int btnSendW = S(168);
            int btnModeBW = S(178);
            int rightBtnsTotal = btnSelfW + btnSendW + btnModeBW + S(8) * 3;
            int startBtnW = Math.Max(S(260), (contentW - pad * 2) - rightBtnsTotal);

            int curX = _rcCardActions.left + pad;
            DeferControl(ref hdwp, _btnStartStop, curX, actY, startBtnW, actH);
            curX += startBtnW + S(8);
            DeferControl(ref hdwp, _btnApplyModeB, curX, actY, btnModeBW, actH);
            curX += btnModeBW + S(8);
            DeferControl(ref hdwp, _btnSendFile, curX, actY, btnSendW, actH);
            curX += btnSendW + S(8);
            DeferControl(ref hdwp, _btnSelfTest, curX, actY, btnSelfW, actH);

            // --- Position Card 5 Controls (Event Log) ---
            DeferControl(ref hdwp, _btnClearLog, _rcCardLog.right - pad - S(86), _rcCardLog.top + S(6), S(86), S(22));
            int logTop = _rcCardLog.top + S(30);
            int logH = Math.Max(S(50), _rcCardLog.bottom - logTop - S(10));
            DeferControl(ref hdwp, _txtLog, _rcCardLog.left + pad, logTop, contentW - pad * 2, logH);

            EndDeferWindowPos(hdwp);

            // Proportionally adjust ListView columns
            int col0 = S(185);
            int col1 = S(120);
            int col2 = S(145);
            int col3 = S(135);
            int col4 = Math.Max(S(240), lvW - (col0 + col1 + col2 + col3 + S(22)));
            SendMessageW(_lvPeers, LVM_SETCOLUMNWIDTH, 0, col0);
            SendMessageW(_lvPeers, LVM_SETCOLUMNWIDTH, 1, col1);
            SendMessageW(_lvPeers, LVM_SETCOLUMNWIDTH, 2, col2);
            SendMessageW(_lvPeers, LVM_SETCOLUMNWIDTH, 3, col3);
            SendMessageW(_lvPeers, LVM_SETCOLUMNWIDTH, 4, col4);

            InvalidateRect(_hwndMain, IntPtr.Zero, false);
        }

        private static void DeferControl(ref IntPtr hdwp, IntPtr hCtrl, int x, int y, int w, int h)
        {
            if (hCtrl == IntPtr.Zero)
                return;

            const uint flags = 0x0004 | 0x0010 | 0x0100; // SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOCOPYBITS
            if (hdwp != IntPtr.Zero)
            {
                IntPtr next = DeferWindowPos(hdwp, hCtrl, IntPtr.Zero, x, y, w, h, flags);
                if (next != IntPtr.Zero)
                {
                    hdwp = next;
                    return;
                }
                hdwp = IntPtr.Zero;
            }

            SetWindowPos(hCtrl, IntPtr.Zero, x, y, w, h, flags);
        }

        private void PaintWindow(IntPtr hWnd)
        {
            IntPtr hdc = BeginPaint(hWnd, out PAINTSTRUCT ps);
            if (hdc == IntPtr.Zero) return;

            int w = Math.Max(1, _clientWidth);
            int h = Math.Max(1, _clientHeight);

            IntPtr memDc = CreateCompatibleDC(hdc);
            IntPtr memBmp = CreateCompatibleBitmap(hdc, w, h);
            IntPtr oldBmp = SelectObject(memDc, memBmp);

            try
            {
                _ = SetBkMode(memDc, 1 /* TRANSPARENT */);

                // 1. Fill main slate background
                FillSolidRect(memDc, new RECT { left = 0, top = 0, right = w, bottom = h }, ColAppBg);

                // 2. Top Header Banner
                FillSolidRect(memDc, _rcHeader, ColHeaderBg);
                DrawTextLine(memDc, _fontHeaderTitle, _currentPalette.ColHeaderTitle,
                    $"{LocalizationManager.Get(LocKey.AppTitle)} ({Environment.MachineName})",
                    S(16), S(10));
                DrawTextLine(memDc, _fontHeaderSub, _currentPalette.ColHeaderSub,
                    LocalizationManager.Format(LocKey.HeaderSubtitle, _localLanIp, _localBtMac),
                    S(16), S(38));

                // Status Pill in top-right of Header
                DrawLiveStatusBadge(memDc);

                // 3. Card 1: Role & Identity
                DrawModernCard(memDc, _rcCardRole, LocalizationManager.Get(LocKey.CardRoleTitle));
                int pinGroupX = _rcCardRole.right - S(14) - S(322);
                DrawTextLine(memDc, _fontLabelBold, ColTextSecondary, LocalizationManager.Get(LocKey.PinLabel), pinGroupX, _rcCardRole.top + S(35));
                DrawTextLine(memDc, _fontLabelBold, ColTextSecondary, LocalizationManager.Get(LocKey.PhysicalNicLabel), _rcCardRole.left + S(14), _rcCardRole.top + S(68));

                // 4. Card 2: Network & Bluetooth Scanner
                DrawModernCard(memDc, _rcCardScanner, LocalizationManager.Get(LocKey.CardScannerTitle));
                DrawTextLine(memDc, _fontSmall, ColTextMuted, _scanSummaryText,
                    _rcCardScanner.left + S(270), _rcCardScanner.top + S(35));

                // 5. Card 3A: Target & Connection Mode
                DrawModernCard(memDc, _rcCardConn, LocalizationManager.Get(LocKey.CardTargetTitle));
                int c3LblX = _rcCardConn.left + S(14);
                DrawTextLine(memDc, _fontLabelBold, ColTextSecondary, LocalizationManager.Get(LocKey.TargetIpLabel), c3LblX, _rcCardConn.top + S(35));
                DrawTextLine(memDc, _fontLabelBold, ColTextSecondary, LocalizationManager.Get(LocKey.TargetBtLabel), c3LblX, _rcCardConn.top + S(66));
                DrawTextLine(memDc, _fontSmall, ColTextMuted, LocalizationManager.Get(LocKey.TargetBtNote),
                    _rcCardConn.right - S(14) - S(156), _rcCardConn.top + S(67));
                DrawTextLine(memDc, _fontLabelBold, ColTextSecondary, LocalizationManager.Get(LocKey.ConnModeLabel), c3LblX, _rcCardConn.top + S(96));

                // 6. Card 3B: Visual Monitor Preview & Edge Selector
                ScreenEdge opposite = EdgeCoordinateMapper.GetOppositeEdge(_config.MonitorEdge);
                DrawModernCard(memDc, _rcCardEdge, LocalizationManager.Format(LocKey.CardEdgeTitle, LocalizationManager.GetEdgeName(_config.MonitorEdge), LocalizationManager.GetEdgeName(opposite)));
                DrawMonitorTopologyPreview(memDc, _rcMonitorPreview, _config.MonitorEdge);

                // 7. Card 4: Action Bar
                DrawModernCard(memDc, _rcCardActions, "");

                // 8. Card 5: Real-Time Event Log
                DrawModernCard(memDc, _rcCardLog, LocalizationManager.Get(LocKey.CardLogTitle));

                BitBlt(hdc, 0, 0, w, h, memDc, 0, 0, 0x00CC0020 /* SRCCOPY */);
            }
            finally
            {
                SelectObject(memDc, oldBmp);
                DeleteObject(memBmp);
                DeleteDC(memDc);
                EndPaint(hWnd, ref ps);
            }
        }

        private void DrawLiveStatusBadge(IntPtr hdc)
        {
            string badgeText;
            uint badgeBg;
            uint badgeBorder;

            if (_isSessionRunning)
            {
                if (_config.DeviceRole == DeviceOperationRole.ReceiverControlled)
                {
                    badgeText = LocalizationManager.Format(LocKey.BadgeReceiverActive, _config.PairingPin);
                    badgeBg = Rgb(3, 105, 161);     // Sky 700
                    badgeBorder = Rgb(56, 189, 248);
                }
                else if (_isRemoteControllingNow)
                {
                    badgeText = LocalizationManager.Format(LocKey.BadgeControllingRemote, LocalizationManager.GetEdgeName(_config.MonitorEdge));
                    badgeBg = Rgb(180, 83, 9);      // Amber 700
                    badgeBorder = Rgb(251, 191, 36);
                }
                else
                {
                    badgeText = LocalizationManager.Format(LocKey.BadgeSenderActive, LocalizationManager.GetEdgeName(_config.MonitorEdge));
                    badgeBg = Rgb(21, 128, 61);     // Green 700
                    badgeBorder = Rgb(74, 222, 128);
                }
            }
            else
            {
                badgeText = _config.DeviceRole == DeviceOperationRole.ReceiverControlled
                    ? LocalizationManager.Format(LocKey.BadgeReceiverReady, _config.PairingPin)
                    : LocalizationManager.Format(LocKey.BadgeSenderReady, _config.PairingPin);
                badgeBg = _currentPalette.ColDarkAccent;
                badgeBorder = _currentPalette.ColCardBorder;
            }

            int pillW = S(290);
            int pillH = S(24);
            var rcPill = new RECT
            {
                left = _rcHeader.right - S(14) - pillW,
                top = _rcHeader.top + S(7),
                right = _rcHeader.right - S(14),
                bottom = _rcHeader.top + S(7) + pillH
            };

            DrawRoundedBox(hdc, rcPill, S(15), badgeBg, badgeBorder, 1);
            IntPtr oldFont = SelectObject(hdc, _fontLabelBold);
            _ = SetTextColor(hdc, Rgb(255, 255, 255));
            _ = DrawTextW(hdc, badgeText, -1, ref rcPill, 0x01 | 0x04 | 0x20 /* DT_CENTER | DT_VCENTER | DT_SINGLELINE */);
            SelectObject(hdc, oldFont);
        }

        private void DrawModernCard(IntPtr hdc, RECT rc, string title)
        {
            DrawRoundedBox(hdc, rc, S(8), ColCardBg, ColCardBorder, 1);
            if (!string.IsNullOrEmpty(title))
            {
                DrawTextLine(hdc, _fontSectionTitle, ColTextPrimary, title, rc.left + S(14), rc.top + S(7));
            }
        }

        private void DrawMonitorTopologyPreview(IntPtr hdc, RECT area, ScreenEdge edge)
        {
            DrawRoundedBox(hdc, area, S(6), _currentPalette.ColSecondaryBg, _currentPalette.ColCardBorder, 1);

            int cx = (area.left + area.right) / 2;
            int cy = (area.top + area.bottom) / 2;
            int monW = S(68);
            int monH = S(34);

            var rcLocal = new RECT
            {
                left = cx - monW / 2,
                top = cy - monH / 2,
                right = cx + monW / 2,
                bottom = cy + monH / 2
            };

            int offset = S(6);
            RECT rcRemote = edge switch
            {
                ScreenEdge.Left => new RECT { left = rcLocal.left - monW - offset, top = rcLocal.top, right = rcLocal.left - offset, bottom = rcLocal.bottom },
                ScreenEdge.Right => new RECT { left = rcLocal.right + offset, top = rcLocal.top, right = rcLocal.right + offset + monW, bottom = rcLocal.bottom },
                ScreenEdge.Top => new RECT { left = rcLocal.left + monW + offset, top = area.top + S(4), right = rcLocal.left + monW * 2 + offset, bottom = area.top + S(4) + monH },
                ScreenEdge.Bottom => new RECT { left = rcLocal.left + monW + offset, top = area.bottom - S(4) - monH, right = rcLocal.left + monW * 2 + offset, bottom = area.bottom - S(4) },
                _ => new RECT { left = rcLocal.right + offset, top = rcLocal.top, right = rcLocal.right + offset + monW, bottom = rcLocal.bottom }
            };

            DrawRoundedBox(hdc, rcLocal, S(4), _currentPalette.ColPreviewLocalBg, ColPrimaryBlue, 2);
            IntPtr oldFont = SelectObject(hdc, _fontSmall);
            _ = SetTextColor(hdc, _currentPalette.IsDarkTitleBar ? Rgb(255, 255, 255) : ColTextPrimary);
            _ = DrawTextW(hdc, LocalizationManager.Get(LocKey.MonitorLocal), -1, ref rcLocal, 0x01 | 0x04 | 0x20);

            DrawRoundedBox(hdc, rcRemote, S(4), _currentPalette.ColPreviewRemoteBg, ColPrimaryBlue, 1);
            _ = SetTextColor(hdc, _currentPalette.IsDarkTitleBar ? Rgb(248, 250, 252) : ColPrimaryBlueDark);
            string remLabel = edge switch
            {
                ScreenEdge.Left => $"<- {LocalizationManager.Get(LocKey.MonitorRemote)}",
                ScreenEdge.Right => $"{LocalizationManager.Get(LocKey.MonitorRemote)} ->",
                ScreenEdge.Top => $"^ {LocalizationManager.Get(LocKey.MonitorRemote)}",
                ScreenEdge.Bottom => $"v {LocalizationManager.Get(LocKey.MonitorRemote)}",
                _ => LocalizationManager.Get(LocKey.MonitorRemote)
            };
            _ = DrawTextW(hdc, remLabel, -1, ref rcRemote, 0x01 | 0x04 | 0x20);
            SelectObject(hdc, oldFont);
        }

        private void DrawOwnerDrawButton(ref DRAWITEMSTRUCT dis)
        {
            if (!_buttonsById.TryGetValue((int)dis.CtlID, out var btn))
                return;

            IntPtr hdc = dis.hDC;
            RECT rc = dis.rcItem;
            bool isPressed = (dis.itemState & 0x0001 /* ODS_SELECTED */) != 0;
            bool isDisabled = (dis.itemState & 0x0004 /* ODS_DISABLED */) != 0;

            // Clear button background with container color so rounded corners look clean
            uint bgClearCol = (btn.Id is >= 701 and <= 713) ? ColHeaderBg : ColCardBg;
            FillSolidRect(hdc, rc, bgClearCol);

            uint fillCol;
            uint borderCol;
            uint textCol;
            int borderWidth = 1;
            IntPtr font = _fontRegular;

            if (isDisabled)
            {
                fillCol = _currentPalette.IsDarkTitleBar ? Rgb(30, 41, 59) : Rgb(241, 245, 249);
                borderCol = ColCardBorder;
                textCol = ColTextMuted;
            }
            else
            {
                switch (btn.Variant)
                {
                    case ButtonStyleVariant.PrimaryAction:
                        fillCol = isPressed ? ColPrimaryBlueDark : (btn.IsHovered ? ColPrimaryBlueHover : ColPrimaryBlue);
                        borderCol = fillCol;
                        textCol = Rgb(255, 255, 255);
                        font = _fontButtonBold;
                        break;

                    case ButtonStyleVariant.DangerAction:
                        fillCol = isPressed ? Rgb(153, 27, 27) : (btn.IsHovered ? ColDangerRedHover : ColDangerRed);
                        borderCol = fillCol;
                        textCol = Rgb(255, 255, 255);
                        font = _fontButtonBold;
                        break;

                    case ButtonStyleVariant.DarkAccent:
                        fillCol = isPressed ? Rgb(15, 23, 42) : (btn.IsHovered ? ColDarkAccentHover : ColDarkAccent);
                        borderCol = fillCol;
                        textCol = Rgb(255, 255, 255);
                        font = _fontButtonBold;
                        break;

                    case ButtonStyleVariant.SegmentToggle:
                        if (btn.IsSelected)
                        {
                            fillCol = ColSegmentActiveBg;
                            borderCol = ColPrimaryBlue;
                            textCol = _currentPalette.IsDarkTitleBar ? Rgb(255, 255, 255) : ColPrimaryBlueDark;
                            borderWidth = 2;
                            font = _fontButtonBold;
                        }
                        else
                        {
                            fillCol = btn.IsHovered ? ColSecondaryHover : ColSecondaryBg;
                            borderCol = ColCardBorder;
                            textCol = ColTextSecondary;
                            font = _fontRegular;
                        }
                        break;

                    default: // SecondaryOutline
                        fillCol = isPressed ? ColCardBorder : (btn.IsHovered ? ColSecondaryHover : ColSecondaryBg);
                        borderCol = btn.IsHovered ? Rgb(148, 163, 184) : ColCardBorder;
                        textCol = ColTextPrimary;
                        font = _fontRegular;
                        break;
                }
            }

            DrawRoundedBox(hdc, rc, S(6), fillCol, borderCol, borderWidth);

            _ = SetBkMode(hdc, 1 /* TRANSPARENT */);
            _ = SetTextColor(hdc, textCol);
            IntPtr oldFont = SelectObject(hdc, font);
            _ = DrawTextW(hdc, btn.Label, -1, ref rc, 0x01 | 0x04 | 0x20 /* DT_CENTER | DT_VCENTER | DT_SINGLELINE */);
            SelectObject(hdc, oldFont);
        }

        private void SyncVisualStates()
        {
            bool isReceiver = _config.DeviceRole == DeviceOperationRole.ReceiverControlled;

            SetButtonSelected(IDC_BTN_LANG_PT, LocalizationManager.ActiveLanguage == AppLanguage.Portuguese);
            SetButtonSelected(IDC_BTN_LANG_EN, LocalizationManager.ActiveLanguage == AppLanguage.English);
            SetButtonSelected(IDC_BTN_LANG_ES, LocalizationManager.ActiveLanguage == AppLanguage.Spanish);

            SetButtonSelected(IDC_BTN_THEME_LIGHT, _config.Theme == AppTheme.Light);
            SetButtonSelected(IDC_BTN_THEME_DARK, _config.Theme == AppTheme.Dark);
            SetButtonSelected(IDC_BTN_THEME_SYSTEM, _config.Theme == AppTheme.System);

            SetButtonSelected(IDC_BTN_ROLE_SENDER, !isReceiver);
            SetButtonSelected(IDC_BTN_ROLE_RECEIVER, isReceiver);

            SetButtonSelected(IDC_BTN_EDGE_LEFT, _config.MonitorEdge == ScreenEdge.Left);
            SetButtonSelected(IDC_BTN_EDGE_TOP, _config.MonitorEdge == ScreenEdge.Top);
            SetButtonSelected(IDC_BTN_EDGE_BOTTOM, _config.MonitorEdge == ScreenEdge.Bottom);
            SetButtonSelected(IDC_BTN_EDGE_RIGHT, _config.MonitorEdge == ScreenEdge.Right);

            if (_buttonsById.TryGetValue(IDC_BTN_START_STOP, out var startBtn))
            {
                if (_isSessionRunning)
                {
                    startBtn.Variant = ButtonStyleVariant.DangerAction;
                    startBtn.Label = isReceiver
                        ? LocalizationManager.Get(LocKey.StopReceiverButton)
                        : LocalizationManager.Get(LocKey.DisconnectButton);
                }
                else
                {
                    startBtn.Variant = ButtonStyleVariant.PrimaryAction;
                    startBtn.Label = isReceiver
                        ? LocalizationManager.Get(LocKey.StartReceiverButton)
                        : LocalizationManager.Get(LocKey.ConnectButton);
                }
                InvalidateRect(startBtn.Hwnd, IntPtr.Zero, false);
            }

            EnableWindow(_btnRoleSender, !_isSessionRunning);
            EnableWindow(_btnRoleReceiver, !_isSessionRunning);
            EnableWindow(_cmbLocalNic, !_isSessionRunning);

            if (_hwndMain != IntPtr.Zero)
            {
                InvalidateRect(_hwndMain, IntPtr.Zero, false);
            }
        }

        private void SetButtonSelected(int id, bool selected)
        {
            if (_buttonsById.TryGetValue(id, out var st))
            {
                st.IsSelected = selected;
                InvalidateRect(st.Hwnd, IntPtr.Zero, false);
            }
        }

        private void SetButtonLabel(int id, string text)
        {
            if (_buttonsById.TryGetValue(id, out var st))
            {
                st.Label = text;
                InvalidateRect(st.Hwnd, IntPtr.Zero, false);
            }
        }

        private void PopulateConnModesCombo()
        {
            if (_cmbConnMode == IntPtr.Zero) return;
            SendMessageW(_cmbConnMode, 0x014B /* CB_RESETCONTENT */, IntPtr.Zero, IntPtr.Zero);
            AddComboString(_cmbConnMode, LocalizationManager.Get(LocKey.ModeAuto));
            AddComboString(_cmbConnMode, LocalizationManager.Get(LocKey.ModeA));
            AddComboString(_cmbConnMode, LocalizationManager.Get(LocKey.ModeB));
            AddComboString(_cmbConnMode, LocalizationManager.Get(LocKey.ModeC));

            int modeIdx = _config.PreferredVpnMode switch
            {
                VpnFallbackStrategy.ModeA_ZeroAdminDiagnostic => 1,
                VpnFallbackStrategy.ModeB_StepUpWfpLanPermit => 2,
                VpnFallbackStrategy.ModeC_BluetoothOutOfBand => 3,
                _ => 0
            };
            SendMessageW(_cmbConnMode, CB_SETCURSEL, modeIdx, IntPtr.Zero);
        }

        private void ApplyLanguage(AppLanguage language)
        {
            _config = _config with { Language = language };
            PersistGuiSettingsToConfig();
            LocalizationManager.SetLanguage(language);

            PopulateConnModesCombo();
            UpdateLocalizedUiTexts();
            SyncVisualStates();
            if (_hwndMain != IntPtr.Zero)
            {
                InvalidateRect(_hwndMain, IntPtr.Zero, true);
            }
        }

        private void ApplyTheme(AppTheme theme)
        {
            _config = _config with { Theme = theme };
            PersistGuiSettingsToConfig();
            _currentPalette = ThemePalette.Resolve(theme);

            if (_hwndMain != IntPtr.Zero)
            {
                int isDark = _currentPalette.IsDarkTitleBar ? 1 : 0;
                _ = DwmSetWindowAttribute(_hwndMain, DWMWA_USE_IMMERSIVE_DARK_MODE, ref isDark, sizeof(int));

                DeleteIfValid(_brushCardBg);
                DeleteIfValid(_brushEditBg);
                DeleteIfValid(_brushLogBg);

                _brushCardBg = CreateSolidBrush(ColCardBg);
                _brushEditBg = CreateSolidBrush(ColInputBg);
                _brushLogBg = CreateSolidBrush(ColLogBg);

                SyncVisualStates();
                InvalidateRect(_hwndMain, IntPtr.Zero, true);
            }
        }

        private void UpdateLocalizedUiTexts()
        {
            SetButtonLabel(IDC_BTN_ROLE_SENDER, LocalizationManager.Get(LocKey.RoleSenderButton));
            SetButtonLabel(IDC_BTN_ROLE_RECEIVER, LocalizationManager.Get(LocKey.RoleReceiverButton));
            SetButtonLabel(IDC_BTN_REFRESH_NICS, LocalizationManager.Get(LocKey.RefreshButton));
            SetButtonLabel(IDC_BTN_NEW_PIN, LocalizationManager.Get(LocKey.NewPinButton));
            SetButtonLabel(IDC_BTN_COPY_MY_INFO, LocalizationManager.Get(LocKey.CopyDataButton));
            SetButtonLabel(IDC_BTN_SCAN_PEERS, LocalizationManager.Get(LocKey.ScanPeersButton));
            SetButtonLabel(IDC_BTN_PROBE_VPN, LocalizationManager.Get(LocKey.ProbeVpnButton));
            SetButtonLabel(IDC_BTN_EDGE_LEFT, LocalizationManager.Get(LocKey.EdgeLeft));
            SetButtonLabel(IDC_BTN_EDGE_TOP, LocalizationManager.Get(LocKey.EdgeTop));
            SetButtonLabel(IDC_BTN_EDGE_BOTTOM, LocalizationManager.Get(LocKey.EdgeBottom));
            SetButtonLabel(IDC_BTN_EDGE_RIGHT, LocalizationManager.Get(LocKey.EdgeRight));
            SetButtonLabel(IDC_BTN_APPLY_MODE_B, LocalizationManager.Get(LocKey.ApplyModeBButton));
            SetButtonLabel(IDC_BTN_SEND_FILE, LocalizationManager.Get(LocKey.SendFileButton));
            SetButtonLabel(IDC_BTN_SELF_TEST, LocalizationManager.Get(LocKey.SelfTestButton));
            SetButtonLabel(IDC_BTN_CLEAR_LOG, LocalizationManager.Get(LocKey.ClearLogButton));

            SetButtonLabel(IDC_BTN_THEME_LIGHT, "☀️ " + LocalizationManager.Get(LocKey.ThemeLight));
            SetButtonLabel(IDC_BTN_THEME_DARK, "🌙 " + LocalizationManager.Get(LocKey.ThemeDark));
            SetButtonLabel(IDC_BTN_THEME_SYSTEM, "💻 " + LocalizationManager.Get(LocKey.ThemeSystem));

            if (_chkAutostart != IntPtr.Zero)
            {
                SetWindowTextW(_chkAutostart, LocalizationManager.Get(LocKey.AutostartCheckbox));
            }

            SetListViewColumnText(_lvPeers, 0, LocalizationManager.Get(LocKey.ColHost));
            SetListViewColumnText(_lvPeers, 1, LocalizationManager.Get(LocKey.ColIp));
            SetListViewColumnText(_lvPeers, 2, LocalizationManager.Get(LocKey.ColBtMac));
            SetListViewColumnText(_lvPeers, 3, LocalizationManager.Get(LocKey.ColArpMac));
            SetListViewColumnText(_lvPeers, 4, LocalizationManager.Get(LocKey.ColDiag));

            if (_hwndMain != IntPtr.Zero)
            {
                SetWindowTextW(_hwndMain, $"{LocalizationManager.Get(LocKey.AppTitle)} ({Environment.MachineName})");
            }
        }

        private static void SetListViewColumnText(IntPtr hLv, int index, string header)
        {
            if (hLv == IntPtr.Zero) return;
            IntPtr textPtr = Marshal.StringToHGlobalUni(header);
            try
            {
                var col = new LVCOLUMNW
                {
                    mask = 0x0004 /* LVCF_TEXT */,
                    pszText = textPtr
                };
                IntPtr colPtr = Marshal.AllocHGlobal(Marshal.SizeOf<LVCOLUMNW>());
                try
                {
                    Marshal.StructureToPtr(col, colPtr, false);
                    SendMessageW(hLv, 0x101A /* LVM_SETCOLUMNW */, index, colPtr);
                }
                finally
                {
                    Marshal.FreeHGlobal(colPtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(textPtr);
            }
        }

        private void RefreshPhysicalNicsCombo()
        {
            SendMessageW(_cmbLocalNic, CB_RESETCONTENT, IntPtr.Zero, IntPtr.Zero);
            _physicalNics = VpnBypassNetwork.GetPhysicalLanInterfaces();

            if (_physicalNics.Count == 0)
            {
                AddComboString(_cmbLocalNic, $"Loopback / Local ({_localLanIp})");
                SendMessageW(_cmbLocalNic, CB_SETCURSEL, IntPtr.Zero, IntPtr.Zero);
                return;
            }

            int selectedIdx = 0;
            for (int i = 0; i < _physicalNics.Count; i++)
            {
                var nic = _physicalNics[i];
                AddComboString(_cmbLocalNic, $"{nic.Ipv4Address}  —  [{nic.Type}] {nic.Name} ({nic.Description})");
                if (nic.Ipv4Address.Equals(_localLanIp))
                {
                    selectedIdx = i;
                }
            }

            SendMessageW(_cmbLocalNic, CB_SETCURSEL, selectedIdx, IntPtr.Zero);
            _localLanIp = _physicalNics[selectedIdx].Ipv4Address;
        }

        private void PersistGuiSettingsToConfig()
        {
            string pin = GetControlText(_txtPin).Trim();
            if (string.IsNullOrWhiteSpace(pin))
            {
                pin = _config.PairingPin;
            }

            string targetIp = GetControlText(_txtTargetIp).Trim();
            string targetBt = GetControlText(_txtTargetBtMac).Trim();

            int modeSel = (int)SendMessageW(_cmbConnMode, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
            var preferredVpn = modeSel switch
            {
                2 => VpnFallbackStrategy.ModeB_StepUpWfpLanPermit,
                3 => VpnFallbackStrategy.ModeC_BluetoothOutOfBand,
                _ => VpnFallbackStrategy.ModeA_ZeroAdminDiagnostic
            };

            _config = _config with
            {
                PairingPin = pin,
                PreferredVpnMode = preferredVpn,
                LastTargetIp = targetIp,
                LastTargetBtMac = targetBt
            };

            TrayAndEdgeManager.SaveConfig(_config);
            UpdateTrayTooltip();
        }

        private void StartNetworkScan()
        {
            if (_isScanning) return;
            _isScanning = true;
            EnableWindow(_btnScanPeers, false);
            SetButtonLabel(IDC_BTN_SCAN_PEERS, "Buscando na Rede e Bluetooth...");
            _scanSummaryText = $"Varrendo sub-rede de {_localLanIp} (UDP Beacon + ARP L2 + Bluetooth)...";
            InvalidateRect(_hwndMain, IntPtr.Zero, false);

            IPAddress scanIp = _localLanIp;
            _ = Task.Run(async () =>
            {
                try
                {
                    var peers = await NetworkPeerDiscovery.ScanNetworkAndBluetoothPeersAsync(
                        scanIp,
                        msg => AppendLogLineThreadSafe(msg),
                        _appCt);

                    InvokeOnUi(() =>
                    {
                        _discoveredPeers = peers;
                        PopulatePeersListView(peers);
                        _scanSummaryText = peers.Count > 0
                            ? $"{peers.Count} dispositivo(s) encontrado(s). Clique em uma linha para preencher IP, MAC Bluetooth e Modo."
                            : "Nenhum dispositivo respondeu ainda. Verifique a rede ou informe o IP/Hostname manualmente.";
                        InvalidateRect(_hwndMain, IntPtr.Zero, false);
                    });
                }
                catch (Exception ex)
                {
                    AppendLogLineThreadSafe($"[Scanner Erro] {ex.Message}");
                }
                finally
                {
                    InvokeOnUi(() =>
                    {
                        _isScanning = false;
                        EnableWindow(_btnScanPeers, true);
                        SetButtonLabel(IDC_BTN_SCAN_PEERS, "Buscar Dispositivos na Rede e Bluetooth");
                    });
                }
            });
        }

        private void PopulatePeersListView(List<DiscoveredNetworkPeer> peers)
        {
            SendMessageW(_lvPeers, LVM_DELETEALLITEMS, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < peers.Count; i++)
            {
                var p = peers[i];
                InsertListViewItem(_lvPeers, i, p.Hostname);
                SetListViewSubItem(_lvPeers, i, 1, string.IsNullOrWhiteSpace(p.IpAddress) ? "N/A (Bluetooth)" : p.IpAddress);
                SetListViewSubItem(_lvPeers, i, 2, string.IsNullOrWhiteSpace(p.BluetoothMac) ? "—" : p.BluetoothMac);
                SetListViewSubItem(_lvPeers, i, 3, p.PhysicalMac);
                SetListViewSubItem(_lvPeers, i, 4, $"{p.RoleDescription} — {p.StatusSummary}");
            }
        }

        private void OnPeerListSelectionChanged()
        {
            int idx = (int)SendMessageW(_lvPeers, LVM_GETNEXTITEM, -1, LVNI_SELECTED);
            if (idx < 0 || idx >= _discoveredPeers.Count)
                return;

            var peer = _discoveredPeers[idx];
            if (!string.IsNullOrWhiteSpace(peer.IpAddress))
            {
                SetWindowTextW(_txtTargetIp, peer.IpAddress);
            }
            if (!string.IsNullOrWhiteSpace(peer.BluetoothMac))
            {
                SetWindowTextW(_txtTargetBtMac, peer.BluetoothMac);
            }

            int recommendedComboIdx = peer.RecommendedStrategy switch
            {
                VpnFallbackStrategy.ModeA_ZeroAdminDiagnostic => 1,
                VpnFallbackStrategy.ModeB_StepUpWfpLanPermit => 2,
                VpnFallbackStrategy.ModeC_BluetoothOutOfBand => 3,
                _ => 0
            };

            if (string.IsNullOrWhiteSpace(peer.IpAddress) && !string.IsNullOrWhiteSpace(peer.BluetoothMac))
            {
                recommendedComboIdx = 3;
            }

            SendMessageW(_cmbConnMode, CB_SETCURSEL, recommendedComboIdx, IntPtr.Zero);
            PersistGuiSettingsToConfig();

            AppendLogLine($"[Seleção] '{peer.Hostname}' -> IP='{peer.IpAddress}' | BT='{peer.BluetoothMac}' | Recomendado={peer.RecommendedStrategy}");
        }

        private async Task<IPAddress?> ResolveTargetLanIpAsync(string inputIpOrHost)
        {
            string clean = inputIpOrHost.Trim();
            if (string.IsNullOrWhiteSpace(clean))
                return null;

            if (IPAddress.TryParse(clean, out IPAddress? directIp))
            {
                return directIp;
            }

            AppendLogLineThreadSafe($"[DNS/mDNS] Resolvendo hostname '{clean}' na rede local...");
            string[] candidates = clean.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
                ? [clean, clean[..^6]]
                : [clean, $"{clean}.local"];

            foreach (string host in candidates)
            {
                try
                {
                    var entry = await Dns.GetHostEntryAsync(host);
                    var lanAddr = entry.AddressList.FirstOrDefault(a =>
                        a.AddressFamily == AddressFamily.InterNetwork && IntranetGuard.IsStrictlyLanIp(a));
                    if (lanAddr != null)
                    {
                        AppendLogLineThreadSafe($"[DNS/mDNS] Hostname '{host}' resolvido para IP LAN: {lanAddr}");
                        InvokeOnUi(() => SetWindowTextW(_txtTargetIp, lanAddr.ToString()));
                        return lanAddr;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private void ToggleStartStopSession()
        {
            if (_isSessionRunning)
            {
                StopActiveSession();
                return;
            }

            PersistGuiSettingsToConfig();
            string pin = _config.PairingPin;
            IPAddress localIp = _localLanIp;
            int connModeIdx = (int)SendMessageW(_cmbConnMode, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
            string rawTargetIp = GetControlText(_txtTargetIp).Trim();
            string rawTargetBt = GetControlText(_txtTargetBtMac).Trim();

            _activeSessionCts?.Dispose();
            _activeSessionCts = CancellationTokenSource.CreateLinkedTokenSource(_appCt);
            CancellationToken sessionToken = _activeSessionCts.Token;
            _isSessionRunning = true;
            _isRemoteControllingNow = false;
            SyncVisualStates();

            if (_config.DeviceRole == DeviceOperationRole.ReceiverControlled)
            {
                AppendLogLine($"[Operação] Iniciando MODO RECEPTOR em {localIp}:{MKSharePeerNode.DefaultUdpPort} + Bluetooth ({_localBtMac}) + Arquivos (:{SecureFileChannel.DefaultTcpFilePort})...");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await MKSharePeerNode.RunReceiverAsync(localIp, MKSharePeerNode.DefaultUdpPort, pin, sessionToken);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        AppendLogLineThreadSafe($"[Receptor Erro] {ex.Message}");
                    }
                    finally
                    {
                        InvokeOnUi(() =>
                        {
                            _isSessionRunning = false;
                            SyncVisualStates();
                            AppendLogLine("[Operação] Modo Receptor encerrado.");
                        });
                    }
                }, sessionToken);
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (connModeIdx == 3 || (string.IsNullOrWhiteSpace(rawTargetIp) && !string.IsNullOrWhiteSpace(rawTargetBt)))
                    {
                        string btTarget = rawTargetBt;
                        if (string.IsNullOrWhiteSpace(btTarget))
                        {
                            var paired = BluetoothRfcommNode.GetPairedBluetoothDevices();
                            if (paired.Count > 0)
                            {
                                btTarget = paired[0].MacAddress;
                                InvokeOnUi(() => SetWindowTextW(_txtTargetBtMac, btTarget));
                            }
                        }

                        if (string.IsNullOrWhiteSpace(btTarget))
                        {
                            InvokeOnUi(() => _ = MessageBoxW(_hwndMain,
                                "Para conectar no Modo C (Bluetooth Out-of-Band), informe o MAC Bluetooth do outro PC ou selecione um item Bluetooth na tabela.",
                                "KMShare — MAC Bluetooth Necessário",
                                0x00000030));
                            return;
                        }

                        AppendLogLineThreadSafe($"[Operação] Conectando como CONTROLADOR via Modo C (Bluetooth Out-of-Band -> {btTarget})...");
                        await BluetoothRfcommNode.RunBluetoothSenderAsync(btTarget, pin, sessionToken);
                        return;
                    }

                    IPAddress? targetIp = await ResolveTargetLanIpAsync(rawTargetIp);
                    if (targetIp == null)
                    {
                        InvokeOnUi(() => _ = MessageBoxW(_hwndMain,
                            "Informe um endereço IP LAN válido (ex: 192.168.15.6) ou selecione o outro computador na tabela acima.",
                            "KMShare — Endereço do Dispositivo Necessário",
                            0x00000030));
                        return;
                    }

                    IntranetGuard.EnforceLanEndpoint(new IPEndPoint(targetIp, MKSharePeerNode.DefaultUdpPort));

                    if (connModeIdx == 2)
                    {
                        AppendLogLineThreadSafe($"[Modo B] Solicitando autorização Step-Up para rota /32 e firewall UDP para {targetIp}...");
                        bool okB = MKSharePeerNode.TryApplyModeBStepUpFirewallAndRoute(localIp, targetIp, MKSharePeerNode.DefaultUdpPort);
                        AppendLogLineThreadSafe(okB
                            ? $"[Modo B] Exceção /32 aplicada para {targetIp}!"
                            : "[Modo B] Elevação não aplicada; tentando conexão LAN...");
                    }

                    AppendLogLineThreadSafe($"[Operação] Conectando como CONTROLADOR via LAN ({localIp} -> {targetIp}:{MKSharePeerNode.DefaultUdpPort})...");
                    await MKSharePeerNode.RunSenderAsync(
                        localIp,
                        targetIp,
                        MKSharePeerNode.DefaultUdpPort,
                        pin,
                        sessionToken,
                        autoReconnectOnNetworkLoss: true);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    AppendLogLineThreadSafe($"[Controlador Erro] {ex.Message}");
                }
                finally
                {
                    InvokeOnUi(() =>
                    {
                        _isSessionRunning = false;
                        _isRemoteControllingNow = false;
                        SyncVisualStates();
                        AppendLogLine("[Operação] Sessão do Controlador finalizada.");
                    });
                }
            }, sessionToken);
        }

        private void StopActiveSession()
        {
            try
            {
                _activeSessionCts?.Cancel();
                HidPlatformFactory.GetCurrent().TerminateSessionAndReleaseInput("Sessão interrompida pelo usuário");
            }
            catch
            {
            }
            finally
            {
                _isSessionRunning = false;
                _isRemoteControllingNow = false;
                if (_hwndMain != IntPtr.Zero)
                {
                    SyncVisualStates();
                }
            }
        }

        private int PromptVpnFallbackDialog(IPAddress targetIp)
        {
            if (_activeSessionCts == null || _activeSessionCts.IsCancellationRequested)
                return 0;

            int tcsResult = 0;
            using var waitHandle = new ManualResetEventSlim(false);

            InvokeOnUi(() =>
            {
                try
                {
                    string btMac = GetControlText(_txtTargetBtMac).Trim();
                    string msg =
                        $"Não houve resposta UDP de {targetIp}:{MKSharePeerNode.DefaultUdpPort}.\n\n" +
                        "Causas comuns:\n" +
                        "  1. O outro computador ainda não iniciou o 'Modo Receptor'.\n" +
                        "  2. Uma VPN com Kill-Switch está bloqueando a Camada 3.\n\n" +
                        "Escolha como prosseguir:\n" +
                        "  • [SIM]: Autorizar Exceção de Rota/Firewall Step-Up (/32 - Modo B) e tentar em 1000Hz.\n" +
                        (string.IsNullOrWhiteSpace(btMac)
                            ? "  • [NÃO]: Tentar novamente via LAN Direta (Modo A).\n"
                            : $"  • [NÃO]: Conectar imediatamente via Bluetooth Out-of-Band (Modo C -> {btMac}).\n") +
                        "  • [CANCELAR]: Parar tentativa.";

                    int mb = MessageBoxW(_hwndMain, msg, "KMShare — Timeout LAN / Selecionar Modo de Fallback", 0x00000023);
                    if (mb == 6)
                        tcsResult = 2;
                    else if (mb == 7)
                        tcsResult = string.IsNullOrWhiteSpace(btMac) ? 1 : 3;
                    else
                        tcsResult = 0;
                }
                finally
                {
                    waitHandle.Set();
                }
            });

            waitHandle.Wait();
            return tcsResult;
        }

        private string? GetSelectedBluetoothTarget()
        {
            string bt = "";
            using var waitHandle = new ManualResetEventSlim(false);
            InvokeOnUi(() =>
            {
                bt = GetControlText(_txtTargetBtMac).Trim();
                waitHandle.Set();
            });
            waitHandle.Wait();
            return string.IsNullOrWhiteSpace(bt) ? null : bt;
        }

        private void OnSendFileClicked()
        {
            PersistGuiSettingsToConfig();
            string rawIp = GetControlText(_txtTargetIp).Trim();
            string? selectedFile = ShowOpenFileDialog(_hwndMain);
            if (string.IsNullOrWhiteSpace(selectedFile) || !File.Exists(selectedFile))
                return;

            EnableWindow(_btnSendFile, false);
            IPAddress localIp = _localLanIp;
            string pin = _config.PairingPin;

            _ = Task.Run(async () =>
            {
                try
                {
                    IPAddress? targetIp = await ResolveTargetLanIpAsync(rawIp);
                    if (targetIp == null)
                    {
                        InvokeOnUi(() => _ = MessageBoxW(_hwndMain,
                            "Informe o IP LAN do computador receptor antes de enviar o arquivo.",
                            "KMShare — IP Necessário",
                            0x00000030));
                        return;
                    }

                    AppendLogLineThreadSafe($"[FileTransfer] Conectando em {targetIp}:{SecureFileChannel.DefaultTcpFilePort} para enviar '{Path.GetFileName(selectedFile)}'...");
                    var res = await SecureFileChannel.SendSingleFileOverTcpAsync(
                        localIp,
                        new IPEndPoint(targetIp, SecureFileChannel.DefaultTcpFilePort),
                        pin,
                        selectedFile,
                        (sent, total) =>
                        {
                            if (sent == total || sent % (64 * 1024) == 0)
                            {
                                AppendLogLineThreadSafe($"[FileTransfer] Progresso: {sent:N0} / {total:N0} bytes...");
                            }
                        },
                        ct: _appCt);

                    AppendLogLineThreadSafe($"[FileTransfer] Resultado: {res.Status} ({res.BytesTransferred:N0} bytes).");
                    InvokeOnUi(() => _ = MessageBoxW(_hwndMain,
                        $"Arquivo: {res.FileName}\nStatus: {res.Status}\nBytes transferidos: {res.BytesTransferred:N0}",
                        "KMShare — Transferência Segura de Arquivo",
                        res.Status == FileTransferStatus.Committed ? 0x00000040u : 0x00000030u));
                }
                catch (Exception ex)
                {
                    AppendLogLineThreadSafe($"[FileTransfer Erro] {ex.Message}");
                    InvokeOnUi(() => _ = MessageBoxW(_hwndMain,
                        $"Falha ao enviar arquivo: {ex.Message}\nCertifique-se de que o outro PC está com o Modo Receptor ativo.",
                        "KMShare — Erro no Envio",
                        0x00000010));
                }
                finally
                {
                    InvokeOnUi(() => EnableWindow(_btnSendFile, true));
                }
            });
        }

        private void OnProbeVpnClicked()
        {
            string target = GetControlText(_txtTargetIp).Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                target = "ntdesenv03";
            }

            IPAddress localIp = _localLanIp;
            AppendLogLine($"[Diagnóstico] Executando VpnKillSwitchProbe (SendARP L2 + ICMP/mDNS/LLMNR/NetBIOS L3) para '{target}'...");
            _ = Task.Run(async () =>
            {
                try
                {
                    await VpnKillSwitchProbe.ProbeTargetAsync(target, localIp);
                }
                catch (Exception ex)
                {
                    AppendLogLineThreadSafe($"[Diagnóstico Erro] {ex.Message}");
                }
            });
        }

        private void OnApplyModeBClicked()
        {
            string rawIp = GetControlText(_txtTargetIp).Trim();
            IPAddress localIp = _localLanIp;
            _ = Task.Run(async () =>
            {
                IPAddress? targetIp = await ResolveTargetLanIpAsync(rawIp);
                if (targetIp == null)
                {
                    InvokeOnUi(() => _ = MessageBoxW(_hwndMain,
                        "Informe ou selecione o IP LAN do outro dispositivo para autorizar a regra /32 do Modo B.",
                        "KMShare — Modo B Step-Up",
                        0x00000030));
                    return;
                }

                bool ok = MKSharePeerNode.TryApplyModeBStepUpFirewallAndRoute(localIp, targetIp, MKSharePeerNode.DefaultUdpPort);
                AppendLogLineThreadSafe(ok
                    ? $"[Modo B] Rota /32 e regra de Firewall para {targetIp} aplicadas com sucesso!"
                    : "[Modo B] Operação cancelada pelo usuário ou não aplicada.");
            });
        }

        private void OnCopyMyInfoClicked()
        {
            PersistGuiSettingsToConfig();
            string info =
                $"KMShare Device: {Environment.MachineName} | LAN IP: {_localLanIp} | Bluetooth MAC: {_localBtMac} | PIN: {_config.PairingPin}";
            TrySetClipboardText(_hwndMain, info);
            AppendLogLine($"[Área de Transferência] Copiado: {info}");
            _ = MessageBoxW(_hwndMain,
                $"Dados deste computador copiados para a Área de Transferência:\n\n" +
                $"• Computador: {Environment.MachineName}\n" +
                $"• IP LAN Física: {_localLanIp}\n" +
                $"• MAC Bluetooth: {_localBtMac}\n" +
                $"• PIN de Pareamento: {_config.PairingPin}",
                "KMShare — Dados Locais Copiados",
                0x00000040);
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_CREATE:
                    _hwndMain = hWnd;
                    BuildChildControls(hWnd);
                    return IntPtr.Zero;

                case WM_ERASEBKGND:
                    return 1;

                case WM_GETMINMAXINFO:
                    {
                        if (lParam != IntPtr.Zero)
                        {
                            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                            mmi.ptMinTrackSize.X = S(880);
                            mmi.ptMinTrackSize.Y = S(680);
                            Marshal.StructureToPtr(mmi, lParam, false);
                        }
                        return IntPtr.Zero;
                    }

                case WM_SIZE:
                    {
                        int newW = (int)(lParam.ToInt64() & 0xFFFF);
                        int newH = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
                        if (newW > 0 && newH > 0)
                        {
                            _clientWidth = newW;
                            _clientHeight = newH;
                            LayoutControls(newW, newH);
                        }
                        return IntPtr.Zero;
                    }

                case WM_PAINT:
                    PaintWindow(hWnd);
                    return IntPtr.Zero;

                case WM_SETTINGCHANGE:
                case WM_THEMECHANGED:
                    {
                        if (_config.Theme == AppTheme.System)
                        {
                            ApplyTheme(AppTheme.System);
                        }
                        break;
                    }

                case WM_DRAWITEM:
                    {
                        if (lParam != IntPtr.Zero)
                        {
                            var dis = Marshal.PtrToStructure<DRAWITEMSTRUCT>(lParam);
                            if (dis.CtlType == 4 /* ODT_BUTTON */)
                            {
                                DrawOwnerDrawButton(ref dis);
                                return 1;
                            }
                        }
                        break;
                    }

                case WM_CTLCOLORBTN:
                case WM_CTLCOLORSTATIC:
                    {
                        if (lParam == _txtLog)
                        {
                            _ = SetBkMode(wParam, 2 /* OPAQUE */);
                            _ = SetBkColor(wParam, ColLogBg);
                            _ = SetTextColor(wParam, ColTextPrimary);
                            return _brushLogBg;
                        }
                        _ = SetBkMode(wParam, 1 /* TRANSPARENT */);
                        _ = SetBkColor(wParam, ColCardBg);
                        _ = SetTextColor(wParam, ColTextPrimary);
                        return _brushCardBg;
                    }

                case WM_CTLCOLOREDIT:
                case WM_CTLCOLORLISTBOX:
                    {
                        _ = SetTextColor(wParam, ColTextPrimary);
                        if (lParam == _txtLog)
                        {
                            _ = SetBkMode(wParam, 2 /* OPAQUE */);
                            _ = SetBkColor(wParam, ColLogBg);
                            return _brushLogBg;
                        }
                        _ = SetBkColor(wParam, ColInputBg);
                        return _brushEditBg;
                    }

                case WM_APP_UI_DISPATCH:
                    while (_uiDispatchQueue.TryDequeue(out Action? action))
                    {
                        try { action(); } catch { }
                    }
                    return IntPtr.Zero;

                case WM_APP_TRAYICON:
                    {
                        int trayEvent = (int)(lParam.ToInt64() & 0xFFFF);
                        if (trayEvent is 0x0202 or 0x0203 or 0x0205)
                        {
                            ShowWindow(hWnd, 9 /* SW_RESTORE */);
                            SetForegroundWindow(hWnd);
                        }
                        return IntPtr.Zero;
                    }

                case WM_NOTIFY:
                    {
                        if (lParam != IntPtr.Zero)
                        {
                            var hdr = Marshal.PtrToStructure<NMHDR>(lParam);
                            if (hdr.idFrom.ToInt64() == IDC_LV_PEERS &&
                                (hdr.code == LVN_ITEMCHANGED || hdr.code == NM_CLICK || hdr.code == NM_DBLCLK))
                            {
                                OnPeerListSelectionChanged();
                            }
                        }
                        break;
                    }

                case WM_COMMAND:
                    {
                        int id = (int)(wParam.ToInt64() & 0xFFFF);
                        int notifyCode = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
                        HandleCommand(id, notifyCode);
                        return IntPtr.Zero;
                    }

                case WM_CLOSE:
                    PersistGuiSettingsToConfig();
                    StopActiveSession();
                    DestroyWindow(hWnd);
                    return IntPtr.Zero;

                case WM_DESTROY:
                    RemoveSystemTrayIcon();
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }

            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        private void HandleCommand(int id, int notifyCode)
        {
            switch (id)
            {
                case IDC_BTN_ROLE_SENDER:
                    _config = _config with { DeviceRole = DeviceOperationRole.SenderController };
                    PersistGuiSettingsToConfig();
                    SyncVisualStates();
                    AppendLogLine("[Modo de Operação] Definido como CONTROLADOR / PRINCIPAL (Sender).");
                    break;

                case IDC_BTN_ROLE_RECEIVER:
                    _config = _config with { DeviceRole = DeviceOperationRole.ReceiverControlled };
                    PersistGuiSettingsToConfig();
                    SyncVisualStates();
                    AppendLogLine("[Modo de Operação] Definido como RECEPTOR / SECUNDÁRIO (Receiver).");
                    break;

                case IDC_CMB_LOCAL_NIC when notifyCode == CBN_SELCHANGE:
                    {
                        int sel = (int)SendMessageW(_cmbLocalNic, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
                        if (sel >= 0 && sel < _physicalNics.Count)
                        {
                            _localLanIp = _physicalNics[sel].Ipv4Address;
                            SyncVisualStates();
                            AppendLogLine($"[Placa Física] Selecionada interface {_physicalNics[sel].Name} ({_localLanIp}).");
                        }
                        break;
                    }

                case IDC_BTN_REFRESH_NICS:
                    RefreshPhysicalNicsCombo();
                    SyncVisualStates();
                    AppendLogLine($"[Placa Física] Interfaces físicas atualizadas ({_physicalNics.Count} encontrada(s)).");
                    break;

                case IDC_BTN_NEW_PIN:
                    {
                        string newPin = RandomNumberGenerator.GetInt32(100000, 999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        SetWindowTextW(_txtPin, newPin);
                        _config = _config with { PairingPin = newPin };
                        PersistGuiSettingsToConfig();
                        SyncVisualStates();
                        AppendLogLine($"[Segurança] Novo PIN gerado: {newPin} (informe o mesmo PIN no outro dispositivo).");
                        break;
                    }

                case IDC_BTN_COPY_MY_INFO:
                    OnCopyMyInfoClicked();
                    break;

                case IDC_CHK_AUTOSTART:
                    {
                        bool isChecked = SendMessageW(_chkAutostart, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) == BST_CHECKED;
                        if (TrayAndEdgeManager.SetUserSpaceAutostart(isChecked))
                        {
                            _config = _config with { RunAtUserLogin = isChecked };
                            PersistGuiSettingsToConfig();
                            AppendLogLine($"[Autostart] Inicialização com o login do usuário: {(isChecked ? "ATIVADA" : "DESATIVADA")}.");
                        }
                        break;
                    }

                case IDC_BTN_SCAN_PEERS:
                    StartNetworkScan();
                    break;

                case IDC_BTN_PROBE_VPN:
                    OnProbeVpnClicked();
                    break;

                case IDC_CMB_CONN_MODE when notifyCode == CBN_SELCHANGE:
                    PersistGuiSettingsToConfig();
                    break;

                case IDC_BTN_EDGE_TOP:
                    SetMonitorEdge(ScreenEdge.Top);
                    break;
                case IDC_BTN_EDGE_LEFT:
                    SetMonitorEdge(ScreenEdge.Left);
                    break;
                case IDC_BTN_EDGE_RIGHT:
                    SetMonitorEdge(ScreenEdge.Right);
                    break;
                case IDC_BTN_EDGE_BOTTOM:
                    SetMonitorEdge(ScreenEdge.Bottom);
                    break;

                case IDC_BTN_START_STOP:
                    ToggleStartStopSession();
                    break;

                case IDC_BTN_APPLY_MODE_B:
                    OnApplyModeBClicked();
                    break;

                case IDC_BTN_SEND_FILE:
                    OnSendFileClicked();
                    break;

                case IDC_BTN_SELF_TEST:
                    {
                        if (_onRunDiagnostics != null)
                        {
                            EnableWindow(_btnSelfTest, false);
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _onRunDiagnostics();
                                }
                                catch (Exception ex)
                                {
                                    AppendLogLineThreadSafe($"[Self-Test Erro] {ex.Message}");
                                }
                                finally
                                {
                                    InvokeOnUi(() => EnableWindow(_btnSelfTest, true));
                                }
                            });
                        }
                        break;
                    }

                case IDC_BTN_CLEAR_LOG:
                    _logBuffer.Clear();
                    SetWindowTextW(_txtLog, "");
                    break;

                case IDC_BTN_LANG_PT:
                    ApplyLanguage(AppLanguage.Portuguese);
                    AppendLogLine("[Idioma] Alterado para Português.");
                    break;

                case IDC_BTN_LANG_EN:
                    ApplyLanguage(AppLanguage.English);
                    AppendLogLine("[Language] Changed to English.");
                    break;

                case IDC_BTN_LANG_ES:
                    ApplyLanguage(AppLanguage.Spanish);
                    AppendLogLine("[Idioma] Cambiado a Español.");
                    break;

                case IDC_BTN_THEME_LIGHT:
                    ApplyTheme(AppTheme.Light);
                    AppendLogLine("[Tema] Modo Claro (Light) ativado.");
                    break;

                case IDC_BTN_THEME_DARK:
                    ApplyTheme(AppTheme.Dark);
                    AppendLogLine("[Tema] Modo Escuro (Dark) ativado.");
                    break;

                case IDC_BTN_THEME_SYSTEM:
                    ApplyTheme(AppTheme.System);
                    AppendLogLine("[Tema] Modo Automático do Sistema ativado.");
                    break;
            }
        }

        private void SetMonitorEdge(ScreenEdge edge)
        {
            _config = _config with { MonitorEdge = edge };
            PersistGuiSettingsToConfig();
            SyncVisualStates();
            AppendLogLine($"[Monitor] Borda de travessia definida para {edge} (entra pela borda {EdgeCoordinateMapper.GetOppositeEdge(edge)} do outro PC).");
        }

        private void InvokeOnUi(Action action)
        {
            if (_hwndMain == IntPtr.Zero) return;
            _uiDispatchQueue.Enqueue(action);
            PostMessageW(_hwndMain, WM_APP_UI_DISPATCH, IntPtr.Zero, IntPtr.Zero);
        }

        private void AppendLogLineThreadSafe(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            InvokeOnUi(() => AppendLogLine(line));
        }

        private void AppendLogLine(string line)
        {
            if (_txtLog == IntPtr.Zero) return;
            string timestamped = $"[{DateTime.Now:HH:mm:ss}] {line.TrimEnd()}\r\n";
            _logBuffer.Append(timestamped);
            if (_logBuffer.Length > 28_000)
            {
                _logBuffer.Remove(0, _logBuffer.Length - 20_000);
            }
            string full = _logBuffer.ToString();
            SetWindowTextW(_txtLog, full);
            SendMessageW(_txtLog, EM_SETSEL, full.Length, full.Length);
            SendMessageW(_txtLog, EM_SCROLLCARET, IntPtr.Zero, IntPtr.Zero);
        }

        private void InitSystemTrayIcon(IntPtr hWnd)
        {
            try
            {
                IntPtr hExe = GetModuleHandleW(null);
                IntPtr hTrayIcon = LoadIconW(hExe, 1);
                if (hTrayIcon == IntPtr.Zero)
                {
                    hTrayIcon = LoadIconW(IntPtr.Zero, 32512);
                }
                _trayNid = new NOTIFYICONDATAW
                {
                    cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                    hWnd = hWnd,
                    uID = 2001,
                    uFlags = 0x0001 | 0x0002 | 0x0004,
                    uCallbackMessage = WM_APP_TRAYICON,
                    hIcon = hTrayIcon
                };
                SetTrayTip(ref _trayNid, $"KMShare | {_localLanIp} | Borda:{_config.MonitorEdge} | PIN:{_config.PairingPin}");
                _trayAdded = Shell_NotifyIconW(0x00000000, ref _trayNid);
            }
            catch
            {
            }
        }

        private void UpdateTrayTooltip()
        {
            if (!_trayAdded) return;
            try
            {
                SetTrayTip(ref _trayNid, $"KMShare | {_localLanIp} | Borda:{_config.MonitorEdge} | PIN:{_config.PairingPin}");
                Shell_NotifyIconW(0x00000001, ref _trayNid);
            }
            catch
            {
            }
        }

        private static unsafe void SetTrayTip(ref NOTIFYICONDATAW nid, string text)
        {
            int len = Math.Min(text.Length, 120);
            fixed (char* pTip = nid.szTip)
            {
                text.AsSpan(0, len).CopyTo(new Span<char>(pTip, 128));
                pTip[len] = '\0';
            }
        }

        private void RemoveSystemTrayIcon()
        {
            if (_trayAdded)
            {
                try { Shell_NotifyIconW(0x00000002, ref _trayNid); } catch { }
                _trayAdded = false;
            }
        }

        public void Dispose()
        {
            try { _discoveryResponderCts.Cancel(); } catch { }
            _discoveryResponderCts.Dispose();
            StopActiveSession();
            _activeSessionCts?.Dispose();
            RemoveSystemTrayIcon();

            MKSharePeerNode.CustomVpnFallbackPrompt = null;
            MKSharePeerNode.CustomBluetoothTargetSelector = null;
            Console.SetOut(_originalConsoleOut);
            _logTeeWriter.Dispose();

            DeleteIfValid(_fontHeaderTitle);
            DeleteIfValid(_fontHeaderSub);
            DeleteIfValid(_fontSectionTitle);
            DeleteIfValid(_fontLabelBold);
            DeleteIfValid(_fontRegular);
            DeleteIfValid(_fontSmall);
            DeleteIfValid(_fontButtonBold);
            DeleteIfValid(_fontMono);
            DeleteIfValid(_brushCardBg);
            DeleteIfValid(_brushEditBg);
            DeleteIfValid(_brushLogBg);
            GC.SuppressFinalize(this);
        }

        private static void DeleteIfValid(IntPtr gdiObj)
        {
            if (gdiObj != IntPtr.Zero) DeleteObject(gdiObj);
        }

        // ---------------------------------------------------------------------
        // Win32 GDI & Control Helpers
        // ---------------------------------------------------------------------
        private static IntPtr CreateCleanFont(int heightPx, int weight, string faceName)
        {
            // CLEARTYPE_QUALITY = 5
            return CreateFontW(-heightPx, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, faceName);
        }

        private IntPtr CreateOwnerDrawButton(IntPtr hParent, string label, int id, ButtonStyleVariant variant)
        {
            IntPtr hBtn = CreateWindowExW(
                0, "BUTTON", label,
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW,
                0, 0, 100, 28,
                hParent, id, GetModuleHandleW(null), IntPtr.Zero);

            var state = new OwnerDrawButtonState
            {
                Id = id,
                Hwnd = hBtn,
                Label = label,
                Variant = variant
            };
            _buttonsById[id] = state;

            nint proc(nint hW, uint uMsg, nint wP, nint lP, nuint uId, nint dwRef)
            {
                if (uMsg == WM_MOUSEMOVE && !state.IsHovered)
                {
                    state.IsHovered = true;
                    var tme = new TRACKMOUSEEVENT
                    {
                        cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(),
                        dwFlags = 0x00000002 /* TME_LEAVE */,
                        hwndTrack = hW,
                        dwHoverTime = 0
                    };
                    TrackMouseEvent(ref tme);
                    InvalidateRect(hW, IntPtr.Zero, false);
                }
                else if (uMsg == WM_MOUSELEAVE && state.IsHovered)
                {
                    state.IsHovered = false;
                    InvalidateRect(hW, IntPtr.Zero, false);
                }
                return DefSubclassProc(hW, uMsg, wP, lP);
            }
            _buttonSubclasses[hBtn] = proc;
            SetWindowSubclass(hBtn, proc, (UIntPtr)id, IntPtr.Zero);

            return hBtn;
        }

        private IntPtr CreateEditControl(IntPtr hParent, string text, int id, IntPtr font)
        {
            IntPtr hEdit = CreateControl(hParent, "EDIT", text,
                WS_CHILD | WS_VISIBLE | WS_BORDER | WS_TABSTOP | ES_AUTOHSCROLL,
                id, font);
            SendMessageW(hEdit, EM_SETMARGINS, 3, (S(6) << 16) | S(6));
            return hEdit;
        }

        private static IntPtr CreateControl(IntPtr hParent, string className, string text, int style, int id, IntPtr hFont)
        {
            IntPtr hCtrl = CreateWindowExW(
                0, className, text, style,
                0, 0, 100, 24,
                hParent, id, GetModuleHandleW(null), IntPtr.Zero);
            if (hCtrl != IntPtr.Zero && hFont != IntPtr.Zero)
            {
                SendMessageW(hCtrl, WM_SETFONT, hFont, 1);
            }
            return hCtrl;
        }

        private static void FillSolidRect(IntPtr hdc, RECT rc, uint color)
        {
            IntPtr br = CreateSolidBrush(color);
            try
            {
                _ = FillRect(hdc, ref rc, br);
            }
            finally
            {
                DeleteObject(br);
            }
        }

        private static void DrawRoundedBox(IntPtr hdc, RECT rc, int radius, uint fillCol, uint borderCol, int borderWidth)
        {
            IntPtr brush = CreateSolidBrush(fillCol);
            IntPtr pen = CreatePen(0 /* PS_SOLID */, borderWidth, borderCol);
            IntPtr oldBr = SelectObject(hdc, brush);
            IntPtr oldPen = SelectObject(hdc, pen);
            try
            {
                RoundRect(hdc, rc.left, rc.top, rc.right, rc.bottom, radius, radius);
            }
            finally
            {
                SelectObject(hdc, oldBr);
                SelectObject(hdc, oldPen);
                DeleteObject(brush);
                DeleteObject(pen);
            }
        }

        private static void DrawTextLine(IntPtr hdc, IntPtr font, uint color, string text, int x, int y)
        {
            IntPtr oldFont = SelectObject(hdc, font);
            _ = SetTextColor(hdc, color);
            var rc = new RECT { left = x, top = y, right = x + 2400, bottom = y + 60 };
            _ = DrawTextW(hdc, text, -1, ref rc, 0x0000 | 0x0020 | 0x0100 | 0x0800 /* DT_LEFT | DT_SINGLELINE | DT_NOCLIP | DT_NOPREFIX */);
            SelectObject(hdc, oldFont);
        }

        private static void AddComboString(IntPtr hCombo, string text)
        {
            IntPtr ptr = Marshal.StringToHGlobalUni(text);
            try
            {
                SendMessageW(hCombo, CB_ADDSTRING, IntPtr.Zero, ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        private static unsafe string GetControlText(IntPtr hCtrl)
        {
            int len = GetWindowTextLengthW(hCtrl);
            if (len <= 0) return "";
            char[] buf = new char[len + 2];
            fixed (char* pBuf = buf)
            {
                int copied = GetWindowTextW(hCtrl, pBuf, buf.Length);
                return copied > 0 ? new string(buf, 0, copied) : "";
            }
        }

        private static void InsertListViewColumn(IntPtr hLv, int index, string header, int widthPx)
        {
            IntPtr textPtr = Marshal.StringToHGlobalUni(header);
            try
            {
                var col = new LVCOLUMNW
                {
                    mask = 0x0001 | 0x0002 | 0x0004,
                    fmt = 0,
                    cx = widthPx,
                    pszText = textPtr
                };
                IntPtr colPtr = Marshal.AllocHGlobal(Marshal.SizeOf<LVCOLUMNW>());
                try
                {
                    Marshal.StructureToPtr(col, colPtr, false);
                    SendMessageW(hLv, LVM_INSERTCOLUMNW, index, colPtr);
                }
                finally
                {
                    Marshal.FreeHGlobal(colPtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(textPtr);
            }
        }

        private static void InsertListViewItem(IntPtr hLv, int row, string text)
        {
            IntPtr textPtr = Marshal.StringToHGlobalUni(text);
            try
            {
                var item = new LVITEMW
                {
                    mask = 0x0001,
                    iItem = row,
                    iSubItem = 0,
                    pszText = textPtr
                };
                IntPtr itemPtr = Marshal.AllocHGlobal(Marshal.SizeOf<LVITEMW>());
                try
                {
                    Marshal.StructureToPtr(item, itemPtr, false);
                    SendMessageW(hLv, LVM_INSERTITEMW, IntPtr.Zero, itemPtr);
                }
                finally
                {
                    Marshal.FreeHGlobal(itemPtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(textPtr);
            }
        }

        private static void SetListViewSubItem(IntPtr hLv, int row, int subItem, string text)
        {
            IntPtr textPtr = Marshal.StringToHGlobalUni(text);
            try
            {
                var item = new LVITEMW
                {
                    mask = 0x0001,
                    iItem = row,
                    iSubItem = subItem,
                    pszText = textPtr
                };
                IntPtr itemPtr = Marshal.AllocHGlobal(Marshal.SizeOf<LVITEMW>());
                try
                {
                    Marshal.StructureToPtr(item, itemPtr, false);
                    SendMessageW(hLv, LVM_SETITEMW, IntPtr.Zero, itemPtr);
                }
                finally
                {
                    Marshal.FreeHGlobal(itemPtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(textPtr);
            }
        }

        private static unsafe string? ShowOpenFileDialog(IntPtr hwndOwner)
        {
            const int maxChars = 520;
            IntPtr fileBuf = Marshal.AllocHGlobal(maxChars * 2);
            const string filter = "Todos os Arquivos (*.*)\0*.*\0";
            const string title = "KMShare — Selecionar Arquivo para Enviar com Criptografia Ponta-a-Ponta";
            try
            {
                Marshal.WriteInt16(fileBuf, 0, 0);
                fixed (char* pFilter = filter)
                fixed (char* pTitle = title)
                {
                    var ofn = new OPENFILENAMEW
                    {
                        lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                        hwndOwner = hwndOwner,
                        lpstrFilter = (IntPtr)pFilter,
                        lpstrFile = fileBuf,
                        nMaxFile = maxChars,
                        lpstrTitle = (IntPtr)pTitle,
                        Flags = 0x00080000 | 0x00001000 | 0x00000800
                    };

                    if (GetOpenFileNameW(ref ofn))
                    {
                        return Marshal.PtrToStringUni(fileBuf);
                    }
                }
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(fileBuf);
            }
        }

        private static void TrySetClipboardText(IntPtr hWnd, string text)
        {
            if (!OpenClipboard(hWnd)) return;
            try
            {
                EmptyClipboard();
                byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
                IntPtr hGlobal = GlobalAlloc(0x0042, (UIntPtr)bytes.Length);
                if (hGlobal == IntPtr.Zero) return;
                IntPtr target = GlobalLock(hGlobal);
                if (target != IntPtr.Zero)
                {
                    try
                    {
                        Marshal.Copy(bytes, 0, target, bytes.Length);
                    }
                    finally
                    {
                        GlobalUnlock(hGlobal);
                    }
                    SetClipboardData(13, hGlobal);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        // ---------------------------------------------------------------------
        // Win32 Structs & P/Invoke Declarations
        // ---------------------------------------------------------------------
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private delegate IntPtr SubclassProcDelegate(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

        [StructLayout(LayoutKind.Sequential)]
        private struct INITCOMMONCONTROLSEX { public int dwSize; public uint dwICC; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public RECT rcPaint;
            public int fRestore;
            public int fIncUpdate;
            public long rgbReserved0;
            public long rgbReserved1;
            public long rgbReserved2;
            public long rgbReserved3;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DRAWITEMSTRUCT
        {
            public uint CtlType;
            public uint CtlID;
            public uint itemID;
            public uint itemAction;
            public uint itemState;
            public IntPtr hwndItem;
            public IntPtr hDC;
            public RECT rcItem;
            public UIntPtr itemData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TRACKMOUSEEVENT
        {
            public int cbSize;
            public uint dwFlags;
            public IntPtr hwndTrack;
            public uint dwHoverTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public IntPtr lpszMenuName;
            public IntPtr lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NMHDR
        {
            public IntPtr hwndFrom;
            public IntPtr idFrom;
            public int code;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LVCOLUMNW
        {
            public uint mask;
            public int fmt;
            public int cx;
            public IntPtr pszText;
            public int cchTextMax;
            public int iSubItem;
            public int iImage;
            public int iOrder;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LVITEMW
        {
            public uint mask;
            public int iItem;
            public int iSubItem;
            public uint state;
            public uint stateMask;
            public IntPtr pszText;
            public int cchTextMax;
            public int iImage;
            public IntPtr lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct NOTIFYICONDATAW
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            public fixed char szTip[128];
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct OPENFILENAMEW
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public IntPtr lpstrFilter;
            public IntPtr lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public IntPtr lpstrFileTitle;
            public int nMaxFileTitle;
            public IntPtr lpstrInitialDir;
            public IntPtr lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public IntPtr lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public IntPtr lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [LibraryImport("comctl32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX picce);

        [LibraryImport("comctl32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowSubclass(IntPtr hWnd, SubclassProcDelegate pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

        [LibraryImport("comctl32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetProcessDPIAware();

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr GetDC(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr BeginDeferWindowPos(int nNumWindows);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr DeferWindowPos(IntPtr hWinPosInfo, IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool EndDeferWindowPos(IntPtr hWinPosInfo);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [LibraryImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr CreateWindowExW(
            int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UpdateWindow(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DestroyWindow(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial void PostQuitMessage(int nExitCode);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool TranslateMessage(ref MSG lpMsg);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr DispatchMessageW(ref MSG lpMsg);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowTextW(IntPtr hWnd, string lpString);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static unsafe partial int GetWindowTextW(IntPtr hWnd, char* lpString, int nMaxCount);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int GetWindowTextLengthW(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool EnableWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool bEnable);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetForegroundWindow(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int GetSystemMetrics(int nIndex);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool OpenClipboard(IntPtr hWndNewOwner);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool EmptyClipboard();

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseClipboard();

        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr GetModuleHandleW(string? lpModuleName);

        [LibraryImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [LibraryImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr GlobalLock(IntPtr hMem);

        [LibraryImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GlobalUnlock(IntPtr hMem);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int GetDeviceCaps(IntPtr hdc, int index);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr CreateCompatibleDC(IntPtr hdc);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DeleteDC(IntPtr hdc);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr CreateSolidBrush(uint color);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr CreatePen(int iStyle, int cWidth, uint color);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool RoundRect(IntPtr hdc, int left, int top, int right, int bottom, int width, int height);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int SetBkMode(IntPtr hdc, int mode);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial uint SetBkColor(IntPtr hdc, uint color);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial uint SetTextColor(IntPtr hdc, uint color);

        [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr CreateFontW(
            int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
            uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet,
            uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily, string pszFaceName);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DeleteObject(IntPtr ho);

        [LibraryImport("shell32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpdata);

        [LibraryImport("comdlg32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetOpenFileNameW(ref OPENFILENAMEW lpofn);

        [LibraryImport("dwmapi.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }

    private sealed class GuiLogTeeWriter(TextWriter originalOut, Action<string> onLine) : TextWriter
    {
        private readonly StringBuilder _lineBuf = new();
        private readonly Lock _lock = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            try { originalOut.Write(value); } catch { }
            lock (_lock)
            {
                if (value == '\n')
                {
                    string line = _lineBuf.ToString().TrimEnd('\r');
                    _lineBuf.Clear();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        onLine(line);
                    }
                }
                else
                {
                    _lineBuf.Append(value);
                }
            }
        }

        public override void WriteLine(string? value)
        {
            try { originalOut.WriteLine(value); } catch { }
            if (!string.IsNullOrWhiteSpace(value))
            {
                onLine(value);
            }
        }
    }
}
