using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using KMShare.Platform;
using KMShare.Security;

namespace KMShare.Network;

public sealed record PeerDiscoveryAnnouncement(
    string Hostname,
    string LanIp,
    string BluetoothMac,
    string Role,
    int UdpPort,
    int FilePort,
    string MonitorEdge);

public sealed record DiscoveredNetworkPeer(
    string Hostname,
    string IpAddress,
    string PhysicalMac,
    string BluetoothMac,
    string RoleDescription,
    bool IsKmShareResponding,
    bool Layer2ArpAlive,
    bool Layer3Reachable,
    bool IsBluetoothPaired,
    VpnFallbackStrategy RecommendedStrategy,
    string StatusSummary);

public static class NetworkPeerDiscovery
{
    public const int DefaultDiscoveryUdpPort = 51822;
    public const byte WireDiscoveryProbe = 0x01;
    public const byte WireDiscoveryAnnounce = 0x02;
    private static readonly byte[] DiscoveryMagicBytes = "KMSHARE_DISCOVERY_V1"u8.ToArray();

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] pMacAddr, ref int phyAddrLen);

    public static void DisableWindowsUdpConnReset(UdpClient udp)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                udp.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
            }
            catch
            {
            }
        }
    }

    public static byte[] BuildDiscoveryProbePacket()
    {
        return [WireDiscoveryProbe, .. DiscoveryMagicBytes];
    }

    public static bool IsDiscoveryProbePacket(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 1 + DiscoveryMagicBytes.Length || buffer[0] != WireDiscoveryProbe)
            return false;

        return buffer.Slice(1, DiscoveryMagicBytes.Length).SequenceEqual(DiscoveryMagicBytes);
    }

    public static byte[] BuildDiscoveryAnnouncePacket(
        IPAddress localLanIp,
        string localBtMac,
        string role,
        ScreenEdge monitorEdge,
        int udpPort = KMSharePeerNode.DefaultUdpPort,
        int filePort = 51821)
    {
        var payload = new PeerDiscoveryAnnouncement(
            Hostname: Environment.MachineName,
            LanIp: localLanIp.ToString(),
            BluetoothMac: localBtMac,
            Role: role,
            UdpPort: udpPort,
            FilePort: filePort,
            MonitorEdge: monitorEdge.ToString());

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload);
        return [WireDiscoveryAnnounce, .. json];
    }

    public static bool TryParseDiscoveryAnnouncePacket(
        ReadOnlySpan<byte> buffer,
        out PeerDiscoveryAnnouncement? announcement)
    {
        announcement = null;
        if (buffer.Length < 3 || buffer[0] != WireDiscoveryAnnounce)
            return false;

        try
        {
            announcement = JsonSerializer.Deserialize<PeerDiscoveryAnnouncement>(buffer[1..]);
            return announcement != null && !string.IsNullOrWhiteSpace(announcement.LanIp);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs a background UDP responder on port 51822 so other KMShare devices on the LAN
    /// can find this device (Hostname, LAN IP, Bluetooth MAC, active Role) with 1 click.
    /// </summary>
    public static async Task RunBackgroundDiscoveryResponderAsync(
        Func<IPAddress> getLocalLanIp,
        Func<string> getLocalBtMac,
        Func<string> getCurrentRole,
        Func<ScreenEdge> getMonitorEdge,
        CancellationToken ct,
        int listenPort = DefaultDiscoveryUdpPort)
    {
        IPAddress bindIp = getLocalLanIp();
        IntranetGuard.EnforceLanEndpoint(new IPEndPoint(bindIp, listenPort));

        UdpClient? udp = null;
        try
        {
            udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, listenPort));
            DisableWindowsUdpConnReset(udp);

            while (!ct.IsCancellationRequested)
            {
                var res = await udp.ReceiveAsync(ct);
                if (!IntranetGuard.IsStrictlyLanIp(res.RemoteEndPoint.Address))
                    continue;

                if (IsDiscoveryProbePacket(res.Buffer))
                {
                    IPAddress currentLanIp = getLocalLanIp();
                    IntranetGuard.EnforceLanEndpoint(res.RemoteEndPoint);
                    byte[] reply = BuildDiscoveryAnnouncePacket(
                        currentLanIp,
                        getLocalBtMac(),
                        getCurrentRole(),
                        getMonitorEdge());
                    await udp.SendAsync(reply, res.RemoteEndPoint, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Port may already be bound by another local instance; ignore gracefully
        }
        finally
        {
            udp?.Dispose();
        }
    }

    /// <summary>
    /// Probes a single endpoint (e.g. Loopback or specific LAN peer) for a KMShare discovery announcement.
    /// </summary>
    public static async Task<PeerDiscoveryAnnouncement?> ProbeSingleEndpointAsync(
        IPAddress localBindIp,
        IPEndPoint targetEndpoint,
        int timeoutMs = 800,
        CancellationToken ct = default)
    {
        IntranetGuard.EnforceLanEndpoint(new IPEndPoint(localBindIp, 0));
        IntranetGuard.EnforceLanEndpoint(targetEndpoint);

        using var udp = new UdpClient(new IPEndPoint(localBindIp, 0));
        DisableWindowsUdpConnReset(udp);

        byte[] probe = BuildDiscoveryProbePacket();
        await udp.SendAsync(probe, targetEndpoint, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            var res = await udp.ReceiveAsync(timeoutCts.Token);
            IntranetGuard.EnforceLanEndpoint(res.RemoteEndPoint);
            if (TryParseDiscoveryAnnouncePacket(res.Buffer, out var ann))
            {
                return ann;
            }
        }
        catch
        {
        }

        return null;
    }

    /// <summary>
    /// Performs a comprehensive Intranet & Bluetooth scan:
    /// 1. KMShare UDP Discovery Beacon across the /24 subnet (:51820 and :51822)
    /// 2. Layer 2 SendARP + Layer 3 ICMP Ping scan across the physical /24 subnet (detects peers even under VPN Kill-Switch)
    /// 3. Paired Bluetooth devices enumeration for Out-of-Band Mode C
    /// </summary>
    public static async Task<List<DiscoveredNetworkPeer>> ScanNetworkAndBluetoothPeersAsync(
        IPAddress localPhysicalIp,
        Action<string>? onProgressLog = null,
        CancellationToken ct = default)
    {
        IntranetGuard.EnforceLanEndpoint(new IPEndPoint(localPhysicalIp, 0));

        var kmSharePeers = new ConcurrentDictionary<string, PeerDiscoveryAnnouncement>(StringComparer.OrdinalIgnoreCase);
        var l2MacByIp = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var l3AliveByIp = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        onProgressLog?.Invoke($"[Scanner] Iniciando varredura na sub-rede física de {localPhysicalIp} (UDP Beacon + ARP L2 + Bluetooth)...");

        using var udpScanner = new UdpClient(new IPEndPoint(localPhysicalIp, 0));
        DisableWindowsUdpConnReset(udpScanner);
        udpScanner.EnableBroadcast = true;

        using var rxCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receiveLoopTask = Task.Run(async () =>
        {
            while (!rxCts.Token.IsCancellationRequested)
            {
                try
                {
                    var rx = await udpScanner.ReceiveAsync(rxCts.Token);
                    if (!IntranetGuard.IsStrictlyLanIp(rx.RemoteEndPoint.Address))
                        continue;

                    if (TryParseDiscoveryAnnouncePacket(rx.Buffer, out var ann) && ann != null)
                    {
                        string ipKey = rx.RemoteEndPoint.Address.ToString();
                        kmSharePeers[ipKey] = ann with { LanIp = ipKey };
                        l3AliveByIp[ipKey] = true;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                }
            }
        }, rxCts.Token);

        byte[] probePacket = BuildDiscoveryProbePacket();
        byte[] ipBytes = localPhysicalIp.GetAddressBytes();
        bool isIpv4 = localPhysicalIp.AddressFamily == AddressFamily.InterNetwork && ipBytes.Length == 4;

        if (isIpv4 && !IPAddress.IsLoopback(localPhysicalIp))
        {
            // Send directed subnet broadcast first
            var broadcastIp = new IPAddress([ipBytes[0], ipBytes[1], ipBytes[2], 255]);
            if (IntranetGuard.IsStrictlyLanIp(broadcastIp))
            {
                try
                {
                    await udpScanner.SendAsync(probePacket, new IPEndPoint(broadcastIp, DefaultDiscoveryUdpPort), ct);
                    await udpScanner.SendAsync(probePacket, new IPEndPoint(broadcastIp, KMSharePeerNode.DefaultUdpPort), ct);
                }
                catch
                {
                }
            }

            // Sweep /24 subnet: async UDP Probe + async Ping first, with low-concurrency ARP L2 to avoid ThreadPool starvation
            using var gate = new SemaphoreSlim(64);
            using var arpGate = new SemaphoreSlim(6);
            var tasks = new List<Task>(254);

            for (int host = 1; host <= 254; host++)
            {
                if (host == ipBytes[3]) continue; // Skip own IP

                var candidateIp = new IPAddress([ipBytes[0], ipBytes[1], ipBytes[2], (byte)host]);
                if (!IntranetGuard.IsStrictlyLanIp(candidateIp)) continue;

                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        string ipStr = candidateIp.ToString();

                        // 1. Send UDP discovery probes to :51822 and :51820
                        try
                        {
                            await udpScanner.SendAsync(probePacket, new IPEndPoint(candidateIp, DefaultDiscoveryUdpPort), ct);
                            await udpScanner.SendAsync(probePacket, new IPEndPoint(candidateIp, KMSharePeerNode.DefaultUdpPort), ct);
                        }
                        catch
                        {
                        }

                        // 2. Non-blocking async ICMP Ping (also triggers kernel ARP resolution on LAN)
                        try
                        {
                            using var ping = new Ping();
                            var reply = await ping.SendPingAsync(candidateIp, 250);
                            if (reply.Status == IPStatus.Success)
                            {
                                l3AliveByIp[ipStr] = true;
                            }
                        }
                        catch
                        {
                        }

                        // 3. Check Layer 2 ARP reachability on Windows (throttled so synchronous SendARP never starves ThreadPool)
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && l3AliveByIp.ContainsKey(ipStr))
                        {
                            await arpGate.WaitAsync(ct);
                            try
                            {
                                if (TrySendArpProbe(localPhysicalIp, candidateIp, out string mac) && !IsBroadcastOrZeroMac(mac))
                                {
                                    l2MacByIp[ipStr] = mac;
                                }
                            }
                            finally
                            {
                                arpGate.Release();
                            }
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, ct));
            }

            await Task.WhenAll(tasks);
        }

        // Give UDP responses a brief window to arrive
        await Task.Delay(350, ct);
        rxCts.Cancel();
        try { await receiveLoopTask; } catch { }

        // Collect all unique discovered IPs
        var allIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in kmSharePeers.Keys) allIps.Add(k);
        foreach (var k in l2MacByIp.Keys) allIps.Add(k);
        foreach (var k in l3AliveByIp.Keys) allIps.Add(k);

        var results = new List<DiscoveredNetworkPeer>();
        var pairedBt = BluetoothRfcommNode.GetPairedBluetoothDevices();
        var matchedBtMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string ip in allIps.OrderBy(ParseIpSortKey))
        {
            bool hasKmShare = kmSharePeers.TryGetValue(ip, out var ann);
            bool l2Alive = l2MacByIp.TryGetValue(ip, out string? arpMac);
            bool l3Alive = hasKmShare || (l3AliveByIp.TryGetValue(ip, out bool pOk) && pOk);

            string hostname = hasKmShare && !string.IsNullOrWhiteSpace(ann!.Hostname)
                ? ann.Hostname
                : await TryResolveHostnameFastAsync(ip);

            string btMac = hasKmShare && !string.IsNullOrWhiteSpace(ann!.BluetoothMac) && ann.BluetoothMac != "N/A"
                ? ann.BluetoothMac
                : "";

            // Try matching paired BT device by hostname if BT MAC wasn't announced
            if (string.IsNullOrWhiteSpace(btMac) && !string.IsNullOrWhiteSpace(hostname))
            {
                var btMatch = pairedBt.FirstOrDefault(b =>
                    b.Name.Equals(hostname, StringComparison.OrdinalIgnoreCase) ||
                    hostname.StartsWith(b.Name + ".", StringComparison.OrdinalIgnoreCase));
                if (btMatch != null)
                {
                    btMac = btMatch.MacAddress;
                }
            }

            if (!string.IsNullOrWhiteSpace(btMac))
            {
                matchedBtMacs.Add(btMac);
            }

            string roleDesc = hasKmShare
                ? $"KMShare Ativo ({ann!.Role} | Borda {ann.MonitorEdge})"
                : "Dispositivo na Rede Local";

            VpnFallbackStrategy recommended;
            string statusSummary;

            if (hasKmShare)
            {
                recommended = VpnFallbackStrategy.ModeA_ZeroAdminDiagnostic;
                statusSummary = "Pronto p/ Conectar (Modo A: LAN UDP Direta 1000Hz)";
            }
            else if (l3Alive)
            {
                recommended = VpnFallbackStrategy.ModeA_ZeroAdminDiagnostic;
                statusSummary = "LAN L3 Acessível (Inicie o Receptor lá -> Modo A)";
            }
            else if (l2Alive && !l3Alive)
            {
                recommended = !string.IsNullOrWhiteSpace(btMac)
                    ? VpnFallbackStrategy.ModeC_BluetoothOutOfBand
                    : VpnFallbackStrategy.ModeB_StepUpWfpLanPermit;
                statusSummary = "L2 ARP Ativo, L3 Bloqueado (VPN Kill-Switch -> Use Modo B ou C)";
            }
            else
            {
                recommended = VpnFallbackStrategy.ModeA_ZeroAdminDiagnostic;
                statusSummary = "Detectado na sub-rede local";
            }

            results.Add(new DiscoveredNetworkPeer(
                Hostname: string.IsNullOrWhiteSpace(hostname) ? $"Host ({ip})" : hostname,
                IpAddress: ip,
                PhysicalMac: arpMac ?? "N/A",
                BluetoothMac: btMac,
                RoleDescription: roleDesc,
                IsKmShareResponding: hasKmShare,
                Layer2ArpAlive: l2Alive,
                Layer3Reachable: l3Alive,
                IsBluetoothPaired: !string.IsNullOrWhiteSpace(btMac),
                RecommendedStrategy: recommended,
                StatusSummary: statusSummary));
        }

        // Also include any paired Bluetooth devices not already merged with a LAN IP
        foreach (var bt in pairedBt)
        {
            if (matchedBtMacs.Contains(bt.MacAddress))
                continue;

            results.Add(new DiscoveredNetworkPeer(
                Hostname: $"{bt.Name} (Bluetooth)",
                IpAddress: "",
                PhysicalMac: "N/A (Out-of-Band)",
                BluetoothMac: bt.MacAddress,
                RoleDescription: bt.IsConnected ? "Bluetooth Pareado (Conectado)" : "Bluetooth Pareado",
                IsKmShareResponding: false,
                Layer2ArpAlive: false,
                Layer3Reachable: false,
                IsBluetoothPaired: true,
                RecommendedStrategy: VpnFallbackStrategy.ModeC_BluetoothOutOfBand,
                StatusSummary: "Pronto p/ Modo C (Bluetooth Out-of-Band 133Hz - Ignora VPN)"));
        }

        onProgressLog?.Invoke(
            $"[Scanner] Concluído: {results.Count} dispositivo(s) encontrado(s) ({kmSharePeers.Count} com KMShare ativo, {l2MacByIp.Count} via ARP L2, {pairedBt.Count} Bluetooth pareados).");

        return results
            .OrderByDescending(r => r.IsKmShareResponding)
            .ThenByDescending(r => r.Layer3Reachable)
            .ThenByDescending(r => r.Layer2ArpAlive)
            .ThenByDescending(r => r.IsBluetoothPaired)
            .ToList();
    }

    public static bool TrySendArpProbe(IPAddress localPhysicalIp, IPAddress targetIp, out string formattedMac)
    {
        formattedMac = "";
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        try
        {
            byte[] mac = new byte[6];
            int macLen = mac.Length;
            uint dst = BitConverter.ToUInt32(targetIp.GetAddressBytes(), 0);
            uint src = BitConverter.ToUInt32(localPhysicalIp.GetAddressBytes(), 0);
            int err = SendARP(dst, src, mac, ref macLen);
            if (err == 0 && macLen >= 6)
            {
                formattedMac = string.Join(":", mac.Take(6).Select(b => b.ToString("X2")));
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static bool IsBroadcastOrZeroMac(string mac)
    {
        return string.IsNullOrWhiteSpace(mac)
            || mac == "FF:FF:FF:FF:FF:FF"
            || mac == "00:00:00:00:00:00";
    }

    private static async Task<string> TryResolveHostnameFastAsync(string ipAddress)
    {
        try
        {
            var resolveTask = Dns.GetHostEntryAsync(ipAddress);
            var done = await Task.WhenAny(resolveTask, Task.Delay(350));
            if (done == resolveTask)
            {
                var entry = await resolveTask;
                if (!string.IsNullOrWhiteSpace(entry.HostName) && entry.HostName != ipAddress)
                {
                    return entry.HostName;
                }
            }
        }
        catch
        {
        }
        return "";
    }

    private static uint ParseIpSortKey(string ip)
    {
        if (IPAddress.TryParse(ip, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = parsed.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }
        return uint.MaxValue;
    }
}

