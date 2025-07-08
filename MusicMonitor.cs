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
        
        // Simple state tracking
        private string _currentTrack = "";
        private string _connectedDeviceName = "";
        private string _connectedDeviceAddress = "";
        private DateTime _lastCommentTime = DateTime.MinValue;
        private readonly TimeSpan _commentThrottle = TimeSpan.FromSeconds(30);
        
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
            Console.WriteLine("🎵 Initializing Simple Bluetooth Speaker...");
            
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Console.WriteLine("Warning: This is designed for Linux with BlueZ.");
                return;
            }

            // Do complete automatic setup
            await AutoSetupBluetoothSpeakerAsync();
            
            Console.WriteLine("✅ Simple Bluetooth Speaker initialized and ready!");
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
                    // 1. Check for connected devices (every cycle)
                    await CheckConnectedDevicesAsync();
                    
                    // 2. If device connected, check for music
                    if (!string.IsNullOrEmpty(_connectedDeviceAddress))
                    {
                        await CheckCurrentTrackAsync();
                    }
                    
                    // Simple 2-second polling like your working system
                    await Task.Delay(2000, cancellationToken);
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
                // Method 1: Try BlueALSA metadata (most direct for Bluetooth)
                var blualsaMetadata = await GetBlueALSAMetadataAsync();
                if (!string.IsNullOrEmpty(blualsaMetadata) && blualsaMetadata != _currentTrack)
                {
                    Console.WriteLine($"🎵 Now playing: {blualsaMetadata}");
                    _currentTrack = blualsaMetadata;
                    
                    // Ensure audio routing is working
                    await EnsureAudioRoutingAsync();
                    
                    // Generate AI commentary
                    if (ShouldGenerateComment())
                    {
                        await GenerateTrackCommentAsync(blualsaMetadata);
                    }
                    return;
                }
                
                // Method 2: Try playerctl (works for some devices)
                var metadata = await RunCommandWithOutputAsync("playerctl", "metadata");
                if (!string.IsNullOrEmpty(metadata))
                {
                    var trackInfo = ParseTrackInfo(metadata);
                    
                    if (!string.IsNullOrEmpty(trackInfo) && trackInfo != _currentTrack)
                    {
                        Console.WriteLine($"🎵 Now playing: {trackInfo}");
                        _currentTrack = trackInfo;
                        
                        // Ensure audio routing is working
                        await EnsureAudioRoutingAsync();
                        
                        // Generate AI commentary
                        if (ShouldGenerateComment())
                        {
                            await GenerateTrackCommentAsync(trackInfo);
                        }
                    }
                    return;
                }
                
                // Method 3: Try bluetoothctl as fallback
                if (!string.IsNullOrEmpty(_connectedDeviceAddress) && _connectedDeviceAddress != "detected")
                {
                    var playerInfo = await RunCommandWithOutputAsync("bluetoothctl", "info " + _connectedDeviceAddress);
                    if (!string.IsNullOrEmpty(playerInfo))
                    {
                        var trackInfo = ParseBluetoothctlTrackInfo(playerInfo);
                        
                        if (!string.IsNullOrEmpty(trackInfo) && trackInfo != _currentTrack)
                        {
                            Console.WriteLine($"🎵 Now playing: {trackInfo}");
                            _currentTrack = trackInfo;
                            
                            // Ensure audio routing is working
                            await EnsureAudioRoutingAsync();
                            
                            if (ShouldGenerateComment())
                            {
                                await GenerateTrackCommentAsync(trackInfo);
                            }
                        }
                    }
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
            var randomCheck = _random.Next(0, 3) == 0; // 33% chance
            
            return throttleCheck && randomCheck;
        }

        private async Task GenerateWelcomeCommentAsync(string deviceName)
        {
            var prompts = new[]
            {
                $"Oh great, {deviceName} just connected. Let me guess, you're about to blast some questionable music choices through me?",
                $"Well well, {deviceName} has arrived. Time to judge your terrible taste in music.",
                $"{deviceName} connected. I'm ready to roast whatever audio nightmare you're about to subject me to.",
                $"Oh look, {deviceName} wants to use me. Hope you have better music taste than my last victim."
            };
            
            var prompt = prompts[_random.Next(prompts.Length)];
            await GenerateAndSpeakCommentAsync(prompt);
        }

        private async Task GenerateTrackCommentAsync(string trackInfo)
        {
            var prompts = new[]
            {
                $"Really? '{trackInfo}'? That's what passes for music these days?",
                $"Oh wonderful, '{trackInfo}'. Let me guess, this is your 'favorite song'?",
                $"'{trackInfo}' - because nothing says 'good taste' like... actually, no, this doesn't say that at all.",
                $"Playing '{trackInfo}'. Well, I've heard worse... wait, no, I haven't.",
                $"'{trackInfo}' coming right up. Hope your neighbors appreciate your... unique... musical choices."
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
                        "Nice music choice!",
                        "That's an interesting track.",
                        "Music is playing... how exciting.",
                        "Another song, another dollar.",
                        "I'd comment on this if I had AI powers."
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
                        "Hmm, that's a song alright.",
                        "Music detected. Commenting... eventually.",
                        "I would roast this but my wit generator is broken."
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
                    Console.WriteLine("🔊 SPEAKER SAYS: Something went wrong with my wit generator!");
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
            await RunCommandSilentlyAsync("apt-get", "install -y bluetooth bluez bluez-tools bluealsa alsa-utils playerctl espeak");
            
            Console.WriteLine("� Starting services...");
            await RunCommandSilentlyAsync("systemctl", "enable bluetooth");
            await RunCommandSilentlyAsync("systemctl", "start bluetooth");
            
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
            Console.WriteLine("=== SIMPLE BLUETOOTH SPEAKER STATUS ===");
            Console.WriteLine($"Connected Device: {(_connectedDeviceName ?? "None")}");
            Console.WriteLine($"Device Address: {(_connectedDeviceAddress ?? "None")}");
            Console.WriteLine($"Current Track: {(_currentTrack ?? "None")}");
            Console.WriteLine($"Last Comment: {_lastCommentTime:HH:mm:ss}");
            
            // Service status check
            var bluetoothStatus = await RunCommandWithOutputAsync("systemctl", "is-active bluetooth");
            var blualsaStatus = await RunCommandWithOutputAsync("systemctl", "is-active bluealsa");
            var aplayStatus = await RunCommandWithOutputAsync("systemctl", "is-active bluealsa-aplay");
            
            Console.WriteLine($"\nServices:");
            Console.WriteLine($"  Bluetooth: {bluetoothStatus.Trim()}");
            Console.WriteLine($"  BlueALSA: {blualsaStatus.Trim()}");
            Console.WriteLine($"  BlueALSA-aplay: {aplayStatus.Trim()}");
            
            // Check for any connected devices
            var connectedDevices = await RunCommandWithOutputAsync("bluetoothctl", "devices Connected");
            Console.WriteLine($"\nConnected via bluetoothctl: {(!string.IsNullOrEmpty(connectedDevices?.Trim()) ? "Yes" : "No")}");
            
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
            Console.WriteLine("=== TRACK DETECTION DEBUG ===");
            
            Console.WriteLine("\n1. Testing playerctl metadata...");
            var playerctlResult = await RunCommandWithOutputAsync("playerctl", "metadata");
            Console.WriteLine($"   Raw output length: {playerctlResult?.Length ?? 0} characters");
            if (!string.IsNullOrEmpty(playerctlResult) && playerctlResult.Length < 500)
            {
                Console.WriteLine($"   Raw output: {playerctlResult}");
                var parsed = ParseTrackInfo(playerctlResult);
                Console.WriteLine($"   Parsed result: '{parsed}'");
            }
            
            Console.WriteLine("\n2. Testing playerctl title/artist separately...");
            var title = await RunCommandWithOutputAsync("playerctl", "metadata title");
            var artist = await RunCommandWithOutputAsync("playerctl", "metadata artist");
            Console.WriteLine($"   Title: '{title?.Trim()}'");
            Console.WriteLine($"   Artist: '{artist?.Trim()}'");
            
            Console.WriteLine("\n3. Testing playerctl players list...");
            var players = await RunCommandWithOutputAsync("playerctl", "--list-all");
            Console.WriteLine($"   Available players: '{players?.Trim()}'");
            
            Console.WriteLine("\n4. Testing bluetoothctl info...");
            if (!string.IsNullOrEmpty(_connectedDeviceAddress) && _connectedDeviceAddress != "detected")
            {
                var deviceInfo = await RunCommandWithOutputAsync("bluetoothctl", "info " + _connectedDeviceAddress);
                Console.WriteLine($"   Device info length: {deviceInfo?.Length ?? 0} characters");
                if (!string.IsNullOrEmpty(deviceInfo))
                {
                    var parsed = ParseBluetoothctlTrackInfo(deviceInfo);
                    Console.WriteLine($"   Parsed track info: '{parsed}'");
                    
                    // Show relevant lines
                    var lines = deviceInfo.Split('\n');
                    foreach (var line in lines)
                    {
                        if (line.Contains("Artist:") || line.Contains("Title:") || line.Contains("Track:"))
                        {
                            Console.WriteLine($"   Found: {line.Trim()}");
                        }
                    }
                }
            }
            else
            {
                Console.WriteLine("   No device connected for testing");
            }
            
            Console.WriteLine("\n=== Current State ===");
            Console.WriteLine($"Connected Device: {_connectedDeviceName ?? "None"}");
            Console.WriteLine($"Device Address: {_connectedDeviceAddress ?? "None"}");
            Console.WriteLine($"Current Track: {_currentTrack ?? "None"}");
            
            Console.WriteLine("\n=== END DEBUG ===");
        }

        // ...existing code...
    }
}
