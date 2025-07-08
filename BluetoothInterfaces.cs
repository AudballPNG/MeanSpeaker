using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tmds.DBus;

namespace BluetoothSpeaker
{
    // D-Bus interfaces for BlueZ Bluetooth stack
    [DBusInterface("org.bluez.Device1")]
    public interface IDevice : IDBusObject
    {
        Task<IDictionary<string, object>> GetAllAsync();
        Task<object> GetAsync(string prop);
        Task SetAsync(string prop, object val);
        Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
    }

    [DBusInterface("org.bluez.MediaPlayer1")]
    public interface IMediaPlayer : IDBusObject
    {
        Task<IDictionary<string, object>> GetAllAsync();
        Task<object> GetAsync(string prop);
        Task SetAsync(string prop, object val);
        Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
        
        // MediaPlayer1 specific methods
        Task PlayAsync();
        Task PauseAsync();
        Task StopAsync();
        Task NextAsync();
        Task PreviousAsync();
        Task FastForwardAsync();
        Task RewindAsync();
    }

    [DBusInterface("org.freedesktop.DBus.ObjectManager")]
    public interface IObjectManager : IDBusObject
    {
        Task<IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>>> GetManagedObjectsAsync();
        Task<IDisposable> WatchInterfacesAddedAsync(Action<(ObjectPath objectPath, IDictionary<string, IDictionary<string, object>> interfacesAndProperties)> handler);
        Task<IDisposable> WatchInterfacesRemovedAsync(Action<(ObjectPath objectPath, string[] interfaces)> handler);
    }

    // Track metadata structure
    public class TrackMetadata
    {
        public string Artist { get; set; } = "Unknown Artist";
        public string Title { get; set; } = "Unknown Track";
        public string Album { get; set; } = "Unknown Album";
        public string Genre { get; set; } = "";
        public uint TrackNumber { get; set; } = 0;
        public ulong Duration { get; set; } = 0; // Duration in microseconds
        public DateTime LastUpdated { get; set; } = DateTime.Now;

        public string FormattedString => $"{Artist} - {Title}";
        
        public string DetailedString => string.IsNullOrEmpty(Album) 
            ? FormattedString 
            : $"{Artist} - {Title} (from {Album})";

        public bool IsValid => !string.IsNullOrEmpty(Artist) && Artist != "Unknown Artist" &&
                              !string.IsNullOrEmpty(Title) && Title != "Unknown Track";

        public override bool Equals(object? obj)
        {
            if (obj is TrackMetadata other)
            {
                return Artist == other.Artist && Title == other.Title && Album == other.Album;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Artist, Title, Album);
        }

        public static TrackMetadata FromDictionary(IDictionary<string, object>? trackData)
        {
            var metadata = new TrackMetadata();
            
            if (trackData == null) return metadata;

            try
            {
                if (trackData.TryGetValue("Artist", out var artist) && artist is string artistStr)
                    metadata.Artist = artistStr;
                
                if (trackData.TryGetValue("Title", out var title) && title is string titleStr)
                    metadata.Title = titleStr;
                
                if (trackData.TryGetValue("Album", out var album) && album is string albumStr)
                    metadata.Album = albumStr;
                
                if (trackData.TryGetValue("Genre", out var genre) && genre is string genreStr)
                    metadata.Genre = genreStr;
                
                if (trackData.TryGetValue("TrackNumber", out var trackNum) && trackNum is uint trackNumVal)
                    metadata.TrackNumber = trackNumVal;
                
                if (trackData.TryGetValue("Duration", out var duration) && duration is ulong durationVal)
                    metadata.Duration = durationVal;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error parsing track metadata: {ex.Message}");
            }

            return metadata;
        }
    }

    // Playback state enumeration
    public enum PlaybackState
    {
        Unknown,
        Playing,
        Paused,
        Stopped,
        Forward,
        Reverse
    }

    // Event args for track changes
    public class TrackChangedEventArgs : EventArgs
    {
        public TrackMetadata? PreviousTrack { get; set; }
        public TrackMetadata CurrentTrack { get; set; }
        public DateTime ChangeTime { get; set; } = DateTime.Now;

        public TrackChangedEventArgs(TrackMetadata currentTrack, TrackMetadata? previousTrack = null)
        {
            CurrentTrack = currentTrack;
            PreviousTrack = previousTrack;
        }
    }

    // Event args for playback state changes
    public class PlaybackStateChangedEventArgs : EventArgs
    {
        public PlaybackState PreviousState { get; set; }
        public PlaybackState CurrentState { get; set; }
        public DateTime ChangeTime { get; set; } = DateTime.Now;
        public TrackMetadata? CurrentTrack { get; set; }

        public PlaybackStateChangedEventArgs(PlaybackState currentState, PlaybackState previousState, TrackMetadata? currentTrack = null)
        {
            CurrentState = currentState;
            PreviousState = previousState;
            CurrentTrack = currentTrack;
        }
    }
}
