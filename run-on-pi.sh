#!/bin/bash

# Comprehensive Bluetooth Speaker Setup and Run Script for Raspberry Pi
# This script handles everything: system setup, dependencies, build fixes, and running

echo "🎵 Snarky Bluetooth Speaker - Complete Setup and Run Script"
echo "============================================================"

# Function to run system setup
setup_system() {
    echo "🔧 Setting up Raspberry Pi Bluetooth system..."
    
    # Update system
    echo "Updating system packages..."
    sudo apt-get update
    
    # Install required packages (handle bluealsa not being available)
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
        speech-dispatcher || echo "Some packages may not be available, continuing..."
    
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
    
    # Add user to audio group
    echo "Adding user to audio group..."
    sudo usermod -a -G audio $USER
    
    # Set up automatic pairing agent
    echo "Setting up automatic pairing agent..."
    sudo tee /usr/local/bin/bluetooth-agent.sh > /dev/null << 'EOF'
#!/bin/bash
bluetoothctl << 'BTEOF'
agent on
default-agent
discoverable on
pairable on
BTEOF
EOF
    
    sudo chmod +x /usr/local/bin/bluetooth-agent.sh
    
    # Create service for auto-pairing
    sudo tee /etc/systemd/system/bluetooth-agent.service > /dev/null << 'EOF'
[Unit]
Description=Bluetooth Auto-Pairing Agent
After=bluetooth.service
Requires=bluetooth.service

[Service]
Type=simple
ExecStart=/usr/local/bin/bluetooth-agent.sh
Restart=on-failure
User=root

[Install]
WantedBy=multi-user.target
EOF
    
    sudo systemctl daemon-reload
    sudo systemctl enable bluetooth-agent.service
    sudo systemctl start bluetooth-agent.service
    
    # Mark setup as complete
    sudo touch /etc/bluetooth-speaker-setup-complete
    
    # Restart Bluetooth service
    echo "Restarting Bluetooth service..."
    sudo systemctl restart bluetooth
    
    echo "✅ System setup complete!"
    echo ""
}

# Configure BlueALSA and audio routing services
setup_audio_routing() {
    echo "🔊 Setting up audio routing for Bluetooth speakers..."
    
    # Install BlueALSA if not already installed
    if ! dpkg -s bluealsa &>/dev/null; then
        echo "Installing BlueALSA..."
        sudo apt-get install -y bluealsa
    fi
    
    # Configure BlueALSA service
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
    echo "Setting up audio routing service..."
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

    # Enable and start services
    sudo systemctl daemon-reload
    sudo systemctl enable bluealsa
    sudo systemctl start bluealsa
    sudo systemctl enable bluealsa-aplay
    sudo systemctl start bluealsa-aplay
    
    # Set audio levels
    echo "Setting audio levels..."
    amixer sset Master,0 90% > /dev/null 2>&1 || true
    amixer sset PCM,0 90% > /dev/null 2>&1 || true
    
    echo "✅ Audio routing setup complete!"
}

# Function to set up auto-start service
setup_autostart() {
    echo "🚀 Setting up auto-start service..."
    
    # Get current user and paths
    USER_NAME=$(whoami)
    APP_PATH=$(pwd)
    DOTNET_PATH=$(which dotnet)
    
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
    
    # Create systemd service
    sudo tee /etc/systemd/system/meanspeaker.service > /dev/null << EOF
[Unit]
Description=MeanSpeaker - Snarky Bluetooth Speaker with AI Commentary
After=network.target bluetooth.target bluealsa.service bluealsa-aplay.service sound.target
Wants=bluetooth.target
Requires=network.target bluealsa.service bluealsa-aplay.service

[Service]
Type=simple
User=$USER_NAME
Group=$USER_NAME
WorkingDirectory=$APP_PATH
Environment=DOTNET_ROOT=/home/$USER_NAME/.dotnet
Environment=PATH=/home/$USER_NAME/.dotnet:\$PATH
EnvironmentFile=$APP_PATH/.env
ExecStart=$DOTNET_PATH run --configuration Release
Restart=always
RestartSec=10
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF
    
    # Enable and start service
    sudo systemctl daemon-reload
    sudo systemctl enable meanspeaker.service
    sudo systemctl start meanspeaker.service
    
    echo "✅ Auto-start service created and started!"
    echo "Use 'sudo systemctl status meanspeaker' to check status"
    echo "Use 'sudo journalctl -u meanspeaker -f' to view logs"
}

# Check if this is the first run
SETUP_MARKER="/tmp/meanspeaker-setup-complete"

if [ ! -f "$SETUP_MARKER" ]; then
    echo "🔧 First run detected. Running complete system setup..."
    setup_system
    
    # Configure audio routing
    setup_audio_routing
    
    # Create setup marker
    touch "$SETUP_MARKER"
    echo "📝 Setup marker created. System setup will be skipped on future runs."
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
echo "Useful commands:"
echo "  - Check service status: sudo systemctl status meanspeaker"
echo "  - View logs: sudo journalctl -u meanspeaker -f"
echo "  - Stop service: sudo systemctl stop meanspeaker"
echo "  - Start service: sudo systemctl start meanspeaker"
echo "  - Restart service: sudo systemctl restart meanspeaker"
