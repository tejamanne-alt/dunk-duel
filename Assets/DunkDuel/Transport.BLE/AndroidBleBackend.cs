#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Text;
using DunkDuel.Net;
using UnityEngine;

namespace DunkDuel.Transport.BLE
{
    /// <summary>
    /// Bridge to com.dunkduel.ble.DunkDuelBle (Assets/Plugins/Android/DunkDuelBle.kt). One JNI call per poll
    /// returns every pending record as a single byte[]; sends are one JNI call per packet or fragment.
    /// Java byte[] is marshalled as C# sbyte[].
    /// </summary>
    internal sealed class AndroidBleBackend : IBleBackend
    {
        private const string PluginClass = "com.dunkduel.ble.DunkDuelBle";
        private AndroidJavaObject _plugin;
        private sbyte[] _input20 = new sbyte[BleProtocol.InputPacketBytes];

        public string Platform => BleProtocol.PlatformNameAndroid;
        public bool Supported => true;
        public string UnsupportedReason => null;

        public void Init(bool useL2cap)
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                _plugin = new AndroidJavaObject(PluginClass, activity);
            }
            _plugin.Call<bool>("init", useL2cap);
        }

        public bool CanHost() => _plugin != null && _plugin.Call<bool>("canHost");

        public void Host(byte[] localNameUtf8, byte[] serviceData)
        {
            string name = localNameUtf8 != null ? Encoding.UTF8.GetString(localNameUtf8) : string.Empty;
            _plugin?.Call("host", name, ToSigned(serviceData ?? Array.Empty<byte>(), 0, serviceData?.Length ?? 0));
        }

        public void StartScan() => _plugin?.Call("startScan");
        public void StopScan() => _plugin?.Call("stopScan");
        public void Join(string address) => _plugin?.Call("join", address ?? string.Empty);

        public bool Send(TransportChannel channel, byte[] data, int offset, int count)
        {
            if (_plugin == null) return false;
            sbyte[] s;
            if (count == BleProtocol.InputPacketBytes)
            {
                s = _input20; // JNI copies into a fresh Java array, so reusing the managed buffer is safe
                Buffer.BlockCopy(data, offset, s, 0, count);
            }
            else
            {
                s = ToSigned(data, offset, count);
            }
            return _plugin.Call<bool>("send", (int)channel, s);
        }

        public int Poll(ref byte[] buffer)
        {
            if (_plugin == null) return 0;
            sbyte[] batch = _plugin.Call<sbyte[]>("poll");
            if (batch == null || batch.Length == 0) return 0;
            if (buffer.Length < batch.Length) buffer = new byte[Math.Max(batch.Length, buffer.Length * 2)];
            Buffer.BlockCopy(batch, 0, buffer, 0, batch.Length);
            return batch.Length;
        }

        public void Disconnect() => _plugin?.Call("disconnect");

        public void Dispose()
        {
            if (_plugin == null) return;
            _plugin.Call("shutdown");
            _plugin.Dispose();
            _plugin = null;
        }

        private static sbyte[] ToSigned(byte[] data, int offset, int count)
        {
            var s = new sbyte[count];
            if (count > 0) Buffer.BlockCopy(data, offset, s, 0, count);
            return s;
        }
    }
}
#endif
