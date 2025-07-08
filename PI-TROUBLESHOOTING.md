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
```bash
# Check status
sudo systemctl status meanspeaker

# View logs
sudo journalctl -u meanspeaker -f

# Restart service
sudo systemctl restart meanspeaker
```

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
