using System.Runtime.InteropServices;
using MKShare.Security;

namespace MKShare.Platform;

public sealed class WindowsHidEngine : IHidPlatform
{
    public static WindowsHidEngine Instance { get; } = new();

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const int VK_F12 = 0x7B;

    // If no heartbeat/ack arrives from remote peer within 2.5 seconds, immediately release local mouse & keyboard
    public const long PeerDeadTimeoutMs = 2500;

    private static IntPtr _mouseHook = IntPtr.Zero;
    private static IntPtr _kbdHook = IntPtr.Zero;
    private static HookProc? _mouseProcDelegate;
    private static HookProc? _kbdProcDelegate;
    private static uint _hookThreadId;

    private static volatile bool _isRemoteActive;
    private static volatile bool _isSessionTerminated;
    private static long _lastPeerAliveTickMs;
    private static ScreenEdge _activeCrossingEdge = ScreenEdge.Right;

    private static int _screenWidth = 1920;
    private static int _screenHeight = 1080;
    private static int _anchorX = 960;
    private static int _anchorY = 540;
    private static bool _suppressNextWarpEvent;

    static WindowsHidEngine()
    {
        (_screenWidth, _screenHeight) = GetPrimaryScreenBounds();
        _anchorX = _screenWidth / 2;
        _anchorY = _screenHeight / 2;
    }

    public static event Action<byte[]>? OnOutgoingHidFrame;
    public static event Action<bool>? OnRemoteStateChanged;
    public static event Action<ScreenEdge, ushort>? OnEdgeCrossed;

    public static bool IsRemoteActive => _isRemoteActive;

    public static ScreenEdge ActiveCrossingEdge
    {
        get => _activeCrossingEdge;
        set => _activeCrossingEdge = value;
    }

    public static void ResetEventHandlers()
    {
        OnOutgoingHidFrame = null;
        OnRemoteStateChanged = null;
        OnEdgeCrossed = null;
    }

    public static void NotifyPeerAlive()
    {
        Interlocked.Exchange(ref _lastPeerAliveTickMs, Environment.TickCount64);
    }

    public static bool IsPeerAlive()
    {
        if (_isSessionTerminated) return false;
        long last = Interlocked.Read(ref _lastPeerAliveTickMs);
        return Environment.TickCount64 - last <= PeerDeadTimeoutMs;
    }

    public static void TerminateSessionAndReleaseInput(string reason)
    {
        if (_isSessionTerminated) return;
        _isSessionTerminated = true;

        if (_isRemoteActive)
        {
            _isRemoteActive = false;
            _suppressNextWarpEvent = true;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var (rx, ry) = GetReturnCursorPosition(_activeCrossingEdge, null);
                SetCursorPos(rx, ry);
            }
        }

        Console.WriteLine($"\n[WATCHDOG] {reason} -> Mouse e Teclado locais liberados e encerrando captura.");

        if (_hookThreadId != 0 && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            PostThreadMessage(_hookThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public static void SetRemoteActive(bool active, ushort? returnNormalizedCoord = null)
    {
        if (_isSessionTerminated && active) return;
        if (_isRemoteActive == active) return;

        if (active && !IsPeerAlive())
        {
            TerminateSessionAndReleaseInput("Conexão com o outro PC inativa/perdida (>2.5s sem resposta)");
            return;
        }

        if (active)
        {
            // Keep cursor parked right at the edge where it crossed (no jump to center of screen!)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && GetCursorPos(out POINT cur))
            {
                _anchorX = cur.X;
                _anchorY = cur.Y;
            }

            ushort normCoord = _activeCrossingEdge is ScreenEdge.Left or ScreenEdge.Right
                ? EdgeCoordinateMapper.Normalize(_anchorY, _screenHeight)
                : EdgeCoordinateMapper.Normalize(_anchorX, _screenWidth);

            _suppressNextWarpEvent = true;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var (px, py) = GetParkedEdgeAnchorPosition(_activeCrossingEdge);
                SetCursorPos(px, py);
            }
            _isRemoteActive = true;
            OnEdgeCrossed?.Invoke(_activeCrossingEdge, normCoord);
        }
        else
        {
            _isRemoteActive = false;
            _suppressNextWarpEvent = true;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var (rx, ry) = GetReturnCursorPosition(_activeCrossingEdge, returnNormalizedCoord);
                SetCursorPos(rx, ry);
            }
        }

        OnRemoteStateChanged?.Invoke(active);
    }

    private static (int X, int Y) GetParkedEdgeAnchorPosition(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Right => (_screenWidth - 4, _anchorY),
        ScreenEdge.Left => (4, _anchorY),
        ScreenEdge.Bottom => (_anchorX, _screenHeight - 4),
        ScreenEdge.Top => (_anchorX, 4),
        _ => (_screenWidth - 4, _anchorY)
    };

    private static (int X, int Y) GetReturnCursorPosition(ScreenEdge edge, ushort? returnNormalizedCoord)
    {
        int y = returnNormalizedCoord.HasValue && edge is ScreenEdge.Left or ScreenEdge.Right
            ? EdgeCoordinateMapper.Denormalize(returnNormalizedCoord.Value, _screenHeight)
            : _anchorY;
        int x = returnNormalizedCoord.HasValue && edge is ScreenEdge.Top or ScreenEdge.Bottom
            ? EdgeCoordinateMapper.Denormalize(returnNormalizedCoord.Value, _screenWidth)
            : _anchorX;

        return edge switch
        {
            ScreenEdge.Right => (_screenWidth - 25, y),
            ScreenEdge.Left => (25, y),
            ScreenEdge.Bottom => (x, _screenHeight - 25),
            ScreenEdge.Top => (x, 25),
            _ => (_screenWidth - 25, y)
        };
    }

    public static void WarpCursorOnEdgeEnter(ScreenEdge senderExitEdge, ushort normalizedCoord)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        (_screenWidth, _screenHeight) = GetPrimaryScreenBounds();
        var (nx, ny) = EdgeCoordinateMapper.MapEntryPosition(senderExitEdge, normalizedCoord, _screenWidth, _screenHeight, marginPx: 10);
        SetCursorPos(nx, ny);
    }

    public static void RunSenderCaptureLoop(CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("Win32 hooks require Windows.");

        Exception? hookError = null;
        var hookThread = new Thread(() =>
        {
            try
            {
                RunSenderCaptureLoopCore(ct);
            }
            catch (Exception ex)
            {
                hookError = ex;
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "KMShare-Win32HidHook"
        };

        hookThread.Start();
        hookThread.Join();

        if (hookError != null)
        {
            throw new InvalidOperationException(hookError.Message, hookError);
        }
    }

    private static void RunSenderCaptureLoopCore(CancellationToken ct)
    {
        _isSessionTerminated = false;
        NotifyPeerAlive();

        (_screenWidth, _screenHeight) = GetPrimaryScreenBounds();
        _anchorX = _screenWidth / 2;
        _anchorY = _screenHeight / 2;

        _mouseProcDelegate = MouseHookCallback;
        _kbdProcDelegate = KeyboardHookCallback;

        IntPtr hMod = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProcDelegate, hMod, 0);
        _kbdHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbdProcDelegate, hMod, 0);

        if (_mouseHook == IntPtr.Zero || _kbdHook == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to install user-space hooks (err={Marshal.GetLastWin32Error()}).");
        }

        Console.WriteLine($"  [HID Capture Ready] Screen={_screenWidth}x{_screenHeight} | Edge={_activeCrossingEdge} | Watchdog Timeout={PeerDeadTimeoutMs}ms.");
        Console.WriteLine($"  -> Mova o mouse até a BORDA ({_activeCrossingEdge}) da tela para cruzar para o outro PC!");
        Console.WriteLine("  -> Pressione [F12] a qualquer momento para forçar retorno imediato do mouse/teclado.");

        _hookThreadId = GetCurrentThreadId();
        using var reg = ct.Register(() => PostThreadMessage(_hookThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero));

        // Background Watchdog Timer: Checks every 250ms if remote connection timed out
        using var watchdogTimer = new Timer(_ =>
        {
            if (!IsPeerAlive())
            {
                TerminateSessionAndReleaseInput($"Timeout de conexão (> {PeerDeadTimeoutMs}ms sem heartbeat do PC remoto)");
            }
        }, null, dueTime: 500, period: 250);

        try
        {
            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            _hookThreadId = 0;
            _isRemoteActive = false;
            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
            if (_kbdHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_kbdHook);
                _kbdHook = IntPtr.Zero;
            }
        }
    }

    private static bool HasCrossedConfiguredEdge(POINT pt) => _activeCrossingEdge switch
    {
        ScreenEdge.Right => pt.X >= _screenWidth - 2,
        ScreenEdge.Left => pt.X <= 1,
        ScreenEdge.Bottom => pt.Y >= _screenHeight - 2,
        ScreenEdge.Top => pt.Y <= 1,
        _ => pt.X >= _screenWidth - 2
    };

    private static IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && lParam != IntPtr.Zero)
        {
            int msg = (int)wParam;
            if (msg != WM_MOUSEMOVE && !_isRemoteActive)
            {
                return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            // Fail-safe directly inside hook: never swallow local mouse if peer timed out or session ended
            if (_isRemoteActive && !IsPeerAlive())
            {
                TerminateSessionAndReleaseInput("Conexão perdida detectada durante movimento do mouse");
                return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            var pt = new POINT
            {
                X = Marshal.ReadInt32(lParam, 0),
                Y = Marshal.ReadInt32(lParam, 4)
            };

            if (msg == WM_MOUSEMOVE)
            {
                if (_suppressNextWarpEvent)
                {
                    _suppressNextWarpEvent = false;
                    return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
                }

                if (!_isRemoteActive)
                {
                    if (!_isSessionTerminated && HasCrossedConfiguredEdge(pt))
                    {
                        SetRemoteActive(true);
                        return (IntPtr)1;
                    }
                }
                else
                {
                    // When WH_MOUSE_LL returns 1 (swallowed), Windows keeps the OS cursor at (_anchorX, _anchorY).
                    // pt contains (_anchorX + rawDx, _anchorY + rawDy). No SetCursorPos needed per frame!
                    GetCursorPos(out POINT currentAnchor);
                    short dx = (short)Math.Clamp(pt.X - currentAnchor.X, -500, 500);
                    short dy = (short)Math.Clamp(pt.Y - currentAnchor.Y, -500, 500);

                    if (dx != 0 || dy != 0)
                    {
                        OnOutgoingHidFrame?.Invoke(HidFrame.EncodeMouseMove(dx, dy));
                    }
                    return (IntPtr)1;
                }
            }
            else if (_isRemoteActive)
            {
                switch (msg)
                {
                    case WM_LBUTTONDOWN:
                        OnOutgoingHidFrame?.Invoke(HidFrame.EncodeMouseButton(1, true));
                        return (IntPtr)1;
                    case WM_LBUTTONUP:
                        OnOutgoingHidFrame?.Invoke(HidFrame.EncodeMouseButton(1, false));
                        return (IntPtr)1;
                    case WM_RBUTTONDOWN:
                        OnOutgoingHidFrame?.Invoke(HidFrame.EncodeMouseButton(2, true));
                        return (IntPtr)1;
                    case WM_RBUTTONUP:
                        OnOutgoingHidFrame?.Invoke(HidFrame.EncodeMouseButton(2, false));
                        return (IntPtr)1;
                }
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            // Fail-safe directly inside hook: never swallow local keys if peer timed out or session ended
            if (_isRemoteActive && !IsPeerAlive())
            {
                TerminateSessionAndReleaseInput("Conexão perdida detectada ao pressionar tecla");
                return CallNextHookEx(_kbdHook, nCode, wParam, lParam);
            }

            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;

            if (isDown && info.vkCode == VK_F12)
            {
                SetRemoteActive(!_isRemoteActive);
                return (IntPtr)1;
            }

            if (_isRemoteActive)
            {
                OnOutgoingHidFrame?.Invoke(HidFrame.EncodeKey((ushort)info.scanCode, isDown));
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_kbdHook, nCode, wParam, lParam);
    }

    public static bool InjectHidEventAndCheckLeftEdgeReturn(HidEvent ev, out bool hitLeftEdge)
        => InjectHidEvent(ev, ScreenEdge.Left, out hitLeftEdge, out _);

    public static bool InjectHidEvent(HidEvent ev, ScreenEdge returnEdge, out bool hitReturnEdge, out ushort returnNormalizedCoord)
    {
        hitReturnEdge = false;
        returnNormalizedCoord = 5000;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        INPUT input = default;
        switch (ev.Type)
        {
            case HidFrame.MouseMove:
                input.type = INPUT_MOUSE;
                input.u.mi.dx = ev.Arg1;
                input.u.mi.dy = ev.Arg2;
                input.u.mi.dwFlags = MOUSEEVENTF_MOVE;
                break;

            case HidFrame.MouseButton:
                input.type = INPUT_MOUSE;
                bool down = ev.Arg2 == 1;
                input.u.mi.dwFlags = ev.Arg1 switch
                {
                    1 => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
                    2 => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
                    _ => 0
                };
                if (input.u.mi.dwFlags == 0) return false;
                break;

            case HidFrame.KeyEvent:
                input.type = INPUT_KEYBOARD;
                input.u.ki.wScan = (ushort)ev.Arg1;
                input.u.ki.dwFlags = KEYEVENTF_SCANCODE | (ev.Arg2 == 0 ? KEYEVENTF_KEYUP : 0u);
                break;

            default:
                return false;
        }

        INPUT[] inputs = [input];
        bool ok = SendInput(1, inputs, Marshal.SizeOf<INPUT>()) == 1;

        if (!ok && ev.Type == HidFrame.MouseMove)
        {
            // Fallback if SendInput is blocked by an elevated foreground window (UIPI) on receiver:
            // SetCursorPos still moves the cursor in user-space!
            if (GetCursorPos(out POINT cur))
            {
                int nx = Math.Clamp(cur.X + ev.Arg1, 0, _screenWidth);
                int ny = Math.Clamp(cur.Y + ev.Arg2, 0, _screenHeight);
                ok = SetCursorPos(nx, ny);
            }
        }

        if (ev.Type == HidFrame.MouseMove && GetCursorPos(out POINT pt))
        {
            switch (returnEdge)
            {
                case ScreenEdge.Left when ev.Arg1 < 0 && pt.X <= 0:
                    SetCursorPos(10, pt.Y);
                    hitReturnEdge = true;
                    returnNormalizedCoord = EdgeCoordinateMapper.Normalize(pt.Y, _screenHeight);
                    break;
                case ScreenEdge.Right when ev.Arg1 > 0 && pt.X >= _screenWidth - 2:
                    SetCursorPos(_screenWidth - 10, pt.Y);
                    hitReturnEdge = true;
                    returnNormalizedCoord = EdgeCoordinateMapper.Normalize(pt.Y, _screenHeight);
                    break;
                case ScreenEdge.Top when ev.Arg2 < 0 && pt.Y <= 0:
                    SetCursorPos(pt.X, 10);
                    hitReturnEdge = true;
                    returnNormalizedCoord = EdgeCoordinateMapper.Normalize(pt.X, _screenWidth);
                    break;
                case ScreenEdge.Bottom when ev.Arg2 > 0 && pt.Y >= _screenHeight - 2:
                    SetCursorPos(pt.X, _screenHeight - 10);
                    hitReturnEdge = true;
                    returnNormalizedCoord = EdgeCoordinateMapper.Normalize(pt.X, _screenWidth);
                    break;
            }
        }

        return ok;
    }

    public static void ReleaseAllModifierKeysOnReceiver()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        ushort[] modScancodes = [0x5B, 0x5C, 0x1D, 0x2A, 0x36, 0x38];
        foreach (ushort sc in modScancodes)
        {
            InjectHidEventAndCheckLeftEdgeReturn(new HidEvent(HidFrame.KeyEvent, (short)sc, 0), out _);
        }
    }

    public static (int Width, int Height) GetPrimaryScreenBounds()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return (1920, 1080);
        int w = GetSystemMetrics(0);
        int h = GetSystemMetrics(1);
        return (w > 0 ? w : 1920, h > 0 ? h : 1080);
    }

    // IHidPlatform explicit/instance forwarding
    event Action<byte[]>? IHidPlatform.OnOutgoingHidFrame
    {
        add => OnOutgoingHidFrame += value;
        remove => OnOutgoingHidFrame -= value;
    }

    event Action<bool>? IHidPlatform.OnRemoteStateChanged
    {
        add => OnRemoteStateChanged += value;
        remove => OnRemoteStateChanged -= value;
    }

    event Action<ScreenEdge, ushort>? IHidPlatform.OnEdgeCrossed
    {
        add => OnEdgeCrossed += value;
        remove => OnEdgeCrossed -= value;
    }

    bool IHidPlatform.IsRemoteActive => IsRemoteActive;
    ScreenEdge IHidPlatform.ActiveCrossingEdge
    {
        get => ActiveCrossingEdge;
        set => ActiveCrossingEdge = value;
    }

    void IHidPlatform.NotifyPeerAlive() => NotifyPeerAlive();
    bool IHidPlatform.IsPeerAlive() => IsPeerAlive();
    void IHidPlatform.SetRemoteActive(bool active, ushort? returnNormalizedCoord) => SetRemoteActive(active, returnNormalizedCoord);
    void IHidPlatform.TerminateSessionAndReleaseInput(string reason) => TerminateSessionAndReleaseInput(reason);
    void IHidPlatform.RunSenderCaptureLoop(CancellationToken ct) => RunSenderCaptureLoop(ct);
    bool IHidPlatform.InjectHidEvent(HidEvent ev, ScreenEdge returnEdge, out bool hitReturnEdge, out ushort returnNormalizedCoord)
        => InjectHidEvent(ev, returnEdge, out hitReturnEdge, out returnNormalizedCoord);
    void IHidPlatform.WarpCursorOnEdgeEnter(ScreenEdge senderExitEdge, ushort normalizedCoord)
        => WarpCursorOnEdgeEnter(senderExitEdge, normalizedCoord);
    void IHidPlatform.ReleaseAllModifierKeysOnReceiver() => ReleaseAllModifierKeysOnReceiver();
    (int Width, int Height) IHidPlatform.GetPrimaryScreenBounds() => GetPrimaryScreenBounds();

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
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
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
