#!/bin/bash

set -euo pipefail

# MeanSpeaker production installer (systemd service + self-contained publish)
# - Publishes a self-contained Linux ARM build of BluetoothSpeaker
# - Installs to /opt/meanspeaker
# - Creates/updates a systemd service: meanspeaker.service

APP_NAME="BluetoothSpeaker"
INSTALL_DIR="/opt/meanspeaker"
SERVICE_NAME="meanspeaker"
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
USER_NAME="meanspeaker"

echo "[MeanSpeaker] Detecting architecture..."
ARCH=$(uname -m)
case "$ARCH" in
  aarch64) RID="linux-arm64" ;;
  armv7l|armv6l|arm) RID="linux-arm" ;;
  *) echo "Unsupported arch: $ARCH. This script targets Raspberry Pi (ARM)." ; exit 1 ;;
esac
echo "[MeanSpeaker] Using RID: $RID"

echo "[MeanSpeaker] Ensuring required tools are installed..."
command -v dotnet >/dev/null 2>&1 || { echo "dotnet not found. Install .NET 8 SDK and retry."; exit 1; }
command -v systemctl >/dev/null 2>&1 || { echo "systemctl not found. This must run on a system with systemd."; exit 1; }

echo "[MeanSpeaker] Publishing self-contained release..."
PUBLISH_DIR="bin/Release/net8.0/${RID}/publish"
dotnet publish "${APP_NAME}.csproj" -c Release -r "$RID" --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:InvariantGlobalization=true >/dev/null

if [ ! -f "$PUBLISH_DIR/${APP_NAME}" ]; then
  echo "Publish output not found at $PUBLISH_DIR/${APP_NAME}"; exit 1
fi

echo "[MeanSpeaker] Creating user and install directory..."
if ! id -u "$USER_NAME" >/dev/null 2>&1; then
  sudo useradd --system --create-home --shell /usr/sbin/nologin "$USER_NAME" || true
fi

sudo mkdir -p "$INSTALL_DIR"
sudo cp -f "$PUBLISH_DIR/${APP_NAME}" "$INSTALL_DIR/"
sudo cp -rf "$PUBLISH_DIR"/* "$INSTALL_DIR/" >/dev/null 2>&1 || true
sudo chown -R "$USER_NAME":"$USER_NAME" "$INSTALL_DIR"
sudo chmod 755 "$INSTALL_DIR/${APP_NAME}"

echo "[MeanSpeaker] Writing systemd service: $SERVICE_FILE"
sudo tee "$SERVICE_FILE" >/dev/null <<EOF
[Unit]
Description=MeanSpeaker - Snarky Bluetooth Speaker
After=bluetooth.service bluealsa.service network.target
Wants=bluetooth.service bluealsa.service

[Service]
Type=simple
User=${USER_NAME}
Group=${USER_NAME}
WorkingDirectory=${INSTALL_DIR}
ExecStart=${INSTALL_DIR}/${APP_NAME} --tts piper
Restart=always
RestartSec=5
Environment=DOTNET_EnableWriteXorExecute=0
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
# Optionally pass API key if using cloud mode (default is local AI)
# Environment=OPENAI_API_KEY=your_key_here

[Install]
WantedBy=multi-user.target
EOF

echo "[MeanSpeaker] Reloading and enabling service..."
sudo systemctl daemon-reload
sudo systemctl enable "$SERVICE_NAME"
sudo systemctl restart "$SERVICE_NAME"

echo ""
echo "[MeanSpeaker] Deployed! Service status:" 
sudo systemctl --no-pager --full status "$SERVICE_NAME" || true
echo ""
echo "Controls:" 
echo "  sudo systemctl restart ${SERVICE_NAME}"
echo "  sudo systemctl stop ${SERVICE_NAME}"
echo "  journalctl -u ${SERVICE_NAME} -f"
