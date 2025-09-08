#!/bin/bash
set -euo pipefail

SERVICE_NAME="meanspeaker"
INSTALL_DIR="/opt/meanspeaker"
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
USER_NAME="meanspeaker"

echo "[MeanSpeaker] Stopping and disabling service..."
sudo systemctl stop "$SERVICE_NAME" || true
sudo systemctl disable "$SERVICE_NAME" || true

echo "[MeanSpeaker] Removing service file..."
sudo rm -f "$SERVICE_FILE"
sudo systemctl daemon-reload || true

echo "[MeanSpeaker] Removing install directory..."
sudo rm -rf "$INSTALL_DIR"

echo "[MeanSpeaker] (Optional) Remove service user: $USER_NAME"
echo "Run: sudo userdel $USER_NAME && sudo rm -rf /home/$USER_NAME"

echo "[MeanSpeaker] Uninstall complete."
