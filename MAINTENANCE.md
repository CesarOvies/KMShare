# KMShare — Manual de Manutenção, Sustentação e Invariantes para Agentes

> **ESCOPO:** Referência técnica obrigatória para agentes de IA encarregados da manutenção corretiva, evolutiva e sustentação do **KMShare**. Qualquer alteração de código deve preservar 100% dos invariantes arquiteturais, criptográficos e de sistema operacional abaixo.

---

## 1. Mapa Arquitetural do Sistema

```
KMShare/
├── MAINTENANCE.md                             # Manual de manutenção, invariantes e protocolos para agentes
├── app.manifest                               # Manifesto User-Space (asInvoker, uiAccess=false)
├── build-all.ps1                              # Build Single-File para win-x64, linux-x64 e osx-arm64
├── KMShare.csproj                             # Executável principal (100% Intranet)
├── Program.cs                                 # Entrypoint CLI / Tray + Suíte de Verificação (--self-test)
├── src/
│   ├── Security/
│   │   ├── IntranetGuard.cs                   # Validação estrita de IPs privados RFC1918/Link-Local/ULA
│   │   ├── SecureHidSession.cs                # ECDH P-256 + HKDF-SHA256 + AES-256-GCM + Kill-Switch + TrustedPeerVault
│   │   └── FileTransferGatekeeper.cs          # Step-Up Auth por transação + Token HMAC de uso único (48 bytes)
│   ├── Network/
│   │   ├── MKSharePeerNode.cs                 # Transporte LAN UDP (:51820), Heartbeat 500ms, Reconexão e WireEdgeEnter/Return
│   │   ├── NetworkPeerDiscovery.cs            # Descoberta LAN (Beacon UDP :51822/:51820 + Sub-rede /24 SendARP L2 + Bluetooth)
│   │   ├── BluetoothRfcommNode.cs             # Transporte Out-of-Band RFCOMM (Win AF_BTH=32 / Linux BlueZ AF_BLUETOOTH=31)
│   │   ├── VpnFallbackModes.cs                # Orquestrador Modos A/B/C + BluetoothMouseCoalescer (1000Hz -> 133Hz)
│   │   └── VpnKillSwitchProbe.cs              # Diagnóstico Camada 2 SendARP vs Camada 3 WFP
│   ├── Platform/
│   │   ├── IHidPlatform.cs                    # Interface multiplataforma + EdgeCoordinateMapper (escala 0..10000)
│   │   ├── WindowsHidEngine.cs                # Win32 WH_MOUSE_LL/WH_KEYBOARD_LL + Watchdog 2.5s + Fallback UIPI
│   │   ├── LinuxHidEngine.cs                  # X11 (libX11/libXtst) + Wayland (xdg-desktop-portal D-Bus / libei)
│   │   └── MacHidEngine.cs                    # macOS CoreGraphics CGEventTap (kCGSessionEventTap) + AXIsProcessTrusted
│   ├── FileTransfer/
│   │   └── SecureFileChannel.cs               # Canal TCP dedicado (:51821) em 3 etapas com arquivo .part e validação SHA-256
│   └── UI/
│       ├── KMShareGuiWindow.cs                # Interface Gráfica Nativa (Win32 User-Space) p/ Operação, Papel, Scanner e Modos
│       └── TrayAndEdgeManager.cs              # System Tray leve (Shell_NotifyIconW), TUI e Autostart User-Space
└── updater/
    └── KMShare.Updater/
        ├── KMShare.Updater.csproj             # Binário separado para atualização via HTTPS
        └── Program.cs                         # Verificador Ed25519 (RFC 8032) + RSA-PSS + SHA-256
```

---

## 2. Invariantes de Segurança e Operação (NUNCA VIOLAR)

1. **Execução 100% User-Space (`asInvoker`):**
   - [`app.manifest`](app.manifest) mantém `requestedExecutionLevel level="asInvoker" uiAccess="false"`.
   - Captura/injeção HID em Windows ([`WindowsHidEngine.cs`](src/Platform/WindowsHidEngine.cs)), Linux ([`LinuxHidEngine.cs`](src/Platform/LinuxHidEngine.cs)) e macOS ([`MacHidEngine.cs`](src/Platform/MacHidEngine.cs)) opera sem privilégios de administrador (`root`/`sudo`).
   - Registro de inicialização automática ([`TrayAndEdgeManager.cs`](src/UI/TrayAndEdgeManager.cs)) opera exclusivamente no escopo do usuário (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, `~/.config/autostart`, `~/Library/LaunchAgents`).

2. **Isolamento Estrito de Intranet no Core (`IntranetGuard`):**
   - Todo endpoint IP em [`MKSharePeerNode.cs`](src/Network/MKSharePeerNode.cs) e [`SecureFileChannel.cs`](src/FileTransfer/SecureFileChannel.cs) passa obrigatoriamente por [`IntranetGuard.EnforceLanEndpoint`](src/Security/IntranetGuard.cs).
   - Apenas faixas RFC1918 (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`), Link-Local (`169.254.0.0/16`, `fe80::/10`), ULA (`fc00::/7`) e Loopback são permitidas. Qualquer IP público dispara `IntranetSecurityException`.
   - O processo principal `KMShare` **jamais** realiza conexões à Internet.

3. **Protocolo HID Binário Fixo (6 Bytes) + Sessão Criptográfica + Cofre DPAPI/Keyring:**
   - [`HidFrame`](src/Security/SecureHidSession.cs) possui exatamente 6 bytes: `Magic (0x4D) + Type (0x01..0x03) + Arg1 (int16 LE) + Arg2 (int16 LE)`.
   - [`SecureHidSession`](src/Security/SecureHidSession.cs) usa **ECDH P-256 + HKDF-SHA256 + AES-256-GCM** com contador monotônico de 64 bits.
   - [`TrustedPeerVault`](src/Security/SecureHidSession.cs) persiste chaves públicas de pares confiáveis protegidas por DPAPI (`CryptProtectData` User-Scope no Windows) ou AES-256-GCM (`0600` no Linux/macOS).
   - **Kill-Switch Imediato:** Qualquer status `Revoked*` (`RevokedCryptoTamperOrReplay`, `RevokedNonHidPayload`, `RevokedSuperhumanRateAnomaly`, `RevokedSuspiciousShellShortcut`) aciona `RevokeAuthentication()`, zera a chave de sessão na RAM (`CryptographicOperations.ZeroMemory`), **deleta imediatamente a chave do par no `TrustedPeerVault`** e libera todas as teclas modificadoras no receptor (`ReleaseAllModifierKeysOnReceiver`).

4. **Transferência de Arquivos em Canal Dedicado com Step-Up Auth por Transação:**
   - [`SecureFileChannel.cs`](src/FileTransfer/SecureFileChannel.cs) opera em canal TCP/Stream separado do canal HID (`51821`), criptografado com ECDH P-256 + AES-256-GCM.
   - [`FileTransferGatekeeper.cs`](src/Security/FileTransferGatekeeper.cs) exige aprovação por transação via prompt nativo do SO (`CredUIPromptForWindowsCredentialsW` no Windows, `pkexec` no Linux, `osascript` no macOS) e emite token HMAC único de 48 bytes válido por 60s vinculado a `Nonce | FileName | FileSize | SHA256`.
   - **Fail-Closed:** O token é consumido na primeira chamada a `TryCommitTransfer`. Divergência de hash ou token inválido apaga o arquivo `.part` imediatamente e queima o token.

5. **Resiliência a VPN com Kill-Switch (Modos A, B e C):**
   - Enumeração de interfaces físicas ignora adaptadores virtuais/VPN (`vpn`, `tap`, `tun`, `wg`, `wsl`, `hyper-v`, `vethernet`).
   - Diagnóstico Camada 2 via `iphlpapi.dll!SendARP` ([`VpnKillSwitchProbe.cs`](src/Network/VpnKillSwitchProbe.cs)).
   - Transporte alternativo Out-of-Band via Bluetooth RFCOMM ([`BluetoothRfcommNode.cs`](src/Network/BluetoothRfcommNode.cs)): Windows `AF_BTH (32)` e Linux BlueZ `AF_BLUETOOTH (31)` com [`BluetoothMouseCoalescer`](src/Network/VpnFallbackModes.cs) (`7.5ms` / `133Hz`).

6. **Atualizador Isolado com Verificação Criptográfica Ed25519 / RSA-PSS:**
   - [`updater/KMShare.Updater/Program.cs`](updater/KMShare.Updater/Program.cs) valida esquema `https://`, hash SHA-256 em tempo constante (`FixedTimeEquals`) e assinatura digital **Ed25519 (RFC 8032)** / **RSA-PSS** contra a chave pública oficial antes de atualizar o binário em `%LOCALAPPDATA%\Programs\KMShare`.

---

## 3. Regras Anti-Regressão de Sistema Operacional (Crítico na Manutenção)

1. **Proibido chamar `SetCursorPos` por frame dentro de `WH_MOUSE_LL` ([`WindowsHidEngine.cs`](src/Platform/WindowsHidEngine.cs)):**
   - Quando `MouseHookCallback` retorna `(IntPtr)1`, o Windows mantém o cursor congelado na âncora.
   - Chame `SetCursorPos` **apenas uma vez** na transição `SetRemoteActive(true)` / `SetRemoteActive(false)` com `_suppressNextWarpEvent = true`. Nos eventos `WM_MOUSEMOVE` seguintes com `_isRemoteActive == true`, apenas leia `GetCursorPos(out currentAnchor)` e calcule `dx = info.pt.X - currentAnchor.X`, `dy = info.pt.Y - currentAnchor.Y`.

2. **Watchdog de 2.5s (`PeerDeadTimeoutMs = 2500`) e Tecla de Emergência `[F12]`:**
   - O heartbeat bidirecional ocorre a cada `500ms` (`WireHeartbeatPing = 0x40` -> `WireHeartbeatAck = 0x41`).
   - `IsPeerAlive()` deve permanecer verificado tanto no `Timer` de background (250ms) quanto na primeira linha de `MouseHookCallback` / `KeyboardHookCallback` / `EventTapCallback`. Se `false`, destravar o input local imediatamente.

3. **Filtro de Auto-Repeat Tipemático em [`SecureHidSession.cs`](src/Security/SecureHidSession.cs):**
   - `OpenAndValidatePacket` ignora teclas modificadoras (`0x5B, 0x5C, 0x1D, 0x2A, 0x36, 0x38`) e repetições consecutivas do mesmo `scancode` (`scancode == _lastKeyDownScancode`), contando apenas transições de teclas distintas para evitar falso positivo ao segurar teclas.

4. **Fallback UIPI no Receptor Windows ([`WindowsHidEngine.cs`](src/Platform/WindowsHidEngine.cs)):**
   - Se `SendInput` retornar `0` em `MouseMove` (janela elevada em foco no receptor), manter o fallback imediato com `GetCursorPos` + `SetCursorPos`.

5. **Proteção `SIO_UDP_CONNRESET` no Windows ([`MKSharePeerNode.cs`](src/Network/MKSharePeerNode.cs)):**
   - Todo `UdpClient` no Windows deve chamar `DisableWindowsUdpConnReset` (`IOControl(-1744830452)`) após a criação.

---

## 4. Especificação dos Protocolos de Rede (Wire Format)

### 4.1 Canal HID e Descoberta LAN (UDP `:51820`, Descoberta `:51822` e Bluetooth RFCOMM Canal `5`)
- `0x01` (`WireDiscoveryProbe`): `[0x01, .. "KMSHARE_DISCOVERY_V1"u8]`
- `0x02` (`WireDiscoveryAnnounce`): `[0x02, .. UTF8_JSON({ Hostname, LanIp, BluetoothMac, Role, UdpPort, FilePort, MonitorEdge })]`
- `0x10` (`WireHello`): `[0x10, .. SenderSpkiBytes]`
- `0x11` (`WireHelloAck`): `[0x11, .. ReceiverSpkiBytes]`
- `0x20` (`WireHidPacket`): `[0x20, Seq(8B LE), Ciphertext(6B), AesGcmTag(16B)]`
  - Payload decifrado (`HidFrame`, 6 bytes): `[0x4D ('M'), Type(1=Move, 2=Btn, 3=Key), Arg1(i16 LE), Arg2(i16 LE)]`
- `0x2F` (`WireEdgeEnter`): `[0x2F, ScreenEdge(1B), NormalizedCoord(u16 LE, 0..10000)]`
- `0x30` (`WireEdgeReturn`): `[0x30, ScreenEdge(1B), NormalizedCoord(u16 LE, 0..10000)]`
- `0x40` (`WireHeartbeatPing`): `[0x40]` (enviado a cada `500ms`)
- `0x41` (`WireHeartbeatAck`): `[0x41]` (timeout de morte do peer: `2500ms`)
- `0xFF` (`WireRevoked`): `[0xFF, PacketStatus(1B)]`

### 4.2 Canal de Transferência de Arquivos (TCP `:51821` — [`SecureFileChannel.cs`](src/FileTransfer/SecureFileChannel.cs))
1. **Handshake:** `FrameHello (0x10)` / `FrameHelloAck (0x11)` -> Deriva chave de sessão via `ECDH P-256 + HKDF-SHA256 (info="MKSHARE_FILE_V1")`.
2. **Etapa 1 (`FrameOffer = 0x20`):** Envia JSON cifrado `{ FileName, FileSize, Sha256Hex }`.
3. **Etapa 2 (`FrameTokenIssued = 0x21` / `FrameTokenDenied = 0x22`):** Receptor solicita consentimento do usuário via `OsStepUpAuth` e retorna o `OneTimeToken` de 48 bytes (`Nonce[16] + HMAC_SHA256[32]`).
4. **Etapa 3 (`FrameChunk = 0x30` + `FrameCommit = 0x31` -> `FrameResult = 0x40`):** Blocos de 16 KB são gravados em `.part`, validados contra `FileTransferGatekeeper.TryCommitTransfer` e só então comitados no destino final.

---

## 5. Procedimento Padrão de Verificação e Sustentação

Sempre que realizar manutenção em qualquer arquivo de `src/` ou `updater/`:

1. **Compilação Multiplataforma Limpa (Zero Warnings / Zero Errors):**
   ```powershell
   dotnet build KMShare.csproj
   dotnet build KMShare.csproj -r win-x64
   dotnet build KMShare.csproj -r linux-x64
   dotnet build KMShare.csproj -r osx-arm64
   dotnet build updater\KMShare.Updater\KMShare.Updater.csproj
   ```
2. **Execução da Suíte Completa de Autoteste:**
   ```powershell
   dotnet run --project KMShare.csproj -- --self-test
   ```
3. **Empacotamento Single-File de Produção:**
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\build-all.ps1
   ```

