#!/bin/bash

# Manual Piper TTS Installation Script
# Handles externally-managed-environment restrictions

echo "🗣️ Manual Piper TTS Installation..."
echo "📋 This script handles the 'externally-managed-environment' restriction"

# Check current Python environment
echo "🔍 Checking Python environment..."
python3 --version
pip3 --version

# Method 1: Try standard pip install first
echo ""
echo "🔧 Method 1: Trying standard pip install..."
if sudo pip3 install piper-tts 2>&1; then
    echo "✅ Standard installation successful!"
    INSTALL_METHOD="standard"
elif pip3 install piper-tts 2>&1 | grep -q "externally-managed-environment"; then
    echo "⚠️ externally-managed-environment detected"
    
    # Method 2: Try with --break-system-packages
    echo ""
    echo "🔧 Method 2: Trying with --break-system-packages..."
    if sudo pip3 install --break-system-packages piper-tts 2>&1; then
        echo "✅ Installation with --break-system-packages successful!"
        INSTALL_METHOD="break-system-packages"
    else
        echo "❌ --break-system-packages failed"
        
        # Method 3: Create system virtual environment
        echo ""
        echo "🔧 Method 3: Creating system virtual environment..."
        
        # Install python3-venv if not available
        echo "📦 Installing python3-venv..."
        sudo apt-get update
        sudo apt-get install -y python3-venv python3-full
        
        # Create virtual environment in /opt
        echo "📁 Creating virtual environment in /opt/piper-venv..."
        sudo python3 -m venv /opt/piper-venv
        
        # Install piper in the virtual environment
        echo "📦 Installing Piper in virtual environment..."
        sudo /opt/piper-venv/bin/pip install piper-tts
        
        if [ $? -eq 0 ]; then
            echo "✅ Virtual environment installation successful!"
            INSTALL_METHOD="venv"
            
            # Create wrapper script
            echo "🔧 Creating wrapper script..."
            sudo tee /usr/local/bin/piper-venv > /dev/null << 'EOF'
#!/bin/bash
/opt/piper-venv/bin/python -m piper "$@"
EOF
            sudo chmod +x /usr/local/bin/piper-venv
        else
            echo "❌ Virtual environment installation failed"
            INSTALL_METHOD="failed"
        fi
    fi
else
    echo "❌ Standard installation failed for other reasons"
    INSTALL_METHOD="failed"
fi

# Create universal piper alias
if [ "$INSTALL_METHOD" != "failed" ]; then
    echo ""
    echo "🔧 Creating universal Piper command alias..."
    sudo tee /usr/local/bin/piper > /dev/null << 'EOF'
#!/bin/bash

# Universal Piper command that handles different installation methods
if command -v piper-tts >/dev/null 2>&1; then
    # Direct piper-tts command available
    piper-tts "$@"
elif python3 -c "import piper" >/dev/null 2>&1; then
    # Python module available globally
    python3 -m piper "$@"
elif [ -x "/opt/piper-venv/bin/python" ]; then
    # Virtual environment installation
    /opt/piper-venv/bin/python -m piper "$@"
elif [ -x "/usr/local/bin/piper-venv" ]; then
    # Use the venv wrapper
    /usr/local/bin/piper-venv "$@"
else
    echo "❌ Piper not found. Installation may have failed."
    echo "ℹ️ Try running this script again or check the installation manually."
    exit 1
fi
EOF
    sudo chmod +x /usr/local/bin/piper
    
    echo "✅ Universal piper command created"
fi

# Download voice models
echo ""
echo "📥 Downloading voice models..."
mkdir -p /home/pi/.local/share/piper/voices
cd /home/pi/.local/share/piper/voices

# Download popular English voices
echo "📥 Downloading en_US-lessac-medium voice..."
wget -q https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/lessac/medium/en_US-lessac-medium.onnx
wget -q https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/lessac/medium/en_US-lessac-medium.onnx.json

echo "📥 Downloading en_US-ryan-medium voice..."
wget -q https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/ryan/medium/en_US-ryan-medium.onnx
wget -q https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/ryan/medium/en_US-ryan-medium.onnx.json

# Set proper permissions
chown -R pi:pi /home/pi/.local/share/piper/ 2>/dev/null || true

echo ""
echo "🧪 Testing Piper installation..."

# Test the installation
TEST_TEXT="Hello, this is a test of Piper neural text to speech."

if command -v piper >/dev/null 2>&1; then
    echo "🔊 Testing piper command..."
    if echo "$TEST_TEXT" | piper --output_file /tmp/piper_install_test.wav 2>/dev/null; then
        echo "✅ Piper test successful!"
        if command -v aplay >/dev/null 2>&1; then
            echo "🔊 Playing test audio..."
            aplay /tmp/piper_install_test.wav 2>/dev/null
        fi
        rm -f /tmp/piper_install_test.wav
    else
        echo "⚠️ Piper test failed"
    fi
else
    echo "❌ Piper command not available"
fi

echo ""
echo "📋 Installation Summary:"
echo "   Method used: $INSTALL_METHOD"
echo "   Voice models: $(ls /home/pi/.local/share/piper/voices/*.onnx 2>/dev/null | wc -l) installed"
echo "   Piper command: $(command -v piper >/dev/null 2>&1 && echo "Available" || echo "Not available")"

if [ "$INSTALL_METHOD" = "failed" ]; then
    echo ""
    echo "❌ Installation failed!"
    echo "💡 Manual alternatives:"
    echo "   1. Use apt: sudo apt install python3-piper-tts (if available)"
    echo "   2. Use pipx: sudo apt install pipx && pipx install piper-tts"
    echo "   3. Use Docker: docker run -it rhasspy/piper"
    exit 1
else
    echo ""
    echo "✅ Piper TTS installation complete!"
    echo "🎯 You can now use: piper --help"
fi
