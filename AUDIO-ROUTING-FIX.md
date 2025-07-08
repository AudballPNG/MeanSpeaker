# Audio Routing Fix for Bluetooth Speaker

## The Problem
You can connect to the Bluetooth speaker, but:
- ❌ Audio still plays from your phone, not the speaker
- ❌ The app doesn't detect that you've connected
- ❌ Music monitoring doesn't work

## Root Cause
The issue is that **BlueALSA receives the audio stream but doesn't route it to speakers**. The missing component is `bluealsa-aplay`, which routes audio from BlueALSA to your system's speakers.

## The Solution

### Step 1: Install and Configure Audio Routing
Run this updated setup script that includes audio routing:

```bash
sudo ./setup-raspberry-pi.sh
```

Or run the updated application (it will auto-configure):
```bash
sudo dotnet run
```

### Step 2: Manual Audio Routing Setup (If Needed)
If the automatic setup doesn't work, run these commands manually:

```bash
# Stop any existing audio services
sudo systemctl stop bluealsa-aplay
sudo systemctl stop bluealsa

# Create the audio routing service
sudo tee /etc/systemd/system/bluealsa-aplay.service > /dev/null << 'EOF'
[Unit]
Description=BlueALSA audio routing service
After=bluealsa.service sound.target
Requires=bluealsa.service

[Service]
Type=simple
ExecStart=/usr/bin/bluealsa-aplay --pcm-buffer-time=250000 00:00:00:00:00:00
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

# Enable and start both services
sudo systemctl daemon-reload
sudo systemctl enable bluealsa
sudo systemctl start bluealsa
sudo systemctl enable bluealsa-aplay
sudo systemctl start bluealsa-aplay

# Set audio levels
amixer sset Master,0 90%
amixer sset PCM,0 90%
```

### Step 3: Test Audio Routing
```bash
# Check if services are running
sudo systemctl status bluealsa
sudo systemctl status bluealsa-aplay

# Check audio devices
aplay -l

# Monitor BlueALSA connections
bluealsa-aplay --list-devices
```

### Step 4: Connect and Test
1. **Connect your phone** to "The Little Shit" via Bluetooth
2. **Play music** - it should now come through the Pi's speakers
3. **Check the app** - it should now detect the connection and start monitoring

## Troubleshooting

### If audio still doesn't work:
```bash
# Restart audio services
sudo systemctl restart bluealsa
sudo systemctl restart bluealsa-aplay

# Check for errors
sudo journalctl -u bluealsa -f
sudo journalctl -u bluealsa-aplay -f
```

### If the app doesn't detect connections:
```bash
# Check Bluetooth status
bluetoothctl show
bluetoothctl devices

# Restart the app
sudo dotnet run
```

### Force audio routing for specific device:
```bash
# Replace XX:XX:XX:XX:XX:XX with your phone's MAC address
sudo systemctl stop bluealsa-aplay
bluealsa-aplay --pcm-buffer-time=250000 XX:XX:XX:XX:XX:XX &
```

## What's Fixed

### Before:
- ✅ BlueALSA receives audio from phone
- ❌ Audio stays in BlueALSA, doesn't reach speakers
- ❌ App can't detect music because audio isn't flowing

### After:
- ✅ BlueALSA receives audio from phone
- ✅ **bluealsa-aplay routes audio to speakers**
- ✅ App detects music and generates commentary
- ✅ Audio flows: Phone → BlueALSA → bluealsa-aplay → Speakers

## Key Components Added

1. **bluealsa-aplay service** - Routes audio from BlueALSA to ALSA
2. **Audio routing monitoring** - Ensures routing stays active
3. **Automatic service restart** - Restarts routing when devices connect
4. **Audio level management** - Sets proper volume levels
5. **Connection notifications** - Confirms audio routing is working

The app now automatically ensures audio routing works when devices connect!
