using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using MKShare.Platform;
using MKShare.Security;

namespace MKShare.Network;

public record BluetoothPeerDevice(string Name, string MacAddress, ulong RawAddress, bool IsConnected, bool IsAuthenticated);

public static class BluetoothRfcommNode
{
    private const AddressFamily AF_BTH_WINDOWS = (AddressFamily)32;
    private const AddressFamily AF_BLUETOOTH_LINUX = (AddressFamily)31;
    private const ProtocolType BTHPROTO_RFCOMM = (ProtocolType)3;
    public const uint DefaultRfcommChannel = 5;

    private const byte WireHello = 0x10;
    private const byte WireHelloAck = 0x11;
    private const byte WireHidPacket = 0x20;
    private const byte WireEdgeEnter = 0x2F;
    private const byte WireEdgeReturn = 0x30;
    private const byte WireHeartbeatPing = 0x40;
    private const byte WireHeartbeatAck = 0x41;
    private const byte WireRevoked = 0xFF;

    private static AddressFamily PlatformBtFamily =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? AF_BLUETOOTH_LINUX : AF_BTH_WINDOWS;

    public static string GetLocalBluetoothMacAddress()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var findParams = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
            IntPtr hFind = BluetoothFindFirstRadio(ref findParams, out IntPtr hRadio);
            if (hFind == IntPtr.Zero) return "N/A";

            try
            {
                var info = new BLUETOOTH_RADIO_INFO { dwSize = Marshal.SizeOf<BLUETOOTH_RADIO_INFO>() };
                if (BluetoothGetRadioInfo(hRadio, ref info) == 0)
                {
                    return FormatBtAddress(info.address);
                }
                return "N/A";
            }
            finally
            {
                CloseHandle(hRadio);
                BluetoothFindRadioClose(hFind);
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                const string sysPath = "/sys/class/bluetooth/hci0/address";
                if (File.Exists(sysPath))
                {
                    return File.ReadAllText(sysPath).Trim().ToUpperInvariant();
                }
            }
            catch
            {
            }
            return "00:00:00:00:00:00";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo("system_profiler", "SPBluetoothDataType")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                proc?.WaitForExit(2000);
                if (proc?.ExitCode == 0)
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    foreach (string line in output.Split('\n'))
                    {
                        if (line.Contains("Address:", StringComparison.OrdinalIgnoreCase))
                        {
                            int idx = line.IndexOf(':');
                            if (idx >= 0) return line[(idx + 1)..].Trim().ToUpperInvariant();
                        }
                    }
                }
            }
            catch
            {
            }
            return "00:00:00:00:00:00";
        }

        return "00:00:00:00:00:00";
    }

    public static List<BluetoothPeerDevice> GetPairedBluetoothDevices()
    {
        var list = new List<BluetoothPeerDevice>();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return list;

        var searchParams = new BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            dwSize = Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
            fReturnAuthenticated = true,
            fReturnRemembered = true,
            fReturnUnknown = false,
            fReturnConnected = true,
            fIssueInquiry = false,
            cTimeoutMultiplier = 1,
            hRadio = IntPtr.Zero
        };

        var devInfo = new BLUETOOTH_DEVICE_INFO { dwSize = Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>() };
        IntPtr hFind = BluetoothFindFirstDevice(ref searchParams, ref devInfo);
        if (hFind == IntPtr.Zero) return list;

        try
        {
            do
            {
                list.Add(new BluetoothPeerDevice(
                    Name: string.IsNullOrWhiteSpace(devInfo.szName) ? "Unknown" : devInfo.szName,
                    MacAddress: FormatBtAddress(devInfo.Address),
                    RawAddress: devInfo.Address,
                    IsConnected: devInfo.fConnected,
                    IsAuthenticated: devInfo.fAuthenticated));

                devInfo = new BLUETOOTH_DEVICE_INFO { dwSize = Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>() };
            }
            while (BluetoothFindNextDevice(hFind, ref devInfo));
        }
        finally
        {
            BluetoothFindDeviceClose(hFind);
        }

        return list;
    }

    public static Socket CreatePlatformRfcommSocket()
    {
        return new Socket(PlatformBtFamily, SocketType.Stream, BTHPROTO_RFCOMM);
    }

    public static EndPoint CreatePlatformRfcommEndPoint(ulong btAddr, uint channel)
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? new LinuxBlueZRfcommEndPoint(btAddr, (byte)channel)
            : new WindowsBluetoothEndPoint(btAddr, channel);
    }

    public static async Task RunBluetoothReceiverAsync(string pin, CancellationToken ct)
    {
        IHidPlatform hid = HidPlatformFactory.GetCurrent();
        string localBtMac = GetLocalBluetoothMacAddress();
        using var listenSocket = CreatePlatformRfcommSocket();
        listenSocket.Bind(CreatePlatformRfcommEndPoint(0, DefaultRfcommChannel));
        listenSocket.Listen(2);

        Console.WriteLine($"[MKShare BLUETOOTH RECEIVER] Escutando em {localBtMac} (Canal RFCOMM {DefaultRfcommChannel})");

        using var reg = ct.Register(() => listenSocket.Close());

        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listenSocket.AcceptAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                continue;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                using (var stream = new NetworkStream(client, ownsSocket: false))
                using (var session = new SecureHidSession(maxDistinctKeystrokesPerSecond: 30))
                {
                    ScreenEdge activeReturnEdge = ScreenEdge.Left;
                    try
                    {
                        Console.WriteLine("[+] Conexão Bluetooth RFCOMM recebida! Validando handshake ECDH...");

                        byte[]? helloFrame = await ReadFrameAsync(stream, ct);
                        if (helloFrame == null || helloFrame[0] != WireHello) return;

                        var (ownEcdh, ownPub) = SecureHidSession.CreateHandshakeKeyPair();
                        using (ownEcdh)
                        {
                            session.EstablishSession(ownEcdh, helloFrame.AsSpan(1), pin, peerId: "bt-peer");
                        }

                        await WriteFrameAsync(stream, [WireHelloAck, .. ownPub], ct);
                        Console.WriteLine("[+] Handshake ECDH P-256 + AES-256-GCM concluído via Bluetooth!");

                        while (!ct.IsCancellationRequested)
                        {
                            byte[]? frame = await ReadFrameAsync(stream, ct);
                            if (frame == null || frame.Length < 1) break;

                            if (frame[0] == WireHeartbeatPing && session.IsAuthenticated)
                            {
                                await WriteFrameAsync(stream, [WireHeartbeatAck], ct);
                            }
                            else if (frame[0] == WireEdgeEnter && session.IsAuthenticated)
                            {
                                if (EdgeCoordinateMapper.TryDecodeWireEdgePacket(frame, WireEdgeEnter, ScreenEdge.Right, out ScreenEdge senderExitEdge, out ushort normCoord))
                                {
                                    activeReturnEdge = EdgeCoordinateMapper.GetOppositeEdge(senderExitEdge);
                                    hid.WarpCursorOnEdgeEnter(senderExitEdge, normCoord);
                                }
                            }
                            else if (frame[0] == WireHidPacket && frame.Length >= 2)
                            {
                                var status = session.OpenAndValidatePacket(frame.AsSpan(1), out HidEvent ev);
                                if (status == PacketStatus.Accepted)
                                {
                                    hid.InjectHidEvent(ev, activeReturnEdge, out bool hitReturnEdge, out ushort returnNormCoord);
                                    if (hitReturnEdge)
                                    {
                                        hid.ReleaseAllModifierKeysOnReceiver();
                                        byte[] edgeMsg = EdgeCoordinateMapper.EncodeWireEdgePacket(WireEdgeReturn, activeReturnEdge, returnNormCoord);
                                        await WriteFrameAsync(stream, edgeMsg, ct);
                                        Console.WriteLine($"[<-] Cursor tocou a borda ({activeReturnEdge}): devolvendo controle para o PC principal.");
                                    }
                                }
                                else
                                {
                                    hid.ReleaseAllModifierKeysOnReceiver();
                                    Console.WriteLine($"[!] ALERTA DE SEGURANÇA BLUETOOTH: Pacote rejeitado ({status})! Sessão revogada.");
                                    await WriteFrameAsync(stream, [WireRevoked, (byte)status], ct);
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        hid.ReleaseAllModifierKeysOnReceiver();
                        Console.WriteLine($"[-] Sessão Bluetooth encerrada ({ex.Message}). Aguardando nova conexão...");
                    }
                }
            }, ct);
        }
    }

    public static async Task RunBluetoothSenderAsync(string targetBtMac, string pin, CancellationToken ct)
    {
        IHidPlatform hid = HidPlatformFactory.GetCurrent();
        ulong btAddr = ParseBtAddress(targetBtMac);
        using var socket = CreatePlatformRfcommSocket();

        Console.WriteLine("=======================================================================");
        Console.WriteLine($"[MKShare BLUETOOTH SENDER - Modo C] Conectando a {targetBtMac} (Canal {DefaultRfcommChannel})...");
        Console.WriteLine("=======================================================================");

        socket.Connect(CreatePlatformRfcommEndPoint(btAddr, DefaultRfcommChannel));
        using var stream = new NetworkStream(socket, ownsSocket: false);
        using var session = new SecureHidSession(maxDistinctKeystrokesPerSecond: 30);

        var (ownEcdh, ownPub) = SecureHidSession.CreateHandshakeKeyPair();
        using (ownEcdh)
        {
            await WriteFrameAsync(stream, [WireHello, .. ownPub], ct);
            byte[]? ack = await ReadFrameAsync(stream, ct);
            if (ack == null || ack.Length < 2 || ack[0] != WireHelloAck)
            {
                Console.WriteLine("[!] Falha no handshake Bluetooth.");
                return;
            }
            session.EstablishSession(ownEcdh, ack.AsSpan(1), pin, peerId: targetBtMac);
        }

        hid.NotifyPeerAlive();
        Console.WriteLine($"[+] Conectado e autenticado via Bluetooth com {targetBtMac}!");

        var coalescer = new BluetoothMouseCoalescer();
        object syncLock = new();

        Action<bool> stateHandler = isRemote =>
        {
            Console.WriteLine(isRemote
                ? $"[->] CRUZOU A TELA (Bluetooth - {hid.ActiveCrossingEdge}): Controlando {targetBtMac}! (F12 para voltar)"
                : "[<-] VOLTOU: Controle local restaurado.");
        };

        Action<ScreenEdge, ushort> edgeCrossHandler = (edge, normCoord) =>
        {
            try
            {
                byte[] edgeEnter = EdgeCoordinateMapper.EncodeWireEdgePacket(WireEdgeEnter, edge, normCoord);
                lock (syncLock)
                {
                    Span<byte> lenPrefix = stackalloc byte[2];
                    BinaryPrimitives.WriteUInt16LittleEndian(lenPrefix, (ushort)edgeEnter.Length);
                    stream.Write(lenPrefix);
                    stream.Write(edgeEnter);
                }
            }
            catch
            {
            }
        };

        Action<byte[]> hidHandler = hidFrame =>
        {
            if (!session.IsAuthenticated)
            {
                hid.TerminateSessionAndReleaseInput("Sessão Bluetooth não autenticada");
                return;
            }

            if (hidFrame[1] == HidFrame.MouseMove)
            {
                short dx = BinaryPrimitives.ReadInt16LittleEndian(hidFrame.AsSpan(2, 2));
                short dy = BinaryPrimitives.ReadInt16LittleEndian(hidFrame.AsSpan(4, 2));
                lock (syncLock)
                {
                    coalescer.PushRawMouseMove(dx, dy);
                }
            }
            else
            {
                SendSealedHidNow(stream, session, hid, hidFrame, syncLock);
            }
        };

        hid.OnRemoteStateChanged += stateHandler;
        hid.OnEdgeCrossed += edgeCrossHandler;
        hid.OnOutgoingHidFrame += hidHandler;

        try
        {
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested && session.IsAuthenticated)
                {
                    await Task.Delay(7, ct);
                    byte[]? coalesced = null;
                    lock (syncLock)
                    {
                        if (coalescer.TryFlushCoalescedFrame(out byte[] f, out _))
                            coalesced = f;
                    }
                    if (coalesced != null && session.IsAuthenticated)
                    {
                        SendSealedHidNow(stream, session, hid, coalesced, syncLock);
                    }
                }
            }, ct);

            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested && session.IsAuthenticated)
                {
                    try
                    {
                        lock (syncLock)
                        {
                            Span<byte> buf = [0x01, 0x00, WireHeartbeatPing];
                            stream.Write(buf);
                        }
                        await Task.Delay(500, ct);
                    }
                    catch
                    {
                        hid.TerminateSessionAndReleaseInput("Queda no stream Bluetooth durante heartbeat");
                        break;
                    }
                }
            }, ct);

            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        byte[]? rx = await ReadFrameAsync(stream, ct);
                        if (rx == null || rx.Length < 1)
                        {
                            hid.TerminateSessionAndReleaseInput("Conexão Bluetooth fechada pelo outro PC");
                            break;
                        }

                        if (rx[0] == WireHeartbeatAck)
                        {
                            hid.NotifyPeerAlive();
                        }
                        else if (rx[0] == WireEdgeReturn)
                        {
                            hid.NotifyPeerAlive();
                            EdgeCoordinateMapper.TryDecodeWireEdgePacket(rx, WireEdgeReturn, ScreenEdge.Left, out _, out ushort retNorm);
                            hid.SetRemoteActive(false, retNorm);
                        }
                        else if (rx[0] == WireRevoked)
                        {
                            var reason = rx.Length >= 2 ? ((PacketStatus)rx[1]).ToString() : "Revoked";
                            session.RevokeAuthentication();
                            hid.TerminateSessionAndReleaseInput($"Sessão Bluetooth revogada pelo receptor ({reason})");
                            break;
                        }
                    }
                    catch
                    {
                        hid.TerminateSessionAndReleaseInput("Leitura Bluetooth interrompida");
                        break;
                    }
                }
            }, ct);

            hid.RunSenderCaptureLoop(ct);
        }
        finally
        {
            hid.OnRemoteStateChanged -= stateHandler;
            hid.OnEdgeCrossed -= edgeCrossHandler;
            hid.OnOutgoingHidFrame -= hidHandler;
        }
    }

    private static void SendSealedHidNow(NetworkStream stream, SecureHidSession session, IHidPlatform hid, byte[] hidFrame, object syncLock)
    {
        try
        {
            lock (syncLock)
            {
                byte[] sealedPacket = session.SealFrame(hidFrame);
                byte[] payload = [WireHidPacket, .. sealedPacket];
                Span<byte> lenPrefix = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(lenPrefix, (ushort)payload.Length);
                stream.Write(lenPrefix);
                stream.Write(payload);
            }
        }
        catch
        {
            hid.TerminateSessionAndReleaseInput("Falha ao enviar pacote Bluetooth");
        }
    }

    private static async Task WriteFrameAsync(NetworkStream stream, byte[] payload, CancellationToken ct)
    {
        byte[] prefix = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(prefix, (ushort)payload.Length);
        await stream.WriteAsync(prefix, ct);
        await stream.WriteAsync(payload, ct);
    }

    private static async Task<byte[]?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] prefix = new byte[2];
        int r = await stream.ReadAtLeastAsync(prefix, 2, throwOnEndOfStream: false, ct);
        if (r < 2) return null;
        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(prefix);
        byte[] buf = new byte[len];
        int r2 = await stream.ReadAtLeastAsync(buf, len, throwOnEndOfStream: false, ct);
        return r2 < len ? null : buf;
    }

    private static string FormatBtAddress(ulong addr)
    {
        byte[] b = BitConverter.GetBytes(addr);
        return $"{b[5]:X2}:{b[4]:X2}:{b[3]:X2}:{b[2]:X2}:{b[1]:X2}:{b[0]:X2}";
    }

    public static ulong ParseBtAddress(string mac)
    {
        string clean = mac.Replace(":", "").Replace("-", "").Trim();
        return ulong.Parse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public sealed class WindowsBluetoothEndPoint(ulong btAddr, uint channel) : EndPoint
    {
        public override AddressFamily AddressFamily => AF_BTH_WINDOWS;

        public override SocketAddress Serialize()
        {
            var sa = new SocketAddress(AF_BTH_WINDOWS, 30);
            Span<byte> buf = stackalloc byte[30];
            BinaryPrimitives.WriteUInt16LittleEndian(buf[..2], 32);
            BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(2, 8), btAddr);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(26, 4), channel);
            for (int i = 0; i < 30; i++) sa[i] = buf[i];
            return sa;
        }

        public override EndPoint Create(SocketAddress socketAddress)
        {
            Span<byte> buf = stackalloc byte[30];
            for (int i = 0; i < Math.Min(30, socketAddress.Size); i++) buf[i] = socketAddress[i];
            ulong addr = BinaryPrimitives.ReadUInt64LittleEndian(buf.Slice(2, 8));
            uint port = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(26, 4));
            return new WindowsBluetoothEndPoint(addr, port);
        }
    }

    /// <summary>
    /// Linux BlueZ sockaddr_rc (AF_BLUETOOTH = 31, BTPROTO_RFCOMM = 3):
    /// [0..1] sa_family = 31 (uint16 LE), [2..7] bdaddr (6 bytes LE), [8] rc_channel (uint8)
    /// </summary>
    public sealed class LinuxBlueZRfcommEndPoint(ulong btAddr, byte channel) : EndPoint
    {
        public override AddressFamily AddressFamily => AF_BLUETOOTH_LINUX;

        public override SocketAddress Serialize()
        {
            var sa = new SocketAddress(AF_BLUETOOTH_LINUX, 10);
            Span<byte> buf = stackalloc byte[10];
            BinaryPrimitives.WriteUInt16LittleEndian(buf[..2], 31);
            Span<byte> addrBytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(addrBytes, btAddr);
            addrBytes[..6].CopyTo(buf.Slice(2, 6));
            buf[8] = channel;
            for (int i = 0; i < 10; i++) sa[i] = buf[i];
            return sa;
        }

        public override EndPoint Create(SocketAddress socketAddress)
        {
            Span<byte> addrBytes = stackalloc byte[8];
            for (int i = 0; i < 6 && i + 2 < socketAddress.Size; i++) addrBytes[i] = socketAddress[i + 2];
            ulong addr = BinaryPrimitives.ReadUInt64LittleEndian(addrBytes);
            byte ch = socketAddress.Size > 8 ? socketAddress[8] : (byte)DefaultRfcommChannel;
            return new LinuxBlueZRfcommEndPoint(addr, ch);
        }
    }

    /// <summary>
    /// macOS IOBluetooth RFCOMM user-space channel wrapper (IOBluetoothDeviceOpenRFCOMMChannelSync).
    /// </summary>
    public static class MacIoBluetoothRfcomm
    {
        private const string IoBluetoothFramework = "/System/Library/Frameworks/IOBluetooth.framework/IOBluetooth";

        public static bool IsAvailable()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return false;
            if (NativeLibrary.TryLoad(IoBluetoothFramework, out IntPtr handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_FIND_RADIO_PARAMS { public int dwSize; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_RADIO_INFO
    {
        public int dwSize;
        public ulong address;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string szName;
        public uint ulClassofDevice;
        public ushort lmpSubversion;
        public ushort manufacturer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
    {
        public int dwSize;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnUnknown;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_DEVICE_INFO
    {
        public int dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
        public SYSTEMTIME stLastSeen;
        public SYSTEMTIME stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string szName;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern int BluetoothGetRadioInfo(IntPtr hRadio, ref BLUETOOTH_RADIO_INFO pRadioInfo);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindRadioClose(IntPtr hFind);

    [DllImport("bthprops.cpl", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS pbtsp, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("bthprops.cpl", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindDeviceClose(IntPtr hFind);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
