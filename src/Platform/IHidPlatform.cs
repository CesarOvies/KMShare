using System.Buffers.Binary;
using System.Runtime.InteropServices;
using KMShare.Security;

namespace KMShare.Platform;

public enum ScreenEdge : byte
{
    Left = 0,
    Right = 1,
    Top = 2,
    Bottom = 3
}

public static class EdgeCoordinateMapper
{
    public const int ScaleMax = 10_000;

    public static ScreenEdge GetOppositeEdge(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Left => ScreenEdge.Right,
        ScreenEdge.Right => ScreenEdge.Left,
        ScreenEdge.Top => ScreenEdge.Bottom,
        ScreenEdge.Bottom => ScreenEdge.Top,
        _ => ScreenEdge.Left
    };

    public static ushort Normalize(int coordinate, int screenSpan)
    {
        if (screenSpan <= 1) return 5000;
        int clamped = Math.Clamp(coordinate, 0, screenSpan);
        return (ushort)Math.Clamp((clamped * ScaleMax) / screenSpan, 0, ScaleMax);
    }

    public static int Denormalize(ushort normalized, int targetScreenSpan)
    {
        if (targetScreenSpan <= 1) return 0;
        int normClamped = Math.Clamp((int)normalized, 0, ScaleMax);
        return Math.Clamp((normClamped * targetScreenSpan) / ScaleMax, 0, targetScreenSpan - 1);
    }

    public static (int X, int Y) MapEntryPosition(
        ScreenEdge senderExitEdge,
        ushort normalizedCoord,
        int targetWidth,
        int targetHeight,
        int marginPx = 10)
    {
        int w = Math.Max(100, targetWidth);
        int h = Math.Max(100, targetHeight);
        int m = Math.Clamp(marginPx, 2, 50);

        return senderExitEdge switch
        {
            ScreenEdge.Right => (m, Denormalize(normalizedCoord, h)),
            ScreenEdge.Left => (w - m, Denormalize(normalizedCoord, h)),
            ScreenEdge.Bottom => (Denormalize(normalizedCoord, w), m),
            ScreenEdge.Top => (Denormalize(normalizedCoord, w), h - m),
            _ => (m, Denormalize(normalizedCoord, h))
        };
    }

    public static byte[] EncodeWireEdgePacket(byte wireType, ScreenEdge edge, ushort normalizedCoord)
    {
        byte[] packet = new byte[4];
        packet[0] = wireType;
        packet[1] = (byte)edge;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), normalizedCoord);
        return packet;
    }

    public static bool TryDecodeWireEdgePacket(
        ReadOnlySpan<byte> packet,
        byte expectedWireType,
        ScreenEdge defaultEdge,
        out ScreenEdge edge,
        out ushort normalizedCoord)
    {
        edge = defaultEdge;
        normalizedCoord = 5000;

        if (packet.Length < 1 || packet[0] != expectedWireType)
            return false;

        if (packet.Length >= 4 && packet[1] <= (byte)ScreenEdge.Bottom)
        {
            edge = (ScreenEdge)packet[1];
            normalizedCoord = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(2, 2)), (ushort)ScaleMax);
        }

        return true;
    }
}

public interface IHidPlatform
{
    event Action<byte[]>? OnOutgoingHidFrame;
    event Action<bool>? OnRemoteStateChanged;
    event Action<ScreenEdge, ushort>? OnEdgeCrossed;

    bool IsRemoteActive { get; }
    ScreenEdge ActiveCrossingEdge { get; set; }

    void NotifyPeerAlive();
    bool IsPeerAlive();
    void SetRemoteActive(bool active, ushort? returnNormalizedCoord = null);
    void TerminateSessionAndReleaseInput(string reason);
    void RunSenderCaptureLoop(CancellationToken ct);
    bool InjectHidEvent(HidEvent ev, ScreenEdge returnEdge, out bool hitReturnEdge, out ushort returnNormalizedCoord);
    void WarpCursorOnEdgeEnter(ScreenEdge senderExitEdge, ushort normalizedCoord);
    void ReleaseAllModifierKeysOnReceiver();
    (int Width, int Height) GetPrimaryScreenBounds();
}

public static class HidPlatformFactory
{
    public static IHidPlatform GetCurrent()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return WindowsHidEngine.Instance;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return LinuxHidEngine.Instance;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return MacHidEngine.Instance;

        return WindowsHidEngine.Instance;
    }
}

