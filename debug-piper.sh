#!/bin/bash

echo "🧪 Piper TTS Debug Test"
echo "======================"

# Get current user and home directory
CURRENT_USER="${SUDO_USER:-$USER}"
USER_HOME="${HOME:-$(eval echo ~$CURRENT_USER)}"

echo "User: $CURRENT_USER"
echo "Home: $USER_HOME"
echo ""

# Test text
TEST_TEXT="Hello, this is a test of the Piper text to speech system."

echo "🔍 Checking Piper installation methods..."

# Check 1: piper command
if command -v piper >/dev/null 2>&1; then
    echo "✅ piper command found at: $(which piper)"
    echo "🔊 Testing piper command..."
    if echo "$TEST_TEXT" | piper --output_file /tmp/piper_debug1.wav 2>&1; then
        echo "✅ piper command works"
        if aplay /tmp/piper_debug1.wav 2>/dev/null; then
            echo "✅ Audio playback works"
        else
            echo "❌ Audio playback failed"
        fi
    else
        echo "❌ piper command failed"
    fi
    rm -f /tmp/piper_debug1.wav
else
    echo "❌ piper command not found"
fi

echo ""

# Check 2: python module
if python3 -c "import piper" 2>/dev/null; then
    echo "✅ Python piper module found"
    echo "🔊 Testing python module..."
    if echo "$TEST_TEXT" | python3 -m piper --output_file /tmp/piper_debug2.wav 2>&1; then
        echo "✅ Python module works"
        if aplay /tmp/piper_debug2.wav 2>/dev/null; then
            echo "✅ Audio playback works"
        else
            echo "❌ Audio playback failed"
        fi
    else
        echo "❌ Python module failed"
    fi
    rm -f /tmp/piper_debug2.wav
else
    echo "❌ Python piper module not found"
fi

echo ""

# Check 3: Virtual environment
if [ -x "/opt/piper-venv/bin/python" ]; then
    echo "✅ Virtual environment found"
    echo "🔊 Testing virtual environment..."
    if echo "$TEST_TEXT" | /opt/piper-venv/bin/python -m piper --output_file /tmp/piper_debug3.wav 2>&1; then
        echo "✅ Virtual environment works"
        if aplay /tmp/piper_debug3.wav 2>/dev/null; then
            echo "✅ Audio playback works"
        else
            echo "❌ Audio playback failed"
        fi
    else
        echo "❌ Virtual environment failed"
    fi
    rm -f /tmp/piper_debug3.wav
else
    echo "❌ Virtual environment not found"
fi

echo ""

# Check voice models
echo "🔍 Checking voice models..."
VOICE_DIR="$USER_HOME/.local/share/piper/voices"
if [ -d "$VOICE_DIR" ]; then
    echo "✅ Voice directory exists: $VOICE_DIR"
    echo "Voice models found:"
    ls -la "$VOICE_DIR"/*.onnx 2>/dev/null || echo "  No .onnx files found"
    
    # Test with specific voice model if available
    VOICE_MODEL="$VOICE_DIR/en_US-lessac-medium.onnx"
    if [ -f "$VOICE_MODEL" ]; then
        echo ""
        echo "🔊 Testing with specific voice model..."
        if command -v piper >/dev/null 2>&1; then
            if echo "$TEST_TEXT" | piper --model "$VOICE_MODEL" --output_file /tmp/piper_debug_voice.wav 2>&1; then
                echo "✅ Specific voice model works"
                aplay /tmp/piper_debug_voice.wav 2>/dev/null
            else
                echo "❌ Specific voice model failed"
            fi
            rm -f /tmp/piper_debug_voice.wav
        fi
    else
        echo "⚠️ Default voice model not found: $VOICE_MODEL"
    fi
else
    echo "❌ Voice directory not found: $VOICE_DIR"
fi

echo ""

# Check audio system
echo "🔍 Checking audio system..."
if command -v aplay >/dev/null 2>&1; then
    echo "✅ aplay found"
    echo "Audio devices:"
    aplay -l 2>/dev/null | head -10
else
    echo "❌ aplay not found"
fi

echo ""
echo "🎯 Debug test complete!"
