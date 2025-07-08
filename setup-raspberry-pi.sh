#!/bin/bash

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

# Create BlueALSA audio routing service
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

# Enable BlueALSA services
sudo systemctl daemon-reload
sudo systemctl enable bluealsa
sudo systemctl start bluealsa
sudo systemctl enable bluealsa-aplay
sudo systemctl start bluealsa-aplay

# Configure audio for better Bluetooth performance
echo "Configuring audio settings..."
sudo tee /etc/asound.conf > /dev/null << 'EOF'
defaults.bluealsa.interface "hci0"
defaults.bluealsa.profile "a2dp"
defaults.bluealsa.delay 20000
defaults.bluealsa.battery "yes"
EOF

# Add user to audio group
echo "Adding user to audio group..."
sudo usermod -a -G audio $USER

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

# Set up audio mixer levels for optimal volume
echo "Setting up audio levels..."
sudo amixer sset PCM,0 100% > /dev/null 2>&1
sudo amixer sset Master,0 100% > /dev/null 2>&1

# Create a marker file to indicate setup is complete
sudo touch /etc/bluetooth-speaker-setup-complete

# Verify that everything is running correctly
echo "Verifying audio and Bluetooth setup..."

# Check BlueALSA service
if systemctl is-active --quiet bluealsa; then
    echo "✅ BlueALSA service is running"
else
    echo "⚠️  BlueALSA service is not running, starting it now..."
    sudo systemctl start bluealsa
fi

# Check BlueALSA-aplay service (audio routing)
if systemctl is-active --quiet bluealsa-aplay; then
    echo "✅ BlueALSA-aplay service is running (audio routing active)"
else
    echo "⚠️  BlueALSA-aplay service is not running, starting it now..."
    sudo systemctl start bluealsa-aplay
fi

# Check Bluetooth service
if systemctl is-active --quiet bluetooth; then
    echo "✅ Bluetooth service is running"
else
    echo "⚠️  Bluetooth service is not running, starting it now..."
    sudo systemctl start bluetooth
fi

# Test audio output
echo "Testing audio output..."
aplay -l
echo

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
echo "Next steps:"
echo "1. Reboot your Raspberry Pi: sudo reboot"
echo "2. Set your OpenAI API key: export OPENAI_API_KEY='your-key-here'"
echo "3. Build and run the application: dotnet run"
echo "4. Connect your phone to 'The Little Shit' and start playing music!"
echo ""
echo "Audio Routing Information:"
echo "- Music should now play through your Pi's speakers when connected via Bluetooth"
echo "- If audio doesn't work after reboot, run: sudo systemctl restart bluealsa-aplay"
echo "- To check audio routing status: systemctl status bluealsa-aplay"
echo ""
echo "The speaker will automatically insult your music choices! 🎵"
