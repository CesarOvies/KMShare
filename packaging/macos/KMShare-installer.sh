#!/usr/bin/env bash
# ==============================================================================
# KMShare — macOS User-Space Installer (Apple Silicon / osx-arm64)
# ==============================================================================
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TARGET_DIR="/Applications"
USER_TARGET_DIR="$HOME/Applications"

echo "=========================================================="
echo "  KMShare — Instalador macOS (Apple Silicon osx-arm64)"
echo "=========================================================="

APP_SOURCE="$SCRIPT_DIR/KMShare.app"
if [ ! -d "$APP_SOURCE" ]; then
    echo "Erro: KMShare.app não encontrado em $SCRIPT_DIR"
    exit 1
fi

DEST_DIR="$TARGET_DIR"
if [ ! -w "$TARGET_DIR" ]; then
    echo "[INFO] /Applications requer permissões de administrador. Instalando em $USER_TARGET_DIR..."
    mkdir -p "$USER_TARGET_DIR"
    DEST_DIR="$USER_TARGET_DIR"
fi

echo "[1/3] Copiando KMShare.app para $DEST_DIR..."
rm -rf "$DEST_DIR/KMShare.app"
cp -R "$APP_SOURCE" "$DEST_DIR/KMShare.app"

echo "[2/3] Removendo atributos de quarentena do Gatekeeper..."
xattr -dr com.apple.quarantine "$DEST_DIR/KMShare.app" 2>/dev/null || true

echo "[3/3] Criando comando CLI 'kmshare'..."
CLI_TARGET="/usr/local/bin"
if [ -w "$CLI_TARGET" ]; then
    ln -sf "$DEST_DIR/KMShare.app/Contents/MacOS/KMShare" "$CLI_TARGET/kmshare" 2>/dev/null || true
fi

echo "=========================================================="
echo "  KMShare instalado com sucesso em $DEST_DIR/KMShare.app!"
echo ""
echo "  NOTA DE SEGURANÇA DO MACOS:"
echo "  Para habilitar o KVM (compartilhamento de mouse e teclado):"
echo "  Abra: Ajustes do Sistema -> Privacidade e Segurança -> Acessibilidade"
echo "  e marque a caixa de seleção ao lado de 'KMShare'."
echo "=========================================================="

