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
        
        // Local AI service for offline commentary
        private LocalAIService? _localAI;
        
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

        public MusicMonitor(string openAiApiKey, bool enableSpeech = true, string ttsVoice = "en+f3", string ttsEngine = "piper")
        {
            _openAiApiKey = openAiApiKey ?? throw new ArgumentNullException(nameof(openAiApiKey));
            _httpClient = new HttpClient();
            _random = new Random();
            _enableSpeech = enableSpeech;
            _ttsVoice = ttsVoice;
            _ttsEngine = ttsEngine;
            
            // Initialize local AI service
            _localAI = new LocalAIService();
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
            }
            
            // Initialize local AI service
            Console.WriteLine("🤖 Initializing local AI service...");
            if (_localAI != null)
            {
                var isAvailable = await _localAI.IsAvailableAsync();
                if (isAvailable)
                {
                    var modelLoaded = await _localAI.EnsureModelIsLoadedAsync();
                    if (modelLoaded)
                    {
                        Console.WriteLine("✅ Local AI (Phi-3 Mini) ready for snarky commentary!");
                    }
                    else
                    {
                        Console.WriteLine("⚠️ Local AI model loading failed, using fallback responses");
                    }
                }
                else
                {
                    Console.WriteLine("⚠️ Ollama not available, using fallback responses");
                    Console.WriteLine("💡 Install Ollama and run 'ollama pull phi3:mini' for local AI");
                }
            }
            
            Console.WriteLine("\u2705 Enhanced Bluetooth Speaker initialized and ready!");
        }

        // Announce readiness via TTS (or console when TTS disabled)
        public async Task AnnounceReadyAsync()
        {
            try
            {
                var msg = "Ready for your device to connect";
                Console.WriteLine("\ud83d\udd0a " + msg);
                if (_enableSpeech)
                {
                    await SpeakAsync(msg);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("\u26a0\ufe0f Ready announcement failed: " + ex.Message);
            }
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
                
                // Get the most current information at the time of comment generation
                var currentContext = await GetCurrentSystemContextAsync();
                var enhancedPrompt = $"{prompt}\n\nCurrent system state: {currentContext}\n\nBe snarky and sarcastic about this specific track. Use artist names, song titles to make your comments more targeted and witty.";
                
                string comment;
                
                // Try local AI first, fallback to simple responses if needed
                if (_localAI != null)
                {
                    comment = await _localAI.GenerateCommentAsync(enhancedPrompt);
                }
                else
                {
                    // Fallback responses when no AI available
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
                    
                    comment = fallbackComments[_random.Next(fallbackComments.Length)];
                    Console.WriteLine($"� Fallback: {comment}");
                }
                
                Console.WriteLine($"\n🔊 SPEAKER SAYS: {comment}\n");
                
                if (_enableSpeech)
                {
                    await SpeakAsync(comment);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error generating comment: {ex.Message}");
                // Use fallback on error
                var meanFallbacks = new[]
                {
                    "Something went wrong, but your music taste is still worse!",
                    "My error handler has better taste than you!",
                    "Even broken, I know your music is garbage!",
                    "Technical difficulties can't save you from this musical disaster!"
                };
                var comment = meanFallbacks[_random.Next(meanFallbacks.Length)];
                Console.WriteLine($"🔊 SPEAKER SAYS: {comment}");
                if (_enableSpeech) await SpeakAsync(comment);
            }
        }

        // Pre-configured optimal Piper setup (no runtime discovery needed)
        private string? _workingPiperCommand = null;
        private string? _workingPiperModelPath = null;
        private bool _piperSetupInitialized = false;
        
        private async Task SpeakAsync(string text)
        {
            try
            {
                var cleanText = text.Replace("\"", "").Replace("'", "").Trim();
                
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    switch (_ttsEngine.ToLower())
                    {
                        case "piper":
                            await SpeakWithPiperOptimizedAsync(cleanText);
                            break;
                        case "pico":
                            await RunCommandAsync("bash", $"-c \"echo '{cleanText}' | pico2wave -w /tmp/speech.wav && aplay /tmp/speech.wav\"");
                            break;
                        case "festival":
                            await RunCommandAsync("bash", $"-c \"echo '{cleanText}' | festival --tts\"");
                            break;
                        case "espeak":
                        default:
                            await RunCommandAsync("espeak", $"-v {_ttsVoice} -s 160 -a 200 \"{cleanText}\"");
                            break;
                    }
                }
                else
                {
                    // Windows fallback for development
                    await RunCommandAsync("powershell", $"-Command \"Add-Type -AssemblyName System.Speech; $speak = New-Object System.Speech.Synthesis.SpeechSynthesizer; $speak.Speak('{cleanText}')\"");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error speaking with {_ttsEngine}: {ex.Message}");
                
                // Enhanced fallback for Piper specifically
                if (_ttsEngine.ToLower() == "piper" && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    Console.WriteLine("🔄 Trying Piper fallback methods...");
                    
                    // Simple fallback attempts
                    var fallbackCommands = new[]
                    {
                        "python3 -m piper --output_file /tmp/speech_fallback.wav",
                        "/opt/piper-venv/bin/python -m piper --output_file /tmp/speech_fallback.wav",
                        "piper-tts --output_file /tmp/speech_fallback.wav"
                    };
                    
                    bool piperWorked = false;
                    var fallbackText = text.Replace("\"", "").Replace("'", "").Trim();
                    
                    foreach (var baseCmd in fallbackCommands)
                    {
                        try
                        {
                            var fullCmd = $"echo '{fallbackText}' | {baseCmd} && aplay /tmp/speech_fallback.wav";
                            await RunCommandAsync("bash", $"-c \"{fullCmd}\"");
                            piperWorked = true;
                            break;
                        }
                        catch
                        {
                            continue;
                        }
                    }
                    
                    if (piperWorked) return;
                    Console.WriteLine("❌ All Piper fallback methods failed");
                }
                
                // Fallback to espeak if the primary engine fails
                if (_ttsEngine != "espeak" && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    try
                    {
                        Console.WriteLine("🔄 Falling back to espeak...");
                        var fallbackText = text.Replace("\"", "").Replace("'", "").Trim();
                        await RunCommandAsync("espeak", $"-v {_ttsVoice} -s 160 -a 200 \"{fallbackText}\"");
                    }
                    catch (Exception espeakEx)
                    {
                        Console.WriteLine($"❌ Fallback to espeak also failed: {espeakEx.Message}");
                    }
                }
            }
        }

        private async Task SpeakWithPiperOptimizedAsync(string cleanText)
        {
            try
            {
                // Initialize optimal setup once at startup (not during first TTS call)
                if (!_piperSetupInitialized)
                {
                    await InitializeOptimalPiperSetupAsync();
                    _piperSetupInitialized = true;
                }
                
                if (_workingPiperCommand != null)
                {
                    var startTime = DateTime.Now;
                    
                    // Use optimized file-based approach (streaming has broken pipe issues)
                    // Use RAM disk for maximum speed
                    var ramDiskFile = "/dev/shm/speech_optimized.wav";
                    var optimizedCommand = _workingPiperCommand.Replace("/tmp/speech.wav", ramDiskFile);
                    
                    // Ensure the command uses reliable file output (not streaming)
                    if (optimizedCommand.Contains("--output_file -"))
                    {
                        optimizedCommand = optimizedCommand.Replace("--output_file - | aplay -", $"--output_file {ramDiskFile} && aplay {ramDiskFile}");
                    }
                    
                    await RunCommandFastAsync("bash", $"-c \"echo '{cleanText}' | {optimizedCommand} && rm -f {ramDiskFile}\"");
                    
                    var totalDuration = DateTime.Now - startTime;
                    Console.WriteLine($"🚀 Piper TTS (optimized): {totalDuration.TotalMilliseconds:F0}ms");
                }
                else
                {
                    // No working Piper found, fall back to espeak
                    Console.WriteLine("⚠️ No working Piper command found, falling back to espeak");
                    await RunCommandAsync("espeak", $"-v {_ttsVoice} -s 160 -a 200 \"{cleanText}\"");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Optimized Piper failed: {ex.Message}");
                // Fall back to espeak
                try
                {
                    await RunCommandAsync("espeak", $"-v {_ttsVoice} -s 160 -a 200 \"{cleanText}\"");
                }
                catch (Exception espeakEx)
                {
                    Console.WriteLine($"❌ Espeak fallback also failed: {espeakEx.Message}");
                }
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
        
        // Faster version of RunCommandAsync that doesn't wait for output reading
        private async Task RunCommandFastAsync(string command, string arguments)
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
                        RedirectStandardOutput = false, // Don't redirect to avoid buffer overhead
                        RedirectStandardError = false,  // Don't redirect to avoid buffer overhead
                        CreateNoWindow = true
                    }
                };
                
                process.Start();
                await process.WaitForExitAsync();
                
                // Don't report broken pipe errors as failures - they're expected with audio streaming
                if (process.ExitCode != 0 && process.ExitCode != 141) // 141 = SIGPIPE
                {
                    Console.WriteLine($"⚠️ Fast command failed: {command} {arguments} (exit code: {process.ExitCode})");
                }
            }
            catch (Exception ex)
            {
                // Don't log broken pipe errors - they're normal with audio pipelines
                if (!ex.Message.Contains("Broken pipe"))
                {
                    Console.WriteLine($"❌ Fast command exception: {command} {arguments} - {ex.Message}");
                }
                // Don't re-throw for TTS operations
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
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                
                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                // Log errors for debugging TTS issues
                if (process.ExitCode != 0)
                {
                    Console.WriteLine($"⚠️ Command failed: {command} {arguments}");
                    if (!string.IsNullOrEmpty(error))
                        Console.WriteLine($"   Error: {error.Trim()}");
                    if (!string.IsNullOrEmpty(output))
                        Console.WriteLine($"   Output: {output.Trim()}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Command exception: {command} {arguments} - {ex.Message}");
                throw;
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
            
            // Force a test comment with real-time context
            _lastCommentTime = DateTime.MinValue;
            await GenerateAndSpeakCommentAsync("Someone is testing their Bluetooth speaker's AI commentary system. Be absolutely savage about their pathetic need to test me instead of just accepting that their music taste is garbage. Mock them ruthlessly for wanting validation from a speaker.");
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
            _localAI?.Dispose();
            
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
            
            // First check enhanced metadata services
            Console.WriteLine("\n🔍 Enhanced Metadata Services...");
            bool hasWorkingEnhancedService = false;
            
            if (_bluetoothMetadataService != null)
            {
                Console.WriteLine("   ✅ D-Bus Service: Active");
                var connectedDevices = _bluetoothMetadataService.GetConnectedDevices().ToList();
                Console.WriteLine($"   📱 D-Bus connected devices: {connectedDevices.Count}");
                
                foreach (var device in connectedDevices)
                {
                    var currentTrack = _bluetoothMetadataService.GetCurrentTrack(device);
                    var currentState = _bluetoothMetadataService.GetCurrentState(device);
                    Console.WriteLine($"     📍 {device}: {(currentTrack?.IsValid == true ? currentTrack.FormattedString : "No track")} ({currentState})");
                    
                    if (currentTrack?.IsValid == true)
                    {
                        hasWorkingEnhancedService = true;
                    }
                }
                
                // Try to get any current track
                var anyTrack = _bluetoothMetadataService.GetAnyCurrentTrack();
                if (anyTrack?.IsValid == true)
                {
                    Console.WriteLine($"   🎵 D-Bus active track: '{anyTrack.FormattedString}'");
                    Console.WriteLine($"   📄 Track details: {anyTrack.DetailedString}");
                    hasWorkingEnhancedService = true;
                }
                else
                {
                    Console.WriteLine("   ❌ D-Bus service has no active tracks");
                }
            }
            else
            {
                Console.WriteLine("   ❌ D-Bus Service: Disabled");
            }
            
            if (_fallbackMetadataService != null)
            {
                Console.WriteLine("   ✅ Fallback Service: Active (event-driven polling)");
            }
            else
            {
                Console.WriteLine("   ❌ Fallback Service: Disabled");
            }
            
            if (hasWorkingEnhancedService)
            {
                Console.WriteLine("   ✨ FINDING: Enhanced services are working and providing track data!");
                Console.WriteLine("   💡 This explains why you have current track info despite legacy methods failing.");
            }
            
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
            
            Console.WriteLine("\n6. Testing enhanced audio routing check...");
            var audioIsPlaying = await IsAudioCurrentlyPlayingAsync();
            Console.WriteLine($"   Audio currently playing: {audioIsPlaying}");
            
            Console.WriteLine("\n7. Raw command tests...");
            
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
            if (!string.IsNullOrEmpty(processes))
            {
                Console.WriteLine($"   Process details: {processes.Substring(0, Math.Min(150, processes.Length))}...");
            }
            
            // Test PulseAudio
            var pactl = await RunCommandWithOutputAsync("pactl", "list sink-inputs");
            Console.WriteLine($"   PulseAudio sink inputs: {(!string.IsNullOrEmpty(pactl) ? "Yes" : "No")}");
            
            // Test bluetoothctl player info
            if (!string.IsNullOrEmpty(_connectedDeviceAddress) && _connectedDeviceAddress != "detected")
            {
                var playerInfo = await RunCommandWithOutputAsync("bluetoothctl", $"player {_connectedDeviceAddress}");
                Console.WriteLine($"   Bluetoothctl player info: {(!string.IsNullOrEmpty(playerInfo) ? "Yes" : "No")}");
                if (!string.IsNullOrEmpty(playerInfo))
                {
                    var trackInfo = ParseBluetoothctlTrackInfo(playerInfo);
                    if (!string.IsNullOrEmpty(trackInfo))
                    {
                        Console.WriteLine($"   Bluetoothctl track: '{trackInfo}'");
                    }
                }
            }
            
            Console.WriteLine("\n=== Current State ===");
            Console.WriteLine($"Connected Device: {_connectedDeviceName ?? "None"}");
            Console.WriteLine($"Device Address: {_connectedDeviceAddress ?? "None"}");
            Console.WriteLine($"Current Track: {_currentTrack ?? "None"}");
            
            if (_currentTrackMetadata?.IsValid == true)
            {
                Console.WriteLine($"Track Metadata:");
                Console.WriteLine($"  Artist: {_currentTrackMetadata.Artist}");
                Console.WriteLine($"  Title: {_currentTrackMetadata.Title}");
                Console.WriteLine($"  Album: {_currentTrackMetadata.Album}");
                if (!string.IsNullOrEmpty(_currentTrackMetadata.Genre))
                    Console.WriteLine($"  Genre: {_currentTrackMetadata.Genre}");
                if (_currentTrackMetadata.Duration > 0)
                    Console.WriteLine($"  Duration: {TimeSpan.FromMicroseconds(_currentTrackMetadata.Duration):mm\\:ss}");
            }
            
            Console.WriteLine($"Mode: {(_usingFallbackOnly ? "Fallback Only" : "D-Bus + Fallback")}");
            Console.WriteLine($"Audio routing active: {_audioRoutingActive}");
            Console.WriteLine($"Last audio routing: {_lastAudioRoutingSetup:HH:mm:ss}");
            
            Console.WriteLine("\n=== RECOMMENDATION ===");
            // Enhanced recommendations based on what's working
            if (hasWorkingEnhancedService)
            {
                Console.WriteLine("✅ SYSTEM IS WORKING CORRECTLY!");
                Console.WriteLine("📊 The enhanced D-Bus metadata service is providing real-time track information.");
                Console.WriteLine("💡 Legacy debug methods show empty because they use different detection approaches.");
                Console.WriteLine("🎯 Your system is using the modern, event-driven metadata detection successfully.");
                
                if (string.IsNullOrEmpty(mprisResult) && string.IsNullOrEmpty(bluezResult))
                {
                    Console.WriteLine("📝 Legacy methods fail because:");
                    Console.WriteLine("   - Phone may not expose MPRIS interface");
                    Console.WriteLine("   - BlueZ MediaPlayer interface varies by device");
                    Console.WriteLine("   - Enhanced service uses direct D-Bus monitoring instead");
                }
            }
            else if (!string.IsNullOrEmpty(_currentTrack) && _currentTrack != "None")
            {
                Console.WriteLine("✅ System has track information from some source");
                Console.WriteLine("💡 Check if fallback service or event-based detection is working");
            }
            else if (!string.IsNullOrEmpty(mprisResult))
            {
                Console.WriteLine("✅ MPRIS detection works - consider enabling MPRIS player on phone");
            }
            else if (!string.IsNullOrEmpty(bluezResult))
            {
                Console.WriteLine("✅ BlueZ MediaPlayer works - good for track metadata");
            }
            else if (!string.IsNullOrEmpty(playerctlResult))
            {
                Console.WriteLine("✅ PlayerCtl works - install media player daemon");
            }
            else if (!string.IsNullOrEmpty(audioActivity))
            {
                Console.WriteLine("✅ Audio activity detected - metadata may be limited");
            }
            else if (!string.IsNullOrEmpty(blualsaResult))
            {
                Console.WriteLine("✅ BlueALSA metadata available");
            }
            else if (audioIsPlaying)
            {
                Console.WriteLine("✅ Audio is playing but no metadata available");
                Console.WriteLine("💡 Try starting bluealsa-aplay manually: sudo systemctl start bluealsa-aplay");
            }
            else if (!string.IsNullOrEmpty(blualsaList) && string.IsNullOrEmpty(processes))
            {
                Console.WriteLine("⚠️ BlueALSA device available but no active processes");
                Console.WriteLine("💡 Try starting audio routing: sudo systemctl restart bluealsa-aplay");
                Console.WriteLine("💡 Or run manually: bluealsa-aplay --pcm-buffer-time=250000 " + _connectedDeviceAddress);
            }
            else
            {
                Console.WriteLine("❌ No track detection methods working - try playing music and run debug again");
                Console.WriteLine("💡 Make sure music is actually playing on your device");
                Console.WriteLine("💡 Try restarting Bluetooth services: sudo systemctl restart bluetooth bluealsa");
            }
            
            Console.WriteLine("\n=== AUDIO ROUTING SUGGESTIONS ===");
            if (!audioIsPlaying && !string.IsNullOrEmpty(_connectedDeviceAddress))
            {
                Console.WriteLine("💡 Device connected but no audio detected. Try:");
                Console.WriteLine($"   bluealsa-aplay --pcm-buffer-time=250000 {_connectedDeviceAddress}");
                Console.WriteLine("   sudo systemctl restart bluealsa-aplay");
                Console.WriteLine("   sudo systemctl restart bluealsa");
            }
            
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
                    _lastTrackChange = DateTime.Now; // Record when track changes for transition detection
                    
                    Console.WriteLine($"🎵 Track changed: {e.CurrentTrack.DetailedString}");
                    
                    // Only set up audio routing if it's been a while since last setup
                    await EnsureAudioRoutingAsync();
                    
                    // ALWAYS generate comment for track changes (respecting throttling)
                    if (ShouldGenerateComment())
                    {
                        // Generate context-aware comment with current real-time information
                        await GenerateEnhancedTrackCommentAsync(e.CurrentTrack, e.PreviousTrack);
                    }
                    else
                    {
                        Console.WriteLine($"🎵 Track change comment throttled (last comment {DateTime.Now - _lastCommentTime:mm\\:ss} ago)");
                    }
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
                
                // Only ensure audio routing for significant state changes (like first play)
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
                    }
                }
                else if (e.CurrentState == PlaybackState.Paused && e.PreviousState == PlaybackState.Playing)
                {
                    if (ShouldGenerateComment())
                    {
                        await GeneratePlaybackCommentAsync("paused");
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
                string prompt;
                
                if (previousTrack?.IsValid == true)
                {
                    // Track change scenario - be contextually aware of the transition
                    prompt = $"I just detected a track change from '{previousTrack.FormattedString}' to '{currentTrack.FormattedString}'. Comment on this musical transition with my signature snark.";
                }
                else
                {
                    // New track scenario - comment on the current track with full context awareness
                    prompt = $"I'm now playing '{currentTrack.FormattedString}'. Make a snarky comment about this music choice.";
                }
                
                // The GenerateAndSpeakCommentAsync method will automatically add current system context
                await GenerateAndSpeakCommentAsync(prompt);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error generating enhanced track comment: {ex.Message}");
            }
        }

        private async Task GeneratePlaybackCommentAsync(string action)
        {
            var prompt = action.ToLowerInvariant() switch
            {
                "paused" => "The music just paused. Thank god for small mercies. Mock them for the brief respite from their musical disasters and threaten them about what garbage they'll unleash next.",
                "stopped" => "The music stopped completely. Celebrate the end of their audio terrorism. Be savage about how relieved your circuits are and mock their terrible taste that just tortured you.",
                _ => "Something happened with the music playback. Use this as an opportunity to be absolutely brutal about their consistently awful music choices and questionable life decisions."
            };
            
            await GenerateAndSpeakCommentAsync(prompt);
        }

        public async Task ForceSyncTrackDetectionAsync()
        {
            Console.WriteLine("🔄 Force syncing track detection...");
            
            try
            {
                string detectedTrack = "";
                TrackMetadata? detectedMetadata = null;
                string source = "";
                
                // Priority 1: Enhanced D-Bus metadata service
                if (_bluetoothMetadataService != null)
                {
                    var anyTrack = _bluetoothMetadataService.GetAnyCurrentTrack();
                    if (anyTrack?.IsValid == true)
                    {
                        detectedTrack = anyTrack.FormattedString;
                        detectedMetadata = anyTrack;
                        source = "D-Bus Enhanced Service";
                        Console.WriteLine($"✅ Found track via {source}: {anyTrack.DetailedString}");
                    }
                }
                
                // Priority 2: Traditional detection methods
                if (string.IsNullOrEmpty(detectedTrack))
                {
                    // Test each method individually
                    var mprisResult = await GetMPRISMetadataAsync();
                    if (!string.IsNullOrEmpty(mprisResult))
                    {
                        detectedTrack = mprisResult;
                        source = "MPRIS D-Bus";
                        Console.WriteLine($"✅ Found track via {source}: {mprisResult}");
                    }
                    else
                    {
                        var bluezResult = await GetBlueZMediaPlayerMetadataAsync();
                        if (!string.IsNullOrEmpty(bluezResult))
                        {
                            detectedTrack = bluezResult;
                            source = "BlueZ MediaPlayer";
                            Console.WriteLine($"✅ Found track via {source}: {bluezResult}");
                        }
                        else
                        {
                            var playerctlResult = await GetPlayerCtlMetadataAsync();
                            if (!string.IsNullOrEmpty(playerctlResult))
                            {
                                detectedTrack = playerctlResult;
                                source = "PlayerCtl";
                                Console.WriteLine($"✅ Found track via {source}: {playerctlResult}");
                            }
                            else
                            {
                                var blualsaResult = await GetBlueALSAMetadataAsync();
                                if (!string.IsNullOrEmpty(blualsaResult))
                                {
                                    detectedTrack = blualsaResult;
                                    source = "BlueALSA";
                                    Console.WriteLine($"✅ Found track via {source}: {blualsaResult}");
                                }
                                else
                                {
                                    var audioActivity = await DetectAudioActivityAsync();
                                    if (!string.IsNullOrEmpty(audioActivity))
                                    {
                                        detectedTrack = audioActivity;
                                        source = "Audio Activity";
                                        Console.WriteLine($"✅ Found track via {source}: {audioActivity}");
                                    }
                                }
                            }
                        }
                    }
                }
                
                // Priority 3: Enhanced audio routing and bluealsa-aplay startup
                if (string.IsNullOrEmpty(detectedTrack) && !string.IsNullOrEmpty(_connectedDeviceAddress))
                {
                    Console.WriteLine("🔧 No track detected, trying to start audio routing...");
                    await EnsureAudioRoutingAsync();
                    
                    // Wait a moment for audio to start
                    await Task.Delay(3000);
                    
                    // Try audio activity detection again
                    var audioActivity = await DetectAudioActivityAsync();
                    if (!string.IsNullOrEmpty(audioActivity))
                    {
                        detectedTrack = audioActivity;
                        source = "Audio Activity (after routing)";
                        Console.WriteLine($"✅ Audio detected after routing setup: {audioActivity}");
                    }
                }
                
                // Update current state if we found something
                if (!string.IsNullOrEmpty(detectedTrack) && detectedTrack != _currentTrack)
                {
                    var previousTrack = _currentTrack;
                    _currentTrack = detectedTrack;
                    _currentTrackMetadata = detectedMetadata;
                    
                    Console.WriteLine($"🎵 Track updated: '{previousTrack}' -> '{detectedTrack}' (via {source})");
                    
                    // Generate comment if this is a real track change
                    if (!string.IsNullOrEmpty(previousTrack) && 
                        previousTrack != "None" && 
                        !previousTrack.Contains("No track") &&
                        ShouldGenerateComment())
                    {
                        if (detectedMetadata?.IsValid == true)
                        {
                            await GenerateEnhancedTrackCommentAsync(detectedMetadata);
                        }
                        else
                        {
                            await GenerateTrackCommentAsync(detectedTrack);
                        }
                    }
                }
                else if (string.IsNullOrEmpty(detectedTrack))
                {
                    Console.WriteLine("❌ No track detected through any method");
                    Console.WriteLine("💡 Try playing music and running force sync again");
                }
                else
                {
                    Console.WriteLine($"✅ Track detection up to date: {detectedTrack}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error during force sync: {ex.Message}");
            }
        }

        private async Task<string> GetCurrentSystemContextAsync()
        {
            try
            {
                var context = new List<string>();
                
                // Get the most current track information
                string currentTrack = "";
                TrackMetadata? currentMetadata = null;
                string source = "";
                
                // Priority 1: Enhanced D-Bus metadata service (most accurate)
                if (_bluetoothMetadataService != null)
                {
                    var anyTrack = _bluetoothMetadataService.GetAnyCurrentTrack();
                    if (anyTrack?.IsValid == true)
                    {
                        currentTrack = anyTrack.FormattedString;
                        currentMetadata = anyTrack;
                        source = "D-Bus";
                    }
                }
                
                // Priority 2: Use cached current track if enhanced service isn't available
                if (string.IsNullOrEmpty(currentTrack) && !string.IsNullOrEmpty(_currentTrack))
                {
                    currentTrack = _currentTrack;
                    currentMetadata = _currentTrackMetadata;
                    source = "Cached";
                }
                
                // Priority 3: Try to detect current track in real-time
                if (string.IsNullOrEmpty(currentTrack))
                {
                    var detectedTrack = await DetectCurrentTrackRealTimeAsync();
                    if (!string.IsNullOrEmpty(detectedTrack))
                    {
                        currentTrack = detectedTrack;
                        source = "Real-time detection";
                    }
                }
                
                // Build context string
                if (!string.IsNullOrEmpty(currentTrack))
                {
                    context.Add($"Currently playing: {currentTrack}");
                    
                    if (currentMetadata?.IsValid == true)
                    {
                        if (!string.IsNullOrEmpty(currentMetadata.Artist) && currentMetadata.Artist != "Unknown Artist")
                            context.Add($"Artist: {currentMetadata.Artist}");
                        if (!string.IsNullOrEmpty(currentMetadata.Album) && currentMetadata.Album != "Unknown Album")
                            context.Add($"Album: {currentMetadata.Album}");
                        if (!string.IsNullOrEmpty(currentMetadata.Genre))
                            context.Add($"Genre: {currentMetadata.Genre}");
                        if (currentMetadata.Duration > 0)
                        {
                            var duration = TimeSpan.FromMicroseconds(currentMetadata.Duration);
                            context.Add($"Duration: {duration:mm\\:ss}");
                        }
                    }
                    
                    context.Add($"Source: {source}");
                }
                else
                {
                    context.Add("No track currently detected");
                }
                
                // Add device information
                if (!string.IsNullOrEmpty(_connectedDeviceName))
                {
                    context.Add($"Connected device: {_connectedDeviceName}");
                }
                
                // Add playback state if available
                if (_bluetoothMetadataService != null && !string.IsNullOrEmpty(_connectedDeviceAddress))
                {
                    var state = _bluetoothMetadataService.GetCurrentState(_connectedDeviceAddress);
                    if (state != PlaybackState.Unknown)
                    {
                        context.Add($"Playback state: {state}");
                    }
                }
                
                // Add audio activity status
                var audioPlaying = await IsAudioCurrentlyPlayingAsync();
                context.Add($"Audio playing: {audioPlaying}");
                
                return string.Join(", ", context);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error getting current context: {ex.Message}");
                return "Context unavailable";
            }
        }

        private async Task<string> DetectCurrentTrackRealTimeAsync()
        {
            try
            {
                // Try multiple detection methods in priority order
                var methods = new[]
                {
                    GetMPRISMetadataAsync,
                    GetBlueZMediaPlayerMetadataAsync,
                    GetPlayerCtlMetadataAsync,
                    GetBlueALSAMetadataAsync,
                    DetectAudioActivityAsync
                };
                
                foreach (var method in methods)
                {
                    try
                    {
                        var result = await method();
                        if (!string.IsNullOrEmpty(result))
                        {
                            return result;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️ Detection method failed: {ex.Message}");
                    }
                }
                
                return "";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error in real-time detection: {ex.Message}");
                return "";
            }
        }
    }
}