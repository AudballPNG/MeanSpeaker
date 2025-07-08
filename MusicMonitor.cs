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

        public MusicMonitor(string openAiApiKey)
        {
            _openAiApiKey = openAiApiKey ?? throw new ArgumentNullException(nameof(openAiApiKey));
            _httpClient = new HttpClient();
            _random = new Random();
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

            // Initialize D-Bus connection
            await InitializeDBusAsync();

            Console.WriteLine("Bluetooth Speaker initialized. Ready to receive audio and insult your music taste!");
        }

        public async Task StartMonitoringAsync()
        {
            if (_disposed) return;

            _monitoringCancellation = new CancellationTokenSource();
            var token = _monitoringCancellation.Token;

            Console.WriteLine("Starting music monitoring...");

            // Enable Bluetooth adapter and make discoverable
            await ConfigureBluetoothAsync();

            // Start monitoring tasks
            _ = Task.Run(() => MonitorDevicesAsync(token), token);
            _ = Task.Run(() => MonitorMusicAsync(token), token);

            Console.WriteLine("Monitoring started. Connect your device to start streaming music!");
        }

        public void StopMonitoring()
        {
            Console.WriteLine("Stopping music monitoring...");
            
            _monitoringCancellation?.Cancel();
            
            // Clean up watchers and display session summaries
            foreach (var (address, deviceEntry) in _activeDevices)
            {
                deviceEntry.StatusWatcher?.Dispose();
                
                if (_deviceSessions.TryGetValue(address, out var session))
                {
                    Console.WriteLine($"\n=== SESSION SUMMARY FOR {session.DeviceName} ===");
                    Console.WriteLine($"Connected: {session.ConnectedAt:yyyy-MM-dd HH:mm:ss}");
                    Console.WriteLine($"Session Duration: {session.SessionDuration:hh\\:mm\\:ss}");
                    Console.WriteLine($"Total Tracks Played: {session.TotalTracksPlayed}");
                    Console.WriteLine($"Unique Tracks: {session.PlayedTracks.Count}");
                    Console.WriteLine($"Comments Generated: {session.GeneratedComments.Count}");
                    
                    if (session.TrackPlayCount.Any())
                    {
                        var mostPlayed = session.TrackPlayCount.OrderByDescending(kvp => kvp.Value).First();
                        Console.WriteLine($"Most Played: {mostPlayed.Key} ({mostPlayed.Value} times)");
                    }
                    
                    Console.WriteLine("=== END SESSION SUMMARY ===\n");
                }
            }
            
            _activeDevices.Clear();
            _deviceSessions.Clear();
            
            Console.WriteLine("Monitoring stopped.");
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
                await _adapter.SetAsync("Discoverable", true);
                await _adapter.SetAsync("DiscoverableTimeout", (uint)0);
                await _adapter.SetAsync("Pairable", true);
                await _adapter.SetAsync("PairableTimeout", (uint)0);

                Console.WriteLine("Bluetooth adapter configured and discoverable");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error configuring Bluetooth: {ex.Message}");
                // Fall back to command line
                await RunCommandAsync("bluetoothctl", "power on");
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
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Use playerctl as fallback for music monitoring
                    var status = await RunCommandWithOutputAsync("playerctl", "status");
                    var metadata = await RunCommandWithOutputAsync("playerctl", "metadata");
                    
                    if (!string.IsNullOrEmpty(metadata))
                    {
                        var trackInfo = ParseTrackInfo(metadata);
                        
                        if (!string.IsNullOrEmpty(trackInfo))
                        {
                            // Find which device is playing (simplified - could be enhanced)
                            var activeSession = _deviceSessions.Values.FirstOrDefault();
                            if (activeSession != null && trackInfo != activeSession.CurrentTrack)
                            {
                                activeSession.AddTrack(trackInfo);
                                
                                Console.WriteLine($"Now playing: {trackInfo}");
                                
                                // Generate comment with session context
                                if (ShouldGenerateCommentForDevice(activeSession.DeviceAddress))
                                {
                                    await GenerateCommentAboutTrackAsync(activeSession.DeviceAddress, trackInfo);
                                }
                            }
                        }
                    }
                    
                    // Check playback state for all sessions
                    bool isPlaying = status.Contains("Playing");
                    foreach (var session in _deviceSessions.Values)
                    {
                        bool wasPlaying = session.IsPlaying;
                        session.IsPlaying = isPlaying;
                        
                        if (isPlaying && !wasPlaying)
                        {
                            Console.WriteLine($"[{session.DeviceName}] Music started playing");
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

# Enable and start BlueALSA
systemctl daemon-reload
systemctl enable bluealsa.service
systemctl start bluealsa.service

# Mark setup as complete
touch /etc/bluetooth-speaker-setup-complete

echo ""Bluetooth speaker setup complete!""
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
