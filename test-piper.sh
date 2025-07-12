#!/bin/bash

echo "🧪 Testing Piper TTS Installation..."

# Test 1: Check if piper command exists
echo "📋 Checking piper command..."
if command -v piper &> /dev/null; then
    echo "✅ piper command found"
else
    echo "❌ piper command not found"
    echo "🔧 Trying to create alias..."
    sudo tee /usr/local/bin/piper > /dev/null << 'EOF'
#!/bin/bash
python3 -m piper "$@"
EOF
    sudo chmod +x /usr/local/bin/piper
    echo "✅ Created piper alias"
fi

# Test 2: Check if piper module is installed
echo "📋 Checking piper module..."
if python3 -c "import piper" 2>/dev/null; then
    echo "✅ piper module found"
else
    echo "❌ piper module not found"
    echo "🔧 Installing piper-tts..."
    sudo pip3 install piper-tts
fi

# Test 3: Check for voice models
echo "📋 Checking voice models..."
VOICE_DIR="/home/pi/.local/share/piper/voices"
if [ -d "$VOICE_DIR" ] && [ "$(ls -A $VOICE_DIR)" ]; then
    echo "✅ Voice models found:"
    ls -la "$VOICE_DIR"/*.onnx 2>/dev/null || echo "  No .onnx files found"
else
    echo "❌ No voice models found"
    echo "📥 Downloading default voice models..."
    mkdir -p "$VOICE_DIR"
    cd "$VOICE_DIR"
    wget -q https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/lessac/medium/en_US-lessac-medium.onnx
    wget -q https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/lessac/medium/en_US-lessac-medium.onnx.json
    chown -R pi:pi /home/pi/.local/share/piper/
    echo "✅ Downloaded default voice model"
fi

# Test 4: Test basic TTS
echo "📋 Testing TTS synthesis..."
TEST_TEXT="Hello, this is a test of Piper neural text to speech."

# Try with alias first
if command -v piper &> /dev/null; then
    echo "🔊 Testing with piper command..."
    if echo "$TEST_TEXT" | piper --output_file /tmp/piper_test.wav 2>/dev/null; then
        echo "✅ Piper command works"
        if command -v aplay &> /dev/null; then
            echo "🔊 Playing test audio..."
            aplay /tmp/piper_test.wav 2>/dev/null
        fi
    else
        echo "❌ Piper command failed"
    fi
fi

# Try with python module
echo "🔊 Testing with python module..."
if echo "$TEST_TEXT" | python3 -m piper --output_file /tmp/piper_test2.wav 2>/dev/null; then
    echo "✅ Python module works"
    if command -v aplay &> /dev/null; then
        echo "🔊 Playing test audio..."
        aplay /tmp/piper_test2.wav 2>/dev/null
    fi
else
    echo "❌ Python module failed"
fi

# Test 5: Test with specific voice model
if [ -f "/home/pi/.local/share/piper/voices/en_US-lessac-medium.onnx" ]; then
    echo "🔊 Testing with specific voice model..."
    if echo "$TEST_TEXT" | piper --model /home/pi/.local/share/piper/voices/en_US-lessac-medium.onnx --output_file /tmp/piper_test3.wav 2>/dev/null; then
        echo "✅ Specific voice model works"
        if command -v aplay &> /dev/null; then
            echo "🔊 Playing test audio..."
            aplay /tmp/piper_test3.wav 2>/dev/null
        fi
    else
        echo "❌ Specific voice model failed"
    fi
fi

# Cleanup
rm -f /tmp/piper_test*.wav

echo "🎯 Piper TTS test complete!"
