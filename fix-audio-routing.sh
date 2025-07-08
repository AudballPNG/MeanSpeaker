#!/bin/bash

echo "🎵 Fixing Audio Routing for Bluetooth Speaker..."

# Stop any existing audio services
sudo systemctl stop bluealsa-aplay
sudo systemctl stop bluealsa

# Install required packages if missing
echo "Installing required packages..."
sudo apt-get update
sudo apt-get install -y bluealsa alsa-utils

# Configure BlueALSA for A2DP sink
echo "Configuring BlueALSA..."
sudo tee /etc/systemd/system/bluealsa.service > /dev/null << 'EOF'
[Unit]
Description=BlueALSA service
After=bluetooth.service
Requires=bluetooth.service

[Service]
Type=simple
ExecStart=/usr/bin/bluealsa -p a2dp-sink -p a2dp-source
Restart=on-failure

[Install]
WantedBy=multi-user.target
EOF

# Create BlueALSA audio routing service (critical for audio to work)
echo "Setting up audio routing..."
sudo tee /etc/systemd/system/bluealsa-aplay.service > /dev/null << 'EOF'
[Unit]
Description=BlueALSA audio routing service
After=bluealsa.service sound.target
Requires=bluealsa.service

[Service]
Type=simple
ExecStart=/usr/bin/bluealsa-aplay --pcm-buffer-time=250000 00:00:00:00:00:00
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

# Configure audio for better Bluetooth performance
echo "Configuring audio settings..."
sudo tee /etc/asound.conf > /dev/null << 'EOF'
defaults.bluealsa.interface "hci0"
defaults.bluealsa.profile "a2dp"
defaults.bluealsa.delay 20000
defaults.bluealsa.battery "yes"
EOF

# Reload systemd, enable and start services
echo "Starting audio services..."
sudo systemctl daemon-reload
sudo systemctl enable bluealsa
sudo systemctl start bluealsa
sudo systemctl enable bluealsa-aplay
sudo systemctl start bluealsa-aplay

# Set audio levels
echo "Setting audio levels..."
amixer sset Master,0 90%
amixer sset PCM,0 90%

# Restart Bluetooth to ensure changes take effect
echo "Restarting Bluetooth..."
sudo systemctl restart bluetooth

# Verify services are running
echo "Verifying audio routing setup..."
if systemctl is-active --quiet bluealsa-aplay; then
    echo "✅ Audio routing service is active and running"
else
    echo "❌ Audio routing service is not running!"
    echo "Manual fix: sudo systemctl start bluealsa-aplay"
fi

echo "✅ Audio routing fix complete!"
echo "Connect your device and test audio playback."
echo "If it still doesn't work, try rebooting: sudo reboot"
