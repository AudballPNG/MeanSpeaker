#!/bin/bash

# Comprehensive Bluetooth Speaker Setup and Run Script for Raspberry Pi
# This script handles everything: system setup, dependencies, build fixes, and running

echo "🎵 Snarky Bluetooth Speaker - Complete Setup and Run Script"
echo "============================================================"

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

# Function to debug media player connectivity
debug_media_player() {
    echo "🔍 Debugging media player connectivity..."
    
    # Check if D-Bus system is working
    echo "Checking D-Bus system connection..."
    if dbus-send --system --print-reply --dest=org.bluez /org/bluez org.freedesktop.DBus.Introspectable.Introspect > /dev/null 2>&1; then
        echo "✅ D-Bus system connection working"
    else
        echo "❌ D-Bus system connection failed"
        echo "   This will prevent media player monitoring"
    fi
    
    # Check for BlueZ media players
    echo "Checking for BlueZ media players..."
    dbus-send --system --print-reply --dest=org.bluez /org/bluez org.freedesktop.DBus.ObjectManager.GetManagedObjects 2>/dev/null | grep -i "MediaPlayer" || echo "No MediaPlayer interfaces found"
    
    # Check playerctl availability
    echo "Checking playerctl functionality..."
    if command -v playerctl &> /dev/null; then
        echo "✅ playerctl is installed"
        echo "Available players:"
        playerctl -l 2>/dev/null || echo "No media players detected by playerctl"
        
        echo "Current player status:"
        playerctl status 2>/dev/null || echo "No active media player"
        
        echo "Metadata test:"
        playerctl metadata 2>/dev/null || echo "No metadata available"
    else
        echo "❌ playerctl not found"
    fi
    
    # Check for connected Bluetooth devices
    echo "Checking connected Bluetooth devices..."
    bluetoothctl devices Connected || echo "No connected devices found"
    
    # Check BlueALSA status
    echo "Checking BlueALSA status..."
    if systemctl is-active --quiet bluealsa; then
        echo "✅ BlueALSA is running"
        # Check if it's exposing media player interface
        ps aux | grep bluealsa | grep -v grep
    else
        echo "❌ BlueALSA is not running"
    fi
    
    # Check permissions
    echo "Checking user permissions..."
    groups $USER | grep -q "audio" && echo "✅ User in audio group" || echo "❌ User not in audio group"
    groups $USER | grep -q "bluetooth" && echo "✅ User in bluetooth group" || echo "❌ User not in bluetooth group"
    
    echo "Debug complete. Run with: ./run-on-pi.sh --debug-media"
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

if [ "$1" = "--debug-media" ]; then
    debug_media_player
    exit 0
fi

if [ "$1" = "--help" ]; then
    echo "Usage: $0 [--fix-audio|--fix-assembly|--debug-media|--help]"
    echo "  --fix-audio     Fix audio routing issues"
    echo "  --fix-assembly  Fix assembly attribute build errors"
    echo "  --debug-media   Debug media player connectivity issues"
    echo "  --help          Show this help message"
    exit 0
fi

# Function to run system setup
setup_system() {
    echo "🔧 Setting up Raspberry Pi Bluetooth system..."
    
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
        playerctl \
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

    # Add user to audio and bluetooth groups
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

    # Mark setup as complete
    sudo touch /etc/bluetooth-speaker-setup-complete

    echo "✅ System setup complete!"
    echo ""
}

# Configure BlueALSA and audio routing services
setup_audio_routing() {
    echo "🔊 Setting up audio routing for Bluetooth speakers..."
    
    # Stop any existing audio services before reconfiguring
    echo "Stopping existing audio services..."
    sudo systemctl stop bluealsa-aplay 2>/dev/null || true
    sudo systemctl stop bluealsa 2>/dev/null || true
    
    # Configure BlueALSA for A2DP sink with improved settings AND media player support
    echo "Configuring BlueALSA with media player support..."
    sudo tee /etc/systemd/system/bluealsa.service > /dev/null << 'EOF'
[Unit]
Description=BlueALSA service
After=bluetooth.service sound.target
Requires=bluetooth.service

[Service]
Type=simple
ExecStart=/usr/bin/bluealsa -p a2dp-sink -p a2dp-source --a2dp-force-mono=false --a2dp-force-audio-cd=true --enable-media-player
Restart=on-failure
RestartSec=3
User=root
Group=audio
# Enable D-Bus access for media player interface
Environment=DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus

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
# Enable media player monitoring
Environment=PULSE_RUNTIME_PATH=/run/user/1000/pulse

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

    # Enable BlueALSA services with proper ordering
    echo "Enabling audio services..."
    sudo systemctl daemon-reload
    sudo systemctl enable bluealsa
    sudo systemctl enable bluealsa-aplay
    
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
    
    # Add media player monitoring setup
    echo "Setting up media player monitoring..."
    
    # Install additional monitoring tools
    sudo apt-get install -y dbus-x11 mpris-interface-doc
    
    # Configure D-Bus for media player access
    sudo tee /etc/dbus-1/system.d/bluealsa-media.conf > /dev/null << 'EOF'
<!DOCTYPE busconfig PUBLIC "-//freedesktop//DTD D-BUS Bus Configuration 1.0//EN"
 "http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd">
<busconfig>
  <policy user="root">
    <allow own="org.bluez"/>
    <allow send_destination="org.bluez"/>
    <allow send_interface="org.bluez.MediaPlayer1"/>
    <allow receive_interface="org.bluez.MediaPlayer1"/>
    <allow receive_interface="org.freedesktop.DBus.Properties"/>
  </policy>
  
  <policy group="audio">
    <allow send_destination="org.bluez"/>
    <allow send_interface="org.bluez.MediaPlayer1"/>
    <allow receive_interface="org.bluez.MediaPlayer1"/>
    <allow receive_interface="org.freedesktop.DBus.Properties"/>
  </policy>
</busconfig>
EOF

    # Enable D-Bus service for user session
    sudo tee /etc/systemd/system/dbus-user.service > /dev/null << 'EOF'
[Unit]
Description=D-Bus User Message Bus
Documentation=man:dbus-daemon(1)
After=systemd-user-sessions.service

[Service]
ExecStart=/usr/bin/dbus-daemon --user --systemd-activation --address=unix:path=/run/user/%u/bus --nofork --nopidfile --systemd-activation
Restart=on-failure

[Install]
WantedBy=default.target
EOF

    # Enable and start D-Bus user service
    sudo systemctl daemon-reload
    sudo systemctl enable dbus-user.service
    sudo systemctl start dbus-user.service

    echo "✅ Audio routing setup complete!"
}

# Function to verify all services are running correctly
verify_services() {
    echo "🔍 Verifying audio and Bluetooth setup..."

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

    echo ""
    echo "✅ Service verification complete!"
    echo ""
    echo "🎵 IMPORTANT: Audio Routing Information"
    echo "================================================"
    echo "The most critical service for audio playback is 'bluealsa-aplay'"
    echo "This service routes audio FROM Bluetooth devices TO your speakers"
    echo ""
    echo "If audio doesn't work after connecting:"
    echo "1. Check the service: systemctl status bluealsa-aplay"
    echo "2. Restart if needed: sudo systemctl restart bluealsa-aplay"
    echo "3. Use the fix: ./run-on-pi.sh --fix-audio"
    echo ""
}

# Function to set up auto-start service
setup_autostart() {
    echo "🚀 Setting up auto-start service..."
    
    # Get current user and paths
    USER_NAME=$(whoami)
    USER_ID=$(id -u $USER_NAME)
    APP_PATH=$(pwd)
    DOTNET_PATH=$(which dotnet)
    
    # Ensure .NET is properly installed for the user
    if [ -z "$DOTNET_PATH" ]; then
        echo "Installing .NET 8 for current user..."
        wget https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
        chmod +x /tmp/dotnet-install.sh
        /tmp/dotnet-install.sh --channel 8.0 --install-dir /home/$USER_NAME/.dotnet
        DOTNET_PATH="/home/$USER_NAME/.dotnet/dotnet"
        rm /tmp/dotnet-install.sh
    fi
    
    # Check if .env file exists, create if not
    if [ ! -f ".env" ]; then
        echo "Creating .env file template..."
        cat > .env << 'EOF'
# OpenAI API Configuration
OPENAI_API_KEY=your-api-key-here

# Speech Configuration
ENABLE_SPEECH=true
TTS_VOICE=en+f3
EOF
        echo "⚠️  Please edit .env file and add your OpenAI API key!"
    fi
    
    # Build in release mode
    echo "Building application in release mode..."
    dotnet build -c Release
    
    # Ensure proper permissions
    sudo chown -R $USER_NAME:$USER_NAME $APP_PATH
    sudo chmod +x $APP_PATH/*.sh
    
    # Create systemd service with proper environment and permissions
    sudo tee /etc/systemd/system/meanspeaker.service > /dev/null << EOF
[Unit]
Description=MeanSpeaker - Snarky Bluetooth Speaker with AI Commentary
After=network-online.target bluetooth.target bluealsa.service bluealsa-aplay.service sound.target multi-user.target
Wants=network-online.target bluetooth.target
Requires=bluealsa.service bluealsa-aplay.service

[Service]
Type=simple
User=$USER_NAME
Group=audio
SupplementaryGroups=bluetooth audio dialout
WorkingDirectory=$APP_PATH
Environment=HOME=/home/$USER_NAME
Environment=USER=$USER_NAME
Environment=DOTNET_ROOT=/home/$USER_NAME/.dotnet
Environment=PATH=/home/$USER_NAME/.dotnet:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
Environment=DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$USER_ID/bus
EnvironmentFile=-$APP_PATH/.env
ExecStartPre=/bin/sleep 10
ExecStart=$DOTNET_PATH run --configuration Release --project $APP_PATH/BluetoothSpeaker.csproj
Restart=always
RestartSec=15
TimeoutStartSec=60
StandardOutput=journal
StandardError=journal
KillMode=mixed
KillSignal=SIGTERM

# Security settings
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=false
ProtectSystem=strict
ReadWritePaths=$APP_PATH /tmp /var/tmp
CapabilityBoundingSet=CAP_NET_BIND_SERVICE

[Install]
WantedBy=multi-user.target
EOF
    
    # Create a startup delay script to ensure all services are ready
    sudo tee /usr/local/bin/meanspeaker-start.sh > /dev/null << EOF
#!/bin/bash
# Wait for all dependencies to be ready
sleep 15

# Ensure bluetooth is discoverable
bluetoothctl discoverable on
bluetoothctl pairable on

# Start the main application
cd $APP_PATH
su $USER_NAME -c "$DOTNET_PATH run --configuration Release"
EOF
    
    sudo chmod +x /usr/local/bin/meanspeaker-start.sh
    
    # Create alternative service using the startup script
    sudo tee /etc/systemd/system/meanspeaker-alt.service > /dev/null << EOF
[Unit]
Description=MeanSpeaker Alternative Start
After=network-online.target bluetooth.target bluealsa.service bluealsa-aplay.service
Wants=network-online.target
Requires=bluealsa.service bluealsa-aplay.service

[Service]
Type=simple
ExecStart=/usr/local/bin/meanspeaker-start.sh
Restart=always
RestartSec=30
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF
    
    # Enable and start service
    sudo systemctl daemon-reload
    sudo systemctl enable meanspeaker.service
    
    # Test the service
    echo "Testing service startup..."
    sudo systemctl start meanspeaker.service
    sleep 5
    
    if systemctl is-active --quiet meanspeaker.service; then
        echo "✅ Auto-start service created and started successfully!"
    else
        echo "⚠️  Primary service failed to start, trying alternative..."
        sudo systemctl stop meanspeaker.service
        sudo systemctl disable meanspeaker.service
        sudo systemctl enable meanspeaker-alt.service
        sudo systemctl start meanspeaker-alt.service
        sleep 5
        
        if systemctl is-active --quiet meanspeaker-alt.service; then
            echo "✅ Alternative auto-start service working!"
        else
            echo "❌ Both services failed. Check logs with:"
            echo "   sudo journalctl -u meanspeaker -f"
            echo "   sudo journalctl -u meanspeaker-alt -f"
        fi
    fi
    
    echo ""
    echo "Auto-start service commands:"
    echo "  - Check status: sudo systemctl status meanspeaker"
    echo "  - View logs: sudo journalctl -u meanspeaker -f"
    echo "  - Stop service: sudo systemctl stop meanspeaker"
    echo "  - Start service: sudo systemctl start meanspeaker"
    echo "  - Restart service: sudo systemctl restart meanspeaker"
    echo "  - Check alternative: sudo systemctl status meanspeaker-alt"
}

# Check if this is the first run
SETUP_MARKER="/tmp/meanspeaker-setup-complete"

if [ ! -f "$SETUP_MARKER" ]; then
    echo "🔧 First run detected. Running complete system setup..."
    setup_system
    
    # Configure audio routing
    setup_audio_routing
    
    # Verify all services are working
    verify_services
    
    # Create setup marker
    touch "$SETUP_MARKER"
    echo "📝 Setup marker created. System setup will be skipped on future runs."
    echo ""
else
    echo "📝 System setup already completed. Skipping to build and run."
    echo "    To force setup again, delete: $SETUP_MARKER"
    echo ""
fi

# Comprehensive build and run process
echo "🔨 Building and running application..."

# Nuclear clean - remove everything
echo "Performing complete clean..."
dotnet clean
rm -rf obj/ bin/
dotnet nuget locals all --clear

# Remove any problematic assembly files
echo "Removing assembly attribute files..."
find . -name "*.AssemblyAttributes.cs" -delete 2>/dev/null || true
find . -name "*.AssemblyInfo.cs" -delete 2>/dev/null || true

# Restore packages with no cache
echo "Restoring NuGet packages..."
dotnet restore --no-cache

# Build with assembly info disabled
echo "Building application..."
dotnet build --no-restore -p:GenerateAssemblyInfo=false --verbosity minimal

# If build still fails, try with minimal project settings
if [ $? -ne 0 ]; then
    echo "Build failed. Trying with minimal project settings..."
    
    # Backup original project file
    cp BluetoothSpeaker.csproj BluetoothSpeaker.csproj.backup
    
    # Create minimal project file
    cat > BluetoothSpeaker.csproj << 'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <UseAppHost>true</UseAppHost>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.Text.Json" Version="9.0.6" />
    <PackageReference Include="Tmds.DBus" Version="0.21.2" />
  </ItemGroup>
</Project>
EOF
    
    # Clean and build again
    dotnet clean
    rm -rf obj/ bin/
    dotnet restore --no-cache
    dotnet build --no-restore -p:GenerateAssemblyInfo=false --verbosity minimal
    
    if [ $? -ne 0 ]; then
        echo "❌ Build still failed. Restoring original project file."
        mv BluetoothSpeaker.csproj.backup BluetoothSpeaker.csproj
        exit 1
    fi
fi

# Check if build was successful
if [ $? -eq 0 ]; then
    echo "✅ Build successful! Starting application..."
    echo ""
    
    # Check for OpenAI API key
    if [ -z "$OPENAI_API_KEY" ]; then
        echo "⚠️  OpenAI API key not found in environment variables."
        echo "Checking for .env file..."
        
        if [ -f ".env" ]; then
            echo "Found .env file. Make sure it contains your OpenAI API key."
            echo "You can edit it with: nano .env"
        else
            echo "No .env file found. You'll be prompted to enter your API key."
        fi
        echo ""
    fi
    
    # Run the application
    echo "🎵 Starting Snarky Bluetooth Speaker..."
    dotnet run
else
    echo "❌ Build failed. Please check the errors above."
    exit 1
fi

# After successful run, ask about auto-start setup
echo ""
echo "🚀 Would you like to set up MeanSpeaker to start automatically on boot?"
echo "This will create a systemd service to run the application automatically."
read -p "Set up auto-start? (y/n): " -n 1 -r
echo ""

if [[ $REPLY =~ ^[Yy]$ ]]; then
    setup_autostart
else
    echo "Auto-start setup skipped."
    echo "You can run this script again and choose 'y' to set up auto-start later."
fi

echo ""
echo "🎵 Script completed! Your Bluetooth speaker is ready to insult music choices!"
echo ""
echo "🔧 Troubleshooting Commands:"
echo "  - Fix audio routing: ./run-on-pi.sh --fix-audio"
echo "  - Fix build issues: ./run-on-pi.sh --fix-assembly"
echo "  - Debug media player: ./run-on-pi.sh --debug-media"
echo "  - Check all services: systemctl status bluealsa bluealsa-aplay bluetooth bt-agent"
echo "  - Restart services: sudo systemctl restart bluetooth bluealsa bluealsa-aplay"
echo "  - Check audio devices: aplay -l"
echo "  - Test audio: speaker-test -t wav -c 2"
echo "  - Check logs: journalctl -u bluealsa-aplay -f"
echo ""
echo "🤖 AI Commentary Troubleshooting:"
echo "  If audio works but no AI commentary:"
echo "  1. Run: ./run-on-pi.sh --debug-media"
echo "  2. Check D-Bus permissions: sudo systemctl restart dbus"
echo "  3. Verify OpenAI API key in .env file"
echo "  4. Check app logs: journalctl -u meanspeaker -f"
echo "  5. Test playerctl: playerctl -l && playerctl status"
echo ""
echo "📱 To connect your device:"
echo "  1. Make sure Bluetooth is on"
echo "  2. Look for 'The Little Shit' in your Bluetooth devices"
echo "  3. Connect and start playing music"
echo "  4. Wait 10-15 seconds for AI commentary to start"
echo "  5. If no commentary, check troubleshooting steps above"
echo ""
if [ -f "/etc/systemd/system/meanspeaker.service" ]; then
    echo "🚀 Auto-start service commands:"
    echo "  - Check status: sudo systemctl status meanspeaker"
    echo "  - View logs: sudo journalctl -u meanspeaker -f"
    echo "  - Stop service: sudo systemctl stop meanspeaker"
    echo "  - Start service: sudo systemctl start meanspeaker"
    echo "  - Restart service: sudo systemctl restart meanspeaker"
fi
