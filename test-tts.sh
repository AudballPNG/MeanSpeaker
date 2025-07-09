#!/bin/bash

# TTS Engine Test Script
echo "🗣️ Testing all TTS engines..."

TEST_TEXT="Your music taste is questionable at best, but I'll play along."

echo "Testing Piper neural TTS..."
if command -v piper &> /dev/null; then
    echo "$TEST_TEXT" | piper --model en_US-lessac-medium --output_file /tmp/test_piper.wav 2>/dev/null && aplay /tmp/test_piper.wav 2>/dev/null
    echo "✅ Piper test complete"
else
    echo "❌ Piper not found"
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