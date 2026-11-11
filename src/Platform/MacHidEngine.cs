using System.Runtime.InteropServices;
using MKShare.Security;

namespace MKShare.Platform;

public sealed class MacHidEngine : IHidPlatform
{
    public static MacHidEngine Instance { get; } = new();

    private const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // kCGSessionEventTap = 1 (User-Space session level, zero root/sudo required)
    private const uint kCGHIDEventTap = 0;
    private const uint kCGSessionEventTap = 1;
    private const uint kCGHeadInsertEventTap = 0;
    private const uint kCGEventTapOptionDefault = 0;

    private const uint kCGEventLeftMouseDown = 1;
    private const uint kCGEventLeftMouseUp = 2;
    private const uint kCGEventRightMouseDown = 3;
    private const uint kCGEventRightMouseUp = 4;
    private const uint kCGEventMouseMoved = 5;
    private const uint kCGEventKeyDown = 10;
    private const uint kCGEventKeyUp = 11;

    private const long kCGKeyboardEventKeycode = 9;
    private const ushort MacKeyF12 = 111; // kVK_F12 = 0x6F

    public const long PeerDeadTimeoutMs = 2500;

    private volatile bool _isRemoteActive;
    private volatile bool _isSessionTerminated;
    private long _lastPeerAliveTickMs;
    private ScreenEdge _activeCrossingEdge = ScreenEdge.Right;

    private int _screenWidth = 1920;
    private int _screenHeight = 1080;
    private int _anchorX = 960;
    private int _anchorY = 540;
    private IntPtr _runLoop = IntPtr.Zero;
    private CGEventTapCallBack? _tapCallbackDelegate;

    public event Action<byte[]>? OnOutgoingHidFrame;
    public event Action<bool>? OnRemoteStateChanged;
    public event Action<ScreenEdge, ushort>? OnEdgeCrossed;

    public bool IsRemoteActive => _isRemoteActive;

    public ScreenEdge ActiveCrossingEdge
    {
        get => _activeCrossingEdge;
        set => _activeCrossingEdge = value;
    }

    public static bool EnsureUserSpaceAccessibilityTrusted()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return false;
        try
        {
            return AXIsProcessTrusted();
        }
        catch
        {
            return false;
        }
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

        Console.WriteLine($"\n[MACOS WATCHDOG] {reason} -> Input local liberado.");
        if (_runLoop != IntPtr.Zero && RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            CFRunLoopStop(_runLoop);
        }
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
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;
        (_screenWidth, _screenHeight) = GetPrimaryScreenBounds();
        var (nx, ny) = EdgeCoordinateMapper.MapEntryPosition(senderExitEdge, normalizedCoord, _screenWidth, _screenHeight, marginPx: 10);
        CGWarpMouseCursorPosition(new CGPoint { X = nx, Y = ny });
    }

    public void RunSenderCaptureLoop(CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            throw new PlatformNotSupportedException("MacHidEngine requires macOS.");

        if (!EnsureUserSpaceAccessibilityTrusted())
        {
            Console.WriteLine("  [macOS Accessibility] Habilite o KMShare em Ajustes do Sistema > Privacidade e Segurança > Acessibilidade (User-Space, sem sudo).");
        }

        _isSessionTerminated = false;
        NotifyPeerAlive();
        (_screenWidth, _screenHeight) = GetPrimaryScreenBounds();
        _anchorX = _screenWidth / 2;
        _anchorY = _screenHeight / 2;

        _tapCallbackDelegate = EventTapCallback;
        ulong mask = (1UL << (int)kCGEventMouseMoved)
                   | (1UL << (int)kCGEventLeftMouseDown)
                   | (1UL << (int)kCGEventLeftMouseUp)
                   | (1UL << (int)kCGEventRightMouseDown)
                   | (1UL << (int)kCGEventRightMouseUp)
                   | (1UL << (int)kCGEventKeyDown)
                   | (1UL << (int)kCGEventKeyUp);

        IntPtr tap = CGEventTapCreate(
            kCGSessionEventTap,
            kCGHeadInsertEventTap,
            kCGEventTapOptionDefault,
            mask,
            _tapCallbackDelegate,
            IntPtr.Zero);

        if (tap == IntPtr.Zero)
        {
            throw new InvalidOperationException("CGEventTapCreate failed: grant Accessibility permission in System Settings.");
        }

        IntPtr source = CFMachPortCreateRunLoopSource(IntPtr.Zero, tap, 0);
        _runLoop = CFRunLoopGetCurrent();

        using var reg = ct.Register(() =>
        {
            if (_runLoop != IntPtr.Zero) CFRunLoopStop(_runLoop);
        });

        using var watchdog = new Timer(_ =>
        {
            if (!IsPeerAlive())
            {
                TerminateSessionAndReleaseInput("Timeout de conexão (>2.5s sem heartbeat)");
            }
        }, null, dueTime: 500, period: 250);

        try
        {
            CGEventTapEnable(tap, true);
            CFRunLoopRun();
        }
        finally
        {
            _isRemoteActive = false;
            if (source != IntPtr.Zero) CFRelease(source);
            if (tap != IntPtr.Zero) CFRelease(tap);
            _runLoop = IntPtr.Zero;
        }
    }

    private IntPtr EventTapCallback(IntPtr proxy, uint type, IntPtr cgEvent, IntPtr userInfo)
    {
        if (_isRemoteActive && !IsPeerAlive())
        {
            TerminateSessionAndReleaseInput("Conexão perdida detectada no callback CGEventTap");
            return cgEvent;
        }

        if (type == kCGEventKeyDown || type == kCGEventKeyUp)
        {
            bool isDown = type == kCGEventKeyDown;
            ushort keycode = (ushort)CGEventGetIntegerValueField(cgEvent, kCGKeyboardEventKeycode);
            if (isDown && keycode == MacKeyF12)
            {
                SetRemoteActive(!_isRemoteActive);
                return IntPtr.Zero;
            }

            if (_isRemoteActive)
            {
                OnOutgoingHidFrame?.Invoke(HidFrame.EncodeKey(keycode, isDown));
                return IntPtr.Zero;
            }
            return cgEvent;
        }

        if (type == kCGEventMouseMoved)
        {
            CGPoint loc = CGEventGetLocation(cgEvent);
            int x = (int)loc.X;
            int y = (int)loc.Y;

            if (!_isRemoteActive)
            {
                bool crossed = _activeCrossingEdge switch
                {
                    ScreenEdge.Right => x >= _screenWidth - 2,
                    ScreenEdge.Left => x <= 1,
                    ScreenEdge.Bottom => y >= _screenHeight - 2,
                    ScreenEdge.Top => y <= 1,
                    _ => x >= _screenWidth - 2
                };

                if (crossed)
                {
                    _anchorX = Math.Clamp(x, 4, _screenWidth - 4);
                    _anchorY = Math.Clamp(y, 4, _screenHeight - 4);
                    SetRemoteActive(true);
                    CGWarpMouseCursorPosition(new CGPoint { X = _anchorX, Y = _anchorY });
                    return IntPtr.Zero;
                }
            }
            else
            {
                short dx = (short)Math.Clamp(x - _anchorX, -500, 500);
                short dy = (short)Math.Clamp(y - _anchorY, -500, 500);
                if (dx != 0 || dy != 0)
                {
                    OnOutgoingHidFrame?.Invoke(HidFrame.EncodeMouseMove(dx, dy));
                    CGWarpMouseCursorPosition(new CGPoint { X = _anchorX, Y = _anchorY });
                }
                return IntPtr.Zero;
            }
        }
        else if (_isRemoteActive)
        {
            switch (type)
            {
                case kCGEventLeftMouseDown:
                    OnOutgoingHidFrame?.Invoke(HidFrame.EncodeMouseButton(1, true));
                    return IntPtr.Zero;
                case kCGEventLeftMouseUp:
                    OnOutgoingHidFrame?.Invoke(HidFrame.EncodeMouseButton(1, false));
                    return IntPtr.Zero;
                case kCGEventRightMouseDown:
                    OnOutgoingHidFrame?.Invoke(HidFrame.EncodeMouseButton(2, true));
                    return IntPtr.Zero;
                case kCGEventRightMouseUp:
                    OnOutgoingHidFrame?.Invoke(HidFrame.EncodeMouseButton(2, false));
                    return IntPtr.Zero;
            }
        }

        return cgEvent;
    }

    public bool InjectHidEvent(HidEvent ev, ScreenEdge returnEdge, out bool hitReturnEdge, out ushort returnNormalizedCoord)
    {
        hitReturnEdge = false;
        returnNormalizedCoord = 5000;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return false;

        (_screenWidth, _screenHeight) = GetPrimaryScreenBounds();

        switch (ev.Type)
        {
            case HidFrame.MouseMove:
            {
                IntPtr dummy = CGEventCreate(IntPtr.Zero);
                if (dummy == IntPtr.Zero) return false;
                CGPoint cur = CGEventGetLocation(dummy);
                CFRelease(dummy);

                double nx = Math.Clamp(cur.X + ev.Arg1, 0, _screenWidth);
                double ny = Math.Clamp(cur.Y + ev.Arg2, 0, _screenHeight);
                CGWarpMouseCursorPosition(new CGPoint { X = nx, Y = ny });

                switch (returnEdge)
                {
                    case ScreenEdge.Left when ev.Arg1 < 0 && nx <= 0:
                        CGWarpMouseCursorPosition(new CGPoint { X = 10, Y = ny });
                        hitReturnEdge = true;
                        returnNormalizedCoord = EdgeCoordinateMapper.Normalize((int)ny, _screenHeight);
                        break;
                    case ScreenEdge.Right when ev.Arg1 > 0 && nx >= _screenWidth - 2:
                        CGWarpMouseCursorPosition(new CGPoint { X = _screenWidth - 10, Y = ny });
                        hitReturnEdge = true;
                        returnNormalizedCoord = EdgeCoordinateMapper.Normalize((int)ny, _screenHeight);
                        break;
                    case ScreenEdge.Top when ev.Arg2 < 0 && ny <= 0:
                        CGWarpMouseCursorPosition(new CGPoint { X = nx, Y = 10 });
                        hitReturnEdge = true;
                        returnNormalizedCoord = EdgeCoordinateMapper.Normalize((int)nx, _screenWidth);
                        break;
                    case ScreenEdge.Bottom when ev.Arg2 > 0 && ny >= _screenHeight - 2:
                        CGWarpMouseCursorPosition(new CGPoint { X = nx, Y = _screenHeight - 10 });
                        hitReturnEdge = true;
                        returnNormalizedCoord = EdgeCoordinateMapper.Normalize((int)nx, _screenWidth);
                        break;
                }
                return true;
            }

            case HidFrame.MouseButton:
            {
                IntPtr dummy = CGEventCreate(IntPtr.Zero);
                if (dummy == IntPtr.Zero) return false;
                CGPoint cur = CGEventGetLocation(dummy);
                CFRelease(dummy);

                bool down = ev.Arg2 == 1;
                uint eventType = ev.Arg1 == 2
                    ? (down ? kCGEventRightMouseDown : kCGEventRightMouseUp)
                    : (down ? kCGEventLeftMouseDown : kCGEventLeftMouseUp);
                uint mouseBtn = ev.Arg1 == 2 ? 1u : 0u;

                IntPtr mEv = CGEventCreateMouseEvent(IntPtr.Zero, eventType, cur, mouseBtn);
                if (mEv == IntPtr.Zero) return false;
                CGEventPost(kCGHIDEventTap, mEv);
                CFRelease(mEv);
                return true;
            }

            case HidFrame.KeyEvent:
            {
                IntPtr kEv = CGEventCreateKeyboardEvent(IntPtr.Zero, (ushort)ev.Arg1, ev.Arg2 == 1);
                if (kEv == IntPtr.Zero) return false;
                CGEventPost(kCGHIDEventTap, kEv);
                CFRelease(kEv);
                return true;
            }

            default:
                return false;
        }
    }

    public void ReleaseAllModifierKeysOnReceiver()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;
        // Command (0x37), Shift (0x38), Option (0x3A), Control (0x3B)
        ushort[] macModKeycodes = [0x37, 0x38, 0x3A, 0x3B];
        foreach (ushort kc in macModKeycodes)
        {
            InjectHidEvent(new HidEvent(HidFrame.KeyEvent, (short)kc, 0), ScreenEdge.Left, out _, out _);
        }
    }

    public (int Width, int Height) GetPrimaryScreenBounds()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return (1920, 1080);

        try
        {
            uint mainId = CGMainDisplayID();
            int w = (int)CGDisplayPixelsWide(mainId);
            int h = (int)CGDisplayPixelsHigh(mainId);
            if (w > 0 && h > 0) return (w, h);
        }
        catch
        {
        }
        return (1920, 1080);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X;
        public double Y;
    }

    private delegate IntPtr CGEventTapCallBack(IntPtr proxy, uint type, IntPtr cgEvent, IntPtr userInfo);

    [DllImport(AppServices)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AXIsProcessTrusted();

    [DllImport(AppServices)]
    private static extern uint CGMainDisplayID();

    [DllImport(AppServices)]
    private static extern nuint CGDisplayPixelsWide(uint display);

    [DllImport(AppServices)]
    private static extern nuint CGDisplayPixelsHigh(uint display);

    [DllImport(AppServices)]
    private static extern IntPtr CGEventTapCreate(
        uint tap,
        uint place,
        uint options,
        ulong eventsOfInterest,
        CGEventTapCallBack callback,
        IntPtr userInfo);

    [DllImport(AppServices)]
    private static extern void CGEventTapEnable(IntPtr tap, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport(AppServices)]
    private static extern IntPtr CGEventCreate(IntPtr source);

    [DllImport(AppServices)]
    private static extern CGPoint CGEventGetLocation(IntPtr cgEvent);

    [DllImport(AppServices)]
    private static extern long CGEventGetIntegerValueField(IntPtr cgEvent, long field);

    [DllImport(AppServices)]
    private static extern int CGWarpMouseCursorPosition(CGPoint newCursorPosition);

    [DllImport(AppServices)]
    private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, uint mouseType, CGPoint mouseCursorPosition, uint mouseButton);

    [DllImport(AppServices)]
    private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, [MarshalAs(UnmanagedType.Bool)] bool keyDown);

    [DllImport(AppServices)]
    private static extern void CGEventPost(uint tap, IntPtr cgEvent);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFMachPortCreateRunLoopSource(IntPtr allocator, IntPtr port, nint order);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFRunLoopGetCurrent();

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopRun();

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopStop(IntPtr rl);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);
}

