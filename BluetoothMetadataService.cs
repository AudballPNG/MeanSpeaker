using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus;

namespace BluetoothSpeaker
{
    public class BluetoothMetadataService : IDisposable
    {
        private Connection? _systemBus;
        private IObjectManager? _bluezObjectManager;
        private readonly Dictionary<string, IMediaPlayer> _mediaPlayers = new();
        private readonly Dictionary<string, IDisposable> _propertyWatchers = new();
        private readonly Dictionary<string, TrackMetadata> _deviceTracks = new();
        private readonly Dictionary<string, PlaybackState> _deviceStates = new();
        
        private readonly SemaphoreSlim _operationLock = new(1, 1);
        private bool _disposed = false;
        private bool _isInitialized = false;

        // Events
        public event EventHandler<TrackChangedEventArgs>? TrackChanged;
        public event EventHandler<PlaybackStateChangedEventArgs>? PlaybackStateChanged;
        public event EventHandler<string>? DeviceConnected;
        public event EventHandler<string>? DeviceDisconnected;

        public async Task<bool> InitializeAsync()
        {
            if (_isInitialized) return true;

            try
            {
                Console.WriteLine("🔌 Initializing D-Bus Bluetooth metadata service...");
                
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    Console.WriteLine("⚠️ D-Bus service only available on Linux. Using fallback methods.");
                    return false;
                }

                // Connect to system D-Bus
                _systemBus = Connection.System;
                
                // Get BlueZ object manager
                _bluezObjectManager = _systemBus.CreateProxy<IObjectManager>("org.bluez", "/");
                
                // Setup watchers for new interfaces
                await SetupInterfaceWatchersAsync();
                
                // Discover existing media players
                await DiscoverExistingMediaPlayersAsync();
                
                _isInitialized = true;
                Console.WriteLine("✅ D-Bus Bluetooth metadata service initialized");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to initialize D-Bus service: {ex.Message}");
                return false;
            }
        }

        private async Task SetupInterfaceWatchersAsync()
        {
            try
            {
                if (_bluezObjectManager == null) return;

                // Watch for new interfaces (devices/players connecting)
                await _bluezObjectManager.WatchInterfacesAddedAsync(OnInterfacesAdded);
                
                // Watch for removed interfaces (devices/players disconnecting)
                await _bluezObjectManager.WatchInterfacesRemovedAsync(OnInterfacesRemoved);
                
                Console.WriteLine("🔍 Interface watchers established");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error setting up interface watchers: {ex.Message}");
            }
        }

        private async Task DiscoverExistingMediaPlayersAsync()
        {
            try
            {
                if (_bluezObjectManager == null || _systemBus == null) return;

                var managedObjects = await _bluezObjectManager.GetManagedObjectsAsync();
                
                foreach (var (objectPath, interfaces) in managedObjects)
                {
                    // Look for MediaPlayer1 interfaces
                    if (interfaces.ContainsKey("org.bluez.MediaPlayer1"))
                    {
                        var deviceAddress = ExtractDeviceAddressFromPath(objectPath.ToString());
                        if (!string.IsNullOrEmpty(deviceAddress))
                        {
                            await SetupMediaPlayerAsync(objectPath.ToString(), deviceAddress);
                        }
                    }
                }
                
                Console.WriteLine($"🎵 Discovered {_mediaPlayers.Count} existing media players");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error discovering existing media players: {ex.Message}");
            }
        }

        private async void OnInterfacesAdded((ObjectPath objectPath, IDictionary<string, IDictionary<string, object>> interfacesAndProperties) args)
        {
            try
            {
                var (objectPath, interfaces) = args;
                
                if (interfaces.ContainsKey("org.bluez.MediaPlayer1"))
                {
                    var deviceAddress = ExtractDeviceAddressFromPath(objectPath.ToString());
                    if (!string.IsNullOrEmpty(deviceAddress))
                    {
                        Console.WriteLine($"📱 New media player detected: {deviceAddress}");
                        await SetupMediaPlayerAsync(objectPath.ToString(), deviceAddress);
                        DeviceConnected?.Invoke(this, deviceAddress);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error handling interface addition: {ex.Message}");
            }
        }

        private async void OnInterfacesRemoved((ObjectPath objectPath, string[] interfaces) args)
        {
            try
            {
                var (objectPath, interfaces) = args;
                
                if (interfaces.Contains("org.bluez.MediaPlayer1"))
                {
                    var deviceAddress = ExtractDeviceAddressFromPath(objectPath.ToString());
                    if (!string.IsNullOrEmpty(deviceAddress))
                    {
                        Console.WriteLine($"📱 Media player disconnected: {deviceAddress}");
                        await CleanupMediaPlayerAsync(deviceAddress);
                        DeviceDisconnected?.Invoke(this, deviceAddress);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error handling interface removal: {ex.Message}");
            }
        }

        private async Task SetupMediaPlayerAsync(string objectPath, string deviceAddress)
        {
            await _operationLock.WaitAsync();
            try
            {
                if (_systemBus == null || _mediaPlayers.ContainsKey(deviceAddress))
                    return;

                // Create media player proxy
                var mediaPlayer = _systemBus.CreateProxy<IMediaPlayer>("org.bluez", objectPath);
                _mediaPlayers[deviceAddress] = mediaPlayer;

                // Setup property watcher
                var watcher = await mediaPlayer.WatchPropertiesAsync(changes => 
                    HandleMediaPlayerPropertyChanges(deviceAddress, changes));
                _propertyWatchers[deviceAddress] = watcher;

                // Get initial state
                await UpdateInitialPlayerStateAsync(mediaPlayer, deviceAddress);
                
                Console.WriteLine($"✅ Media player setup complete for {deviceAddress}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error setting up media player for {deviceAddress}: {ex.Message}");
            }
            finally
            {
                _operationLock.Release();
            }
        }

        private async Task UpdateInitialPlayerStateAsync(IMediaPlayer mediaPlayer, string deviceAddress)
        {
            try
            {
                var properties = await mediaPlayer.GetAllAsync();
                
                // Get initial track
                if (properties.TryGetValue("Track", out var trackObj) && 
                    trackObj is IDictionary<string, object> trackDict)
                {
                    var metadata = TrackMetadata.FromDictionary(trackDict);
                    _deviceTracks[deviceAddress] = metadata;
                    
                    if (metadata.IsValid)
                    {
                        Console.WriteLine($"🎵 Initial track for {deviceAddress}: {metadata.FormattedString}");
                        TrackChanged?.Invoke(this, new TrackChangedEventArgs(metadata));
                    }
                }

                // Get initial playback state
                if (properties.TryGetValue("Status", out var statusObj) && statusObj is string status)
                {
                    var state = ParsePlaybackState(status);
                    _deviceStates[deviceAddress] = state;
                    Console.WriteLine($"▶️ Initial state for {deviceAddress}: {state}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error getting initial player state for {deviceAddress}: {ex.Message}");
            }
        }

        private async void HandleMediaPlayerPropertyChanges(string deviceAddress, PropertyChanges changes)
        {
            try
            {
                await _operationLock.WaitAsync();

                foreach (var change in changes.Changed)
                {
                    if (change.Key == "Track" && change.Value is IDictionary<string, object> trackDict)
                    {
                        HandleTrackChangeAsync(deviceAddress, trackDict);
                    }
                    else if (change.Key == "Status" && change.Value is string status)
                    {
                        HandlePlaybackStateChangeAsync(deviceAddress, status);
                    }
                    else if (change.Key == "Position" && change.Value is uint position)
                    {
                        // Track position updates - could be used for progress tracking
                        // Console.WriteLine($"🕐 Position update for {deviceAddress}: {position}ms");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error handling property changes for {deviceAddress}: {ex.Message}");
            }
            finally
            {
                _operationLock.Release();
            }
        }

        private void HandleTrackChangeAsync(string deviceAddress, IDictionary<string, object> trackDict)
        {
            try
            {
                var newTrack = TrackMetadata.FromDictionary(trackDict);
                var previousTrack = _deviceTracks.TryGetValue(deviceAddress, out var prev) ? prev : null;

                // Only trigger if track actually changed and is valid
                if (!newTrack.Equals(previousTrack) && newTrack.IsValid)
                {
                    _deviceTracks[deviceAddress] = newTrack;
                    
                    Console.WriteLine($"🎵 Track changed on {deviceAddress}:");
                    if (previousTrack?.IsValid == true)
                        Console.WriteLine($"   From: {previousTrack.FormattedString}");
                    Console.WriteLine($"   To: {newTrack.DetailedString}");
                    
                    TrackChanged?.Invoke(this, new TrackChangedEventArgs(newTrack, previousTrack));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error handling track change for {deviceAddress}: {ex.Message}");
            }
        }

        private void HandlePlaybackStateChangeAsync(string deviceAddress, string status)
        {
            try
            {
                var newState = ParsePlaybackState(status);
                var previousState = _deviceStates.TryGetValue(deviceAddress, out var prev) ? prev : PlaybackState.Unknown;

                if (newState != previousState)
                {
                    _deviceStates[deviceAddress] = newState;
                    var currentTrack = _deviceTracks.TryGetValue(deviceAddress, out var track) ? track : null;
                    
                    Console.WriteLine($"▶️ Playback state changed on {deviceAddress}: {previousState} -> {newState}");
                    if (currentTrack?.IsValid == true)
                        Console.WriteLine($"   Current track: {currentTrack.FormattedString}");
                    
                    PlaybackStateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(newState, previousState, currentTrack));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error handling playback state change for {deviceAddress}: {ex.Message}");
            }
        }

        private PlaybackState ParsePlaybackState(string status)
        {
            return status.ToLowerInvariant() switch
            {
                "playing" => PlaybackState.Playing,
                "paused" => PlaybackState.Paused,
                "stopped" => PlaybackState.Stopped,
                "forward-seek" => PlaybackState.Forward,
                "reverse-seek" => PlaybackState.Reverse,
                _ => PlaybackState.Unknown
            };
        }

        private string ExtractDeviceAddressFromPath(string objectPath)
        {
            try
            {
                // Extract MAC address from BlueZ object path
                // Example: /org/bluez/hci0/dev_XX_XX_XX_XX_XX_XX/player0
                var match = Regex.Match(objectPath, @"dev_([0-9A-Fa-f]{2}_[0-9A-Fa-f]{2}_[0-9A-Fa-f]{2}_[0-9A-Fa-f]{2}_[0-9A-Fa-f]{2}_[0-9A-Fa-f]{2})");
                if (match.Success)
                {
                    return match.Groups[1].Value.Replace("_", ":");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error extracting device address from {objectPath}: {ex.Message}");
            }
            return "";
        }

        private async Task CleanupMediaPlayerAsync(string deviceAddress)
        {
            await _operationLock.WaitAsync();
            try
            {
                if (_propertyWatchers.TryGetValue(deviceAddress, out var watcher))
                {
                    watcher.Dispose();
                    _propertyWatchers.Remove(deviceAddress);
                }

                _mediaPlayers.Remove(deviceAddress);
                _deviceTracks.Remove(deviceAddress);
                _deviceStates.Remove(deviceAddress);
                
                Console.WriteLine($"🧹 Cleaned up media player for {deviceAddress}");
            }
            finally
            {
                _operationLock.Release();
            }
        }

        // Public methods for getting current state
        public TrackMetadata? GetCurrentTrack(string deviceAddress)
        {
            return _deviceTracks.TryGetValue(deviceAddress, out var track) ? track : null;
        }

        public PlaybackState GetCurrentState(string deviceAddress)
        {
            return _deviceStates.TryGetValue(deviceAddress, out var state) ? state : PlaybackState.Unknown;
        }

        public IEnumerable<string> GetConnectedDevices()
        {
            return _mediaPlayers.Keys.ToList();
        }

        public TrackMetadata? GetAnyCurrentTrack()
        {
            return _deviceTracks.Values.FirstOrDefault(t => t.IsValid);
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                // Cleanup all watchers
                foreach (var watcher in _propertyWatchers.Values)
                {
                    watcher?.Dispose();
                }

                _propertyWatchers.Clear();
                _mediaPlayers.Clear();
                _deviceTracks.Clear();
                _deviceStates.Clear();

                _systemBus?.Dispose();
                _operationLock?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error during disposal: {ex.Message}");
            }

            _disposed = true;
        }
    }
}
