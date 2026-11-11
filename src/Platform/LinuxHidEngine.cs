using System.Diagnostics;
using System.Runtime.InteropServices;
using MKShare.Security;

namespace MKShare.Platform;

public sealed class LinuxHidEngine : IHidPlatform
{
    public static LinuxHidEngine Instance { get; } = new();

    private const string LibX11 = "libX11.so.6";
    private const string LibXtst = "libXtst.so.6";
    private const string LibEi = "libei.so.1";

    public const long PeerDeadTimeoutMs = 2500;

    private volatile bool _isRemoteActive;
    private volatile bool _isSessionTerminated;
    private long _lastPeerAliveTickMs;
    private ScreenEdge _activeCrossingEdge = ScreenEdge.Right;

    private int _screenWidth = 1920;
    private int _screenHeight = 1080;
    private int _anchorX = 960;
    private int _anchorY = 540;

    public event Action<byte[]>? OnOutgoingHidFrame;
    public event Action<bool>? OnRemoteStateChanged;
    public event Action<ScreenEdge, ushort>? OnEdgeCrossed;

    public bool IsRemoteActive => _isRemoteActive;

    public ScreenEdge ActiveCrossingEdge
    {
        get => _activeCrossingEdge;
        set => _activeCrossingEdge = value;
    }

    public static bool IsWaylandSession()
    {
        string? sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        string? waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        return string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(waylandDisplay);
    }

    public void NotifyPeerAlive()
    {
        Interlocked.Exchange(ref _lastPeerAliveTickMs, Environment.TickCount64);
    }

    public bool IsPeerAlive()
    {
        if (_isSessionTerminated) return false;
        long last = Interlocked.Read(ref _lastPeerAliveTickMs);
        return Environment.TickCount64 - last <= PeerDeadTimeoutMs;
    }

    public void TerminateSessionAndReleaseInput(string reason)
    {
        if (_isSessionTerminated) return;
        _isSessionTerminated = true;
        _isRemoteActive = false;
        Console.WriteLine($"\n[LINUX WATCHDOG] {reason} -> Input local liberado.");
    }

    public void SetRemoteActive(bool active, ushort? returnNormalizedCoord = null)
    {
        if (_isSessionTerminated && active) return;
        if (_isRemoteActive == active) return;

        if (active && !IsPeerAlive())
        {
            TerminateSessionAndReleaseInput("Peer inativo (>2.5s sem resposta)");
            return;
        }

        _isRemoteActive = active;
        if (active)
        {
            ushort norm = _activeCrossingEdge is ScreenEdge.Left or ScreenEdge.Right
                ? EdgeCoordinateMapper.Normalize(_anchorY, _screenHeight)
                : EdgeCoordinateMapper.Normalize(_anchorX, _screenWidth);
            OnEdgeCrossed?.Invoke(_activeCrossingEdge, norm);
        }

        OnRemoteStateChanged?.Invoke(active);
    }

    public void WarpCursorOnEdgeEnter(ScreenEdge senderExitEdge, ushort normalizedCoord)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
        (_screenWidth, _screenHeight) = GetPrimaryScreenBounds();
        var (nx, ny) = EdgeCoordinateMapper.MapEntryPosition(senderExitEdge, normalizedCoord, _screenWidth, _screenHeight, marginPx: 10);

        IntPtr dpy = XOpenDisplay(IntPtr.Zero);
        if (dpy != IntPtr.Zero)
        {
            try
            {
                IntPtr root = XDefaultRootWindow(dpy);
                XWarpPointer(dpy, IntPtr.Zero, root, 0, 0, 0, 0, nx, ny);
                XFlush(dpy);
            }
            finally
            {
                XCloseDisplay(dpy);
            }
        }
    }

    public void RunSenderCaptureLoop(CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            throw new PlatformNotSupportedException("LinuxHidEngine requires Linux.");

        _isSessionTerminated = false;
        NotifyPeerAlive();

        if (IsWaylandSession() && TryRequestWaylandPortalSession(out string portalDiag))
        {
            Console.WriteLine($"  [Linux Wayland Portal] {portalDiag}");
        }

        IntPtr dpy = XOpenDisplay(IntPtr.Zero);
        if (dpy == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to open X11/XWayland display in user-space.");
        }

        try
        {
            int screen = XDefaultScreen(dpy);
            _screenWidth = Math.Max(640, XDisplayWidth(dpy, screen));
            _screenHeight = Math.Max(480, XDisplayHeight(dpy, screen));
            _anchorX = _screenWidth / 2;
            _anchorY = _screenHeight / 2;
            IntPtr root = XDefaultRootWindow(dpy);

            Console.WriteLine($"  [Linux HID Capture Ready] Screen={_screenWidth}x{_screenHeight} | Edge={_activeCrossingEdge}");

            uint prevMask = 0;
            while (!ct.IsCancellationRequested && !_isSessionTerminated)
            {
                if (_isRemoteActive && !IsPeerAlive())
                {
                    XUngrabPointer(dpy, 0);
                    XUngrabKeyboard(dpy, 0);
                    XFlush(dpy);
                    TerminateSessionAndReleaseInput("Timeout de conexão (>2.5s sem heartbeat)");
                    break;
                }

                if (XQueryPointer(dpy, root, out _, out _, out int rx, out int ry, out _, out _, out uint mask))
                {
                    if (!_isRemoteActive)
                    {
                        bool crossed = _activeCrossingEdge switch
                        {
                            ScreenEdge.Right => rx >= _screenWidth - 2,
                            ScreenEdge.Left => rx <= 1,
                            ScreenEdge.Bottom => ry >= _screenHeight - 2,
                            ScreenEdge.Top => ry <= 1,
                            _ => rx >= _screenWidth - 2
                        };

                        if (crossed)
                        {
                            _anchorX = Math.Clamp(rx, 4, _screenWidth - 4);
                            _anchorY = Math.Clamp(ry, 4, _screenHeight - 4);
                            SetRemoteActive(true);
                            XWarpPointer(dpy, IntPtr.Zero, root, 0, 0, 0, 0, _anchorX, _anchorY);
                            XFlush(dpy);
                        }
                    }
                    else
                    {
                        short dx = (short)Math.Clamp(rx - _anchorX, -500, 500);
                        short dy = (short)Math.Clamp(ry - _anchorY, -500, 500);
                        if (dx != 0 || dy != 0)
                        {
                            OnOutgoingHidFrame?.Invoke(HidFrame.EncodeMouseMove(dx, dy));
                            XWarpPointer(dpy, IntPtr.Zero, root, 0, 0, 0, 0, _anchorX, _anchorY);
                            XFlush(dpy);
                        }

                        // Button 1 (0x100) and Button 3 (0x400) state transitions
                        bool prevLeft = (prevMask & 0x100) != 0;
                        bool curLeft = (mask & 0x100) != 0;
                        if (prevLeft != curLeft)
                            OnOutgoingHidFrame?.Invoke(HidFrame.EncodeMouseButton(1, curLeft));

                        bool prevRight = (prevMask & 0x400) != 0;
                        bool curRight = (mask & 0x400) != 0;
                        if (prevRight != curRight)
                            OnOutgoingHidFrame?.Invoke(HidFrame.EncodeMouseButton(2, curRight));

                        prevMask = mask;
                    }
                }

                Thread.Sleep(2);
            }
        }
        finally
        {
            _isRemoteActive = false;
            XUngrabPointer(dpy, 0);
            XUngrabKeyboard(dpy, 0);
            XCloseDisplay(dpy);
        }
    }

    public static bool TryRequestWaylandPortalSession(out string diagnostic)
    {
        // Connects via xdg-desktop-portal (org.freedesktop.portal.RemoteDesktop / InputCapture) in user session without root
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(
                "gdbus",
                "introspect --session --dest org.freedesktop.portal.Desktop --object-path /org/freedesktop/portal/desktop")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            proc?.WaitForExit(1500);
            if (proc?.ExitCode == 0)
            {
                string output = proc.StandardOutput.ReadToEnd();
                bool hasInputCapture = output.Contains("org.freedesktop.portal.InputCapture", StringComparison.Ordinal);
                bool hasRemoteDesktop = output.Contains("org.freedesktop.portal.RemoteDesktop", StringComparison.Ordinal);
                bool hasLibEi = NativeLibrary.TryLoad(LibEi, out IntPtr eiHandle);
                if (hasLibEi) NativeLibrary.Free(eiHandle);

                diagnostic = $"D-Bus portal active (RemoteDesktop={hasRemoteDesktop}, InputCapture={hasInputCapture}, libei={hasLibEi})";
                return hasRemoteDesktop || hasInputCapture;
            }
        }
        catch
        {
        }

        diagnostic = "Wayland portal not active; using X11/XWayland user-space backend";
        return false;
    }

    public bool InjectHidEvent(HidEvent ev, ScreenEdge returnEdge, out bool hitReturnEdge, out ushort returnNormalizedCoord)
    {
        hitReturnEdge = false;
        returnNormalizedCoord = 5000;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        IntPtr dpy = XOpenDisplay(IntPtr.Zero);
        if (dpy == IntPtr.Zero) return false;

        try
        {
            int screen = XDefaultScreen(dpy);
            _screenWidth = Math.Max(640, XDisplayWidth(dpy, screen));
            _screenHeight = Math.Max(480, XDisplayHeight(dpy, screen));
            IntPtr root = XDefaultRootWindow(dpy);

            switch (ev.Type)
            {
                case HidFrame.MouseMove:
                    XTestFakeRelativeMotionEvent(dpy, ev.Arg1, ev.Arg2, 0);
                    XFlush(dpy);

                    if (XQueryPointer(dpy, root, out _, out _, out int rx, out int ry, out _, out _, out _))
                    {
                        switch (returnEdge)
                        {
                            case ScreenEdge.Left when ev.Arg1 < 0 && rx <= 0:
                                XWarpPointer(dpy, IntPtr.Zero, root, 0, 0, 0, 0, 10, ry);
                                hitReturnEdge = true;
                                returnNormalizedCoord = EdgeCoordinateMapper.Normalize(ry, _screenHeight);
                                break;
                            case ScreenEdge.Right when ev.Arg1 > 0 && rx >= _screenWidth - 2:
                                XWarpPointer(dpy, IntPtr.Zero, root, 0, 0, 0, 0, _screenWidth - 10, ry);
                                hitReturnEdge = true;
                                returnNormalizedCoord = EdgeCoordinateMapper.Normalize(ry, _screenHeight);
                                break;
                            case ScreenEdge.Top when ev.Arg2 < 0 && ry <= 0:
                                XWarpPointer(dpy, IntPtr.Zero, root, 0, 0, 0, 0, rx, 10);
                                hitReturnEdge = true;
                                returnNormalizedCoord = EdgeCoordinateMapper.Normalize(rx, _screenWidth);
                                break;
                            case ScreenEdge.Bottom when ev.Arg2 > 0 && ry >= _screenHeight - 2:
                                XWarpPointer(dpy, IntPtr.Zero, root, 0, 0, 0, 0, rx, _screenHeight - 10);
                                hitReturnEdge = true;
                                returnNormalizedCoord = EdgeCoordinateMapper.Normalize(rx, _screenWidth);
                                break;
                        }
                        if (hitReturnEdge) XFlush(dpy);
                    }
                    return true;

                case HidFrame.MouseButton:
                    uint xBtn = ev.Arg1 == 2 ? 3u : 1u;
                    XTestFakeButtonEvent(dpy, xBtn, ev.Arg2 == 1, 0);
                    XFlush(dpy);
                    return true;

                case HidFrame.KeyEvent:
                    // Linux evdev/X11 keycode offset is scancode + 8
                    uint keycode = (uint)(ev.Arg1 + 8);
                    XTestFakeKeyEvent(dpy, keycode, ev.Arg2 == 1, 0);
                    XFlush(dpy);
                    return true;

                default:
                    return false;
            }
        }
        finally
        {
            XCloseDisplay(dpy);
        }
    }

    public void ReleaseAllModifierKeysOnReceiver()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
        ushort[] modScancodes = [0x5B, 0x5C, 0x1D, 0x2A, 0x36, 0x38];
        foreach (ushort sc in modScancodes)
        {
            InjectHidEvent(new HidEvent(HidFrame.KeyEvent, (short)sc, 0), ScreenEdge.Left, out _, out _);
        }
    }

    public (int Width, int Height) GetPrimaryScreenBounds()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return (1920, 1080);

        try
        {
            IntPtr dpy = XOpenDisplay(IntPtr.Zero);
            if (dpy != IntPtr.Zero)
            {
                try
                {
                    int s = XDefaultScreen(dpy);
                    int w = XDisplayWidth(dpy, s);
                    int h = XDisplayHeight(dpy, s);
                    if (w > 0 && h > 0) return (w, h);
                }
                finally
                {
                    XCloseDisplay(dpy);
                }
            }
        }
        catch
        {
        }
        return (1920, 1080);
    }

    [DllImport(LibX11)]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport(LibX11)]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport(LibX11)]
    private static extern int XDefaultScreen(IntPtr display);

    [DllImport(LibX11)]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport(LibX11)]
    private static extern int XDisplayWidth(IntPtr display, int screenNumber);

    [DllImport(LibX11)]
    private static extern int XDisplayHeight(IntPtr display, int screenNumber);

    [DllImport(LibX11)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool XQueryPointer(
        IntPtr display,
        IntPtr w,
        out IntPtr rootReturn,
        out IntPtr childReturn,
        out int rootXReturn,
        out int rootYReturn,
        out int winXReturn,
        out int winYReturn,
        out uint maskReturn);

    [DllImport(LibX11)]
    private static extern int XWarpPointer(
        IntPtr display,
        IntPtr srcW,
        IntPtr destW,
        int srcX,
        int srcY,
        uint srcWidth,
        uint srcHeight,
        int destX,
        int destY);

    [DllImport(LibX11)]
    private static extern int XUngrabPointer(IntPtr display, ulong time);

    [DllImport(LibX11)]
    private static extern int XUngrabKeyboard(IntPtr display, ulong time);

    [DllImport(LibX11)]
    private static extern int XFlush(IntPtr display);

    [DllImport(LibXtst)]
    private static extern int XTestFakeRelativeMotionEvent(IntPtr display, int dx, int dy, ulong delay);

    [DllImport(LibXtst)]
    private static extern int XTestFakeButtonEvent(IntPtr display, uint button, [MarshalAs(UnmanagedType.Bool)] bool isPress, ulong delay);

    [DllImport(LibXtst)]
    private static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, [MarshalAs(UnmanagedType.Bool)] bool isPress, ulong delay);
}
