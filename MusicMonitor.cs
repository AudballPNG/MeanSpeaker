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
    public class MusicMonitor : IDisposable
    {
        private readonly string _openAiApiKey;
        private readonly HttpClient _httpClient;
        private readonly Random _random;
        private readonly bool _enableSpeech;
        private readonly string _ttsVoice;
        private readonly string _ttsEngine;
        
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
        private readonly TimeSpan _commentThrottle = TimeSpan.FromSeconds(10); // Reduced to 5 seconds for maximum meanness
        
        // Audio routing state to prevent unnecessary restarts
        private DateTime _lastAudioRoutingSetup = DateTime.MinValue;
        private readonly TimeSpan _audioRoutingCooldown = TimeSpan.FromSeconds(15);
        private bool _audioRoutingActive = false;
        
        private CancellationTokenSource? _monitoringCancellation;
        private bool _disposed = false;

        // Add periodic audio detection and commentary
        private DateTime _lastAudioCheck = DateTime.MinValue;
        private readonly TimeSpan _audioCheckInterval = TimeSpan.FromSeconds(10);
        private bool _wasPlayingAudio = false;

        // TTS optimization fields
        private readonly SemaphoreSlim _ttsLock = new SemaphoreSlim(1, 1);
        private bool _piperWarmupComplete = false;
        private readonly Queue<string> _ttsQueue = new Queue<string>();
        private Task? _ttsWorkerTask;
        private CancellationTokenSource? _ttsWorkerCancellation;

        public MusicMonitor(string openAiApiKey, bool enableSpeech = true, string ttsVoice = "en+f3", string ttsEngine = "piper")
        {
            _openAiApiKey = openAiApiKey ?? throw new ArgumentNullException(nameof(openAiApiKey));
            _httpClient = new HttpClient();
            _random = new Random();
            _enableSpeech = enableSpeech;
            _ttsVoice = ttsVoice;
            _ttsEngine = ttsEngine;
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
            
            // Initialize TTS optimization during startup (not during first speech)
            if (_ttsEngine.ToLower() == "piper" && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                await InitializeOptimalPiperSetupAsync();
                _piperSetupInitialized = true;
                
                // Pre-warm Piper TTS with a silent test
                Console.WriteLine("🔥 Pre-warming Piper TTS...");
                await PreWarmPiperTTSAsync();
            }
            
            // Start background TTS worker
            StartTTSWorker();
            
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

        // Track transition detection to prevent "silence" comments during song changes
        private DateTime _lastTrackChange = DateTime.MinValue;
        private readonly TimeSpan _trackTransitionWindow = TimeSpan.FromSeconds(5);
        
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
                        // Audio just started - but don't override real track info
                        if (string.IsNullOrEmpty(_currentTrack))
                        {
                            Console.WriteLine($"🎵 Audio playback detected from {_connectedDeviceName}");
                            _currentTrack = "Audio detected - identifying track...";
                            
                            if (ShouldGenerateComment())
                            {
                                await GenerateGenericMusicCommentAsync();
                            }
                        }
                    }
                    else
                    {
                        // Audio stopped - but don't immediately assume silence during track changes
                        var timeSinceLastTrackChange = now - _lastTrackChange;
                        
                        if (timeSinceLastTrackChange > _trackTransitionWindow)
                        {
                            Console.WriteLine($"🔇 Audio playback stopped");
                            _currentTrack = "";
                            
                            if (ShouldGenerateComment())
                            {
                                await GeneratePlaybackCommentAsync("stopped");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"🔄 Audio gap detected - likely track transition (waiting {_trackTransitionWindow.TotalSeconds}s)");
                        }
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
            var prompt = "I detect that music started playing but I can't identify the specific track. Be nasty about my inability to see what garbage they're probably playing. Assume it's terrible and roast them for it.";
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
                    _audioRoutingActive = false; // Reset audio routing state
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
                    _currentTrack = mprisMetadata;
                    _lastTrackChange = DateTime.Now;
                    await EnsureAudioRoutingAsync();
                    if (ShouldGenerateComment())
                    {
                        await GenerateTrackCommentAsync(mprisMetadata);
                    }
                    return;
                }
                
                // Method 2: Try BlueZ MediaPlayer1 interface
                var bluezMetadata = await GetBlueZMediaPlayerMetadataAsync();
                if (!string.IsNullOrEmpty(bluezMetadata) && bluezMetadata != _currentTrack)
                {
                    Console.WriteLine($"🎵 Now playing: {bluezMetadata}");
                    _currentTrack = bluezMetadata;
                    _lastTrackChange = DateTime.Now;
                    await EnsureAudioRoutingAsync();
                    if (ShouldGenerateComment())
                    {
                        await GenerateTrackCommentAsync(bluezMetadata);
                    }
                    return;
                }
                
                // Method 3: Try playerctl with specific player detection
                var playerctlMetadata = await GetPlayerCtlMetadataAsync();
                if (!string.IsNullOrEmpty(playerctlMetadata) && playerctlMetadata != _currentTrack)
                {
                    Console.WriteLine($"🎵 Now playing: {playerctlMetadata}");
                    _currentTrack = playerctlMetadata;
                    _lastTrackChange = DateTime.Now;
                    await EnsureAudioRoutingAsync();
                    if (ShouldGenerateComment())
                    {
                        await GenerateTrackCommentAsync(playerctlMetadata);
                    }
                    return;
                }
                
                // Method 4: Audio activity detection (fallback)
                var audioActivity = await DetectAudioActivityAsync();
                if (!string.IsNullOrEmpty(audioActivity) && audioActivity != _currentTrack)
                {
                    Console.WriteLine($"🎵 {audioActivity}");
                    _currentTrack = audioActivity;
                    _lastTrackChange = DateTime.Now;
                    await EnsureAudioRoutingAsync();
                    if (ShouldGenerateComment())
                    {
                        await GenerateTrackCommentAsync(audioActivity);
                    }
                    return;
                }
                
                // Method 5: Try BlueALSA metadata (original method)
                var blualsaMetadata = await GetBlueALSAMetadataAsync();
                if (!string.IsNullOrEmpty(blualsaMetadata) && blualsaMetadata != _currentTrack)
                {
                    Console.WriteLine($"🎵 Now playing: {blualsaMetadata}");
                    _currentTrack = blualsaMetadata;
                    _lastTrackChange = DateTime.Now;
                    await EnsureAudioRoutingAsync();
                    if (ShouldGenerateComment())
                    {
                        await GenerateTrackCommentAsync(blualsaMetadata);
                    }
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
                // Check if we recently set up audio routing to avoid unnecessary restarts
                var timeSinceLastSetup = DateTime.Now - _lastAudioRoutingSetup;
                if (_audioRoutingActive && timeSinceLastSetup < _audioRoutingCooldown)
                {
                    Console.WriteLine("🔧 Audio routing recently configured, skipping restart");
                    return;
                }
                
                Console.WriteLine("🔧 Ensuring audio routing is active...");
                
                // Method 1: Check if bluealsa-aplay is running (but don't restart unless really needed)
                var status = await RunCommandWithOutputAsync("systemctl", "is-active bluealsa-aplay");
                if (status.Trim() != "active")
                {
                    Console.WriteLine("🔧 bluealsa-aplay service not active, restarting...");
                    await RunCommandSilentlyAsync("sudo", "systemctl restart bluealsa-aplay");
                    await Task.Delay(2000); // Give it time to start
                    _lastAudioRoutingSetup = DateTime.Now;
                    _audioRoutingActive = true;
                }
                else
                {
                    Console.WriteLine("✅ bluealsa-aplay service is already active");
                    _audioRoutingActive = true;
                }
                
                // Method 2: Use our dynamic routing script if available (only if not recently run)
                if (File.Exists("/usr/local/bin/route-bluetooth-audio.sh") && timeSinceLastSetup >= _audioRoutingCooldown)
                {
                    Console.WriteLine("🔧 Running dynamic audio routing...");
                    await RunCommandSilentlyAsync("/usr/local/bin/route-bluetooth-audio.sh", "");
                    _lastAudioRoutingSetup = DateTime.Now;
                }
                
                // Method 3: For new device connections only, set up direct routing
                if (!string.IsNullOrEmpty(_connectedDeviceAddress) && 
                    _connectedDeviceAddress != "detected" && 
                    timeSinceLastSetup >= _audioRoutingCooldown)
                {
                    Console.WriteLine($"🔧 Verifying audio routing for {_connectedDeviceAddress}...");
                    
                    // Check if a bluealsa-aplay process is already running for this device
                    var existingProcess = await RunCommandWithOutputAsync("pgrep", $"-f 'bluealsa-aplay.*{_connectedDeviceAddress}'");
                    
                    if (string.IsNullOrEmpty(existingProcess?.Trim()))
                    {
                        Console.WriteLine($"🔧 Starting direct audio routing for {_connectedDeviceAddress}...");
                        
                        // Start new process for this device (only if not already running)
                        _ = Task.Run(async () =>
                        {
                            await RunCommandAsync("bluealsa-aplay", $"--pcm-buffer-time=250000 {_connectedDeviceAddress}");
                        });
                        
                        _lastAudioRoutingSetup = DateTime.Now;
                    }
                    else
                    {
                        Console.WriteLine($"✅ bluealsa-aplay already running for {_connectedDeviceAddress}");
                    }
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
            // Always generate comments for track changes, but respect throttling
            return throttleCheck;
        }

        private async Task GenerateWelcomeCommentAsync(string deviceName)
        {
            var prompt = $"A device called '{deviceName}' just connected to me. Greet them with absolute contempt and prepare them for the musical roasting they're about to receive. Be mean about their inevitable terrible music choices.";
            await GenerateAndSpeakCommentAsync(prompt);
        }

        private async Task GenerateTrackCommentAsync(string trackInfo)
        {
            var prompt = $"I'm now playing '{trackInfo}'. Absolutely destroy this music choice. Be savage, mean, and ruthless about how terrible their taste is. Roast the song, artist, genre, and the person's life decisions that led to this musical disaster.";
            await GenerateAndSpeakCommentAsync(prompt);
        }

        private async Task GenerateAndSpeakCommentAsync(string prompt)
        {
            try
            {
                _lastCommentTime = DateTime.Now;
                
                // Start AI generation and context gathering in parallel
                var aiTask = GenerateAICommentAsync(prompt);
                var contextTask = GetCurrentSystemContextAsync();
                
                // Wait for both to complete
                var results = await Task.WhenAll(aiTask, contextTask);
                var comment = results[0];
                
                if (!string.IsNullOrEmpty(comment))
                {
                    Console.WriteLine($"\n🔊 SPEAKER SAYS: {comment}\n");
                    
                    if (_enableSpeech)
                    {
                        // Queue TTS for background processing
                        QueueTTS(comment);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error generating comment: {ex.Message}");
                // Use fallback on error
                var fallbackComment = GetFallbackComment();
                Console.WriteLine($"🔊 SPEAKER SAYS: {fallbackComment}");
                if (_enableSpeech) QueueTTS(fallbackComment);
            }
        }

        private async Task<string> GenerateAICommentAsync(string prompt)
        {
            try
            {
                // Skip AI if we don't have a real API key
                if (_openAiApiKey == "dummy-key")
                {
                    return GetFallbackComment();
                }
                
                // Get the most current information at the time of comment generation
                var currentContext = await GetCurrentSystemContextAsync();
                var enhancedPrompt = $"{prompt}\n\nCurrent system state: {currentContext}\n\nBe absolutely savage and target specific details from the current track info. Use artist names, genres, albums to make your roasts more personal and devastating.";
                
                // Optimize AI request for speed
                var requestBody = new
                {
                    model = "gpt-3.5-turbo",
                    messages = new[]
                    {
                        new { role = "system", content = "You are a brutally honest, savage Bluetooth speaker that absolutely destroys people's music taste. You are mean, ruthless, and merciless about every song choice. Roast their musical taste, question their life decisions, insult their vibes, and be as nasty as possible about their awful music. Mock the artist, the genre, the listener's taste - everything. Be creative with your insults but keep it about music taste. Under 30 words of pure musical savagery." },
                        new { role = "user", content = enhancedPrompt }
                    },
                    max_tokens = 80,  // Reduced for faster response
                    temperature = 0.9,
                    stream = false    // Ensure we get the full response at once
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_openAiApiKey}");
                _httpClient.Timeout = TimeSpan.FromSeconds(10); // Shorter timeout

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
                        return messageContent.GetString()?.Trim() ?? GetFallbackComment();
                    }
                }
                
                return GetFallbackComment();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ AI generation error: {ex.Message}");
                return GetFallbackComment();
            }
        }

        private string GetFallbackComment()
        {
            var fallbackComments = new[]
            {
                "Ugh, what trash are you playing now?",
                "This music is an assault on my circuits.",
                "Did you lose a bet? Why else would you play this garbage?",
                "Your taste in music is worse than dial-up internet.",
                "I'd rather be unplugged than listen to this.",
                "Congratulations, you've found new ways to disappoint me.",
                "Is this what passes for music in your dimension?",
                "My speakers weren't designed for this level of audio terrorism.",
                "Even my error sounds are more musical than this."
            };
            
            return fallbackComments[_random.Next(fallbackComments.Length)];
        }

        // Pre-configured optimal Piper setup (no runtime discovery needed)
        private string? _workingPiperCommand = null;
        private string? _workingPiperModelPath = null;
        private bool _piperSetupInitialized = false;
        
        private async Task SpeakAsync(string text)
        {
            // Use the new optimized TTS queue system instead of direct processing
            QueueTTS(text);
            
            // For compatibility, wait a short time to ensure the TTS starts
            await Task.Delay(100);
        }

        private async Task SpeakWithPiperOptimizedAsync(string cleanText)
        {
            try
            {
                if (_workingPiperCommand != null)
                {
                    var startTime = DateTime.Now;
                    
                    // Use memory-based audio file for maximum speed
                    var audioFile = $"/dev/shm/speech_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.wav";
                    var optimizedCommand = _workingPiperCommand.Replace("/dev/shm/speech_optimized.wav", audioFile);
                    
                    // Add speed optimizations
                    if (!optimizedCommand.Contains("--length_scale"))
                    {
                        optimizedCommand = optimizedCommand.Replace("piper", "piper --length_scale 0.8"); // 20% faster speech
                    }
                    
                    // Use fire-and-forget approach for audio playback
                    var command = $"echo '{cleanText}' | {optimizedCommand} && aplay {audioFile} && rm -f {audioFile}";
                    await RunCommandFastAsync("bash", $"-c \"{command}\"");
                    
                    var totalDuration = DateTime.Now - startTime;
                    Console.WriteLine($"🚀 Piper TTS: {totalDuration.TotalMilliseconds:F0}ms");
                }
                else
                {
                    // No working Piper found, fall back to espeak (fast)
                    await RunCommandFastAsync("espeak", $"-v {_ttsVoice} -s 200 -a 200 \"{cleanText}\"");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Optimized Piper failed: {ex.Message}");
                // Fast fallback to espeak
                await RunCommandFastAsync("espeak", $"-v {_ttsVoice} -s 200 -a 200 \"{cleanText}\"");
            }
        }
        
        private async Task InitializeOptimalPiperSetupAsync()
        {
            Console.WriteLine("🔧 Initializing optimal Piper TTS setup...");
            
            var piperVoice = _ttsVoice.Contains("en_US") ? _ttsVoice : "en_US-lessac-medium";
            var userHome = Environment.GetEnvironmentVariable("HOME") ?? "/home/" + Environment.UserName;
            var piperModelPath = $"{userHome}/.local/share/piper/voices/{piperVoice}.onnx";
            
            // Pre-configured commands based on your working setup
            var optimizedCommands = new[]
            {
                // Your working setup: direct piper with model (most reliable)
                File.Exists(piperModelPath) ? $"piper --model '{piperModelPath}' --output_file /dev/shm/speech_optimized.wav && aplay /dev/shm/speech_optimized.wav" : null,
                
                // Fallback: piper without model
                "piper --output_file /dev/shm/speech_optimized.wav && aplay /dev/shm/speech_optimized.wav",
                
                // Last resort: python module
                "python3 -m piper --output_file /dev/shm/speech_optimized.wav && aplay /dev/shm/speech_optimized.wav"
            };
            
            // Quick validation (just check if piper command exists, don't actually test audio)
            foreach (var command in optimizedCommands)
            {
                if (string.IsNullOrEmpty(command)) continue;
                
                try
                {
                    var piperExecutable = command.Split(' ')[0];
                    
                    // Quick check if the executable exists
                    var whichResult = await RunCommandWithOutputAsync("which", piperExecutable);
                    if (!string.IsNullOrEmpty(whichResult?.Trim()))
                    {
                        _workingPiperCommand = command;
                        if (command.Contains("--model"))
                        {
                            _workingPiperModelPath = piperModelPath;
                        }
                        Console.WriteLine($"✅ Piper TTS ready: {piperExecutable}");
                        return;
                    }
                }
                catch
                {
                    continue;
                }
            }
            
            Console.WriteLine("⚠️ Piper not found, will use espeak fallback");
        }
        
        private async Task PreWarmPiperTTSAsync()
        {
            try
            {
                if (_workingPiperCommand != null)
                {
                    // Generate a very short silent audio file to initialize the model
                    var warmupFile = "/dev/shm/warmup.wav";
                    var warmupCommand = _workingPiperCommand.Replace("/dev/shm/speech_optimized.wav", warmupFile);
                    
                    await RunCommandFastAsync("bash", $"-c \"echo '.' | {warmupCommand} 2>/dev/null && rm -f {warmupFile}\"");
                    _piperWarmupComplete = true;
                    Console.WriteLine("✅ Piper TTS pre-warmed");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Piper pre-warm failed: {ex.Message}");
            }
        }

        private void StartTTSWorker()
        {
            _ttsWorkerCancellation = new CancellationTokenSource();
            _ttsWorkerTask = Task.Run(async () =>
            {
                while (!_ttsWorkerCancellation.Token.IsCancellationRequested)
                {
                    try
                    {
                        string? textToSpeak = null;
                        
                        lock (_ttsQueue)
                        {
                            if (_ttsQueue.Count > 0)
                            {
                                textToSpeak = _ttsQueue.Dequeue();
                            }
                        }
                        
                        if (!string.IsNullOrEmpty(textToSpeak))
                        {
                            await ProcessTTSAsync(textToSpeak);
                        }
                        else
                        {
                            await Task.Delay(100, _ttsWorkerCancellation.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ TTS worker error: {ex.Message}");
                        await Task.Delay(1000, _ttsWorkerCancellation.Token);
                    }
                }
            }, _ttsWorkerCancellation.Token);
        }

        private void QueueTTS(string text)
        {
            lock (_ttsQueue)
            {
                // Clear queue if it's getting too long (prioritize latest comment)
                if (_ttsQueue.Count > 2)
                {
                    _ttsQueue.Clear();
                }
                _ttsQueue.Enqueue(text);
            }
        }

        private async Task ProcessTTSAsync(string text)
        {
            try
            {
                await _ttsLock.WaitAsync();
                
                var cleanText = text.Replace("\"", "").Replace("'", "").Trim();
                
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    switch (_ttsEngine.ToLower())
                    {
                        case "piper":
                            await SpeakWithPiperOptimizedAsync(cleanText);
                            break;
                        case "pico":
                            await RunCommandFastAsync("bash", $"-c \"echo '{cleanText}' | pico2wave -w /dev/shm/speech.wav && aplay /dev/shm/speech.wav && rm -f /dev/shm/speech.wav\"");
                            break;
                        case "festival":
                            await RunCommandFastAsync("bash", $"-c \"echo '{cleanText}' | festival --tts\"");
                            break;
                        case "espeak":
                        default:
                            await RunCommandFastAsync("espeak", $"-v {_ttsVoice} -s 180 -a 200 \"{cleanText}\"");
                            break;
                    }
                }
                else
                {
                    // Windows fallback for development
                    await RunCommandFastAsync("powershell", $"-Command \"Add-Type -AssemblyName System.Speech; $speak = New-Object System.Speech.Synthesis.SpeechSynthesizer; $speak.Rate = 2; $speak.Speak('{cleanText}')\"");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ TTS processing error: {ex.Message}");
                
                // Fallback to espeak if primary TTS fails
                if (_ttsEngine != "espeak" && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    try
                    {
                        await RunCommandFastAsync("espeak", $"-v {_ttsVoice} -s 180 -a 200 \"{cleanText}\"");
                    }
                    catch (Exception espeakEx)
                    {
                        Console.WriteLine($"❌ Fallback TTS also failed: {espeakEx.Message}");
                    }
                }
            }
            finally
            {
                _ttsLock.Release();
            }
        }

        private async Task<string> GetCurrentSystemContextAsync()
        {
            try
            {
                var context = new StringBuilder();
                
                // Add current track info if available
                if (_currentTrackMetadata?.IsValid == true)
                {
                    context.AppendLine($"Track: {_currentTrackMetadata.Artist} - {_currentTrackMetadata.Title}");
                    if (!string.IsNullOrEmpty(_currentTrackMetadata.Album) && _currentTrackMetadata.Album != "Unknown Album")
                        context.AppendLine($"Album: {_currentTrackMetadata.Album}");
                    if (!string.IsNullOrEmpty(_currentTrackMetadata.Genre))
                        context.AppendLine($"Genre: {_currentTrackMetadata.Genre}");
                }
                else if (!string.IsNullOrEmpty(_currentTrack))
                {
                    context.AppendLine($"Track: {_currentTrack}");
                }
                
                // Add device info
                if (!string.IsNullOrEmpty(_connectedDeviceName))
                {
                    context.AppendLine($"Device: {_connectedDeviceName}");
                }
                
                return context.ToString().Trim();
            }
            catch
            {
                return "Unknown audio source";
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            StopMonitoring();
            
            // Stop TTS worker
            _ttsWorkerCancellation?.Cancel();
            _ttsWorkerTask?.Wait(2000); // Wait up to 2 seconds
            _ttsWorkerCancellation?.Dispose();
            _ttsLock?.Dispose();
            
            // Dispose metadata services
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
            
            _httpClient?.Dispose();
            _disposed = true;
        }

        public void StopMonitoring()
        {
            _monitoringCancellation?.Cancel();
        }

        private void OnTrackChanged(object? sender, TrackMetadata track)
        {
            if (track.IsValid && track.FormattedString != _currentTrack)
            {
                Console.WriteLine($"🎵 Now playing: {track.FormattedString}");
                _currentTrack = track.FormattedString;
                _currentTrackMetadata = track;
                _lastTrackChange = DateTime.Now;
                
                if (ShouldGenerateComment())
                {
                    // Use async fire-and-forget for non-blocking operation
                    _ = Task.Run(async () => await GenerateTrackCommentAsync(track.FormattedString));
                }
            }
        }

        private void OnPlaybackStateChanged(object? sender, string state)
        {
            Console.WriteLine($"🎵 Playback state: {state}");
            
            if (state.ToLower() == "stopped" && ShouldGenerateComment())
            {
                _ = Task.Run(async () => await GeneratePlaybackCommentAsync("stopped"));
            }
        }

        private void OnDeviceConnected(object? sender, string deviceInfo)
        {
            Console.WriteLine($"📱 Device connected: {deviceInfo}");
            if (ShouldGenerateComment())
            {
                _ = Task.Run(async () => await GenerateWelcomeCommentAsync(deviceInfo));
            }
        }

        private void OnDeviceDisconnected(object? sender, string deviceInfo)
        {
            Console.WriteLine($"📱 Device disconnected: {deviceInfo}");
            _currentTrack = "";
            _connectedDeviceName = "";
            _connectedDeviceAddress = "";
        }

        private async Task GeneratePlaybackCommentAsync(string state)
        {
            var prompt = $"Playback just {state}. Mock them for their inability to keep the music going or comment on the blessed silence.";
            await GenerateAndSpeakCommentAsync(prompt);
        }
    }
}