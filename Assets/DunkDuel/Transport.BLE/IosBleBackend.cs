#if UNITY_IOS && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using System.Text;
using DunkDuel.Net;

namespace DunkDuel.Transport.BLE
{
    /// <summary>
    /// CoreBluetooth bridge (Assets/Plugins/iOS/DunkDuelNative/DunkDuelBLE.swift). The Swift side exports a
    /// C ABI with @_cdecl; IL2CPP links these "__Internal" symbols into UnityFramework.
    /// </summary>
    internal sealed class IosBleBackend : IBleBackend
    {
        [DllImport("__Internal")] private static extern void dd_ble_init(int useL2cap);
        [DllImport("__Internal")] private static extern int dd_ble_can_host();
        [DllImport("__Internal")] private static extern void dd_ble_host(byte[] localName, int localNameLength, byte[] serviceData, int serviceDataLength);
        [DllImport("__Internal")] private static extern void dd_ble_scan(int enable);
        [DllImport("__Internal")] private static extern void dd_ble_join(byte[] address, int addressLength);
        [DllImport("__Internal")] private static extern int dd_ble_send(int channel, byte[] data, int offset, int length);
        [DllImport("__Internal")] private static extern int dd_ble_poll([In, Out] byte[] buffer, int capacity);
        [DllImport("__Internal")] private static extern void dd_ble_disconnect();
        [DllImport("__Internal")] private static extern void dd_ble_shutdown();

        public string Platform => BleProtocol.PlatformNameIos;
        public bool Supported => true;
        public string UnsupportedReason => null;

        public void Init(bool useL2cap) => dd_ble_init(useL2cap ? 1 : 0);
        public bool CanHost() => dd_ble_can_host() != 0;

        public void Host(byte[] localNameUtf8, byte[] serviceData)
        {
            byte[] name = localNameUtf8 ?? Array.Empty<byte>();
            byte[] sd = serviceData ?? Array.Empty<byte>();
            dd_ble_host(name, name.Length, sd, sd.Length);
        }

        public void StartScan() => dd_ble_scan(1);
        public void StopScan() => dd_ble_scan(0);

        public void Join(string address)
        {
            byte[] a = Encoding.UTF8.GetBytes(address ?? string.Empty);
            dd_ble_join(a, a.Length);
        }

        public bool Send(TransportChannel channel, byte[] data, int offset, int count) =>
            dd_ble_send((int)channel, data, offset, count) != 0;

        public int Poll(ref byte[] buffer) => dd_ble_poll(buffer, buffer.Length);

        public void Disconnect() => dd_ble_disconnect();
        public void Dispose() => dd_ble_shutdown();
    }
}
#endif
