#!/bin/bash

# TTS Engine Test Script
# This script tests different TTS engines to help you choose the best one

echo "🔊 TTS Engine Test Script"
echo "========================="
echo

TEST_TEXT="Hello there! I'm testing different text-to-speech engines for your snarky bluetooth speaker. Which one sounds the most natural to you?"

echo "Testing available TTS engines..."
echo

# Test Pico TTS
echo "1. Testing Pico TTS (recommended)..."
if command -v pico2wave &> /dev/null && command -v aplay &> /dev/null; then
    echo "   ▶️ Playing Pico TTS sample..."
    TEMP_FILE=$(mktemp).wav
    pico2wave -w "$TEMP_FILE" "$TEST_TEXT" 2>/dev/null && aplay "$TEMP_FILE" 2>/dev/null
    rm -f "$TEMP_FILE" 2>/dev/null
    echo "   ✅ Pico TTS available"
else
    echo "   ❌ Pico TTS not available (install with: sudo apt install libttspico-utils)"
fi

echo

# Test Festival
echo "2. Testing Festival TTS..."
if command -v festival &> /dev/null; then
    echo "   ▶️ Playing Festival sample..."
    echo "$TEST_TEXT" | festival --tts 2>/dev/null
    echo "   ✅ Festival available"
else
    echo "   ❌ Festival not available (install with: sudo apt install festival festvox-kallpc16k)"
fi

echo

# Test eSpeak
echo "3. Testing eSpeak TTS..."
if command -v espeak &> /dev/null; then
    echo "   ▶️ Playing eSpeak sample (female voice)..."
    espeak -v en+f3 -s 160 "$TEST_TEXT" 2>/dev/null
    echo "   ✅ eSpeak available"
else
    echo "   ❌ eSpeak not available (install with: sudo apt install espeak)"
fi

echo
echo "🎯 Recommendation:"
echo "   • For best quality: Use Pico TTS (--tts pico)"
echo "   • For good balance: Use Festival (--tts festival)"  
echo "   • For lightweight: Use eSpeak (--tts espeak)"
echo
echo "💡 To use in your speaker:"
echo "   dotnet run --tts pico      # Use Pico TTS"
echo "   dotnet run --tts festival  # Use Festival"
echo "   dotnet run --tts espeak    # Use eSpeak"
