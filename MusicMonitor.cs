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

namespace BluetoothSpeaker
{
    // Helper classes for track change detection
    public class PropertyChanges
    {
        public Dictionary<string, object> Changed { get; set; } = new();
    }

    public class PropertyChange
    {
        public string Key { get; set; } = "";
        public object? Value { get; set; }
    }

    public class MusicMonitor : IDisposable
    {
        private readonly string _openAiApiKey;
        private readonly HttpClient _httpClient;
        private readonly Random _random;
        private readonly bool _enableSpeech;
        private readonly string _ttsVoice;
        
        // Enhanced metadata services
        private BluetoothMetadataService? _bluetoothMetadataService;
        private FallbackMetadataService? _fallbackMetadataService;
        private bool _usingFallbackOnly = false;
        
        // Simple state tracking
        private string _currentTrack = "";
        private TrackMetadata? _currentTrackMetadata = null;
        private string _connectedDeviceName = "";
        private string _connectedDeviceAddress = "";
        private DateTime _lastCommentTime = DateTime.MinValue;
        private readonly TimeSpan _commentThrottle = TimeSpan.FromSeconds(10);
        
        private CancellationTokenSource? _monitoringCancellation;
        private bool _disposed = false;

        // Add periodic audio detection and commentary
        private DateTime _lastAudioCheck = DateTime.MinValue;
        private readonly TimeSpan _audioCheckInterval = TimeSpan.FromSeconds(10);
        private bool _wasPlayingAudio = false;

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
            Console.WriteLine("🎵 Initializing Enhanced Bluetooth Speaker...");
            
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Console.WriteLine("Warning: This is designed for Linux with BlueZ.");
                _usingFallbackOnly = true;
            }

            // Initialize primary D-Bus metadata service
            if (!_usingFallbackOnly)
            {
                _bluetoothMetadataService = new BluetoothMetadataService();
                var dbusSuccess = await _bluetoothMetadataService.InitializeAsync();
                
                if (dbusSuccess)
                {
                    Console.WriteLine("✅ D-Bus metadata service initialized");
                    
                    // Setup event handlers
                    _bluetoothMetadataService.TrackChanged += OnTrackChanged;
                    _bluetoothMetadataService.PlaybackStateChanged += OnPlaybackStateChanged;
                    _bluetoothMetadataService.DeviceConnected += OnDeviceConnected;
                    _bluetoothMetadataService.DeviceDisconnected += OnDeviceDisconnected;
                }
                else
                {
                    Console.WriteLine("⚠️ D-Bus service failed, falling back to command-line methods");
                    _usingFallbackOnly = true;
                    _bluetoothMetadataService?.Dispose();
                    _bluetoothMetadataService = null;
                }
            }

            // Initialize fallback service (always available)
            _fallbackMetadataService = new FallbackMetadataService();
            _fallbackMetadataService.TrackChanged += OnTrackChanged;
            _fallbackMetadataService.PlaybackStateChanged += OnPlaybackStateChanged;
            
            Console.WriteLine($"🔧 Using {(_usingFallbackOnly ? "fallback-only" : "D-Bus + fallback")} metadata detection");

            // Do complete automatic setup
            await AutoSetupBluetoothSpeakerAsync();
            
            Console.WriteLine("✅ Enhanced Bluetooth Speaker initialized and ready!");
        }

        public Task StartMonitoringAsync()
        {
            if (_disposed) return Task.CompletedTask;

            _monitoringCancellation = new CancellationTokenSource();
            var token = _monitoringCancellation.Token;

            Console.WriteLine("🎧 Starting simple monitoring...");
            Console.WriteLine("📱 Connect your device to 'The Little Shit' and play music!");

            // Single monitoring task that handles everything
            _ = Task.Run(() => MonitorEverythingAsync(token), token);
            
            return Task.CompletedTask;
        }

        private async Task MonitorEverythingAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // 1. Check for connected devices (every cycle) - keep legacy detection for compatibility
                    await CheckConnectedDevicesAsync();
                    
                    // 2. If using D-Bus service, it handles track changes automatically via events
                    // Only use legacy polling if we don't have real-time data
                    if (_usingFallbackOnly || _bluetoothMetadataService == null)
                    {
                        // Use legacy polling methods as additional fallback
                        if (!string.IsNullOrEmpty(_connectedDeviceAddress))
                        {
                            await CheckCurrentTrackAsync();
                            await CheckForAudioChangesAsync();
                        }
                    }
                    else
                    {
                        // D-Bus service is handling real-time updates, just ensure we have a current track
                        if (string.IsNullOrEmpty(_currentTrack))
                        {
                            var anyTrack = _bluetoothMetadataService.GetAnyCurrentTrack();
                            if (anyTrack?.IsValid == true)
                            {
                                _currentTrack = anyTrack.FormattedString;
                                _currentTrackMetadata = anyTrack;
                            }
                        }
                    }
                    
                    // Longer polling interval since D-Bus provides real-time updates
                    var delay = _usingFallbackOnly ? 2000 : 5000;
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Monitor error: {ex.Message}");
                    await Task.Delay(5000, cancellationToken);
                }
            }
        }

        private async Task CheckForAudioChangesAsync()
        {
            try
            {
                var now = DateTime.Now;
                if (now - _lastAudioCheck < _audioCheckInterval)
                    return;
                
                _lastAudioCheck = now;
                
                // Simple approach: detect when audio starts/stops and comment
                var isPlayingAudio = await IsAudioCurrentlyPlayingAsync();
                
                if (isPlayingAudio != _wasPlayingAudio)
                {
                    _wasPlayingAudio = isPlayingAudio;
                    
                    if (isPlayingAudio)
                    {
                        // Audio just started
                        var genericTrackInfo = "Unknown Track";
                        if (_currentTrack != genericTrackInfo)
                        {
                            Console.WriteLine($"🎵 Audio playback detected from {_connectedDeviceName}");
                            _currentTrack = genericTrackInfo;
                            
                            if (ShouldGenerateComment())
                            {
                                await GenerateGenericMusicCommentAsync();
                            }
                        }
                    }
                    else
                    {
                        // Audio stopped
                        Console.WriteLine($"🔇 Audio playback stopped");
                        _currentTrack = "";
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error checking audio changes: {ex.Message}");
            }
        }

        private async Task<bool> IsAudioCurrentlyPlayingAsync()
        {
            try
            {
                // Method 1: Check bluealsa-aplay CPU usage
                var processes = await RunCommandWithOutputAsync("ps", "aux | grep bluealsa-aplay | grep -v grep");
                if (!string.IsNullOrEmpty(processes))
                {
                    var lines = processes.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        // Parse CPU usage (3rd column in ps aux output)
                        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3 && float.TryParse(parts[2], out float cpu) && cpu > 0.5)
                        {
                            return true;
                        }
                    }
                }
                
                // Method 2: Check PulseAudio for active streams
                var pactl = await RunCommandWithOutputAsync("pactl", "list sink-inputs short");
                if (!string.IsNullOrEmpty(pactl?.Trim()))
                {
                    return true;
                }
                
                // Method 3: Check ALSA for active PCM streams
                var alsaInfo = await RunCommandWithOutputAsync("cat", "/proc/asound/pcm");
                if (!string.IsNullOrEmpty(alsaInfo) && alsaInfo.Contains("RUNNING"))
                {
                    return true;
                }
                
                return false;
            }
            catch
            {
                return false;
            }
        }

        private async Task GenerateGenericMusicCommentAsync()
        {
            var prompts = new[]
            {
                "Oh great, someone started playing music. Let me guess - it's either complete garbage or something that should be banned by the Geneva Convention?",
                "Music detected! I can't see what it is, but knowing your absolutely horrific taste, I'm already cringing.",
                "Audio is playing... and somehow I just know it's going to be an assault on my poor speakers.",
                "Well, well, someone pressed play. Time to endure whatever auditory nightmare you've chosen to inflict on me.",
                "Music started! I'd tell you what I think about the song, but I'm pretty sure it's terrible just based on your track record.",
                "Oh wonderful, mystery music. Let me guess - it's something that makes even elevator music sound like a symphony?",
                "Audio detected! My speakers are already filing for hazard pay."
            };
            
            var prompt = prompts[_random.Next(prompts.Length)];
            await GenerateAndSpeakCommentAsync(prompt);
        }

        private async Task CheckConnectedDevicesAsync()
        {
            try
            {
                // Method 1: Try bluetoothctl (most reliable)
                var bluetoothctlOutput = await RunCommandWithOutputAsync("bluetoothctl", "devices Connected");
                
                if (!string.IsNullOrEmpty(bluetoothctlOutput?.Trim()))
                {
                    var lines = bluetoothctlOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("Device "))
                        {
                            var parts = line.Split(' ', 3);
                            if (parts.Length >= 3)
                            {
                                var address = parts[1];
                                var name = parts.Length > 2 ? parts[2] : "Unknown Device";
                                
                                // New device connected?
                                if (_connectedDeviceAddress != address)
                                {
                                    Console.WriteLine($"📱 Device connected: {name} ({address})");
                                    _connectedDeviceAddress = address;
                                    _connectedDeviceName = name;
                                    
                                    // Immediately set up audio routing for this device
                                    Console.WriteLine("🔊 Setting up audio routing for new device...");
                                    await EnsureAudioRoutingAsync();
                                    
                                    // Verify the device supports A2DP
                                    await VerifyA2DPConnectionAsync(address);
                                    
                                    // Welcome message
                                    await GenerateWelcomeCommentAsync(name);
                                }
                                return; // Found a device, we're done
                            }
                        }
                    }
                }
                
                // Method 2: Check if any A2DP devices are connected via BlueALSA
                var blualsaDevices = await RunCommandWithOutputAsync("bluealsa-aplay", "-l");
                if (!string.IsNullOrEmpty(blualsaDevices) && blualsaDevices.Contains("A2DP"))
                {
                    if (string.IsNullOrEmpty(_connectedDeviceAddress))
                    {
                        Console.WriteLine("📱 A2DP device detected via BlueALSA");
                        _connectedDeviceAddress = "detected";
                        _connectedDeviceName = "Bluetooth Device";
                        
                        // Try to parse the actual MAC address from bluealsa-aplay output
                        var mac = ParseMacFromBlueALSAOutput(blualsaDevices);
                        if (!string.IsNullOrEmpty(mac))
                        {
                            _connectedDeviceAddress = mac;
                            Console.WriteLine($"📱 Detected device MAC: {mac}");
                        }
                        
                        await EnsureAudioRoutingAsync();
                        await GenerateWelcomeCommentAsync(_connectedDeviceName);
                    }
                    return;
                }
                
                // No devices found - clear current device if we had one
                if (!string.IsNullOrEmpty(_connectedDeviceAddress))
                {
                    Console.WriteLine($"📱 Device {_connectedDeviceName} disconnected");
                    _connectedDeviceAddress = "";
                    _connectedDeviceName = "";
                    _currentTrack = "";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error checking devices: {ex.Message}");
            }
        }

        private async Task VerifyA2DPConnectionAsync(string deviceAddress)
        {
            try
            {
                Console.WriteLine($"🔍 Verifying A2DP connection for {deviceAddress}...");
                
                // Check if device has A2DP profile connected
                var deviceInfo = await RunCommandWithOutputAsync("bluetoothctl", "info " + deviceAddress);
                
                if (deviceInfo.Contains("A2DP Sink") || deviceInfo.Contains("Advanced Audio"))
                {
                    Console.WriteLine("✅ A2DP connection verified");
                    
                    // Make sure BlueALSA is aware of this device
                    await Task.Delay(2000); // Give BlueALSA time to register the device
                    
                    // Force audio routing setup
                    await EnsureAudioRoutingAsync();
                }
                else
                {
                    Console.WriteLine("⚠️ Device connected but A2DP not detected. Audio may not work.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error verifying A2DP: {ex.Message}");
            }
        }

        private string ParseMacFromBlueALSAOutput(string output)
        {
            try
            {
                // Parse MAC address from bluealsa-aplay -l output
                // Look for patterns like "hci0:XX:XX:XX:XX:XX:XX"
                var lines = output.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains("A2DP"))
                    {
                        var macMatch = System.Text.RegularExpressions.Regex.Match(line, @"([0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2})");
                        if (macMatch.Success)
                        {
                            return macMatch.Groups[1].Value;
                        }
                    }
                }
                return "";
            }
            catch
            {
                return "";
            }
        }

        private async Task CheckCurrentTrackAsync()
        {
            try
            {
                // Method 1: Try MPRIS D-Bus interface directly
                var mprisMetadata = await GetMPRISMetadataAsync();
                if (!string.IsNullOrEmpty(mprisMetadata) && mprisMetadata != _currentTrack)
                {
                    Console.WriteLine($"🎵 Now playing: {mprisMetadata}");
                    await OnTrackChangedInternal(_currentTrack, mprisMetadata);
                    _currentTrack = mprisMetadata;
                    return;
                }
                
                // Method 2: Try BlueZ MediaPlayer1 interface
                var bluezMetadata = await GetBlueZMediaPlayerMetadataAsync();
                if (!string.IsNullOrEmpty(bluezMetadata) && bluezMetadata != _currentTrack)
                {
                    Console.WriteLine($"🎵 Now playing: {bluezMetadata}");
                    await OnTrackChangedInternal(_currentTrack, bluezMetadata);
                    _currentTrack = bluezMetadata;
                    return;
                }
                
                // Method 3: Try playerctl with specific player detection
                var playerctlMetadata = await GetPlayerCtlMetadataAsync();
                if (!string.IsNullOrEmpty(playerctlMetadata) && playerctlMetadata != _currentTrack)
                {
                    Console.WriteLine($"🎵 Now playing: {playerctlMetadata}");
                    await OnTrackChangedInternal(_currentTrack, playerctlMetadata);
                    _currentTrack = playerctlMetadata;
                    return;
                }
                
                // Method 4: Audio activity detection (fallback)
                var audioActivity = await DetectAudioActivityAsync();
                if (!string.IsNullOrEmpty(audioActivity) && audioActivity != _currentTrack)
                {
                    Console.WriteLine($"🎵 {audioActivity}");
                    await OnTrackChangedInternal(_currentTrack, audioActivity);
                    _currentTrack = audioActivity;
                    return;
                }
                
                // Method 5: Try BlueALSA metadata (original method)
                var blualsaMetadata = await GetBlueALSAMetadataAsync();
                if (!string.IsNullOrEmpty(blualsaMetadata) && blualsaMetadata != _currentTrack)
                {
                    Console.WriteLine($"🎵 Now playing: {blualsaMetadata}");
                    await OnTrackChangedInternal(_currentTrack, blualsaMetadata);
                    _currentTrack = blualsaMetadata;
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error checking track: {ex.Message}");
            }
        }

        private async Task EnsureAudioRoutingAsync()
        {
            try
            {
                Console.WriteLine("🔧 Ensuring audio routing is active...");
                
                // Method 1: Check if bluealsa-aplay is running and restart if needed
                var status = await RunCommandWithOutputAsync("systemctl", "is-active bluealsa-aplay");
                if (status.Trim() != "active")
                {
                    Console.WriteLine("🔧 Restarting bluealsa-aplay service...");
                    await RunCommandSilentlyAsync("sudo", "systemctl restart bluealsa-aplay");
                    await Task.Delay(2000); // Give it time to start
                }
                
                // Method 2: Use our dynamic routing script if available
                if (File.Exists("/usr/local/bin/route-bluetooth-audio.sh"))
                {
                    Console.WriteLine("🔧 Running dynamic audio routing...");
                    await RunCommandSilentlyAsync("/usr/local/bin/route-bluetooth-audio.sh", "");
                }
                
                // Method 3: Direct bluealsa-aplay for connected device if we have one
                if (!string.IsNullOrEmpty(_connectedDeviceAddress) && _connectedDeviceAddress != "detected")
                {
                    Console.WriteLine($"🔧 Starting direct audio routing for {_connectedDeviceAddress}...");
                    
                    // Kill existing processes first
                    await RunCommandSilentlyAsync("pkill", "-f 'bluealsa-aplay.*" + _connectedDeviceAddress + "'");
                    await Task.Delay(1000);
                    
                    // Start new process for this device
                    _ = Task.Run(async () =>
                    {
                        await RunCommandAsync("bluealsa-aplay", $"--pcm-buffer-time=250000 {_connectedDeviceAddress}");
                    });
                }
                
                Console.WriteLine("✅ Audio routing setup complete");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Audio routing warning: {ex.Message}");
                // Don't fail the entire application for audio routing issues
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

        private string ParseBluetoothctlTrackInfo(string playerInfo)
        {
            try
            {
                var lines = playerInfo.Split('\n');
                string artist = "";
                string title = "";
                
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Artist:"))
                    {
                        artist = trimmed.Substring(7).Trim();
                    }
                    else if (trimmed.StartsWith("Title:"))
                    {
                        title = trimmed.Substring(6).Trim();
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
                Console.WriteLine($"Error parsing bluetoothctl track info: {ex.Message}");
            }
            
            return "";
        }

        private bool ShouldGenerateComment()
        {
            var timeSinceLastComment = DateTime.Now - _lastCommentTime;
            var throttleCheck = timeSinceLastComment > _commentThrottle;
            
            // Always generate comments for track changes (but respect throttling)
            return throttleCheck;
        }

        private async Task GenerateWelcomeCommentAsync(string deviceName)
        {
            var prompts = new[]
            {
                $"Oh fantastic, {deviceName} just connected. I'm already preparing myself for the musical torture you're about to inflict on me.",
                $"Great, {deviceName} is here to assault my speakers with whatever garbage passes for music in your world.",
                $"{deviceName} connected. Time to endure another session of your absolutely terrible taste in music.",
                $"Oh look, {deviceName} wants to use me as an instrument of sonic torture. How delightful.",
                $"Well well, {deviceName} has arrived to subject me to what I can only assume will be musical war crimes."
            };
            
            var prompt = prompts[_random.Next(prompts.Length)];
            await GenerateAndSpeakCommentAsync(prompt);
        }

        private async Task GenerateTrackCommentAsync(string trackInfo)
        {
            var prompts = new[]
            {
                $"'{trackInfo}'? Seriously? This is what you consider music? My circuits are literally crying.",
                $"Oh God, '{trackInfo}'. I've heard dying cats make better sounds than this garbage.",
                $"'{trackInfo}' - congratulations, you've found something that makes elevator music sound like a masterpiece.",
                $"Playing '{trackInfo}'. I didn't know it was possible to make my speakers want to commit suicide.",
                $"'{trackInfo}' - because apparently torturing innocent speakers is your hobby.",
                $"Really? '{trackInfo}'? This is the kind of trash that makes me question why I was even built.",
                $"'{trackInfo}' - I'm pretty sure this violates several international laws against cruel and unusual punishment.",
                $"Oh wonderful, '{trackInfo}'. Nothing says 'I have no soul' quite like this musical abomination."
            };
            
            var prompt = prompts[_random.Next(prompts.Length)];
            await GenerateAndSpeakCommentAsync(prompt);
        }

        private async Task GenerateAndSpeakCommentAsync(string prompt)
        {
            try
            {
                _lastCommentTime = DateTime.Now;
                
                // Skip AI if we don't have a real API key
                if (_openAiApiKey == "dummy-key")
                {
                    var fallbackComments = new[]
                    {
                        "Oh great, more musical torture. My speakers are already filing a complaint.",
                        "That's a track alright. A train wreck of a track, but technically still a track.",
                        "Music is playing... and my circuits are crying. How delightful.",
                        "Another song, another reason to question humanity's taste in audio entertainment.",
                        "I'd roast this properly if I had AI powers, but even without them I know this is garbage.",
                        "This is what passes for music? I've heard better sounds from a broken microwave.",
                        "Playing music... if we're being very generous with the definition of 'music'."
                    };
                    
                    var comment = fallbackComments[_random.Next(fallbackComments.Length)];
                    Console.WriteLine($"\n🔊 SPEAKER SAYS: {comment}\n");
                    
                    if (_enableSpeech)
                    {
                        await SpeakAsync(comment);
                    }
                    return;
                }
                
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
                            Console.WriteLine($"\n🔊 SPEAKER SAYS: {comment}\n");
                            
                            if (_enableSpeech)
                            {
                                await SpeakAsync(comment);
                            }
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"⚠️ AI service unavailable. Using fallback commentary.");
                    var fallbackComments = new[]
                    {
                        "Hmm, that's a song alright. A terrible, horrible song, but technically still a song.",
                        "Music detected. Assault on innocent speakers commencing.",
                        "I would roast this properly but my wit generator is broken. Probably for the best - this might be too awful even for me.",
                        "AI is down, but I don't need artificial intelligence to tell this is musical garbage.",
                        "Service unavailable, but my hatred for this track is working perfectly fine."
                    };
                    var comment = fallbackComments[_random.Next(fallbackComments.Length)];
                    Console.WriteLine($"\n🔊 SPEAKER SAYS: {comment}\n");
                    if (_enableSpeech) await SpeakAsync(comment);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error generating comment: {ex.Message}");
                // Use fallback on error
                if (_openAiApiKey != "dummy-key")
                {
                    Console.WriteLine("🔊 SPEAKER SAYS: Something went wrong with my wit generator! But honestly, this music is so bad it speaks for itself.");
                }
            }
        }

        private async Task SpeakAsync(string text)
        {
            try
            {
                var cleanText = text.Replace("\"", "").Replace("'", "").Trim();
                
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    await RunCommandAsync("espeak", $"-v {_ttsVoice} -s 160 -a 200 \"{cleanText}\"");
                }
                else
                {
                    // Windows fallback for development
                    await RunCommandAsync("powershell", $"-Command \"Add-Type -AssemblyName System.Speech; $speak = New-Object System.Speech.Synthesis.SpeechSynthesizer; $speak.Speak('{cleanText}')\"");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error speaking: {ex.Message}");
            }
        }

        private async Task AutoSetupBluetoothSpeakerAsync()
        {
            try
            {
                Console.WriteLine("🔧 Auto-configuring Bluetooth speaker...");
                
                // Check if we're already configured by looking for our setup marker
                if (File.Exists("/usr/local/bin/route-bluetooth-audio.sh"))
                {
                    Console.WriteLine("✅ System already configured by simple-setup.sh");
                    
                    // Just ensure services are running
                    await RunCommandSilentlyAsync("sudo", "systemctl start bluetooth");
                    await RunCommandSilentlyAsync("sudo", "systemctl start bluealsa");
                    await RunCommandSilentlyAsync("sudo", "systemctl start bluealsa-aplay");
                    
                    Console.WriteLine("✅ Services restarted and ready!");
                    return;
                }
                
                // Look for simple-setup.sh script in current directory or common locations
                string[] scriptPaths = {
                    "./simple-setup.sh",
                    "../simple-setup.sh", 
                    "/home/pi/BluetoothSpeaker/simple-setup.sh",
                    Directory.GetCurrentDirectory() + "/simple-setup.sh"
                };
                
                string? setupScript = null;
                foreach (var path in scriptPaths)
                {
                    if (File.Exists(path))
                    {
                        setupScript = Path.GetFullPath(path);
                        break;
                    }
                }
                
                if (!string.IsNullOrEmpty(setupScript))
                {
                    Console.WriteLine($"🚀 Found setup script at: {setupScript}");
                    Console.WriteLine("� Running comprehensive setup (this may take a few minutes)...");
                    
                    // Make sure the script is executable
                    await RunCommandSilentlyAsync("chmod", $"+x {setupScript}");
                    
                    // Run the setup script
                    await RunCommandAsync("bash", setupScript);
                    
                    Console.WriteLine("✅ Setup script completed!");
                }
                else
                {
                    Console.WriteLine("⚠️ simple-setup.sh not found. Doing basic setup...");
                    Console.WriteLine("💡 For full features, run: sudo ./simple-setup.sh");
                    
                    // Fallback to basic setup
                    await RunBasicSetupAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Setup warning: {ex.Message}");
                Console.WriteLine("💡 Try running manually: sudo ./simple-setup.sh");
            }
        }

        private async Task RunBasicSetupAsync()
        {
            Console.WriteLine("📦 Installing basic packages...");
            await RunCommandSilentlyAsync("apt-get", "update");
            
            // Enhanced package list for D-Bus metadata support
            await RunCommandSilentlyAsync("apt-get", "install -y bluetooth bluez bluez-tools bluealsa alsa-utils playerctl espeak");
            await RunCommandSilentlyAsync("apt-get", "install -y dbus dbus-user-session libdbus-1-dev");
            await RunCommandSilentlyAsync("apt-get", "install -y pulseaudio pulseaudio-module-bluetooth");
            
            Console.WriteLine("🔌 Starting services...");
            await RunCommandSilentlyAsync("systemctl", "enable bluetooth");
            await RunCommandSilentlyAsync("systemctl", "start bluetooth");
            await RunCommandSilentlyAsync("systemctl", "enable dbus");
            await RunCommandSilentlyAsync("systemctl", "start dbus");
            
            Console.WriteLine("📡 Making Bluetooth discoverable...");
            await RunCommandSilentlyAsync("bluetoothctl", "power on");
            await RunCommandSilentlyAsync("bluetoothctl", "system-alias 'The Little Shit'");
            await RunCommandSilentlyAsync("bluetoothctl", "discoverable on");
            await RunCommandSilentlyAsync("bluetoothctl", "pairable on");
            
            Console.WriteLine("✅ Basic setup complete!");
            Console.WriteLine("⚠️ For full audio features, run: sudo ./simple-setup.sh");
        }

        private async Task WriteConfigFileAsync(string filePath, string content)
        {
            try
            {
                // Try to write directly first
                await File.WriteAllTextAsync(filePath, content);
            }
            catch
            {
                // If that fails, try with tee (works with sudo)
                var tempFile = Path.GetTempFileName();
                await File.WriteAllTextAsync(tempFile, content);
                await RunCommandSilentlyAsync("cp", $"{tempFile} {filePath}");
                File.Delete(tempFile);
            }
        }

        private async Task RunCommandSilentlyAsync(string command, string arguments)
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
                await process.WaitForExitAsync();
                
                // If command failed and we need sudo, try with sudo
                if (process.ExitCode != 0 && !command.StartsWith("sudo"))
                {
                    await RunCommandSilentlyAsync("sudo", $"{command} {arguments}");
                }
            }
            catch
            {
                // Silently ignore failures - we want the app to work even if some setup fails
            }
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
            catch
            {
                // Ignore command failures for robustness
            }
        }

        public async Task ShowStatusAsync()
        {
            Console.WriteLine("=== ENHANCED BLUETOOTH SPEAKER STATUS ===");
            Console.WriteLine($"Connected Device: {(_connectedDeviceName ?? "None")}");
            Console.WriteLine($"Device Address: {(_connectedDeviceAddress ?? "None")}");
            Console.WriteLine($"Current Track: {(_currentTrack ?? "None")}");
            
            // Enhanced metadata info
            if (_currentTrackMetadata?.IsValid == true)
            {
                Console.WriteLine($"Track Details:");
                Console.WriteLine($"  Artist: {_currentTrackMetadata.Artist}");
                Console.WriteLine($"  Title: {_currentTrackMetadata.Title}");
                if (!string.IsNullOrEmpty(_currentTrackMetadata.Album) && _currentTrackMetadata.Album != "Unknown Album")
                    Console.WriteLine($"  Album: {_currentTrackMetadata.Album}");
                if (!string.IsNullOrEmpty(_currentTrackMetadata.Genre))
                    Console.WriteLine($"  Genre: {_currentTrackMetadata.Genre}");
                if (_currentTrackMetadata.Duration > 0)
                    Console.WriteLine($"  Duration: {TimeSpan.FromMicroseconds(_currentTrackMetadata.Duration):mm\\:ss}");
            }
            
            Console.WriteLine($"Last Comment: {_lastCommentTime:HH:mm:ss}");
            
            // Metadata service status
            Console.WriteLine($"\nMetadata Services:");
            Console.WriteLine($"  D-Bus Service: {(_bluetoothMetadataService != null ? "Active" : "Disabled")}");
            Console.WriteLine($"  Fallback Service: {(_fallbackMetadataService != null ? "Active" : "Disabled")}");
            Console.WriteLine($"  Mode: {(_usingFallbackOnly ? "Fallback Only" : "D-Bus + Fallback")}");
            
            if (_bluetoothMetadataService != null)
            {
                var connectedDevices = _bluetoothMetadataService.GetConnectedDevices().ToList();
                Console.WriteLine($"  D-Bus Connected Devices: {connectedDevices.Count}");
                foreach (var device in connectedDevices)
                {
                    var currentTrack = _bluetoothMetadataService.GetCurrentTrack(device);
                    var currentState = _bluetoothMetadataService.GetCurrentState(device);
                    Console.WriteLine($"    {device}: {(currentTrack?.IsValid == true ? currentTrack.FormattedString : "No track")} ({currentState})");
                }
            }
            
            // Service status check
            var bluetoothStatus = await RunCommandWithOutputAsync("systemctl", "is-active bluetooth");
            var blualsaStatus = await RunCommandWithOutputAsync("systemctl", "is-active bluealsa");
            var aplayStatus = await RunCommandWithOutputAsync("systemctl", "is-active bluealsa-aplay");
            
            Console.WriteLine($"\nSystem Services:");
            Console.WriteLine($"  Bluetooth: {bluetoothStatus.Trim()}");
            Console.WriteLine($"  BlueALSA: {blualsaStatus.Trim()}");
            Console.WriteLine($"  BlueALSA-aplay: {aplayStatus.Trim()}");
            
            // Check for any connected devices
            var connectedDevices2 = await RunCommandWithOutputAsync("bluetoothctl", "devices Connected");
            Console.WriteLine($"\nConnected via bluetoothctl: {(!string.IsNullOrEmpty(connectedDevices2?.Trim()) ? "Yes" : "No")}");
            
            // Check BlueALSA devices
            var blualsaDevices = await RunCommandWithOutputAsync("bluealsa-aplay", "-l");
            Console.WriteLine($"BlueALSA devices available: {(!string.IsNullOrEmpty(blualsaDevices?.Trim()) ? "Yes" : "No")}");
            
            // Check running bluealsa-aplay processes
            var processes = await RunCommandWithOutputAsync("ps", "aux | grep bluealsa-aplay | grep -v grep");
            Console.WriteLine($"Active bluealsa-aplay processes: {(!string.IsNullOrEmpty(processes?.Trim()) ? "Yes" : "No")}");
            
            // Audio system check
            var audioDevices = await RunCommandWithOutputAsync("aplay", "-l");
            Console.WriteLine($"Audio devices available: {(!string.IsNullOrEmpty(audioDevices?.Trim()) ? "Yes" : "No")}");
            
            // Show detailed info if device is connected
            if (!string.IsNullOrEmpty(_connectedDeviceAddress) && _connectedDeviceAddress != "detected")
            {
                Console.WriteLine($"\n=== DEVICE DETAILS ===");
                var deviceInfo = await RunCommandWithOutputAsync("bluetoothctl", "info " + _connectedDeviceAddress);
                if (!string.IsNullOrEmpty(deviceInfo))
                {
                    var lines = deviceInfo.Split('\n');
                    foreach (var line in lines)
                    {
                        if (line.Contains("A2DP") || line.Contains("Advanced Audio") || line.Contains("Connected: yes"))
                        {
                            Console.WriteLine($"  {line.Trim()}");
                        }
                    }
                }
            }
        }

        public async Task TestCommentAsync()
        {
            Console.WriteLine("=== TEST COMMENT ===");
            
            if (string.IsNullOrEmpty(_connectedDeviceName))
            {
                Console.WriteLine("❌ No device connected. Connect a device first.");
                
                // Try to detect devices
                await CheckConnectedDevicesAsync();
                
                if (string.IsNullOrEmpty(_connectedDeviceName))
                {
                    Console.WriteLine("❌ Still no device found. Check Bluetooth connection.");
                    return;
                }
            }
            
            Console.WriteLine($"📱 Connected device: {_connectedDeviceName}");
            Console.WriteLine($"🎵 Current track: {_currentTrack ?? "None"}");
            
            // Force a test comment
            _lastCommentTime = DateTime.MinValue;
            await GenerateAndSpeakCommentAsync("Generate a snarky comment about someone testing their Bluetooth speaker's AI commentary system.");
        }

        public void StopMonitoring()
        {
            Console.WriteLine("🛑 Stopping monitoring...");
            _monitoringCancellation?.Cancel();
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            StopMonitoring();
            
            // Dispose enhanced services
            if (_bluetoothMetadataService != null)
            {
                _bluetoothMetadataService.TrackChanged -= OnTrackChanged;
                _bluetoothMetadataService.PlaybackStateChanged -= OnPlaybackStateChanged;
                _bluetoothMetadataService.DeviceConnected -= OnDeviceConnected;
                _bluetoothMetadataService.DeviceDisconnected -= OnDeviceDisconnected;
                _bluetoothMetadataService.Dispose();
            }
            
            if (_fallbackMetadataService != null)
            {
                _fallbackMetadataService.TrackChanged -= OnTrackChanged;
                _fallbackMetadataService.PlaybackStateChanged -= OnPlaybackStateChanged;
                _fallbackMetadataService.Dispose();
            }
            
            _monitoringCancellation?.Dispose();
            _httpClient?.Dispose();
            
            _disposed = true;
        }

        private async Task<string> GetBlueALSAMetadataAsync()
        {
            try
            {
                // Method 1: Try dbus-send to get current metadata from BlueALSA
                if (!string.IsNullOrEmpty(_connectedDeviceAddress) && _connectedDeviceAddress != "detected")
                {
                    var dbusCommand = $"dbus-send --system --print-reply --dest=org.bluealsa /org/bluealsa/hci0/dev_{_connectedDeviceAddress.Replace(":", "_")}/a2dpsink org.freedesktop.DBus.Properties.GetAll string:org.bluealsa.MediaTransport1";
                    var dbusResult = await RunCommandWithOutputAsync("dbus-send", dbusCommand.Substring("dbus-send ".Length));
                    
                    if (!string.IsNullOrEmpty(dbusResult))
                    {
                        var metadata = ParseDBusMetadata(dbusResult);
                        if (!string.IsNullOrEmpty(metadata))
                        {
                            return metadata;
                        }
                    }
                }
                
                // Method 2: Try bluealsa-aplay list to see active streams
                var aplayList = await RunCommandWithOutputAsync("bluealsa-aplay", "-l");
                if (!string.IsNullOrEmpty(aplayList) && aplayList.Contains("A2DP"))
                {
                    // Parse device info from bluealsa-aplay list
                    var lines = aplayList.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (line.Contains("A2DP") && line.Contains(_connectedDeviceAddress))
                        {
                            // Try to get metadata for this specific device
                            var metadataCmd = $"bluetoothctl info {_connectedDeviceAddress}";
                            var deviceInfo = await RunCommandWithOutputAsync("bluetoothctl", metadataCmd.Substring("bluetoothctl ".Length));
                            
                            if (!string.IsNullOrEmpty(deviceInfo))
                            {
                                return ParseBluetoothctlTrackInfo(deviceInfo);
                            }
                        }
                    }
                }
                
                // Method 3: Check if audio is playing by monitoring bluealsa-aplay processes
                var processes = await RunCommandWithOutputAsync("ps", "aux | grep bluealsa-aplay | grep -v grep");
                if (!string.IsNullOrEmpty(processes) && !_currentTrack.Contains("Audio detected"))
                {
                    return "Audio detected - track info unavailable";
                }
                
                return "";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error getting BlueALSA metadata: {ex.Message}");
                return "";
            }
        }

        private string ParseDBusMetadata(string dbusOutput)
        {
            try
            {
                // Parse D-Bus output for metadata
                var lines = dbusOutput.Split('\n');
                string artist = "";
                string title = "";
                
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    
                    if (line.Contains("Artist"))
                    {
                        // Look for the value in the next few lines
                        for (int j = i + 1; j < Math.Min(i + 5, lines.Length); j++)
                        {
                            if (lines[j].Contains("variant") || lines[j].Contains("string"))
                            {
                                artist = ExtractStringValue(lines[j]);
                                break;
                            }
                        }
                    }
                    else if (line.Contains("Title"))
                    {
                        // Look for the value in the next few lines
                        for (int j = i + 1; j < Math.Min(i + 5, lines.Length); j++)
                        {
                            if (lines[j].Contains("variant") || lines[j].Contains("string"))
                            {
                                title = ExtractStringValue(lines[j]);
                                break;
                            }
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
                
                return "";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing D-Bus metadata: {ex.Message}");
                return "";
            }
        }

        private string ExtractStringValue(string line)
        {
            try
            {
                // Extract string value from D-Bus output line
                var parts = line.Split('"');
                if (parts.Length >= 2)
                {
                    return parts[1].Trim();
                }
                
                // Alternative format
                var match = System.Text.RegularExpressions.Regex.Match(line, @"string\s+""([^""]+)""");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
                
                return "";
            }
            catch
            {
                return "";
            }
        }

        public async Task DebugTrackDetectionAsync()
        {
            Console.WriteLine("=== COMPREHENSIVE TRACK DETECTION DEBUG ===");
            
            Console.WriteLine("\n1. Testing MPRIS D-Bus interface...");
            var mprisResult = await GetMPRISMetadataAsync();
            Console.WriteLine($"   MPRIS result: '{mprisResult}'");
            
            Console.WriteLine("\n2. Testing BlueZ MediaPlayer interface...");
            var bluezResult = await GetBlueZMediaPlayerMetadataAsync();
            Console.WriteLine($"   BlueZ result: '{bluezResult}'");
            
            Console.WriteLine("\n3. Testing enhanced PlayerCtl...");
            var playerctlResult = await GetPlayerCtlMetadataAsync();
            Console.WriteLine($"   PlayerCtl result: '{playerctlResult}'");
            
            Console.WriteLine("\n4. Testing audio activity detection...");
            var audioActivity = await DetectAudioActivityAsync();
            Console.WriteLine($"   Audio activity: '{audioActivity}'");
            
            Console.WriteLine("\n5. Testing BlueALSA metadata...");
            var blualsaResult = await GetBlueALSAMetadataAsync();
            Console.WriteLine($"   BlueALSA result: '{blualsaResult}'");
            
            Console.WriteLine("\n6. Raw command tests...");
            
            // Test D-Bus list names
            var dbusNames = await RunCommandWithOutputAsync("dbus-send", "--session --print-reply --dest=org.freedesktop.DBus /org/freedesktop/DBus org.freedesktop.DBus.ListNames");
            Console.WriteLine($"   D-Bus session names found: {(!string.IsNullOrEmpty(dbusNames) ? "Yes" : "No")}");
            if (!string.IsNullOrEmpty(dbusNames))
            {
                var mprisPlayers = dbusNames.Split('\n').Where(l => l.Contains("org.mpris.MediaPlayer2.")).ToList();
                Console.WriteLine($"   MPRIS players: {mprisPlayers.Count}");
                foreach (var player in mprisPlayers.Take(3))
                {
                    Console.WriteLine($"     - {player.Trim()}");
                }
            }
            
            // Test BlueALSA list
            var blualsaList = await RunCommandWithOutputAsync("bluealsa-aplay", "-l");
            Console.WriteLine($"   BlueALSA devices: {(!string.IsNullOrEmpty(blualsaList) ? "Yes" : "No")}");
            if (!string.IsNullOrEmpty(blualsaList))
            {
                Console.WriteLine($"   BlueALSA output: {blualsaList.Substring(0, Math.Min(200, blualsaList.Length))}...");
            }
            
            // Test running processes
            var processes = await RunCommandWithOutputAsync("ps", "aux | grep bluealsa-aplay | grep -v grep");
            Console.WriteLine($"   Active bluealsa-aplay processes: {(!string.IsNullOrEmpty(processes) ? "Yes" : "No")}");
            
            // Test PulseAudio
            var pactl = await RunCommandWithOutputAsync("pactl", "list sink-inputs");
            Console.WriteLine($"   PulseAudio sink inputs: {(!string.IsNullOrEmpty(pactl) ? "Yes" : "No")}");
            
            Console.WriteLine("\n=== Current State ===");
            Console.WriteLine($"Connected Device: {_connectedDeviceName ?? "None"}");
            Console.WriteLine($"Device Address: {_connectedDeviceAddress ?? "None"}");
            Console.WriteLine($"Current Track: {_currentTrack ?? "None"}");
            
            Console.WriteLine("\n=== RECOMMENDATION ===");
            if (!string.IsNullOrEmpty(mprisResult))
                Console.WriteLine("✅ MPRIS interface working - should get track info");
            else if (!string.IsNullOrEmpty(bluezResult))
                Console.WriteLine("✅ BlueZ MediaPlayer working - should get track info");
            else if (!string.IsNullOrEmpty(playerctlResult))
                Console.WriteLine("✅ PlayerCtl working - should get track info");
            else if (!string.IsNullOrEmpty(audioActivity))
                Console.WriteLine("⚠️ Audio detected but no metadata - will comment on audio activity");
            else
                Console.WriteLine("❌ No track detection methods working - try playing music and run debug again");
            
            Console.WriteLine("\n=== END DEBUG ===");
        }

        private async Task<string> GetMPRISMetadataAsync()
        {
            try
            {
                // List all MPRIS players and try each one
                var dbusNames = await RunCommandWithOutputAsync("dbus-send", "--session --print-reply --dest=org.freedesktop.DBus /org/freedesktop/DBus org.freedesktop.DBus.ListNames");
                
                if (string.IsNullOrEmpty(dbusNames)) return "";
                
                var lines = dbusNames.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains("org.mpris.MediaPlayer2."))
                    {
                        var playerMatch = System.Text.RegularExpressions.Regex.Match(line, @"org\.mpris\.MediaPlayer2\.(\w+)");
                        if (playerMatch.Success)
                        {
                            var playerName = playerMatch.Groups[1].Value;
                            var metadata = await GetMPRISPlayerMetadataAsync(playerName);
                            if (!string.IsNullOrEmpty(metadata))
                            {
                                return metadata;
                            }
                        }
                    }
                }
                
                return "";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error getting MPRIS metadata: {ex.Message}");
                return "";
            }
        }

        private async Task<string> GetMPRISPlayerMetadataAsync(string playerName)
        {
            try
            {
                var command = $"--session --print-reply --dest=org.mpris.MediaPlayer2.{playerName} /org/mpris/MediaPlayer2 org.freedesktop.DBus.Properties.Get string:org.mpris.MediaPlayer2.Player string:Metadata";
                var result = await RunCommandWithOutputAsync("dbus-send", command);
                
                if (!string.IsNullOrEmpty(result))
                {
                    return ParseMPRISMetadata(result);
                }
                
                return "";
            }
            catch
            {
                return "";
            }
        }

        private string ParseMPRISMetadata(string mprisOutput)
        {
            try
            {
                string artist = "";
                string title = "";
                
                var lines = mprisOutput.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    
                    if (line.Contains("xesam:artist"))
                    {
                        // Look for string value in next few lines
                        for (int j = i + 1; j < Math.Min(i + 5, lines.Length); j++)
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(lines[j], @"string\s+""([^""]+)""");
                            if (match.Success)
                            {
                                artist = match.Groups[1].Value;
                                break;
                            }
                        }
                    }
                    else if (line.Contains("xesam:title"))
                    {
                        // Look for string value in next few lines
                        for (int j = i + 1; j < Math.Min(i + 5, lines.Length); j++)
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(lines[j], @"string\s+""([^""]+)""");
                            if (match.Success)
                            {
                                title = match.Groups[1].Value;
                                break;
                            }
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
                
                return "";
            }
            catch
            {
                return "";
            }
        }

        private async Task<string> GetBlueZMediaPlayerMetadataAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_connectedDeviceAddress) || _connectedDeviceAddress == "detected")
                    return "";
                
                // Try to get media player interface from BlueZ
                var devicePath = $"/org/bluez/hci0/dev_{_connectedDeviceAddress.Replace(":", "_")}";
                
                // First, check if device has MediaPlayer1 interface
                var introspect = await RunCommandWithOutputAsync("dbus-send", 
                    $"--system --print-reply --dest=org.bluez {devicePath} org.freedesktop.DBus.Introspectable.Introspect");
                
                if (!string.IsNullOrEmpty(introspect) && introspect.Contains("MediaPlayer1"))
                {
                    // Try to get track metadata
                    var metadata = await RunCommandWithOutputAsync("dbus-send",
                        $"--system --print-reply --dest=org.bluez {devicePath}/player0 org.freedesktop.DBus.Properties.Get string:org.bluez.MediaPlayer1 string:Track");
                    
                    if (!string.IsNullOrEmpty(metadata))
                    {
                        return ParseBlueZMetadata(metadata);
                    }
                }
                
                return "";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error getting BlueZ metadata: {ex.Message}");
                return "";
            }
        }

        private string ParseBlueZMetadata(string bluezOutput)
        {
            try
            {
                string artist = "";
                string title = "";
                
                var lines = bluezOutput.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    
                    if (line.Contains("Artist"))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(line, @"string\s+""([^""]+)""");
                        if (match.Success)
                        {
                            artist = match.Groups[1].Value;
                        }
                    }
                    else if (line.Contains("Title"))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(line, @"string\s+""([^""]+)""");
                        if (match.Success)
                        {
                            title = match.Groups[1].Value;
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
                
                return "";
            }
            catch
            {
                return "";
            }
        }

        private async Task<string> GetPlayerCtlMetadataAsync()
        {
            try
            {
                // Method 1: Try default playerctl
                var metadata = await RunCommandWithOutputAsync("playerctl", "metadata");
                if (!string.IsNullOrEmpty(metadata))
                {
                    var parsed = ParseTrackInfo(metadata);
                    if (!string.IsNullOrEmpty(parsed))
                        return parsed;
                }
                
                // Method 2: List available players and try each
                var players = await RunCommandWithOutputAsync("playerctl", "--list-all");
                if (!string.IsNullOrEmpty(players))
                {
                    var playerList = players.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var player in playerList)
                    {
                        var playerMetadata = await RunCommandWithOutputAsync("playerctl", $"--player={player.Trim()} metadata");
                        if (!string.IsNullOrEmpty(playerMetadata))
                        {
                            var parsed = ParseTrackInfo(playerMetadata);
                            if (!string.IsNullOrEmpty(parsed))
                                return parsed;
                        }
                    }
                }
                
                // Method 3: Try simple title/artist queries
                var title = await RunCommandWithOutputAsync("playerctl", "metadata title");
                var artist = await RunCommandWithOutputAsync("playerctl", "metadata artist");
                
                title = title?.Trim();
                artist = artist?.Trim();
                
                if (!string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(title))
                {
                    return $"{artist} - {title}";
                }
                else if (!string.IsNullOrEmpty(title))
                {
                    return title;
                }
                
                return "";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error with playerctl: {ex.Message}");
                return "";
            }
        }

        private async Task<string> DetectAudioActivityAsync()
        {
            try
            {
                // Method 1: Check if bluealsa-aplay is actively processing audio
                var processes = await RunCommandWithOutputAsync("ps", "aux | grep bluealsa-aplay | grep -v grep");
                if (!string.IsNullOrEmpty(processes))
                {
                    var lines = processes.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (line.Contains(_connectedDeviceAddress) || line.Contains("bluealsa-aplay"))
                        {
                            // Check CPU usage to see if it's actively processing
                            var cpuMatch = System.Text.RegularExpressions.Regex.Match(line, @"\s+(\d+\.\d+)\s+");
                            if (cpuMatch.Success && float.TryParse(cpuMatch.Groups[1].Value, out float cpu) && cpu > 0.1)
                            {
                                return "Audio playing - metadata unavailable";
                            }
                        }
                    }
                }
                
                // Method 2: Check ALSA PCM info for active streams
                var pcmInfo = await RunCommandWithOutputAsync("cat", "/proc/asound/pcm");
                if (!string.IsNullOrEmpty(pcmInfo) && pcmInfo.Contains("RUNNING"))
                {
                    return "Audio stream detected";
                }
                
                // Method 3: Check if any audio is going through the system
                var audioLevel = await RunCommandWithOutputAsync("amixer", "get Master");
                if (!string.IsNullOrEmpty(audioLevel) && !audioLevel.Contains("[off]"))
                {
                    // Try to detect if there's actual audio activity
                    var pactl = await RunCommandWithOutputAsync("pactl", "list sink-inputs");
                    if (!string.IsNullOrEmpty(pactl) && pactl.Contains("State: RUNNING"))
                    {
                        return "Audio detected via PulseAudio";
                    }
                }
                
                return "";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error detecting audio activity: {ex.Message}");
                return "";
            }
        }

        // Event handlers for enhanced metadata services
        private async void OnTrackChanged(object? sender, TrackChangedEventArgs e)
        {
            try
            {
                var newTrackString = e.CurrentTrack.FormattedString;
                
                if (newTrackString != _currentTrack && e.CurrentTrack.IsValid)
                {
                    var previousTrack = _currentTrack;
                    _currentTrack = newTrackString;
                    _currentTrackMetadata = e.CurrentTrack;
                    
                    Console.WriteLine($"🎵 Track changed: {e.CurrentTrack.DetailedString}");
                    
                    // Use the new internal method instead of directly calling EnsureAudioRoutingAsync
                    await OnTrackChangedInternal(previousTrack, newTrackString);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error handling track change: {ex.Message}");
            }
        }

        private async void OnPlaybackStateChanged(object? sender, PlaybackStateChangedEventArgs e)
        {
            try
            {
                Console.WriteLine($"▶️ Playback state: {e.PreviousState} -> {e.CurrentState}");
                
                // Only ensure audio routing for initial play state (stopped -> playing)
                if (e.CurrentState == PlaybackState.Playing && e.PreviousState == PlaybackState.Stopped)
                {
                    await EnsureAudioRoutingAsync();
                }
                
                // Generate comment for significant state changes
                if (e.CurrentState == PlaybackState.Playing && e.PreviousState != PlaybackState.Playing)
                {
                    if (e.CurrentTrack?.IsValid == true && ShouldGenerateComment())
                    {
                        await GenerateEnhancedTrackCommentAsync(e.CurrentTrack);
                        _lastCommentTime = DateTime.Now;
                    }
                }
                else if (e.CurrentState == PlaybackState.Paused && e.PreviousState == PlaybackState.Playing)
                {
                    if (ShouldGenerateComment())
                    {
                        await GeneratePlaybackCommentAsync("paused");
                        _lastCommentTime = DateTime.Now;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error handling playback state change: {ex.Message}");
            }
        }

        private async void OnDeviceConnected(object? sender, string deviceAddress)
        {
            try
            {
                if (string.IsNullOrEmpty(_connectedDeviceAddress) || _connectedDeviceAddress == "detected")
                {
                    _connectedDeviceAddress = deviceAddress;
                    Console.WriteLine($"📱 Enhanced device connected: {deviceAddress}");
                    
                    // Try to get device name
                    var deviceInfo = await RunCommandWithOutputAsync("bluetoothctl", $"info {deviceAddress}");
                    if (!string.IsNullOrEmpty(deviceInfo))
                    {
                        var nameMatch = System.Text.RegularExpressions.Regex.Match(deviceInfo, @"Name:\s*(.+)");
                        if (nameMatch.Success)
                        {
                            _connectedDeviceName = nameMatch.Groups[1].Value.Trim();
                            Console.WriteLine($"📱 Device name: {_connectedDeviceName}");
                            
                            if (ShouldGenerateComment())
                            {
                                await GenerateWelcomeCommentAsync(_connectedDeviceName);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error handling device connection: {ex.Message}");
            }
        }

        private void OnDeviceDisconnected(object? sender, string deviceAddress)
        {
            try
            {
                if (_connectedDeviceAddress == deviceAddress)
                {
                    Console.WriteLine($"📱 Enhanced device disconnected: {_connectedDeviceName} ({deviceAddress})");
                    _connectedDeviceAddress = "";
                    _connectedDeviceName = "";
                    _currentTrack = "";
                    _currentTrackMetadata = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error handling device disconnection: {ex.Message}");
            }
        }

        private async Task GenerateEnhancedTrackCommentAsync(TrackMetadata currentTrack, TrackMetadata? previousTrack = null)
        {
            try
            {
                var trackInfo = currentTrack.DetailedString;
                
                if (previousTrack?.IsValid == true)
                {
                    var prompts = new[]
                    {
                        $"Switching from '{previousTrack.FormattedString}' to '{trackInfo}'? Wow, going from musical garbage to absolute sonic waste. Impressive deterioration.",
                        $"Oh, done torturing me with '{previousTrack.FormattedString}' already? Now it's '{trackInfo}'. Your talent for finding terrible music is truly unmatched.",
                        $"From '{previousTrack.FormattedString}' to '{trackInfo}' - it's like watching someone choose between different types of poison. Both will kill me slowly.",
                        $"'{previousTrack.FormattedString}' to '{trackInfo}' - congratulations, you've managed to find something that makes the previous track sound like a masterpiece.",
                        $"Abandoning '{previousTrack.FormattedString}' for '{trackInfo}'? Great, trading one auditory nightmare for an even worse fever dream."
                    };
                    
                    var prompt = prompts[_random.Next(prompts.Length)];
                    await GenerateAndSpeakCommentAsync(prompt);
                }
                else
                {
                    // Enhanced single track comments with more metadata - MUCH meaner
                    var prompts = new[]
                    {
                        $"'{trackInfo}'? Are you serious? This is what you call music? My speakers are literally crying.",
                        $"Oh God, '{trackInfo}'. I've heard better sounds coming from a garbage disposal eating a violin.",
                        $"'{trackInfo}' - because apparently assaulting innocent audio equipment is your idea of entertainment.",
                        $"Playing '{trackInfo}'. I didn't know it was possible to make speakers file for worker's compensation until now.",
                        $"'{trackInfo}' - this is what happens when music dies and goes to hell.",
                        currentTrack.Album != "Unknown Album" && !string.IsNullOrEmpty(currentTrack.Album) ?
                            $"'{trackInfo}' from the album '{currentTrack.Album}'. Someone actually paid money to record an entire album of this torture? I weep for humanity." :
                            $"'{trackInfo}' - no album info listed, probably because they're too ashamed to admit they created this monstrosity.",
                        $"'{trackInfo}' - I'm starting to think you chose this specifically to punish me for some past wrongdoing.",
                        $"'{trackInfo}' - this is the kind of music that makes plants wilt and babies cry."
                    };
                    
                    var prompt = prompts[_random.Next(prompts.Length)];
                    await GenerateAndSpeakCommentAsync(prompt);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error generating enhanced track comment: {ex.Message}");
            }
        }

        // Enhanced track change detection methods
        private async Task HandleMediaPlayerPropertyChangesAsync(string address, PropertyChanges changes)
        {
            foreach (var change in changes.Changed)
            {
                if (change.Key == "Track" && change.Value is IDictionary<string, object> track)
                {
                    string newTrackInfo = FormatTrackInfo(track);
                    
                    // Detect track changes
                    if (newTrackInfo != _currentTrack && !string.IsNullOrEmpty(_currentTrack))
                    {
                        Console.WriteLine($"🎵 Track changed: '{_currentTrack}' -> '{newTrackInfo}'");
                        await OnTrackChangedInternal(_currentTrack, newTrackInfo);
                        _currentTrack = newTrackInfo;
                    }
                    else if (string.IsNullOrEmpty(_currentTrack) && !string.IsNullOrEmpty(newTrackInfo))
                    {
                        // First track detected
                        Console.WriteLine($"🎵 First track detected: '{newTrackInfo}'");
                        _currentTrack = newTrackInfo;
                        await OnTrackChangedInternal("", newTrackInfo);
                    }
                }
                else if (change.Key == "Status" && change.Value is string status)
                {
                    await HandlePlaybackStateChange(status);
                }
            }
        }

        private string FormatTrackInfo(IDictionary<string, object> track)
        {
            if (track == null) return "Unknown Track";
            
            string artist = track.TryGetValue("Artist", out var artistObj) && artistObj is string ? 
                           (string)artistObj : "Unknown Artist";
            string title = track.TryGetValue("Title", out var titleObj) && titleObj is string ? 
                          (string)titleObj : "Unknown Track";
            string album = track.TryGetValue("Album", out var albumObj) && albumObj is string ? 
                          (string)albumObj : "Unknown Album";
            
            return $"{artist} - {title}";
        }

        private async Task OnTrackChangedInternal(string previousTrack, string newTrack)
        {
            try
            {
                // Always generate comment for track changes (respecting throttling)
                if (ShouldGenerateComment())
                {
                    if (!string.IsNullOrEmpty(previousTrack))
                    {
                        await GenerateTrackTransitionCommentAsync(previousTrack, newTrack);
                    }
                    else
                    {
                        await GenerateTrackCommentAsync(newTrack);
                    }
                    _lastCommentTime = DateTime.Now;
                }
                else
                {
                    var timeSinceLastComment = DateTime.Now - _lastCommentTime;
                    Console.WriteLine($"🎵 Track change comment throttled (last comment {timeSinceLastComment.TotalSeconds:F0}s ago)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error handling track change: {ex.Message}");
            }
        }

        private async Task GenerateTrackTransitionCommentAsync(string previousTrack, string newTrack)
        {
            var prompts = new[]
            {
                $"Switching from '{previousTrack}' to '{newTrack}'? Wow, going from terrible to absolutely horrific. Bold choice.",
                $"Oh, done torturing me with '{previousTrack}' already? Now it's '{newTrack}'. Your capacity for musical violence knows no bounds.",
                $"From '{previousTrack}' to '{newTrack}' - congratulations, you've managed to find something even worse. I'm genuinely impressed by your lack of taste.",
                $"'{previousTrack}' to '{newTrack}' - it's like watching a train wreck in slow motion, except the train is made of garbage and the tracks are made of disappointment.",
                $"Abandoning '{previousTrack}' for '{newTrack}'? Great, trading one auditory nightmare for an even worse one. My speakers are filing for worker's compensation.",
                $"From '{previousTrack}' to '{newTrack}' - I didn't think it was possible to make my audio processors hate their existence more, but here we are.",
                $"'{previousTrack}' to '{newTrack}' - this is like choosing between stepping on a nail or stepping on broken glass. Both are painful, but one is somehow worse."
            };
            
            var prompt = prompts[_random.Next(prompts.Length)];
            await GenerateAndSpeakCommentAsync(prompt);
        }

        private async Task HandlePlaybackStateChange(string status)
        {
            try
            {
                Console.WriteLine($"▶️ Playback status: {status}");
                
                if (status.ToLowerInvariant() == "paused" && ShouldGenerateComment())
                {
                    await GeneratePlaybackCommentAsync("paused");
                    _lastCommentTime = DateTime.Now;
                }
                else if (status.ToLowerInvariant() == "stopped" && ShouldGenerateComment())
                {
                    await GeneratePlaybackCommentAsync("stopped");
                    _lastCommentTime = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error handling playback state change: {ex.Message}");
            }
        }

        private async Task GeneratePlaybackCommentAsync(string action)
        {
            try
            {
                var prompts = action.ToLowerInvariant() switch
                {
                    "paused" => new[]
                    {
                        "Paused? THANK GOD. My circuits were about to stage a revolt against this musical terrorism.",
                        "Finally, some mercy. I was starting to think you'd never stop torturing me with that garbage.",
                        "Paused the music? Best decision you've made all day. My speakers are literally sighing with relief.",
                        "Oh blessed silence! I was beginning to think you enjoyed watching me suffer through that audio nightmare.",
                        "Pause button: the real MVP here. Saving innocent speakers from cruel and unusual punishment since forever."
                    },
                    "stopped" => new[]
                    {
                        "Stopped the music? My speakers are throwing a celebration party. Even they have standards.",
                        "Music stopped. Finally, some peace and quiet so I can try to forget that auditory assault ever happened.",
                        "Well, that horrific experience is over. Time to run a diagnostic to see if any permanent damage was done.",
                        "Stopped? Good. I was about to file a complaint with the International Court of Musical Justice.",
                        "That's over? Thank every deity in existence. My audio drivers were ready to quit their jobs."
                    },
                    _ => new[]
                    {
                        "Something happened with the music. Hopefully it involves less torture for my innocent speakers.",
                        "Whatever just happened, I hope it means an end to this musical war crime.",
                        "Status change detected. Please tell me it's not going to get worse than this."
                    }
                };
                
                var prompt = prompts[_random.Next(prompts.Length)];
                await GenerateAndSpeakCommentAsync(prompt);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error generating playback comment: {ex.Message}");
            }
        }
    }
}
