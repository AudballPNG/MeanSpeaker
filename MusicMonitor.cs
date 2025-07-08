using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus;

namespace BluetoothSpeaker
{
    // Session memory for each connected device
    public class DeviceSession
    {
        public string DeviceName { get; set; } = "";
        public string DeviceAddress { get; set; } = "";
        public DateTime ConnectedAt { get; set; } = DateTime.Now;
        public List<string> PlayedTracks { get; set; } = new();
        public List<string> GeneratedComments { get; set; } = new();
        public Dictionary<string, int> TrackPlayCount { get; set; } = new();
        public string CurrentTrack { get; set; } = "";
        public string PreviousTrack { get; set; } = "";
        public DateTime LastCommentTime { get; set; } = DateTime.MinValue;
        public bool IsPlaying { get; set; } = false;
        public int TotalTracksPlayed { get; set; } = 0;
        
        public void AddTrack(string track)
        {
            if (string.IsNullOrEmpty(track)) return;
            
            PreviousTrack = CurrentTrack;
            CurrentTrack = track;
            
            if (!PlayedTracks.Contains(track))
            {
                PlayedTracks.Add(track);
            }
            
            TrackPlayCount[track] = TrackPlayCount.GetValueOrDefault(track, 0) + 1;
            TotalTracksPlayed++;
        }
        
        public void AddComment(string comment)
        {
            GeneratedComments.Add($"[{DateTime.Now:HH:mm:ss}] {comment}");
        }
        
        public bool HasPlayedBefore(string track)
        {
            return TrackPlayCount.ContainsKey(track);
        }
        
        public int GetPlayCount(string track)
        {
            return TrackPlayCount.GetValueOrDefault(track, 0);
        }
        
        public TimeSpan SessionDuration => DateTime.Now - ConnectedAt;
    }

    public class MusicMonitor : IDisposable
    {
        private readonly string _openAiApiKey;
        private readonly HttpClient _httpClient;
        private readonly Random _random;
        
        // Text-to-speech settings
        private readonly bool _enableSpeech;
        private readonly string _ttsVoice;
        
        // Bluetooth monitoring
        private IObjectManager? _objectManager;
        private IAdapter1? _adapter;
        private ObjectPath _adapterPath;
        private Dictionary<string, (IDevice1 Device, ObjectPath Path, IMediaPlayer1? Player, ObjectPath? PlayerPath, IDisposable? StatusWatcher)> _activeDevices = new();
        
        // Session-based memory for each device
        private Dictionary<string, DeviceSession> _deviceSessions = new();
        
        // Global state tracking
        private readonly TimeSpan _commentThrottle = TimeSpan.FromMinutes(1); // Reduced throttle for better session experience
        
        // Setup tracking
        private readonly string _setupMarkerFile = "/etc/bluetooth-speaker-setup-complete";
        private readonly string _tempSetupScriptPath = "/tmp/bluetooth-speaker-setup.sh";
        
        private CancellationTokenSource? _monitoringCancellation;
        private bool _disposed = false;

        public MusicMonitor(string openAiApiKey, bool enableSpeech = true, string ttsVoice = "en+f3")
        {
            _openAiApiKey = openAiApiKey ?? throw new ArgumentNullException(nameof(openAiApiKey));
            _httpClient = new HttpClient();
            _random = new Random();
            _enableSpeech = enableSpeech;
            _ttsVoice = ttsVoice;
        }

        public async Task InitializeAsync()
        {
            Console.WriteLine("Initializing Bluetooth Speaker...");

            // Check if we're on Linux (required for BlueZ)
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Console.WriteLine("Warning: This application is designed for Linux with BlueZ. Some features may not work on other platforms.");
                return;
            }

            // Check if setup is needed
            if (await CheckIfSetupNeeded())
            {
                Console.WriteLine("Setting up Bluetooth audio system...");
                await RunSetupAsync();
            }

            // Initialize D-Bus connection with retry logic
            await InitializeDBusAsync();

            // Verify audio setup
            await VerifyAudioSetupAsync();
            
            // Debug media player connectivity
            await DebugMediaPlayerConnectivityAsync();

            Console.WriteLine("Bluetooth Speaker initialized. Ready to receive audio and insult your music taste!");
        }
        
        private async Task DebugMediaPlayerConnectivityAsync()
        {
            try
            {
                Console.WriteLine("🔍 Debugging media player connectivity...");
                
                // Check D-Bus system connection
                var objects = await _objectManager!.GetManagedObjectsAsync();
                Console.WriteLine($"Found {objects.Count} D-Bus objects");
                
                // Look for media players
                int mediaPlayerCount = 0;
                foreach (var obj in objects)
                {
                    if (obj.Value.ContainsKey("org.bluez.MediaPlayer1"))
                    {
                        mediaPlayerCount++;
                        Console.WriteLine($"Found MediaPlayer: {obj.Key}");
                    }
                }
                
                if (mediaPlayerCount == 0)
                {
                    Console.WriteLine("⚠️  No MediaPlayer interfaces found in BlueZ");
                    Console.WriteLine("   This may indicate that devices need to be connected first");
                    Console.WriteLine("   or that BlueALSA media player support is not enabled");
                }
                else
                {
                    Console.WriteLine($"✅ Found {mediaPlayerCount} MediaPlayer interfaces");
                }
                
                // Test playerctl
                var playerctlOutput = await RunCommandWithOutputAsync("playerctl", "-l");
                if (string.IsNullOrEmpty(playerctlOutput.Trim()))
                {
                    Console.WriteLine("⚠️  No playerctl media players detected");
                    Console.WriteLine("   This is normal when no devices are connected");
                }
                else
                {
                    Console.WriteLine($"✅ Playerctl detected players: {playerctlOutput.Trim()}");
                }
                
                // Check BlueALSA version and capabilities
                var bluelsaVersion = await RunCommandWithOutputAsync("bluealsa", "--version");
                Console.WriteLine($"BlueALSA version: {bluelsaVersion.Trim()}");
                
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning during media player debug: {ex.Message}");
            }
        }
        
        private async Task InitializeDBusAsync()
        {
            try
            {
                Console.WriteLine("Connecting to BlueZ via D-Bus...");
                
                _objectManager = Connection.System.CreateProxy<IObjectManager>("org.bluez", "/");
                
                // Find Bluetooth adapter
                var objects = await _objectManager.GetManagedObjectsAsync();
                foreach (var obj in objects)
                {
                    if (obj.Value.ContainsKey("org.bluez.Adapter1"))
                    {
                        _adapterPath = obj.Key;
                        _adapter = Connection.System.CreateProxy<IAdapter1>("org.bluez", _adapterPath);
                        Console.WriteLine($"Found Bluetooth adapter: {_adapterPath}");
                        break;
                    }
                }

                if (_adapter == null)
                {
                    throw new InvalidOperationException("No Bluetooth adapter found!");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize D-Bus: {ex.Message}");
                throw;
            }
        }

        private async Task ConfigureBluetoothAsync()
        {
            try
            {
                // Power on and make discoverable
                await _adapter!.SetAsync("Powered", true);
                await _adapter.SetAsync("Alias", "The Little Shit");
                await _adapter.SetAsync("Discoverable", true);
                await _adapter.SetAsync("DiscoverableTimeout", (uint)0);
                await _adapter.SetAsync("Pairable", true);
                await _adapter.SetAsync("PairableTimeout", (uint)0);

                Console.WriteLine("Bluetooth adapter configured and discoverable as 'The Little Shit'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error configuring Bluetooth: {ex.Message}");
                // Fall back to command line
                await RunCommandAsync("bluetoothctl", "power on");
                await RunCommandAsync("bluetoothctl", "system-alias 'The Little Shit'");
                await RunCommandAsync("bluetoothctl", "discoverable on");
                await RunCommandAsync("bluetoothctl", "pairable on");
            }
        }

        private async Task MonitorDevicesAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var devices = await _objectManager!.GetConnectedDevicesAsync();
                    var currentDeviceAddresses = devices.Select(d => d.Address).ToHashSet();
                    
                    // Check for disconnected devices and clean up their sessions
                    var disconnectedDevices = _activeDevices.Keys.Where(addr => !currentDeviceAddresses.Contains(addr)).ToList();
                    foreach (var address in disconnectedDevices)
                    {
                        if (_deviceSessions.TryGetValue(address, out var session))
                        {
                            Console.WriteLine($"Device {session.DeviceName} disconnected. Session lasted {session.SessionDuration:hh\\:mm\\:ss}");
                            Console.WriteLine($"  Total tracks played: {session.TotalTracksPlayed}");
                            Console.WriteLine($"  Comments generated: {session.GeneratedComments.Count}");
                            
                            // Clean up session memory
                            _deviceSessions.Remove(address);
                        }
                        
                        // Clean up device watcher
                        if (_activeDevices.TryGetValue(address, out var deviceEntry))
                        {
                            deviceEntry.StatusWatcher?.Dispose();
                            _activeDevices.Remove(address);
                        }
                    }
                    
                    // Handle new device connections
                    foreach (var (device, path, address, name) in devices)
                    {
                        if (!_activeDevices.ContainsKey(address))
                        {
                            Console.WriteLine($"New device connected: {name} ({address})");
                            
                            // Create new session for this device
                            var session = new DeviceSession
                            {
                                DeviceName = name,
                                DeviceAddress = address,
                                ConnectedAt = DateTime.Now
                            };
                            _deviceSessions[address] = session;
                            
                            // Set device as trusted
                            await device.SetAsync("Trusted", true);
                            
                            // Ensure audio routing is working for this device
                            await EnsureAudioRoutingAsync(address);
                            
                            // Find media player
                            var playerInfo = await _objectManager.FindMediaPlayerForDeviceAsync(path);
                            
                            IDisposable? watcher = null;
                            
                            if (playerInfo.HasValue)
                            {
                                // Set up media player watcher
                                watcher = await playerInfo.Value.Player.WatchPropertiesAsync(changes =>
                                {
                                    _ = Task.Run(() => HandleMediaPlayerChangesAsync(address, changes));
                                });
                            
                            Console.WriteLine($"Media player found for {name}");
                        }
                            
                            var deviceEntry = (device, path, playerInfo?.Player, playerInfo?.Path, watcher);
                            _activeDevices[address] = deviceEntry;
                            
                            // Welcome message
                            await GenerateCommentForDeviceAsync(address, $"Oh great, {name} just connected. Let me guess, you're about to blast some questionable music choices through me?");
                        }
                    }
                    
                    await Task.Delay(5000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error monitoring devices: {ex.Message}");
                    await Task.Delay(10000, cancellationToken);
                }
            }
        }

        private async Task MonitorMusicAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("🎵 Starting enhanced music monitoring...");
            
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Primary method: Use playerctl for music monitoring
                    var playerctlStatus = await RunCommandWithOutputAsync("playerctl", "status");
                    var playerctlMetadata = await RunCommandWithOutputAsync("playerctl", "metadata");
                    
                    // Fallback method: Monitor BlueALSA directly
                    var bluelsaInfo = await RunCommandWithOutputAsync("bluealsa-cli", "list-devices");
                    
                    // Enhanced debugging
                    if (!string.IsNullOrEmpty(playerctlMetadata))
                    {
                        Console.WriteLine($"[DEBUG] Playerctl metadata detected: {playerctlMetadata.Length} chars");
                        
                        var trackInfo = ParseTrackInfo(playerctlMetadata);
                        
                        if (!string.IsNullOrEmpty(trackInfo))
                        {
                            Console.WriteLine($"[DEBUG] Parsed track: {trackInfo}");
                            
                            // Find which device is playing - enhanced logic
                            var activeSession = FindActiveDeviceSession();
                            if (activeSession != null)
                            {
                                if (trackInfo != activeSession.CurrentTrack)
                                {
                                    Console.WriteLine($"[{activeSession.DeviceName}] Track changed: {trackInfo}");
                                    activeSession.AddTrack(trackInfo);
                                    
                                    // Generate comment with session context
                                    if (ShouldGenerateCommentForDevice(activeSession.DeviceAddress))
                                    {
                                        await GenerateCommentAboutTrackAsync(activeSession.DeviceAddress, trackInfo);
                                    }
                                }
                            }
                            else
                            {
                                Console.WriteLine("[DEBUG] No active device session found for track");
                            }
                        }
                        else
                        {
                            Console.WriteLine("[DEBUG] Could not parse track info from metadata");
                        }
                    }
                    else
                    {
                        Console.WriteLine("[DEBUG] No playerctl metadata available");
                        
                        // Try alternative method: check MPRIS directly
                        var mprisPlayers = await RunCommandWithOutputAsync("dbus-send", "--session --print-reply --dest=org.freedesktop.DBus /org/freedesktop/DBus org.freedesktop.DBus.ListNames");
                        if (mprisPlayers.Contains("org.mpris.MediaPlayer2"))
                        {
                            Console.WriteLine("[DEBUG] MPRIS player detected via D-Bus");
                        }
                    }
                    
                    // Check playback state for all sessions
                    bool isPlaying = playerctlStatus.Contains("Playing");
                    bool isDetectedPlaying = false;
                    
                    foreach (var session in _deviceSessions.Values)
                    {
                        bool wasPlaying = session.IsPlaying;
                        session.IsPlaying = isPlaying;
                        
                        if (isPlaying)
                        {
                            isDetectedPlaying = true;
                            if (!wasPlaying)
                            {
                                Console.WriteLine($"[{session.DeviceName}] Music started playing");
                                
                                // Welcome back message
                                if (_random.Next(0, 3) == 0) // 33% chance
                                {
                                    await GenerateCommentForDeviceAsync(session.DeviceAddress, "Oh, you're back for more musical punishment? Let's see what questionable choices you've made this time.");
                                }
                            }
                        }
                        else if (!isPlaying && wasPlaying)
                        {
                            Console.WriteLine($"[{session.DeviceName}] Music paused/stopped");
                            
                            if (_random.Next(0, 4) == 0) // 25% chance
                            {
                                await GenerateCommentForDeviceAsync(session.DeviceAddress, "What, couldn't handle the truth about your music taste? Good choice pausing that.");
                            }
                        }
                    }
                    
                    // Periodic status update
                    if (!isDetectedPlaying && _deviceSessions.Any())
                    {
                        Console.WriteLine($"[DEBUG] No active playback detected. Connected devices: {_deviceSessions.Count}");
                    }
                    
                    await Task.Delay(3000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error monitoring music: {ex.Message}");
                    await Task.Delay(5000, cancellationToken);
                }
            }
        }
        
        private DeviceSession? FindActiveDeviceSession()
        {
            // Enhanced logic to find the active device
            // For now, return the first connected device
            // This could be enhanced to detect which device is actually playing
            return _deviceSessions.Values.FirstOrDefault();
        }

        private async Task HandleMediaPlayerChangesAsync(string deviceAddress, PropertyChanges changes)
        {
            try
            {
                if (!_deviceSessions.TryGetValue(deviceAddress, out var session))
                    return;

                foreach (var change in changes.Changed)
                {
                    if (change.Key == "Track")
                    {
                        if (change.Value is IDictionary<string, object> trackDict)
                        {
                            var trackInfo = FormatTrackInfo(trackDict);
                            
                            if (!string.IsNullOrEmpty(trackInfo) && trackInfo != session.CurrentTrack)
                            {
                                session.AddTrack(trackInfo);
                                
                                Console.WriteLine($"[{session.DeviceName}] Track changed: {trackInfo}");
                                
                                if (ShouldGenerateCommentForDevice(deviceAddress))
                                {
                                    await GenerateCommentAboutTrackAsync(deviceAddress, trackInfo);
                                }
                            }
                        }
                    }
                    else if (change.Key == "Status")
                    {
                        var status = change.Value?.ToString() ?? "";
                        Console.WriteLine($"[{session.DeviceName}] Playback status: {status}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling media player changes: {ex.Message}");
            }
        }

        private bool ShouldGenerateCommentForDevice(string deviceAddress)
        {
            if (!_deviceSessions.TryGetValue(deviceAddress, out var session))
                return false;
            
            // Comment throttling and randomness per device session
            return DateTime.Now - session.LastCommentTime > _commentThrottle && _random.Next(0, 3) == 0; // 33% chance
        }

        private async Task GenerateCommentAboutTrackAsync(string deviceAddress, string trackInfo)
        {
            if (!_deviceSessions.TryGetValue(deviceAddress, out var session))
                return;

            var playCount = session.GetPlayCount(trackInfo);
            var hasPlayedBefore = session.HasPlayedBefore(trackInfo);
            
            var prompts = new List<string>();
            
            if (playCount > 1)
            {
                prompts.Add($"This is the {GetOrdinal(playCount)} time you've played '{trackInfo}' this session. Make a sarcastic comment about their repetitive listening habits.");
                prompts.Add($"They're playing '{trackInfo}' again ({playCount} times now). Roast them for being stuck on repeat.");
            }
            else if (hasPlayedBefore)
            {
                prompts.Add($"They're playing '{trackInfo}' again. Comment sarcastically about their predictable music choices.");
            }
            else
            {
                prompts.Add($"Generate a short, snarky, humorous comment about someone playing '{trackInfo}' for the first time this session. Be witty but not offensive.");
                prompts.Add($"Make a sarcastic comment about '{trackInfo}' being played. Keep it clever and brief.");
            }
            
            // Add context about session length and variety
            if (session.PlayedTracks.Count > 10)
            {
                prompts.Add($"After {session.PlayedTracks.Count} songs in this session, now they're playing '{trackInfo}'. Comment on their musical journey.");
            }
            
            var prompt = prompts[_random.Next(prompts.Count)];
            await GenerateCommentForDeviceAsync(deviceAddress, prompt);
        }

        private async Task GenerateCommentForDeviceAsync(string deviceAddress, string prompt)
        {
            if (!_deviceSessions.TryGetValue(deviceAddress, out var session))
                return;

            try
            {
                session.LastCommentTime = DateTime.Now;
                
                var requestBody = new
                {
                    model = "gpt-3.5-turbo",
                    messages = new[]
                    {
                        new { role = "system", content = "You are a snarky Bluetooth speaker that makes witty comments about music. Keep responses under 25 words and be clever but not offensive." },
                        new { role = "user", content = prompt }
                    },
                    max_tokens = 100,
                    temperature = 0.9
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_openAiApiKey}");

                var response = await _httpClient.PostAsync("https://api.openai.com/v1/chat/completions", content);
                
                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync();
                    var jsonDoc = JsonDocument.Parse(responseJson);
                    
                    if (jsonDoc.RootElement.TryGetProperty("choices", out var choices) &&
                        choices.GetArrayLength() > 0 &&
                        choices[0].TryGetProperty("message", out var message) &&
                        message.TryGetProperty("content", out var messageContent))
                    {
                        var comment = messageContent.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(comment))
                        {
                            session.AddComment(comment);
                            Console.WriteLine($"\n🎵 [{session.DeviceName}] SPEAKER SAYS: {comment}\n");
                            
                            // Speak the comment out loud
                            if (_enableSpeech)
                            {
                                await SpeakAsync(comment);
                            }
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"Failed to generate comment: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error generating comment: {ex.Message}");
            }
        }

        private async Task SpeakAsync(string text)
        {
            try
            {
                // Clean up text for better speech synthesis
                var cleanText = CleanTextForSpeech(text);
                
                // Use espeak for text-to-speech on Linux
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // Use espeak with specified voice and speed
                    await RunCommandAsync("espeak", $"-v {_ttsVoice} -s 160 -a 200 \"{cleanText}\"");
                }
                else
                {
                    // Fallback for development on Windows - use built-in SAPI
                    await RunCommandAsync("powershell", $"-Command \"Add-Type -AssemblyName System.Speech; $speak = New-Object System.Speech.Synthesis.SpeechSynthesizer; $speak.Speak('{cleanText}')\"");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error speaking text: {ex.Message}");
            }
        }

        private string CleanTextForSpeech(string text)
        {
            // Remove or replace characters that might cause issues with TTS
            return text
                .Replace("\"", "")
                .Replace("'", "")
                .Replace("`", "")
                .Replace("&", "and")
                .Replace("<", "less than")
                .Replace(">", "greater than")
                .Trim();
        }

        private string GetOrdinal(int number)
        {
            if (number <= 0) return number.ToString();
            
            switch (number % 100)
            {
                case 11:
                case 12:
                case 13:
                    return number + "th";
            }
            
            switch (number % 10)
            {
                case 1:
                    return number + "st";
                case 2:
                    return number + "nd";
                case 3:
                    return number + "rd";
                default:
                    return number + "th";
            }
        }

        private string ParseTrackInfo(string metadata)
        {
            try
            {
                var lines = metadata.Split('\n');
                string artist = "";
                string title = "";
                
                foreach (var line in lines)
                {
                    if (line.Contains("xesam:artist"))
                    {
                        var parts = line.Split(new[] { "xesam:artist" }, StringSplitOptions.None);
                        if (parts.Length > 1)
                        {
                            artist = parts[1].Trim().Trim('"', ' ');
                        }
                    }
                    else if (line.Contains("xesam:title"))
                    {
                        var parts = line.Split(new[] { "xesam:title" }, StringSplitOptions.None);
                        if (parts.Length > 1)
                        {
                            title = parts[1].Trim().Trim('"', ' ');
                        }
                    }
                }
                
                if (!string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(title))
                {
                    return $"{artist} - {title}";
                }
                else if (!string.IsNullOrEmpty(title))
                {
                    return title;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing track info: {ex.Message}");
            }
            
            return "";
        }

        private string FormatTrackInfo(IDictionary<string, object> track)
        {
            try
            {
                string artist = "";
                string title = "";
                
                if (track.TryGetValue("Artist", out var artistObj))
                    artist = artistObj?.ToString() ?? "";
                    
                if (track.TryGetValue("Title", out var titleObj))
                    title = titleObj?.ToString() ?? "";
                
                if (!string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(title))
                    return $"{artist} - {title}";
                else if (!string.IsNullOrEmpty(title))
                    return title;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error formatting track info: {ex.Message}");
            }
            
            return "";
        }

        private async Task<bool> CheckIfSetupNeeded()
        {
            return !File.Exists(_setupMarkerFile);
        }

        private async Task RunSetupAsync()
        {
            try
            {
                await CreateSetupScriptAsync();
                
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "sudo",
                        Arguments = $"bash {_tempSetupScriptPath}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                
                process.OutputDataReceived += (sender, e) => {
                    if (!string.IsNullOrEmpty(e.Data))
                        Console.WriteLine($"Setup: {e.Data}");
                };
                
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                
                await process.WaitForExitAsync();
                
                if (process.ExitCode == 0)
                {
                    Console.WriteLine("Setup completed successfully!");
                }
                else
                {
                    Console.WriteLine($"Setup failed with exit code: {process.ExitCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during setup: {ex.Message}");
            }
            finally
            {
                if (File.Exists(_tempSetupScriptPath))
                    File.Delete(_tempSetupScriptPath);
            }
        }

        private async Task CreateSetupScriptAsync()
        {
            var script = @"#!/bin/bash

# Bluetooth Speaker Setup Script
echo ""Setting up Bluetooth audio for Raspberry Pi...""

# Update packages
apt-get update

# Install Bluetooth and audio packages
apt-get install -y bluez bluealsa bluetooth bluez-tools
apt-get install -y playerctl pulseaudio-module-bluetooth pulseaudio alsa-utils

# Install text-to-speech packages
apt-get install -y espeak espeak-data mbrola mbrola-voices

# Install additional voice packages for better quality
apt-get install -y festival festival-dev speech-dispatcher

# Enable services
systemctl enable bluetooth
systemctl start bluetooth

# Configure Bluetooth for A2DP sink
cat > /etc/bluetooth/main.conf << EOF
[General]
Class = 0x41C
DiscoverableTimeout = 0
PairableTimeout = 0

[Policy]
AutoEnable=true
EOF

# Create BlueALSA service
cat > /etc/systemd/system/bluealsa.service << EOF
[Unit]
Description=BlueALSA service
After=bluetooth.service
Requires=bluetooth.service

[Service]
Type=simple
ExecStart=/usr/bin/bluealsa -p a2dp-sink
Restart=on-failure

[Install]
WantedBy=multi-user.target
EOF

# Create BlueALSA audio routing service
cat > /etc/systemd/system/bluealsa-aplay.service << EOF
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

# Enable and start BlueALSA services
systemctl daemon-reload
systemctl enable bluealsa.service
systemctl start bluealsa.service
systemctl enable bluealsa-aplay.service
systemctl start bluealsa-aplay.service

# Configure audio for better Bluetooth performance
cat > /etc/asound.conf << EOF
defaults.bluealsa.interface ""hci0""
defaults.bluealsa.profile ""a2dp""
defaults.bluealsa.delay 20000
defaults.bluealsa.battery ""yes""
EOF

# Set up audio mixer levels
amixer sset PCM,0 100%
amixer sset Master,0 100%

# Mark setup as complete
touch /etc/bluetooth-speaker-setup-complete

echo ""Bluetooth speaker setup complete!""
echo ""Audio routing configured - BlueALSA will now route audio to speakers""
";

            await File.WriteAllTextAsync(_tempSetupScriptPath, script);
        }

        private async Task<string> RunCommandWithOutputAsync(string command, string arguments)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = command,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                
                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                return output;
            }
            catch
            {
                return "";
            }
        }

        private async Task RunCommandAsync(string command, string arguments)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = command,
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                
                process.Start();
                await process.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error running command '{command} {arguments}': {ex.Message}");
            }
        }

        private async Task EnsureAudioRoutingAsync(string deviceAddress)
        {
            try
            {
                Console.WriteLine($"Ensuring audio routing for device {deviceAddress}...");
                
                // Restart bluealsa-aplay service to pick up new device
                await RunCommandAsync("systemctl", "restart bluealsa-aplay");
                
                // Wait a moment for the service to restart
                await Task.Delay(2000);
                
                // Check if audio routing is working
                var bluetoothDevices = await RunCommandWithOutputAsync("bluetoothctl", "info");
                Console.WriteLine("Audio routing configured for connected device");
                
                // Set audio levels for better quality
                await RunCommandAsync("amixer", "sset Master,0 90%");
                await RunCommandAsync("amixer", "sset PCM,0 90%");
                
                // Test audio routing with a brief notification
                if (_enableSpeech)
                {
                    _ = Task.Run(async () => 
                    {
                        await Task.Delay(3000); // Give audio routing time to establish
                        await SpeakAsync("Audio connection established. Your music will now play through the speaker.");
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not ensure audio routing: {ex.Message}");
                // Try fallback method
                await RunCommandAsync("pulseaudio", "--kill");
                await Task.Delay(1000);
                await RunCommandAsync("pulseaudio", "--start");
            }
        }

        private async Task MonitorAudioRoutingAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Check if bluealsa-aplay service is running
                    var serviceStatus = await RunCommandWithOutputAsync("systemctl", "is-active bluealsa-aplay");
                    if (serviceStatus.Trim() != "active")
                    {
                        Console.WriteLine("Warning: bluealsa-aplay service is not running. Attempting to restart...");
                        await RunCommandAsync("systemctl", "restart bluealsa-aplay");
                    }
                    
                    // Check for connected A2DP devices
                    var bluetoothInfo = await RunCommandWithOutputAsync("bluetoothctl", "info");
                    
                    // Wait before next check
                    await Task.Delay(30000, cancellationToken); // Check every 30 seconds
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error monitoring audio routing: {ex.Message}");
                    await Task.Delay(10000, cancellationToken);
                }
            }
        }

        private async Task VerifyAudioSetupAsync()
        {
            try
            {
                Console.WriteLine("Verifying audio setup...");
                
                // Check if bluealsa service is running
                var bluelsaStatus = await RunCommandWithOutputAsync("systemctl", "is-active bluealsa");
                if (bluelsaStatus.Trim() != "active")
                {
                    Console.WriteLine("⚠️  BlueALSA service is not running. Audio may not work.");
                    Console.WriteLine("   Run: sudo systemctl start bluealsa");
                }
                else
                {
                    Console.WriteLine("✅ BlueALSA service is running");
                }
                
                // Check if bluealsa-aplay service is running
                var aplayStatus = await RunCommandWithOutputAsync("systemctl", "is-active bluealsa-aplay");
                if (aplayStatus.Trim() != "active")
                {
                    Console.WriteLine("⚠️  BlueALSA-aplay service is not running. Audio routing may not work.");
                    Console.WriteLine("   This is the most common cause of 'no audio' issues.");
                    Console.WriteLine("   Run: sudo systemctl start bluealsa-aplay");
                }
                else
                {
                    Console.WriteLine("✅ BlueALSA-aplay service is running (audio routing active)");
                }
                
                // Check audio devices
                var audioDevices = await RunCommandWithOutputAsync("aplay", "-l");
                if (string.IsNullOrEmpty(audioDevices))
                {
                    Console.WriteLine("⚠️  No audio devices found. Check your speaker connection.");
                }
                else
                {
                    Console.WriteLine("✅ Audio devices detected");
                }
                
                Console.WriteLine("Audio setup verification complete.\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not verify audio setup: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            StopMonitoring();
            _monitoringCancellation?.Dispose();
            _httpClient?.Dispose();
            
            _disposed = true;
        }
    }
}
