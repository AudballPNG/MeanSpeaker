using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BluetoothSpeaker
{
    public class FallbackMetadataService : IDisposable
    {
        private readonly Timer? _pollTimer;
        private readonly Dictionary<string, TrackMetadata> _lastTracks = new();
        private readonly Dictionary<string, PlaybackState> _lastStates = new();
        private readonly SemaphoreSlim _operationLock = new(1, 1);
        private bool _disposed = false;

        // Events
        public event EventHandler<TrackChangedEventArgs>? TrackChanged;
        public event EventHandler<PlaybackStateChangedEventArgs>? PlaybackStateChanged;

        public FallbackMetadataService()
        {
            // Poll every 3 seconds for metadata changes
            _pollTimer = new Timer(async _ => await PollForChangesAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        }

        private async Task PollForChangesAsync()
        {
            if (_disposed) return;

            await _operationLock.WaitAsync();
            try
            {
                // Method 1: Try PlayerCtl with enhanced parsing
                await CheckPlayerCtlMetadataAsync();
                
                // Method 2: Try BluetoothCtl player info
                await CheckBluetoothCtlMetadataAsync();
                
                // Method 3: Try MPRIS via command line
                await CheckMPRISMetadataAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error in fallback metadata polling: {ex.Message}");
            }
            finally
            {
                _operationLock.Release();
            }
        }

        private async Task CheckPlayerCtlMetadataAsync()
        {
            try
            {
                // Enhanced PlayerCtl method with multiple approaches
                
                // Method 1: Get all available players
                var playersOutput = await RunCommandWithOutputAsync("playerctl", "--list-all");
                if (string.IsNullOrEmpty(playersOutput)) return;

                var players = playersOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                
                foreach (var player in players)
                {
                    var playerName = player.Trim();
                    if (string.IsNullOrEmpty(playerName)) continue;

                    // Get metadata for this specific player
                    var metadata = await GetPlayerCtlMetadataForPlayerAsync(playerName);
                    if (metadata?.IsValid == true)
                    {
                        ProcessTrackChangeAsync($"playerctl-{playerName}", metadata);
                    }

                    // Get playback status
                    var status = await RunCommandWithOutputAsync("playerctl", $"--player={playerName} status");
                    if (!string.IsNullOrEmpty(status))
                    {
                        var state = ParsePlayerCtlStatus(status.Trim());
                        ProcessStateChangeAsync($"playerctl-{playerName}", state, metadata);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error checking PlayerCtl metadata: {ex.Message}");
            }
        }

        private async Task<TrackMetadata?> GetPlayerCtlMetadataForPlayerAsync(string playerName)
        {
            try
            {
                // Method 1: Get full metadata output
                var fullMetadata = await RunCommandWithOutputAsync("playerctl", $"--player={playerName} metadata");
                if (!string.IsNullOrEmpty(fullMetadata))
                {
                    var parsed = ParsePlayerCtlMetadata(fullMetadata);
                    if (parsed?.IsValid == true) return parsed;
                }

                // Method 2: Get individual fields
                var artist = await RunCommandWithOutputAsync("playerctl", $"--player={playerName} metadata artist");
                var title = await RunCommandWithOutputAsync("playerctl", $"--player={playerName} metadata title");
                var album = await RunCommandWithOutputAsync("playerctl", $"--player={playerName} metadata album");

                artist = artist?.Trim();
                title = title?.Trim();
                album = album?.Trim();

                if (!string.IsNullOrEmpty(artist) || !string.IsNullOrEmpty(title))
                {
                    return new TrackMetadata
                    {
                        Artist = !string.IsNullOrEmpty(artist) ? artist : "Unknown Artist",
                        Title = !string.IsNullOrEmpty(title) ? title : "Unknown Track",
                        Album = !string.IsNullOrEmpty(album) ? album : ""
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error getting PlayerCtl metadata for {playerName}: {ex.Message}");
            }

            return null;
        }

        private TrackMetadata? ParsePlayerCtlMetadata(string output)
        {
            try
            {
                var metadata = new TrackMetadata();
                var lines = output.Split('\n');

                foreach (var line in lines)
                {
                    if (line.Contains("xesam:artist") || line.Contains("artist"))
                    {
                        var artist = ExtractPlayerCtlValue(line);
                        if (!string.IsNullOrEmpty(artist)) metadata.Artist = artist;
                    }
                    else if (line.Contains("xesam:title") || line.Contains("title"))
                    {
                        var title = ExtractPlayerCtlValue(line);
                        if (!string.IsNullOrEmpty(title)) metadata.Title = title;
                    }
                    else if (line.Contains("xesam:album") || line.Contains("album"))
                    {
                        var album = ExtractPlayerCtlValue(line);
                        if (!string.IsNullOrEmpty(album)) metadata.Album = album;
                    }
                    else if (line.Contains("xesam:genre") || line.Contains("genre"))
                    {
                        var genre = ExtractPlayerCtlValue(line);
                        if (!string.IsNullOrEmpty(genre)) metadata.Genre = genre;
                    }
                }

                return metadata.IsValid ? metadata : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error parsing PlayerCtl metadata: {ex.Message}");
                return null;
            }
        }

        private string ExtractPlayerCtlValue(string line)
        {
            try
            {
                // Try different patterns for playerctl output
                var patterns = new[]
                {
                    @"^\s*\w+:?\w*:?\w*\s+(.+)$",  // General pattern
                    @"^\s*[^:]+:\s*(.+)$",         // Key: Value pattern
                    @"(.+)$"                       // Fallback - whole line
                };

                foreach (var pattern in patterns)
                {
                    var match = Regex.Match(line, pattern);
                    if (match.Success && match.Groups.Count > 1)
                    {
                        var value = match.Groups[1].Value.Trim().Trim('"', '\'');
                        if (!string.IsNullOrEmpty(value) && value != "Unknown" && value != "N/A")
                        {
                            return value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error extracting value from line '{line}': {ex.Message}");
            }

            return "";
        }

        private async Task CheckBluetoothCtlMetadataAsync()
        {
            try
            {
                // Get connected devices
                var devicesOutput = await RunCommandWithOutputAsync("bluetoothctl", "devices Connected");
                if (string.IsNullOrEmpty(devicesOutput)) return;

                var deviceLines = devicesOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                
                foreach (var deviceLine in deviceLines)
                {
                    var macMatch = Regex.Match(deviceLine, @"([0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2})");
                    if (!macMatch.Success) continue;

                    var deviceAddress = macMatch.Groups[1].Value;
                    
                    // Get player info for this device
                    var playerInfo = await RunCommandWithOutputAsync("bluetoothctl", $"info {deviceAddress}");
                    if (string.IsNullOrEmpty(playerInfo)) continue;

                    var metadata = ParseBluetoothCtlPlayerInfo(playerInfo);
                    if (metadata?.IsValid == true)
                    {
                        ProcessTrackChangeAsync($"bluetoothctl-{deviceAddress}", metadata);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error checking BluetoothCtl metadata: {ex.Message}");
            }
        }

        private TrackMetadata? ParseBluetoothCtlPlayerInfo(string playerInfo)
        {
            try
            {
                var metadata = new TrackMetadata();
                var lines = playerInfo.Split('\n');

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    
                    if (trimmed.StartsWith("Artist:"))
                    {
                        metadata.Artist = trimmed.Substring(7).Trim();
                    }
                    else if (trimmed.StartsWith("Title:"))
                    {
                        metadata.Title = trimmed.Substring(6).Trim();
                    }
                    else if (trimmed.StartsWith("Album:"))
                    {
                        metadata.Album = trimmed.Substring(6).Trim();
                    }
                    else if (trimmed.StartsWith("Genre:"))
                    {
                        metadata.Genre = trimmed.Substring(6).Trim();
                    }
                    else if (trimmed.StartsWith("Track:") && uint.TryParse(trimmed.Substring(6).Trim(), out uint trackNum))
                    {
                        metadata.TrackNumber = trackNum;
                    }
                    else if (trimmed.StartsWith("Duration:") && ulong.TryParse(trimmed.Substring(9).Trim(), out ulong duration))
                    {
                        metadata.Duration = duration;
                    }
                }

                return metadata.IsValid ? metadata : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error parsing BluetoothCtl player info: {ex.Message}");
                return null;
            }
        }

        private async Task CheckMPRISMetadataAsync()
        {
            try
            {
                // Get list of MPRIS players via D-Bus command
                var dbusOutput = await RunCommandWithOutputAsync("dbus-send", 
                    "--session --print-reply --dest=org.freedesktop.DBus /org/freedesktop/DBus org.freedesktop.DBus.ListNames");
                
                if (string.IsNullOrEmpty(dbusOutput)) return;

                var mprisPlayers = new List<string>();
                var lines = dbusOutput.Split('\n');
                
                foreach (var line in lines)
                {
                    var match = Regex.Match(line, @"org\.mpris\.MediaPlayer2\.(\w+)");
                    if (match.Success)
                    {
                        mprisPlayers.Add(match.Groups[1].Value);
                    }
                }

                foreach (var player in mprisPlayers)
                {
                    var metadata = await GetMPRISMetadataViaCommandAsync(player);
                    if (metadata?.IsValid == true)
                    {
                        ProcessTrackChangeAsync($"mpris-{player}", metadata);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error checking MPRIS metadata: {ex.Message}");
            }
        }

        private async Task<TrackMetadata?> GetMPRISMetadataViaCommandAsync(string playerName)
        {
            try
            {
                var metadataOutput = await RunCommandWithOutputAsync("dbus-send",
                    $"--session --print-reply --dest=org.mpris.MediaPlayer2.{playerName} /org/mpris/MediaPlayer2 org.freedesktop.DBus.Properties.Get string:org.mpris.MediaPlayer2.Player string:Metadata");

                if (string.IsNullOrEmpty(metadataOutput)) return null;

                return ParseMPRISCommandOutput(metadataOutput);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error getting MPRIS metadata for {playerName}: {ex.Message}");
                return null;
            }
        }

        private TrackMetadata? ParseMPRISCommandOutput(string output)
        {
            try
            {
                var metadata = new TrackMetadata();
                var lines = output.Split('\n');

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    
                    if (line.Contains("xesam:artist"))
                    {
                        var artist = ExtractMPRISStringValue(lines, i);
                        if (!string.IsNullOrEmpty(artist)) metadata.Artist = artist;
                    }
                    else if (line.Contains("xesam:title"))
                    {
                        var title = ExtractMPRISStringValue(lines, i);
                        if (!string.IsNullOrEmpty(title)) metadata.Title = title;
                    }
                    else if (line.Contains("xesam:album"))
                    {
                        var album = ExtractMPRISStringValue(lines, i);
                        if (!string.IsNullOrEmpty(album)) metadata.Album = album;
                    }
                }

                return metadata.IsValid ? metadata : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error parsing MPRIS command output: {ex.Message}");
                return null;
            }
        }

        private string ExtractMPRISStringValue(string[] lines, int startIndex)
        {
            try
            {
                // Look for string value in the next few lines
                for (int i = startIndex + 1; i < Math.Min(startIndex + 5, lines.Length); i++)
                {
                    var match = Regex.Match(lines[i], @"string\s+""([^""]+)""");
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error extracting MPRIS string value: {ex.Message}");
            }
            return "";
        }

        private PlaybackState ParsePlayerCtlStatus(string status)
        {
            return status.ToLowerInvariant() switch
            {
                "playing" => PlaybackState.Playing,
                "paused" => PlaybackState.Paused,
                "stopped" => PlaybackState.Stopped,
                _ => PlaybackState.Unknown
            };
        }

        private void ProcessTrackChangeAsync(string sourceId, TrackMetadata newTrack)
        {
            try
            {
                var previousTrack = _lastTracks.TryGetValue(sourceId, out var prev) ? prev : null;
                
                if (!newTrack.Equals(previousTrack))
                {
                    _lastTracks[sourceId] = newTrack;
                    TrackChanged?.Invoke(this, new TrackChangedEventArgs(newTrack, previousTrack));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error processing track change for {sourceId}: {ex.Message}");
            }
        }

        private void ProcessStateChangeAsync(string sourceId, PlaybackState newState, TrackMetadata? currentTrack)
        {
            try
            {
                var previousState = _lastStates.TryGetValue(sourceId, out var prev) ? prev : PlaybackState.Unknown;
                
                if (newState != previousState)
                {
                    _lastStates[sourceId] = newState;
                    PlaybackStateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(newState, previousState, currentTrack));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error processing state change for {sourceId}: {ex.Message}");
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

                return process.ExitCode == 0 ? output : "";
            }
            catch
            {
                return "";
            }
        }

        public TrackMetadata? GetAnyCurrentTrack()
        {
            return _lastTracks.Values.FirstOrDefault(t => t.IsValid);
        }

        public void Dispose()
        {
            if (_disposed) return;

            _pollTimer?.Dispose();
            _operationLock?.Dispose();
            
            _disposed = true;
        }
    }
}
