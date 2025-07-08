using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus;

namespace BluetoothSpeaker
{
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
        
        // State tracking
        private string _currentTrack = string.Empty;
        private string _previousTrack = string.Empty;
        private DateTime _lastCommentTime = DateTime.MinValue;
        private readonly TimeSpan _commentThrottle = TimeSpan.FromMinutes(2);
        private bool _isPlaying = false;
        
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
            
            // Clean up watchers
            foreach (var device in _activeDevices.Values)
            {
                device.StatusWatcher?.Dispose();
            }
            _activeDevices.Clear();
            
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
                    
                    foreach (var (device, path, address, name) in devices)
                    {
                        if (!_activeDevices.ContainsKey(address))
                        {
                            Console.WriteLine($"New device connected: {name} ({address})");
                            
                            // Set device as trusted
                            await device.SetAsync("Trusted", true);
                            
                            // Find media player
                            var playerInfo = await _objectManager.FindMediaPlayerForDeviceAsync(path);
                            
                            IDisposable? watcher = null;
                            
                            if (playerInfo.HasValue)
                            {
                                // Set up media player watcher
                                watcher = await playerInfo.Value.Player.WatchPropertiesAsync(changes =>
                                    HandleMediaPlayerChangesAsync(address, changes));
                                
                                Console.WriteLine($"Media player found for {name}");
                            }
                            
                            var deviceEntry = (device, path, playerInfo?.Player, playerInfo?.Path, watcher);
                            _activeDevices[address] = deviceEntry;
                            
                            // Welcome message
                            await GenerateCommentAsync($"Oh great, {name} just connected. Let me guess, you're about to blast some questionable music choices through me?");
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
                        
                        if (!string.IsNullOrEmpty(trackInfo) && trackInfo != _currentTrack)
                        {
                            _previousTrack = _currentTrack;
                            _currentTrack = trackInfo;
                            
                            Console.WriteLine($"Now playing: {_currentTrack}");
                            
                            // Maybe generate a comment
                            if (ShouldGenerateComment())
                            {
                                await GenerateCommentAboutTrackAsync(_currentTrack);
                            }
                        }
                    }
                    
                    // Check playback state
                    bool wasPlaying = _isPlaying;
                    _isPlaying = status.Contains("Playing");
                    
                    if (_isPlaying && !wasPlaying)
                    {
                        Console.WriteLine("Music started playing");
                    }
                    else if (!_isPlaying && wasPlaying)
                    {
                        Console.WriteLine("Music paused/stopped");
                        
                        if (_random.Next(0, 4) == 0) // 25% chance
                        {
                            await GenerateCommentAsync("What, couldn't handle the truth about your music taste? Good choice pausing that.");
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
                foreach (var change in changes.Changed)
                {
                    if (change.Key == "Track")
                    {
                        if (change.Value is IDictionary<string, object> trackDict)
                        {
                            var trackInfo = FormatTrackInfo(trackDict);
                            
                            if (!string.IsNullOrEmpty(trackInfo) && trackInfo != _currentTrack)
                            {
                                _previousTrack = _currentTrack;
                                _currentTrack = trackInfo;
                                
                                Console.WriteLine($"Track changed: {_currentTrack}");
                                
                                if (ShouldGenerateComment())
                                {
                                    await GenerateCommentAboutTrackAsync(_currentTrack);
                                }
                            }
                        }
                    }
                    else if (change.Key == "Status")
                    {
                        var status = change.Value?.ToString() ?? "";
                        Console.WriteLine($"Playback status: {status}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling media player changes: {ex.Message}");
            }
        }

        private bool ShouldGenerateComment()
        {
            // Comment throttling and randomness
            return DateTime.Now - _lastCommentTime > _commentThrottle && _random.Next(0, 3) == 0; // 33% chance
        }

        private async Task GenerateCommentAboutTrackAsync(string trackInfo)
        {
            var prompts = new[]
            {
                $"Generate a short, snarky, humorous comment about someone playing '{trackInfo}'. Be witty but not offensive.",
                $"Roast this music choice in a funny way: '{trackInfo}'. Keep it clever and brief.",
                $"Make a sarcastic comment about '{trackInfo}' being played. Be humorous but not mean-spirited.",
                $"Write a witty, sardonic observation about someone's choice to play '{trackInfo}'."
            };
            
            var prompt = prompts[_random.Next(prompts.Length)];
            await GenerateCommentAsync(prompt);
        }

        private async Task GenerateCommentAsync(string prompt)
        {
            try
            {
                _lastCommentTime = DateTime.Now;
                
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
                            Console.WriteLine($"\n🎵 SPEAKER SAYS: {comment}\n");
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
