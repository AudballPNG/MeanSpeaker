#!/bin/bash

# Script to properly run the Bluetooth Speaker on Raspberry Pi

echo "🎵 Preparing to run Snarky Bluetooth Speaker on Raspberry Pi..."

# Check if this is the first run by looking for setup marker
SETUP_MARKER="/tmp/meanspeaker-setup-complete"

if [ ! -f "$SETUP_MARKER" ]; then
    echo "🔧 First run detected. Running system setup..."
    
    # Run Raspberry Pi setup script
    if [ -f "setup-raspberry-pi.sh" ]; then
        echo "Setting up Raspberry Pi Bluetooth system..."
        chmod +x setup-raspberry-pi.sh
        ./setup-raspberry-pi.sh
        
        if [ $? -eq 0 ]; then
            echo "✅ Raspberry Pi setup completed successfully!"
        else
            echo "❌ Raspberry Pi setup failed. Please check the errors above."
            exit 1
        fi
    else
        echo "⚠️  setup-raspberry-pi.sh not found. Skipping system setup."
    fi
    
    # Create setup marker
    touch "$SETUP_MARKER"
    echo "📝 Setup marker created. System setup will be skipped on future runs."
    echo ""
fi

# Clean any leftover build artifacts
echo "Cleaning build artifacts..."
dotnet clean

# Remove obj and bin directories completely to ensure clean build
echo "Removing obj and bin directories..."
rm -rf obj/
rm -rf bin/

# Restore packages
echo "Restoring NuGet packages..."
dotnet restore

# Build the application
echo "Building application..."
dotnet build

# Check if build was successful
if [ $? -eq 0 ]; then
    echo "✅ Build successful! Starting application..."
    echo ""
    
    # Set OpenAI API key if not already set
    if [ -z "$OPENAI_API_KEY" ]; then
        echo "⚠️  OpenAI API key not found in environment variables."
        echo "You'll be prompted to enter it when the application starts."
        echo ""
    fi
    
    # Run the application
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
    if [ -f "setup-autostart.sh" ]; then
        echo "Setting up auto-start service..."
        chmod +x setup-autostart.sh
        ./setup-autostart.sh
        
        if [ $? -eq 0 ]; then
            echo "✅ Auto-start setup completed successfully!"
            echo "MeanSpeaker will now start automatically on boot."
        else
            echo "❌ Auto-start setup failed. Please check the errors above."
        fi
    else
        echo "⚠️  setup-autostart.sh not found. Cannot set up auto-start."
    fi
else
    echo "Auto-start setup skipped. You can run setup-autostart.sh manually later."
fi
