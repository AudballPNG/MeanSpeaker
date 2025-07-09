#!/bin/bash

# TTS Engine Test Script
echo "🗣️ Testing all TTS engines..."

TEST_TEXT="Your music taste is questionable at best, but I'll play along."

echo "Testing Piper neural TTS..."

# Automatically detect user home directory
USER_HOME="${HOME:-$(eval echo ~$USER)}"
VOICE_MODEL_PATH="$USER_HOME/.local/share/piper/voices/en_US-lessac-medium.onnx"

# Test the piper alias (should handle all installation methods)
if command -v piper &> /dev/null; then
    echo "✅ Piper alias found, testing..."
    echo "$TEST_TEXT" | piper --output_file /tmp/test_piper.wav 2>/dev/null && aplay /tmp/test_piper.wav 2>/dev/null
    echo "✅ Piper (alias) test complete"
    
    # Test with specific voice if available
    if [ -f "$VOICE_MODEL_PATH" ]; then
        echo "$TEST_TEXT" | piper --model "$VOICE_MODEL_PATH" --output_file /tmp/test_piper2.wav 2>/dev/null && aplay /tmp/test_piper2.wav 2>/dev/null
        echo "✅ Piper (specific model) test complete"
    else
        echo "ℹ️ Voice model not found at: $VOICE_MODEL_PATH"
    fi
elif command -v piper-tts &> /dev/null; then
    # Direct piper-tts command
    echo "✅ piper-tts command found, testing..."
    echo "$TEST_TEXT" | piper-tts --output_file /tmp/test_piper.wav 2>/dev/null && aplay /tmp/test_piper.wav 2>/dev/null
    echo "✅ Piper-tts test complete"
elif python3 -c "import piper" 2>/dev/null; then
    # Python module
    echo "✅ Piper python module found, testing..."
    echo "$TEST_TEXT" | python3 -m piper --output_file /tmp/test_piper.wav 2>/dev/null && aplay /tmp/test_piper.wav 2>/dev/null
    echo "✅ Piper (python module) test complete"
elif [ -x "/opt/piper-venv/bin/python" ]; then
    # Virtual environment
    echo "✅ Piper virtual environment found, testing..."
    echo "$TEST_TEXT" | /opt/piper-venv/bin/python -m piper --output_file /tmp/test_piper.wav 2>/dev/null && aplay /tmp/test_piper.wav 2>/dev/null
    echo "✅ Piper (virtual env) test complete"
else
    echo "❌ Piper not found - run simple-setup.sh to install"
    echo "ℹ️ Installation issue detected:"
    if pip3 list 2>/dev/null | grep -q piper; then
        echo "   - Piper package is installed but not accessible"
        echo "   - Try: sudo pip3 install --break-system-packages piper-tts"
    else
        echo "   - Piper package not installed"
        echo "   - This may be due to externally-managed-environment restriction"
    fi
fi

echo ""
echo "Testing Pico TTS..."
if command -v pico2wave &> /dev/null; then
    echo "$TEST_TEXT" | pico2wave -w /tmp/test_pico.wav 2>/dev/null && aplay /tmp/test_pico.wav 2>/dev/null
    echo "✅ Pico test complete"
else
    echo "❌ Pico not found"
fi

echo ""
echo "Testing Festival TTS..."
if command -v festival &> /dev/null; then
    echo "$TEST_TEXT" | festival --tts 2>/dev/null
    echo "✅ Festival test complete"  
else
    echo "❌ Festival not found"
fi

echo ""
echo "Testing eSpeak TTS..."
if command -v espeak &> /dev/null; then
    espeak -v en+f3 -s 160 "$TEST_TEXT" 2>/dev/null
    echo "✅ eSpeak test complete"
else
    echo "❌ eSpeak not found"
fi

echo ""
echo "🎵 TTS test complete! Now testing the actual application..."
echo ""

# Test the application with different engines
echo "Testing application with Piper..."
timeout 5 dotnet run -- --tts piper --no-speech &
sleep 2
pkill -f "dotnet run" 2>/dev/null

echo "Testing application with Pico..."  
timeout 5 dotnet run -- --tts pico --no-speech &
sleep 2
pkill -f "dotnet run" 2>/dev/null

echo "Testing application with eSpeak..."
timeout 5 dotnet run -- --tts espeak --no-speech &
sleep 2
pkill -f "dotnet run" 2>/dev/null

echo ""
echo "✅ All TTS engine tests completed!"
echo "Run 'dotnet run -- --help' for usage options."