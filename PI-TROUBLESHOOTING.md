# Troubleshooting Guide for Bluetooth Speaker on Raspberry Pi

## Issue: Duplicate Assembly Attributes Error

If you see errors like:
```
error CS0579: Duplicate 'global::System.Runtime.Versioning.TargetFrameworkAttribute' attribute
```

### Solution:

1. **Clean everything completely:**
   ```bash
   cd ~/MeanSpeaker
   dotnet clean
   rm -rf obj/
   rm -rf bin/
   ```

2. **Run the provided script (recommended):**
   ```bash
   chmod +x run-on-pi.sh
   ./run-on-pi.sh
   ```
   
   This script will:
   - Automatically run system setup on first run
   - Clean and build the project
   - Run the application
   - Optionally set up auto-start service

3. **Or manually step by step:**
   ```bash
   dotnet restore
   dotnet build
   dotnet run
   ```

## Common Issues and Solutions:

### 1. Build Errors
- Always clean before building: `dotnet clean`
- Delete obj/ and bin/ directories completely
- Restore packages: `dotnet restore`

### 2. Missing Dependencies
Make sure you have:
- .NET 8 SDK installed
- BlueZ and Bluetooth tools: `sudo apt-get install bluez bluetooth bluez-tools`
- Audio tools: `sudo apt-get install playerctl pulseaudio-module-bluetooth`

### 3. Permission Issues
- Run with sudo if needed for Bluetooth operations
- Make sure your user is in the bluetooth group: `sudo usermod -a -G bluetooth $USER`

### 4. API Key Issues
- Set environment variable: `export OPENAI_API_KEY="your-key-here"`
- Or add to ~/.bashrc for permanent setting

### 5. Application Shows "Hello World"
This was caused by a duplicate project structure. Make sure you're in the correct directory and there's only one Program.cs file.

## Expected Output:
When running correctly, you should see:
```
🎵 Snarky Bluetooth Speaker Starting Up...
Speech: Enabled
Voice: en+f3
Initializing Bluetooth Speaker...
...
```

NOT "Hello, World!"
