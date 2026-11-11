#!/usr/bin/env bash
# ==============================================================================
# KMShare — Linux User-Space Installer (Zero-Sudo / asInvoker)
# ==============================================================================
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="$HOME/.local/share/Programs/KMShare"
BIN_DIR="$HOME/.local/bin"
DESKTOP_DIR="$HOME/.local/share/applications"
ICON_DIR="$HOME/.local/share/icons/hicolor/scalable/apps"

echo "=========================================================="
echo "  KMShare — Instalador Linux (User-Space / Zero-Sudo)"
echo "=========================================================="

mkdir -p "$INSTALL_DIR"
mkdir -p "$BIN_DIR"
mkdir -p "$DESKTOP_DIR"
mkdir -p "$ICON_DIR"

echo "[1/4] Instalando binários em $INSTALL_DIR..."
if [ -f "$SCRIPT_DIR/KMShare" ]; then
    cp -f "$SCRIPT_DIR/KMShare" "$INSTALL_DIR/KMShare"
    chmod +x "$INSTALL_DIR/KMShare"
fi

if [ -f "$SCRIPT_DIR/kmshare-updater" ]; then
    cp -f "$SCRIPT_DIR/kmshare-updater" "$INSTALL_DIR/kmshare-updater"
    chmod +x "$INSTALL_DIR/kmshare-updater"
fi

echo "[2/4] Criando links simbólicos em $BIN_DIR..."
ln -sf "$INSTALL_DIR/KMShare" "$BIN_DIR/kmshare"
ln -sf "$INSTALL_DIR/kmshare-updater" "$BIN_DIR/kmshare-updater"

echo "[3/4] Instalando ícones e atalho no menu de aplicativos..."
if [ -f "$SCRIPT_DIR/logo.svg" ]; then
    cp -f "$SCRIPT_DIR/logo.svg" "$ICON_DIR/kmshare.svg"
elif [ -f "$SCRIPT_DIR/kmshare.svg" ]; then
    cp -f "$SCRIPT_DIR/kmshare.svg" "$ICON_DIR/kmshare.svg"
fi

if [ -f "$SCRIPT_DIR/kmshare.desktop" ]; then
    cp -f "$SCRIPT_DIR/kmshare.desktop" "$DESKTOP_DIR/kmshare.desktop"
    chmod +x "$DESKTOP_DIR/kmshare.desktop"
    if command -v update-desktop-database >/dev/null 2>&1; then
        update-desktop-database "$DESKTOP_DIR" 2>/dev/null || true
    fi
fi

echo "[4/4] Verificando PATH do usuário..."
case ":$PATH:" in
    *":$BIN_DIR:"*) ;;
    *)
        echo "  [AVISO] Adicione $BIN_DIR ao seu PATH em ~/.bashrc ou ~/.zshrc:"
        echo "  export PATH=\"\$HOME/.local/bin:\$PATH\""
        ;;
esac

echo "=========================================================="
echo "  KMShare instalado com sucesso no Linux!"
echo "  Para iniciar via terminal: kmshare --gui"
echo "  Ou abra 'KMShare' no seu menu de aplicativos."
echo "=========================================================="

