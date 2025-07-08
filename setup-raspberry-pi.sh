#!/bin/bash

# Function to fix audio routing issues
fix_audio_routing() {
    echo "🔧 Fixing audio routing issues..."
    
    # Stop services
    sudo systemctl stop bluealsa-aplay bluealsa
    
    # Clear any audio locks
    sudo fuser -k /dev/snd/* 2>/dev/null || true
    
    # Restart in proper order
    sudo systemctl start bluealsa
    sleep 3
    sudo systemctl start bluealsa-aplay
    sleep 2
    
    # Set audio levels
    sudo amixer sset Master,0 90% > /dev/null 2>&1
    sudo amixer sset PCM,0 90% > /dev/null 2>&1
    sudo amixer sset Headphone,0 90% > /dev/null 2>&1
    
    # Check if services are running
    if systemctl is-active --quiet bluealsa-aplay; then
        echo "✅ Audio routing fixed!"
        echo "🎵 Try connecting your device and playing audio now"
    else
        echo "❌ Audio routing still not working."
        echo "Check logs: journalctl -u bluealsa-aplay -n 20"
        echo "Check BlueALSA: journalctl -u bluealsa -n 20"
    fi
}

# Function to run assembly attribute fix
fix_assembly_attributes() {
    echo "🔧 Fixing assembly attributes..."
    
    # Clean build
    dotnet clean
    rm -rf obj/ bin/
    
    # Clear NuGet cache
    dotnet nuget locals all --clear
    
    # Remove assembly attribute files
    find . -name "*.AssemblyAttributes.cs" -delete 2>/dev/null || true
    
    # Build with specific settings
    dotnet build -p:GenerateAssemblyInfo=false
    
    echo "✅ Assembly attributes fixed!"
}

# Handle command line arguments
if [ "$1" = "--fix-audio" ]; then
    fix_audio_routing
    exit 0
fi

if [ "$1" = "--fix-assembly" ]; then
    fix_assembly_attributes
    exit 0
fi

if [ "$1" = "--help" ]; then
    echo "Usage: $0 [--fix-audio|--fix-assembly|--help]"
    echo "  --fix-audio     Fix audio routing issues"
    echo "  --fix-assembly  Fix assembly attribute build errors"
    echo "  --help          Show this help message"
    exit 0
fi

echo "🎵 Setting up Snarky Bluetooth Speaker on Raspberry Pi..."

# Update system
echo "Updating system packages..."
sudo apt-get update

# Install required packages
echo "Installing Bluetooth and audio packages..."
sudo apt-get install -y \
    bluetooth \
    bluez \
    bluez-tools \
    bluealsa \
    pulseaudio \
    pulseaudio-module-bluetooth \
    alsa-utils \
    playerctl

# Install text-to-speech packages
echo "Installing text-to-speech packages..."
sudo apt-get install -y \
    espeak \
    espeak-data \
    mbrola \
    mbrola-voices \
    festival \
    festival-dev \
    speech-dispatcher

# Enable and start Bluetooth service
echo "Enabling Bluetooth service..."
sudo systemctl enable bluetooth
sudo systemctl start bluetooth

# Configure Bluetooth for A2DP sink
echo "Configuring Bluetooth as A2DP sink..."
sudo tee /etc/bluetooth/main.conf > /dev/null << 'EOF'
[General]
Name = The Little Shit
Class = 0x240404
DiscoverableTimeout = 0
PairableTimeout = 0

[Policy]
AutoEnable=true
EOF

# Stop any existing audio services before reconfiguring
echo "Stopping existing audio services..."
sudo systemctl stop bluealsa-aplay 2>/dev/null || true
sudo systemctl stop bluealsa 2>/dev/null || true

# Configure BlueALSA for A2DP sink with improved settings
echo "Configuring BlueALSA..."
sudo tee /etc/systemd/system/bluealsa.service > /dev/null << 'EOF'
[Unit]
Description=BlueALSA service
After=bluetooth.service sound.target
Requires=bluetooth.service

[Service]
Type=simple
ExecStart=/usr/bin/bluealsa -p a2dp-sink -p a2dp-source --a2dp-force-mono=false --a2dp-force-audio-cd=true
Restart=on-failure
RestartSec=3
User=root
Group=audio

[Install]
WantedBy=multi-user.target
EOF

# Create BlueALSA audio routing service (CRITICAL for audio playback)
echo "Setting up audio routing service..."
sudo tee /etc/systemd/system/bluealsa-aplay.service > /dev/null << 'EOF'
[Unit]
Description=BlueALSA audio routing service
After=bluealsa.service sound.target
Requires=bluealsa.service

[Service]
Type=simple
ExecStart=/usr/bin/bluealsa-aplay --pcm-buffer-time=250000 --pcm-period-time=50000 --profile-a2dp 00:00:00:00:00:00
Restart=on-failure
RestartSec=2
User=root
Group=audio
StandardOutput=journal
StandardError=journal

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

# Default audio device configuration
pcm.!default {
    type plug
    slave.pcm "bluealsa"
}

ctl.!default {
    type bluealsa
}

# BlueALSA PCM device
pcm.bluealsa {
    type bluealsa
    device 00:00:00:00:00:00
    profile "a2dp"
}
EOF

# Add audio group configuration
echo "Configuring audio groups..."
sudo usermod -a -G audio $USER
sudo usermod -a -G bluetooth $USER

# Enable BlueALSA services with proper ordering
echo "Enabling audio services..."
sudo systemctl daemon-reload
sudo systemctl enable bluealsa
sudo systemctl enable bluealsa-aplay

# Add user to audio group
echo "Adding user to audio and bluetooth groups..."
sudo usermod -a -G audio $USER
sudo usermod -a -G bluetooth $USER

# Disable PulseAudio auto-spawn (can interfere with BlueALSA)
echo "Configuring PulseAudio settings..."
mkdir -p ~/.config/pulse
echo "autospawn = no" > ~/.config/pulse/client.conf

# Configure ALSA for better audio quality
echo "Configuring ALSA for optimal audio quality..."
sudo tee /etc/modprobe.d/alsa-base.conf > /dev/null << 'EOF'
# Prevent unnecessary modules from loading
options snd-usb-audio index=-2
# Optimize for low latency
options snd-hda-intel model=generic
EOF

# Set up automatic pairing agent
echo "Setting up automatic pairing agent..."
sudo tee /usr/local/bin/bt-agent.py > /dev/null << 'EOF'
#!/usr/bin/python3
import dbus
import dbus.service
import dbus.mainloop.glib
from gi.repository import GLib

BUS_NAME = 'org.bluez'
AGENT_INTERFACE = 'org.bluez.Agent1'
AGENT_PATH = "/test/agent"

class Agent(dbus.service.Object):
    @dbus.service.method(AGENT_INTERFACE, in_signature="os", out_signature="")
    def AuthorizeService(self, device, uuid):
        print("AuthorizeService (%s, %s)" % (device, uuid))
        return

    @dbus.service.method(AGENT_INTERFACE, in_signature="o", out_signature="s")
    def RequestPinCode(self, device):
        print("RequestPinCode (%s)" % (device))
        return "0000"

    @dbus.service.method(AGENT_INTERFACE, in_signature="o", out_signature="u")
    def RequestPasskey(self, device):
        print("RequestPasskey (%s)" % (device))
        return dbus.UInt32(0000)

    @dbus.service.method(AGENT_INTERFACE, in_signature="", out_signature="")
    def Cancel(self):
        print("Cancel")

if __name__ == '__main__':
    dbus.mainloop.glib.DBusGMainLoop(set_as_default=True)
    
    bus = dbus.SystemBus()
    agent = Agent(bus, AGENT_PATH)
    manager = dbus.Interface(bus.get_object(BUS_NAME, "/org/bluez"), "org.bluez.AgentManager1")
    manager.RegisterAgent(AGENT_PATH, "NoInputNoOutput")
    manager.RequestDefaultAgent(AGENT_PATH)
    
    print("Bluetooth agent registered")
    
    mainloop = GLib.MainLoop()
    mainloop.run()
EOF

sudo chmod +x /usr/local/bin/bt-agent.py

# Create systemd service for the agent
sudo tee /etc/systemd/system/bt-agent.service > /dev/null << 'EOF'
[Unit]
Description=Bluetooth Agent
After=bluetooth.service
Requires=bluetooth.service

[Service]
Type=simple
ExecStart=/usr/local/bin/bt-agent.py
Restart=on-failure

[Install]
WantedBy=multi-user.target
EOF

# Enable the agent service
sudo systemctl enable bt-agent
sudo systemctl start bt-agent

# Restart Bluetooth to apply changes
echo "Restarting Bluetooth service..."
sudo systemctl restart bluetooth

# Start audio services in proper order
echo "Starting audio services..."
sudo systemctl start bluealsa
sleep 2
sudo systemctl start bluealsa-aplay

# Set up audio mixer levels for optimal volume
echo "Setting up audio levels..."
sudo amixer sset PCM,0 90% > /dev/null 2>&1
sudo amixer sset Master,0 90% > /dev/null 2>&1
sudo amixer sset Headphone,0 90% > /dev/null 2>&1

# Test audio hardware
echo "Testing audio hardware..."
if command -v speaker-test &> /dev/null; then
    echo "Audio hardware test (you should hear white noise for 3 seconds):"
    timeout 3 speaker-test -t wav -c 2 -l 1 2>/dev/null || echo "Audio test completed"
fi

# Create a marker file to indicate setup is complete
sudo touch /etc/bluetooth-speaker-setup-complete

# Verify that everything is running correctly
echo "Verifying audio and Bluetooth setup..."

# Check BlueALSA service
if systemctl is-active --quiet bluealsa; then
    echo "✅ BlueALSA service is running"
else
    echo "⚠️  BlueALSA service is not running, attempting to start..."
    sudo systemctl start bluealsa
    sleep 2
    if systemctl is-active --quiet bluealsa; then
        echo "✅ BlueALSA service started successfully"
    else
        echo "❌ Failed to start BlueALSA service"
        systemctl status bluealsa --no-pager
    fi
fi

# Check BlueALSA-aplay service (audio routing) - CRITICAL FOR AUDIO
if systemctl is-active --quiet bluealsa-aplay; then
    echo "✅ BlueALSA-aplay service is running (audio routing active)"
else
    echo "⚠️  BlueALSA-aplay service is not running, attempting to start..."
    sudo systemctl start bluealsa-aplay
    sleep 2
    if systemctl is-active --quiet bluealsa-aplay; then
        echo "✅ BlueALSA-aplay service started successfully"
    else
        echo "❌ Failed to start BlueALSA-aplay service - AUDIO WILL NOT WORK!"
        systemctl status bluealsa-aplay --no-pager
    fi
fi

# Check Bluetooth service
if systemctl is-active --quiet bluetooth; then
    echo "✅ Bluetooth service is running"
else
    echo "⚠️  Bluetooth service is not running, attempting to start..."
    sudo systemctl start bluetooth
    sleep 2
    if systemctl is-active --quiet bluetooth; then
        echo "✅ Bluetooth service started successfully"
    else
        echo "❌ Failed to start Bluetooth service"
        systemctl status bluetooth --no-pager
    fi
fi

# Check Bluetooth agent service
if systemctl is-active --quiet bt-agent; then
    echo "✅ Bluetooth agent service is running"
else
    echo "⚠️  Bluetooth agent service is not running, attempting to start..."
    sudo systemctl start bt-agent
    sleep 2
    if systemctl is-active --quiet bt-agent; then
        echo "✅ Bluetooth agent service started successfully"
    else
        echo "❌ Failed to start Bluetooth agent service"
        systemctl status bt-agent --no-pager
    fi
fi

# Test audio output and show available devices
echo "Testing audio output..."
echo "Available audio devices:"
aplay -l 2>/dev/null || echo "No audio devices found"
echo

# Check for common audio issues
echo "Checking for common audio issues..."
if [ ! -f /etc/asound.conf ]; then
    echo "⚠️  ALSA configuration file missing"
else
    echo "✅ ALSA configuration file exists"
fi

if groups $USER | grep -q "audio"; then
    echo "✅ User is in audio group"
else
    echo "⚠️  User is not in audio group"
fi

if groups $USER | grep -q "bluetooth"; then
    echo "✅ User is in bluetooth group"
else
    echo "⚠️  User is not in bluetooth group"
fi

# Install .NET 8 if not already installed
if ! command -v dotnet &> /dev/null; then
    echo "Installing .NET 8..."
    wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
    chmod +x dotnet-install.sh
    ./dotnet-install.sh --channel 8.0
    echo 'export PATH=$PATH:$HOME/.dotnet' >> ~/.bashrc
    source ~/.bashrc
    rm dotnet-install.sh
fi

echo "✅ Setup complete!"
echo ""
echo "🎵 IMPORTANT: Audio Routing Information"
echo "================================================"
echo "The most critical service for audio playback is 'bluealsa-aplay'"
echo "This service routes audio FROM Bluetooth devices TO your speakers"
echo ""
echo "If audio doesn't work after connecting:"
echo "1. Check the service: systemctl status bluealsa-aplay"
echo "2. Restart if needed: sudo systemctl restart bluealsa-aplay"
echo "3. Check logs: journalctl -u bluealsa-aplay -f"
echo ""
echo "Next steps:"
echo "1. Reboot your Raspberry Pi: sudo reboot"
echo "2. Set your OpenAI API key: export OPENAI_API_KEY='your-key-here'"
echo "3. Build and run the application: dotnet run"
echo "4. Connect your phone to 'The Little Shit' and start playing music!"
echo ""
echo "Troubleshooting Commands:"
echo "- Check all audio services: systemctl status bluealsa bluealsa-aplay bluetooth"
echo "- Restart all services: sudo systemctl restart bluetooth bluealsa bluealsa-aplay"
echo "- Check audio devices: aplay -l"
echo "- Test audio output: speaker-test -t wav -c 2"
echo "- Check BlueALSA devices: bluealsa-aplay -l"
echo ""
echo "The speaker will automatically insult your music choices! 🎵"
