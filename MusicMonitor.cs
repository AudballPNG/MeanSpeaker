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
        private readonly object _deviceLock = new object();
        private Dictionary<string, (IDevice1 Device, ObjectPath Path, IMediaPlayer1? Player, ObjectPath? PlayerPath, IDisposable? StatusWatcher)> _activeDevices = new();
        
        // Session-based memory for each device
        private Dictionary<string, DeviceSession> _deviceSessions = new();
        
        // Global state tracking
        private readonly TimeSpan _commentThrottle = TimeSpan.FromSeconds(30); // Reduced for testing - was 1 minute
        
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

            // Initialize D-Bus connection
            await InitializeDBusAsync();

            // Verify audio setup
            await VerifyAudioSetupAsync();

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
            _ = Task.Run(() => MonitorAudioRoutingAsync(token), token);

            Console.WriteLine("Monitoring started. Connect your device to start streaming music!");
        }

        public void StopMonitoring()
        {
            Console.WriteLine("Stopping music monitoring...");
            
            _monitoringCancellation?.Cancel();
            
            // Clean up watchers and display session summaries
            Dictionary<string, (IDevice1 Device, ObjectPath Path, IMediaPlayer1? Player, ObjectPath? PlayerPath, IDisposable? StatusWatcher)> devicesToCleanup;
            lock (_deviceLock)
            {
                devicesToCleanup = new Dictionary<string, (IDevice1, ObjectPath, IMediaPlayer1?, ObjectPath?, IDisposable?)>(_activeDevices);
                _activeDevices.Clear();
            }
            
            foreach (var (address, deviceEntry) in devicesToCleanup)
            {
                try
                {
                    deviceEntry.StatusWatcher?.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error disposing watcher for {address}: {ex.Message}");
                }
                
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
                    var disconnectedDevices = new List<string>();
                    lock (_deviceLock)
                    {
                        disconnectedDevices = _activeDevices.Keys.Where(addr => !currentDeviceAddresses.Contains(addr)).ToList();
                    }
                    
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
                        
                        // Clean up device watcher safely
                        lock (_deviceLock)
                        {
                            if (_activeDevices.TryGetValue(address, out var deviceEntry))
                            {
                                try
                                {
                                    deviceEntry.StatusWatcher?.Dispose();
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Error disposing watcher for {address}: {ex.Message}");
                                }
                                _activeDevices.Remove(address);
                            }
                        }
                    }
                    
                    // Handle new device connections
                    foreach (var (device, path, address, name) in devices)
                    {
                        bool isNewDevice = false;
                        lock (_deviceLock)
                        {
                            isNewDevice = !_activeDevices.ContainsKey(address);
                        }
                        
                        if (isNewDevice)
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
                            
                            // Find media player - try multiple times as it may take a moment to appear
                            IDisposable? watcher = null;
                            var playerInfo = await _objectManager.FindMediaPlayerForDeviceAsync(path);
                            
                            if (playerInfo.HasValue)
                            {
                                Console.WriteLine($"Media player found for {name}");
                                
                                // Set up media player watcher
                                try
                                {
                                    watcher = await playerInfo.Value.Player.WatchPropertiesAsync(changes =>
                                    {
                                        _ = Task.Run(() => HandleMediaPlayerChangesAsync(address, changes));
                                    });
                                    Console.WriteLine($"Media player watcher set up for {name}");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Failed to set up media player watcher for {name}: {ex.Message}");
                                }
                            }
                            else
                            {
                                Console.WriteLine($"No media player found for {name} yet, will retry later");
                                
                                // Schedule a retry to find the media player later
                                _ = Task.Run(async () =>
                                {
                                    for (int i = 0; i < 10; i++) // Try for 30 seconds
                                    {
                                        await Task.Delay(3000);
                                        
                                        // Check if device is still connected before retrying
                                        bool deviceStillExists = false;
                                        lock (_deviceLock)
                                        {
                                            deviceStillExists = _activeDevices.ContainsKey(address);
                                        }
                                        
                                        if (!deviceStillExists)
                                        {
                                            Console.WriteLine($"Device {name} disconnected during retry, stopping search");
                                            break;
                                        }
                                        
                                        try
                                        {
                                            var retryPlayerInfo = await _objectManager.FindMediaPlayerForDeviceAsync(path);
                                            if (retryPlayerInfo.HasValue)
                                            {
                                                Console.WriteLine($"Media player found for {name} on retry {i + 1}");
                                                
                                                var retryWatcher = await retryPlayerInfo.Value.Player.WatchPropertiesAsync(changes =>
                                                {
                                                    _ = Task.Run(() => HandleMediaPlayerChangesAsync(address, changes));
                                                });
                                                
                                                // Safely update the device entry with the new watcher
                                                lock (_deviceLock)
                                                {
                                                    if (_activeDevices.TryGetValue(address, out var currentEntry))
                                                    {
                                                        // Dispose old watcher if it exists
                                                        currentEntry.StatusWatcher?.Dispose();
                                                        
                                                        var updatedEntry = (currentEntry.Device, currentEntry.Path, retryPlayerInfo.Value.Player, retryPlayerInfo.Value.Path, retryWatcher);
                                                        _activeDevices[address] = updatedEntry;
                                                        Console.WriteLine($"Media player watcher set up for {name} on retry");
                                                    }
                                                    else
                                                    {
                                                        // Device was removed while we were setting up, dispose the new watcher
                                                        retryWatcher?.Dispose();
                                                        Console.WriteLine($"Device {name} was removed during watcher setup");
                                                    }
                                                }
                                                break;
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"Failed to set up media player watcher for {name} on retry {i + 1}: {ex.Message}");
                                        }
                                    }
                                });
                            }
                            
                            var deviceEntry = (device, path, playerInfo?.Player, playerInfo?.Path, watcher);
                            lock (_deviceLock)
                            {
                                _activeDevices[address] = deviceEntry;
                            }
                            
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
                    
                    // Debug output
                    if (!string.IsNullOrEmpty(status) || !string.IsNullOrEmpty(metadata))
                    {
                        Console.WriteLine($"[DEBUG] PlayerCtl Status: '{status.Trim()}'");
                        if (!string.IsNullOrEmpty(metadata))
                            Console.WriteLine($"[DEBUG] PlayerCtl Metadata available: {metadata.Length} chars");
                    }
                    
                    if (!string.IsNullOrEmpty(metadata))
                    {
                        var trackInfo = ParseTrackInfo(metadata);
                        
                        if (!string.IsNullOrEmpty(trackInfo))
                        {
                            Console.WriteLine($"[DEBUG] Parsed track: '{trackInfo}'");
                            
                            // Try to match track to specific device session
                            bool trackHandled = false;
                            foreach (var session in _deviceSessions.Values)
                            {
                                if (trackInfo != session.CurrentTrack)
                                {
                                    Console.WriteLine($"[DEBUG] Adding track '{trackInfo}' to session for {session.DeviceName}");
                                    session.AddTrack(trackInfo);
                                    
                                    Console.WriteLine($"Now playing on {session.DeviceName}: {trackInfo}");
                                    
                                    // Generate comment with session context
                                    if (ShouldGenerateCommentForDevice(session.DeviceAddress))
                                    {
                                        Console.WriteLine($"[DEBUG] Generating comment for {session.DeviceName}");
                                        await GenerateCommentAboutTrackAsync(session.DeviceAddress, trackInfo);
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[DEBUG] Skipping comment for {session.DeviceName} (throttled or random)");
                                    }
                                    trackHandled = true;
                                    break; // Only handle for one session to avoid duplicates
                                }
                            }
                            
                            if (!trackHandled && _deviceSessions.Any())
                            {
                                Console.WriteLine($"[DEBUG] Track '{trackInfo}' already current for all sessions");
                            }
                        }
                        else
                        {
                            Console.WriteLine("[DEBUG] Failed to parse track info from metadata");
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
            {
                Console.WriteLine($"[DEBUG] No session found for device {deviceAddress}");
                return false;
            }
            
            var timeSinceLastComment = DateTime.Now - session.LastCommentTime;
            var throttleCheck = timeSinceLastComment > _commentThrottle;
            var randomCheck = _random.Next(0, 3) == 0; // 33% chance
            
            Console.WriteLine($"[DEBUG] Comment check for {session.DeviceName}: " +
                            $"Time since last: {timeSinceLastComment:mm\\:ss}, " +
                            $"Throttle passed: {throttleCheck}, " +
                            $"Random passed: {randomCheck}");
            
            return throttleCheck && randomCheck;
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
            {
                Console.WriteLine($"[DEBUG] Cannot generate comment - no session for device {deviceAddress}");
                return;
            }

            try
            {
                Console.WriteLine($"[DEBUG] Generating comment for {session.DeviceName} with prompt: {prompt}");
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

                Console.WriteLine($"[DEBUG] Sending request to OpenAI...");
                var response = await _httpClient.PostAsync("https://api.openai.com/v1/chat/completions", content);
                
                Console.WriteLine($"[DEBUG] OpenAI response status: {response.StatusCode}");
                
                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[DEBUG] OpenAI response length: {responseJson.Length} chars");
                    
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
                                Console.WriteLine($"[DEBUG] Speaking comment: {comment}");
                                await SpeakAsync(comment);
                            }
                            else
                            {
                                Console.WriteLine("[DEBUG] Speech disabled, not speaking comment");
                            }
                        }
                        else
                        {
                            Console.WriteLine("[DEBUG] OpenAI returned empty comment");
                        }
                    }
                    else
                    {
                        Console.WriteLine("[DEBUG] Failed to parse OpenAI response structure");
                        Console.WriteLine($"[DEBUG] Response content: {responseJson}");
                    }
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Failed to generate comment: {response.StatusCode}");
                    Console.WriteLine($"Error details: {errorContent}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error generating comment: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
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

        public async Task ShowStatusAsync()
        {
            Console.WriteLine("=== BLUETOOTH SPEAKER STATUS ===");
            
            int activeDeviceCount, deviceSessionCount;
            lock (_deviceLock)
            {
                activeDeviceCount = _activeDevices.Count;
            }
            deviceSessionCount = _deviceSessions.Count;
            
            Console.WriteLine($"Active devices: {activeDeviceCount}");
            Console.WriteLine($"Device sessions: {deviceSessionCount}");
            
            foreach (var session in _deviceSessions.Values)
            {
                Console.WriteLine($"\nDevice: {session.DeviceName} ({session.DeviceAddress})");
                Console.WriteLine($"  Connected: {session.ConnectedAt:yyyy-MM-dd HH:mm:ss}");
                Console.WriteLine($"  Current track: {session.CurrentTrack}");
                Console.WriteLine($"  Total tracks: {session.TotalTracksPlayed}");
                Console.WriteLine($"  Comments generated: {session.GeneratedComments.Count}");
                Console.WriteLine($"  Last comment: {session.LastCommentTime:yyyy-MM-dd HH:mm:ss}");
                Console.WriteLine($"  Is playing: {session.IsPlaying}");
            }
            
            // Check services
            Console.WriteLine("\n=== SERVICE STATUS ===");
            var blualsaStatus = await RunCommandWithOutputAsync("systemctl", "is-active bluealsa");
            var aplayStatus = await RunCommandWithOutputAsync("systemctl", "is-active bluealsa-aplay");
            var bluetoothStatus = await RunCommandWithOutputAsync("systemctl", "is-active bluetooth");
            
            Console.WriteLine($"BlueALSA: {blualsaStatus.Trim()}");
            Console.WriteLine($"BlueALSA-aplay: {aplayStatus.Trim()}");
            Console.WriteLine($"Bluetooth: {bluetoothStatus.Trim()}");
            
            // Check playerctl
            var playerStatus = await RunCommandWithOutputAsync("playerctl", "status");
            var playerMetadata = await RunCommandWithOutputAsync("playerctl", "metadata");
            
            Console.WriteLine($"\nPlayerctl status: '{playerStatus.Trim()}'");
            if (!string.IsNullOrEmpty(playerMetadata))
            {
                Console.WriteLine("Playerctl metadata available: Yes");
                var trackInfo = ParseTrackInfo(playerMetadata);
                Console.WriteLine($"Parsed track: '{trackInfo}'");
            }
            else
            {
                Console.WriteLine("Playerctl metadata available: No");
            }
        }

        public async Task TestCommentAsync()
        {
            if (!_deviceSessions.Any())
            {
                Console.WriteLine("No active device sessions. Connect a device first.");
                return;
            }
            
            var session = _deviceSessions.Values.First();
            Console.WriteLine($"Generating test comment for {session.DeviceName}...");
            
            // Force comment generation by bypassing throttle
            session.LastCommentTime = DateTime.MinValue;
            
            await GenerateCommentForDeviceAsync(session.DeviceAddress, 
                "Generate a snarky comment about someone testing the AI commentary system of their Bluetooth speaker.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            try
            {
                StopMonitoring();
                _monitoringCancellation?.Dispose();
                _httpClient?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during dispose: {ex.Message}");
            }
            
            _disposed = true;
        }
    }
}
