using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tmds.DBus;

namespace BluetoothSpeaker
{
    // D-Bus interface definitions for BlueZ
    
    [DBusInterface("org.freedesktop.DBus.ObjectManager")]
    public interface IObjectManager : IDBusObject
    {
        Task<IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>>> GetManagedObjectsAsync();
    }

    [DBusInterface("org.bluez.Adapter1")]
    public interface IAdapter1 : IDBusObject
    {
        Task StartDiscoveryAsync();
        Task StopDiscoveryAsync();
        Task RemoveDeviceAsync(ObjectPath device);

        Task<T> GetAsync<T>(string prop);
        Task<BlueZAdapter1Properties> GetAllAsync();
        Task SetAsync(string prop, object val);
        Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
    }

    [Dictionary]
    public class BlueZAdapter1Properties
    {
        public string? Address { get; set; }
        public string? Name { get; set; }
        public string? Alias { get; set; }
        public bool Powered { get; set; }
        public bool Discoverable { get; set; }
        public uint DiscoverableTimeout { get; set; }
        public bool Pairable { get; set; }
        public uint PairableTimeout { get; set; }
        public bool Discovering { get; set; }
        public string[]? UUIDs { get; set; }
    }

    [DBusInterface("org.bluez.Device1")]
    public interface IDevice1 : IDBusObject
    {
        Task ConnectAsync();
        Task DisconnectAsync();
        Task PairAsync();
        Task CancelPairingAsync();

        Task<T> GetAsync<T>(string prop);
        Task<BlueZDevice1Properties> GetAllAsync();
        Task SetAsync(string prop, object val);
        Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
    }

    [Dictionary]
    public class BlueZDevice1Properties
    {
        public string? Address { get; set; }
        public string? Name { get; set; }
        public string? Alias { get; set; }
        public string[]? UUIDs { get; set; }
        public bool Paired { get; set; }
        public bool Connected { get; set; }
        public bool Trusted { get; set; }
        public bool Blocked { get; set; }
        public short RSSI { get; set; }
        public ObjectPath Adapter { get; set; }
    }

    [DBusInterface("org.bluez.MediaPlayer1")]
    public interface IMediaPlayer1 : IDBusObject
    {
        Task PlayAsync();
        Task PauseAsync();
        Task StopAsync();
        Task NextAsync();
        Task PreviousAsync();

        Task<T> GetAsync<T>(string prop);
        Task<MediaPlayer1Properties> GetAllAsync();
        Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
    }

    [Dictionary]
    public class MediaPlayer1Properties
    {
        public string? Name { get; set; }
        public string? Status { get; set; }
        public IDictionary<string, object>? Track { get; set; }
        public IDictionary<string, object>? Position { get; set; }
        public bool Shuffle { get; set; }
        public string? Repeat { get; set; }
    }

    // Extension methods for convenience
    public static class BlueZExtensions
    {
        public static async Task<List<(IDevice1 Device, ObjectPath Path, string Address, string Name)>> GetConnectedDevicesAsync(
            this IObjectManager objectManager)
        {
            var result = new List<(IDevice1, ObjectPath, string, string)>();
            
            try
            {
                var objects = await objectManager.GetManagedObjectsAsync();
                Console.WriteLine($"[DEBUG] GetManagedObjects returned {objects.Count} objects");
                
                foreach (var obj in objects)
                {
                    if (obj.Value.TryGetValue("org.bluez.Device1", out var deviceProps))
                    {
                        Console.WriteLine($"[DEBUG] Checking device: {obj.Key}");
                        
                        // Check if device is connected
                        bool connected = false;
                        if (deviceProps.TryGetValue("Connected", out var connectedObj))
                        {
                            connected = connectedObj is bool c && c;
                            Console.WriteLine($"[DEBUG] Device connected status: {connected}");
                        }
                        
                        if (connected)
                        {
                            var device = Connection.System.CreateProxy<IDevice1>("org.bluez", obj.Key);
                            string address = string.Empty;
                            string name = string.Empty;
                            
                            if (deviceProps.TryGetValue("Address", out var addrObj) && addrObj is string)
                                address = (string)addrObj;
                                
                            if (deviceProps.TryGetValue("Name", out var nameObj) && nameObj is string)
                                name = (string)nameObj;
                            else if (deviceProps.TryGetValue("Alias", out var aliasObj) && aliasObj is string)
                                name = (string)aliasObj;
                            
                            Console.WriteLine($"[DEBUG] Adding connected device: {name} ({address})");
                            result.Add((device, obj.Key, address, name));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to get connected devices: {ex.Message}");
                throw;
            }
            
            return result;
        }

        public static async Task<(IMediaPlayer1 Player, ObjectPath Path)?> FindMediaPlayerForDeviceAsync(
            this IObjectManager objectManager, ObjectPath devicePath)
        {
            var objects = await objectManager.GetManagedObjectsAsync();
            string devicePathStr = devicePath.ToString();
            
            foreach (var obj in objects)
            {
                if (obj.Key.ToString().StartsWith(devicePathStr) && 
                    obj.Value.ContainsKey("org.bluez.MediaPlayer1"))
                {
                    var player = Connection.System.CreateProxy<IMediaPlayer1>("org.bluez", obj.Key);
                    return (player, obj.Key);
                }
            }
            
            return null;
        }
    }
}
