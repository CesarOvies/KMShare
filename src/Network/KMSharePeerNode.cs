using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using KMShare.FileTransfer;
using KMShare.Platform;
using KMShare.Security;

namespace KMShare.Network;

public static class KMSharePeerNode
{
    public const int DefaultUdpPort = 51820;

    public const byte WireHello = 0x10;
    public const byte WireHelloAck = 0x11;
    public const byte WireHidPacket = 0x20;
    public const byte WireEdgeEnter = 0x2F;
    public const byte WireEdgeReturn = 0x30;
    public const byte WireHeartbeatPing = 0x40;
    public const byte WireHeartbeatAck = 0x41;
    public const byte WireRevoked = 0xFF;

    public static Func<IPAddress, int>? CustomVpnFallbackPrompt { get; set; }
    public static Func<string?>? CustomBluetoothTargetSelector { get; set; }

    private static void DisableWindowsUdpConnReset(UdpClient udp)
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

    public static async Task RunReceiverAsync(IPAddress bindLanIp, int port, string pin, CancellationToken ct)
    {
        IntranetGuard.EnforceLanEndpoint(new IPEndPoint(bindLanIp, port));

        IHidPlatform hid = HidPlatformFactory.GetCurrent();
        using var udp = new UdpClient(new IPEndPoint(bindLanIp, port));
        DisableWindowsUdpConnReset(udp);
        using var session = new SecureHidSession(maxDistinctKeystrokesPerSecond: 30);

        string localBtMac = BluetoothRfcommNode.GetLocalBluetoothMacAddress();

        Console.WriteLine("=======================================================================");
        Console.WriteLine("[KMShare RECEIVER - Dual Stack (LAN UDP + Bluetooth RFCOMM)]");
        Console.WriteLine($"  -> Escutando LAN Física:  {bindLanIp}:{port}");
        Console.WriteLine($"  -> Escutando Bluetooth:   {localBtMac} (Canal {BluetoothRfcommNode.DefaultRfcommChannel})");
        Console.WriteLine($"  -> Canal de Arquivos TCP: {bindLanIp}:{SecureFileChannel.DefaultTcpFilePort} (Step-Up Auth)");
        Console.WriteLine($"  -> PIN de Pareamento:     {pin}");
        Console.WriteLine("Aguardando conexão do PC principal (LAN ou Bluetooth)... (Ctrl+C p/ sair)");
        Console.WriteLine("=======================================================================\n");

        // Start Bluetooth RFCOMM listener in parallel so the receiver accepts Mode A, B, or C seamlessly
        _ = Task.Run(async () =>
        {
            try
            {
                await BluetoothRfcommNode.RunBluetoothReceiverAsync(pin, ct);
            }
            catch
            {
            }
        }, ct);

        // Start Secure File Transfer TCP listener (:51821) in parallel with per-transaction Step-Up Auth
        _ = Task.Run(async () =>
        {
            var gatekeeper = new FileTransferGatekeeper();
            string downloadsDir = SecureFileChannel.GetDefaultDownloadsDirectory();
            TcpListener? fileListener = null;
            try
            {
                fileListener = new TcpListener(bindLanIp, SecureFileChannel.DefaultTcpFilePort);
                fileListener.Start();
                using var reg = ct.Register(() => fileListener.Stop());
                while (!ct.IsCancellationRequested)
                {
                    var res = await SecureFileChannel.ReceiveSingleFileOverTcpAsync(
                        fileListener,
                        pin,
                        gatekeeper,
                        downloadsDir,
                        requireInteractiveOsPrompt: true,
                        ct: ct);
                    Console.WriteLine($"[FileTransfer Receiver] Arquivo='{res.FileName}' | Status={res.Status} | Salvo={res.SavedFilePath ?? "N/A"}");
                }
            }
            catch
            {
            }
            finally
            {
                try { fileListener?.Stop(); } catch { }
            }
        }, ct);

        IPEndPoint? activePeer = null;
        ScreenEdge activeReturnEdge = ScreenEdge.Left;

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try
            {
                res = await udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                // Ignore transient ICMP port unreachable on UDP
                continue;
            }

            IntranetGuard.EnforceLanEndpoint(res.RemoteEndPoint);
            byte[] buf = res.Buffer;
            if (buf.Length < 1) continue;

            byte wireType = buf[0];

            if (NetworkPeerDiscovery.IsDiscoveryProbePacket(buf))
            {
                byte[] announce = NetworkPeerDiscovery.BuildDiscoveryAnnouncePacket(
                    bindLanIp,
                    localBtMac,
                    "Receptor Ativo",
                    hid.ActiveCrossingEdge,
                    port);
                await udp.SendAsync(announce, res.RemoteEndPoint, ct);
            }
            else if (wireType == WireHello && buf.Length >= 2)
            {
                var (ownEcdh, ownPub) = SecureHidSession.CreateHandshakeKeyPair();
                using (ownEcdh)
                {
                    session.EstablishSession(ownEcdh, buf.AsSpan(1), pin, peerId: res.RemoteEndPoint.Address.ToString());
                }

                activePeer = res.RemoteEndPoint;
                byte[] ack = [WireHelloAck, .. ownPub];
                await udp.SendAsync(ack, activePeer, ct);
                Console.WriteLine($"[+] Pareamento LAN ECDH P-256 + AES-256-GCM concluído com {activePeer}!");
            }
            else if (wireType == WireHeartbeatPing && activePeer != null && res.RemoteEndPoint.Equals(activePeer) && session.IsAuthenticated)
            {
                byte[] hbAck = [WireHeartbeatAck];
                await udp.SendAsync(hbAck, activePeer, ct);
            }
            else if (wireType == WireEdgeEnter && activePeer != null && res.RemoteEndPoint.Equals(activePeer) && session.IsAuthenticated)
            {
                if (EdgeCoordinateMapper.TryDecodeWireEdgePacket(buf, WireEdgeEnter, ScreenEdge.Right, out ScreenEdge senderExitEdge, out ushort normCoord))
                {
                    activeReturnEdge = EdgeCoordinateMapper.GetOppositeEdge(senderExitEdge);
                    hid.WarpCursorOnEdgeEnter(senderExitEdge, normCoord);
                }
            }
            else if (wireType == WireHidPacket && activePeer != null && res.RemoteEndPoint.Equals(activePeer))
            {
                var status = session.OpenAndValidatePacket(buf.AsSpan(1), out HidEvent ev);
                if (status == PacketStatus.Accepted)
                {
                    hid.InjectHidEvent(ev, activeReturnEdge, out bool hitReturnEdge, out ushort returnNormCoord);
                    if (hitReturnEdge)
                    {
                        hid.ReleaseAllModifierKeysOnReceiver();
                        byte[] edgeMsg = EdgeCoordinateMapper.EncodeWireEdgePacket(WireEdgeReturn, activeReturnEdge, returnNormCoord);
                        await udp.SendAsync(edgeMsg, activePeer, ct);
                        Console.WriteLine($"[<-] Cursor tocou a borda ({activeReturnEdge}): devolvendo controle para o PC principal.");
                    }
                }
                else
                {
                    hid.ReleaseAllModifierKeysOnReceiver();
                    Console.WriteLine($"[!] ALERTA DE SEGURANÇA: Pacote rejeitado ({status})! Conexão desfeita e chave revogada.");
                    byte[] revokeMsg = [WireRevoked, (byte)status];
                    for (int i = 0; i < 3; i++)
                    {
                        await udp.SendAsync(revokeMsg, activePeer, ct);
                    }
                    activePeer = null;
                    Console.WriteLine("[*] Pronto para aceitar novo pareamento...");
                }
            }
        }
    }

    public static async Task RunSenderAsync(
        IPAddress localBindIp,
        IPAddress targetIp,
        int port,
        string pin,
        CancellationToken ct,
        bool autoReconnectOnNetworkLoss = true)
    {
        var targetEp = new IPEndPoint(targetIp, port);
        IntranetGuard.EnforceLanEndpoint(new IPEndPoint(localBindIp, 0));
        IntranetGuard.EnforceLanEndpoint(targetEp);

        IHidPlatform hid = HidPlatformFactory.GetCurrent();

        while (!ct.IsCancellationRequested)
        {
            using var udp = new UdpClient(new IPEndPoint(localBindIp, 0));
            DisableWindowsUdpConnReset(udp);
            using var session = new SecureHidSession(maxDistinctKeystrokesPerSecond: 30);

            Console.WriteLine("=================================================================");
            Console.WriteLine($"[KMShare SENDER] Placa Física Local: {localBindIp} -> Destino: {targetEp}");
            Console.WriteLine("Iniciando pareamento ECDH P-256 + HKDF-SHA256...");
            Console.WriteLine("=================================================================");

            bool connectedLan = await TryPerformUdpHandshakeAsync(udp, targetEp, session, pin, ct);

            if (!connectedLan)
            {
                Console.WriteLine($"\n[!] Timeout conectando em {targetEp}. Verificando Camada 2 (ARP) para diagnóstico de VPN...");
                await VpnKillSwitchProbe.ProbeTargetAsync(targetIp.ToString(), localBindIp);

                int choice = PromptUserForVpnFallbackAction(targetIp);
                if (choice == 1)
                {
                    Console.WriteLine("\n[*] Retentando conexão LAN...\n");
                    continue;
                }
                if (choice == 2)
                {
                    bool elevatedOk = TryApplyModeBStepUpFirewallAndRoute(localBindIp, targetIp, port);
                    Console.WriteLine(elevatedOk
                        ? "[+] Exceção de Rota/Firewall aplicada! Retentando conexão LAN...\n"
                        : "[!] Elevação cancelada ou falhou. Retentando...\n");
                    continue;
                }
                if (choice == 3)
                {
                    string? btMac = SelectOrPromptBluetoothTarget();
                    if (!string.IsNullOrWhiteSpace(btMac))
                    {
                        await BluetoothRfcommNode.RunBluetoothSenderAsync(btMac, pin, ct);
                    }
                    return;
                }
                return;
            }

            hid.NotifyPeerAlive();
            Console.WriteLine($"[+] Conectado e autenticado via LAN com {targetEp}!");

            bool wasRevokedBySecurity = false;
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            Action<bool> stateHandler = isRemote =>
            {
                Console.WriteLine(isRemote
                    ? $"[->] CRUZOU A TELA ({hid.ActiveCrossingEdge}): Mouse e Teclado agora controlando {targetIp}! (F12 para voltar)"
                    : "[<-] VOLTOU: Mouse e Teclado de volta na máquina local.");
            };

            Action<ScreenEdge, ushort> edgeCrossHandler = (edge, normCoord) =>
            {
                try
                {
                    byte[] edgeEnter = EdgeCoordinateMapper.EncodeWireEdgePacket(WireEdgeEnter, edge, normCoord);
                    udp.Send(edgeEnter, edgeEnter.Length, targetEp);
                }
                catch
                {
                }
            };

            Action<byte[]> hidHandler = hidFrame =>
            {
                if (!session.IsAuthenticated)
                {
                    hid.TerminateSessionAndReleaseInput("Sessão não autenticada");
                    return;
                }
                try
                {
                    byte[] sealedPacket = session.SealFrame(hidFrame);
                    byte[] wire = [WireHidPacket, .. sealedPacket];
                    udp.Send(wire, wire.Length, targetEp);
                }
                catch
                {
                    hid.TerminateSessionAndReleaseInput("Erro de envio no socket UDP");
                }
            };

            hid.OnRemoteStateChanged += stateHandler;
            hid.OnEdgeCrossed += edgeCrossHandler;
            hid.OnOutgoingHidFrame += hidHandler;

            try
            {
                _ = Task.Run(async () =>
                {
                    byte[] pingMsg = [WireHeartbeatPing];
                    while (!sessionCts.Token.IsCancellationRequested && session.IsAuthenticated)
                    {
                        try
                        {
                            await udp.SendAsync(pingMsg, targetEp, sessionCts.Token);
                            await Task.Delay(500, sessionCts.Token);
                        }
                        catch
                        {
                            break;
                        }
                    }
                }, sessionCts.Token);

                _ = Task.Run(async () =>
                {
                    while (!sessionCts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            var rx = await udp.ReceiveAsync(sessionCts.Token);
                            if (rx.Buffer.Length < 1) continue;

                            byte msgType = rx.Buffer[0];
                            if (msgType == WireHeartbeatAck)
                            {
                                hid.NotifyPeerAlive();
                            }
                            else if (msgType == WireEdgeReturn)
                            {
                                hid.NotifyPeerAlive();
                                EdgeCoordinateMapper.TryDecodeWireEdgePacket(rx.Buffer, WireEdgeReturn, ScreenEdge.Left, out _, out ushort retNorm);
                                hid.SetRemoteActive(false, retNorm);
                            }
                            else if (msgType == WireRevoked)
                            {
                                wasRevokedBySecurity = true;
                                var reason = rx.Buffer.Length >= 2 ? ((PacketStatus)rx.Buffer[1]).ToString() : "Revoked";
                                session.RevokeAuthentication();
                                hid.TerminateSessionAndReleaseInput($"Sessão revogada pelo receptor ({reason})");
                                break;
                            }
                        }
                        catch
                        {
                            hid.TerminateSessionAndReleaseInput("Socket de recepção encerrado");
                            break;
                        }
                    }
                }, sessionCts.Token);

                hid.RunSenderCaptureLoop(sessionCts.Token);
            }
            finally
            {
                sessionCts.Cancel();
                hid.OnRemoteStateChanged -= stateHandler;
                hid.OnEdgeCrossed -= edgeCrossHandler;
                hid.OnOutgoingHidFrame -= hidHandler;
            }

            if (wasRevokedBySecurity || !autoReconnectOnNetworkLoss || ct.IsCancellationRequested)
            {
                return;
            }

            Console.WriteLine("[*] Reconexão automática ativa: aguardando retorno da rede com o peer...");
            await Task.Delay(1000, ct);
        }
    }

    public static async Task<bool> TryPerformUdpHandshakeAsync(
        UdpClient udp,
        IPEndPoint targetEp,
        SecureHidSession session,
        string pin,
        CancellationToken ct,
        TrustedPeerVault? vault = null)
    {
        IntranetGuard.EnforceLanEndpoint(targetEp);
        var (ownEcdh, ownPub) = SecureHidSession.CreateHandshakeKeyPair();
        using (ownEcdh)
        {
            byte[] hello = [WireHello, .. ownPub];
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await udp.SendAsync(hello, targetEp, ct);
                var recvTask = udp.ReceiveAsync(ct).AsTask();
                var done = await Task.WhenAny(recvTask, Task.Delay(1200, ct));
                if (done == recvTask)
                {
                    var ack = await recvTask;
                    if (ack.Buffer.Length >= 2 && ack.Buffer[0] == WireHelloAck)
                    {
                        session.EstablishSession(ownEcdh, ack.Buffer.AsSpan(1), pin, peerId: targetEp.Address.ToString(), vault: vault);
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private static int PromptUserForVpnFallbackAction(IPAddress targetIp)
    {
        if (CustomVpnFallbackPrompt != null)
        {
            return CustomVpnFallbackPrompt(targetIp);
        }

        Console.WriteLine("\n=======================================================================");
        Console.WriteLine($"[ESCOLHA COMO CONECTAR AO PEER {targetIp}]");
        Console.WriteLine("  [1] Modo A (Zero-Admin LAN): Já liberei a LAN na VPN / reiniciei o --listen lá -> Tentar LAN de novo");
        Console.WriteLine("  [2] Modo B (Step-Up Auth LAN): Autorizar rota/regra local p/ este IP e tentar LAN (1000Hz)");
        Console.WriteLine("  [3] Modo C (Bluetooth Out-of-Band): Conectar via Bluetooth agora ignorando a VPN (133Hz, Zero-Admin)");
        Console.WriteLine("  [0] Sair");
        Console.Write("Digite sua opção (1, 2, 3 ou 0): ");

        string? input = Console.ReadLine()?.Trim();
        return input switch
        {
            "1" => 1,
            "2" => 2,
            "3" => 3,
            _ => 0
        };
    }

    public static bool TryApplyModeBStepUpFirewallAndRoute(IPAddress localIp, IPAddress targetIp, int port)
    {
        if (!OsStepUpAuth.PromptUserConsent(
                $"KMShare Modo B: Autorizar rota direta e porta UDP {port} exclusivamente para {targetIp}?"))
        {
            return false;
        }

        try
        {
            string args = $"/c route add {targetIp} mask 255.255.255.255 {localIp} metric 1 & " +
                          $"netsh advfirewall firewall add rule name=\"KMShare_Peer_{targetIp}\" dir=in action=allow remoteip={targetIp}/32 protocol=UDP localport={port}";
            using var proc = Process.Start(new ProcessStartInfo("cmd.exe", args)
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            proc?.WaitForExit(15_000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? SelectOrPromptBluetoothTarget()
    {
        if (CustomBluetoothTargetSelector != null)
        {
            return CustomBluetoothTargetSelector();
        }

        var paired = BluetoothRfcommNode.GetPairedBluetoothDevices();
        Console.WriteLine("\n--- Dispositivos Bluetooth Pareados neste Windows ---");
        for (int i = 0; i < paired.Count; i++)
        {
            var d = paired[i];
            Console.WriteLine($"  [{i + 1}] {d.Name,-28} | MAC: {d.MacAddress} | Connected={d.IsConnected}");
        }
        Console.WriteLine("  [M] Digitar endereço MAC Bluetooth manualmente (mostrado no --listen do outro PC)");
        Console.Write("Escolha o número do dispositivo ou digite o MAC (ex: AA:BB:CC:DD:EE:FF): ");

        string? ans = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(ans)) return null;

        if (int.TryParse(ans, out int idx) && idx >= 1 && idx <= paired.Count)
        {
            return paired[idx - 1].MacAddress;
        }

        if (ans.Equals("M", StringComparison.OrdinalIgnoreCase))
        {
            Console.Write("Digite o MAC Bluetooth do outro PC: ");
            return Console.ReadLine()?.Trim();
        }

        return ans;
    }
}
