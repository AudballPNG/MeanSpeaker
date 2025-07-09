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
    festival \
    libttspico-utils \
    pulseaudio \
    pulseaudio-module-bluetooth \
    dbus \
    dbus-user-session \
    libdbus-1-dev \
    python3-pip

# Install Piper TTS
echo "🗣️ Installing Piper neural TTS..."

# Handle the externally-managed-environment restriction
if pip3 install piper-tts 2>&1 | grep -q "externally-managed-environment"; then
    echo "⚠️ Python environment is externally managed, using alternative installation methods..."
    
    # Method 1: Try with --break-system-packages (if available)
    echo "🔧 Trying with --break-system-packages flag..."
    if sudo pip3 install --break-system-packages piper-tts; then
        echo "✅ Piper installed with --break-system-packages"
    else
        # Method 2: Try installing via apt if available
        echo "🔧 Trying apt package manager..."
        sudo apt-get update
        if sudo apt-get install -y python3-piper-tts 2>/dev/null; then
            echo "✅ Piper installed via apt"
        else
            # Method 3: Create a virtual environment for system use
            echo "🔧 Creating system virtual environment for Piper..."
            sudo python3 -m venv /opt/piper-venv
            sudo /opt/piper-venv/bin/pip install piper-tts
            echo "✅ Piper installed in virtual environment"
            
            # Create wrapper script that uses the venv
            sudo tee /usr/local/bin/piper-venv > /dev/null << 'EOF'
#!/bin/bash
/opt/piper-venv/bin/python -m piper "$@"
EOF
            sudo chmod +x /usr/local/bin/piper-venv
        fi
    fi
else
    echo "✅ Piper installed successfully"
fi

# Create a convenient alias for piper command that handles different installation methods
echo "🔧 Setting up Piper command alias..."
sudo tee /usr/local/bin/piper > /dev/null << 'EOF'
#!/bin/bash

# Try different Piper installation methods
if command -v piper-tts >/dev/null 2>&1; then
    # Direct piper-tts command available
    piper-tts "$@"
elif python3 -c "import piper" >/dev/null 2>&1; then
    # Python module available
    python3 -m piper "$@"
elif [ -x "/opt/piper-venv/bin/python" ]; then
    # Virtual environment installation
    /opt/piper-venv/bin/python -m piper "$@"
elif [ -x "/usr/local/bin/piper-venv" ]; then
    # Use the venv wrapper
    /usr/local/bin/piper-venv "$@"
else
    echo "❌ Piper not found. Please install manually or re-run setup."
    exit 1
fi
EOF
sudo chmod +x /usr/local/bin/piper

# Download default Piper voice models
echo "📥 Downloading Piper voice models..."

# Automatically detect the user and home directory
CURRENT_USER="${SUDO_USER:-$USER}"
USER_HOME=$(eval echo ~$CURRENT_USER)

echo "🔍 Detected user: $CURRENT_USER"
echo "🏠 User home: $USER_HOME"

mkdir -p "$USER_HOME/.local/share/piper/voices"
cd "$USER_HOME/.local/share/piper/voices"

# Download popular English voices
wget -q https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/lessac/medium/en_US-lessac-medium.onnx
wget -q https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/lessac/medium/en_US-lessac-medium.onnx.json
wget -q https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/ryan/medium/en_US-ryan-medium.onnx
wget -q https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/ryan/medium/en_US-ryan-medium.onnx.json

# Set proper permissions for the detected user
echo "🔐 Setting permissions for user: $CURRENT_USER"
chown -R "$CURRENT_USER:$CURRENT_USER" "$USER_HOME/.local/share/piper/" 2>/dev/null || true
cd "$USER_HOME"

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
CURRENT_DIR=$(pwd)
TARGET_DIR="/home/$ACTUAL_USER/BluetoothSpeaker"

# Only copy if we're not already in the target directory
if [ "$CURRENT_DIR" != "$TARGET_DIR" ]; then
    sudo mkdir -p "$TARGET_DIR"
    sudo cp -r * "$TARGET_DIR/" 2>/dev/null || true
    sudo cp -r .[^.]* "$TARGET_DIR/" 2>/dev/null || true  # Copy hidden files
    sudo chown -R $ACTUAL_USER:$ACTUAL_USER "$TARGET_DIR"
    sudo chmod +x "$TARGET_DIR/speaker-control.sh"
    echo "✅ Project files copied to $TARGET_DIR"
else
    echo "✅ Already running from target directory, skipping copy"
    sudo chown -R $ACTUAL_USER:$ACTUAL_USER "$TARGET_DIR"
    sudo chmod +x "$TARGET_DIR/speaker-control.sh"
fi

# Enable the service
echo "🚀 Enabling auto-start service..."
sudo systemctl daemon-reload
sudo systemctl enable bluetooth-speaker.service

echo ""
echo "🧪 Testing Piper TTS installation..."
if command -v piper >/dev/null 2>&1; then
    echo "✅ Piper command available"
    # Quick test (silent)
    if echo "Test" | piper --output_file /tmp/piper_setup_test.wav 2>/dev/null; then
        echo "✅ Piper TTS working correctly"
        rm -f /tmp/piper_setup_test.wav
    else
        echo "⚠️ Piper installed but test failed - check logs if TTS doesn't work"
    fi
else
    echo "⚠️ Piper command not available - TTS may not work properly"
fi

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
