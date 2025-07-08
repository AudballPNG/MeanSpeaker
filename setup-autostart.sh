#!/bin/bash

# MeanSpeaker Auto-Start Setup Script
# This creates a systemd service to run MeanSpeaker automatically on boot

set -e

echo "Setting up MeanSpeaker as a system service..."

# Get the current user and application path
USER_NAME=$(whoami)
APP_PATH=$(pwd)
DOTNET_PATH=$(which dotnet)

# Check if we're in the right directory
if [ ! -f "BluetoothSpeaker.csproj" ]; then
    echo "Error: Please run this script from the MeanSpeaker project directory"
    exit 1
fi

# Check if .env file exists
if [ ! -f ".env" ]; then
    echo "Error: Please create a .env file with your OpenAI API key first"
    echo "Copy .env.example to .env and add your API key"
    exit 1
fi

# Build the application in release mode
echo "Building MeanSpeaker in release mode..."
dotnet build -c Release

# Create the systemd service file
echo "Creating systemd service file..."
sudo tee /etc/systemd/system/meanspeaker.service > /dev/null <<EOF
[Unit]
Description=MeanSpeaker - Snarky Bluetooth Speaker with AI Commentary
After=network.target bluetooth.target sound.target
Wants=bluetooth.target
Requires=network.target

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

# Audio and Bluetooth permissions
SupplementaryGroups=audio bluetooth pulse-access

[Install]
WantedBy=multi-user.target
EOF

# Create a startup script for better control
echo "Creating startup script..."
sudo tee /usr/local/bin/meanspeaker > /dev/null <<EOF
#!/bin/bash
# MeanSpeaker Control Script

case "\$1" in
    start)
        echo "Starting MeanSpeaker..."
        sudo systemctl start meanspeaker
        ;;
    stop)
        echo "Stopping MeanSpeaker..."
        sudo systemctl stop meanspeaker
        ;;
    restart)
        echo "Restarting MeanSpeaker..."
        sudo systemctl restart meanspeaker
        ;;
    status)
        sudo systemctl status meanspeaker
        ;;
    logs)
        sudo journalctl -u meanspeaker -f
        ;;
    enable)
        echo "Enabling MeanSpeaker to start on boot..."
        sudo systemctl enable meanspeaker
        ;;
    disable)
        echo "Disabling MeanSpeaker auto-start..."
        sudo systemctl disable meanspeaker
        ;;
    *)
        echo "Usage: \$0 {start|stop|restart|status|logs|enable|disable}"
        exit 1
        ;;
esac
EOF

# Make the control script executable
sudo chmod +x /usr/local/bin/meanspeaker

# Reload systemd and enable the service
echo "Enabling MeanSpeaker service..."
sudo systemctl daemon-reload
sudo systemctl enable meanspeaker

# Add user to required groups
echo "Adding user to audio and bluetooth groups..."
sudo usermod -a -G audio,bluetooth $USER_NAME

echo ""
echo "✅ MeanSpeaker auto-start setup complete!"
echo ""
echo "Your Pi will now automatically start MeanSpeaker on boot."
echo ""
echo "Control commands:"
echo "  meanspeaker start    - Start the service"
echo "  meanspeaker stop     - Stop the service"
echo "  meanspeaker restart  - Restart the service"
echo "  meanspeaker status   - Check service status"
echo "  meanspeaker logs     - View live logs"
echo "  meanspeaker disable  - Disable auto-start"
echo ""
echo "To start now: meanspeaker start"
echo "To view logs: meanspeaker logs"
echo ""
echo "Reboot your Pi to test auto-start, or run 'meanspeaker start' now!"
