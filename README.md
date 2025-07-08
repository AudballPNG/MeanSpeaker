# Simple Snarky Bluetooth Speaker

A Raspberry Pi Bluetooth speaker that plays your music AND provides snarky AI commentary about your music choices using OpenAI's GPT.

**🚀 Setup once, works forever! Automatically starts on boot.**

## Features

- **🎵 Bluetooth A2DP Audio Sink**: Works like any normal Bluetooth speaker
- **🤖 AI Music Commentary**: Uses OpenAI GPT to generate witty, sarcastic comments about your music
- **🔊 Text-to-Speech**: Actually speaks the commentary out loud using espeak
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

### 1. Install .NET 8 on Raspberry Pi
```bash
wget https://dot.net/v1/dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 8.0
echo 'export PATH=$PATH:$HOME/.dotnet' >> ~/.bashrc
source ~/.bashrc
```

### 2. Clone and Setup
```bash
git clone <your-repo-url>
cd BluetoothSpeaker
chmod +x simple-setup.sh speaker-control.sh
sudo ./simple-setup.sh
```

**The setup now includes auto-start on boot!** After running the setup script, the Bluetooth speaker will automatically start every time the Pi boots.

### 3. Set OpenAI API Key
```bash
# Option 1: Use the setup helper
./speaker-control.sh install-api-key

# Option 2: Manual setup
export OPENAI_API_KEY="your-api-key-here"
echo 'export OPENAI_API_KEY="your-api-key-here"' >> ~/.bashrc
```

### 4. Reboot and Enjoy!
```bash
sudo reboot
```

After reboot, your Pi will automatically:
- Start the Bluetooth speaker service
- Be discoverable as "The Little Shit"
- Accept connections and provide AI commentary

Simply connect your phone and play music!

**Manual control (if needed):**
```bash
# Service management
./speaker-control.sh status    # Check status
./speaker-control.sh stop      # Stop service
./speaker-control.sh start     # Start service
./speaker-control.sh logs      # View logs

# Manual run (for testing)
./speaker-control.sh manual
```

## Service Management

**The speaker service starts automatically on boot after setup!**

Use the included control script for management:

```bash
# Check what's happening
./speaker-control.sh status     # Service status
./speaker-control.sh logs       # View real-time logs

# Control the service
./speaker-control.sh start      # Start service
./speaker-control.sh stop       # Stop service
./speaker-control.sh restart    # Restart service

# Auto-start management
./speaker-control.sh enable     # Enable auto-start (already done by setup)
./speaker-control.sh disable    # Disable auto-start

# Testing and troubleshooting
./speaker-control.sh manual     # Run manually for testing
```

That's it! Your Pi is now discoverable as "The Little Shit".

## Usage

**After setup, your speaker runs automatically on boot!**

1. **Connect your phone** to "The Little Shit" via Bluetooth
2. **Play music** - it works like any normal Bluetooth speaker
3. **Listen to the roasts** - the AI will occasionally comment on your music choices

### Service Control
```bash
./speaker-control.sh status    # Check if running
./speaker-control.sh logs      # Watch live commentary
./speaker-control.sh restart   # Restart if needed
```

### Manual Testing
While running manually (`./speaker-control.sh manual`), you can type:
- `status` - Show connection and service status
- `test` - Force generate a test comment
- `quit` - Stop the application

## Troubleshooting

### Service Not Starting
```bash
# Check service status
./speaker-control.sh status

# Check logs for errors
./speaker-control.sh logs

# Restart services
sudo systemctl restart bluetooth bluealsa bluealsa-aplay
./speaker-control.sh restart
```

### No Audio Playing
```bash
# Check audio services
sudo systemctl status bluealsa bluealsa-aplay

# Restart audio routing
sudo systemctl restart bluealsa-aplay
```

### No Device Detection
```bash
# Check Bluetooth
bluetoothctl devices Connected
bluetoothctl show

# Make sure discoverable
sudo bluetoothctl discoverable on
```

### No Track Detection
```bash
# Check if playerctl works
playerctl status
playerctl metadata

# Check service logs
./speaker-control.sh logs
```

### API Key Issues
```bash
# Reinstall API key
./speaker-control.sh install-api-key

# Check environment
echo $OPENAI_API_KEY
```

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
- **🔊 Text-to-Speech**: espeak with configurable voices

**Total complexity:** ~500 lines of C# vs 1500+ lines in the old version

## Voice Options

```bash
# Different voice personalities
dotnet run --voice en+f3  # Default female
dotnet run --voice en+m3  # Male voice
dotnet run --voice en+f4  # Different female
dotnet run --no-speech    # Text only, no speech
```

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
