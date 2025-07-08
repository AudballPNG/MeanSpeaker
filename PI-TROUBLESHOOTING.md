# Troubleshooting Guide for Bluetooth Speaker on Raspberry Pi

## One-Stop Solution

**Just run the comprehensive setup script:**
```bash
cd ~/MeanSpeaker
chmod +x run-on-pi.sh
./run-on-pi.sh
```

This single script handles:
- ✅ Complete system setup (first run only)
- ✅ Dependency installation
- ✅ Bluetooth configuration
- ✅ Assembly attributes fix
- ✅ Clean build process
- ✅ Application execution
- ✅ Auto-start service setup

## Auto-Start Behavior:

### First Run:
1. The script sets up everything
2. Runs the application once to test
3. **Asks if you want auto-start enabled**
4. If you say "yes", creates a systemd service
5. **After reboot, the speaker starts automatically!**

### After Reboot:
- **No manual intervention needed** - the service starts automatically
- The Bluetooth speaker will be ready and discoverable
- Just connect your phone and play music
- The AI commentary will work immediately

### Manual Control (if needed):
```bash
# Check if service is running
sudo systemctl status meanspeaker

# View real-time logs
sudo journalctl -u meanspeaker -f

# Manually start/stop/restart
sudo systemctl start meanspeaker
sudo systemctl stop meanspeaker
sudo systemctl restart meanspeaker
```

## Common Issues and Solutions:

### 1. Duplicate Assembly Attributes Error
**The script automatically handles this**, but if you need to fix manually:
```bash
dotnet clean && rm -rf obj/ bin/ && dotnet nuget locals all --clear
find . -name "*.AssemblyAttributes.cs" -delete
dotnet restore --no-cache && dotnet build -p:GenerateAssemblyInfo=false
```

### 2. Missing Dependencies
**The script installs these automatically**, but manual installation:
```bash
sudo apt-get update
sudo apt-get install -y bluetooth bluez bluez-tools pulseaudio pulseaudio-module-bluetooth playerctl espeak
```

### 3. Build Errors
**The script uses multiple fallback strategies**, including:
- Nuclear clean approach
- Assembly info disabled
- Minimal project file fallback

### 4. Service Management
After running the script with auto-start enabled:

**The service runs automatically after reboot - no manual intervention needed!**

But if you need manual control:
```bash
# Check status
sudo systemctl status meanspeaker

# View logs
sudo journalctl -u meanspeaker -f

# Restart service
sudo systemctl restart meanspeaker

# Stop service (if needed)
sudo systemctl stop meanspeaker

# Start service (if stopped)
sudo systemctl start meanspeaker
```

### 5. Typical Workflow:
1. **First time:** Run `./run-on-pi.sh` and choose "yes" for auto-start
2. **Reboot your Pi:** `sudo reboot`
3. **That's it!** The speaker is automatically ready
4. **Connect your phone** to "The Little Shit" 
5. **Play music** and enjoy the AI commentary

## Expected Output:
When running correctly, you should see:
```
🎵 Snarky Bluetooth Speaker Starting Up...
Speech: Enabled
Voice: en+f3
Initializing Bluetooth Speaker...
```

**NOT** "Hello, World!"

## Environment Variables:
The script creates a `.env` file template. Edit it with:
```bash
nano .env
```

Add your OpenAI API key:
```
OPENAI_API_KEY=your-actual-api-key-here
```
