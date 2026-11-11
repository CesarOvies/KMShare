# AGENTS.md — Guia de Contexto, Arquitetura e Regras para Agentes de IA (KMShare)

Este documento fornece o contexto técnico completo, invariantes de segurança, diretrizes de UI e procedimentos de verificação obrigatórios para qualquer agente de IA que atue no repositório **KMShare**.

---

## 1. Visão Geral do Sistema

O **KMShare** é um software KVM virtual (Mouse e Teclado por travessia de borda) e de transferência de arquivos criptografados ponto-a-ponto para Intranet e Bluetooth, construído em **.NET 10 (`net10.0`)** sem nenhuma dependência de pacotes NuGet de terceiros, compilando em binário único (*Single-File*) para `win-x64`, `linux-x64` e `osx-arm64`.

### Módulos Principais
- **`src/Security/`**:
  - [`IntranetGuard.cs`](src/Security/IntranetGuard.cs): Garante que 100% dos endpoints IP sejam privados (RFC1918, Link-Local, ULA ou Loopback). Qualquer IP público lança `IntranetSecurityException`.
  - [`SecureHidSession.cs`](src/Security/SecureHidSession.cs): Handshake ECDH P-256 + HKDF-SHA256 (com PIN como salt), cifragem AES-256-GCM com contador monotônico de 64 bits, cofre `TrustedPeerVault` (DPAPI no Windows / `0600` AES-GCM no Linux/macOS) e Kill-Switch anti-anomalia.
  - [`FileTransferGatekeeper.cs`](src/Security/FileTransferGatekeeper.cs): Autorização Step-Up por transação (`CredUIPromptForWindowsCredentialsW`, `pkexec`, `osascript`) com emissão de token HMAC único de 48 bytes válido por 60 segundos.
- **`src/Network/`**:
  - [`MKSharePeerNode.cs`](src/Network/MKSharePeerNode.cs): Transporte LAN UDP (`:51820`), receptor Dual-Stack (UDP + Bluetooth RFCOMM + Canal de Arquivos TCP `:51821` + resposta a probes de descoberta).
  - [`NetworkPeerDiscovery.cs`](src/Network/NetworkPeerDiscovery.cs): Descoberta automática de dispositivos na LAN via Beacon UDP (`:51822` e `:51820`), varredura paralela Camada 2 `SendARP` na sub-rede `/24` e enumeração de dispositivos Bluetooth pareados.
  - [`BluetoothRfcommNode.cs`](src/Network/BluetoothRfcommNode.cs): Transporte Out-of-Band RFCOMM (`AF_BTH=32` no Windows e `AF_BLUETOOTH=31` no Linux BlueZ, Canal `5`).
  - [`VpnFallbackModes.cs`](src/Network/VpnFallbackModes.cs): Orquestrador dos Modos A (LAN Direta), B (Step-Up `/32` Rota + Firewall) e C (Bluetooth Out-of-Band com `BluetoothMouseCoalescer` 1000Hz -> 133Hz).
  - [`VpnKillSwitchProbe.cs`](src/Network/VpnKillSwitchProbe.cs): Diagnóstico comparativo Camada 2 (`SendARP`) vs Camada 3 (ICMP, LLMNR, mDNS, NetBIOS).
- **`src/Platform/`**:
  - [`IHidPlatform.cs`](src/Platform/IHidPlatform.cs): Interface comum de captura/injeção HID e `EdgeCoordinateMapper` (escala normalizada `0..10000`).
  - [`WindowsHidEngine.cs`](src/Platform/WindowsHidEngine.cs), [`LinuxHidEngine.cs`](src/Platform/LinuxHidEngine.cs), [`MacHidEngine.cs`](src/Platform/MacHidEngine.cs): Motores nativos de captura e injeção em User-Space.
- **`src/FileTransfer/`**:
  - [`SecureFileChannel.cs`](src/FileTransfer/SecureFileChannel.cs): Canal TCP dedicado (`:51821`) em 3 etapas com arquivo temporário `.part` e validação SHA-256 antes do commit.
- **`src/UI/`**:
  - [`KMShareGuiWindow.cs`](src/UI/KMShareGuiWindow.cs): Interface gráfica nativa Win32 responsiva (Double-Buffered GDI, cartões arredondados, botões `BS_OWNERDRAW` com hover, diagrama visual de monitores, layout dinâmico em `WM_SIZE` e escala High-DPI).
  - [`TrayAndEdgeManager.cs`](src/UI/TrayAndEdgeManager.cs): Persistência de configuração (`%LOCALAPPDATA%\KMShare\config.json`), registro de inicialização automática em User-Space e menu TUI.

---

## 2. Regras Inegociáveis para Agentes de IA

1. **Zero Dependências Externas e Compilação Multiplataforma (`net10.0`):**
   - Nunca adicione `<PackageReference>` ou altere `<TargetFramework>net10.0</TargetFramework>` para `net10.0-windows`, pois o mesmo `KMShare.csproj` precisa compilar para `win-x64`, `linux-x64` e `osx-arm64`.
2. **Execução Estrita em User-Space (`asInvoker`):**
   - Preserve `requestedExecutionLevel level="asInvoker" uiAccess="false"` em [`app.manifest`](app.manifest).
3. **Isolamento de Intranet (`IntranetGuard.EnforceLanEndpoint`):**
   - Todo socket UDP/TCP criado em `src/` deve validar tanto o endpoint local quanto o remoto com `IntranetGuard.EnforceLanEndpoint`.
   - Em Windows, todo `UdpClient` deve aplicar `SIO_UDP_CONNRESET` (`-1744830452`) para evitar queda de socket por ICMP Port Unreachable.
4. **Anti-Regressão de Hooks de Mouse e Teclado (`WindowsHidEngine.cs`):**
   - **Jamais** chame `SetCursorPos` a cada frame dentro de `MouseHookCallback` (`WH_MOUSE_LL`). O cursor só é reposicionado na transição `SetRemoteActive(true/false)` com `_suppressNextWarpEvent = true`.
   - O loop `RunSenderCaptureLoop` deve rodar em thread de background quando acionado pela GUI para não bloquear nem encerrar a fila de mensagens da janela principal ao receber `WM_QUIT`.
   - Mantenha o Watchdog de `2500ms` (`IsPeerAlive()`) e a tecla de emergência `[F12]` sempre ativos.
5. **Diretrizes de UI (`KMShareGuiWindow.cs`):**
   - Ao adicionar ou modificar controles na GUI, posicione-os exclusivamente dentro de `LayoutControls(int width, int height)` usando `DeferControl` e escala DPI `S(px)`.
   - Use botões `CreateOwnerDrawButton` (`BS_OWNERDRAW`) com `ButtonStyleVariant` apropriado e desenhe cartões no `PaintWindow` (double-buffered) para manter a estética limpa e sem flicker.

---

## 3. Checklist Obrigatório de Validação

Antes de concluir qualquer tarefa neste repositório, execute e confirme `0 Warning(s)` e `0 Error(s)`:

```powershell
dotnet build KMShare.csproj
dotnet build KMShare.csproj -r win-x64
dotnet build KMShare.csproj -r linux-x64
dotnet build KMShare.csproj -r osx-arm64
dotnet build updater\KMShare.Updater\KMShare.Updater.csproj
dotnet run --project KMShare.csproj -- --self-test
```

