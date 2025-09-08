#!/bin/bash

# Ollama Local AI Setup Script for Bluetooth Speaker
echo "🤖 Setting up Ollama + Phi-3 Mini for local AI commentary..."

# Check if we're on a supported platform
if [[ "$OSTYPE" != "linux-gnu"* ]]; then
    echo "❌ This script is designed for Linux. Please install Ollama manually on other platforms."
    echo "Visit: https://ollama.ai/download"
    exit 1
fi

# Check if Ollama is already installed
if command -v ollama &> /dev/null; then
    echo "✅ Ollama is already installed"
else
    echo "📥 Installing Ollama..."
    
    # Install Ollama using the official script
    if curl -fsSL https://ollama.ai/install.sh | sh; then
        echo "✅ Ollama installed successfully"
    else
        echo "❌ Failed to install Ollama"
        exit 1
    fi
fi

# Check if Ollama service is running
echo "🔍 Checking Ollama service status..."
if ! pgrep -x "ollama" > /dev/null; then
    echo "🚀 Starting Ollama service..."
    
    # Start Ollama in the background
    ollama serve &
    OLLAMA_PID=$!
    
    # Wait for service to start
    sleep 5
    
    if pgrep -x "ollama" > /dev/null; then
        echo "✅ Ollama service started successfully"
    else
        echo "❌ Failed to start Ollama service"
        exit 1
    fi
else
    echo "✅ Ollama service is already running"
fi

# Wait for Ollama to be ready
echo "⏳ Waiting for Ollama to be ready..."
for i in {1..30}; do
    if curl -s http://localhost:11434/api/tags > /dev/null 2>&1; then
        echo "✅ Ollama is ready"
        break
    fi
    sleep 1
    if [ $i -eq 30 ]; then
        echo "❌ Ollama failed to start properly"
        exit 1
    fi
done

# Check if Phi-3 Mini is already installed
echo "🔍 Checking if Phi-3 Mini model is available..."
if ollama list | grep -q "phi3:mini"; then
    echo "✅ Phi-3 Mini is already installed"
else
    echo "📥 Downloading Phi-3 Mini model (this may take several minutes)..."
    echo "⏳ Model size: ~2.4GB - please be patient..."
    
    if ollama pull phi3:mini; then
        echo "✅ Phi-3 Mini downloaded successfully"
    else
        echo "❌ Failed to download Phi-3 Mini model"
        exit 1
    fi
fi

# Test the model
echo "🧪 Testing Phi-3 Mini model..."
TEST_RESPONSE=$(ollama run phi3:mini "Say 'Hello from Phi-3 Mini!' in a snarky way" --format json 2>/dev/null | tail -1)

if echo "$TEST_RESPONSE" | grep -q "Hello\|Phi-3"; then
    echo "✅ Phi-3 Mini is working correctly"
else
    echo "⚠️ Phi-3 Mini test didn't return expected response, but model seems installed"
fi

# Create systemd service for auto-start (optional)
read -p "🤔 Do you want Ollama to start automatically on boot? (y/N): " -n 1 -r
echo
if [[ $REPLY =~ ^[Yy]$ ]]; then
    echo "📝 Creating systemd service for Ollama..."
    
    # Create systemd service file
    sudo tee /etc/systemd/system/ollama.service > /dev/null << EOF
[Unit]
Description=Ollama Service
After=network-online.target

[Service]
ExecStart=/usr/local/bin/ollama serve
User=$USER
Group=$USER
Restart=always
RestartSec=3
Environment="PATH=$PATH"

[Install]
WantedBy=default.target
EOF

    # Enable and start the service
    sudo systemctl daemon-reload
    sudo systemctl enable ollama.service
    sudo systemctl start ollama.service
    
    echo "✅ Ollama will now start automatically on boot"
else
    echo "ℹ️ Ollama will need to be started manually with 'ollama serve'"
fi

echo ""
echo "🎉 Local AI setup complete!"
echo ""
echo "Usage:"
echo "  1. Make sure Ollama is running: ollama serve"
echo "  2. Run your Bluetooth Speaker: dotnet run -- --local-ai"
echo "  3. Connect your device and enjoy snarky local AI commentary!"
echo ""
echo "Benefits of local AI:"
echo "  ✅ Completely offline - no internet required"
echo "  ✅ Private - your music data never leaves the device"
echo "  ✅ No API costs - unlimited commentary"
echo "  ✅ Fast responses - no network latency"
echo "  ✅ Commercial-ready - MIT licensed Phi-3 Mini"
echo ""
echo "Model info:"
echo "  📋 Model: Microsoft Phi-3 Mini (2.4GB)"
echo "  📜 License: MIT (commercial use allowed)"
echo "  🎯 Optimized for: Creative text generation"
echo "  💾 Memory usage: ~4GB RAM recommended"
echo ""

# Check system resources
TOTAL_RAM=$(free -h | awk '/^Mem:/ {print $2}')
echo "💾 System RAM: $TOTAL_RAM"
echo "💡 Recommended: 8GB+ for best performance"

if ollama list | grep -q "phi3:mini"; then
    echo ""
    echo "🚀 Ready to run! Execute: dotnet run -- --local-ai"
fi
