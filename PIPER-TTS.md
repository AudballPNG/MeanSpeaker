# Piper Neural TTS Integration

This document describes the Piper neural text-to-speech integration in the Bluetooth Speaker project.

## What is Piper?

Piper is a fast, local neural text-to-speech system developed by the Home Assistant team. It produces very natural-sounding speech using neural networks while being optimized for embedded systems like Raspberry Pi.

## Why Piper?

- **Neural Quality**: Much more natural than traditional TTS engines
- **Local/Offline**: No internet connection required  
- **Fast**: Optimized for real-time speech generation
- **ARM Optimized**: Built specifically for devices like Raspberry Pi
- **Lightweight**: Reasonable resource usage for neural TTS

## Installation

Piper installation has been enhanced to handle the "externally-managed-environment" restriction on modern Python installations.

### Automatic Installation (Recommended)
```bash
# Run the enhanced setup script
./simple-setup.sh
```

### Manual Installation
If automatic installation fails, use the dedicated Piper installer:
```bash
# Run the manual Piper installer
chmod +x install-piper.sh
./install-piper.sh
```

### Installation Methods

The installer tries multiple methods in order:

1. **Standard pip**: `pip3 install piper-tts`
2. **Break system packages**: `pip3 install --break-system-packages piper-tts`
3. **Virtual environment**: Creates `/opt/piper-venv` and installs there
4. **APT package**: `apt install python3-piper-tts` (if available)

### Universal Command

A universal `piper` command is created that automatically detects and uses the available installation method:

```bash
# The piper command automatically handles:
# - Direct piper-tts command
# - Python module (python3 -m piper)
# - Virtual environment (/opt/piper-venv/bin/python -m piper)
# - Wrapper scripts

# Usage remains the same:
echo "Hello world" | piper --output_file test.wav
```

## Voice Models

The setup script downloads these voice models by default:

| Voice | Gender | Style | Size |
|-------|--------|-------|------|
| `en_US-lessac-medium` | Female | Natural | ~25MB |
| `en_US-ryan-medium` | Male | Natural | ~25MB |

Additional voices can be downloaded from: https://huggingface.co/rhasspy/piper-voices

## Usage

### Command Line
```bash
# Use Piper with default voice
dotnet run -- --tts piper

# Use specific Piper voice
dotnet run -- --tts piper --voice en_US-ryan-medium
dotnet run -- --tts piper --voice en_US-lessac-medium
```

### Programmatic
```csharp
var monitor = new MusicMonitor(apiKey, enableSpeech: true, 
                               ttsVoice: "en_US-lessac-medium", 
                               ttsEngine: "piper");
```

## Fallback Behavior

If Piper fails for any reason, the system automatically falls back to eSpeak:

1. Attempt Piper neural TTS
2. If Piper fails → Try eSpeak  
3. If eSpeak fails → Silent operation

## Performance

**Raspberry Pi 4:**
- Speech generation: ~0.5-1 second for typical comment
- Memory usage: ~50-100MB 
- CPU usage: Moderate (brief spikes during generation)

**Raspberry Pi 3B+:**
- Speech generation: ~1-2 seconds for typical comment
- Memory usage: ~50-100MB
- CPU usage: Higher but acceptable

## Troubleshooting

### Piper Not Found
```bash
# Check if installed
which piper
pip3 list | grep piper

# Check installation method
ls -la /usr/local/bin/piper          # Universal command
ls -la /opt/piper-venv/              # Virtual environment
python3 -c "import piper"            # Python module

# Reinstall using manual installer
./install-piper.sh

# Or try different methods manually:

# Method 1: Break system packages
sudo pip3 install --break-system-packages piper-tts

# Method 2: Virtual environment
sudo python3 -m venv /opt/piper-venv
sudo /opt/piper-venv/bin/pip install piper-tts

# Method 3: APT (if available)
sudo apt install python3-piper-tts

# Test directly with different methods
echo "test" | python3 -m piper --output_file /tmp/test.wav
echo "test" | /opt/piper-venv/bin/python -m piper --output_file /tmp/test.wav
echo "test" | piper-tts --output_file /tmp/test.wav
```

### Externally-Managed-Environment Error
This error occurs on modern Python installations. Solutions:

```bash
# Use the manual installer (recommended)
./install-piper.sh

# Or manually use one of these approaches:
sudo pip3 install --break-system-packages piper-tts
sudo python3 -m venv /opt/piper-venv && sudo /opt/piper-venv/bin/pip install piper-tts
sudo apt install python3-piper-tts  # if available
```

### Voice Models Missing
```bash
# Check voice directory
ls -la /home/pi/.local/share/piper/voices/

# Re-download if needed
cd /home/pi/.local/share/piper/voices/
wget -q https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/lessac/medium/en_US-lessac-medium.onnx
wget -q https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/lessac/medium/en_US-lessac-medium.onnx.json
```

### Audio Issues
```bash
# Test Piper directly
echo "Hello world" | piper --model /home/pi/.local/share/piper/voices/en_US-lessac-medium.onnx --output_file /tmp/test.wav
aplay /tmp/test.wav

# Check ALSA
aplay -l
```

## Quality Comparison

Subjective quality rankings for the snarky AI commentary:

1. **Piper** - Very natural, great for sarcastic delivery
2. **Pico** - Good traditional synthesis, clear
3. **Festival** - Good but slightly robotic
4. **eSpeak** - Fast but very robotic

For the best "snarky AI" experience, Piper's natural intonation makes the commentary much more entertaining!
