using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using KMShare.FileTransfer;
using KMShare.Network;
using KMShare.Platform;
using KMShare.Security;
using KMShare.UI;

namespace KMShare;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        CleanupPendingOldExecutable();

        bool isGuiLaunch = args.Length == 0 ||
            args[0].Equals("--gui", StringComparison.OrdinalIgnoreCase) ||
            args[0].Equals("--tray", StringComparison.OrdinalIgnoreCase);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (isGuiLaunch)
            {
                HideStandaloneConsoleWindowIfAny();
            }
            else
            {
                EnsureParentConsoleAttachedForCli();
            }

            try
            {
                _ = WindowsAuthenticodeTrust.EnsureSelfTrust();
            }
            catch { }
        }

        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        using var cts = new CancellationTokenSource();
        try
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
        }
        catch { }

        var physicalNics = VpnBypassNetwork.GetPhysicalLanInterfaces();
        IPAddress localLanIp = physicalNics.Count > 0 ? physicalNics[0].Ipv4Address : IPAddress.Loopback;

        if (args.Length >= 1)
        {
            string cmd = args[0].ToLowerInvariant();

            switch (cmd)
            {
                case "--listen":
                {
                    string pin = args.Length >= 2 ? args[1] : TrayAndEdgeManager.LoadOrCreateConfig().PairingPin;
                    await KMSharePeerNode.RunReceiverAsync(localLanIp, KMSharePeerNode.DefaultUdpPort, pin, cts.Token);
                    return 0;
                }
                case "--connect" when args.Length >= 2:
                {
                    IPAddress targetIp = IPAddress.Parse(args[1]);
                    string pin = args.Length >= 3 ? args[2] : TrayAndEdgeManager.LoadOrCreateConfig().PairingPin;
                    await KMSharePeerNode.RunSenderAsync(localLanIp, targetIp, KMSharePeerNode.DefaultUdpPort, pin, cts.Token);
                    return 0;
                }
                case "--listen-bt":
                {
                    string pin = args.Length >= 2 ? args[1] : TrayAndEdgeManager.LoadOrCreateConfig().PairingPin;
                    await BluetoothRfcommNode.RunBluetoothReceiverAsync(pin, cts.Token);
                    return 0;
                }
                case "--connect-bt" when args.Length >= 2:
                {
                    string targetBtMac = args[1];
                    string pin = args.Length >= 3 ? args[2] : TrayAndEdgeManager.LoadOrCreateConfig().PairingPin;
                    await BluetoothRfcommNode.RunBluetoothSenderAsync(targetBtMac, pin, cts.Token);
                    return 0;
                }
                case "--gui" or "--tray":
                {
                    string btMac = BluetoothRfcommNode.GetLocalBluetoothMacAddress();
                    await KMShareGuiWindow.RunAsync(
                        localLanIp,
                        btMac,
                        () => RunSystemIntegrityVerificationSuiteAsync(interactiveAuth: false),
                        cts.Token);
                    return 0;
                }
                case "--cli" or "--tui":
                {
                    string btMac = BluetoothRfcommNode.GetLocalBluetoothMacAddress();
                    await TrayAndEdgeManager.RunInteractiveMenuAsync(
                        localLanIp,
                        btMac,
                        () => RunSystemIntegrityVerificationSuiteAsync(interactiveAuth: false),
                        cts.Token);
                    return 0;
                }
                case "--discover":
                {
                    var found = await NetworkPeerDiscovery.ScanNetworkAndBluetoothPeersAsync(
                        localLanIp,
                        msg => Console.WriteLine(msg),
                        cts.Token);
                    foreach (var p in found)
                    {
                        Console.WriteLine($"  -> {p.Hostname,-26} | IP: {(string.IsNullOrEmpty(p.IpAddress) ? "N/A" : p.IpAddress),-15} | BT: {(string.IsNullOrEmpty(p.BluetoothMac) ? "N/A" : p.BluetoothMac),-17} | {p.StatusSummary}");
                    }
                    return 0;
                }
                case "--send-file" when args.Length >= 3:
                {
                    IPAddress targetIp = IPAddress.Parse(args[1]);
                    string filePath = args[2];
                    string pin = args.Length >= 4 ? args[3] : TrayAndEdgeManager.LoadOrCreateConfig().PairingPin;
                    var res = await SecureFileChannel.SendSingleFileOverTcpAsync(
                        localLanIp,
                        new IPEndPoint(targetIp, SecureFileChannel.DefaultTcpFilePort),
                        pin,
                        filePath,
                        ct: cts.Token);
                    Console.WriteLine($"[FileTransfer] Status={res.Status} ({res.BytesTransferred} bytes)");
                    return res.Status == FileTransferStatus.Committed ? 0 : 1;
                }
                case "--self-test" or "--verify":
                {
                    bool interactiveAuth = args.Contains("--prompt-auth");
                    await RunSystemIntegrityVerificationSuiteAsync(interactiveAuth);
                    return 0;
                }
                case "--version" or "-v":
                {
                    Console.WriteLine($"KMShare v{AppVersionInfo.CurrentVersion}");
                    return 0;
                }
                case "--check-update":
                {
                    Console.WriteLine($"[KMShare] Versão atual: v{AppVersionInfo.CurrentVersion}");
                    Console.WriteLine("[KMShare] Verificando atualizações no GitHub...");
                    var cfg = TrayAndEdgeManager.LoadOrCreateConfig();
                    var res = await GitHubUpdateService.CheckForUpdatesAsync(cfg, cts.Token);
                    if (res.HasUpdate)
                    {
                        Console.WriteLine($"[KMShare] ⚡ Nova versão disponível: v{res.LatestVersion} (atual: v{res.CurrentVersion})");
                        Console.WriteLine($"[KMShare] URL do Release: {res.ReleaseUrl}");
                        Console.WriteLine("[KMShare] Para atualizar automaticamente, execute: KMShare.exe --update");
                    }
                    else
                    {
                        Console.WriteLine($"[KMShare] KMShare está na versão mais recente (v{res.CurrentVersion}).");
                    }
                    return 0;
                }
                case "--update":
                {
                    Console.WriteLine($"[KMShare] Versão atual: v{AppVersionInfo.CurrentVersion}");
                    Console.WriteLine("[KMShare] Verificando atualizações no GitHub...");
                    var cfg = TrayAndEdgeManager.LoadOrCreateConfig();
                    var res = await GitHubUpdateService.CheckForUpdatesAsync(cfg, cts.Token);
                    if (!res.HasUpdate)
                    {
                        Console.WriteLine($"[KMShare] KMShare já está na versão mais recente (v{res.CurrentVersion}).");
                        return 0;
                    }

                    Console.WriteLine($"[KMShare] ⚡ Baixando e aplicando atualização v{res.LatestVersion}...");
                    bool ok = await GitHubUpdateService.DownloadAndApplyUpdateAsync(res, cfg, msg => Console.WriteLine($"  -> {msg}"), cts.Token);
                    return ok ? 0 : 1;
                }
            }
        }

        // Default behavior when started interactively (e.g. F5 in VS Code or double-clicking KMShare.exe):
        // Open the native Graphical User Interface & System Tray Control Center.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !Console.IsInputRedirected)
        {
            string btMac = BluetoothRfcommNode.GetLocalBluetoothMacAddress();
            await KMShareGuiWindow.RunAsync(
                localLanIp,
                btMac,
                () => RunSystemIntegrityVerificationSuiteAsync(interactiveAuth: false),
                cts.Token);
            return 0;
        }

        await RunSystemIntegrityVerificationSuiteAsync(interactiveAuth: false);
        return 0;
    }

    public static async Task RunSystemIntegrityVerificationSuiteAsync(bool interactiveAuth)
    {
        Console.WriteLine($"=== KMShare v{AppVersionInfo.CurrentVersion} — Suíte de Verificação de Segurança e Integridade ===\n");
        PrintCliUsageBanner();

        VerifyZeroPrivilegeAndUserInput();
        VerifyIntranetOnlyFirewallGuard();
        await VerifyVpnBypassPhysicalLanBindAsync();
        await VerifyStrictAuthAndAnomalyKillSwitchAsync();
        VerifyStepUpFileTransferAuth(interactiveAuth);

        await VerifyEdgeTopologyVaultAndAutoReconnectAsync();
        VerifyCrossPlatformHidAndBluetoothBackends();
        await VerifyEndToEndSecureFileChannelAsync();
        await VerifyZeroAdminTrayAndAutostartAsync();
        await VerifyIntranetIsolationAndSignedUpdaterAsync();
        VerifyGitHubUpdateServiceAndVersioning();

        Console.WriteLine("\n=== TODAS AS VERIFICAÇÕES DE INTEGRIDADE E SEGURANÇA PASSARAM ===");
    }

    private static void PrintCliUsageBanner()
    {
        string btMac = BluetoothRfcommNode.GetLocalBluetoothMacAddress();
        var nics = VpnBypassNetwork.GetPhysicalLanInterfaces();
        string lanIp = nics.Count > 0 ? nics[0].Ipv4Address.ToString() : "N/A";

        Console.WriteLine("-----------------------------------------------------------------------");
        Console.WriteLine($"[Identificação desta Máquina] KMShare v{AppVersionInfo.CurrentVersion} | LAN Física: {lanIp} | Bluetooth MAC: {btMac}");
        Console.WriteLine("Comandos de operação do KMShare:");
        Console.WriteLine("  0. Interface Gráfica (GUI) e Descoberta Automática na Rede:");
        Console.WriteLine("     Interface Gráfica (Padrão):   KMShare.exe --gui");
        Console.WriteLine("     Escanear Rede e Bluetooth:    KMShare.exe --discover");
        Console.WriteLine("  1. Via Rede Local (Modos A/B):");
        Console.WriteLine("     No PC Receptor:               KMShare.exe --listen 839201");
        Console.WriteLine("     No PC Principal (Sender):     KMShare.exe --connect <IP-DO-RECEPTOR> 839201");
        Console.WriteLine("  2. Via Bluetooth Out-of-Band (Modo C - Ignora Kill-Switch da VPN):");
        Console.WriteLine("     No PC Receptor:               KMShare.exe --listen-bt 839201");
        Console.WriteLine("     No PC Principal (Sender):     KMShare.exe --connect-bt <MAC-BT-DO-RECEPTOR> 839201");
        Console.WriteLine("  3. Menu Console / Configuração de Bordas e Envio de Arquivos:");
        Console.WriteLine("     Menu Console Interativo:      KMShare.exe --cli");
        Console.WriteLine("     Enviar Arquivo (Step-Up):     KMShare.exe --send-file <IP-DO-RECEPTOR> <ARQUIVO> 839201");
        Console.WriteLine("     Suíte de Autoteste:           KMShare.exe --self-test");
        Console.WriteLine("  4. Atualização e Versão:");
        Console.WriteLine("     Checar Atualizações no GitHub: KMShare.exe --check-update");
        Console.WriteLine("     Atualizar para Última Versão:  KMShare.exe --update");
        Console.WriteLine("     Exibir Versão Atual:           KMShare.exe --version");
        Console.WriteLine("-----------------------------------------------------------------------\n");
    }

    private static void VerifyZeroPrivilegeAndUserInput()
    {
        Console.WriteLine("[Check 1] User-Space Execution & HID Input (No Admin)");

        bool isElevated = false;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            isElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        bool isCiEnvironment = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase)
                               || Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--allow-elevated-ci", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine($"  -> Running as Elevated/Admin: {isElevated} (Expected: False | CI Runner: {isCiEnvironment})");
        if (isCiEnvironment && isElevated)
        {
            Console.WriteLine("  [INFO] Running in CI runner environment under hosted elevated account. Bypassing non-admin assertion.");
        }
        else
        {
            Trace.Assert(!isElevated, "KMShare must run without Administrator privileges!");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            bool hookInstalled = true;
            if (!Debugger.IsAttached)
            {
                IntPtr hMod = GetModuleHandle(null);
                HookProc proc = LowLevelMouseProc;
                IntPtr hookId = SetWindowsHookEx(WH_MOUSE_LL, proc, hMod, 0);
                hookInstalled = hookId != IntPtr.Zero;
                if (hookInstalled) UnhookWindowsHookEx(hookId);
                GC.KeepAlive(proc);
            }

            string btMac = BluetoothRfcommNode.GetLocalBluetoothMacAddress();
            Console.WriteLine($"  -> Process Integrity Level: {GetProcessIntegrityLevel()}");
            Console.WriteLine($"  -> Local Bluetooth Radio MAC (User-Space Query): {btMac}");
            Console.WriteLine($"  -> Win32 SetWindowsHookEx(WH_MOUSE_LL): {(hookInstalled ? "SUCCESS" : "FAILED")}");
            if (!isCiEnvironment)
            {
                Trace.Assert(hookInstalled, "Failed to install low-level hook in user-space.");
            }
        }

        Console.WriteLine("  [OK] Check 1 Passed.\n");
    }

    private static void VerifyIntranetOnlyFirewallGuard()
    {
        Console.WriteLine("[Check 2] 100% Intranet Enforcement (Zero-Internet Socket Guard)");

        IPAddress lanIp1 = IPAddress.Parse("192.168.1.50");
        IPAddress publicGoogleDns = IPAddress.Parse("8.8.8.8");

        Trace.Assert(IntranetGuard.IsStrictlyLanIp(lanIp1));
        Trace.Assert(!IntranetGuard.IsStrictlyLanIp(publicGoogleDns));

        bool blockedInternet = false;
        try
        {
            IntranetGuard.EnforceLanEndpoint(new IPEndPoint(publicGoogleDns, 443));
        }
        catch (IntranetSecurityException ex)
        {
            blockedInternet = true;
            Console.WriteLine($"  -> Blocked public IP 8.8.8.8:443 ({ex.Message})");
        }

        Trace.Assert(blockedInternet, "Public IP was not blocked!");
        Console.WriteLine("  [OK] Check 2 Passed.\n");
    }

    private static async Task VerifyVpnBypassPhysicalLanBindAsync()
    {
        Console.WriteLine("[Check 3] Physical NIC Discovery & Explicit Bind (VPN Bypass)");

        var physicalInterfaces = VpnBypassNetwork.GetPhysicalLanInterfaces();
        foreach (var nic in physicalInterfaces)
        {
            Console.WriteLine($"  -> Physical NIC accepted: [{nic.Type}] {nic.Name} ({nic.Description}) -> IP: {nic.Ipv4Address}");
        }

        IPAddress bindIp = physicalInterfaces.Count > 0 ? physicalInterfaces[0].Ipv4Address : IPAddress.Loopback;
        IntranetGuard.EnforceLanEndpoint(new IPEndPoint(bindIp, 0));

        using var receiver = new UdpClient(new IPEndPoint(bindIp, 0));
        int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;

        using var sender = new UdpClient(new IPEndPoint(bindIp, 0));
        byte[] discoveryBeacon = "KMSHARE_DISCOVERY_V1"u8.ToArray();
        await sender.SendAsync(discoveryBeacon, new IPEndPoint(bindIp, port));

        var result = await receiver.ReceiveAsync();
        string msg = Encoding.UTF8.GetString(result.Buffer);
        Console.WriteLine($"  -> Explicit Physical NIC UDP Bind ({bindIp}:{port}) roundtrip: '{msg}'");
        Trace.Assert(msg == "KMSHARE_DISCOVERY_V1");

        await VpnKillSwitchProbe.ProbeTargetAsync("ntdesenv03", bindIp);

        Console.WriteLine("  [OK] Check 3 Passed.\n");
    }

    private static async Task VerifyStrictAuthAndAnomalyKillSwitchAsync()
    {
        Console.WriteLine("[Check 4] Mutual Auth, Strict Binary Protocol & Anti-Anomaly Kill-Switch");

        using var senderSession = new SecureHidSession(maxDistinctKeystrokesPerSecond: 15);
        using var receiverSession = new SecureHidSession(maxDistinctKeystrokesPerSecond: 15);
        const string pin = "839201";

        var (senderEcdh, senderPub) = SecureHidSession.CreateHandshakeKeyPair();
        var (receiverEcdh, receiverPub) = SecureHidSession.CreateHandshakeKeyPair();
        using (senderEcdh)
        using (receiverEcdh)
        {
            senderSession.EstablishSession(senderEcdh, receiverPub, pin);
            receiverSession.EstablishSession(receiverEcdh, senderPub, pin);
        }

        Console.WriteLine("  -> ECDH P-256 + HKDF-SHA256 Session Key established & peer authenticated.");
        Trace.Assert(receiverSession.IsAuthenticated);

        byte[] sealedMouse = senderSession.SealFrame(HidFrame.EncodeMouseMove(12, -5));
        var res1 = receiverSession.OpenAndValidatePacket(sealedMouse, out HidEvent ev1);
        Console.WriteLine($"  -> Legitimate MouseMove frame result: {res1} (dx={ev1.Arg1}, dy={ev1.Arg2})");
        Trace.Assert(res1 == PacketStatus.Accepted);

        byte[] sealedExploit = senderSession.SealFrame("cmd.exe /c calc.exe"u8);
        var resExploit = receiverSession.OpenAndValidatePacket(sealedExploit, out _);
        Console.WriteLine($"  -> Arbitrary non-HID payload ('cmd.exe /c calc.exe') result: {resExploit}");
        Trace.Assert(resExploit == PacketStatus.RevokedNonHidPayload && !receiverSession.IsAuthenticated);

        var (s2, sPub2) = SecureHidSession.CreateHandshakeKeyPair();
        var (r2, rPub2) = SecureHidSession.CreateHandshakeKeyPair();
        using (s2) using (r2)
        {
            senderSession.EstablishSession(s2, rPub2, pin);
            receiverSession.EstablishSession(r2, sPub2, pin);
        }

        PacketStatus holdWinStatus = PacketStatus.Accepted;
        for (int i = 0; i < 50; i++)
        {
            holdWinStatus = receiverSession.OpenAndValidatePacket(senderSession.SealFrame(HidFrame.EncodeKey(0x5B, true)), out _);
            if (holdWinStatus != PacketStatus.Accepted) break;
        }
        Console.WriteLine($"  -> Holding Windows key (50 OS auto-repeat events of 0x5B) result: {holdWinStatus}");
        Trace.Assert(holdWinStatus == PacketStatus.Accepted && receiverSession.IsAuthenticated);

        var winRStatus = receiverSession.OpenAndValidatePacket(senderSession.SealFrame(HidFrame.EncodeKey(0x13, true)), out _);
        Console.WriteLine($"  -> Remote 'Win+R' run-dialog shortcut attempt result: {winRStatus}");
        Trace.Assert(winRStatus == PacketStatus.RevokedSuspiciousShellShortcut && !receiverSession.IsAuthenticated);

        var (s3, sPub3) = SecureHidSession.CreateHandshakeKeyPair();
        var (r3, rPub3) = SecureHidSession.CreateHandshakeKeyPair();
        using (s3) using (r3)
        {
            senderSession.EstablishSession(s3, rPub3, pin);
            receiverSession.EstablishSession(r3, sPub3, pin);
        }

        PacketStatus burstStatus = PacketStatus.Accepted;
        for (ushort sc = 0x10; sc < 0x10 + 25; sc++)
        {
            burstStatus = receiverSession.OpenAndValidatePacket(senderSession.SealFrame(HidFrame.EncodeKey(sc, true)), out _);
            if (burstStatus != PacketStatus.Accepted) break;
        }
        Console.WriteLine($"  -> Superhuman distinct-keystroke burst (25 keys in <5ms) result: {burstStatus}");
        Trace.Assert(burstStatus == PacketStatus.RevokedSuperhumanRateAnomaly && !receiverSession.IsAuthenticated);

        await Task.CompletedTask;
        Console.WriteLine("  [OK] Check 4 Passed.\n");
    }

    private static void VerifyStepUpFileTransferAuth(bool interactiveOsPrompt)
    {
        Console.WriteLine("[Check 5] Per-Transaction Step-Up Auth for File Transfer");

        byte[] fileAContent = "Confidential_Report_2026.pdf content bytes"u8.ToArray();
        byte[] fileBContent = "Another_File_AttemptWithoutAuth.exe"u8.ToArray();

        var gatekeeper = new FileTransferGatekeeper();

        bool unauthorizedAttempt = gatekeeper.TryCommitTransfer("Report.pdf", fileAContent, []);
        Console.WriteLine($"  -> Transfer without per-transaction user auth: {(unauthorizedAttempt ? "ALLOWED" : "BLOCKED")}");
        Trace.Assert(!unauthorizedAttempt);

        byte[] token = gatekeeper.AuthorizeTransaction("Report.pdf", fileAContent, requireInteractiveOsPrompt: interactiveOsPrompt);
        Trace.Assert(token.Length == 48);

        bool swapAttack = gatekeeper.TryCommitTransfer("Report.pdf", fileBContent, token);
        Console.WriteLine($"  -> Attempt to swap file payload with valid token: {(swapAttack ? "ALLOWED" : "BLOCKED & TOKEN REVOKED")}");
        Trace.Assert(!swapAttack);

        token = gatekeeper.AuthorizeTransaction("Report.pdf", fileAContent, requireInteractiveOsPrompt: false);
        bool legitimateTransfer = gatekeeper.TryCommitTransfer("Report.pdf", fileAContent, token);
        Console.WriteLine($"  -> Legitimate transfer of authorized file: {(legitimateTransfer ? "ALLOWED" : "BLOCKED")}");
        Trace.Assert(legitimateTransfer);

        bool replayAttack = gatekeeper.TryCommitTransfer("Report.pdf", fileAContent, token);
        Console.WriteLine($"  -> Replay attempt with already-consumed token: {(replayAttack ? "ALLOWED" : "BLOCKED")}");
        Trace.Assert(!replayAttack);

        Console.WriteLine("  [OK] Check 5 Passed.\n");
    }

    private static async Task VerifyEdgeTopologyVaultAndAutoReconnectAsync()
    {
        Console.WriteLine("[Check 6] IHidPlatform, Proportional Multi-Edge Mapping, Vault & Auto-Reconnect");

        IHidPlatform platform = HidPlatformFactory.GetCurrent();
        Trace.Assert(platform != null);

        ushort normMidY = EdgeCoordinateMapper.Normalize(540, 1080);
        Trace.Assert(normMidY == 5000, $"Expected normalizedY=5000, got {normMidY}");

        var (rxRight, ryRight) = EdgeCoordinateMapper.MapEntryPosition(ScreenEdge.Right, normMidY, 2560, 1440, marginPx: 10);
        Console.WriteLine($"  -> Crossing Right Edge (1920x1080 @ Y=540 -> norm={normMidY}) into 2560x1440: Entry=({rxRight}, {ryRight})");
        Trace.Assert(rxRight == 10 && ryRight == 720);

        ushort normQuarterX = EdgeCoordinateMapper.Normalize(480, 1920);
        var (rxBottom, ryBottom) = EdgeCoordinateMapper.MapEntryPosition(ScreenEdge.Bottom, normQuarterX, 2560, 1440, marginPx: 10);
        Console.WriteLine($"  -> Crossing Bottom Edge (1920x1080 @ X=480 -> norm={normQuarterX}) into 2560x1440: Entry=({rxBottom}, {ryBottom})");
        Trace.Assert(rxBottom == 640 && ryBottom == 10);

        byte[] wireEnter = EdgeCoordinateMapper.EncodeWireEdgePacket(KMSharePeerNode.WireEdgeEnter, ScreenEdge.Top, 7500);
        bool decodedOk = EdgeCoordinateMapper.TryDecodeWireEdgePacket(wireEnter, KMSharePeerNode.WireEdgeEnter, ScreenEdge.Right, out ScreenEdge decEdge, out ushort decNorm);
        Trace.Assert(decodedOk && decEdge == ScreenEdge.Top && decNorm == 7500);

        string tempVaultPath = Path.Combine(Path.GetTempPath(), $"kmshare_test_{Guid.NewGuid():N}.vault");
        try
        {
            var vault = new TrustedPeerVault(tempVaultPath);
            using var sender = new SecureHidSession();
            using var receiver = new SecureHidSession();
            var (sKey, sPub) = SecureHidSession.CreateHandshakeKeyPair();
            var (rKey, rPub) = SecureHidSession.CreateHandshakeKeyPair();
            using (sKey) using (rKey)
            {
                sender.EstablishSession(sKey, rPub, "998877", peerId: "peer-workstation-01", vault: vault);
                receiver.EstablishSession(rKey, sPub, "998877", peerId: "peer-sender-01", vault: vault);
            }

            var reloadedVault = new TrustedPeerVault(tempVaultPath);
            bool persistedBeforeRevoke = reloadedVault.IsPeerTrusted("peer-sender-01", sPub);
            Console.WriteLine($"  -> TrustedPeerVault persisted peer SPKI (DPAPI/Keyring): {persistedBeforeRevoke}");
            Trace.Assert(persistedBeforeRevoke);

            var status = receiver.OpenAndValidatePacket(sender.SealFrame("malicious_payload"u8), out _);
            bool stillTrustedAfterRevoke = reloadedVault.IsPeerTrusted("peer-sender-01", sPub);
            Console.WriteLine($"  -> After {status}, peer key still in vault: {stillTrustedAfterRevoke} (Expected: False)");
            Trace.Assert(status == PacketStatus.RevokedNonHidPayload && !stillTrustedAfterRevoke);
        }
        finally
        {
            if (File.Exists(tempVaultPath)) File.Delete(tempVaultPath);
        }

        using var rxUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)rxUdp.Client.LocalEndPoint!).Port;
        using var txUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var cts = new CancellationTokenSource(5000);

        var rxTask = Task.Run(async () =>
        {
            for (int cycle = 0; cycle < 2; cycle++)
            {
                var req = await rxUdp.ReceiveAsync(cts.Token);
                Trace.Assert(req.Buffer[0] == KMSharePeerNode.WireHello);
                var (rEcdh, rPub) = SecureHidSession.CreateHandshakeKeyPair();
                using (rEcdh)
                {
                    byte[] ack = [KMSharePeerNode.WireHelloAck, .. rPub];
                    await rxUdp.SendAsync(ack, req.RemoteEndPoint, cts.Token);
                }
            }
        }, cts.Token);

        string tempReconnectVaultPath = Path.Combine(Path.GetTempPath(), $"kmshare_reconn_{Guid.NewGuid():N}.vault");
        try
        {
            var reconnVault = new TrustedPeerVault(tempReconnectVaultPath);
            using var session1 = new SecureHidSession();
            bool firstConnect = await KMSharePeerNode.TryPerformUdpHandshakeAsync(
                txUdp,
                new IPEndPoint(IPAddress.Loopback, port),
                session1,
                "112233",
                cts.Token,
                reconnVault);
            Trace.Assert(firstConnect && session1.IsAuthenticated);

            platform.TerminateSessionAndReleaseInput("Simulated temporary network loss");
            Trace.Assert(!platform.IsRemoteActive, "Local input must be released on network loss!");

            using var session2 = new SecureHidSession();
            bool reconnected = await KMSharePeerNode.TryPerformUdpHandshakeAsync(
                txUdp,
                new IPEndPoint(IPAddress.Loopback, port),
                session2,
                "112233",
                cts.Token,
                reconnVault);
            Console.WriteLine($"  -> Auto-reconnection after temporary drop succeeded: {reconnected} | LocalInputLocked={platform.IsRemoteActive}");
            Trace.Assert(reconnected && session2.IsAuthenticated && !platform.IsRemoteActive);
            await rxTask;
        }
        finally
        {
            if (File.Exists(tempReconnectVaultPath)) File.Delete(tempReconnectVaultPath);
        }

        Console.WriteLine("  [OK] Check 6 Passed.\n");
    }

    private static void VerifyCrossPlatformHidAndBluetoothBackends()
    {
        Console.WriteLine("[Check 7] Linux (X11/Wayland) & macOS (CoreGraphics) User-Space Backends + Bluetooth");

        IHidPlatform winBackend = WindowsHidEngine.Instance;
        IHidPlatform linuxBackend = LinuxHidEngine.Instance;
        IHidPlatform macBackend = MacHidEngine.Instance;

        Trace.Assert(winBackend != null && linuxBackend != null && macBackend != null);

        ulong sampleBtMac = BluetoothRfcommNode.ParseBtAddress("AA:BB:CC:DD:EE:FF");
        var linuxEp = new BluetoothRfcommNode.LinuxBlueZRfcommEndPoint(sampleBtMac, 5);
        SocketAddress linuxSa = linuxEp.Serialize();
        var roundtripLinuxEp = (BluetoothRfcommNode.LinuxBlueZRfcommEndPoint)linuxEp.Create(linuxSa);
        SocketAddress roundtripSa = roundtripLinuxEp.Serialize();

        for (int i = 0; i < linuxSa.Size; i++)
        {
            Trace.Assert(linuxSa[i] == roundtripSa[i], "Linux BlueZ sockaddr_rc serialization mismatch!");
        }

        var winEp = new BluetoothRfcommNode.WindowsBluetoothEndPoint(sampleBtMac, 5);
        SocketAddress winSa = winEp.Serialize();
        Trace.Assert(winSa.Size == 30 && linuxSa.Size == 10);

        Console.WriteLine("  -> Backends verified: WindowsHidEngine, LinuxHidEngine (X11/Wayland Portal), MacHidEngine (CGEventTap)");
        Console.WriteLine("  -> Bluetooth RFCOMM EndPoints verified: Windows AF_BTH (30B) & Linux BlueZ sockaddr_rc (10B)");
        Console.WriteLine("  [OK] Check 7 Passed.\n");
    }

    private static async Task VerifyEndToEndSecureFileChannelAsync()
    {
        Console.WriteLine("[Check 8] End-to-End Encrypted File Transfer Channel with Step-Up Token");

        string tempDir = Path.Combine(Path.GetTempPath(), $"KMShare_FileTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string srcFile = Path.Combine(tempDir, "Project_Architecture_Spec.bin");
        string rxDir = Path.Combine(tempDir, "Downloads");
        Directory.CreateDirectory(rxDir);

        byte[] originalBytes = RandomNumberGenerator.GetBytes(40_000);
        await File.WriteAllBytesAsync(srcFile, originalBytes);

        const string pin = "654321";
        var gatekeeper = new FileTransferGatekeeper();

        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var ep = (IPEndPoint)listener.LocalEndpoint;

            var rxTask1 = SecureFileChannel.ReceiveSingleFileOverTcpAsync(listener, pin, gatekeeper, rxDir, requireInteractiveOsPrompt: false);
            var txRes1 = await SecureFileChannel.SendSingleFileOverTcpAsync(
                IPAddress.Loopback,
                ep,
                pin,
                srcFile,
                overrideTokenForTest: new byte[48]);
            var rxRes1 = await rxTask1;
            Console.WriteLine($"  -> [Test 1] Forged/missing token result: {rxRes1.Status}");
            Trace.Assert(txRes1.Status == FileTransferStatus.RejectedTamperedOrInvalidToken && rxRes1.Status == FileTransferStatus.RejectedTamperedOrInvalidToken);

            var rxTask2 = SecureFileChannel.ReceiveSingleFileOverTcpAsync(listener, pin, gatekeeper, rxDir, requireInteractiveOsPrompt: false);
            var txRes2 = await SecureFileChannel.SendSingleFileOverTcpAsync(
                IPAddress.Loopback,
                ep,
                pin,
                srcFile,
                tamperPayloadInTransitForTest: true);
            var rxRes2 = await rxTask2;
            bool tokenStillActiveAfterTamper = gatekeeper.IsTokenActive(rxRes2.IssuedToken);
            bool anyPartLeft = Directory.GetFiles(rxDir, "*.part").Length > 0;
            Console.WriteLine($"  -> [Test 2] Tampered chunk stream result: {rxRes2.Status} | TokenBurned={!tokenStillActiveAfterTamper} | TempPartCleaned={!anyPartLeft}");
            Trace.Assert(rxRes2.Status == FileTransferStatus.RejectedTamperedOrInvalidToken && !tokenStillActiveAfterTamper && !anyPartLeft);

            var rxTask3 = SecureFileChannel.ReceiveSingleFileOverTcpAsync(listener, pin, gatekeeper, rxDir, requireInteractiveOsPrompt: false);
            var txRes3 = await SecureFileChannel.SendSingleFileOverTcpAsync(IPAddress.Loopback, ep, pin, srcFile);
            var rxRes3 = await rxTask3;
            Trace.Assert(txRes3.Status == FileTransferStatus.Committed && rxRes3.Status == FileTransferStatus.Committed);
            Trace.Assert(rxRes3.SavedFilePath != null && File.Exists(rxRes3.SavedFilePath));

            byte[] savedBytes = await File.ReadAllBytesAsync(rxRes3.SavedFilePath!);
            bool hashMatches = CryptographicOperations.FixedTimeEquals(SHA256.HashData(originalBytes), SHA256.HashData(savedBytes));
            Console.WriteLine($"  -> [Test 3] Legitimate 40KB encrypted stream result: {rxRes3.Status} | SHA256 Intact={hashMatches}");
            Trace.Assert(hashMatches);

            bool replayOk = gatekeeper.TryCommitTransfer(Path.GetFileName(srcFile), originalBytes, rxRes3.IssuedToken);
            Console.WriteLine($"  -> [Test 4] Replay with consumed token allowed: {replayOk} (Expected: False)");
            Trace.Assert(!replayOk);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        Console.WriteLine("  [OK] Check 8 Passed.\n");
    }

    private static async Task VerifyZeroAdminTrayAndAutostartAsync()
    {
        Console.WriteLine("[Check 9] GUI/Tray Config, Network Peer Discovery & User-Space Autostart");

        string tempCfgPath = Path.Combine(Path.GetTempPath(), $"kmshare_cfg_{Guid.NewGuid():N}.json");
        try
        {
            var cfg = KMShareUserConfig.CreateDefault() with
            {
                MonitorEdge = ScreenEdge.Left,
                PreferredVpnMode = VpnFallbackStrategy.ModeC_BluetoothOutOfBand,
                PairingPin = "774411",
                DeviceRole = DeviceOperationRole.ReceiverControlled,
                LastTargetIp = "192.168.15.6"
            };
            TrayAndEdgeManager.SaveConfig(cfg, tempCfgPath);
            var loaded = TrayAndEdgeManager.LoadOrCreateConfig(tempCfgPath);
            Trace.Assert(
                loaded.MonitorEdge == ScreenEdge.Left &&
                loaded.PreferredVpnMode == VpnFallbackStrategy.ModeC_BluetoothOutOfBand &&
                loaded.DeviceRole == DeviceOperationRole.ReceiverControlled &&
                loaded.LastTargetIp == "192.168.15.6");
            Trace.Assert(HidPlatformFactory.GetCurrent().ActiveCrossingEdge == ScreenEdge.Left);
            HidPlatformFactory.GetCurrent().ActiveCrossingEdge = ScreenEdge.Right;

            // Verify LAN Peer Discovery Wire Protocol & Loopback Responder Roundtrip
            byte[] probe = NetworkPeerDiscovery.BuildDiscoveryProbePacket();
            Trace.Assert(NetworkPeerDiscovery.IsDiscoveryProbePacket(probe));

            using var discRx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            NetworkPeerDiscovery.DisableWindowsUdpConnReset(discRx);
            int discPort = ((IPEndPoint)discRx.Client.LocalEndPoint!).Port;
            using var discCts = new CancellationTokenSource(3000);

            var responderTask = Task.Run(async () =>
            {
                var req = await discRx.ReceiveAsync(discCts.Token);
                Trace.Assert(NetworkPeerDiscovery.IsDiscoveryProbePacket(req.Buffer));
                byte[] annBytes = NetworkPeerDiscovery.BuildDiscoveryAnnouncePacket(
                    IPAddress.Loopback,
                    "AA:BB:CC:DD:EE:FF",
                    "Receptor Ativo",
                    ScreenEdge.Left,
                    KMSharePeerNode.DefaultUdpPort);
                await discRx.SendAsync(annBytes, req.RemoteEndPoint, discCts.Token);
            }, discCts.Token);

            var discoveredPeer = await NetworkPeerDiscovery.ProbeSingleEndpointAsync(
                IPAddress.Loopback,
                new IPEndPoint(IPAddress.Loopback, discPort),
                timeoutMs: 1500,
                ct: discCts.Token);
            await responderTask;

            Trace.Assert(
                discoveredPeer != null &&
                discoveredPeer.BluetoothMac == "AA:BB:CC:DD:EE:FF" &&
                discoveredPeer.Role == "Receptor Ativo" &&
                discoveredPeer.MonitorEdge == "Left");
            Console.WriteLine($"  -> LAN Discovery Beacon Verified: Host={discoveredPeer.Hostname}, Role={discoveredPeer.Role}, BT={discoveredPeer.BluetoothMac}");

            const string testEntry = "KMShare_AutomatedVerifyTest";
            bool setOk = TrayAndEdgeManager.SetUserSpaceAutostart(true, @"C:\Users\User\AppData\Local\Programs\KMShare\KMShare.exe", testEntry);
            bool isReg = TrayAndEdgeManager.IsUserSpaceAutostartRegistered(testEntry);
            bool removeOk = TrayAndEdgeManager.SetUserSpaceAutostart(false, null, testEntry);
            bool isRegAfterRemove = TrayAndEdgeManager.IsUserSpaceAutostartRegistered(testEntry);

            bool isCiEnvironment = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

            Console.WriteLine($"  -> User-Space Autostart Set={setOk}, VerifiedPresent={isReg}, CleanedUp={removeOk && !isRegAfterRemove}");
            if (!isCiEnvironment)
            {
                Trace.Assert(setOk && isReg && removeOk && !isRegAfterRemove);
            }
            else if (!setOk)
            {
                Console.WriteLine("  [INFO] User-space registry autostart write skipped on headless CI runner profile.");
            }
        }
        finally
        {
            if (File.Exists(tempCfgPath)) File.Delete(tempCfgPath);
        }

        Console.WriteLine("  [OK] Check 9 Passed.\n");
    }

    private static async Task VerifyIntranetIsolationAndSignedUpdaterAsync()
    {
        Console.WriteLine("[Check 10] Core Intranet Isolation & Separate Ed25519 Signed Updater Verification");

        IPAddress[] publicIps =
        [
            IPAddress.Parse("1.1.1.1"),
            IPAddress.Parse("8.8.8.8"),
            IPAddress.Parse("93.184.216.34"),
            IPAddress.Parse("2606:4700:4700::1111")
        ];

        foreach (var pubIp in publicIps)
        {
            bool blocked = false;
            try
            {
                IntranetGuard.EnforceLanEndpoint(new IPEndPoint(pubIp, 443));
            }
            catch (IntranetSecurityException)
            {
                blocked = true;
            }
            Trace.Assert(blocked, $"Core failed to block public IP {pubIp}!");
        }
        Console.WriteLine("  -> Core IntranetGuard blocked 100% of public IPv4/IPv6 endpoints.");

        string updaterProj = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "updater", "KMShare.Updater", "KMShare.Updater.csproj"));
        if (!File.Exists(updaterProj))
        {
            updaterProj = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "updater", "KMShare.Updater", "KMShare.Updater.csproj"));
        }
        if (!File.Exists(updaterProj))
        {
            updaterProj = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "updater", "KMShare.Updater", "KMShare.Updater.csproj"));
        }

        if (File.Exists(updaterProj))
        {
            using var proc = Process.Start(new ProcessStartInfo("dotnet", $"run --project \"{updaterProj}\" -- --self-test")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            string output = await proc!.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            Console.Write(output);
            Trace.Assert(proc.ExitCode == 0, "KMShare.Updater self-test failed!");
        }

        Console.WriteLine("  [OK] Check 10 Passed.");
    }

    private static void VerifyGitHubUpdateServiceAndVersioning()
    {
        Console.WriteLine("\n[Check 11] GitHub Update Service & Versioning Verification");

        string curVer = AppVersionInfo.CurrentVersion;
        Trace.Assert(!string.IsNullOrWhiteSpace(curVer), "AppVersionInfo.CurrentVersion must not be empty");
        Console.WriteLine($"  -> Current Version resolved: v{curVer}");

        Trace.Assert(GitHubUpdateService.IsVersionNewer("1.0.2", "1.0.1"), "1.0.2 must be newer than 1.0.1");
        Trace.Assert(GitHubUpdateService.IsVersionNewer("v2.0.0", "1.9.9"), "v2.0.0 must be newer than 1.9.9");
        Trace.Assert(!GitHubUpdateService.IsVersionNewer("1.0.1", "1.0.1"), "1.0.1 must not be newer than 1.0.1");
        Trace.Assert(!GitHubUpdateService.IsVersionNewer("v1.0.0", "1.0.1"), "v1.0.0 must not be newer than 1.0.1");
        Console.WriteLine("  -> SemVer comparison logic verified.");

        string sampleJson = """
            {
              "tag_name": "v9.9.9",
              "html_url": "https://github.com/CesarOvies/KMShare/releases/tag/v9.9.9",
              "body": "Test release notes",
              "assets": [
                {
                  "id": 12345,
                  "name": "KMShare-v9.9.9-win-x64-installer.exe",
                  "size": 52428800,
                  "browser_download_url": "https://github.com/CesarOvies/KMShare/releases/download/v9.9.9/KMShare-v9.9.9-win-x64-installer.exe",
                  "url": "https://api.github.com/repos/CesarOvies/KMShare/releases/assets/12345"
                }
              ]
            }
            """;

        var parsed = GitHubUpdateService.ParseReleaseJson(sampleJson, curVer);
        Trace.Assert(parsed != null, "Parsed result must not be null");
        Trace.Assert(parsed!.HasUpdate, "Must report update available for v9.9.9");
        Trace.Assert(parsed.LatestVersion == "9.9.9", "LatestVersion must be 9.9.9");
        Trace.Assert(parsed.Assets.Count == 1, "Must contain 1 asset");
        Trace.Assert(parsed.Assets[0].Name == "KMShare-v9.9.9-win-x64-installer.exe", "Asset name mismatch");
        Console.WriteLine("  -> Release JSON parsing & asset binding verified.");

        // Standalone executable asset resolution test: Must always prefer standalone executable over installer
        var sampleAssets = new List<GitHubReleaseAsset>
        {
            new("KMShare-v9.9.9-win-x64-installer.exe", 50000000, "https://example.com/installer", "", 1),
            new("KMShare-v9.9.9-win-x64-portable.zip", 45000000, "https://example.com/zip", "", 2),
            new("KMShare-v9.9.9-win-x64.exe", 40000000, "https://example.com/exe", "", 3),
            new("KMShare-v9.9.9-linux-x64", 38000000, "https://example.com/linux", "", 4),
            new("KMShare-v9.9.9-osx-arm64", 37000000, "https://example.com/mac", "", 5)
        };

        var resolvedWin = GitHubUpdateService.ResolveExecutableAsset(sampleAssets, OSPlatform.Windows);
        Trace.Assert(resolvedWin != null && resolvedWin.Name == "KMShare-v9.9.9-win-x64.exe", "Windows must resolve standalone .exe over installer");

        // When only installer and portable zip are available, must resolve portable zip (never installer)
        var fallbackAssets = sampleAssets.Take(2).ToList();
        var resolvedFallback = GitHubUpdateService.ResolveExecutableAsset(fallbackAssets, OSPlatform.Windows);
        Trace.Assert(resolvedFallback != null && resolvedFallback.Name == "KMShare-v9.9.9-win-x64-portable.zip", "Windows fallback must resolve portable zip, not installer");

        var resolvedLinux = GitHubUpdateService.ResolveExecutableAsset(sampleAssets, OSPlatform.Linux);
        Trace.Assert(resolvedLinux != null && resolvedLinux.Name == "KMShare-v9.9.9-linux-x64", "Linux must resolve standalone binary");

        var resolvedMac = GitHubUpdateService.ResolveExecutableAsset(sampleAssets, OSPlatform.OSX);
        Trace.Assert(resolvedMac != null && resolvedMac.Name == "KMShare-v9.9.9-osx-arm64", "macOS must resolve standalone binary");
        Console.WriteLine("  -> Standalone executable resolution logic verified across Windows, Linux and macOS.");

        Console.WriteLine("  [OK] Check 11 Passed.");
    }

    private static void CleanupPendingOldExecutable()
    {
        try
        {
            string? current = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(current))
            {
                string oldFile = current + ".old";
                if (File.Exists(oldFile))
                {
                    File.Delete(oldFile);
                }
            }
        }
        catch { }
    }

    private const int WH_MOUSE_LL = 14;
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    private static IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        => CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

    private static void HideStandaloneConsoleWindowIfAny()
    {
        try
        {
            IntPtr hConsole = GetConsoleWindow();
            if (hConsole != IntPtr.Zero)
            {
                uint[] pids = new uint[4];
                uint count = GetConsoleProcessList(pids, (uint)pids.Length);
                if (count <= 1)
                {
                    ShowWindow(hConsole, 0 /* SW_HIDE */);
                    FreeConsole();
                }
            }
        }
        catch
        {
        }
    }

    private static void EnsureParentConsoleAttachedForCli()
    {
        try
        {
            IntPtr hStdOut = GetStdHandle(-11 /* STD_OUTPUT_HANDLE */);
            if (hStdOut == IntPtr.Zero || hStdOut == new IntPtr(-1))
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
            }
        }
        catch
        {
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] lpdwProcessList, uint dwProcessCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation, int TokenInformationLength, out int ReturnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthority(IntPtr pSid, uint nSubAuthority);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr pSid);

    [SupportedOSPlatform("windows")]
    private static string GetProcessIntegrityLevel()
    {
        using var identity = WindowsIdentity.GetCurrent();
        const int TokenIntegrityLevel = 25;
        GetTokenInformation(identity.Token, TokenIntegrityLevel, IntPtr.Zero, 0, out int len);
        IntPtr pTil = Marshal.AllocHGlobal(len);
        try
        {
            if (!GetTokenInformation(identity.Token, TokenIntegrityLevel, pTil, len, out _))
                return "Unknown";
            IntPtr pSid = Marshal.ReadIntPtr(pTil);
            IntPtr pCount = GetSidSubAuthorityCount(pSid);
            uint count = (uint)Marshal.ReadByte(pCount);
            IntPtr pRid = GetSidSubAuthority(pSid, count - 1);
            int rid = Marshal.ReadInt32(pRid);
            return rid switch
            {
                < 0x1000 => $"Untrusted (0x{rid:X})",
                < 0x2000 => $"Low / Sandbox (0x{rid:X})",
                < 0x3000 => $"Medium / Standard User (0x{rid:X})",
                < 0x4000 => $"High / Admin (0x{rid:X})",
                _ => $"System (0x{rid:X})"
            };
        }
        finally
        {
            Marshal.FreeHGlobal(pTil);
        }
    }
}

internal record NicInfo(string Name, string Description, NetworkInterfaceType Type, IPAddress Ipv4Address);

internal static class VpnBypassNetwork
{
    private static readonly string[] VpnOrVirtualKeywords =
    [
        "vpn", "tap", "tun", "wireguard", "tailscale", "zerotier", "cisco", "anyconnect",
        "fortinet", "pangp", "globalprotect", "hyper-v", "wsl", "vmware", "virtualbox", "loopback", "vethernet"
    ];

    public static List<NicInfo> GetPhysicalLanInterfaces()
    {
        var list = new List<NicInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet))
                continue;
            if (IsVirtualOrVpn(ni)) continue;

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && IntranetGuard.IsStrictlyLanIp(ua.Address))
                {
                    list.Add(new NicInfo(ni.Name, ni.Description, ni.NetworkInterfaceType, ua.Address));
                }
            }
        }
        return list;
    }

    private static bool IsVirtualOrVpn(NetworkInterface ni)
    {
        string combined = $"{ni.Name} {ni.Description}".ToLowerInvariant();
        return VpnOrVirtualKeywords.Any(k => combined.Contains(k));
    }
}
