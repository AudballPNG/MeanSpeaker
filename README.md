# Simple Snarky Bluetooth Speaker

A Raspberry Pi Bluetooth speaker that plays your music AND provides snarky AI commentary about your music choices using OpenAI's GPT.

**🚀 Setup once, works forever! Automatically starts on boot.**

## Features

- **🎵 Bluetooth A2DP Audio Sink**: Works like any normal Bluetooth speaker
- **🤖 AI Music Commentary**: Uses OpenAI GPT to generate witty, sarcastic comments about your music
- **🔊 Text-to-Speech**: Multiple TTS engines supported (Pico, Festival, eSpeak)
- **📱 Simple Setup**: One script setup, no complex configuration
- **🔧 Reliable**: Uses proven command-line tools, not complex D-Bus event systems
- **⚡ Auto-Start**: Runs automatically on boot after one-time setup

## How It Works

**Like a normal Bluetooth speaker:**
```
Phone → Bluetooth → BlueALSA → Speakers
```

**Plus AI commentary:**
```
playerctl → Track Detection → AI Commentary → Text-to-Speech → Speakers
```

## Quick Setup

**Super Simple - Just run one command!**

```bash
git clone <your-repo-url>
cd BluetoothSpeaker
chmod +x run.sh
./run.sh
```

**That's it!** The program will automatically:
- Install .NET 8 if needed
- Install all required packages (bluetooth, bluez, playerctl, espeak, etc.)
- Configure Bluetooth services
- Set up audio routing
- Make your device discoverable as "The Little Shit"
- Start monitoring for connections and music

### Optional: Set OpenAI API Key for AI Commentary
```bash
export OPENAI_API_KEY="your-api-key-here"
./run.sh
```

If you don't provide an API key, the speaker will still work but with simple fallback commentary instead of AI-generated wit.

### Alternative: Direct dotnet run
```bash
dotnet run
```

Both methods do the same automatic setup!

## Usage

**Just run and go!**

1. **Run the program**: `dotnet run`
2. **Connect your phone** to "The Little Shit" via Bluetooth
3. **Play music** - it works like any normal Bluetooth speaker
4. **Listen to the commentary** - the AI will comment on your music choices

### Command Line Options
```bash
# Run with default settings (speech enabled)
dotnet run

# Run without speech (console only)
dotnet run -- --no-speech

# Run with specific voice
dotnet run -- --voice en+f4

# Run with male voice
dotnet run -- --voice en+m3
```

### Interactive Commands
While the program is running, you can type:
- `status` - Show connection and service status
- `test` - Force generate a test comment
- `quit` or `exit` - Stop the application

## Troubleshooting

### Program Won't Start
```bash
# Make sure .NET 8 is installed
dotnet --version

# Try running with more verbose output
dotnet run --verbosity detailed
```

### No Audio Playing
The program automatically fixes audio routing, but if you still have issues:
```bash
# Check if services are running
systemctl status bluealsa bluealsa-aplay

# Restart the program (it will fix services automatically)
dotnet run
```

### No Device Detection
```bash
# Make sure Bluetooth is working
bluetoothctl devices Connected
bluetoothctl show

# The program makes your device discoverable automatically
# Just restart it if needed
```

### No Track Detection
The program tries multiple methods to detect tracks. If it's not working:
```bash
# Check if playerctl works
playerctl status
playerctl metadata

# Or just restart the program
dotnet run
```

### Permission Issues
If you get permission errors, you might need to run with sudo the first time:
```bash
sudo dotnet run
```

After the first setup, you should be able to run without sudo.

## How It's Different

**Normal Bluetooth speakers:** Just play audio
**This speaker:** Plays audio + judges your music taste

**Complex implementations:** Use D-Bus events, complex state management, etc.
**This implementation:** Simple 2-second polling like normal media players

## Architecture

- **🎵 MusicMonitor**: Single class that handles everything
- **📱 Device Detection**: `bluetoothctl devices Connected` every 2 seconds
- **🎧 Track Detection**: `playerctl metadata` every 2 seconds  
- **🤖 AI Commentary**: OpenAI GPT-3.5-turbo with snarky prompts
- **🔊 Text-to-Speech**: Multiple TTS engines with fallback support

**Total complexity:** ~500 lines of C# vs 1500+ lines in the old version

## Voice & TTS Options

```bash
# TTS Engine Options
dotnet run --tts pico       # Pico TTS (default, most natural)
dotnet run --tts festival   # Festival TTS (good quality)
dotnet run --tts espeak     # eSpeak TTS (lightweight, robotic)

# Voice personalities (for eSpeak)
dotnet run --voice en+f3    # Default female
dotnet run --voice en+m3    # Male voice
dotnet run --voice en+f4    # Different female

# Combined options
dotnet run --tts pico                    # Best quality
dotnet run --tts espeak --voice en+m3    # Male eSpeak voice
dotnet run --no-speech                   # Text only, no speech
```

**TTS Engine Quality Ranking:**
1. **Pico TTS** - Most natural, developed by SVOX (used in Android)
2. **Festival** - Good quality, University of Edinburgh
3. **eSpeak** - Lightweight but robotic sounding

**🎯 Test TTS Engines:**
```bash
chmod +x test-tts.sh
./test-tts.sh
```
This script will play samples of each TTS engine so you can choose your favorite!

## Why This Approach Works

1. **🎯 Simple**: Uses the same tools normal media players use
2. **🔧 Reliable**: Command-line tools are battle-tested
3. **🚀 Fast**: No complex event systems or D-Bus watchers
4. **📊 Debuggable**: Easy to see what's happening
5. **🎵 Normal**: Works exactly like a regular Bluetooth speaker with added AI

The key insight: **You don't need complex event systems to detect music changes.** Just poll the same tools that media players use!

2. **Or use the built-in audio fix**:
   ```bash
   sudo ./run-on-pi.sh --fix-audio
   ```

3. **Verify services are running**:
   ```bash
   systemctl status bluealsa-aplay
   ```

The most common issue is that the audio routing service (`bluealsa-aplay`) 
isn't running. This service routes audio from BlueALSA to your speakers.

### Assembly Build Issues?

If you get assembly attribute errors when building:

```bash
./run-on-pi.sh --fix-assembly
```

## Usage

### Basic Usage
```bash
# Run with default settings (speech enabled)
dotnet run

# Run without speech (console only)
dotnet run -- --no-speech

# Run with specific voice
dotnet run -- --voice en+f4

# Run with male voice
dotnet run -- --voice en+m3
```

### Available Voices
- `en+f3` - Female voice 3 (default)
- `en+f4` - Female voice 4 (higher pitch)
- `en+m3` - Male voice 3
- `en+m4` - Male voice 4
- `en+croak` - Creaky voice (fun for insults!)
- `en+whisper` - Whisper voice

### What You'll Hear

When someone connects and plays music, your speaker will:
- Announce device connections
- Comment on music choices
- Roast repetitive listening habits
- Provide session summaries

Example interactions:
> 🔊 "Oh great, iPhone just connected. Let me guess, you're about to blast some questionable music choices through me?"

> 🔊 "Taylor Swift again? That's the third time this session. Are you stuck in 2008?"

> 🔊 "After 20 songs, you're still playing the same 5 artists. Fascinating."

### Commands

While the application is running, you can use these commands:
- `status` - Show current status
- `quit` or `exit` - Stop the application

## Configuration

The AI commentary behavior can be adjusted in `MusicMonitor.cs`:
- `_commentThrottle` - Minimum time between comments (default: 2 minutes)
- Comment probability in `ShouldGenerateComment()` method
- Comment prompts in `GenerateCommentAboutTrackAsync()` method

## How It Works

1. **Bluetooth Setup**: Configures the Pi as a Bluetooth A2DP sink device
2. **Device Monitoring**: Uses BlueZ D-Bus interface to monitor connected devices
3. **Music Tracking**: Monitors music playback and track information via playerctl and BlueZ
4. **AI Commentary**: Sends track information to OpenAI GPT for witty commentary generation
5. **Audio Routing**: Routes Bluetooth audio to the Pi's audio output

## Architecture

- **BluetoothInterfaces.cs**: D-Bus interface definitions for BlueZ
- **MusicMonitor.cs**: Core service handling Bluetooth monitoring and AI commentary
- **Program.cs**: Main application entry point and user interface

## Troubleshooting

### Bluetooth Issues
```bash
# Restart Bluetooth services
sudo systemctl restart bluetooth
sudo systemctl restart bluealsa

# Check Bluetooth status
sudo systemctl status bluetooth
hciconfig
```

### Audio Troubleshooting
```bash
# Restart Bluetooth services
sudo systemctl restart bluetooth
sudo systemctl restart bluealsa
sudo systemctl restart bluealsa-aplay

# Check Bluetooth status
sudo systemctl status bluetooth
hciconfig

# Fix audio routing specifically
sudo ./run-on-pi.sh --fix-audio

# List audio devices
aplay -l

# Test audio output
speaker-test -t wav

# Check PulseAudio (if using)
pulseaudio --check -v

# Check BlueALSA devices
bluealsa-aplay -l
```

### D-Bus Issues
```bash
# Check if BlueZ is running
systemctl status bluetooth

# Test D-Bus connection
dbus-send --system --print-reply --dest=org.bluez / org.freedesktop.DBus.ObjectManager.GetManagedObjects
```

### Common Solutions
- Ensure your user is in the `bluetooth` group: `sudo usermod -a -G bluetooth $USER`
- Reboot after first setup: `sudo reboot`
- Check that the Pi's audio output is not muted: `alsamixer`
- Verify internet connection for AI features

## Development

### Adding New Commentary Types

To add new types of commentary, modify the `GenerateCommentAboutTrackAsync` method in `MusicMonitor.cs`:

```csharp
private async Task GenerateCommentAboutTrackAsync(string trackInfo)
{
    var prompts = new[]
    {
        // Add your new prompt templates here
        $"Your custom prompt about '{trackInfo}'"
    };
    
    var prompt = prompts[_random.Next(prompts.Length)];
    await GenerateCommentAsync(prompt);
}
```

### Extending Bluetooth Functionality

The `BluetoothInterfaces.cs` file contains D-Bus interface definitions. You can extend these to access additional BlueZ features.

## Auto-Start on Boot

To make your Pi automatically start MeanSpeaker when it boots up (like a commercial speaker):

### 1. Set Up Auto-Start
```bash
# Run the auto-start setup script
chmod +x setup-autostart.sh
./setup-autostart.sh
```

This will:
- Create a systemd service
- Enable auto-start on boot
- Add user to required groups
- Create control commands

### 2. Control the Service
```bash
# Start MeanSpeaker
meanspeaker start

# Stop MeanSpeaker
meanspeaker stop

# Restart MeanSpeaker
meanspeaker restart

# Check status
meanspeaker status

# View live logs
meanspeaker logs

# Disable auto-start
meanspeaker disable
```

### 3. Interactive Control Panel
```bash
# Run the interactive control script
./meanspeaker-control.sh
```

### 4. Test Auto-Start
```bash
# Reboot to test
sudo reboot
```

After reboot, MeanSpeaker should automatically start and be ready for Bluetooth connections!

## License

This project is open source. Please ensure you comply with OpenAI's API terms of service when using the AI commentary features.

## Credits

- Uses BlueZ for Bluetooth functionality
- OpenAI GPT for AI commentary generation  
- Tmds.DBus for D-Bus communication
- playerctl for media control fallback
