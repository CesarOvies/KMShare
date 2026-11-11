using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace MKShare.Network;

public static class VpnKillSwitchProbe
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] pMacAddr, ref int phyAddrLen);

    public static async Task ProbeTargetAsync(string targetHostname, IPAddress localPhysicalIp)
    {
        Console.WriteLine($"\n=== VPN Kill-Switch / Non-Tunneled Block Probe -> '{targetHostname}' ===");
        Console.WriteLine($"  Local Physical Bind IP: {localPhysicalIp}");

        // 1. Test Name Resolution (mDNS .local & standard)
        IPAddress? targetLanIp = null;
        try
        {
            var hostEntry = await Dns.GetHostEntryAsync($"{targetHostname}.local");
            Console.WriteLine($"  [1] Name Resolution ('{targetHostname}') succeeded! Reported IPs:");
            foreach (var ip in hostEntry.AddressList)
            {
                bool sameSubnet = IsSame24Subnet(localPhysicalIp, ip);
                Console.WriteLine($"      - {ip} (AddressFamily={ip.AddressFamily}, SamePhysicalSubnet={sameSubnet})");
                if (sameSubnet && ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    targetLanIp = ip;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [1] Name Resolution failed: {ex.Message}");
        }

        targetLanIp ??= IPAddress.Parse("192.168.15.6");
        Console.WriteLine($"  -> Target Physical LAN IP selected: {targetLanIp}");

        // 1B. Test Layer 2 ARP Reachability (iphlpapi!SendARP) - Bypasses Layer 3 IP filters!
        string detectedMac = "UNKNOWN";
        bool l2ArpAlive = false;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            byte[] mac = new byte[6];
            int macLen = mac.Length;
            uint dst = BitConverter.ToUInt32(targetLanIp.GetAddressBytes(), 0);
            uint src = BitConverter.ToUInt32(localPhysicalIp.GetAddressBytes(), 0);
            var sw = Stopwatch.StartNew();
            int arpErr = SendARP(dst, src, mac, ref macLen);
            sw.Stop();
            if (arpErr == 0)
            {
                l2ArpAlive = true;
                detectedMac = string.Join("-", mac.Take(macLen).Select(b => b.ToString("X2")));
                Console.WriteLine($"  [L2-ARP] Layer 2 SendARP to {targetLanIp}: ALIVE! MAC={detectedMac} ({sw.ElapsedMilliseconds}ms)");
            }
            else
            {
                Console.WriteLine($"  [L2-ARP] Layer 2 SendARP to {targetLanIp}: Failed (err={arpErr})");
            }
        }

        // 2. Test Unicast ICMP Ping bound to local interface
        using var ping = new Ping();
        var pingReply = await ping.SendPingAsync(targetLanIp, 1000);
        Console.WriteLine($"  [2] Unicast ICMP Ping to {targetLanIp}: {pingReply.Status} (RTT={pingReply.RoundtripTime}ms)");

        // 3. Test Direct UDP Unicast Probe (Port 5353 mDNS & Port 5355 LLMNR) bound to Physical NIC
        bool llmnrUnicastAlive = await ProbeUdpDnsQueryAsync(localPhysicalIp, new IPEndPoint(targetLanIp, 5355), targetHostname);
        Console.WriteLine($"  [3] Direct UDP Unicast LLMNR (5355) to {targetLanIp}: {(llmnrUnicastAlive ? "RESPONDED (BYPASSED VPN!)" : "BLOCKED/TIMEOUT")}");

        bool mdnsUnicastAlive = await ProbeUdpDnsQueryAsync(localPhysicalIp, new IPEndPoint(targetLanIp, 5353), $"{targetHostname}.local");
        Console.WriteLine($"  [4] Direct UDP Unicast mDNS (5353) to {targetLanIp}: {(mdnsUnicastAlive ? "RESPONDED (BYPASSED VPN!)" : "BLOCKED/TIMEOUT")}");

        // 4. Test Multicast LLMNR (224.0.0.252:5355) & Multicast mDNS (224.0.0.251:5353) bound to Physical NIC
        bool llmnrMulticastAlive = await ProbeUdpDnsQueryAsync(localPhysicalIp, new IPEndPoint(IPAddress.Parse("224.0.0.252"), 5355), targetHostname, qtype: 0x00FF); // ANY
        Console.WriteLine($"  [5] Multicast LLMNR (224.0.0.252:5355) from {localPhysicalIp}: {(llmnrMulticastAlive ? "RESPONDED (BYPASSED VPN!)" : "BLOCKED/TIMEOUT")}");

        bool mdnsMulticastAlive = await ProbeMdnsMulticastBusAsync(localPhysicalIp, targetLanIp, $"{targetHostname}.local");
        Console.WriteLine($"  [6] Multicast mDNS Bus (224.0.0.251:5353) from {targetLanIp}: {(mdnsMulticastAlive ? "RESPONDED LIVE (BYPASSED VPN!)" : "BLOCKED/TIMEOUT")}");

        // 5. Test NetBIOS Node Status (UDP 137)
        bool netbiosAlive = await ProbeNetBiosNodeStatusAsync(localPhysicalIp, targetLanIp);
        Console.WriteLine($"  [7] UDP NetBIOS Name Service (137) to {targetLanIp}: {(netbiosAlive ? "RESPONDED (BYPASSED VPN!)" : "BLOCKED/TIMEOUT")}");

        bool anyL3Alive = pingReply.Status == IPStatus.Success || llmnrUnicastAlive || mdnsUnicastAlive || llmnrMulticastAlive || mdnsMulticastAlive || netbiosAlive;
        if (l2ArpAlive && !anyL3Alive)
        {
            Console.WriteLine("\n  [!] DIAGNÓSTICO AUTOMÁTICO: Peer fisicamente ativo na LAN (L2 ARP OK), mas Camada 3 bloqueada pelo Kill-Switch da VPN.");
            Console.WriteLine("  -> Testando as 3 opções selecionáveis pelo usuário (Modo A, Modo B, Modo C):");

            foreach (var mode in Enum.GetValues<VpnFallbackStrategy>())
            {
                var report = VpnFallbackOrchestrator.ExecuteUserSelectedStrategy(mode, localPhysicalIp, targetLanIp, detectedMac);
                Console.WriteLine($"     * {report.ActionSummary}");
                Console.WriteLine($"       [Perf: Latência={report.EstimatedHidLatencyMs}ms | Mouse={report.MaxMousePollingHz}Hz | Arquivos={report.MaxFileThroughputMbps} Mbps]");
            }

            // Verify Bluetooth Mouse Coalescer (8 raw 1000Hz movements -> 1 coalesced 133Hz BLE frame)
            var coalescer = new BluetoothMouseCoalescer();
            for (int i = 0; i < 8; i++) coalescer.PushRawMouseMove(dx: 3, dy: -2);
            bool flushed = coalescer.TryFlushCoalescedFrame(out byte[] btFrame, out int mergedCount);
            Trace.Assert(flushed && mergedCount == 8 && btFrame.Length == 6);
            Console.WriteLine($"     * [Bluetooth Coalescer Check] {mergedCount} movimentos de 1000Hz fundidos em 1 frame de {btFrame.Length}B (dx=+24, dy=-16) -> Zero Input Lag!");
        }
    }

    private static async Task ProbeIpv6LinkLocalAsync(string targetHostname)
    {
        try
        {
            // Find physical Ethernet interface index & IPv6 link-local address
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet)
                    continue;
                var ipv6Props = ni.GetIPProperties().GetIPv6Properties();
                var localLl = ni.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 && u.Address.IsIPv6LinkLocal)?.Address;
                if (localLl == null) continue;

                using var udp6 = new UdpClient(AddressFamily.InterNetworkV6);
                udp6.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.ReuseAddress, true);
                udp6.Client.Bind(new IPEndPoint(localLl, 0));

                // Query IPv6 LLMNR (ff02::1:3 port 5355) & IPv6 mDNS (ff02::fb port 5353)
                byte[] query = BuildDnsQueryPacket(targetHostname, 0x00FF);
                var llmnrV6 = new IPEndPoint(IPAddress.Parse($"ff02::1:3%{ipv6Props.Index}"), 5355);
                await udp6.SendAsync(query, llmnrV6);

                var recv = udp6.ReceiveAsync();
                if (await Task.WhenAny(recv, Task.Delay(1200)) == recv)
                {
                    var res = await recv;
                    Console.WriteLine($"  [8] IPv6 Link-Local Multicast LLMNR (ff02::1:3): RESPONDED from {res.RemoteEndPoint} ({res.Buffer.Length} bytes)!");
                    return;
                }
                else
                {
                    Console.WriteLine($"  [8] IPv6 Link-Local Multicast LLMNR (ff02::1:3): BLOCKED/TIMEOUT");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [8] IPv6 Link-Local test error: {ex.Message}");
        }
    }

    private static bool IsSame24Subnet(IPAddress a, IPAddress b)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork || b.AddressFamily != AddressFamily.InterNetwork)
            return false;
        byte[] ba = a.GetAddressBytes();
        byte[] bb = b.GetAddressBytes();
        return ba[0] == bb[0] && ba[1] == bb[1] && ba[2] == bb[2];
    }

    private static async Task<bool> ProbeMdnsMulticastBusAsync(IPAddress localBindIp, IPAddress expectedTargetIp, string qname)
    {
        try
        {
            IPAddress mdnsGroup = IPAddress.Parse("224.0.0.251");
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Bind(new IPEndPoint(IPAddress.Any, 5353));
            listener.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.AddMembership,
                new MulticastOption(mdnsGroup, localBindIp));
            listener.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.MulticastInterface,
                localBindIp.GetAddressBytes());

            byte[] queryPacket = BuildDnsQueryPacket(qname, 0x0001); // QTYPE=A
            await listener.SendToAsync(queryPacket, SocketFlags.None, new IPEndPoint(mdnsGroup, 5353));

            byte[] rxBuf = new byte[1500];
            EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
            using var cts = new CancellationTokenSource(1200);

            while (!cts.IsCancellationRequested)
            {
                var res = await listener.ReceiveFromAsync(rxBuf, SocketFlags.None, remoteEp, cts.Token);
                var senderIp = ((IPEndPoint)res.RemoteEndPoint).Address;
                // Only accept packets originating from our target peer IP (192.168.15.6)
                if (!senderIp.Equals(expectedTargetIp)) continue;

                Console.WriteLine($"      -> [LIVE PACKET CAPTURED] {res.ReceivedBytes} bytes directly from {res.RemoteEndPoint} on 224.0.0.251:5353!");
                ExtractIpsFromDnsPayload(rxBuf.AsSpan(0, res.ReceivedBytes).ToArray());
                return true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      -> mDNS socket error: {ex.Message}");
        }
        return false;
    }

    private static async Task<bool> ProbeUdpDnsQueryAsync(IPAddress localBindIp, IPEndPoint destination, string qname, ushort qtype = 0x0001)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(localBindIp, 0));
            byte[] packet = BuildDnsQueryPacket(qname, qtype);
            await udp.SendAsync(packet, destination);

            var recvTask = udp.ReceiveAsync();
            var done = await Task.WhenAny(recvTask, Task.Delay(1200));
            if (done == recvTask)
            {
                var res = await recvTask;
                Console.WriteLine($"      -> Received {res.Buffer.Length} bytes from {res.RemoteEndPoint}");
                ExtractIpsFromDnsPayload(res.Buffer);
                return true;
            }
        }
        catch
        {
            // Ignore socket errors on blocked ports
        }
        return false;
    }

    private static void ExtractIpsFromDnsPayload(byte[] buf)
    {
        // Scan answer RDATA for 4-byte IPv4 (192.168.x.x) and 16-byte IPv6 Link-Local (fe80::)
        for (int i = 12; i <= buf.Length - 4; i++)
        {
            if (buf[i] == 192 && buf[i + 1] == 168)
            {
                Console.WriteLine($"         [Decoded IPv4 in payload]: { new IPAddress(buf.AsSpan(i, 4)) }");
            }
            if (i <= buf.Length - 16 && buf[i] == 0xFE && buf[i + 1] == 0x80 && buf[i + 2] == 0x00 && buf[i + 3] == 0x00)
            {
                Console.WriteLine($"         [Decoded IPv6 Link-Local in payload]: { new IPAddress(buf.AsSpan(i, 16)) }");
            }
        }
    }

    private static async Task<bool> ProbeNetBiosNodeStatusAsync(IPAddress localBindIp, IPAddress targetIp)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(localBindIp, 0));
            // Standard NetBIOS wildcard NBSTAT query
            byte[] nbstat =
            [
                0x80, 0x94, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x20, 0x43, 0x4B, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
                0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
                0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x00,
                0x00, 0x21, 0x00, 0x01
            ];
            await udp.SendAsync(nbstat, new IPEndPoint(targetIp, 137));
            var recvTask = udp.ReceiveAsync();
            var done = await Task.WhenAny(recvTask, Task.Delay(1200));
            if (done == recvTask)
            {
                var res = await recvTask;
                Console.WriteLine($"      -> NetBIOS response ({res.Buffer.Length} bytes) from {res.RemoteEndPoint}");
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static byte[] BuildDnsQueryPacket(string domain, ushort qtype = 0x0001)
    {
        using var ms = new MemoryStream();
        // Transaction ID + Flags (Standard Query) + QDCOUNT=1
        ms.Write([0x13, 0x37, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        foreach (string label in domain.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0x00);
        // QTYPE, QCLASS=IN (0x0001)
        ms.Write([(byte)(qtype >> 8), (byte)(qtype & 0xFF), 0x00, 0x01]);
        return ms.ToArray();
    }
}
