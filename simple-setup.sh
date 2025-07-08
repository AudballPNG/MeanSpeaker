#!/bin/bash

# Simple Bluetooth Speaker Setup for Raspberry Pi
echo "🎵 Setting up Simple Bluetooth Speaker..."

# Update system
echo "📦 Updating packages..."
sudo apt-get update

# Install required packages
echo "🔧 Installing Bluetooth and audio packages..."
sudo apt-get install -y \
    bluetooth \
    bluez \
    bluez-tools \
    bluealsa \
    alsa-utils \
    playerctl \
    espeak

# Enable and start Bluetooth
echo "🔵 Configuring Bluetooth..."
sudo systemctl enable bluetooth
sudo systemctl start bluetooth

# Configure Bluetooth for A2DP sink
echo "🎧 Setting up A2DP audio sink..."
sudo tee /etc/bluetooth/main.conf > /dev/null << 'EOF'
[General]
Name = The Little Shit
Class = 0x240404
DiscoverableTimeout = 0
PairableTimeout = 0

[Policy]
AutoEnable=true
EOF

# Create BlueALSA service
echo "🎵 Setting up BlueALSA..."
sudo tee /etc/systemd/system/bluealsa.service > /dev/null << 'EOF'
[Unit]
Description=BlueALSA service
After=bluetooth.service
Requires=bluetooth.service

[Service]
Type=simple
ExecStart=/usr/bin/bluealsa -p a2dp-sink
Restart=on-failure

[Install]
WantedBy=multi-user.target
EOF

# Create audio routing service (CRITICAL for audio playback)
echo "🔊 Setting up audio routing..."
sudo tee /etc/systemd/system/bluealsa-aplay.service > /dev/null << 'EOF'
[Unit]
Description=BlueALSA audio routing
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

# Enable and start services
echo "🚀 Starting services..."
sudo systemctl daemon-reload
sudo systemctl enable bluealsa
sudo systemctl start bluealsa
sudo systemctl enable bluealsa-aplay
sudo systemctl start bluealsa-aplay

# Set audio levels
echo "🔊 Setting audio levels..."
sudo amixer sset Master,0 90% 2>/dev/null || true
sudo amixer sset PCM,0 90% 2>/dev/null || true

# Make Bluetooth discoverable
echo "📡 Making Bluetooth discoverable..."
sudo bluetoothctl power on
sudo bluetoothctl system-alias 'The Little Shit'
sudo bluetoothctl discoverable on
sudo bluetoothctl pairable on

echo ""
echo "✅ Setup complete!"
echo "🎵 Your Pi is now discoverable as 'The Little Shit'"
echo "📱 Connect your phone and run: dotnet run"
echo ""
