---
description: Diretrizes obrigatórias de manutenção, sustentação, segurança, UI e invariantes de SO do projeto KMShare.
---

# Regras do Workspace KMShare para Agentes de IA

Antes de modificar qualquer arquivo neste repositório, consulte [`MAINTENANCE.md`](../../MAINTENANCE.md) e [`AGENTS.md`](../../AGENTS.md) na raiz do repositório.

## Invariantes Críticos
1. **User-Space Estrito (`asInvoker`):** Mantenha `requestedExecutionLevel level="asInvoker" uiAccess="false"` em `app.manifest`. Nenhuma funcionalidade padrão (hooks HID, descoberta LAN/Bluetooth, autostart, GUI) pode exigir administrador.
2. **Isolamento 100% Intranet (`IntranetGuard`):** Todo endpoint de rede em `src/Network/` e `src/FileTransfer/` deve passar por `IntranetGuard.EnforceLanEndpoint`. Todo `UdpClient` no Windows deve desativar `SIO_UDP_CONNRESET` (`-1744830452`).
3. **Protocolo HID (6 Bytes) + Sessão Criptográfica + Kill-Switch:** Preserve o handshake ECDH P-256 + HKDF-SHA256 + AES-256-GCM em `SecureHidSession.cs`, o cofre `TrustedPeerVault` e o Kill-Switch imediato em qualquer `PacketStatus.Revoked*`.
4. **Hooks de Sistema Operacional (`WindowsHidEngine.cs`):**
   - Nunca chame `SetCursorPos` por frame dentro de `WH_MOUSE_LL`.
   - Preserve o Watchdog de `2500ms` (`PeerDeadTimeoutMs`) e o atalho de emergência `[F12]`.
5. **Interface Gráfica Responsiva (`KMShareGuiWindow.cs`):**
   - Mantenha o layout responsivo calculado em `LayoutControls(int width, int height)` via `WM_SIZE` + `DeferWindowPos` com escala High-DPI `S(px)`.
   - Utilize botões owner-drawn (`BS_OWNERDRAW`) e renderização double-buffered em `PaintWindow` sem `BS_GROUPBOX` legados.
6. **Validação Obrigatória:**
   Toda alteração deve compilar sem avisos/erros para `win-x64`, `linux-x64` e `osx-arm64` e passar integralmente nos 10 testes de `dotnet run --project KMShare.csproj -- --self-test`.
