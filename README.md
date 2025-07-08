# Snarky Bluetooth Speaker

A Raspberry Pi Bluetooth speaker that not only plays your music but also provides snarky AI commentary about your music choices using OpenAI's GPT.

## Features

- **Bluetooth A2DP Audio Sink**: Receives audio from phones, tablets, and other Bluetooth devices
- **AI Music Commentary**: Uses OpenAI GPT to generate witty, sarcastic comments about your music
- **Text-to-Speech**: Actually speaks the commentary out loud using espeak
- **Voice Options**: Multiple voice options for different personalities
- **Automatic Setup**: Installs and configures all necessary Bluetooth components
- **Cross-Device Compatibility**: Works with iOS, Android, and other Bluetooth audio sources
- **Real-time Monitoring**: Tracks music playback, track changes, and device connections

## Requirements

### Hardware
- Raspberry Pi (3B+ or newer recommended)
- USB speakers or 3.5mm audio output
- Bluetooth adapter (built-in on most Pi models)
- SD card with Raspberry Pi OS

### Software
- .NET 8 SDK
- OpenAI API key
- Internet connection for AI commentary

## Installation

### 1. Prepare Raspberry Pi

```bash
# Update your Pi
sudo apt update && sudo apt upgrade -y

# Install .NET 8
wget https://dot.net/v1/dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 8.0
echo 'export PATH=$PATH:$HOME/.dotnet' >> ~/.bashrc
source ~/.bashrc
```

### 2. Clone and Build

```bash
git clone <your-repo-url>
cd BluetoothSpeaker
dotnet build
```

### 3. Set Up OpenAI API Key

Option 1 - Environment Variable:
```bash
export OPENAI_API_KEY="your-api-key-here"
echo 'export OPENAI_API_KEY="your-api-key-here"' >> ~/.bashrc
```

Option 2 - The application will prompt you for the key when you run it.

### 4. Run the Application

```bash
sudo dotnet run
```

Note: `sudo` is required for Bluetooth system configuration on first run.

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

### Audio Issues
```bash
# List audio devices
aplay -l

# Test audio output
speaker-test -t wav

# Check PulseAudio
pulseaudio --check -v
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

## License

This project is open source. Please ensure you comply with OpenAI's API terms of service when using the AI commentary features.

## Credits

- Uses BlueZ for Bluetooth functionality
- OpenAI GPT for AI commentary generation  
- Tmds.DBus for D-Bus communication
- playerctl for media control fallback
