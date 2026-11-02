using System;
using System.Collections.Generic;
using System.Text;
using DunkDuel.Net;

namespace DunkDuel.Transport.BLE.Tests
{
    /// <summary>
    /// In-process stand-in for the native bridges: two backends wired back to back that speak the same record
    /// format. Asserts the transport never hands the radio a fragment larger than the negotiated payload.
    /// </summary>
    public sealed class LoopbackBleBackend : IBleBackend
    {
        private readonly List<byte[]> _pending = new List<byte[]>();
        private bool _hosting;
        private bool _connected;
        private byte[] _localName = Array.Empty<byte>();
        private byte[] _serviceData = Array.Empty<byte>();

        public LoopbackBleBackend Peer;
        public string Address;
        public int Payload = BleProtocol.DefaultPayload;
        public bool AllowHost = true;
        public int ControlFragmentsSent;
        public int InputPacketsSent;
        public int OversizeFragments;

        public LoopbackBleBackend(string platform, string address)
        {
            Platform = platform;
            Address = address;
        }

        public static void Pair(LoopbackBleBackend a, LoopbackBleBackend b)
        {
            a.Peer = b;
            b.Peer = a;
        }

        public string Platform { get; }
        public bool Supported => true;
        public string UnsupportedReason => null;
        public bool IsConnected => _connected;

        public void Init(bool useL2cap) { }
        public bool CanHost() => AllowHost;

        public void Host(byte[] localNameUtf8, byte[] serviceData)
        {
            _hosting = true;
            _localName = localNameUtf8 ?? Array.Empty<byte>();
            _serviceData = serviceData ?? Array.Empty<byte>();
            Push(BleRecords.State(TransportState.Hosting, TransportError.None));
        }

        public void StartScan()
        {
            Push(BleRecords.State(TransportState.Scanning, TransportError.None));
            if (Peer != null && Peer._hosting && !Peer._connected)
            {
                // iOS hosts advertise a local name, Android hosts advertise service data.
                bool ios = Peer.Platform == BleProtocol.PlatformNameIos;
                Push(BleRecords.Peer(-55, Peer.Address, ios ? Encoding.UTF8.GetString(Peer._localName) : string.Empty,
                    ios ? null : Peer._serviceData));
            }
        }

        public void StopScan() { }

        public void Join(string address)
        {
            if (Peer == null || address != Peer.Address || !Peer._hosting || Peer._connected)
            {
                Push(BleRecords.State(TransportState.Failed, TransportError.ConnectFailed));
                return;
            }
            Push(BleRecords.State(TransportState.Connecting, TransportError.None));
            _connected = true;
            Peer._connected = true;
            Push(BleRecords.Link(Payload, 30000));
            Push(BleRecords.State(TransportState.Connected, TransportError.None));
            Peer.Push(BleRecords.Link(Peer.Payload, 30000));
            Peer.Push(BleRecords.State(TransportState.Connected, TransportError.None));
        }

        public bool Send(TransportChannel channel, byte[] data, int offset, int count)
        {
            if (!_connected || Peer == null) return false;
            if (count > Payload) OversizeFragments++;
            if (channel == TransportChannel.Control) ControlFragmentsSent++;
            else InputPacketsSent++;
            Peer.Push(BleRecords.Data(channel, data, offset, count));
            return true;
        }

        public int Poll(ref byte[] buffer)
        {
            if (_pending.Count == 0) return 0;
            int total = 0;
            for (int i = 0; i < _pending.Count; i++) total += _pending[i].Length;
            if (buffer.Length < total) buffer = new byte[total];
            int p = 0;
            for (int i = 0; i < _pending.Count; i++)
            {
                Buffer.BlockCopy(_pending[i], 0, buffer, p, _pending[i].Length);
                p += _pending[i].Length;
            }
            _pending.Clear();
            return p;
        }

        public void Disconnect()
        {
            _hosting = false;
            if (!_connected) return;
            _connected = false;
            if (Peer != null && Peer._connected)
            {
                Peer._connected = false;
                Peer.Push(BleRecords.State(TransportState.Disconnected, TransportError.None));
            }
        }

        /// <summary>Simulates the radio link dropping under both phones.</summary>
        public void DropLink()
        {
            if (!_connected) return;
            _connected = false;
            Push(BleRecords.State(TransportState.Disconnected, TransportError.None));
            if (Peer != null && Peer._connected)
            {
                Peer._connected = false;
                Peer.Push(BleRecords.State(TransportState.Disconnected, TransportError.None));
            }
        }

        /// <summary>Injects a raw native record (tests for stale or malformed reports).</summary>
        public void Push(byte[] record) => _pending.Add(record);

        public void Dispose() => Disconnect();
    }
}
