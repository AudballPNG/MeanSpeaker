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
    bluealsa-aplay \
    alsa-utils \
    playerctl \
    espeak \
    pulseaudio \
    pulseaudio-module-bluetooth \
    dbus \
    dbus-user-session \
    libdbus-1-dev

# Enable and start Bluetooth
echo "🔵 Configuring Bluetooth..."
sudo systemctl enable bluetooth
sudo systemctl start bluetooth
sudo systemctl enable dbus
sudo systemctl start dbus

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
ExecStart=/usr/bin/bluealsa-aplay --pcm-buffer-time=250000
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

# Enable and start services
echo "🚀 Starting services..."
sudo systemctl daemon-reload
sudo systemctl enable bluetooth
sudo systemctl start bluetooth
sudo systemctl enable bluealsa
sudo systemctl start bluealsa
sudo systemctl enable bluealsa-aplay
sudo systemctl start bluealsa-aplay

# Create dynamic audio routing script
echo "🔊 Creating dynamic audio routing script..."
sudo tee /usr/local/bin/route-bluetooth-audio.sh > /dev/null << 'EOF'
#!/bin/bash
# Dynamic Bluetooth audio routing script

# Kill any existing bluealsa-aplay processes
sudo pkill -f bluealsa-aplay

# Wait a moment
sleep 2

# Get connected A2DP devices
DEVICES=$(bluetoothctl devices Connected | grep -v "^$")

if [ -n "$DEVICES" ]; then
    echo "Found connected devices, starting audio routing..."
    
    # Start bluealsa-aplay for all connected devices
    /usr/bin/bluealsa-aplay --pcm-buffer-time=250000 &
    
    # Also try device-specific routing
    while IFS= read -r line; do
        if [[ $line =~ Device\ ([0-9A-Fa-f:]{17}) ]]; then
            MAC="${BASH_REMATCH[1]}"
            echo "Routing audio for device: $MAC"
            /usr/bin/bluealsa-aplay --pcm-buffer-time=250000 "$MAC" &
        fi
    done <<< "$DEVICES"
    
    echo "Audio routing started for connected devices"
else
    echo "No connected devices found"
fi
EOF

sudo chmod +x /usr/local/bin/route-bluetooth-audio.sh

# Set audio levels
echo "🔊 Setting audio levels..."
sudo amixer sset Master,0 90% 2>/dev/null || true
sudo amixer sset PCM,0 90% 2>/dev/null || true
sudo amixer sset Headphone,0 90% 2>/dev/null || true

# Configure audio for BlueALSA
echo "🎧 Configuring audio system..."
# Add current user to audio group if not already
sudo usermod -a -G audio ${SUDO_USER:-$(whoami)} 2>/dev/null || true

# Ensure ALSA config allows software mixing
sudo tee /etc/asound.conf > /dev/null << 'EOF'
defaults.pcm.card 0
defaults.pcm.device 0
defaults.ctl.card 0

pcm.!default {
    type pulse
    fallback "sysdefault"
    hint {
        show on
        description "Default ALSA Output (currently PulseAudio Sound Server)"
    }
}

ctl.!default {
    type pulse
    fallback "sysdefault"
}
EOF

# Make Bluetooth discoverable
echo "📡 Making Bluetooth discoverable..."
sudo bluetoothctl power on
sudo bluetoothctl system-alias 'The Little Shit'
sudo bluetoothctl discoverable on
sudo bluetoothctl pairable on

# Get the current user (who ran the script with sudo)
ACTUAL_USER=${SUDO_USER:-$(whoami)}
echo "🤖 Setting up auto-start service for user: $ACTUAL_USER..."

sudo tee /etc/systemd/system/bluetooth-speaker.service > /dev/null << EOF
[Unit]
Description=Bluetooth Speaker with AI Commentary
After=bluetooth.service bluealsa.service network.target
Wants=bluetooth.service bluealsa.service
StartLimitIntervalSec=60
StartLimitBurst=3

[Service]
Type=simple
User=$ACTUAL_USER
Group=$ACTUAL_USER
WorkingDirectory=/home/$ACTUAL_USER/BluetoothSpeaker
ExecStart=/usr/bin/dotnet run --project /home/$ACTUAL_USER/BluetoothSpeaker/BluetoothSpeaker.csproj
Restart=always
RestartSec=10
StandardOutput=journal
StandardError=journal

# Environment variables
Environment=DOTNET_ENVIRONMENT=Production
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1

[Install]
WantedBy=multi-user.target
EOF

# Copy project files to /home/$ACTUAL_USER/BluetoothSpeaker
echo "📁 Copying project files..."
sudo mkdir -p /home/$ACTUAL_USER/BluetoothSpeaker
sudo cp -r . /home/$ACTUAL_USER/BluetoothSpeaker/
sudo chown -R $ACTUAL_USER:$ACTUAL_USER /home/$ACTUAL_USER/BluetoothSpeaker
sudo chmod +x /home/$ACTUAL_USER/BluetoothSpeaker/speaker-control.sh

# Enable the service
echo "🚀 Enabling auto-start service..."
sudo systemctl daemon-reload
sudo systemctl enable bluetooth-speaker.service

echo ""
echo "✅ Setup complete!"
echo "🎵 Your Pi is now discoverable as 'The Little Shit'"
echo "🤖 The AI commentary will start automatically on boot"
echo "📱 Connect your phone and the speaker will work automatically"
echo ""
echo "🔧 Service management:"
echo "  Start:   sudo systemctl start bluetooth-speaker"
echo "  Stop:    sudo systemctl stop bluetooth-speaker"
echo "  Status:  sudo systemctl status bluetooth-speaker"
echo "  Logs:    sudo journalctl -u bluetooth-speaker -f"
echo "  Or use:  ./speaker-control.sh"
echo ""
