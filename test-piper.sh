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

# Piper TTS configuration
PIPER_MODEL="/home/audrey/.local/share/piper/voices/en_US-lessac-medium.onnx"
OUTPUT_DIR="/dev/shm"

# Function to speak a single sentence
speak_sentence() {
    local sentence="$1"
    local temp_wav="$OUTPUT_DIR/speech_$(date +%s%N).wav"
    
    echo "🔊 Speaking: $sentence"
    
    # Generate audio for the sentence
    echo "$sentence" | piper --model "$PIPER_MODEL" --output_file "$temp_wav"
    
    # Play the audio file
    aplay -q "$temp_wav"
    
    # Clean up the temporary file
    rm "$temp_wav"
}

# Main TTS function to handle multi-sentence text
speak_tts() {
    local text="$1"
    
    # Split text into sentences (handles '.', '?', '!')
    # The -n 1 ensures we process one sentence at a time
    echo "$text" | sed -e 's/[.?!]/&\n/g' | while IFS= read -r sentence; do
        # Trim leading/trailing whitespace
        sentence=$(echo "$sentence" | xargs)
        if [ -n "$sentence" ]; then
            speak_sentence "$sentence"
        fi
    done
}

# The text to be spoken
TEXT_TO_SPEAK="Oh great, another victim of questionable music taste. Brace yourself, Audrey, for the eardrum assault of basic pop hits and generic love ballads that you subject the world to."

echo "Starting TTS test..."
speak_tts "$TEXT_TO_SPEAK"
echo "TTS test finished."

echo "🎯 Piper TTS test complete!"
