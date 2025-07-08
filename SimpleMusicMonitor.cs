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
    public class SimpleMusicMonitor : IDisposable
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

        public SimpleMusicMonitor(string openAiApiKey, bool enableSpeech = true, string ttsVoice = "en+f3")
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

            // Ensure basic Bluetooth services are running
            await EnsureBluetoothServicesAsync();
            
            Console.WriteLine("✅ Simple Bluetooth Speaker initialized!");
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
                                    
                                    // Welcome message
                                    await GenerateWelcomeCommentAsync(name);
                                }
                                return; // Found a device, we're done
                            }
                        }
                    }
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

        private async Task CheckCurrentTrackAsync()
        {
            try
            {
                // Method 1: Try playerctl (most reliable for track info)
                var metadata = await RunCommandWithOutputAsync("playerctl", "metadata");
                
                if (!string.IsNullOrEmpty(metadata))
                {
                    var trackInfo = ParseTrackInfo(metadata);
                    
                    if (!string.IsNullOrEmpty(trackInfo) && trackInfo != _currentTrack)
                    {
                        Console.WriteLine($"🎵 Now playing: {trackInfo}");
                        _currentTrack = trackInfo;
                        
                        // Generate AI commentary
                        if (ShouldGenerateComment())
                        {
                            await GenerateTrackCommentAsync(trackInfo);
                        }
                    }
                    return;
                }
                
                // Method 2: Try bluetoothctl as fallback
                var playerInfo = await RunCommandWithOutputAsync("bluetoothctl", "player info");
                if (!string.IsNullOrEmpty(playerInfo))
                {
                    var trackInfo = ParseBluetoothctlTrackInfo(playerInfo);
                    
                    if (!string.IsNullOrEmpty(trackInfo) && trackInfo != _currentTrack)
                    {
                        Console.WriteLine($"🎵 Now playing: {trackInfo}");
                        _currentTrack = trackInfo;
                        
                        if (ShouldGenerateComment())
                        {
                            await GenerateTrackCommentAsync(trackInfo);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error checking track: {ex.Message}");
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
                $"Playing '{trackInfo}'. Well, I've heard worse... wait, no I haven't.",
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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error generating comment: {ex.Message}");
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

        private async Task EnsureBluetoothServicesAsync()
        {
            try
            {
                Console.WriteLine("🔧 Ensuring Bluetooth services are running...");
                
                // Start basic services
                await RunCommandAsync("systemctl", "start bluetooth");
                await RunCommandAsync("systemctl", "start bluealsa");
                await RunCommandAsync("systemctl", "start bluealsa-aplay");
                
                // Make discoverable
                await RunCommandAsync("bluetoothctl", "power on");
                await RunCommandAsync("bluetoothctl", "system-alias 'The Little Shit'");
                await RunCommandAsync("bluetoothctl", "discoverable on");
                await RunCommandAsync("bluetoothctl", "pairable on");
                
                Console.WriteLine("✅ Bluetooth services configured");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Warning: Could not configure Bluetooth: {ex.Message}");
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
            
            // Quick service check
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
    }
}
